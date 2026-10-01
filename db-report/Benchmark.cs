using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using SchoolPerformance.QueryShapes;

namespace SchoolPerformance.DbReport;

/// <summary>
/// One open connection plus an optional transaction.
///
/// A measurement loop MUST reuse a single session: opening a connection per iteration
/// measures the handshake, not the query. And an index probe needs a transaction it can
/// roll back, so nothing is ever left behind in the database being measured.
/// </summary>
public sealed class DbSession : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction? Transaction { get; private set; }

    private DbSession(NpgsqlConnection connection) => Connection = connection;

    public static async Task<DbSession> OpenAsync(string connectionString, int statementTimeoutMs)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // A runaway query must fail loudly instead of hanging the report forever.
        await connection.ExecuteAsync($"SET statement_timeout = {statementTimeoutMs};");
        return new DbSession(connection);
    }

    public async Task BeginAsync()
        => Transaction = (NpgsqlTransaction)await Connection.BeginTransactionAsync();

    public async Task RollbackAsync()
    {
        if (Transaction == null) return;
        var tx = Transaction;
        Transaction = null;
        await tx.RollbackAsync();
        await tx.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await RollbackAsync();
        await Connection.DisposeAsync();
    }
}

/// <summary>What EXPLAIN told us about a query.</summary>
public sealed class PlanInfo
{
    /// <summary>Below this many rows a sequential scan is noise, not a finding.</summary>
    public const long SeqScanNoiseRows = 1000;

    public double PlanningMs { get; set; }
    public double ExecutionMs { get; set; }

    /// <summary>Rows the TOP node produced (the rows the caller would actually receive).</summary>
    public long TopRows { get; set; }

    public bool HasSeqScan { get; set; }
    public string? WorstSeqScanRelation { get; set; }
    public long WorstSeqScanRows { get; set; }

    /// <summary>
    /// The single node that consumed the most time. This - not "is there a seq scan" - is
    /// where the time actually went: a count over 841k rows can be slow with NO sequential
    /// scan at all, purely from index-scan volume.
    /// </summary>
    public string? HeaviestNodeType { get; set; }
    public string? HeaviestNodeRelation { get; set; }
    public long HeaviestNodeRows { get; set; }
    public double HeaviestNodeMs { get; set; }

    public List<string> Indexes { get; } = new();
    public long SharedHitBlocks { get; set; }
    public long SharedReadBlocks { get; set; }

    public string HeaviestNodeDescription =>
        HeaviestNodeType == null
            ? "(no plan)"
            : $"{HeaviestNodeType} on {HeaviestNodeRelation ?? "?"} (~{HeaviestNodeRows:N0} rows, {HeaviestNodeMs:F0} ms)";
}

/// <summary>One query's measured cost.</summary>
public sealed class Measurement
{
    public required string Key { get; init; }
    public required double BudgetMs { get; init; }

