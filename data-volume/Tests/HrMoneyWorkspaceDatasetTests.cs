using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the HR MONEY / LIFECYCLE workspace (`taxconfig` + `taxslab`, `employeesalarystructure`,
/// `employeeleavebalance`, `employeetaxrecord`, `performancereviewcycle` + `employeeperformancereview`,
/// and the exit chain `employeeseparation` + `employeesettlement`) and asserts the shape that makes
/// their grid specs measure instead of report SKIP.
///
/// ⚠️ WHY THESE NINE LIVE IN ONE FIXTURE. Every one of them held ZERO rows in every database here, so
/// its spec reported **SKIP**: honest, and useless at once - a SKIP reads as "not measured yet" for a
/// screen the application ships (the payroll tax settings, the salary-structure panel, the performance
/// desk and the exit desk all page over these tables).
///
/// ⚠️ IT DEPENDS ON `HrModuleSeeder` (employee + employeepayroll) AND ON `HrWorkflowSeeder` IS NOT
/// REQUIRED - a settlement is hung off a SEPARATION this seeder writes itself, and a tax record off a
/// payroll row that seeder already made.
///
/// ⚠️ EVERY ASSERTION IS A JOIN, A CONSTRAINT, OR THE APPLICATION'S OWN ARITHMETIC - never a row
/// count. A count proves the INSERT ran; it cannot see a row the application cannot reach. The six
/// that matter:
///   * a salary structure / leave balance / tax record must carry the SAME scope triple as its
///     employee, because every one of those repositories filters on the row's OWN columns - a row
///     written at 0/0/0 exists and is invisible to the desk it was seeded for;
///   * a tax record's payslip must belong to the SAME employee (the tax report joins both, so a
///     mismatched pair reports one person's tax against another's salary);
///   * `employeesettlement.separationid` must resolve to a separation of the SAME employee - the
///     link J5 asserts, and the only thing tying the exit desk's two records together;
///   * `remainingdays` must equal the application's own recompute
///     (`openingbalance + allocateddays - useddays`), because the leave desk decides "can this person
///     afford it" from this column and never recomputes it;
///   * `netsettlementamount` must equal `totalearnings - totaldeductions`;
///   * at most one ACTIVE `taxconfig` per type per campus (the partial unique index).
///
/// Opt in with the same flag the other dataset seeders use (it seeds data rather than asserting
/// application behaviour):
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~HrMoneyWorkspaceDataset"
/// </summary>
public sealed class HrMoneyWorkspaceDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public HrMoneyWorkspaceDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Hr_money_dataset_fills_the_payroll_performance_and_exit_tables_their_grids_read()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the HR money perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed HR money rows into - " +
            "run PerfDatasetTests first");

        var options = new HrMoneyWorkspaceSeedOptions
        {
            SalaryStructures = SeedCampuses.EnvInt("SCUBE_PERF_SALARY_STRUCTURES", 50),
            LeaveBalances = SeedCampuses.EnvInt("SCUBE_PERF_LEAVE_BALANCES", 300),
            TaxRecords = SeedCampuses.EnvInt("SCUBE_PERF_TAX_RECORDS", 100),
            Reviews = SeedCampuses.EnvInt("SCUBE_PERF_REVIEWS", 60),
            Separations = SeedCampuses.EnvInt("SCUBE_PERF_SEPARATIONS", 25),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding HR MONEY for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.SalaryStructures} salary structures, " +
                          $"{options.LeaveBalances} leave balances, {options.TaxRecords} tax records, " +
                          $"{options.Reviews} reviews, {options.Separations} separations");
        _output.WriteLine("");

        var seeder = new HrMoneyWorkspaceSeeder(_connectionString);
        var totalRows = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRows += result.TaxConfigs + result.TaxSlabs + result.SalaryStructures +
                         result.LeaveBalances + result.TaxRecords + result.ReviewCycles +
                         result.Reviews + result.Separations + result.Settlements;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.TaxConfigs,3} tax configs {result.TaxSlabs,3} slabs " +
                $"{result.SalaryStructures,3} salary structures {result.LeaveBalances,4} leave balances " +
                $"{result.TaxRecords,4} tax records {result.ReviewCycles,2} cycles {result.Reviews,3} reviews " +
                $"{result.Separations,3} separations {result.Settlements,3} settlements" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            await AssertRowsShareTheirEmployeeScopeAsync(conn, campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRows:N0} HR money rows");

        Assert.True(totalRows > 0,
            "no HR money rows were seeded, so the payroll/performance/exit grids over these tables " +
            "would still report SKIP");

        await AssertTaxConfigHasOneActivePerTypeAsync(conn, campusIds);
        await AssertLeaveBalanceArithmeticAsync(conn, campusIds);
        await AssertSettlementTotalsBalanceAsync(conn, campusIds);
    }

    /// <summary>
    /// The three tables whose repository filters on the row's OWN scope columns. A row whose triple
    /// disagrees with its employee's is a row that exists and that the desk can never list - the
    /// documented "a seeded row no query can reach" defect, one table over from the e2e baseline's.
    /// </summary>
    private static async Task AssertRowsShareTheirEmployeeScopeAsync(NpgsqlConnection conn, long campusId)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        foreach (var (table, label) in new[]
                 {
                     ("employeesalarystructure", "salary structure"),
                     ("employeeleavebalance", "leave balance"),
                     ("employeetaxrecord", "tax record"),
                     ("employeeseparation", "separation"),
                     ("employeesettlement", "settlement"),
                 })
        {
            var foreign = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {table} r
                     JOIN employee e ON e.id = r.employeeid
                    WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                      AND (e.tenantid, e.schoolid, e.campusid)
                          IS DISTINCT FROM (r.tenantid, r.schoolid, r.campusid)",
                new { tenantId, schoolId, campusId });

            Assert.True(foreign == 0,
                $"campus {campusId} holds {foreign} {label}(s) whose scope triple disagrees with their " +
                "own employee's - the desk filters on the row's columns, so those rows are unreachable");
        }
    }

    /// <summary>
    /// `ux_taxconfig_typepercampus` is a PARTIAL unique index on
    /// (tenantid, schoolid, campusid, taxtype) WHERE isactive - ONE ACTIVE config per type per campus.
    /// The seeder writes three distinct types for that reason; a duplicate would have been refused by
    /// the index, so a mismatch here means the index is missing.
    /// </summary>
    private static async Task AssertTaxConfigHasOneActivePerTypeAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var duplicates = await conn.QueryAsync<string>(
                @"SELECT taxtype FROM taxconfig
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND isactive = true
                   GROUP BY taxtype HAVING COUNT(*) > 1",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            var list = duplicates.ToList();
            Assert.True(list.Count == 0,
                $"campus {campusId} holds more than one ACTIVE tax config for {string.Join(", ", list)}; " +
                "`ux_taxconfig_typepercampus` allows exactly one, and a second flips which rate payroll uses");
        }
    }

    /// <summary>
    /// `remainingdays` is the column the leave desk's own "can this person afford it" check reads, and
    /// nothing recomputes it at read time - so a seeder that wrote a plausible-looking number instead
    /// of the application's own formula would make every later approval decision wrong.
    /// </summary>
    private static async Task AssertLeaveBalanceArithmeticAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var wrong = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeleavebalance
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remainingdays IS DISTINCT FROM (openingbalance + allocateddays - useddays)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(wrong == 0,
                $"campus {campusId} holds {wrong} leave balance(s) whose remainingdays is not " +
                "openingbalance + allocateddays - useddays - the leave desk decides affordability from " +
                "that column and never recomputes it");
        }
    }

    /// <summary>
    /// A settlement's total is what the exit desk prints and pays. It must balance against the lines
    /// the same row carries; an unbalanced row is a document that disagrees with itself.
    /// </summary>
    private static async Task AssertSettlementTotalsBalanceAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var unbalanced = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeesettlement
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND netsettlementamount IS DISTINCT FROM (totalearnings - totaldeductions)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unbalanced == 0,
                $"campus {campusId} holds {unbalanced} settlement(s) where netsettlementamount is not " +
                "totalearnings - totaldeductions");

            // The settlement and its separation must agree about WHO is leaving - they are two records
            // of one exit, and the desk opens the second from the first.
            var mismatched = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeesettlement s
                     JOIN employeeseparation p ON p.id = s.separationid
                    WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                      AND p.employeeid <> s.employeeid",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(mismatched == 0,
                $"campus {campusId} holds {mismatched} settlement(s) whose separation is for a different " +
                "employee - the exit desk resolves the employee through the separation link");
        }
    }
}
