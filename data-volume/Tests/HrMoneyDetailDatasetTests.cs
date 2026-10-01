using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the HR money/structure DETAIL tables and then asserts the joins and guards that decide
/// whether their screens measure anything.
///
/// ⚠️ THE PARENTS WERE POPULATED AND THE CHILDREN WERE ZERO. `employeesalarystructure` held 50 rows,
/// `employeepayroll` 720, `employeeperformancereview` 60 and `employeeloan` 50, while
/// `salarycomponent`, `employeesalarystructuredetail`, `performancekpi`, `employeeperformancedetail`,
/// `performancerecommendation`, `employeegoal`, `employeepayrolldetail`, `payrolladjustment` and
/// `employeeloanpayment` all held NOTHING. So every read keyed on one of those parents returned an
/// EMPTY LIST from a fully configured campus - the parent grid renders rows and the detail dialog
/// opens blank - and the catalogue could not have measured those reads at all.
///
/// ⚠️ THE ASSERTIONS ARE THE QUERIES' OWN PREDICATES, NOT COUNTS:
///
///   * the structure-line read `LEFT JOIN`s `SalaryComponent` for the name/code/type it renders, and
///     `SalaryRepository.GetSalaryDetails` resolves every PERCENTAGE line against the amount stored on
///     the line whose component is named **"Basic Salary"** - so a structure without that component
///     renders every percentage line as zero;
///   * `PerformanceService.StartReviewAsync` sums the campus's ACTIVE KPI weights and refuses outside
///     95-105%, so a KPI set that does not total 100 leaves "Start New Review" permanently closed;
///   * the review-detail read `LEFT JOIN`s `PerformanceKPI` for the name and weight it orders by;
///   * the recommendation feed `INNER JOIN`s `EmployeePerformanceReview` and LEFT JOINs the cycle, so a
///     recommendation whose review is gone renders nowhere;
///   * a loan payment and its installment's status are ONE write (`MarkInstallmentsPaidAsync` marks the
///     installment as it records the payment), so a payment over a still-Pending installment is a state
///     the application cannot produce;
///   * `employeepayrolldetail` is a SNAPSHOT - it carries the component's name/type/calculation type
///     beside its id, because a payslip must still render the line after the catalogue row changes.
///
/// Opt in with the same flag the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~HrMoneyDetailDataset"
/// </summary>
public sealed class HrMoneyDetailDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public HrMoneyDetailDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Hr_money_detail_dataset_fills_every_child_read_of_the_payroll_and_performance_workspaces()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the HR money/structure detail dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed HR details into - " +
            "run PerfDatasetTests first");

        var options = new HrMoneyDetailSeedOptions
        {
            GoalsPerEmployee = SeedCampuses.EnvInt("SCUBE_PERF_HRDETAIL_GOALS", 3),
            EmployeesWithGoals = SeedCampuses.EnvInt("SCUBE_PERF_HRDETAIL_GOAL_EMPLOYEES", 10),
            PayrollRecordsWithDetails = SeedCampuses.EnvInt("SCUBE_PERF_HRDETAIL_PAYROLLS", 200),
            AdjustmentEveryNthPayroll = SeedCampuses.EnvInt("SCUBE_PERF_HRDETAIL_ADJ_EVERY", 10),
            PaidInstallmentsPerLoan = SeedCampuses.EnvInt("SCUBE_PERF_HRDETAIL_PAID_INSTALLMENTS", 3),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding HR MONEY/STRUCTURE DETAIL for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]");
        _output.WriteLine("");

        var seeder = new HrMoneyDetailSeeder(_connectionString);
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                if (result.SalaryComponents > 0) seededCampusIds.Add(campusId);
                continue;
            }

            _output.WriteLine(
                $"  campus {campusId,-5} {result.SalaryComponents,2} components {result.SalaryStructureDetails,4} structure lines " +
                $"{result.PerformanceKpis,3} KPIs {result.PerformanceDetails,4} review lines " +
                $"{result.Recommendations,4} recommendations {result.Goals,4} goals " +
                $"{result.PayrollDetails,5} payslip lines {result.PayrollAdjustments,4} adjustments " +
                $"{result.LoanPayments,4} loan payments");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");

        Assert.True(seededCampusIds.Count > 0,
            "no campus holds salary components - every campus was skipped for a missing prerequisite, so the " +
            "payroll and performance detail specs would still measure an empty table");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. These tables held ZERO rows, so the planner's statistics
        // describe an empty table. The list is the SEEDER's own declaration.
        foreach (var table in HrMoneyDetailSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertTheStructureLinesResolveTheirComponentAndBasicSalaryAsync(conn, seededCampusIds);
        await AssertTheKpiWeightsCanStillOpenAReviewAsync(conn, seededCampusIds);
        await AssertEveryPerformanceDetailResolvesItsKpiAsync(conn, seededCampusIds);
        await AssertEveryRecommendationResolvesItsReviewAsync(conn, seededCampusIds);
        await AssertEveryPayslipLineCarriesItsOwnSnapshotAsync(conn, seededCampusIds);
        await AssertEveryLoanPaymentSettlesItsInstallmentAsync(conn, seededCampusIds);
        await AssertNoSentinelTimestampsAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// ⚠️ THE PERCENTAGE LINES ARE RESOLVED FROM A COMPONENT *NAME*. `SalaryRepository.GetSalaryDetails`
    /// computes `basicSalary = results.Where(d => d.ComponentName == "Basic Salary").Sum(d => d.Amount ?? 0)`
    /// and then rewrites every `Percentage` line as `basicSalary * percentage / 100`. So the basic
    /// component must (a) exist, (b) be named exactly that, and (c) carry a NON-NULL amount on the line -
    /// a NULL amount makes the sum zero and every percentage line read as zero on a screen that looks
    /// fully configured.
    /// </summary>
    private static async Task AssertTheStructureLinesResolveTheirComponentAndBasicSalaryAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeesalarystructuredetail
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `employeesalarystructuredetail` row - the structure editor's own " +
                "component list opens empty for every structure on the campus");

            var nameless = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeesalarystructuredetail d
                   WHERE d.tenantid = @tenantId AND d.schoolid = @schoolId AND d.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM salarycomponent c WHERE c.id = d.salarycomponentid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(nameless == 0,
                $"campus {campusId} holds {nameless} structure line(s) whose `salarycomponentid` resolves to " +
                "nothing - `GetDetails`/`GetSalaryDetails` LEFT JOIN the catalogue for the name and code, so " +
                "these render a nameless line");

            // ⚠️ Every structure must carry a Basic Salary line with a NON-NULL amount, or the
            // percentage lines resolve against zero.
            var structuresWithoutBasic = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeesalarystructure s
                   WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM employeesalarystructuredetail d
                             JOIN salarycomponent c ON c.id = d.salarycomponentid
                            WHERE d.salarystructureid = s.id
                              AND c.name = 'Basic Salary' AND d.amount IS NOT NULL)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(structuresWithoutBasic == 0,
                $"campus {campusId} holds {structuresWithoutBasic} structure(s) with no `Basic Salary` line " +
                "carrying an amount - `GetSalaryDetails` resolves every Percentage line against `Basic Salary`, " +
                "so each of these renders its percentage components as ZERO");

            // A Percentage line must store its percentage (the amount is derived on read), while a
            // Fixed line must store an amount - storing neither makes the line worth nothing.
            var emptyLines = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeesalarystructuredetail d
                    JOIN salarycomponent c ON c.id = d.salarycomponentid
                   WHERE d.tenantid = @tenantId AND d.schoolid = @schoolId AND d.campusid = @campusId
                     AND ((c.calculationtype = 'Percentage' AND d.percentage IS NULL)
                       OR (c.calculationtype <> 'Percentage' AND d.amount IS NULL))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(emptyLines == 0,
                $"campus {campusId} holds {emptyLines} structure line(s) whose stored value does not match the " +
                "component's calculation type - a Percentage line must store its percentage and a Fixed line its " +
                "amount, and the read derives only the percentage case");
        }
    }

    /// <summary>
    /// ⚠️ A GUARD THAT CAN NEVER OPEN IS WORSE THAN NO GUARD.
    /// `PerformanceService.StartReviewAsync` sums the campus's ACTIVE KPI weights and refuses outside
    /// 95-105%, and `Date`/`PerformanceRepository.GetActiveKpis` is what that sum is taken from. A KPI
    /// set whose weights total 60 leaves "Start New Review" refusing for every user of the campus while
    /// its configuration screen looks correct.
    /// </summary>
    private static async Task AssertTheKpiWeightsCanStillOpenAReviewAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var active = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM performancekpi
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND isactive = TRUE",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(active > 0,
                $"campus {campusId} holds no ACTIVE `performancekpi` row - `StartReviewAsync` sums the active " +
                "weights and finds none, so no review can be started");

            var weightSum = await conn.ExecuteScalarAsync<decimal>(
                @"SELECT COALESCE(SUM(defaultweight), 0) FROM performancekpi
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND isactive = TRUE",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(weightSum is >= 95m and <= 105m,
                $"campus {campusId}'s ACTIVE KPI weights total {weightSum:0.##} - `StartReviewAsync` refuses " +
                "anything outside 95-105, so 'Start New Review' is permanently closed");
        }
    }

    /// <summary>
    /// ⚠️ THE REVIEW DETAIL READ ORDERS BY A JOINED COLUMN. `GetReviewDetails` LEFT JOINs
    /// `PerformanceKPI` for `KpiName`, `DefaultWeight` and `MaximumScore`, and sorts by
    /// `k.DefaultWeight DESC` - so a line whose KPI does not resolve renders a blank name, a NULL
    /// weight and sorts unpredictably.
    /// </summary>
    private static async Task AssertEveryPerformanceDetailResolvesItsKpiAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeperformancedetail
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `employeeperformancedetail` row - the review's score table " +
                "(`GetReviewDetails`) opens empty for every review");

            var kpiLess = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeperformancedetail d
                   WHERE d.tenantid = @tenantId AND d.schoolid = @schoolId AND d.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM performancekpi k WHERE k.id = d.performancekpiid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(kpiLess == 0,
                $"campus {campusId} holds {kpiLess} review line(s) whose `performancekpiid` resolves to nothing - " +
                "`GetReviewDetails` renders a blank KPI name and orders them by a NULL weight");

            // The arithmetic the engine performs: the stored final score is the mean of the two
            // assessments. A line where they disagree is the defect J5 already recorded once.
            var mismatched = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeperformancedetail
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND selfscore IS NOT NULL AND managerscore IS NOT NULL AND finalscore IS NOT NULL
                     AND ROUND((selfscore + managerscore) / 2, 2) <> finalscore",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(mismatched == 0,
                $"campus {campusId} holds {mismatched} review line(s) whose `finalscore` is not the mean of its " +
                "self and manager scores - `SubmitManagerReview` stores the mean, so the seeded row contradicts " +
                "the engine");
        }
    }

    /// <summary>
    /// ⚠️ THE FEED JOINS THE REVIEW. `GetEmployeeRecommendations` INNER JOINs
    /// `EmployeePerformanceReview` (and LEFT JOINs the cycle for `ReviewCycleName`), so a
    /// recommendation whose review does not exist renders NOWHERE - the worst kind of row, because
    /// `GetRecommendations(reviewId)` would still count it for the review it names.
    /// </summary>
    private static async Task AssertEveryRecommendationResolvesItsReviewAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM performancerecommendation
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `performancerecommendation` row - the review's recommendation list " +
                "and the employee card feed both render nothing");

            var orphaned = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM performancerecommendation r
                   WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM employeeperformancereview v WHERE v.id = r.employeeperformancereviewid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphaned == 0,
                $"campus {campusId} holds {orphaned} recommendation(s) whose review does not exist - the " +
                "employee feed INNER JOINs the review, so these render nowhere");

            // The approved badge needs both states to exist, or the branch that renders it is untested.
            var approvedStates = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT approved) FROM performancerecommendation
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(approvedStates >= 2,
                $"campus {campusId}'s recommendations carry only {approvedStates} distinct `approved` value(s) - " +
                "the card feed renders an approval badge, so the other state is correct and unexercised");
        }
    }

    /// <summary>
    /// ⚠️ THE PAYSLIP LINE IS A SNAPSHOT, NOT A FOREIGN KEY. `employeepayrolldetail` carries
    /// `componentname`/`componenttype`/`calculationtype` beside `salarycomponentid` precisely so a payslip
    /// still renders the line a component was paid under after the catalogue row is renamed or
    /// deactivated. Writing only the id leaves those columns blank on any read that does not join the
    /// catalogue - and the payslip is a document, so a blank line is a wrong payslip.
    /// </summary>
    private static async Task AssertEveryPayslipLineCarriesItsOwnSnapshotAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeepayrolldetail
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `employeepayrolldetail` row - `GetPayrollDetails` returns nothing, " +
                "so every payslip's earning/deduction breakdown is empty");

            var blankSnapshot = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeepayrolldetail
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND (componentname IS NULL OR componenttype IS NULL OR calculationtype IS NULL)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(blankSnapshot == 0,
                $"campus {campusId} holds {blankSnapshot} payslip line(s) with a blank component snapshot - the " +
                "line must render on the payslip even when the catalogue row is renamed or deactivated");

            var danglingComponent = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeepayrolldetail d
                   WHERE d.tenantid = @tenantId AND d.schoolid = @schoolId AND d.campusid = @campusId
                     AND d.salarycomponentid IS NOT NULL
                     AND NOT EXISTS (SELECT 1 FROM salarycomponent c WHERE c.id = d.salarycomponentid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(danglingComponent == 0,
                $"campus {campusId} holds {danglingComponent} payslip line(s) whose `salarycomponentid` points at " +
                "nothing - the snapshot keeps the line readable but the structure total no longer reconciles");

            // ⚠️ THE ADJUSTMENT READ IS KEYED ON ONE PAYROLL RECORD, AND IT IS SPARSE BY DESIGN. The
            // resolver hands the spec the record that HAS one; if no record does, that spec measures an
            // empty result and reports OK for the wrong reason.
            var payrollsWithAdjustment = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT employeepayrollid) FROM payrolladjustment
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(payrollsWithAdjustment > 0,
                $"campus {campusId} holds no payroll record with an adjustment - `GetAdjustments(payrollId)` " +
                "returns nothing for every record the campus owns");
        }
    }

    /// <summary>
    /// ⚠️ A PAYMENT AND ITS INSTALLMENT STATUS ARE ONE WRITE. `MarkInstallmentsPaidAsync` marks the
    /// installment in the same step that records the payment, so a payment whose installment is still
    /// `Pending` is a state the application cannot produce - and it would leave the loan's outstanding
    /// balance disagreeing with its own schedule, which the payroll collection reads.
    /// </summary>
    private static async Task AssertEveryLoanPaymentSettlesItsInstallmentAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloanpayment
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `employeeloanpayment` row - `LoanRepository.GetPayments(loanId)` " +
                "returns nothing, so a partly-repaid loan's payment history is empty");

            var unscheduled = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloanpayment p
                   WHERE p.tenantid = @tenantId AND p.schoolid = @schoolId AND p.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM employeeloan l WHERE l.id = p.employeeloanid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unscheduled == 0,
                $"campus {campusId} holds {unscheduled} loan payment(s) whose loan does not exist");

            // The paid count must match: every payment has a Paid installment, and the Paid rows must
            // not outnumber the payments (a schedule marked paid without a collection is the mirror of
            // the defect).
            var paid = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloaninstallment
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status = 'Paid'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(paid == total,
                $"campus {campusId} holds {total} loan payment(s) but {paid} PAID installment(s) - " +
                "`MarkInstallmentsPaidAsync` writes the two together, so any difference is a state the " +
                "application cannot produce");

            // A payment must be worth its installment's total, or the loan's outstanding balance and
            // its schedule no longer reconcile.
            var wrongAmount = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM employeeloanpayment p
                    JOIN employeeloaninstallment i
                      ON i.employeeloanid = p.employeeloanid AND i.duedate = p.paymentdate
                   WHERE p.tenantid = @tenantId AND p.schoolid = @schoolId AND p.campusid = @campusId
                     AND i.totalamount <> p.amount",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(wrongAmount == 0,
                $"campus {campusId} holds {wrongAmount} loan payment(s) whose amount disagrees with the " +
                "installment due on that date");
        }
    }

    /// <summary>
    /// The `-infinity` sentinel ties every `ORDER BY CreatedOn` and makes "the newest row" arbitrary.
    /// A hand-written seeding path is exactly where it comes back.
    /// </summary>
    private static async Task AssertNoSentinelTimestampsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var bad = await conn.ExecuteScalarAsync<long>(
                @"SELECT
                    (SELECT COUNT(*) FROM salarycomponent
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM performancekpi
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM employeepayrolldetail
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM employeeloanpayment
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(bad == 0,
                $"campus {campusId} holds {bad} row(s) stamped `-infinity` - the sentinel that ties every " +
                "`ORDER BY CreatedOn` and makes the newest row arbitrary");
        }
    }
}
