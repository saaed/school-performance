using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="HrMoneyDetailSeeder"/>.</summary>
public sealed class HrMoneyDetailSeedOptions
{
    /// <summary>Goals per employee. Three is a term's worth and enough that the card feed scrolls.</summary>
    public int GoalsPerEmployee { get; set; } = 3;

    /// <summary>
    /// Employees that carry goals. The goal read is keyed on ONE employee, so this is how many
    /// employees can be pointed at - ten keeps the fixture quick while leaving the ids arbitrary.
    /// </summary>
    public int EmployeesWithGoals { get; set; } = 10;

    /// <summary>
    /// Payroll records that carry a detail line set. One line per active component is written for
    /// each, so this is the multiplier on the largest table in the batch.
    /// </summary>
    public int PayrollRecordsWithDetails { get; set; } = 200;

    /// <summary>
    /// One adjustment every N payroll records - an adjustment is an EXCEPTION (a bonus, a correction),
    /// not a line every payslip carries, so the honest shape is a sparse spread rather than one each.
    /// </summary>
    public int AdjustmentEveryNthPayroll { get; set; } = 10;

    /// <summary>Collected installments per loan - how much of each schedule has been repaid.</summary>
    public int PaidInstallmentsPerLoan { get; set; } = 3;

    /// <summary>Re-seed even when the campus already holds salary components.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's HR money/structure detail seed produced.</summary>
public sealed class HrMoneyDetailSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int SalaryComponents { get; set; }
    public int SalaryStructureDetails { get; set; }
    public int PerformanceKpis { get; set; }
    public int PerformanceDetails { get; set; }
    public int Recommendations { get; set; }
    public int Goals { get; set; }
    public int PayrollDetails { get; set; }
    public int PayrollAdjustments { get; set; }
    public int LoanPayments { get; set; }
}

/// <summary>
/// Seeds the HR money/structure DETAIL tables - `salarycomponent` + `employeesalarystructuredetail`,
/// `performancekpi` + `employeeperformancedetail` + `performancerecommendation` + `employeegoal`,
/// `employeepayrolldetail` + `payrolladjustment`, and `employeeloanpayment`.
///
/// WHY THIS EXISTS
/// ---------------
/// Nine tables held ZERO rows in every database here while their PARENT tables were already
/// populated (`employeesalarystructure` 50, `employeepayroll` 720, `employeeperformancereview` 60,
/// `employeeloan` 50) - so every read keyed on one of those parents returned an EMPTY LIST from a
/// fully configured campus. That is the worst version of the gap this tool exists to prevent: the
/// parent grid renders rows, the detail dialog opens, and it is blank. It also means the catalogue
/// could not have measured those reads at all - a spec over an empty table reports SKIP.
///
/// ⚠️ IT TOUCHES NO PARENT ROW. Every parent here (structures, payrolls, reviews, loans,
/// installments) belongs to a fixture that already ran, and the only parent column this seeder
/// WRITES is `employeeloaninstallment.Status` - because a payment that leaves its installment
/// Pending is a state the application itself cannot produce (`MarkInstallmentsPaidAsync` marks the
/// installment in the same step that writes the payment). That mutation is confined to the
/// installments it pays, and it is asserted in the fixture.
///
/// ⚠️ THE KPI WEIGHTS MUST TOTAL 100. `PerformanceService.StartReviewAsync` sums the campus's ACTIVE
/// KPI weights and refuses outside 95-105%, so a set of weights that does not add up makes "Start New
/// Review" refuse for every user of the campus - the same "a guard that can never open" defect the
/// event-finance and tax-rule fixtures each recorded once.
/// </summary>
public sealed class HrMoneyDetailSeeder : BaseSeeder
{
    public HrMoneyDetailSeeder(string connectionString) : base(connectionString) { }

