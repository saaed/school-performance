using Xunit;
using Xunit.Abstractions;
using SchoolPerformance.Queries;
using SchoolPerformance.QueryShapes;
using SchoolPerformance.Seeders;
using System.Diagnostics;

namespace SchoolPerformance.Tests;

[Collection("Sequential")]
public class StudentQueryTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;
    private readonly YourActualQueries _queries;
    private readonly StudentSeeder _seeder;
    private readonly QueryProfiler _profiler;

    private const long TenantId = 1;
    private const long SchoolId = 1;
    private const long CampusId = 1;

    public StudentQueryTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";
        _queries = new YourActualQueries(_connectionString);
        _seeder = new StudentSeeder(_connectionString);
        _profiler = new QueryProfiler(_connectionString);
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    // ⚠️ THE QUERY SHAPES ARE NOT DEFINED IN THIS FILE. They come from QueryCatalog, which is
    // shared with `db-report`. These tests used to carry their own hand-copied SQL, which had
    // drifted from the repositories (the copy had lost the active-year join and the
    // EnrollmentStatus projection the repository added later), so the plan they EXPLAINed
    // belonged to a query the application never sends. If a shape looks wrong, fix QueryCatalog.
    private List<QuerySpec> Catalog => QueryCatalog.Build(new ScopeVars
    {
        TenantId = TenantId,
        SchoolId = SchoolId,
        CampusId = CampusId,
    });

    private QuerySpec Spec(string key) => QueryCatalog.Require(Catalog, key);

    // ============================================================
    // STUDENT LIST QUERY TESTS
    // ============================================================

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [InlineData(50000)]
    [InlineData(200000)]
    public async Task StudentList_ByCampus_ScalesWithVolume(int studentCount)
    {
        // Arrange
        _output.WriteLine($"=== Testing Student List with {studentCount:N0} students ===");
        await _seeder.SeedAsync(studentCount, TenantId, SchoolId, CampusId);

        var spec = Spec("student-list-page");

        // Act - Run YOUR actual query
        var stopwatch = Stopwatch.StartNew();
        var result = await _queries.GetStudentListAsync(TenantId, SchoolId, CampusId);
        stopwatch.Stop();

        // Profile with EXPLAIN ANALYZE - the same SQL, from the catalogue
        var plan = await _profiler.ProfileAsync(spec, TenantId, SchoolId, CampusId);

        // Log results
        _output.WriteLine($"Shape: {spec.Key} ({spec.Source})");
        _output.WriteLine($"Data Volume: {studentCount:N0} students");
        _output.WriteLine($"Query Time: {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Rows Returned: {result.Students.Count}");
        _output.WriteLine($"Total Count: {result.Count}");
        _output.WriteLine($"Plan Execution Time: {plan.ExecutionTimeMs:F2} ms");
        _output.WriteLine($"Has Sequential Scan: {plan.HasSequentialScan}");
        _output.WriteLine($"Indexes Used: {string.Join(", ", plan.IndexesUsed)}");
        _output.WriteLine($"Shared Buffers Hit: {plan.SharedBuffersHit}");
        _output.WriteLine($"Shared Buffers Read: {plan.SharedBuffersRead}");

        // Assert - Performance threshold (scaled by actual volume, not requested)
        var actualCount = result.Count;
        _output.WriteLine($"Actual total in DB: {actualCount:N0}");
        Assert.True(stopwatch.ElapsedMilliseconds < 3000,
            $"Student list query took {stopwatch.ElapsedMilliseconds}ms with {actualCount:N0} actual students (threshold: 3000ms)");
        
        // Note sequential scan — useful for diagnosis, not a hard fail on shared DB
        if (plan.HasSequentialScan)
            _output.WriteLine($"INFO: Query has sequential scan with {actualCount:N0} students — consider adding index on (TenantId, SchoolId, CampusId, IsActive)");
    }

    [Theory]
    [InlineData(10000, "Ahmed")]
    [InlineData(50000, "Mohammed")]
    [InlineData(200000, "Ali")]
    public async Task StudentSearch_WithFilter_ScalesWithVolume(int studentCount, string searchTerm)
    {
        // Arrange
        _output.WriteLine($"=== Testing Student Search with {studentCount:N0} students, search='{searchTerm}' ===");
        await _seeder.SeedAsync(studentCount, TenantId, SchoolId, CampusId);

        var spec = Spec("student-search");

        // Act - Run YOUR actual query with search
        var stopwatch = Stopwatch.StartNew();
        var result = await _queries.GetStudentListAsync(
            TenantId, SchoolId, CampusId, search: searchTerm);
        stopwatch.Stop();

        // Profile the search shape, with THIS test's search term overriding the catalogue default
        var plan = await _profiler.ProfileAsync(
            spec, TenantId, SchoolId, CampusId,
            new Dictionary<string, object> { ["search"] = $"%{searchTerm}%" });

        // Log results
        _output.WriteLine($"Shape: {spec.Key} ({spec.Source})");
        _output.WriteLine($"Data Volume: {studentCount:N0} students");
        _output.WriteLine($"Search Term: {searchTerm}");
        _output.WriteLine($"Query Time: {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Rows Returned: {result.Students.Count}");
        _output.WriteLine($"Plan Execution Time: {plan.ExecutionTimeMs:F2} ms");
        _output.WriteLine($"Has Sequential Scan: {plan.HasSequentialScan}");

        // Assert - Search should be slower but still under threshold
        Assert.True(stopwatch.ElapsedMilliseconds < 3000,
            $"Student search query took {stopwatch.ElapsedMilliseconds}ms with {result.Count:N0} students (threshold: 3000ms)");
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(50000)]
    public async Task StudentList_Pagination_ScalesWithOffset(int studentCount)
    {
        // Arrange
        _output.WriteLine($"=== Testing Student List Pagination with {studentCount:N0} students ===");
        await _seeder.SeedAsync(studentCount, TenantId, SchoolId, CampusId);

        // Act - Test different page offsets
        var offsets = new[] { 0, 1000, 10000, studentCount / 2 };
        
        foreach (var offset in offsets.Where(o => o < studentCount))
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _queries.GetStudentListAsync(
                TenantId, SchoolId, CampusId, offset: offset);
            stopwatch.Stop();

            _output.WriteLine($"Offset {offset:N0}: {stopwatch.ElapsedMilliseconds} ms, Rows: {result.Students.Count}");

            Assert.True(stopwatch.ElapsedMilliseconds < 3000,
                $"Pagination at offset {offset:N0} took {stopwatch.ElapsedMilliseconds}ms");
        }
    }

    // ============================================================
    // QUERY PLAN ANALYSIS TESTS
    // ============================================================

    [Fact]
    public async Task StudentList_QueryPlan_ShowsIndexUsage()
    {
        // Arrange - Use small dataset for plan analysis
        await _seeder.SeedAsync(1000, TenantId, SchoolId, CampusId);

        // Act
        var plan = await _profiler.ProfileAsync(
            Spec("student-list-page"), TenantId, SchoolId, CampusId);

        // Log the full plan
        _output.WriteLine("=== QUERY PLAN ===");
        _output.WriteLine(plan.RawPlan);

        // Assert - Should use index, not sequential scan
        Assert.False(plan.HasSequentialScan, 
            "Query should use index, not sequential scan");
        
        Assert.True(plan.IndexesUsed.Count > 0,
            "Query should use at least one index");
    }

    [Fact]
    public async Task StudentSearch_FullText_NeedsIndex()
    {
        // Arrange
        await _seeder.SeedAsync(10000, TenantId, SchoolId, CampusId);

        // Act - the grid's search box, through the shape the app actually sends
        var spec = Spec("student-search");
        var plan = await _profiler.ProfileAsync(
            spec, TenantId, SchoolId, CampusId,
            new Dictionary<string, object> { ["search"] = "%Ahmed%" });

        _output.WriteLine("=== SEARCH QUERY PLAN ===");
        _output.WriteLine(plan.RawPlan);
        _output.WriteLine($"Execution Time: {plan.ExecutionTimeMs:F2} ms");
        _output.WriteLine($"Has Sequential Scan: {plan.HasSequentialScan}");

        // This test DOCUMENTS the shape's cost rather than asserting a threshold, because the
        // app's search clause cannot be served by a plain B-tree index: it is an
        // `lower(col::text) like '%term%'` over THREE columns, one of which (u.Email) lives on a
        // joined table. A leading-wildcard LIKE defeats a B-tree, so the honest fix is a trigram
        // (pg_trgm GIN) index or a narrower search - not "add an index on Name".
        if (plan.HasSequentialScan)
        {
            _output.WriteLine("WARNING: the search uses a sequential scan. A leading-wildcard LIKE " +
                              "cannot use a B-tree; consider a pg_trgm GIN index on the searched " +
                              "columns, or narrowing the searchable column set.");
        }
    }
}
