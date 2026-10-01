using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the INVENTORY ASSET LIFECYCLE tables (`invassetassignment`, `invassetdisposal`,
/// `invassetrevaluation`, `invmaintenance`) and asserts the shape that makes their grid specs
/// measure instead of report SKIP.
///
/// ⚠️ WHY THESE FOUR, AND WHY THEY ARE NOT THE PROCUREMENT SPINE. `InventoryModuleSeeder` fills
/// `invitem` / `invstock` / `invmovement` / `invpurchaseorder` / `invgrn` / `invasset` - the tables
/// the item and stock grids scan. These four are what an asset's OWN screens page over, and all four
/// held ZERO rows, so `inv-asset-disposal-page` and its siblings could only report SKIP.
///
/// ⚠️ EVERY ASSERTION IS A JOIN OR A CONSTRAINT, NOT A ROW COUNT. A count proves the INSERT ran; it
/// cannot see a row the application cannot reach. The three that matter:
///   * every row's `assetid` must resolve to an asset OF THIS CAMPUS - both paged repositories do
///     `JOIN InvAsset a ON a.Id = d.AssetId`, so a row pointing at a neighbouring campus's asset is a
///     row that exists and is invisible.
///   * `ux_invassetdisposal_open_per_asset` is a PARTIAL unique index (Draft/Approved only) - one
///     unfinished disposal per asset. The seeder alternates the status to respect it; this asserts it
///     held, because the index only fires on the SECOND open row.
///   * `ux_invassetdisposal_number` / `ux_invassetrevaluation_number` are UNIQUE on
///     (tenantid, schoolid, number) - **NOT campus-scoped** - so the seeder stamps the campus id into
///     every code and two campuses seeding one school must not collide.
///
/// Opt in with the same flag the other dataset seeders use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~InvAssetWorkspaceDataset"
/// </summary>
public sealed class InvAssetWorkspaceDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public InvAssetWorkspaceDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Asset_workspace_dataset_fills_the_lifecycle_tables_their_asset_screens_page_over()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the asset-workspace perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed asset workspaces into - " +
            "run PerfDatasetTests first");

        var options = new InvAssetWorkspaceSeedOptions
        {
            Assignments = SeedCampuses.EnvInt("SCUBE_PERF_ASSET_ASSIGNMENTS", 40),
            Disposals = SeedCampuses.EnvInt("SCUBE_PERF_ASSET_DISPOSALS", 30),
            Revaluations = SeedCampuses.EnvInt("SCUBE_PERF_ASSET_REVALUATIONS", 30),
            Maintenance = SeedCampuses.EnvInt("SCUBE_PERF_ASSET_MAINTENANCE", 80),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding ASSET WORKSPACE for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.Assignments} assignments, " +
                          $"{options.Disposals} disposals, {options.Revaluations} revaluations, " +
                          $"{options.Maintenance} maintenance rows");
        _output.WriteLine("");

        var seeder = new InvAssetWorkspaceSeeder(_connectionString);
        var totalRows = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRows += result.Assignments + result.Disposals + result.Revaluations + result.Maintenance;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Assignments,4} assignments {result.Disposals,4} disposals " +
                $"{result.Revaluations,4} revaluations {result.Maintenance,4} maintenance" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            await AssertCampusRowsAreReachableAsync(conn, campusId, result);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRows:N0} asset-lifecycle rows");

        Assert.True(totalRows > 0,
            "no asset lifecycle data was seeded, so the disposal/revaluation/assignment/maintenance " +
            "grids would still report SKIP");

        await AssertDisposalAndRevaluationNumbersAreUniqueAsync(conn);
        await AssertAtMostOneOpenDisposalPerAssetAsync(conn);
    }

    /// <summary>
    /// The joins each seeded row has to satisfy for the APPLICATION to see it - both paged
    /// repositories JOIN `InvAsset`, and the assignment list joins it too.
    /// </summary>
    private async Task AssertCampusRowsAreReachableAsync(
        NpgsqlConnection conn, long campusId, InvAssetWorkspaceSeedResult result)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        // One statement per table, same shape: a row whose asset is not on this campus.
        var checks = new (string Table, string Alias, int Rows)[]
        {
            ("invassetassignment", "aa", result.Assignments),
            ("invassetdisposal", "d", result.Disposals),
            ("invassetrevaluation", "r", result.Revaluations),
            ("invmaintenance", "m", result.Maintenance),
        };

        foreach (var (table, alias, rows) in checks)
        {
            if (rows == 0) continue;

            var orphans = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {table} {alias}
                    WHERE {alias}.tenantid = @tenantId AND {alias}.schoolid = @schoolId
                      AND {alias}.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM invasset a
                           WHERE a.id = {alias}.assetid
                             AND a.tenantid = @tenantId AND a.schoolid = @schoolId
                             AND a.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphans == 0,
                $"campus {campusId} has {orphans} {table} row(s) whose asset is not on the campus - " +
                "the repository JOINS InvAsset, so those rows would never be listed");
        }

        if (result.Maintenance > 0)
        {
            // The asset detail modal reads maintenance BY ASSET (`GetByAssetId`). If every row landed
            // on one asset the read would still work, but the worst-case asset would not be exercised
            // by `ScopeVars.InvAssetId`, which points at the asset holding the MOST rows.
            var assetsWithMaintenance = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT assetid) FROM invmaintenance
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });

            Assert.True(assetsWithMaintenance > 1,
                $"campus {campusId} spread its {result.Maintenance} maintenance rows over only " +
                $"{assetsWithMaintenance} asset(s), so the per-asset read is not representative");
        }

        if (result.Assignments > 0)
        {
            // The list query LEFT JOINs Users on AssignedToUserId and merely projects the name, so a
            // NULL user is legal - but an assignment with NEITHER a user nor an employee is an asset
            // handed to nobody, which is not a state the screen can produce.
            var handedToNobody = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM invassetassignment
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND assignedtouserid IS NULL AND assignedtoemployeeid IS NULL",
                new { tenantId, schoolId, campusId });

            Assert.True(handedToNobody == 0,
                $"campus {campusId} has {handedToNobody} assignment(s) with no assignee at all");
        }
    }

    /// <summary>
    /// Both number indexes are UNIQUE on (tenantid, schoolid, number) - deliberately NOT
    /// campus-scoped - so a seeder that used a per-campus counter would collide the moment a second
    /// campus was seeded.
    /// </summary>
    private static async Task AssertDisposalAndRevaluationNumbersAreUniqueAsync(NpgsqlConnection conn)
    {
        foreach (var (table, column) in new[] { ("invassetdisposal", "disposalnumber"), ("invassetrevaluation", "revaluationnumber") })
        {
            var duplicates = await conn.QueryAsync<string>(
                $@"SELECT {column} FROM {table}
                    WHERE tenantid = @tenantId
                    GROUP BY {column} HAVING COUNT(*) > 1",
                new { tenantId = SeedCampuses.TenantId });

            var list = duplicates.ToList();
            Assert.True(list.Count == 0,
                $"ux_{table}_number is UNIQUE on (tenantid, schoolid, {column}) but the seeder wrote " +
                $"{list.Count} duplicated code(s): {string.Join(", ", list.Take(10))}");
        }
    }

    /// <summary>
    /// `ux_invassetdisposal_open_per_asset` is a PARTIAL unique index on (tenantid, schoolid, assetid)
    /// WHERE status IN ('Draft','Approved'). The index enforces it, so a violation would have failed
    /// the INSERT - this asserts the invariant directly, which is what catches a seeder that got the
    /// alternating status wrong on a database where the index is ever dropped.
    /// </summary>
    private static async Task AssertAtMostOneOpenDisposalPerAssetAsync(NpgsqlConnection conn)
    {
        var duplicates = await conn.QueryAsync<string>(
            @"SELECT assetid::text FROM invassetdisposal
               WHERE status IN ('Draft', 'Approved')
            GROUP BY tenantid, schoolid, assetid HAVING COUNT(*) > 1");

        var list = duplicates.ToList();
        Assert.True(list.Count == 0,
            $"ux_invassetdisposal_open_per_asset allows ONE unfinished disposal per asset, but " +
            $"{list.Count} asset(s) hold more than one: {string.Join(", ", list.Take(10))}");
    }
}
