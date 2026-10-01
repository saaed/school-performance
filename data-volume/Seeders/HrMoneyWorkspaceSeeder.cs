using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="HrMoneyWorkspaceSeeder"/>. One campus gets a normal set of each.</summary>
public sealed class HrMoneyWorkspaceSeedOptions
{
    /// <summary>Employees that get a current salary structure.</summary>
    public int SalaryStructures { get; set; } = 50;

    /// <summary>Leave-balance rows (employee x leave type pairs - kept unique by construction).</summary>
    public int LeaveBalances { get; set; } = 300;

    /// <summary>Per-payslip tax records, drawn from the campus's own payroll rows.</summary>
    public int TaxRecords { get; set; } = 100;

    /// <summary>Performance reviews in the seeded cycle.</summary>
    public int Reviews { get; set; } = 60;

    /// <summary>Separations, each with its settlement.</summary>
    public int Separations { get; set; } = 25;

    public bool Force { get; set; }
}

/// <summary>What one campus's HR-money seed produced.</summary>
public sealed class HrMoneyWorkspaceSeedResult
{
    public bool Skipped { get; set; }
    public int TaxConfigs { get; set; }
    public int TaxSlabs { get; set; }
    public int SalaryStructures { get; set; }
    public int LeaveBalances { get; set; }
    public int TaxRecords { get; set; }
    public int ReviewCycles { get; set; }
    public int Reviews { get; set; }
    public int Separations { get; set; }
    public int Settlements { get; set; }
}

