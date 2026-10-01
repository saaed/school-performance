namespace SchoolPerformance.QueryShapes;

/// <summary>
/// An index that a measured query asked for. <see cref="Name"/> is the name it would be
/// DEPLOYED under; the probe that proves it uses a different, disposable name (see
/// <see cref="IndexAdvice.ProbeName"/>) so a leaked probe can never be mistaken for a
/// real index.
/// </summary>
/// <param name="Table">The table, unqualified.</param>
/// <param name="Name">The deployed index name, exactly as a migration must create it.</param>
/// <param name="Columns">The column list, exactly as measured - including any DESC.</param>
/// <param name="Rationale">Why it should help, so the suggestion can be argued with.</param>
public sealed record IndexCandidate(string Table, string Name, string Columns, string Rationale);

/// <summary>
/// Turns "this query wants an index" into DDL, and decides which of a set of candidates is
/// worth shipping.
///
/// ⚠️ WHY THIS IS SHARED CODE AND NOT PART OF THE REPORT. Two things here are decisions
/// rather than formatting, and both of them can ship a wrong migration silently:
///   * which name a probe uses versus which name a migration uses, and
///   * which candidates collapse into one another (<see cref="Reduce"/>).
/// So they live next to the shapes they describe (`QueryCatalog`) and are unit-tested,
/// instead of being a string built inline at the point of printing.
/// </summary>
public static class IndexAdvice
{
    /// <summary>
    /// ⚠️ A PROBE MUST NOT USE THE DEPLOYED NAME. The probe really does create the index -
    /// inside a transaction that is then rolled back - so if one ever leaked (a killed
    /// session, a connection dropped mid-probe) it would otherwise be indistinguishable from
    /// the real thing, and the next migration's `IF NOT EXISTS` would silently no-op against
    /// a build nobody can account for. The prefix makes a leak obvious and safe to drop.
    /// </summary>
    public const string ProbePrefix = "zz_perf_probe_";

    /// <summary>PostgreSQL truncates identifiers at 63 bytes; so do we, deliberately.</summary>
    public const int MaxIdentifierLength = 63;

    /// <summary>A disposable name for the index the probe creates and rolls back.</summary>
    public static string ProbeName(IndexCandidate candidate)
    {
        var name = ProbePrefix + Identifier(candidate.Table) + "_" + Identifier(candidate.Columns);
        return name.Length <= MaxIdentifierLength ? name : name[..MaxIdentifierLength];
    }

    /// <summary>The statement the probe runs inside its transaction. Never persisted.</summary>
    public static string ProbeDdl(IndexCandidate candidate) =>
        $"CREATE INDEX {ProbeName(candidate)} ON {candidate.Table} USING btree ({candidate.Columns})";

    /// <summary>The statement a migration carries - guarded, and scoped to `public`.</summary>
    public static string CreateDdl(IndexCandidate candidate) =>
        $"CREATE INDEX IF NOT EXISTS {candidate.Name} ON public.{candidate.Table} USING btree ({candidate.Columns})";

    /// <summary>
    /// Collapses a raw candidate list into the smallest set of indexes that covers every
    /// query, so one run cannot propose two indexes where one would do.
    ///
    ///   1. the same index proposed twice collapses to one - two queries read the same table
    ///      the same way all the time (a grid's page and its count), and they must produce one
    ///      index, not two;
    ///   2. an index that is a LEADING PREFIX of another ON THE SAME TABLE is DROPPED.
    ///      PostgreSQL serves `(a, b)` from an index on `(a, b, c)`, so shipping both would be
    ///      pure write-amplification. This is the rule that keeps the dashboard's
    ///      `(tenantid, schoolid, campusid)` from becoming a second file once the grid's
    ///      `(tenantid, schoolid, campusid, isactive)` is proven.
    ///
    /// ⚠️ The prefix test compares the column TOKENS including any `DESC`, so
    /// `(a, b DESC)` is NOT treated as covering `(a, b)` - the planner cannot use one for the
    /// other, and merging them would silently change a query's plan.
    /// </summary>
    public static List<IndexCandidate> Reduce(IEnumerable<IndexCandidate> candidates)
    {
        var deduped = new List<IndexCandidate>();
        foreach (var candidate in candidates)
        {
            var duplicate = deduped.Any(kept =>
                Same(kept.Table, candidate.Table) &&
                (Same(kept.Name, candidate.Name) ||
                 ColumnTokens(kept.Columns).SequenceEqual(ColumnTokens(candidate.Columns))));

            if (!duplicate) deduped.Add(candidate);
        }

        var kept = deduped
            .Where((candidate, index) => !deduped
                .Where((_, other) => other != index)
                .Any(other => Same(other.Table, candidate.Table) &&
                              IsLeadingPrefixOf(candidate.Columns, other.Columns)))
            .ToList();

        return kept
            .OrderBy(candidate => candidate.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLeadingPrefixOf(string shorter, string longer)
    {
        var first = ColumnTokens(shorter);
        var second = ColumnTokens(longer);
        return first.Length > 0 && first.Length < second.Length && first.SequenceEqual(second.Take(first.Length));
    }

    private static string[] ColumnTokens(string columns) =>
        columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(token => token.ToLowerInvariant())
               .ToArray();

    /// <summary>Reduce arbitrary text to a legal, lower-case identifier fragment.</summary>
    private static string Identifier(string text)
    {
        var mapped = text.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();

        return string.Join("_", new string(mapped).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