    public double[] SamplesMs { get; set; } = Array.Empty<double>();
    public double Min { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double Max { get; set; }

    /// <summary>
    /// A query whose cost swings by 3x or more between identical executions is not "fast"
    /// or "slow" - it is UNDECIDED, usually because the planner picked a different plan
    /// (parallel hash join vs serial nested loop). That is a latent incident, so it is
    /// called out rather than averaged away.
    ///
    /// ⚠️ THE FLOOR BELONGS ON THE SLOW END, NOT THE FAST ONE. It used to read
    /// `Min >= UnstableFloorMs`, which excluded exactly the shape this detector exists for:
    /// a query that runs at 45 ms five times and 160 ms five times was reported as a plain
    /// FAIL, because 45 ms is just under the floor. The floor's stated purpose is to keep
    /// "1 ms vs 4.5 ms" out of the report - and that is a statement about the SLOW end.
    /// Gating on the MIN also makes the flag depend on how lucky the fast branch was, so the
    /// same bimodal distribution flips in and out of detection as the machine warms.
    /// </summary>
    public const double UnstableFloorMs = 50;

    public bool IsUnstable => SamplesMs.Length >= 3 && Min > 0 && Max >= UnstableFloorMs && Max >= Min * 3;

    public int RowsReturned { get; set; }
    public long? ScalarValue { get; set; }
    public PlanInfo? Plan { get; set; }

    public string? Error { get; set; }

    /// <summary>Rows the spec would scan; when tiny, the timing is not worth judging.</summary>
    public long? Volume { get; set; }
    public long MinVolume { get; init; } = 1000;

    public bool IsSkipped => Volume.HasValue && Volume.Value < MinVolume;

    public bool Passed => Error == null && !IsSkipped && P95 <= BudgetMs;

    /// <summary>
    /// The samples disagree with each other AND the slow end misses the budget, so the tool
    /// cannot say whether this query is within budget - it is not a FAIL.
    ///
    /// ⚠️ WHY THIS IS ITS OWN VERDICT rather than a FAIL. `Stats` computes p95 as
    /// `sorted[ceil(0.95 * (L-1))]`, which for any run count below 21 is the MAX - so one
    /// outlier IS the verdict. Only 21+ samples make p95 a percentile at all, and the
    /// documented headline command uses `--runs 3`. Collapsing a bimodal sample set to a
    /// single FAIL therefore reports a machine-level hiccup as a query defect, while
    /// collapsing it to OK would hide a genuine latent incident. Neither is true, so the
    /// honest answer is the third one.
    /// </summary>
    public bool IsUndecided => IsUnstable && Error == null && !IsSkipped && P95 > BudgetMs;

    public string Verdict
    {
        get
        {
            if (Error != null) return "ERROR";
            if (IsSkipped) return "SKIP";
            if (Passed) return "OK";
            return IsUndecided ? "UNSTABLE" : "FAIL";
        }
    }
}

public sealed class BenchmarkRunner
{
    private readonly string _connectionString;
    private readonly int _statementTimeoutMs;

    public BenchmarkRunner(string connectionString, int statementTimeoutMs)
    {
        _connectionString = connectionString;
        _statementTimeoutMs = statementTimeoutMs;
    }

    public async Task<DbSession> OpenAsync()
        => await DbSession.OpenAsync(_connectionString, _statementTimeoutMs);

    public async Task<long> ScalarAsync(DbSession session, string sql, object? parameters = null)
        => await session.Connection.ExecuteScalarAsync<long>(sql, parameters, session.Transaction);

    /// <summary>
    /// Times a spec <paramref name="runs"/> times on one warm session, then explains it.
    /// The first execution is a WARM-UP and is discarded: it pays for plan cache and
    /// shared-buffer population, and reporting it would overstate a query that is fine.
    /// </summary>
    public async Task<Measurement> MeasureAsync(QuerySpec spec, int runs)
    {
        var m = new Measurement { Key = spec.Key, BudgetMs = spec.P95BudgetMs, MinVolume = spec.MinVolume };
        try
        {
            await using var session = await OpenAsync();
            await SetReportScopeAsync(session, spec);

            if (spec.VolumeSql != null)
                m.Volume = await ScalarAsync(session, spec.VolumeSql, BindableFor(spec.VolumeSql, spec.Params));

            await ExecuteAsync(session, spec); // warm-up, discarded

            var samples = new List<double>(runs);
            for (var i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                var outcome = await ExecuteAsync(session, spec);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                m.RowsReturned = outcome.Rows;
                m.ScalarValue = outcome.Scalar;
            }

            m.SamplesMs = samples.ToArray();
            (m.Min, m.P50, m.P95, m.Max) = Stats(m.SamplesMs);
            m.Plan = await ExplainAsync(session, spec);
        }
        catch (Exception ex)
        {
            m.Error = Flatten(ex);
        }
        return m;
    }