/// <summary>
/// Seeds the HR MONEY / LIFECYCLE workspace for one campus: the payroll tax configuration and its
/// slabs, salary structures, leave balances, per-payslip tax records, the performance review cycle
/// and its reviews, and the exit chain (separation + settlement).
///
/// WHY THIS EXISTS
/// ---------------
/// These are the tables the payroll, performance and exit DESKS page over, and every one of them
/// held ZERO rows in every database here - so their grid specs could only report **SKIP**: honest,
/// and useless at once, because a SKIP reads as "not measured yet" for a screen the application
/// ships. `HrModuleSeeder` filled the volume tables (`employee`, `employeeattendance`,
/// `employeepayroll`); these are the reference and transaction rows ABOVE that spine.
///
/// ⚠️ IT DEPENDS ON `HrModuleSeeder` AND, FOR THE TAX RECORDS, ON ITS `employeepayroll` ROWS. A tax
/// record hangs off a real payslip (`employeetaxrecord.employeepayrollid` is NOT NULL), so this
/// reads the campus's own payroll rows rather than inventing one - the same rule as every other
/// seeder here, and the reason a table can look populated while the screen stays empty.
///
/// ⚠️ THREE DATABASE RULES SHAPE THE ROW SET, and each is noted where it bites:
///   * `uix_elb_employeeleavetype` is UNIQUE (employeeid, leavetypeid, COALESCE(year, 0));
///   * `uqx_employeeperformancereview` is UNIQUE (employeeid, cycleid);
///   * `ux_taxconfig_typepercampus` is a PARTIAL unique on (tenant, school, campus, taxtype) WHERE
///     isactive - ONE ACTIVE config per type per campus.
///
/// IDEMPOTENT: a campus that already holds separations is skipped unless Force is set (and a skipped
/// campus still REPORTS what it holds, so a fixture's own assertion is true on a re-run).
/// </summary>
public sealed class HrMoneyWorkspaceSeeder : BaseSeeder
{
    public HrMoneyWorkspaceSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables an HR-money load invalidates statistics for.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "taxconfig", "taxslab", "employeesalarystructure", "employeeleavebalance",
        "employeetaxrecord", "performancereviewcycle", "employeeperformancereview",
        "employeeseparation", "employeesettlement"
    };

    /// <summary>The exit chain is what this dataset exists for, so it is the skip marker.</summary>
    private const string SkipMarkerTable = "employeeseparation";

    /// <summary>One active config per type - the partial unique index allows exactly that.</summary>
    private static readonly (string Type, string Method, string BorneBy)[] TaxConfigs =
    {
        ("IncomeTax", "Progressive", "Employee"),
        ("SocialSecurity", "Flat", "Shared"),
        ("GOSI", "Flat", "Shared"),
    };

    /// <summary>The four types `hr.settlement.html` offers - read off the page, not invented.</summary>
    private static readonly string[] SeparationTypes =
        { "Resignation", "Termination", "Retirement", "EndOfContract" };

    public async Task<HrMoneyWorkspaceSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, HrMoneyWorkspaceSeedOptions options, bool verbose = true)
    {
        var result = new HrMoneyWorkspaceSeedResult();
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
                Console.WriteLine($"  HR money: campus {campusId} already holds {existing:N0} separations - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        var employeeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employee
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND isactive = true
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (employeeIds.Count == 0)
            throw new InvalidOperationException(
                $"campus {campusId} holds no active employee, so no HR money row could reference one - "
                + "run HrModuleSeeder first.");

        // Leave types are the campus's own catalogue (the e2e baseline seeds eight).
        var leaveTypeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM leavetype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (options.Force && existing > 0)
        {
            // Children before parents, and the two settlement children (`taxslab`) before the config.
            foreach (var table in new[]
                     {
                         "employeesettlement", "employeeseparation", "employeeperformancereview",
                         "performancereviewcycle", "employeetaxrecord", "employeeleavebalance",
                         "employeesalarystructure", "taxslab", "taxconfig"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose) Console.WriteLine($"  HR money: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. Tax configuration + its progressive slabs.
        //
        // ⚠️ `ux_taxconfig_typepercampus` is a PARTIAL unique on
        // (tenantid, schoolid, campusid, taxtype) WHERE isactive - ONE ACTIVE config per type. Three
        // distinct types is the whole allowance, so this loop cannot grow.
        // ------------------------------------------------------------------
        long? incomeTaxConfigId = null;

        foreach (var (type, method, borneBy) in TaxConfigs)
        {
            var configId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO taxconfig
                      (tenantid, schoolid, campusid, name, countrycode, taxtype, calculationmethod,
                       flatrate, exemptamount, employerrate, employeerate, maxcontributionbase,
                       mincontributionbase, borneby, isactive, displayorder,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, 'AE', @type, @method,
                          @flatRate, 0, @employerRate, @employeeRate, NULL,
                          0, @borneBy, true, @order,
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId,
                    name = $"Perf {type} Config",
                    type, method,
                    flatRate = method == "Flat" ? 5m : 0m,
                    employerRate = borneBy == "Employee" ? 0m : 5m,
                    employeeRate = borneBy == "Employer" ? 0m : 5m,
                    borneBy,
                    order = result.TaxConfigs + 1,
                    now
                });

            if (type == "IncomeTax") incomeTaxConfigId = configId;
            result.TaxConfigs++;
        }

        // The slabs a PROGRESSIVE income-tax config is made of - one row per bracket.
        if (incomeTaxConfigId.HasValue)
        {
            var bands = new (decimal From, decimal? To, decimal Rate, decimal Flat)[]
            {
                (0m, 50_000m, 0m, 0m),
                (50_000m, 250_000m, 5m, 0m),
                (250_000m, null, 15m, 0m),
            };

            for (var i = 0; i < bands.Length; i++)
            {
                var (from, to, rate, flat) = bands[i];
                await conn.ExecuteAsync(
                    @"INSERT INTO taxslab
                          (tenantid, schoolid, campusid, taxconfigid, fromamount, toamount,
                           rate, flatdeduction, displayorder,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @configId, @from, @to,
                              @rate, @flat, @order, 1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId, campusId, configId = incomeTaxConfigId.Value,
                        from, to, rate, flat, order = i + 1, now
                    });
                result.TaxSlabs++;
            }
        }

        // ------------------------------------------------------------------
        // 2. Salary structures - one CURRENT row per employee, which is what
        //    `EmployeeSalaryStructureRepository.GetCurrentByEmployee` resolves.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.SalaryStructures && i < employeeIds.Count; i++)
        {
            var effectiveFrom = DateTime.Today.AddYears(-1).AddMonths(-(i % 12));

            await conn.ExecuteAsync(
                @"INSERT INTO employeesalarystructure
                      (tenantid, schoolid, campusid, employeeid, employmentcontractid, effectivefrom,
                       effectiveto, iscurrent, remarks,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, NULL, @effectiveFrom::date,
                          NULL, true, 'Perf seed structure', 1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, employeeId = employeeIds[i], effectiveFrom, now });
            result.SalaryStructures++;
        }

        // ------------------------------------------------------------------
        // 3. Leave balances.
        //
        // ⚠️ `uix_elb_employeeleavetype` is UNIQUE (employeeid, leavetypeid, COALESCE(year, 0)).
        // Cycling the EMPLOYEE fastest and the LEAVE TYPE second is what keeps every pair distinct
        // for any row count up to employees x leave types - an `i % leaveTypes` loop would collide
        // on the second lap and answer a 23505.
        // ------------------------------------------------------------------
        if (leaveTypeIds.Count > 0)
        {
            var capacity = employeeIds.Count * leaveTypeIds.Count;
            var wanted = Math.Min(options.LeaveBalances, capacity);

            for (var i = 0; i < wanted; i++)
            {
                var employeeId = employeeIds[i % employeeIds.Count];
                var leaveTypeId = leaveTypeIds[(i / employeeIds.Count) % leaveTypeIds.Count];
                const decimal allocated = 30m;
                var used = i % 5;

                await conn.ExecuteAsync(
                    @"INSERT INTO employeeleavebalance
                          (tenantid, schoolid, campusid, employeeid, leavetypeid, academicyearid,
                           openingbalance, allocateddays, useddays, remainingdays,
                           createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @leaveTypeId, NULL,
                          0, @allocated, @used, @remaining, 1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId, campusId, employeeId, leaveTypeId,
                        allocated, used, remaining = allocated - used, now
                    });
                result.LeaveBalances++;
            }
        }

        // ------------------------------------------------------------------
        // 4. Per-payslip tax records. Read off the campus's OWN payroll rows - `employeepayrollid`
        //    is NOT NULL and the tax report joins the payslip.
        // ------------------------------------------------------------------
        if (result.TaxConfigs > 0)
        {
            var payrollRows = (await conn.QueryAsync<(long Id, long EmployeeId)>(
                @"SELECT id, employeeid FROM employeepayroll
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                   ORDER BY id LIMIT @limit",
                new { tenantId, schoolId, campusId, limit = options.TaxRecords })).ToList();

            var configId = incomeTaxConfigId ?? 0;

            foreach (var row in payrollRows)
            {
                var annual = 96_000m + (row.Id % 10) * 6_000m;
                await conn.ExecuteAsync(
                    @"INSERT INTO employeetaxrecord
                          (tenantid, schoolid, campusid, employeepayrollid, employeeid, taxconfigid,
                           annualgrosssalary, taxableincome, annualtaxamount, monthlytaxwithheld,
                           yeartodatetax, employercontribution, employeecontribution, calculationbreakdown,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @payrollId, @employeeId, @configId,
                              @annual, @taxable, @annualTax, @monthlyTax,
                              @annualTax, 0, @monthlyTax, 'Perf seed breakdown',
                              1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId, campusId,
                        payrollId = row.Id, employeeId = row.EmployeeId, configId,
                        annual,
                        taxable = annual - 50_000m,
                        annualTax = Math.Round((annual - 50_000m) * 0.05m, 2),
                        monthlyTax = Math.Round((annual - 50_000m) * 0.05m / 12m, 2),
                        now
                    });
                result.TaxRecords++;
            }
        }

        // ------------------------------------------------------------------
        // 5. The performance review cycle and its reviews.
        //
        // ⚠️ `uqx_employeeperformancereview` is UNIQUE (employeeid, cycleid), so ONE cycle means at
        // most one review per employee. `performancereviewcycle` also has to exist at all - it was
        // empty in every database here, and a review cannot be written without one.
        // ------------------------------------------------------------------
        var cycleId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO performancereviewcycle
                  (tenantid, schoolid, campusid, name, startdate, enddate, status, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              VALUES (@tenantId, @schoolId, @campusId, @name, @start::date, @end::date, 'Open', true,
                      1, 1, @now, @now)
              RETURNING id",
            new
            {
                tenantId, schoolId, campusId,
                name = $"Perf Cycle {DateTime.Today.Year}",
                start = DateTime.Today.AddMonths(-3),
                end = DateTime.Today.AddMonths(3),
                now
            });
        result.ReviewCycles++;

        var reviewStatuses = new[] { "Draft", "SelfAssessment", "ManagerReview", "Completed" };

        for (var i = 0; i < options.Reviews && i < employeeIds.Count; i++)
        {
            var status = reviewStatuses[i % reviewStatuses.Length];
            var completed = status == "Completed";

            await conn.ExecuteAsync(
                @"INSERT INTO employeeperformancereview
                      (tenantid, schoolid, campusid, employeeid, performancereviewcycleid,
                       selfassessmentcompleted, managerassessmentcompleted, finalrating, finalcomments,
                       reviewstatus, reviewedby, revieweddate,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @cycleId,
                          @selfDone, @managerDone, @rating, NULL,
                          @status, @reviewedBy, @reviewedDate,
                          1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId,
                    employeeId = employeeIds[i], cycleId,
                    selfDone = status != "Draft",
                    managerDone = completed,
                    rating = completed ? 3.5m + (i % 3) * 0.5m : (decimal?)null,
                    status,
                    reviewedBy = completed ? employeeIds[(i + 1) % employeeIds.Count] : (long?)null,
                    reviewedDate = completed ? now : (DateTime?)null,
                    now
                });
            result.Reviews++;
        }

        // ------------------------------------------------------------------
        // 6. The exit chain: a separation and the settlement calculated from it.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Separations && i < employeeIds.Count; i++)
        {
            var employeeId = employeeIds[i];
            var lastWorkingDate = DateTime.Today.AddDays(-(30 + i * 5));

            var separationId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO employeeseparation
                      (tenantid, schoolid, campusid, employeeid, separationtype, lastworkingdate,
                       noticeenddate, noticeperioddays, reason, status, settlementid,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @type, @lastWorkingDate::date,
                          NULL, 30, 'Perf seed separation', @status, NULL,
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId, employeeId,
                    type = SeparationTypes[i % SeparationTypes.Length],
                    lastWorkingDate,
                    // A campus accumulates separations in every state the desk can show.
                    status = i % 3 == 0 ? "Draft" : (i % 3 == 1 ? "Approved" : "Completed"),
                    now
                });
            result.Separations++;

            // `employeesettlement.separationid` is NOT NULL, so the settlement always follows its
            // separation. The REVERSE pointer (`employeeseparation.settlementid`) is deliberately left
            // NULL: nothing in the resource server writes it, and filling it here would claim a link
            // the application does not make.
            var dailyRate = 400m + (i % 5) * 25m;
            var unusedLeave = 10m + (i % 6);
            var leaveEncashment = dailyRate * unusedLeave;

            await conn.ExecuteAsync(
                @"INSERT INTO employeesettlement
                      (tenantid, schoolid, campusid, employeeid, separationid, lastpayrollperiodid,
                       status, finalsalary, gratuityamount, yearsofservice, leaveencashmentamount,
                       leavedaysencashed, unusedleavedays, dailyrate,
                       otherearnings, otherearningsdescription,
                       outstandingloanbalance, outstandingadvances, noticeshortfalldeduction,
                       otherdeductions, otherdeductionsdescription,
                       totalearnings, totaldeductions, netsettlementamount,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @separationId, NULL,
                          @status, @finalSalary, @gratuity, @years, @leaveEncashment,
                          @unusedLeave, @unusedLeave, @dailyRate,
                          0, NULL,
                          0, 0, 0,
                          0, NULL,
                          @earnings, 0, @earnings,
                          1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, employeeId, separationId,
                    // The settlement's own vocabulary is the narrower one the desk filters on.
                    status = i % 3 == 0 ? "Draft" : (i % 3 == 1 ? "Approved" : "Calculated"),
                    finalSalary = dailyRate * 30m,
                    gratuity = dailyRate * 30m * (1 + (i % 4)),
                    years = 1m + (i % 8),
                    leaveEncashment,
                    unusedLeave,
                    dailyRate,
                    earnings = dailyRate * 30m * (2 + (i % 4)) + leaveEncashment,
                    now
                });
            result.Settlements++;
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  HR money: campus {campusId} -> {result.TaxConfigs} tax configs + {result.TaxSlabs} slabs, "
                + $"{result.SalaryStructures:N0} salary structures, {result.LeaveBalances:N0} leave balances, "
                + $"{result.TaxRecords:N0} tax records, {result.ReviewCycles} cycle + {result.Reviews:N0} reviews, "
                + $"{result.Separations:N0} separations + {result.Settlements:N0} settlements");
        }

        return result;
    }

    /// <summary>Fills <paramref name="result"/> from what the campus already holds (the skip path).</summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, HrMoneyWorkspaceSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table}"
                + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.TaxConfigs = await ScopedAsync("taxconfig");
        result.TaxSlabs = await ScopedAsync("taxslab");
        result.SalaryStructures = await ScopedAsync("employeesalarystructure");
        result.LeaveBalances = await ScopedAsync("employeeleavebalance");
        result.TaxRecords = await ScopedAsync("employeetaxrecord");
        result.ReviewCycles = await ScopedAsync("performancereviewcycle");
        result.Reviews = await ScopedAsync("employeeperformancereview");
        result.Separations = await ScopedAsync("employeeseparation");
        result.Settlements = await ScopedAsync("employeesettlement");
    }
}
