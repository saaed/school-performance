using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="HrWorkflowSeeder"/>. One campus gets a normal set of each.</summary>
public sealed class HrWorkflowSeedOptions
{
    /// <summary>Employees that get a loan. Each loan writes its installment schedule too.</summary>
    public int Loans { get; set; } = 50;

    /// <summary>Overtime rows (one per employee per day).</summary>
    public int OvertimeRows { get; set; } = 60;

    /// <summary>Employees that get an employment contract.</summary>
    public int Contracts { get; set; } = 50;

    /// <summary>Employees that get an uploaded document.</summary>
    public int Documents { get; set; } = 100;

    /// <summary>Reporting lines (employee -> manager).</summary>
    public int ReportingLines { get; set; } = 50;

    /// <summary>Attendance correction requests.</summary>
    public int Corrections { get; set; } = 40;

    /// <summary>Instalments written per loan (the schedule the payroll collects from).</summary>
    public int LoanTermMonths { get; set; } = 12;

    public bool Force { get; set; }
}

/// <summary>What one campus's HR-workflow seed produced.</summary>
public sealed class HrWorkflowSeedResult
{
    public bool Skipped { get; set; }
    public int LoanTypes { get; set; }
    public int OvertimePolicies { get; set; }
    public int DocumentTypes { get; set; }
    public int ContractTypes { get; set; }
    public int PerformanceScales { get; set; }
    public int PerformanceScaleLevels { get; set; }
    public int Loans { get; set; }
    public int LoanInstallments { get; set; }
    public int Overtimes { get; set; }
    public int Contracts { get; set; }
    public int Documents { get; set; }
    public int ReportingLines { get; set; }
    public int Corrections { get; set; }
}