    public async Task<PlanInfo> ExplainAsync(DbSession session, QuerySpec spec)
    {
        var json = await session.Connection.QueryFirstOrDefaultAsync<string>(
            $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {spec.Sql}", BindableFor(spec.Sql, spec.Params), session.Transaction);

        var info = new PlanInfo();
        if (string.IsNullOrWhiteSpace(json)) return info;

        var roots = JsonSerializer.Deserialize<List<ExplainRoot>>(json);
        var root = roots?.FirstOrDefault();
        if (root == null) return info;

        info.PlanningMs = root.PlanningTime;
        info.ExecutionMs = root.ExecutionTime;
        Walk(root.Plan, info, 0);
        return info;
    }

    /// <summary>
    /// Creates the candidate index INSIDE a transaction, re-measures, then ROLLS BACK.
    ///
    /// This is the only honest way to answer "would this index help?" without changing the
    /// database. PostgreSQL DDL is transactional, so the index exists for the planner during
    /// the probe and is gone afterwards - even if this process is killed.
    ///
    /// ⚠️ The probe creates it under a DISPOSABLE name (<see cref="IndexAdvice.ProbeName"/>),
    /// never the name a migration would use. If a probe ever leaked - a killed session, a
    /// connection dropped mid-transaction - the leftover index is then greppable and safe to
    /// drop, instead of silently pre-creating the real index in a state nobody can account for.
    /// </summary>
    public async Task<Measurement?> ProbeIndexAsync(QuerySpec spec, int runs)
    {
        var candidate = QueryCatalog.IndexFor(spec);
        if (candidate == null) return null;

        var m = new Measurement { Key = spec.Key, BudgetMs = spec.P95BudgetMs };
        try
        {
            await using var session = await OpenAsync();
            await session.BeginAsync();
            await SetReportScopeAsync(session, spec);

            await session.Connection.ExecuteAsync(IndexAdvice.ProbeDdl(candidate), transaction: session.Transaction);

            // Fresh statistics so the planner can actually choose the new index.
            if (spec.IndexTable != null)
                await session.Connection.ExecuteAsync($"ANALYZE {spec.IndexTable};", transaction: session.Transaction);

            await ExecuteAsync(session, spec); // warm-up

            var samples = new List<double>(runs);
            for (var i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                await ExecuteAsync(session, spec);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            m.SamplesMs = samples.ToArray();
            (m.Min, m.P50, m.P95, m.Max) = Stats(m.SamplesMs);
            m.Plan = await ExplainAsync(session, spec);

            await session.RollbackAsync(); // <- nothing is kept
        }
        catch (Exception ex)
        {
            m.Error = Flatten(ex);
        }
        return m;
    }

    /// <summary>
    /// Set the session-scope variables a REPORTING VIEW reads, for a spec that declares
    /// <see cref="QuerySpec.ReportScope"/>.
    ///
    /// ⚠️ This mirrors `ReportQueryExecutor.SetScopeAsync` and must stay in step with it: the views
    /// read `app.tenant_id` / `app.school_id` / `app.campus_id` with `current_setting(name, true)`,
    /// which FAILS CLOSED - outside the engine they return zero rows rather than raising. Measuring a
    /// view without setting the scope would therefore report a slow report as a fast one, which is
    /// the exact class of false green this tool exists to prevent.
    ///
    /// `is_local => false` (session-level) rather than the engine's `true` (transaction-level): the
    /// measurement session runs outside a transaction, and every session here is short-lived and
    /// disposed at the end of the spec, so there is nothing for a setting to leak into.
    ///
    /// ⚠️ IT ALSO SETS `enable_mergejoin = off`, because the engine does - see the long note in
    /// `ReportQueryExecutor.SetScopeAsync` for the measurement behind it. This is not cosmetic: on
    /// `ayra_perf` the same COUNT(*) over `vw_student_attendance` reads **85.5 s** with merge joins
    /// allowed and **0.44 s** without, so a harness that measured the default planner strategy would
    /// be measuring a query the application never issues. A view spec whose number looks
    /// impossibly good or bad should be checked for this line first.
    /// </summary>
    private static async Task SetReportScopeAsync(DbSession session, QuerySpec spec)
    {
        var scope = spec.ReportScope;
        if (scope == null) return;

        foreach (var (name, value) in new[]
                 {
                     ("app.tenant_id", scope.TenantId),
                     ("app.school_id", scope.SchoolId),
                     ("app.campus_id", scope.CampusId),
                 })
        {
            await session.Connection.ExecuteAsync(
                "SELECT set_config(@name, @value, false)",
                new { name, value = value.ToString() },
                session.Transaction);
        }

        // Mirrors the engine's `SET LOCAL enable_mergejoin = off`.
        await session.Connection.ExecuteAsync(
            "SET enable_mergejoin = off",
            null,
            session.Transaction);
    }

    private readonly record struct ExecOutcome(int Rows, long? Scalar);

    /// <summary>
    /// Bind only the parameters the STATEMENT actually references.
    ///
    /// ⚠️ The catalogue legitimately holds two kinds of spec at once. Most are parameterized
    /// (`@tenantId`), because that is what the repositories normally send - and it matters, since
    /// PostgreSQL may choose a generic plan for parameters and a better custom plan for literals.
    /// But a few repositories build their SQL by string interpolation, so the scope values are
    /// LITERALS in the statement and there is nothing to bind.
    ///
    /// Those specs still need a parameterized `VolumeSql` (to decide whether the scope has enough
    /// rows to be worth judging), so the parameter bag is not empty while the statement under test
    /// names no parameters. Filtering per statement is what lets both shapes live in one catalogue
    /// without the literal ones having to fake a parameter they do not have.
    /// </summary>
    private static Dictionary<string, object>? BindableFor(string sql, Dictionary<string, object>? parameters)
    {
        if (parameters == null || parameters.Count == 0) return parameters;

        var used = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            // Register under the SPELLING THE STATEMENT USES, not the one the bag was written with.
            //
            // A specification holds TWO statements - the `Sql` transcribed from the repository (whose
            // placeholders are lower-case, `@tenantid`) and its own `VolumeSql` (camelCase, `@tenantId`)
            // - and one parameter bag serves both. Matching by `Contains("@name")` exactly is not good
            // enough in either direction: a statement whose placeholder is spelled differently silently
            // loses the binding, and PostgreSQL then reads `@tenantId` as the `@` (absolute value)
            // PREFIX OPERATOR applied to a COLUMN. That parses whenever the query's row sources happen to
            // expose a column of that name - `t.tenantid`, `t.schoolid`, `t.campusid` all exist on
            // `Timetable`, so the scope parameters looked bound while the statement was quietly filtering
            // on `abs(tenantid) = abs(tenantid)`-style expressions - and it fails with
            // `42703 column "x" does not exist` only for the parameter that has no matching column. The
            // tell is an error naming a parameter (`column "timetableteacherid" does not exist`) rather
            // than a relation.
            //
            // Npgsql parameter-name matching is not guaranteed case-insensitive across versions, so the
            // safe form is to hand it the placeholder's own spelling.
            var actual = PlaceholderIn(sql, pair.Key);
            if (actual != null) used[actual] = pair.Value;
        }

        return used;
    }

    /// <summary>
    /// Returns the spelling <paramref name="name"/> carries in <paramref name="sql"/> (without the
    /// `@`), matched case-insensitively but only on a whole-placeholder boundary, or null when the
    /// statement does not name it at all.
    /// </summary>
    private static string? PlaceholderIn(string sql, string name)
    {
        var index = sql.IndexOf('@');
        while (index >= 0)
        {
            var start = index + 1;
            var end = start + name.Length;

            if (end <= sql.Length
                && string.Compare(sql, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == sql.Length || (!char.IsLetterOrDigit(sql[end]) && sql[end] != '_')))
            {
                return sql.Substring(start, name.Length);
            }

            index = sql.IndexOf('@', index + 1);
        }

        return null;
    }

    private static async Task<ExecOutcome> ExecuteAsync(DbSession session, QuerySpec spec)
    {
        var parameters = BindableFor(spec.Sql, spec.Params);

        if (spec.IsScalar)
        {
            var value = await session.Connection.ExecuteScalarAsync<long?>(spec.Sql, parameters, session.Transaction);
            return new ExecOutcome(1, value);
        }

        var rows = 0;
        using var reader = await session.Connection.ExecuteReaderAsync(spec.Sql, parameters, session.Transaction);
        while (await reader.ReadAsync()) rows++;
        return new ExecOutcome(rows, null);
    }

    private static void Walk(PlanNode? node, PlanInfo info, int depth)
    {
        if (node == null) return;

        info.SharedHitBlocks += node.SharedHitBlocks;
        info.SharedReadBlocks += node.SharedReadBlocks;

        var type = node.NodeType ?? string.Empty;

        // A scan of a 20-row lookup table is not a performance problem. Only report a
        // sequential scan once it is big enough to be worth acting on.
        if (type.Contains("Seq Scan", StringComparison.OrdinalIgnoreCase) && node.ActualRows >= PlanInfo.SeqScanNoiseRows)
        {
            info.HasSeqScan = true;
            if (node.ActualRows > info.WorstSeqScanRows)
            {
                info.WorstSeqScanRows = node.ActualRows;
                info.WorstSeqScanRelation = node.RelationName;
            }
        }

        if (node.ActualTotalTime > info.HeaviestNodeMs)
        {
            info.HeaviestNodeMs = node.ActualTotalTime;
            info.HeaviestNodeType = type;
            info.HeaviestNodeRelation = node.RelationName;
            info.HeaviestNodeRows = node.ActualRows;
        }

        if (!string.IsNullOrEmpty(node.IndexName))
            info.Indexes.Add(node.IndexName!);

        if (depth == 0) info.TopRows = node.ActualRows;

        foreach (var child in node.Plans ?? new List<PlanNode>())
            Walk(child, info, depth + 1);
    }

    private static (double min, double p50, double p95, double max) Stats(double[] samples)
    {
        if (samples.Length == 0) return (0, 0, 0, 0);
        var sorted = samples.OrderBy(x => x).ToArray();
        var p50 = sorted[(int)Math.Floor(0.50 * (sorted.Length - 1))];
        var p95 = sorted[(int)Math.Ceiling(0.95 * (sorted.Length - 1))];
        return (sorted[0], p50, p95, sorted[^1]);
    }

    private static string Flatten(Exception ex)
    {
        var message = ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return message.Length > 200 ? message[..200] + "..." : message;
    }
}

// ---------------------------------------------------------------------------
// EXPLAIN (FORMAT JSON) shapes.
//
// ⚠️ These JsonPropertyName attributes are NOT decoration. The JSON keys contain
// SPACES ("Actual Total Time", "Planning Time", "Shared Hit Blocks"), so plain
// property names deserialize to 0 with no error at all - which is exactly the bug
// in ../data-volume/Queries/QueryProfiler.cs, where every number it reports is
// silently zero. Keep them in sync with `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`.
// ---------------------------------------------------------------------------

public sealed class ExplainRoot
{
    [JsonPropertyName("Plan")] public PlanNode? Plan { get; set; }
    [JsonPropertyName("Planning Time")] public double PlanningTime { get; set; }
    [JsonPropertyName("Execution Time")] public double ExecutionTime { get; set; }
}

public sealed class PlanNode
{
    [JsonPropertyName("Node Type")] public string? NodeType { get; set; }
    [JsonPropertyName("Relation Name")] public string? RelationName { get; set; }
    [JsonPropertyName("Index Name")] public string? IndexName { get; set; }
    [JsonPropertyName("Actual Rows")] public long ActualRows { get; set; }
    [JsonPropertyName("Actual Total Time")] public double ActualTotalTime { get; set; }
    [JsonPropertyName("Shared Hit Blocks")] public long SharedHitBlocks { get; set; }
    [JsonPropertyName("Shared Read Blocks")] public long SharedReadBlocks { get; set; }
    [JsonPropertyName("Plans")] public List<PlanNode>? Plans { get; set; }
}
