using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="InvAssetWorkspaceSeeder"/>. One campus gets a normal set of each.</summary>
public sealed class InvAssetWorkspaceSeedOptions
{
    /// <summary>Assignment/hand-over rows (one per asset, cycled).</summary>
    public int Assignments { get; set; } = 40;

    /// <summary>Disposal records.</summary>
    public int Disposals { get; set; } = 30;

    /// <summary>Revaluation records.</summary>
    public int Revaluations { get; set; } = 30;

    /// <summary>Maintenance log rows - several per asset, which is what the asset detail modal reads.</summary>
    public int Maintenance { get; set; } = 80;

    public bool Force { get; set; }
}

/// <summary>What one campus's asset-workspace seed produced.</summary>
public sealed class InvAssetWorkspaceSeedResult
{
    public bool Skipped { get; set; }
    public int Assignments { get; set; }
    public int Disposals { get; set; }
    public int Revaluations { get; set; }
    public int Maintenance { get; set; }
}

/// <summary>
/// Seeds the INVENTORY ASSET LIFECYCLE tables for one campus - assignment/hand-over, disposal,
/// revaluation and the maintenance log - so their grids and the asset detail modal measure instead
/// of reporting SKIP.
///
/// WHY THIS EXISTS
/// ---------------
/// `InventoryModuleSeeder` fills the procurement spine (`invitem`, `invstock`, `invmovement`,
/// `invpurchaseorder`, `invgrn`, `invasset`, `invdepreciation`) - which is what the item and stock
/// grids scan. These four are what an asset's OWN screens page over, and all four held ZERO rows in
/// every database here, so `inv-asset-disposal-page` and its siblings could only report **SKIP**:
/// honest, and useless at once - a grid the application ships, reading as "not measured yet".
///
/// ⚠️ IT DEPENDS ON `InventoryModuleSeeder`, AND THAT IS A PRECONDITION RATHER THAN A CONVENIENCE.
/// Every row here hangs off a real `invasset` of the campus (both paged repositories JOIN
/// `InvAsset`), so a seeder that invented an asset id would write rows that exist and are INVISIBLE -
/// the failure mode where a table looks populated while the screen stays empty. This throws rather
/// than falling back to a placeholder.
///
/// ⚠️ TWO UNIQUENESS RULES ARE ENFORCED BY THE DATABASE AND ONE OF THEM IS PARTIAL, which is why the
/// status column is assigned the way it is. See the two notes inside <see cref="SeedAsync"/>.
///
/// IDEMPOTENT: a campus that already holds disposals is skipped unless Force is set (and a skipped
/// campus still REPORTS what it holds, so a fixture's own assertion is true on a re-run).
/// </summary>
public sealed class InvAssetWorkspaceSeeder : BaseSeeder
{
    public InvAssetWorkspaceSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables an asset-workspace load invalidates statistics for.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "invassetassignment", "invassetdisposal", "invassetrevaluation", "invmaintenance"
    };

    /// <summary>The disposal grid is what this dataset exists for, so it is the skip marker.</summary>
    private const string SkipMarkerTable = "invassetdisposal";

    /// <summary>The six types `inv.disposal.html` offers - read off the page, not invented.</summary>
    private static readonly string[] DisposalTypes =
        { "Scrapped", "Sold", "Donated", "Lost", "Stolen", "Obsolete" };

    public async Task<InvAssetWorkspaceSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, InvAssetWorkspaceSeedOptions options, bool verbose = true)
    {
        var result = new InvAssetWorkspaceSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {SkipMarkerTable}"
            + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
                Console.WriteLine($"  Asset workspace: campus {campusId} already holds {existing:N0} disposals - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // The assets every row below hangs off. Both paged repositories JOIN InvAsset, so an asset id
        // that does not resolve is a row no screen can list.
        var assetIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM invasset
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (assetIds.Count == 0)
            throw new InvalidOperationException(
                $"campus {campusId} holds no invasset row, so no asset lifecycle record could reference one - "
                + "run InventoryModuleSeeder first.");

        var employeeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employee
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (options.Force && existing > 0)
        {
            // All four are independent children of `invasset`, so no ordering between them matters -
            // only that none is cleared after something that references it.
            foreach (var table in new[]
                     {
                         "invassetassignment", "invassetdisposal", "invassetrevaluation", "invmaintenance"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose) Console.WriteLine($"  Asset workspace: campus {campusId} cleared for a forced re-seed");
        }

        // ⚠️ `invassetassignment` / `invassetdisposal` / `invassetrevaluation` / `invmaintenance` all
        // have a NON-identity `id` with NO default, so unlike every identity table in this project the
        // id has to be supplied. `GetMaxIdAsync` is what keeps the value above whatever the campus
        // (or a neighbouring one) already wrote.
        var assignmentId = await GetMaxIdAsync(conn, "invassetassignment");
        var disposalId = await GetMaxIdAsync(conn, "invassetdisposal");
        var revaluationId = await GetMaxIdAsync(conn, "invassetrevaluation");
        var maintenanceId = await GetMaxIdAsync(conn, "invmaintenance");

        // ------------------------------------------------------------------
        // 1. Assignments / hand-overs. An asset is handed to a person and (sometimes) comes back.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Assignments; i++)
        {
            var assetId = assetIds[i % assetIds.Count];
            var employeeId = employeeIds.Count > 0 ? employeeIds[i % employeeIds.Count] : (long?)null;
            var assigned = DateTime.Today.AddDays(-(i % 180));
            // Every third assignment is still open - the state the asset's "assigned to" badge reads.
            var returned = i % 3 == 0 ? assigned.AddDays(30) : (DateTime?)null;

            await conn.ExecuteAsync(
                @"INSERT INTO invassetassignment
                      (id, tenantid, schoolid, campusid, assetid, assignedtoemployeeid,
                       assigneddate, returneddate, notes, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@id, @tenantId, @schoolId, @campusId, @assetId, @employeeId,
                          @assigned, @returned, 'Perf seed hand-over', 1, 1, @now, @now)",
                new
                {
                    id = ++assignmentId, tenantId, schoolId, campusId, assetId, employeeId,
                    assigned, returned, now
                });
            result.Assignments++;
        }

        // ------------------------------------------------------------------
        // 2. Disposals.
        //
        // ⚠️ `ux_invassetdisposal_open_per_asset` IS A PARTIAL UNIQUE INDEX on (tenantid, schoolid,
        // assetid) WHERE status IN ('Draft','Approved') - at most ONE unfinished disposal per asset.
        // So the FIRST pass over the asset list may be open and every later pass must be CLOSED, or
        // the seeder answers a 23505 the moment Disposals exceeds the asset count.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Disposals; i++)
        {
            var assetId = assetIds[i % assetIds.Count];
            var status = i < assetIds.Count ? "Draft" : "Completed";
            var disposalDate = DateTime.Today.AddDays(-(i % 120));

            await conn.ExecuteAsync(
                @"INSERT INTO invassetdisposal
                      (id, tenantid, schoolid, campusid, assetid, disposalnumber, disposaltype,
                       disposaldate, disposalvalue, reason, status, notes,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@id, @tenantId, @schoolId, @campusId, @assetId, @number, @type,
                          @disposalDate, @value, 'Perf seed disposal', @status, NULL,
                          1, 1, @now, @now)",
                new
                {
                    id = ++disposalId, tenantId, schoolId, campusId, assetId,
                    // ⚠️ `ux_invassetdisposal_number` is UNIQUE on (tenantid, schoolid, disposalnumber)
                    // - NOT campus-scoped - so the campus id has to be part of the code.
                    number = $"PERF-DSP-{campusId}-{i + 1}",
                    type = DisposalTypes[i % DisposalTypes.Length],
                    disposalDate,
                    value = i % 4 == 0 ? 500m : 0m,
                    status, now
                });
            result.Disposals++;
        }

        // ------------------------------------------------------------------
        // 3. Revaluations. No partial index here, so the status does not have to alternate - but the
        //    GRID's status filter is exercised on both an open and a closed value.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Revaluations; i++)
        {
            var assetId = assetIds[i % assetIds.Count];
            var revaluationDate = DateTime.Today.AddDays(-(i % 150));
            var oldValue = 10_000m + (i % 5) * 1_000m;

            await conn.ExecuteAsync(
                @"INSERT INTO invassetrevaluation
                      (id, tenantid, schoolid, campusid, assetid, revaluationnumber, revaluationdate,
                       oldvalue, newvalue, reason, status, notes,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@id, @tenantId, @schoolId, @campusId, @assetId, @number, @revaluationDate,
                          @oldValue, @newValue, 'Perf seed revaluation', @status, NULL,
                          1, 1, @now, @now)",
                new
                {
                    id = ++revaluationId, tenantId, schoolId, campusId, assetId,
                    // `ux_invassetrevaluation_number` is (tenantid, schoolid, revaluationnumber) - same
                    // tenant+school-only shape as the disposal number, so the campus is stamped in.
                    number = $"PERF-RVL-{campusId}-{i + 1}",
                    revaluationDate, oldValue,
                    newValue = oldValue + 2_000m,
                    status = i < assetIds.Count ? "Approved" : "Completed",
                    now
                });
            result.Revaluations++;
        }

        // ------------------------------------------------------------------
        // 4. Maintenance - several rows per asset, because the asset detail modal reads them BY ASSET
        //    and a single row per asset would time an empty result set.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Maintenance; i++)
        {
            var assetId = assetIds[i % assetIds.Count];
            var maintenanceDate = DateTime.Today.AddDays(-(i % 400));

            await conn.ExecuteAsync(
                @"INSERT INTO invmaintenance
                      (id, tenantid, schoolid, campusid, assetid, maintenancedate, description, cost,
                       performedby, nextduedate, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@id, @tenantId, @schoolId, @campusId, @assetId, @maintenanceDate, @description, @cost,
                          'Perf Vendor', @nextDue, 1, 1, @now, @now)",
                new
                {
                    id = ++maintenanceId, tenantId, schoolId, campusId, assetId,
                    maintenanceDate,
                    description = $"Perf seed service #{i + 1}",
                    cost = 150m + (i % 10) * 25m,
                    nextDue = maintenanceDate.AddMonths(6),
                    now
                });
            result.Maintenance++;
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  Asset workspace: campus {campusId} -> {result.Assignments:N0} assignments, "
                + $"{result.Disposals:N0} disposals, {result.Revaluations:N0} revaluations, "
                + $"{result.Maintenance:N0} maintenance rows");
        }

        return result;
    }

    /// <summary>Fills <paramref name="result"/> from what the campus already holds (the skip path).</summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, InvAssetWorkspaceSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table}"
                + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.Assignments = await ScopedAsync("invassetassignment");
        result.Disposals = await ScopedAsync("invassetdisposal");
        result.Revaluations = await ScopedAsync("invassetrevaluation");
        result.Maintenance = await ScopedAsync("invmaintenance");
    }
}
