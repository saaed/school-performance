using Dapper;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// The two rules every module dataset fixture shares: WHICH campuses to seed, and the assertion
/// that makes the seeding meaningful.
///
/// ⚠️ WHY THIS IS SHARED RATHER THAN COPIED. `ModuleDatasetTests` carried its own private copy of
/// both, and a second fixture needs exactly the same two rules. A third copy is how a rule drifts -
/// and the campus rule is the one that decides whether a measurement means anything at all, so a
/// stale copy does not fail, it just quietly measures the wrong scope.
///
/// ⚠️ WHY NOT "THE FIRST N CAMPUSES". An index on `(tenantid, schoolid, campusid)` cannot be judged
/// on a scope that holds the whole table - that is the trap `V130` recorded on `student`. On
/// `ayra_perf` campus 1 holds 843,414 of 879,514 students (an artifact of an early single-campus
/// seed), so the campuses are chosen from the population that holds a FRACTION of the table, which
/// is also the population `db-report` measures (it takes the MEDIAN campus by student count).
/// </summary>
internal static class SeedCampuses
{
    public const long TenantId = 1;
    public const long SchoolId = 1;

    /// <summary>
    /// The perf database every dataset fixture seeds and measures. One copy, so a fixture cannot
    /// seed a different database from the one `db-report` reads.
    /// </summary>
    public const string ConnectionString =
        "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";

    /// <summary>Seeding is opt-in: it writes data rather than asserting application behaviour.</summary>
    public static bool DatasetEnabled =>
        Environment.GetEnvironmentVariable("SCUBE_PERF_DATASET") == "1";

    /// <summary>Re-seed a campus that already holds rows.</summary>
    public static bool Force =>
        Environment.GetEnvironmentVariable("SCUBE_PERF_FORCE") == "1";

    public static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    /// <summary>
    /// True when the operator named the campuses to seed.
    ///
    /// ⚠️ THE SCOPE-SELECTIVITY ASSERTION DOES NOT APPLY IN THIS MODE, AND THAT IS NOT A LOOPHOLE.
    /// The assertion exists because campuses are CHOSEN from the general population; an explicit
    /// list bypasses that choice by definition, so a deliberate `SCUBE_PERF_MODULE_CAMPUS_LIST=15`
    /// makes one scope own 100% of the fixture every time. Skipping it there keeps single-campus
    /// iteration usable; the assertion still runs on every default (multi-campus) seed, which is the
    /// population `db-report` actually measures.
    /// </summary>
    public static bool ExplicitCampusList =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCUBE_PERF_MODULE_CAMPUS_LIST"));

    /// <summary>
    /// The scopes a module is seeded into. `SCUBE_PERF_MODULE_CAMPUS_LIST` overrides the list
    /// explicitly (that is how a single campus is seeded on purpose); otherwise it is the campuses
    /// that hold at most 10% of the students, PLUS the campus `db-report` actually measures.
    /// </summary>
    public static async Task<List<long>> CampusesAsync(NpgsqlConnection conn, int defaultLimit = 6)
    {
        var explicitList = Environment.GetEnvironmentVariable("SCUBE_PERF_MODULE_CAMPUS_LIST");
        if (!string.IsNullOrWhiteSpace(explicitList))
            return explicitList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse).ToList();

        var campuses = (await conn.QueryAsync<long>(
            @"WITH counts AS (
                  SELECT campusid, COUNT(*) AS n FROM student
                   WHERE tenantid = @tenantId AND schoolid = @schoolId
                GROUP BY campusid
              )
              SELECT campusid FROM counts
               WHERE n::numeric / NULLIF((SELECT SUM(n) FROM counts), 0) <= 0.10
               ORDER BY campusid
               LIMIT @limit",
            new { tenantId = TenantId, schoolId = SchoolId, limit = defaultLimit }))
            .ToList();

        // ------------------------------------------------------------------
        // ⚠️ THE SCOPE THE TOOL MEASURES MUST BE IN THE LIST, OR THE SEED IS DECORATION.
        // `db-report`'s default scope is the MEDIAN campus by student count, and the <=10% rule
        // above picks campuses by ASCENDING campus id - so on `ayra_perf` it returns [4,5,6,7,8,9]
        // while the tool measures campus 15. Measured: after seeding six campuses, `rpt-leave-summary`
        // and `rpt-student-transfer` STILL reported `SKIP (scope holds 0 rows)` - the data was real
        // and in the wrong scope, which is exactly the failure this query is a copy of.
        //
        // This is the tool's OWN resolution, mirrored rather than re-derived, so the two cannot
        // disagree about which campus is "typical".
        // ------------------------------------------------------------------
        var measured = await conn.QueryFirstOrDefaultAsync<long?>(
            @"WITH counts AS (
                  SELECT tenantid, schoolid, campusid, COUNT(*) AS n
                    FROM student GROUP BY 1, 2, 3
              )
              SELECT campusid FROM counts
            ORDER BY n
              OFFSET (SELECT COUNT(*) / 2 FROM counts)
               LIMIT 1");

        if (measured is > 0 && !campuses.Contains(measured.Value))
            campuses.Add(measured.Value);

        return campuses.OrderBy(x => x).ToList();
    }

    /// <summary>
    /// The assertion every module fixture owes: no single scope owns the table, so a scope-column
    /// index can still be selective on the campus the tool measures.
    /// </summary>
    public static async Task AssertScopeIsSelectiveAsync(
        NpgsqlConnection conn, ITestOutputHelper? output, string table, string label)
    {
        if (ExplicitCampusList)
        {
            output?.WriteLine(
                $"  {label,-12} SKIP: explicit SCUBE_PERF_MODULE_CAMPUS_LIST - the campuses were " +
                "named, not chosen from the population, so one scope owns the fixture by construction");
            return;
        }

        var distribution = (await conn.QueryAsync<DistributionRow>(
            $@"WITH counts AS (
                   SELECT campusid, COUNT(*) AS n
                     FROM {table}
                    WHERE tenantid = @tenantId AND schoolid = @schoolId
                 GROUP BY 1
               )
               SELECT campusid AS CampusId,
                      n AS Rows,
                      n::numeric / NULLIF(SUM(n) OVER (), 0) AS Share
                 FROM counts
             ORDER BY n",
            new { tenantId = TenantId, schoolId = SchoolId })).ToList();

        Assert.True(distribution.Count > 0, $"{table} holds no rows, so {label} is not measurable");

        var median = distribution[distribution.Count / 2];
        output?.WriteLine(
            $"  {label,-12} median campus {median.CampusId} holds {median.Rows:N0} of the table " +
            $"= {median.Share:P2}");

        Assert.True(median.Share < 0.50m,
            $"the MEDIAN campus ({median.CampusId}) holds {median.Share:P2} of {table} - a scope-column " +
            $"index cannot be selective on it, so the {label} reading would be meaningless. " +
            "Raise SCUBE_PERF_MODULE_CAMPUSES.");
    }

    private sealed class DistributionRow
    {
        public long CampusId { get; set; }
        public long Rows { get; set; }
        public decimal Share { get; set; }
    }
}