/// <summary>
/// Seeds the HR WORKFLOW tables the module's desks page over, for one campus. These are the
/// transactions a real school accumulates - the module's grid endpoints - as opposed to the HR
/// spine (`employee`, `employeeattendance`, `employeepayroll`) that <see cref="HrModuleSeeder"/>
/// already fills.
///
/// WHY THIS EXISTS
/// ---------------
/// On `ayra_perf` every one of these tables held ZERO rows, so each of their grid specs could only
/// report **SKIP** - honest and useless at once: a grid the application ships, reading as
/// "not measured yet". Coverage here is `specs x data`, and the data side is the larger cost; the
/// same unlock as HR, inventory, library, transport and accounting before it.
///
/// ⚠️ EVERY ROW HERE IS WRITTEN THROUGH A RESOLVED PARENT, NEVER A PLACEHOLDER ID. These grids are
/// scope-filtered and most of them JOIN the employee, so an `employeeid` that does not resolve is a
/// row that exists and is INVISIBLE - which is the failure mode that makes a seeded table look
/// populated while the screen stays empty (the `attendance.studentenrollmentid` lesson). The
/// employee ids are read from the campus's own `employee` rows, which
/// <see cref="HrModuleSeeder"/> must have created first.
///
/// ⚠️ THESE ARE TENS-TO-HUNDREDS OF ROWS PER CAMPUS, AND THAT IS THE POINT. A staff of 120 holds
/// 120 contracts, not 120,000. The specs these unlock therefore carry low `MinVolume` gates; do
/// not pad the counts to make a timing look impressive.
///
/// IDEMPOTENT: a campus that already holds loans is skipped unless Force is set (and a skipped
/// campus still REPORTS what it holds, so a fixture's own assertion is true on a re-run).
/// </summary>
public sealed class HrWorkflowSeeder : BaseSeeder
{
    public HrWorkflowSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a workflow load invalidates statistics for.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "employeeloantype", "employeeloan", "employeeloaninstallment", "overtimepolicy",
        "employeeovertime", "employeedocument", "employeedocumenttype", "contracttype",
        "employmentcontract", "employeereporting", "performancescale", "performancescalelevel",
        "attendancecorrectionrequest"
    };

    /// <summary>
    /// The grid is driven by `employeeloan`, so that is the skip marker: a campus with loans has
    /// already been seeded, and the reference rows above it are idempotent anyway.
    /// </summary>
    private const string SkipMarkerTable = "employeeloan";

    private static readonly (string Name, string Code, string Purpose)[] LoanTypes =
    {
        ("Personal Loan", "PL", "Personal Loan"),
        ("Emergency Loan", "EL", "Emergency Loan"),
        ("Housing Loan", "HL", "Housing Loan"),
    };

    private static readonly (string Name, string Code)[] DocumentTypes =
    {
        ("Passport", "PASSPORT"),
        ("Educational Certificate", "EDU_CERT"),
        ("Experience Letter", "EXP_LETTER"),
        ("Medical Fitness", "MEDICAL"),
    };

    private static readonly (string Name, string Description)[] ContractTypes =
    {
        ("Permanent", "Open-ended employment"),
        ("Fixed Term", "Renewable fixed-term contract"),
        ("Probation", "Initial probation period"),
    };

    /// <summary>The performance scale and its levels - the dropdown every review scores against.</summary>
    private static readonly (string Label, decimal Score)[] ScaleLevels =
    {
        ("Needs Improvement", 1m),
        ("Meets Expectations", 2m),
        ("Exceeds Expectations", 3m),
        ("Outstanding", 4m),
    };

    public async Task<HrWorkflowSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, HrWorkflowSeedOptions options, bool verbose = true)
    {
        var result = new HrWorkflowSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {SkipMarkerTable}"
            + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // A skipped campus must still report what it HOLDS, or a caller's total describes what
            // THIS RUN wrote rather than what the campus has.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
                Console.WriteLine($"  HR workflow: campus {campusId} already holds {existing:N0} loans - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // The people every transaction below hangs off. A campus with no employee cannot have a
        // workflow, and inventing an id would write rows no screen can join.
        var employeeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employee
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND isactive = true
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (employeeIds.Count == 0)
            throw new InvalidOperationException(
                $"campus {campusId} holds no active employee, so no HR workflow row could reference one - "
                + "run HrModuleSeeder first.");

        if (options.Force && existing > 0)
        {
            // Children first: `employeeloaninstallment` has no scope columns of its own in the
            // generic sense - it does carry the triple - but clearing the parent first would trip
            // the FK, so it goes in child order.
            await ClearTableAsync(conn, "employeeloaninstallment", tenantId, schoolId, campusId);
            foreach (var table in new[]
                     {
                         "employeeloan", "employeeovertime", "attendancecorrectionrequest",
                         "employeereporting", "employmentcontract", "employeedocument",
                         "employeeloantype", "overtimepolicy", "employeedocumenttype", "contracttype",
                         "performancescalelevel", "performancescale"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose) Console.WriteLine($"  HR workflow: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. Reference rows the desks' own dropdowns are fed from.
        // ------------------------------------------------------------------
        foreach (var (name, code, purpose) in LoanTypes)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO employeeloantype
                      (tenantid, schoolid, campusid, name, code, description, isactive, displayorder,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @code, @purpose, true, @order,
                          1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, name, code, purpose, order = result.LoanTypes + 1, now });
            result.LoanTypes++;
        }

        // One campus-wide policy (no department / designation = the fallback rule).
        await conn.ExecuteAsync(
            @"INSERT INTO overtimepolicy
                  (tenantid, schoolid, campusid, normalhours, minimumminutes, maximumhours, multiplier,
                   weekendeligible, holidayeligible, roundingminutes, requiresapproval, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              VALUES (@tenantId, @schoolId, @campusId, 8.0, 15, 4, 1.50,
                      false, false, 15, true, true, 1, 1, @now, @now)",
            new { tenantId, schoolId, campusId, now });
        result.OvertimePolicies++;

        foreach (var (name, code) in DocumentTypes)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO employeedocumenttype
                      (tenantid, schoolid, campusid, name, code, isrequired, hasexpiry, isactive,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @code, false, false, true,
                          1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, name, code, now });
            result.DocumentTypes++;
        }

        foreach (var (name, description) in ContractTypes)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO contracttype
                      (tenantid, schoolid, campusid, name, description, isactive,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @description, true,
                          1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, name, description, now });
            result.ContractTypes++;
        }

        // The scale and its levels. `performancescalelevel` is what a review scores against, so a
        // scale with no levels is a configuration the review screen cannot use.
        var scaleId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO performancescale
                  (name, description, tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
              VALUES (@name, @description, @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
              RETURNING id",
            new { name = "Standard Performance Scale", description = "Seeded scale", tenantId, schoolId, campusId, now });
        result.PerformanceScales++;

        foreach (var (label, score) in ScaleLevels)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO performancescalelevel
                      (performancescaleid, label, score, displayorder)
                  VALUES (@scaleId, @label, @score, @order)",
                new { scaleId, label, score, order = result.PerformanceScaleLevels + 1 });
            result.PerformanceScaleLevels++;
        }

        var contractTypeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM contracttype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var documentTypeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employeedocumenttype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        // `employeedocument.documenttypeid` is NOT NULL, so unlike the loan/contract type ids there
        // is no honest fallback: a document whose type does not resolve is invisible to the desk.
        if (documentTypeIds.Count == 0)
            throw new InvalidOperationException(
                $"campus {campusId} holds no employeedocumenttype row, so no document could be written - "
                + "the reference inserts above must have failed.");

        var loanTypeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employeeloantype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        // ------------------------------------------------------------------
        // 2. Loans + their installment schedule. The schedule is what the payroll collects from
        //    (`installmentstatus = 'Pending'` + a due date inside the period), so a loan with no
        //    installments is a row the payroll screen cannot act on.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Loans; i++)
        {
            var employeeId = employeeIds[i % employeeIds.Count];
            var principal = 5_000m + (i % 10) * 500m;
            var termMonths = options.LoanTermMonths;
            var installment = Math.Round(principal / termMonths, 2);
            var start = DateTime.Today.AddMonths(-(i % 6));

            var loanId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO employeeloan
                      (tenantid, schoolid, campusid, employeeid, loannumber, loantype, purpose,
                       loantypeid, principalamount, interestrate, termmonths, installmentamount,
                       totalinterest, totalrepayable, startdate, firstinstallmentdate, status,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @loanNumber, @loanType, @purpose,
                          @loanTypeId, @principal, 0, @termMonths, @installment,
                          0, @totalRepayable, @start, @firstDue, 'Active',
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId, employeeId,
                    loanNumber = $"PERF-LN-{campusId}-{i + 1}",
                    loanType = "Personal Loan",
                    purpose = "Seeded employee loan",
                    loanTypeId = loanTypeIds.Count > 0 ? loanTypeIds[i % loanTypeIds.Count] : (long?)null,
                    principal,
                    termMonths,
                    installment,
                    totalRepayable = installment * termMonths,
                    start,
                    firstDue = start.AddMonths(1),
                    now
                });
            result.Loans++;

            for (var n = 1; n <= termMonths; n++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO employeeloaninstallment
                          (tenantid, schoolid, campusid, employeeloanid, installmentno, duedate,
                           principalamount, interestamount, totalamount, status,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @loanId, @no, @due,
                              @installment, 0, @installment, 'Pending',
                              1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId, campusId, loanId, no = n,
                        due = start.AddMonths(n), installment, now
                    });
                result.LoanInstallments++;
            }
        }

        // ------------------------------------------------------------------
        // 3. Overtime - attached to a real attendance day where one exists, so the row is the
        //    shape the auto-generation writes.
        // ------------------------------------------------------------------
        // ⚠️ `uq_employeeovertime_employee_date` is UNIQUE (employeeid, attendancedate), so the day
        // must not repeat for an employee. Cycling the DAY first and the employee second is what
        // makes that true for any pool size: with 120 employees the first 60 rows are six dozen
        // different people on one day, and with 10 they are one person per day for six days.
        for (var i = 0; i < options.OvertimeRows; i++)
        {
            var employeeId = employeeIds[i % employeeIds.Count];
            var date = DateTime.Today.AddDays(-(i / employeeIds.Count));

            await conn.ExecuteAsync(
                @"INSERT INTO employeeovertime
                      (tenantid, schoolid, campusid, employeeid, attendancedate,
                       calculatedminutes, requestedminutes, approvedminutes, status, payrollprocessed,
                       remarks, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @date,
                          90, 90, 0, 'Draft', false, 'Seeded overtime', 1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, employeeId, date, now });
            result.Overtimes++;
        }

        // ------------------------------------------------------------------
        // 4. Employment contracts - one CURRENT contract per employee, which is the row the
        //    contract grid and `GetCurrentByEmployee` resolve.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Contracts; i++)
        {
            var employeeId = employeeIds[i % employeeIds.Count];
            var start = DateTime.Today.AddYears(-1).AddMonths(-(i % 12));

            await conn.ExecuteAsync(
                @"INSERT INTO employmentcontract
                      (tenantid, schoolid, campusid, employeeid, contractnumber, contracttypeid,
                       startdate, enddate, basicsalary, currency, workinghours, workingdaysperweek,
                       employmentstatus, iscurrent, isactive, contractstatus,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @number, @typeId,
                          @start, @end, @salary, 'AED', 8, 5,
                          'Active', true, true, 'Active',
                          1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, employeeId,
                    number = $"PERF-CT-{campusId}-{i + 1}",
                    typeId = contractTypeIds.Count > 0 ? contractTypeIds[i % contractTypeIds.Count] : (long?)null,
                    start, end = start.AddYears(2), salary = 8_000m + (i % 10) * 500m, now
                });
            result.Contracts++;
        }

        // ------------------------------------------------------------------
        // 5. Employee documents - the HR documents desk pages over these.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Documents; i++)
        {
            var employeeId = employeeIds[i % employeeIds.Count];
            await conn.ExecuteAsync(
                @"INSERT INTO employeedocument
                      (tenantid, schoolid, campusid, employeeid, documenttypeid, documentnumber,
                       issuedate, expirydate, filename, filepath, fileextension, filesize,
                       verificationstatus, isactive, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @typeId, @number,
                          @issued, @expires, @filename, @filepath, 'pdf', 1024,
                          'Verified', true, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, employeeId,
                    typeId = documentTypeIds.Count > 0 ? documentTypeIds[i % documentTypeIds.Count] : 0L,
                    number = $"PERF-DOC-{campusId}-{i + 1}",
                    issued = DateTime.Today.AddYears(-1),
                    expires = DateTime.Today.AddYears(2),
                    filename = $"perf-doc-{i + 1}.pdf",
                    filepath = "/perf-seed/",
                    now
                });
            result.Documents++;
        }

        // ------------------------------------------------------------------
        // 6. Reporting lines - employee -> manager, ACTIVE (no effective-to), which is exactly what
        //    the approval engine's `AssignToManager` step resolves.
        // ------------------------------------------------------------------
        // ⚠️ `ux_employeereporting_oneactive` is a PARTIAL unique index on (employeeid) WHERE
        // effectiveto IS NULL AND isprimary = true, so an employee may hold only ONE open line.
        // Walking the employee list ONCE is what guarantees that (a `i % count` loop would write a
        // second open line for the same person the moment ReportingLines exceeds the pool).
        if (employeeIds.Count > 1)
        {
            for (var i = 0; i < employeeIds.Count && result.ReportingLines < options.ReportingLines; i++)
            {
                var employeeId = employeeIds[i];
                // The manager is a DIFFERENT person, or the hierarchy is self-referential - which
                // the app refuses by rule.
                var managerId = employeeIds[(i + 1) % employeeIds.Count];
                if (managerId == employeeId) continue;

                await conn.ExecuteAsync(
                    @"INSERT INTO employeereporting
                          (tenantid, schoolid, campusid, employeeid, manageremployeeid, effectivefrom,
                           isprimary, remarks, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @employeeId, @managerId, @effectiveFrom,
                              true, 'Perf seed reporting line', 1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId, campusId, employeeId, managerId,
                        effectiveFrom = DateTime.Today.AddYears(-1), now
                    });
                result.ReportingLines++;
            }
        }

        // ------------------------------------------------------------------
        // 7. Attendance correction requests - one per employee per day, in the state the desk's
        //    own grid filters on.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Corrections; i++)
        {
            var employeeId = employeeIds[i % employeeIds.Count];
            var date = DateTime.Today.AddDays(-(i % 20));

            await conn.ExecuteAsync(
                @"INSERT INTO attendancecorrectionrequest
                      (tenantid, schoolid, campusid, employeeid, attendancedate,
                       requestedcheckin, requestedcheckout, reason, status,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @employeeId, @date,
                          @checkIn, @checkOut, 'Perf seed correction', 'Pending',
                          1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, employeeId, date,
                    checkIn = date.AddHours(9), checkOut = date.AddHours(18), now
                });
            result.Corrections++;
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  HR workflow: campus {campusId} -> {result.LoanTypes} loan types, "
                + $"{result.OvertimePolicies} overtime policy, {result.DocumentTypes} document types, "
                + $"{result.ContractTypes} contract types, {result.PerformanceScaleLevels} scale levels, "
                + $"{result.Loans:N0} loans + {result.LoanInstallments:N0} installments, "
                + $"{result.Overtimes:N0} overtime rows, {result.Contracts:N0} contracts, "
                + $"{result.Documents:N0} documents, {result.ReportingLines:N0} reporting lines, "
                + $"{result.Corrections:N0} corrections");
        }

        return result;
    }

    /// <summary>Fills <paramref name="result"/> from what the campus already holds (the skip path).</summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, HrWorkflowSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table}"
                + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.LoanTypes = await ScopedAsync("employeeloantype");
        result.OvertimePolicies = await ScopedAsync("overtimepolicy");
        result.DocumentTypes = await ScopedAsync("employeedocumenttype");
        result.ContractTypes = await ScopedAsync("contracttype");
        result.PerformanceScales = await ScopedAsync("performancescale");
        result.Loans = await ScopedAsync("employeeloan");
        result.Overtimes = await ScopedAsync("employeeovertime");
        result.Contracts = await ScopedAsync("employmentcontract");
        result.Documents = await ScopedAsync("employeedocument");
        result.ReportingLines = await ScopedAsync("employeereporting");
        result.Corrections = await ScopedAsync("attendancecorrectionrequest");

        // `performancescalelevel` carries no scope columns, so it is counted through its parent.
        result.PerformanceScaleLevels = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM performancescalelevel l
                JOIN performancescale s ON s.id = l.performancescaleid
               WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId",
            new { tenantId, schoolId, campusId });
    }
}
