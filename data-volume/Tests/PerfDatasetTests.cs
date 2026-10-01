using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Builds the MULTI-CAMPUS shape that <c>db-report</c> needs in order to give index
/// advice that means anything.
///
/// ⚠️ WHY THIS IS A TEST AND NOT JUST PART OF THE SUITE. It is expensive (it creates
/// campuses, classrooms, students and attendance) and it exists to PREPARE a
/// measurement, not to assert application behaviour. So it is:
///   * OPT-IN   - skipped unless SCUBE_PERF_DATASET=1, so a routine run never
///                silently seeds hundreds of thousands of rows (which is exactly
///                how one campus reached 842k students in the first place);
///   * IDEMPOTENT - a campus that already holds students is left alone unless
///                SCUBE_PERF_FORCE=1, so re-running does not double the volume.
///
/// Run it, then run `db-report`:
///
///   SCUBE_PERF_DATASET=1 dotnet test SchoolDataVolume.csproj \
///       --filter "FullyQualifiedName~PerfDataset" -v:n
///   dotnet run --project school-performance/db-report -- --advise
///
/// Size it with SCUBE_PERF_CAMPUSES (default 20) and
/// SCUBE_PERF_STUDENTS_PER_CAMPUS (default 2000).
/// </summary>
[Collection("Sequential")]
public class PerfDatasetTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;

    private const long TenantId = 1;
    private const long SchoolId = 1;

    public PerfDatasetTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";
    }

    public void Dispose()
    {
        // Nothing to clean up: this fixture is the point of the database.
    }

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SCUBE_PERF_DATASET") == "1";

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    [Fact]
    public async Task PerfDataset_spreads_the_data_across_many_campuses()
    {
        if (!Enabled)
        {
            _output.WriteLine(
                "SKIPPED: set SCUBE_PERF_DATASET=1 to build the multi-campus perf dataset. " +
                "It is opt-in because it seeds data rather than asserting it.");
            return;
        }

        var options = new PerfDatasetOptions
        {
            TenantId = TenantId,
            SchoolId = SchoolId,
            CampusCount = EnvInt("SCUBE_PERF_CAMPUSES", 20),
            StudentsPerCampus = EnvInt("SCUBE_PERF_STUDENTS_PER_CAMPUS", 2000),
            Force = Environment.GetEnvironmentVariable("SCUBE_PERF_FORCE") == "1",
        };

        _output.WriteLine($"Building a {options.CampusCount}-campus dataset, " +
                          $"{options.StudentsPerCampus:N0} students and " +
                          $"{options.EffectiveAttendancePerCampus:N0} attendance rows per campus...");

        var seeder = new PerfDatasetSeeder(_connectionString);
        var report = await seeder.SeedAsync(options);

        _output.WriteLine(report.Describe());
        foreach (var campus in report.Campuses)
        {
            _output.WriteLine(
                $"  campus {campus.CampusId,-5} {campus.Students,9:N0} students {campus.Attendance,10:N0} attendance" +
                $"{(campus.CreatedNow ? "  [created]" : "")}" +
                $"{(campus.SkippedBecauseItAlreadyHadData ? "  [already had data - skipped]" : "")}");
        }

        // ---- the shape is what makes the measurement meaningful -------------
        var distribution = await ReadDistributionAsync();
        _output.WriteLine("");
        _output.WriteLine("student row counts per campus (largest first):");
        foreach (var row in distribution.Take(8))
            _output.WriteLine($"  {row.Scope,-14} {row.Students,10:N0}   {row.ShareOfStudents:P2} of all students");

        // 1. There really are many campuses holding students now.
        Assert.True(distribution.Count >= options.CampusCount,
            $"expected at least {options.CampusCount} campuses holding students, found {distribution.Count}");

        // 2. Every campus we touched got BOTH students and attendance - a campus with
        //    students but no attendance would make the attendance grid unmeasurable on it.
        foreach (var campus in report.Campuses.Where(c => c.Students > 0))
        {
            Assert.True(campus.Attendance > 0,
                $"campus {campus.CampusId} has {campus.Students:N0} students but no attendance, " +
                "so the attendance queries cannot be measured against it");
        }

        // 3. THE ASSERTION THAT MATTERS. A scope-column index can only be judged on a campus
        //    that holds a FRACTION of the table, and the campus db-report measures BY DEFAULT
        //    is the MEDIAN one (the same OFFSET selection Program.cs makes). If a single campus
        //    holds everything, the median IS that campus and the advice regresses to the
        //    useless ~1.0x this fixture exists to prevent.
        //
        //    ⚠️ Asserting on the SMALLEST campus cannot catch that: the smallest is tiny in
        //    both the good and the bad shape (in the bad one it is 0%), so the check would
        //    pass while every reading it guards was meaningless.
        var ascending = distribution.OrderBy(d => d.Students).ToList();
        var median = ascending[ascending.Count / 2];
        _output.WriteLine("");
        _output.WriteLine($"median campus (the one db-report measures by default): " +
                          $"{median.Scope} holding {median.Students:N0} students " +
                          $"= {median.ShareOfStudents:P2} of the table");

        Assert.True(median.ShareOfStudents < 0.20m,
            $"the MEDIAN campus ({median.Scope}) holds {median.ShareOfStudents:P2} of all students - " +
            "a scope-column index cannot be selective on it, so db-report's advice would be meaningless again");
    }

    /// <summary>The busiest campuses, with each one's share of the whole student table.</summary>
    private async Task<List<DistributionRow>> ReadDistributionAsync()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<DistributionRow>(
            @"WITH counts AS (
                  SELECT tenantid, schoolid, campusid, COUNT(*) AS students
                    FROM student
                GROUP BY 1, 2, 3
              )
              SELECT tenantid || '/' || schoolid || '/' || campusid AS scope,
                     students,
                     students::numeric / NULLIF(SUM(students) OVER (), 0) AS ShareOfStudents
                FROM counts
            ORDER BY students DESC");

        return rows.ToList();
    }

    private sealed class DistributionRow
    {
        public string Scope { get; set; } = string.Empty;
        public long Students { get; set; }
        public decimal ShareOfStudents { get; set; }
    }
}
