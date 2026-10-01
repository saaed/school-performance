using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the INVENTORY, LIBRARY, TRANSPORT and ACCOUNTING modules' tables for several campuses, and
/// asserts the shape that makes them measurable.
///
/// ⚠️ WHY THESE FOUR LIVE IN ONE FIXTURE. Each is a module whose tables held ZERO rows, and each
/// one's grid/statement specs therefore reported SKIP. The seeding rule they share is the same one
/// `HrDatasetTests` documents, so it is stated once here rather than four times, and the campus
/// selection is one helper instead of four copies that could drift.
///
/// ⚠️ WHY SEVERAL CAMPUSES, NOT ONE. An index on `(tenantid, schoolid, campusid)` cannot be judged
/// on a scope that holds the whole table - that is the trap V130 recorded on `student`. So the rows
/// are spread across the campuses that hold a FRACTION of the table, which is also the population
/// `db-report` measures (it takes the MEDIAN campus by student count).
///
/// ⚠️ WHY A CAMPUS HOLDING MORE THAN 10% OF THE STUDENTS IS EXCLUDED. On `ayra_perf` that is campus
/// 1 (843,414 of 879,514 students) - an artifact of an early single-campus seed, and the one scope on
/// which a scope-column index can never be selective. Seeding it would put every module's rows
/// exactly where nothing is measured.
///
/// Opt in with the SAME flag the dataset seeder uses, because this seeds data rather than asserting
/// application behaviour:
///
///   SCUBE_PERF_DATASET=1 dotnet test SchoolDataVolume.csproj \
///       --filter "FullyQualifiedName~ModuleDataset"
///
/// Size it with SCUBE_PERF_MODULE_CAMPUSES (default 6) and the per-module knobs
/// (SCUBE_PERF_INV_ITEMS, SCUBE_PERF_LIB_BOOKS, SCUBE_PERF_TRANSPORT_RIDERS,
/// SCUBE_PERF_ACCT_ENTRIES). SCUBE_PERF_FORCE=1 re-seeds a campus that already holds rows.
/// </summary>
[Collection("Sequential")]
public class ModuleDatasetTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;

    private const long TenantId = SeedCampuses.TenantId;
    private const long SchoolId = SeedCampuses.SchoolId;

    public ModuleDatasetTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";
    }

    public void Dispose()
    {
        // Nothing to clean up: this fixture is the point of the database.
    }

    // The rules this fixture shares with every other dataset fixture live in `SeedCampuses`, so
    // there is ONE copy of the campus-selection query and ONE scope-selectivity assertion. These
    // thin aliases exist so the call sites below read the same as they always have.
    private static bool Enabled => SeedCampuses.DatasetEnabled;

    private static bool Force => SeedCampuses.Force;

    private static int EnvInt(string name, int fallback) => SeedCampuses.EnvInt(name, fallback);

    private static Task<List<long>> CampusesAsync(NpgsqlConnection conn) =>
        SeedCampuses.CampusesAsync(conn);

    private Task AssertScopeIsSelectiveAsync(NpgsqlConnection conn, string table, string label) =>
        SeedCampuses.AssertScopeIsSelectiveAsync(conn, _output, table, label);

    [Fact]
    public async Task Inventory_dataset_seeds_the_inventory_volume_tables_across_campuses()
    {
        if (!Enabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the module perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed inventory into - " +
            "run PerfDatasetTests first");

        var options = new InventorySeedOptions
        {
            ItemsPerCampus = EnvInt("SCUBE_PERF_INV_ITEMS", 200),
            MovementsPerItem = EnvInt("SCUBE_PERF_INV_MOVEMENTS", 30),
            Force = Force,
        };

        _output.WriteLine($"Seeding INVENTORY for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.ItemsPerCampus} items, " +
                          $"{options.MovementsPerItem} movements each");
        _output.WriteLine("");

        var seeder = new InventoryModuleSeeder(_connectionString);
        var totalItems = 0;
        var totalMovements = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(TenantId, SchoolId, campusId, options, verbose: false);

            totalItems += result.Items;
            totalMovements += result.Movements;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Items,6:N0} items {result.StockRows,6:N0} stock " +
                $"{result.Movements,8:N0} movements {result.PurchaseOrders,4} POs {result.Grns,4} GRNs " +
                $"{result.Assets,4} assets {result.DepreciationRows,6:N0} depreciation" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalItems:N0} items, {totalMovements:N0} stock movements");

        Assert.True(totalItems > 0, "no inventory items were seeded, so no inventory grid is measurable");
        Assert.True(totalMovements > 0,
            "no stock movements were seeded - `inv.movement` is the module's volume grid and would " +
            "still report SKIP");

        // A movement that references no item is invisible to the ledger's join (the grid reads
        // `JOIN InvItem i ON i.Id = m.InvItemId`), which is the shape that makes a grid render
        // empty while the table looks populated.
        var orphanMovements = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM invmovement m
               WHERE m.tenantid = @tenantId
                 AND NOT EXISTS (SELECT 1 FROM invitem i WHERE i.id = m.invitemid)",
            new { tenantId = TenantId });
        Assert.Equal(0, orphanMovements);

        // The receipt line's item is what the receiving path moves stock for; a receipt line with
        // no item is the J8 defect this column exists to prevent.
        var itemlessReceiptLines = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM invgrnline WHERE tenantid = @tenantId AND invitemid IS NULL",
            new { tenantId = TenantId });
        Assert.Equal(0, itemlessReceiptLines);

        await AssertScopeIsSelectiveAsync(conn, "invmovement", "Inventory");
    }

    [Fact]
    public async Task Library_dataset_seeds_the_library_volume_tables_across_campuses()
    {
        if (!Enabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the module perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed the library into - " +
            "run PerfDatasetTests first");

        var options = new LibrarySeedOptions
        {
            BooksPerCampus = EnvInt("SCUBE_PERF_LIB_BOOKS", 300),
            IssuesPerMember = EnvInt("SCUBE_PERF_LIB_ISSUES", 4),
            Force = Force,
        };

        _output.WriteLine($"Seeding LIBRARY for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.BooksPerCampus} books, " +
                          $"{options.IssuesPerMember} issues per member");
        _output.WriteLine("");

        var seeder = new LibraryModuleSeeder(_connectionString);
        var totalBooks = 0;
        var totalIssues = 0;
        var totalFines = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(TenantId, SchoolId, campusId, options, verbose: false);

            totalBooks += result.Books;
            totalIssues += result.Issues;
            totalFines += result.Fines;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Books,6:N0} books {result.Copies,6:N0} copies " +
                $"{result.Members,6:N0} members {result.Issues,8:N0} issues {result.Fines,6:N0} fines" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalBooks:N0} books, {totalIssues:N0} issues, {totalFines:N0} fines");

        Assert.True(totalBooks > 0, "no library books were seeded, so no library grid is measurable");
        Assert.True(totalIssues > 0,
            "no issues were seeded - `libraryissue` is the module's volume table and every " +
            "circulation spec would still report SKIP");

        // A loan whose member has no student row renders a blank member name in the circulation
        // grid (it resolves the name through `Student st ON st.Id = lm.StudentId`), so the members
        // seeded here must be resolveable.
        var unresolvedMembers = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM librarymember lm
               WHERE lm.tenantid = @tenantId AND lm.membertype = 'Student'
                 AND lm.studentid IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM student s WHERE s.id = lm.studentid)",
            new { tenantId = TenantId });
        Assert.Equal(0, unresolvedMembers);

        // ⚠️ THE DESK'S OWN RULE: a copy holds at most ONE active loan. This is the invariant the
        // seeder's pass-based assignment exists to preserve, and the screen's issue path enforces it.
        var doubleIssuedCopies = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM (
                  SELECT bookcopyid FROM libraryissue
                   WHERE tenantid = @tenantId AND status = 'Issued'
                GROUP BY bookcopyid HAVING COUNT(*) > 1
              ) x",
            new { tenantId = TenantId });
        Assert.Equal(0, doubleIssuedCopies);

        await AssertScopeIsSelectiveAsync(conn, "libraryissue", "Library");
    }

    [Fact]
    public async Task Transport_dataset_seeds_the_transport_tables_across_campuses()
    {
        if (!Enabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the module perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed transport into - " +
            "run PerfDatasetTests first");

        var options = new TransportSeedOptions
        {
            RidersPerCampus = EnvInt("SCUBE_PERF_TRANSPORT_RIDERS", 500),
            Force = Force,
        };

        _output.WriteLine($"Seeding TRANSPORT for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.RidersPerCampus} riders");
        _output.WriteLine("");

        var seeder = new TransportModuleSeeder(_connectionString);
        var totalVehicles = 0;
        var totalRiders = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(TenantId, SchoolId, campusId, options, verbose: false);

            totalVehicles += result.Vehicles;
            totalRiders += result.StudentAssignments;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Vehicles,4} vehicles {result.Drivers,4} drivers " +
                $"{result.Attendants,4} attendants {result.Routes,4} routes {result.RouteStops,5} stops " +
                $"{result.VehicleAssignments,4} assignments {result.StudentAssignments,6:N0} riders" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalVehicles} vehicles, {totalRiders:N0} riders");

        Assert.True(totalVehicles > 0, "no vehicles were seeded, so no transport grid is measurable");
        Assert.True(totalRiders > 0,
            "no riders were seeded - `transportstudentassignment` is the module's volume table");

        // ⚠️ `ux_transportvehicleassignment_activevehicle` is a PARTIAL unique index, so the
        // database would have refused a second active row - but only for the vehicles that HAVE one.
        // Asserting it here is what proves the seeder wrote the shape the assignment screen expects
        // rather than one row per vehicle by accident.
        var multipleActivePerVehicle = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM (
                  SELECT vehicleid FROM transportvehicleassignment
                   WHERE tenantid = @tenantId AND isactive = true
                GROUP BY vehicleid HAVING COUNT(*) > 1
              ) x",
            new { tenantId = TenantId });
        Assert.Equal(0, multipleActivePerVehicle);

        // A rider must resolve to an ENROLMENT on its own campus - the transport student list
        // joins through it, and a dangling id renders as nobody.
        var unresolvedRiders = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM transportstudentassignment tsa
               WHERE tsa.tenantid = @tenantId
                 AND NOT EXISTS (SELECT 1 FROM studentenrollment se
                                  WHERE se.id = tsa.studentenrollmentid
                                    AND se.tenantid = tsa.tenantid
                                    AND se.campusid = tsa.campusid)",
            new { tenantId = TenantId });
        Assert.Equal(0, unresolvedRiders);

        await AssertScopeIsSelectiveAsync(conn, "transportstudentassignment", "Transport");
    }

    [Fact]
    public async Task Accounting_dataset_seeds_the_journal_and_the_chart_across_campuses()
    {
        if (!Enabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the module perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed accounting into - " +
            "run PerfDatasetTests first");

        var options = new AccountingSeedOptions
        {
            JournalEntriesPerCampus = EnvInt("SCUBE_PERF_ACCT_ENTRIES", 1500),
            Force = Force,
        };

        _output.WriteLine($"Seeding ACCOUNTING for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.JournalEntriesPerCampus} journal entries");
        _output.WriteLine("");

        var seeder = new AccountingModuleSeeder(_connectionString);
        var totalEntries = 0;
        var totalLines = 0;
        var totalPostings = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(TenantId, SchoolId, campusId, options, verbose: false);

            totalEntries += result.JournalEntries;
            totalLines += result.JournalLines;
            totalPostings += result.FinancialPostings;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.FiscalPeriods,3} periods {result.Accounts,4} accounts " +
                $"{result.JournalEntries,8:N0} entries {result.JournalLines,8:N0} lines " +
                $"{result.FinancialPostings,6:N0} postings" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalEntries:N0} journal entries, {totalLines:N0} lines, " +
                          $"{totalPostings:N0} postings");

        Assert.True(totalEntries > 0, "no journal entries were seeded, so no statement is measurable");
        Assert.True(totalLines > 0, "no journal lines were seeded");

        // ⚠️ BALANCE IS THE WHOLE POINT OF A JOURNAL. The statements this fixture exists to make
        // measurable (trial balance, P&L, balance sheet) are only meaningful over a BALANCED ledger,
        // and `chk_debit_credit` guarantees each LINE is one-sided but says nothing about the entry.
        var unbalanced = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM (
                  SELECT jel.journalentryid
                    FROM journalentryline jel
                    JOIN journalentry je ON je.id = jel.journalentryid
                   WHERE je.tenantid = @tenantId
                GROUP BY jel.journalentryid
                  HAVING ABS(SUM(jel.debit) - SUM(jel.credit)) > 0.001
              ) x",
            new { tenantId = TenantId });
        Assert.Equal(0, unbalanced);

        // An entry must land in a period the campus owns: `journalentry.fiscalperiodid` drives the
        // "entry dated into a closed period" rule and the period filter on the grid.
        var orphanEntries = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM journalentry je
               WHERE je.tenantid = @tenantId
                 AND NOT EXISTS (SELECT 1 FROM fiscalperiod fp
                                  WHERE fp.id = je.fiscalperiodid
                                    AND fp.tenantid = je.tenantid
                                    AND fp.campusid = je.campusid)",
            new { tenantId = TenantId });
        Assert.Equal(0, orphanEntries);

        await AssertScopeIsSelectiveAsync(conn, "journalentry", "Accounting");
    }

}