    /// <summary>The tables a bulk detail load invalidates, so a fixture can ANALYZE them.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "salarycomponent", "employeesalarystructuredetail", "performancekpi",
        "employeeperformancedetail", "performancerecommendation", "employeegoal",
        "employeepayrolldetail", "payrolladjustment", "employeeloanpayment",
        "employeeloaninstallment"
    };

    /// <summary>
    /// The campus's salary components. `Basic Salary` is the one the percentage-based components are
    /// resolved against (`SalaryRepository.GetSalaryDetails` reads `d.ComponentName == "Basic Salary"`),
    /// so the name is load-bearing, not a label - a structure without it renders every percentage
    /// component as zero.
    /// </summary>
    private static readonly (string Name, string Code, string ComponentType, string CalculationType,
        decimal Amount, decimal? Percentage, bool IsEarning, bool IsDeduction, bool IsTaxable)[] Components =
    {
        ("Basic Salary", "BASIC", "Earning", "Fixed", 4000.00m, null, true, false, true),
        ("Housing Allowance", "HOUSE", "Earning", "Percentage", 1000.00m, 25.00m, true, false, false),
        ("Transport Allowance", "TRANS", "Earning", "Fixed", 400.00m, null, true, false, false),
        ("Medical Allowance", "MED", "Earning", "Fixed", 250.00m, null, true, false, false),
        ("Social Security", "GOSI", "Deduction", "Percentage", 0m, 5.00m, false, true, false),
        ("Income Tax", "TAX", "Deduction", "Percentage", 0m, 2.00m, false, true, false),
        ("Loan Deduction", "LOAN", "Deduction", "Fixed", 200.00m, null, false, true, false),
        ("Absence Deduction", "ABSENT", "Deduction", "Fixed", 150.00m, null, false, true, false),
    };

    /// <summary>
    /// The campus's KPIs. Weights total exactly 100 - see the class note. The names are what the
    /// review screen's detail table renders and the review's final rating is weighed by these, so
    /// they are the vocabulary a school actually reviews against rather than "KPI 1".
    /// </summary>
    private static readonly (string Name, decimal Weight, decimal MaximumScore)[] Kpis =
    {
        ("Teaching Quality", 25m, 10m),
        ("Student Outcomes", 25m, 10m),
        ("Attendance and Punctuality", 20m, 10m),
        ("Professional Development", 15m, 10m),
        ("Parent Communication", 15m, 10m),
    };

    private static readonly string[] RecommendationTypes =
    {
        "Promotion", "Training", "Increment", "Recognition",
    };

    private static readonly string[] GoalTitles =
    {
        "Complete the term's curriculum plan on schedule",
        "Raise the class average by five percentage points",
        "Attend two professional-development workshops",
        "Publish a learning-outcome assessment for every unit",
    };

    public async Task<HrMoneyDetailSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, HrMoneyDetailSeedOptions options, bool verbose = true)
    {
        var result = new HrMoneyDetailSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM salarycomponent
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // A skipped campus must still report what it HOLDS, not what this run wrote.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  HR money detail: campus {campusId} already holds {existing} salary component(s) - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // The PREREQUISITES, REPORTED rather than thrown.
        // ------------------------------------------------------------------
        var structures = (await conn.QueryAsync<long>(
            @"SELECT id FROM employeesalarystructure
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var reviews = (await conn.QueryAsync<long>(
            @"SELECT id FROM employeeperformancereview
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (structures.Count == 0 || reviews.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} holds {(structures.Count == 0 ? "no salary structure" : $"{structures.Count} structure(s)")} " +
                $"and {(reviews.Count == 0 ? "no performance review" : $"{reviews.Count} review(s)")} - this batch fills the " +
                "DETAIL tables of those two workspaces and touches no parent row, so it needs both to exist. " +
                "Run the HR workspace fixture first.";
            return result;
        }

        // ------------------------------------------------------------------
        // CLEAR, CHILDREN FIRST.
        // ------------------------------------------------------------------
        if (options.Force)
        {
            await ClearTableAsync(conn, "employeeperformancedetail", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "performancerecommendation", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "employeegoal", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "performancekpi", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "employeesalarystructuredetail", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "employeepayrolldetail", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "payrolladjustment", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "employeeloanpayment", tenantId, schoolId, campusId);
            // `salarycomponent` LAST of the money pair: both detail tables reference it.
            await ClearTableAsync(conn, "salarycomponent", tenantId, schoolId, campusId);
        }

        // ------------------------------------------------------------------
        // SALARY COMPONENTS + THE STRUCTURE LINES THAT USE THEM.
        // ------------------------------------------------------------------
        var componentIds = new List<(long Id, string Name, string ComponentType, string CalculationType, decimal Amount, decimal? Percentage)>();

        foreach (var (index, c) in Components.Select((c, i) => (i, c)))
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO salarycomponent
                      (name, code, componenttype, calculationtype, defaultamount, istaxable,
                       isearning, isdeduction, isactive, displayorder,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@name, @code, @componentType, @calculationType, @amount, @isTaxable,
                       @isEarning, @isDeduction, TRUE, @displayOrder,
                       @tenantId, @schoolId, @campusId, 0, 0, @now, @now)
                  RETURNING id",
                new
                {
                    name = c.Name,
                    code = c.Code,
                    componentType = c.ComponentType,
                    calculationType = c.CalculationType,
                    amount = (decimal?)c.Amount,
                    isTaxable = c.IsTaxable,
                    isEarning = c.IsEarning,
                    isDeduction = c.IsDeduction,
                    displayOrder = index + 1,
                    tenantId,
                    schoolId,
                    campusId,
                    now,
                });

            componentIds.Add((id, c.Name, c.ComponentType, c.CalculationType, c.Amount, c.Percentage));
        }

        result.SalaryComponents = componentIds.Count;

        // One line per component on every structure - that is what the structure editor lists and
        // what payroll's calculation consumes. Percentage components store their PERCENTAGE
        // (`SalaryRepository.GetSalaryDetails` resolves the amount from Basic Salary on read), and
        // the fixed ones store an amount; storing both would make the two disagree.
        var lineCount = 0;
        foreach (var structureId in structures)
        {
            foreach (var component in componentIds)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO employeesalarystructuredetail
                          (salarystructureid, salarycomponentid, amount, percentage, remarks,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@structureId, @componentId, @amount, @percentage, NULL,
                           @tenantId, @schoolId, @campusId, 0, 0, @now, @now)",
                    new
                    {
                        structureId,
                        componentId = component.Id,
                        amount = component.CalculationType == "Percentage" ? null : (decimal?)component.Amount,
                        percentage = component.CalculationType == "Percentage" ? component.Percentage : null,
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                lineCount++;
            }
        }

        result.SalaryStructureDetails = lineCount;

        // ------------------------------------------------------------------
        // KPIs + THE REVIEW LINES SCORED AGAINST THEM.
        // ------------------------------------------------------------------
        var kpiIds = new List<(long Id, decimal Weight, decimal MaximumScore)>();

        foreach (var kpi in Kpis)
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO performancekpi
                      (name, description, defaultweight, maximumscore, isactive,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@name, @description, @weight, @maximumScore, TRUE,
                       @tenantId, @schoolId, @campusId, 0, 0, @now, @now)
                  RETURNING id",
                new
                {
                    name = kpi.Name,
                    description = $"Perf dataset: {kpi.Name}",
                    weight = kpi.Weight,
                    maximumScore = kpi.MaximumScore,
                    tenantId,
                    schoolId,
                    campusId,
                    now,
                });

            kpiIds.Add((id, kpi.Weight, kpi.MaximumScore));
        }

        result.PerformanceKpis = kpiIds.Count;

        var detailCount = 0;
        foreach (var reviewId in reviews)
        {
            foreach (var kpi in kpiIds)
            {
                // A scored line: self and manager both out of `MaximumScore`, the final score the
                // mean of the two - the arithmetic `SubmitManagerReview` performs, so the stored row
                // and the screen agree (a line where they disagree is the defect J5 already recorded
                // once on the final rating).
                var self = Math.Round(kpi.MaximumScore * 0.8m, 2);
                var manager = Math.Round(kpi.MaximumScore * 0.9m, 2);
                var final = Math.Round((self + manager) / 2m, 2);

                await conn.ExecuteAsync(
                    @"INSERT INTO employeeperformancedetail
                          (employeeperformancereviewid, performancekpiid, selfscore, managerscore, finalscore,
                           comments, tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@reviewId, @kpiId, @self, @manager, @final,
                           NULL, @tenantId, @schoolId, @campusId, 0, 0, @now, @now)",
                    new { reviewId, kpiId = kpi.Id, self, manager, final, tenantId, schoolId, campusId, now });

                detailCount++;
            }
        }

        result.PerformanceDetails = detailCount;

        // ------------------------------------------------------------------
        // RECOMMENDATIONS - two per review, one APPROVED and one not, so the card feed's approved
        // badge has both states to render.
        // ------------------------------------------------------------------
        var approverUserId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT userid FROM employee
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND userid IS NOT NULL
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var recommendationCount = 0;
        foreach (var (reviewId, index) in reviews.Select((r, i) => (r, i)))
        {
            for (var i = 0; i < 2; i++)
            {
                var approved = i == 0;

                await conn.ExecuteAsync(
                    @"INSERT INTO performancerecommendation
                          (employeeperformancereviewid, recommendationtype, description, approved,
                           approvedby, approveddate, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@reviewId, @type, @description, @approved,
                           @approvedBy, @approvedDate, @tenantId, @schoolId, @campusId,
                           0, 0, @now, @now)",
                    new
                    {
                        reviewId,
                        type = RecommendationTypes[(index + i) % RecommendationTypes.Length],
                        description = approved
                            ? "Endorsed by the review panel."
                            : "Raised for the next review cycle.",
                        approved,
                        approvedBy = approved ? approverUserId : null,
                        approvedDate = approved ? now : (DateTime?)null,
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                recommendationCount++;
            }
        }

        result.Recommendations = recommendationCount;

        // ------------------------------------------------------------------
        // GOALS - per employee. The read is keyed on ONE employee, so a spread of employees is what
        // makes the spec's subject arbitrary rather than accidental.
        // ------------------------------------------------------------------
        var goalEmployeeIds = (await conn.QueryAsync<long>(
            @"SELECT DISTINCT employeeid FROM employeeperformancereview
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY employeeid
               LIMIT @limit",
            new { tenantId, schoolId, campusId, limit = options.EmployeesWithGoals })).ToList();

        var cycleId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM performancereviewcycle
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var goalStatuses = new[] { "NotStarted", "InProgress", "Completed" };
        var goalCount = 0;

        foreach (var employeeId in goalEmployeeIds)
        {
            for (var i = 0; i < options.GoalsPerEmployee; i++)
            {
                var status = goalStatuses[i % goalStatuses.Length];

                await conn.ExecuteAsync(
                    @"INSERT INTO employeegoal
                          (employeeid, performancereviewcycleid, title, description, targetdate, status,
                           progresspercentage, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@employeeId, @cycleId, @title, @description, @targetDate, @status,
                           @progress, @tenantId, @schoolId, @campusId,
                           0, 0, @now, @now)",
                    new
                    {
                        employeeId,
                        cycleId,
                        title = GoalTitles[i % GoalTitles.Length],
                        description = "Perf dataset goal",
                        targetDate = now.Date.AddMonths(3),
                        status,
                        progress = status switch { "Completed" => 100m, "InProgress" => 45m, _ => 0m },
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                goalCount++;
            }
        }

        result.Goals = goalCount;

        // ------------------------------------------------------------------
        // PAYROLL DETAILS + ADJUSTMENTS.
        //
        // ⚠️ THE DETAIL ROWS ARE SNAPSHOTS: `employeepayrolldetail` carries the component's NAME,
        // TYPE and CALCULATION TYPE beside its id, because a payslip must still render the line a
        // component was paid under after the component is renamed or deactivated. Writing only the
        // id would leave the payslip's own columns blank on a read that does not join the catalogue.
        // ------------------------------------------------------------------
        var payrollIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employeepayroll
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id
               LIMIT @limit",
            new { tenantId, schoolId, campusId, limit = options.PayrollRecordsWithDetails })).ToList();

        var payrollDetailCount = 0;
        var adjustmentCount = 0;

        foreach (var (payrollId, index) in payrollIds.Select((p, i) => (p, i)))
        {
            foreach (var component in componentIds)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO employeepayrolldetail
                          (employeepayrollid, salarycomponentid, componentname, componenttype, calculationtype,
                           amount, percentage, displayorder, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@payrollId, @componentId, @componentName, @componentType, @calculationType,
                           @amount, @percentage, @displayOrder, @tenantId, @schoolId, @campusId,
                           0, 0, @now, @now)",
                    new
                    {
                        payrollId,
                        componentId = component.Id,
                        componentName = component.Name,
                        componentType = component.ComponentType,
                        calculationType = component.CalculationType,
                        amount = component.CalculationType == "Percentage" ? 200.00m : component.Amount,
                        percentage = component.Percentage,
                        displayOrder = componentIds.IndexOf(component) + 1,
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                payrollDetailCount++;
            }

            // Sparse: an adjustment is an exception, and the adjustment read is keyed on ONE payroll
            // record, so the fixture's subject has to be a record that HAS one (the resolver below
            // is what guarantees that).
            if (options.AdjustmentEveryNthPayroll > 0 && index % options.AdjustmentEveryNthPayroll == 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO payrolladjustment
                          (employeepayrollid, adjustmenttype, amount, reason, approvedby,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@payrollId, @type, @amount, @reason, @approvedBy,
                           @tenantId, @schoolId, @campusId, 0, 0, @now, @now)",
                    new
                    {
                        payrollId,
                        type = index % 2 == 0 ? "Bonus" : "Deduction",
                        amount = index % 2 == 0 ? 500.00m : 120.00m,
                        reason = "Perf dataset adjustment",
                        approvedBy = approverUserId,
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                adjustmentCount++;
            }
        }

        result.PayrollDetails = payrollDetailCount;
        result.PayrollAdjustments = adjustmentCount;

        // ------------------------------------------------------------------
        // LOAN PAYMENTS - and the installments they settle.
        //
        // ⚠️ A PAYMENT AND ITS INSTALLMENT STATUS ARE ONE WRITE. `MarkInstallmentsPaidAsync` marks the
        // installment in the same step that records the payment, so a payment row whose installment is
        // still Pending is a state the application cannot produce - and it would also leave the loan's
        // outstanding balance disagreeing with its own schedule.
        // ------------------------------------------------------------------
        var loans = (await conn.QueryAsync<long>(
            @"SELECT id FROM employeeloan
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND status = 'Active'
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var paymentCount = 0;

        foreach (var loanId in loans)
        {
            var installments = (await conn.QueryAsync<InstallmentRow>(
                @"SELECT id, installmentno, duedate, principalamount, interestamount, totalamount, COALESCE(status,'') AS status
                    FROM employeeloaninstallment
                   WHERE employeeloanid = @loanId
                   ORDER BY installmentno
                   LIMIT @limit",
                new { loanId, limit = options.PaidInstallmentsPerLoan })).ToList();

            foreach (var installment in installments)
            {
                if (installment.Status == "Paid") continue;

                await conn.ExecuteAsync(
                    @"INSERT INTO employeeloanpayment
                          (employeeloanid, paymentdate, type, amount,
                           principalapplied, interestapplied, interestwaived, remarks,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@loanId, @paymentDate, @type, @amount,
                           @principal, @interest, 0, @remarks,
                           @tenantId, @schoolId, @campusId, 0, 0, @now, @now)",
                    new
                    {
                        loanId,
                        paymentDate = installment.DueDate.Date,
                        type = "Regular",
                        amount = installment.TotalAmount,
                        principal = installment.PrincipalAmount,
                        interest = installment.InterestAmount,
                        remarks = "Perf dataset collection",
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                // ⚠️ The column is `paidon` (timestamp), NOT `paiddate` - the schedule's own name.
                await conn.ExecuteAsync(
                    "UPDATE employeeloaninstallment SET status = 'Paid', paidon = @paidOn WHERE id = @id",
                    new { id = installment.Id, paidOn = installment.DueDate.Date });

                paymentCount++;
            }
        }

        result.LoanPayments = paymentCount;

        if (verbose)
        {
            Console.WriteLine(
                $"  HR money detail: campus {campusId} - {result.SalaryComponents} components, " +
                $"{result.SalaryStructureDetails} structure lines, {result.PerformanceKpis} KPIs, " +
                $"{result.PerformanceDetails} review lines, {result.Recommendations} recommendations, " +
                $"{result.Goals} goals, {result.PayrollDetails} payslip lines, " +
                $"{result.PayrollAdjustments} adjustments, {result.LoanPayments} loan payments");
        }

        return result;
    }

    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, HrMoneyDetailSeedResult result)
    {
        var counts = await conn.QueryFirstOrDefaultAsync<HrMoneyDetailCountsRow>(
            @"SELECT
                  (SELECT COUNT(*) FROM salarycomponent
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS SalaryComponents,
                  (SELECT COUNT(*) FROM employeesalarystructuredetail
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS SalaryStructureDetails,
                  (SELECT COUNT(*) FROM performancekpi
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS PerformanceKpis,
                  (SELECT COUNT(*) FROM employeeperformancedetail
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS PerformanceDetails,
                  (SELECT COUNT(*) FROM performancerecommendation
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS Recommendations,
                  (SELECT COUNT(*) FROM employeegoal
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS Goals,
                  (SELECT COUNT(*) FROM employeepayrolldetail
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS PayrollDetails,
                  (SELECT COUNT(*) FROM payrolladjustment
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS PayrollAdjustments,
                  (SELECT COUNT(*) FROM employeeloanpayment
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS LoanPayments",
            new { tenantId, schoolId, campusId });

        if (counts is null) return;

        result.SalaryComponents = counts.SalaryComponents;
        result.SalaryStructureDetails = counts.SalaryStructureDetails;
        result.PerformanceKpis = counts.PerformanceKpis;
        result.PerformanceDetails = counts.PerformanceDetails;
        result.Recommendations = counts.Recommendations;
        result.Goals = counts.Goals;
        result.PayrollDetails = counts.PayrollDetails;
        result.PayrollAdjustments = counts.PayrollAdjustments;
        result.LoanPayments = counts.LoanPayments;
    }

    private sealed class HrMoneyDetailCountsRow
    {
        public int SalaryComponents { get; set; }
        public int SalaryStructureDetails { get; set; }
        public int PerformanceKpis { get; set; }
        public int PerformanceDetails { get; set; }
        public int Recommendations { get; set; }
        public int Goals { get; set; }
        public int PayrollDetails { get; set; }
        public int PayrollAdjustments { get; set; }
        public int LoanPayments { get; set; }
    }

    private sealed class InstallmentRow
    {
        public long Id { get; set; }
        public int InstallmentNo { get; set; }
        public DateTime DueDate { get; set; }
        public decimal PrincipalAmount { get; set; }
        public decimal InterestAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string? Status { get; set; }
    }
}
