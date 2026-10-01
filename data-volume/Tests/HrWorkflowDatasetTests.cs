using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the HR WORKFLOW transactions a school accumulates - loans + their installment schedule,
/// overtime, contracts, employee documents, reporting lines and attendance corrections - so their
/// grid endpoints measure instead of reporting SKIP.
///
/// ⚠️ WHY THESE AND NOT THE HR SPINE. `HrModuleSeeder` already fills the VOLUME tables
/// (`employee`, `employeeattendance`, `employeepayroll`) - that is what the paged HR grids scan.
/// Every table here held ZERO rows in every database on this machine, so each of their grids could
/// only report **SKIP**: honest, and useless at once, because a SKIP reads as "not measured yet"
/// for a screen the application ships. This fixture is the DATA half of that coverage, and it is
/// the reason the specs over these tables can carry a low `MinVolume` gate.
///
/// ⚠️ IT DEPENDS ON `HrModuleSeeder`, AND THAT IS A PRECONDITION RATHER THAN A CONVENIENCE. Each
/// row here hangs off a real `employee` of the campus (the desks all JOIN it), so a seeder that
/// invented an employee id would write rows that exist and are unreachable - the documented
/// "a seeded row no query can reach" defect. `HrWorkflowSeeder` throws when the campus holds no
/// active employee rather than falling back to a placeholder.
///
/// ⚠️ EVERY ASSERTION IS A JOIN, A CONSTRAINT OR A CARDINALITY - NOT A ROW COUNT. A count proves
/// the INSERT ran; it cannot see a row the application cannot reach. The four that matter:
///   * every `employeeloaninstallment` resolves to a loan of the SAME campus, and the loan's
///     employee is on that campus - the payroll collects from these rows by enrolling them in a
///     period, so an orphan schedule is money nobody can pay.
///   * every `employeereporting` row names a DIFFERENT person as the manager - self-management is
///     the rule the hierarchy screen refuses by name, and an open line per employee is enforced by
///     the partial unique `ux_employeereporting_oneactive`.
///   * every `employeedocument.documenttypeid` / `employmentcontract.contracttypeid` resolves, and
///     `employeedocumenttype` is NOT NULL so there is no honest fallback there.
///   * the overtime rows satisfy `uq_employeeovertime_employee_date` - one row per employee per day.
///
/// Opt in with the same flag the other dataset seeders use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~HrWorkflowDataset"
/// </summary>
public sealed class HrWorkflowDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public HrWorkflowDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Hr_workflow_dataset_fills_the_transaction_tables_their_desks_page_over()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the HR-workflow perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed HR workflows into - " +
            "run PerfDatasetTests first");

        var options = new HrWorkflowSeedOptions
        {
            Loans = SeedCampuses.EnvInt("SCUBE_PERF_HR_LOANS", 50),
            OvertimeRows = SeedCampuses.EnvInt("SCUBE_PERF_HR_OVERTIME", 60),
            Contracts = SeedCampuses.EnvInt("SCUBE_PERF_HR_CONTRACTS", 50),
            Documents = SeedCampuses.EnvInt("SCUBE_PERF_HR_DOCUMENTS", 100),
            ReportingLines = SeedCampuses.EnvInt("SCUBE_PERF_HR_REPORTING", 50),
            Corrections = SeedCampuses.EnvInt("SCUBE_PERF_HR_CORRECTIONS", 40),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding HR WORKFLOW for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.Loans} loans, " +
                          $"{options.OvertimeRows} overtime rows, {options.Contracts} contracts, " +
                          $"{options.Documents} documents, {options.ReportingLines} reporting lines, " +
                          $"{options.Corrections} corrections");
        _output.WriteLine("");

        var seeder = new HrWorkflowSeeder(_connectionString);
        var totalRows = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRows += result.Loans + result.LoanInstallments + result.Overtimes + result.Contracts +
                         result.Documents + result.ReportingLines + result.Corrections;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Loans,4} loans {result.LoanInstallments,6} installments " +
                $"{result.Overtimes,4} overtime {result.Contracts,4} contracts {result.Documents,4} documents " +
                $"{result.ReportingLines,4} reporting lines {result.Corrections,4} corrections " +
                $"{result.PerformanceScaleLevels,2} scale levels" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            await AssertCampusRowsAreReachableAsync(conn, campusId, result);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRows:N0} HR-workflow rows");

        Assert.True(totalRows > 0,
            "no HR workflow was seeded, so the loan/overtime/contract/document/reporting/correction " +
            "grids would still report SKIP");

        await AssertNoEmployeeHoldsTwoOpenReportingLinesAsync(conn);
        await AssertOvertimeIsOneRowPerEmployeePerDayAsync(conn);
    }

    /// <summary>
    /// The joins each seeded row has to satisfy for the APPLICATION to see it. Every desk over these
    /// tables is scope-filtered and most JOIN the employee, so a row whose employee or parent does
    /// not resolve is a row that exists and is invisible - populated-looking data on an empty screen.
    /// </summary>
    private async Task AssertCampusRowsAreReachableAsync(
        NpgsqlConnection conn, long campusId, HrWorkflowSeedResult result)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        if (result.Loans > 0)
        {
            var orphanLoans = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloan l
                    WHERE l.tenantid = @tenantId AND l.schoolid = @schoolId AND l.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employee e
                           WHERE e.id = l.employeeid AND e.tenantid = @tenantId
                             AND e.schoolid = @schoolId AND e.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanLoans == 0,
                $"campus {campusId} has {orphanLoans} loan(s) whose employee is not on the campus - " +
                "the loan grid joins the employee, so those rows would be listed with a blank person");

            // The payroll collects an installment by enrolling it in a period, so a schedule that
            // hangs off another campus's loan is money nobody can pay.
            var orphanInstallments = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloaninstallment i
                    WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employeeloan l
                           WHERE l.id = i.employeeloanid AND l.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanInstallments == 0,
                $"campus {campusId} has {orphanInstallments} installment(s) that do not resolve to a loan " +
                "of the same campus");

            // ⚠️ Each loan must carry the WHOLE schedule its own `termmonths` promises. A loan with 3
            // installments against a 12-month term is a payroll that stops paying after a quarter -
            // and a count assertion over `employeeLoan` alone cannot see it.
            var shortSchedules = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloan l
                    WHERE l.tenantid = @tenantId AND l.schoolid = @schoolId AND l.campusid = @campusId
                      AND (SELECT COUNT(*) FROM employeeloaninstallment i WHERE i.employeeloanid = l.id)
                          <> l.termmonths",
                new { tenantId, schoolId, campusId });

            Assert.True(shortSchedules == 0,
                $"campus {campusId} has {shortSchedules} loan(s) whose installment count does not match " +
                "their own termmonths - the payroll would stop collecting early");
        }

        if (result.Overtimes > 0)
        {
            var orphanOvertime = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeovertime o
                    WHERE o.tenantid = @tenantId AND o.schoolid = @schoolId AND o.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employee e
                           WHERE e.id = o.employeeid AND e.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanOvertime == 0,
                $"campus {campusId} has {orphanOvertime} overtime row(s) whose employee is not on the campus");
        }

        if (result.Contracts > 0)
        {
            var orphanContracts = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employmentcontract c
                    WHERE c.tenantid = @tenantId AND c.schoolid = @schoolId AND c.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employee e
                           WHERE e.id = c.employeeid AND e.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanContracts == 0,
                $"campus {campusId} has {orphanContracts} contract(s) whose employee is not on the campus");

            var badTypes = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employmentcontract c
                    WHERE c.tenantid = @tenantId AND c.schoolid = @schoolId AND c.campusid = @campusId
                      AND c.contracttypeid IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM contracttype t
                           WHERE t.id = c.contracttypeid AND t.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(badTypes == 0,
                $"campus {campusId} has {badTypes} contract(s) whose contract type does not resolve " +
                "within the campus");
        }

        if (result.Documents > 0)
        {
            // `employeedocument.documenttypeid` is NOT NULL, so an unresolvable type is not a blank
            // column - it is a row the document desk cannot render at all.
            var badTypes = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeedocument d
                    WHERE d.tenantid = @tenantId AND d.schoolid = @schoolId AND d.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employeedocumenttype t
                           WHERE t.id = d.documenttypeid AND t.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(badTypes == 0,
                $"campus {campusId} has {badTypes} document(s) whose document type does not resolve " +
                "within the campus");

            var orphanDocuments = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeedocument d
                    WHERE d.tenantid = @tenantId AND d.schoolid = @schoolId AND d.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employee e
                           WHERE e.id = d.employeeid AND e.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanDocuments == 0,
                $"campus {campusId} has {orphanDocuments} document(s) whose employee is not on the campus");
        }

        if (result.ReportingLines > 0)
        {
            // The hierarchy screen refuses self-management by name, and the approval engine's
            // `AssignToManager` step resolves through these rows - a person managing themselves is a
            // chain that can never terminate.
            var selfManaged = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeereporting r
                    WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                      AND r.employeeid = r.manageremployeeid",
                new { tenantId, schoolId, campusId });

            Assert.True(selfManaged == 0,
                $"campus {campusId} has {selfManaged} reporting line(s) where the employee manages " +
                "themselves - the hierarchy screen refuses this by rule");

            var orphanLines = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeereporting r
                    WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                      AND (NOT EXISTS (SELECT 1 FROM employee e WHERE e.id = r.employeeid AND e.campusid = @campusId)
                        OR NOT EXISTS (SELECT 1 FROM employee m WHERE m.id = r.manageremployeeid AND m.campusid = @campusId))",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanLines == 0,
                $"campus {campusId} has {orphanLines} reporting line(s) naming an employee or manager " +
                "who is not on the campus");
        }

        if (result.Corrections > 0)
        {
            var orphanCorrections = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM attendancecorrectionrequest c
                    WHERE c.tenantid = @tenantId AND c.schoolid = @schoolId AND c.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM employee e
                           WHERE e.id = c.employeeid AND e.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanCorrections == 0,
                $"campus {campusId} has {orphanCorrections} correction request(s) whose employee is not " +
                "on the campus");
        }

        if (result.PerformanceScales > 0)
        {
            // A scale with no levels is a configuration the review screen cannot score against -
            // `performancescalelevel` is what the dropdown is fed from, and it has no scope columns
            // of its own, so it is reached through its parent.
            var emptyScales = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM performancescale s
                    WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM performancescalelevel l WHERE l.performancescaleid = s.id)",
                new { tenantId, schoolId, campusId });

            Assert.True(emptyScales == 0,
                $"campus {campusId} has {emptyScales} performance scale(s) with no levels, so a review " +
                "could not be scored against them");
        }
    }

    /// <summary>
    /// `ux_employeereporting_oneactive` is a PARTIAL unique index on (employeeid) WHERE
    /// effectiveto IS NULL AND isprimary = true. A second open line for one person is refused by the
    /// index rather than by a guard, so a seeder that cycled its employee list would fail with a
    /// 23505 - and the invariant is what the approval engine's manager resolution depends on.
    /// </summary>
    private static async Task AssertNoEmployeeHoldsTwoOpenReportingLinesAsync(NpgsqlConnection conn)
    {
        var duplicates = await conn.QueryAsync<long>(
            @"SELECT employeeid FROM employeereporting
               WHERE effectiveto IS NULL AND isprimary = true
            GROUP BY employeeid HAVING COUNT(*) > 1");

        var list = duplicates.ToList();
        Assert.True(list.Count == 0,
            $"ux_employeereporting_oneactive is a PARTIAL unique index but {list.Count} employee(s) hold " +
            $"more than one OPEN primary reporting line: {string.Join(", ", list)}");
    }

    /// <summary>
    /// `uq_employeeovertime_employee_date` is UNIQUE (employeeid, attendancedate) - what stops a day
    /// being counted twice. The seeder cycles the DAY last so the pair stays unique for any employee
    /// pool size, which a `i % 30` date loop does not (it collides as soon as the pool is smaller
    /// than 30).
    /// </summary>
    private static async Task AssertOvertimeIsOneRowPerEmployeePerDayAsync(NpgsqlConnection conn)
    {
        var duplicates = await conn.QueryAsync<string>(
            @"SELECT employeeid || '@' || attendancedate FROM employeeovertime
            GROUP BY employeeid, attendancedate HAVING COUNT(*) > 1");

        var list = duplicates.ToList();
        Assert.True(list.Count == 0,
            $"uq_employeeovertime_employee_date is UNIQUE (employeeid, attendancedate) but {list.Count} " +
            $"employee/day pair(s) carry more than one row: {string.Join(", ", list.Take(10))}");
    }
}
