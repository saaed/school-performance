using Dapper;
using Npgsql;
using SchoolPerformance.QueryShapes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolPerformance.Queries;

public class QueryProfiler
{
    private readonly string _connectionString;

    public QueryProfiler(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Profile a shape from <see cref="QueryCatalog"/> - the SAME SQL the application sends and
    /// db-report measures.
    ///
    /// The point of this overload is that a test never has to re-type the query in order to
    /// EXPLAIN it. Overrides let a test vary a parameter (a search term, a page size) without
    /// forking the shape: <c>ProfileAsync(spec, t, s, c, new() { ["search"] = "%Ahmed%" })</c>.
    /// </summary>
    public async Task<QueryPlan> ProfileAsync(
        QuerySpec spec, long tenantId, long schoolId, long campusId,
        Dictionary<string, object>? overrides = null)
    {
        var parameters = QueryCatalog.ParametersOf(spec, tenantId, schoolId, campusId);

        if (overrides != null)
        {
            foreach (var pair in overrides)
                parameters[pair.Key] = pair.Value;
        }

        return await ProfileAsync(spec.Sql, parameters);
    }

    public async Task<QueryPlan> ProfileAsync(string query, Dictionary<string, object>? parameters = null)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var explainSql = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {query}";
        var result = await conn.QueryFirstOrDefaultAsync<string>(explainSql, parameters ?? new());

        var plan = JsonSerializer.Deserialize<ExplainResult[]>(result);
        var root = plan?.FirstOrDefault();
        var mainPlan = root?.Plan;

        return new QueryPlan
        {
            // The root's own "Execution Time" is the number to trust: a Limit/aggregate top
            // node reports its own slice in ActualTotalTime, which excludes parallel workers.
            ExecutionTimeMs = root?.ExecutionTime ?? mainPlan?.ActualTotalTime ?? 0,
            PlanningTimeMs = root?.PlanningTime ?? 0,
            RowsReturned = mainPlan?.ActualRows ?? 0,
            SharedBuffersHit = mainPlan?.SharedBuffersHit ?? 0,
            SharedBuffersRead = mainPlan?.SharedBuffersRead ?? 0,
            IndexesUsed = ExtractIndexes(mainPlan),
            HasSequentialScan = CheckSequentialScan(mainPlan),
            RawPlan = result
        };
    }

    public async Task<List<SlowQuery>> FindSlowQueriesAsync(double thresholdMs = 100)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT 
                query,
                calls,
                ROUND(mean_exec_time::numeric, 2) AS mean_ms,
                ROUND(total_exec_time::numeric, 2) AS total_ms,
                rows
            FROM pg_stat_statements
            WHERE mean_exec_time > @Threshold
            ORDER BY mean_exec_time DESC
            LIMIT 50";

        return (await conn.QueryAsync<SlowQuery>(sql, new { Threshold = thresholdMs })).ToList();
    }

    public async Task ResetStatsAsync()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT pg_stat_statements_reset()");
    }

    private List<string> ExtractIndexes(PlanNode? plan)
    {
        var indexes = new List<string>();
        if (plan == null) return indexes;

        if (!string.IsNullOrEmpty(plan.IndexName))
            indexes.Add(plan.IndexName);

        if (plan.Plans != null)
        {
            foreach (var child in plan.Plans)
                indexes.AddRange(ExtractIndexes(child));
        }

        return indexes;
    }

    private bool CheckSequentialScan(PlanNode? plan)
    {
        if (plan == null) return false;
        if (plan.NodeType?.Contains("Seq Scan") == true) return true;

        if (plan.Plans != null)
        {
            foreach (var child in plan.Plans)
            {
                if (CheckSequentialScan(child)) return true;
            }
        }

        return false;
    }
}

public class QueryPlan
{
    public double ExecutionTimeMs { get; set; }
    public double PlanningTimeMs { get; set; }
    public long RowsReturned { get; set; }
    public long SharedBuffersHit { get; set; }
    public long SharedBuffersRead { get; set; }
    public List<string> IndexesUsed { get; set; } = new();
    public bool HasSequentialScan { get; set; }
    public string RawPlan { get; set; } = string.Empty;
}

// ⚠️ THE JsonPropertyName ATTRIBUTES ARE LOAD-BEARING.
//
// `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` emits keys with SPACES in them
// ("Actual Total Time", "Shared Hit Blocks", ...). System.Text.Json matches
// property names EXACTLY, so without these attributes every one of these members
// deserializes to 0 - with NO error and NO warning. That is precisely what this
// class did before: it reported an execution time, row count and buffer count of
// zero for every query it profiled, which looks like a fast query rather than a
// broken reader. Never remove them, and never add a member here without checking
// the real key spelling against a live EXPLAIN.
public class ExplainResult
{
    [JsonPropertyName("Plan")] public PlanNode Plan { get; set; } = new();
    [JsonPropertyName("Planning Time")] public double PlanningTime { get; set; }
    [JsonPropertyName("Execution Time")] public double ExecutionTime { get; set; }
}

public class PlanNode
{
    [JsonPropertyName("Node Type")] public string NodeType { get; set; } = string.Empty;
    [JsonPropertyName("Relation Name")] public string RelationName { get; set; } = string.Empty;
    [JsonPropertyName("Alias")] public string Alias { get; set; } = string.Empty;
    [JsonPropertyName("Index Name")] public string IndexName { get; set; } = string.Empty;
    [JsonPropertyName("Actual Total Time")] public double ActualTotalTime { get; set; }
    [JsonPropertyName("Actual Rows")] public long ActualRows { get; set; }
    [JsonPropertyName("Shared Hit Blocks")] public long SharedBuffersHit { get; set; }
    [JsonPropertyName("Shared Read Blocks")] public long SharedBuffersRead { get; set; }
    [JsonPropertyName("Plans")] public List<PlanNode>? Plans { get; set; }
}

public class SlowQuery
{
    public string Query { get; set; } = string.Empty;
    public long Calls { get; set; }
    public double MeanMs { get; set; }
    public double TotalMs { get; set; }
    public long Rows { get; set; }
}
