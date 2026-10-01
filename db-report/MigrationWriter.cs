using System.Text;
using System.Text.RegularExpressions;
using SchoolPerformance.QueryShapes;

namespace SchoolPerformance.DbReport;

/// <summary>
/// One query that FAILED its budget and whose candidate index was then PROVEN to fix it.
/// This is the unit of evidence a generated migration is allowed to cite.
/// </summary>
public sealed record IndexEvidence(
    IndexCandidate Candidate,
    string Key,
    string Title,
    string Source,
    double BeforeP95,
    double AfterP95,
    double BudgetMs,
    string? HeaviestBefore,
    string? HeaviestAfter);

/// <summary>Where the numbers came from, so the generated file carries its own provenance.</summary>
public sealed record MigrationContext(string Target, string Volume, string Scope, string ScopeSize);

/// <summary>
/// Writes a MEASURED index as a migration, so nobody has to hand-copy a DDL string out of a
/// report - which is how the first one of these was produced, from a line that carried the
/// probe's throwaway name rather than a name anyone would want deployed.
///
/// ⚠️ IT ONLY EVER WRITES WHAT A PROBE PROVED. A candidate is evidence for a migration only
/// after the index was created inside a rolled-back transaction and the query measurably came
/// back inside its budget. A suggestion that did not clear the budget is reported and NOT
/// written - shipping it would be shipping a guess.
/// </summary>
public static class MigrationWriter
{
    /// <summary>The suffix every generated file carries, so machine-written ones are obvious.</summary>
    public const string FileSuffix = "__Perf_Index_Advice.sql";

    /// <summary>
    /// The `CREATE INDEX` forms this repo (and PostgreSQL) can express. Matching only the NAME
    /// is correct and is the strongest check available: index names are unique per SCHEMA, so a
    /// hit means the index is already there even if the columns changed.
    /// </summary>
    private const string CreateIndexPattern =
        @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z_][A-Za-z0-9_]*)";

    /// <summary>
    /// Find `school-db/migrations`, or null. Walking up from the binary is what lets the normal
    /// invocation - <c>dotnet run --project school-performance/db-report</c> - write to the repo
    /// without being told where the repo is.
    /// </summary>
    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Directory.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "school-db", "migrations");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>The highest existing `V<n>__` plus one - never reuse a version number.</summary>
    public static int NextVersion(string migrationsDirectory)
    {
        var highest = 0;
        foreach (var path in Directory.EnumerateFiles(migrationsDirectory, "V*.sql"))
        {
            var name = Path.GetFileName(path);
            var separator = name.IndexOf("__", StringComparison.Ordinal);
            if (name.Length == 0 || name[0] != 'V' || separator < 2) continue;

            if (int.TryParse(name.AsSpan(1, separator - 1), out var version) && version > highest)
                highest = version;
        }

        return highest + 1;
    }

    /// <summary>Every index name already declared by ANY migration on disk.</summary>
    public static HashSet<string> DeclaredIndexNames(string migrationsDirectory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(migrationsDirectory, "*.sql"))
        {
            foreach (var name in IndexNamesIn(File.ReadAllText(path)))
                names.Add(name);
        }
        return names;
    }

    public static IEnumerable<string> IndexNamesIn(string sql) =>
        Regex.Matches(sql, CreateIndexPattern, RegexOptions.IgnoreCase)
             .Select(match => match.Groups[1].Value);

    /// <summary>Render and write one migration for the proven indexes. Returns its path.</summary>
    public static string Write(
        string migrationsDirectory,
        IReadOnlyList<IndexEvidence> evidence,
        MigrationContext context)
    {
        var version = NextVersion(migrationsDirectory);
        var path = Path.Combine(migrationsDirectory, $"V{version}{FileSuffix}");

        // ⚠️ LF, written explicitly. The repo's migrations are LF (despite `core.autocrlf=true`),
        // and AppendLine/WriteAllLines would quietly emit CRLF on Windows - a whole-file diff on
        // every migration that follows.
        File.WriteAllText(path, Render(version, evidence, context), new UTF8Encoding(false));
        return path;
    }

    public static string Render(int version, IReadOnlyList<IndexEvidence> evidence, MigrationContext context)
    {
        // One index, however many queries asked for it: a grid's page and its count read the
        // same table the same way, and they must produce one statement, not two.
        var groups = evidence
            .GroupBy(item => item.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Candidate = group.First().Candidate, Items = group.ToList() })
            .OrderBy(group => group.Candidate.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');

        Line("-- =============================================================================");
        Line($"-- V{version}{FileSuffix}");
        Line("--");
        Line("-- GENERATED by school-performance/db-report. The numbers below are MEASUREMENTS,");
        Line("-- not prose - re-measure rather than editing them, and re-generate with:");
        Line("--");
        Line("--     dotnet run --project school-performance/db-report -- --advise --write-migrations");
        Line("--");
        Line($"-- target     : {context.Target}");
        Line($"-- volume     : {context.Volume}");
        Line($"-- scope      : {context.Scope}");
        Line($"-- scope size : {context.ScopeSize}");
        Line($"-- generated  : {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        Line("--");

        foreach (var group in groups)
        {
            Line("-- WHY");
            foreach (var item in group.Items)
            {
                var speedup = item.AfterP95 > 0 ? item.BeforeP95 / item.AfterP95 : 0;
                Line($"--   {item.Key} - {item.Title}");
                Line($"--     p95 {item.BeforeP95:F0} ms -> {item.AfterP95:F1} ms  ({speedup:F1}x, budget {item.BudgetMs:F0} ms)");
                if (item.HeaviestBefore != null || item.HeaviestAfter != null)
                    Line($"--     heaviest node: {item.HeaviestBefore} -> {item.HeaviestAfter}");
                Line($"--     source    : {item.Source}");
            }
            Line($"--   index     : {group.Candidate.Name} on {group.Candidate.Table} ({group.Candidate.Columns})");
            Line($"--   rationale : {group.Candidate.Rationale}");
            Line("--");
        }

        Line("-- ⚠️ LOCKING. A plain `CREATE INDEX` takes a SHARE lock and blocks writes to the table for");
        Line("-- the whole build. Fine on a small or quiet table. On a large, write-hot production table");
        Line("-- use `CREATE INDEX CONCURRENTLY` instead - and then it must NOT run inside a transaction,");
        Line("-- which is why this file deliberately has no BEGIN/COMMIT to remove.");
        Line("--");
        Line("-- SAFE TO RE-RUN: IF NOT EXISTS, and no data is touched.");
        Line("-- =============================================================================");
        Line("");
        Line("SET search_path = public;");
        Line("");

        foreach (var group in groups)
        {
            Line($"-- from {string.Join(", ", group.Items.Select(item => item.Key))}");
            Line(IndexAdvice.CreateDdl(group.Candidate) + ";");
            Line("");
        }

        return sb.ToString();
    }
}
