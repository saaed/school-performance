using Xunit;
using Xunit.Abstractions;
using SchoolPerformance.Queries;
using SchoolPerformance.QueryShapes;
using SchoolPerformance.Seeders;
using System.Diagnostics;

namespace SchoolPerformance.Tests;

[Collection("Sequential")]
public class AttendanceQueryTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;
    private readonly YourActualQueries _queries;
    private readonly StudentSeeder _studentSeeder;
    private readonly AttendanceSeeder _attendanceSeeder;
    private readonly QueryProfiler _profiler;

    private const long TenantId = 1;
    private const long SchoolId = 1;
    private const long CampusId = 1;

    public AttendanceQueryTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";
        _queries = new YourActualQueries(_connectionString);
        _studentSeeder = new StudentSeeder(_connectionString);
        _attendanceSeeder = new AttendanceSeeder(_connectionString);
        _profiler = new QueryProfiler(_connectionString);
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    // ⚠️ THE QUERY SHAPES ARE NOT DEFINED IN THIS FILE. They come from QueryCatalog, which is
    // shared with `db-report`. The attendance clause below used to be re-typed here with
    // `INNER JOIN Student` and a projected column list - which is NOT what the application
    // sends. `AttendanceRepository.GetAll` delegates to `GenericRepository.GetAllAsync`, which
    // emits `SELECT * FROM attendance WHERE TenantId=... AND CampusId=... ORDER BY ... LIMIT
    // ... OFFSET ...` with no joins, so the test's plan described a query that never runs.
    // If a shape looks wrong, fix QueryCatalog.
    private List<QuerySpec> Catalog => QueryCatalog.Build(new ScopeVars
    {
        TenantId = TenantId,
        SchoolId = SchoolId,
        CampusId = CampusId,
        ClassroomId = 1,
    });

    private QuerySpec Spec(string key) => QueryCatalog.Require(Catalog, key);

    // ============================================================
    // ATTENDANCE LIST QUERY TESTS
    // ============================================================

    [Theory]
    [InlineData(10000)]
    [InlineData(100000)]
    [InlineData(1000000)]
    [InlineData(10000000)]
    public async Task AttendanceList_ByCampus_ScalesWithVolume(int attendanceCount)
    {
        // Arrange
        _output.WriteLine($"=== Testing Attendance List with {attendanceCount:N0} records ===");
        
        // Seed students first (need at least 100 students)
        await _studentSeeder.SeedAsync(100, TenantId, SchoolId, CampusId);
        await _attendanceSeeder.SeedAsync(attendanceCount, TenantId, SchoolId, CampusId);

        var spec = Spec("attendance-list-page");

        // Act - Run YOUR actual query
        var stopwatch = Stopwatch.StartNew();
        var result = await _queries.GetAttendanceListAsync(TenantId, CampusId);
        stopwatch.Stop();

        // Profile with EXPLAIN ANALYZE - the same SQL, from the catalogue.
        // NOTE: this shape has no SchoolId parameter; the app's predicate does not use one.
        var plan = await _profiler.ProfileAsync(spec, TenantId, 0, CampusId);

        // Log results
        _output.WriteLine($"Shape: {spec.Key} ({spec.Source})");
        _output.WriteLine($"Data Volume: {attendanceCount:N0} attendance records");
        _output.WriteLine($"Query Time: {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Rows Returned: {result.Records.Count}");
        _output.WriteLine($"Total Count: {result.Count}");
        _output.WriteLine($"Plan Execution Time: {plan.ExecutionTimeMs:F2} ms");
        _output.WriteLine($"Has Sequential Scan: {plan.HasSequentialScan}");
        _output.WriteLine($"Indexes Used: {string.Join(", ", plan.IndexesUsed)}");

        // Assert - Performance threshold
        Assert.True(stopwatch.ElapsedMilliseconds < 3000,
            $"Attendance list query took {stopwatch.ElapsedMilliseconds}ms with {result.Count:N0} records (threshold: 3000ms)");
    }

    // ============================================================
    // ATTENDANCE SUMMARY QUERY TESTS (HEAVIEST)
    //
    // ⚠️ This is a DIAGNOSTIC query, not an application query - it has no repository
    // counterpart, which is why its SQL lives in YourActualQueries.AttendanceSummarySql
    // rather than in QueryCatalog. Its SQL is exposed there as a constant so this test EXPLAINs
    // the same statement it runs instead of keeping a second, subtly different copy.
    // ============================================================

    [Theory]
    [InlineData(100000, 100)]
    [InlineData(1000000, 500)]
    [InlineData(5000000, 1000)]
    [InlineData(10000000, 2000)]
    public async Task AttendanceSummary_StudentPerformance_ScalesWithVolume(
        int attendanceCount, int expectedStudents)
    {
        // Arrange
        _output.WriteLine($"=== Testing Attendance Summary with {attendanceCount:N0} records ===");
        
        // Seed students
        await _studentSeeder.SeedAsync(expectedStudents, TenantId, SchoolId, CampusId);
        await _attendanceSeeder.SeedAsync(attendanceCount, TenantId, SchoolId, CampusId);

        var fromDate = DateTime.Today.AddDays(-180);
        var toDate = DateTime.Today;

        // Act - Run YOUR actual summary query
        var stopwatch = Stopwatch.StartNew();
        var result = await _queries.GetAttendanceSummaryAsync(TenantId, CampusId, fromDate, toDate);
        stopwatch.Stop();

        // Profile with EXPLAIN ANALYZE - the SAME statement, from its one definition
        var plan = await _profiler.ProfileAsync(
            YourActualQueries.AttendanceSummarySql,
            new Dictionary<string, object>
            {
                ["@TenantId"] = TenantId,
                ["@CampusId"] = CampusId,
                ["@FromDate"] = fromDate,
                ["@ToDate"] = toDate
            });

        // Log results
        _output.WriteLine($"[DIAGNOSTIC - not an app query] {YourActualQueries.AttendanceSummarySql.Trim().Split('\n')[0].Trim()}");
        _output.WriteLine($"Data Volume: {attendanceCount:N0} attendance records, {expectedStudents:N0} students");
        _output.WriteLine($"Query Time: {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Rows Returned: {result.Count}");
        _output.WriteLine($"Plan Execution Time: {plan.ExecutionTimeMs:F2} ms");
        _output.WriteLine($"Has Sequential Scan: {plan.HasSequentialScan}");
        _output.WriteLine($"Indexes Used: {string.Join(", ", plan.IndexesUsed)}");

        // Log some sample results
        if (result.Any())
        {
            var lowAttendance = result.Where(r => r.AttendancePercentage < 75).Take(5);
            foreach (var student in lowAttendance)
            {
                _output.WriteLine($"  {student.StudentName}: {student.AttendancePercentage}% ({student.PresentDays}/{student.TotalDays})");
            }
        }

        // Assert - Performance threshold (this is the HEAVIEST query)
        Assert.True(stopwatch.ElapsedMilliseconds < 10000,
            $"Attendance summary query took {stopwatch.ElapsedMilliseconds}ms with {attendanceCount:N0} records (threshold: 10000ms)");
    }

    // ============================================================
    // QUERY PLAN ANALYSIS TESTS
    // ============================================================

    [Fact]
    public async Task AttendanceSummary_QueryPlan_Analysis()
    {
        // Arrange - Use moderate dataset for plan analysis
        await _studentSeeder.SeedAsync(500, TenantId, SchoolId, CampusId);
        await _attendanceSeeder.SeedAsync(100000, TenantId, SchoolId, CampusId);

        var fromDate = DateTime.Today.AddDays(-180);
        var toDate = DateTime.Today;

        // Act
        var plan = await _profiler.ProfileAsync(
            YourActualQueries.AttendanceSummarySql,
            new Dictionary<string, object>
            {
                ["@TenantId"] = TenantId,
                ["@CampusId"] = CampusId,
                ["@FromDate"] = fromDate,
                ["@ToDate"] = toDate
            });

        // Log the full plan
        _output.WriteLine("=== ATTENDANCE SUMMARY QUERY PLAN (diagnostic, not an app query) ===");
        _output.WriteLine(plan.RawPlan);
        _output.WriteLine($"Execution Time: {plan.ExecutionTimeMs:F2} ms");
        _output.WriteLine($"Has Sequential Scan: {plan.HasSequentialScan}");
        _output.WriteLine($"Indexes Used: {string.Join(", ", plan.IndexesUsed)}");

        // Check for performance issues
        if (plan.HasSequentialScan)
        {
            _output.WriteLine("WARNING: Query uses sequential scan - consider adding indexes:");
            _output.WriteLine("  CREATE INDEX idx_attendance_student_date ON attendance(studentid, attendancedate);");
            _output.WriteLine("  CREATE INDEX idx_attendance_status ON attendance(studentid, status, attendancedate);");
        }

        if (plan.SharedBuffersRead > plan.SharedBuffersHit)
        {
            _output.WriteLine("WARNING: More reads from disk than cache - consider increasing shared_buffers");
        }
    }

    [Fact]
    public async Task Attendance_ByDateRange_Performance()
    {
        // Arrange
        await _studentSeeder.SeedAsync(1000, TenantId, SchoolId, CampusId);
        await _attendanceSeeder.SeedAsync(500000, TenantId, SchoolId, CampusId);

        // Act - Test different date ranges
        var ranges = new[]
        {
            ("1 week", DateTime.Today.AddDays(-7), DateTime.Today),
            ("1 month", DateTime.Today.AddMonths(-1), DateTime.Today),
            ("3 months", DateTime.Today.AddMonths(-3), DateTime.Today),
            ("6 months", DateTime.Today.AddMonths(-6), DateTime.Today),
            ("1 year", DateTime.Today.AddYears(-1), DateTime.Today),
        };

        foreach (var (label, from, to) in ranges)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _queries.GetAttendanceSummaryAsync(TenantId, CampusId, from, to);
            stopwatch.Stop();

            _output.WriteLine($"{label}: {stopwatch.ElapsedMilliseconds} ms, Students: {result.Count}");

            Assert.True(stopwatch.ElapsedMilliseconds < 5000,
                $"Attendance summary for {label} took {stopwatch.ElapsedMilliseconds}ms");
        }
    }
}
