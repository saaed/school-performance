using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>
/// Options for <see cref="HrModuleSeeder"/>. The defaults give one median-sized campus a
/// realistically populated HR module - not a token row count.
/// </summary>
public sealed class HrSeedOptions
{
    /// <summary>Staff members on the campus. A 2,000-student school staffs roughly this many.</summary>
    public int EmployeesPerCampus { get; set; } = 120;

    /// <summary>
    /// Working days of attendance recorded per employee. The HR attendance grid is
    /// date-bounded, so this is the axis that makes it a real range scan.
    /// </summary>
    public int AttendanceDaysPerEmployee { get; set; } = 60;

    /// <summary>Monthly payroll periods generated, newest first.</summary>
    public int PayrollPeriods { get; set; } = 6;

    /// <summary>Re-seed even when the campus already holds HR rows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's HR seed produced. Returned so a run can report real counts.</summary>
public sealed class HrSeedResult
{
    public bool Skipped { get; set; }
    public int Departments { get; set; }
    public int Designations { get; set; }
    public int Employees { get; set; }
    public int AttendanceStatuses { get; set; }
    public int Attendance { get; set; }
    public int PayrollPeriods { get; set; }
    public int PayrollRecords { get; set; }
}

/// <summary>
/// Seeds the HR module's VOLUME tables for one campus.
///
/// WHY THIS EXISTS
/// ---------------
/// `ayra_perf` held 14.2M attendance rows and 879k students, and **zero employees**. So every
/// HR grid in `db-report` SKIPPED - the tool was honest about it, and the honest answer was
/// "the HR module cannot be measured at all". That is a different problem from a slow query:
/// 96 controllers carry a grid and the catalogue could reach six of them, not because the
/// specs were missing but because the DATA was.
///
/// WHICH TABLES, AND WHY ONLY THESE
/// --------------------------------
/// The HR module has two kinds of table and only one of them grows:
///
///   * VOLUME tables - one row per person, or per person per day/month. These are what a
///     grid scans and pages, and these are what this seeder fills:
///         employee              (one per staff member)
///         employeeattendance    (one per employee per working day)
///         employeepayroll       (one per employee per payroll period)
///   * WORKFLOW tables - leave requests, loans, overtime, corrections, contracts, reviews,
///     settlements. A school of any size holds TENS of these, so indexing or scanning them
///     is not a performance question. Seeding them would add rows without adding a query
///     shape worth measuring, so they are deliberately left to the module that needs them.
///
/// The reference rows a volume table cannot live without are created here too - department,
/// designation, `attendancestatus` and `payrollperiod` - because their foreign keys are NOT
/// NULL on the tables above.
///
/// ⚠️ `attendancestatus` IS SEEDED PER CAMPUS, AND THAT IS NOT PADDING.
/// The nine canonical statuses already in every database sit at scope **(1,1,1)**, while
/// `AttendanceStatusRepository` matches tenant/school/campus **EXACTLY** - so a campus other
/// than 1 sees an EMPTY list and `employeeattendance.attendancestatusid` has nothing to
/// point at. This is the same "a seeded row no query can reach is worse than a missing one"
/// defect recorded against the e2e baseline; it appears here for the same reason.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds employees is skipped unless Force is
/// set, so re-running cannot silently double the volume.
/// </summary>
public sealed class HrModuleSeeder : BaseSeeder
{
    public HrModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk HR load invalidates, so <see cref="PerfDatasetSeeder"/> can ANALYZE
    /// them. Seeding without this leaves the planner reasoning from the row counts that
    /// described an EMPTY table, which shows up as an index scan chosen for a scope holding
    /// every row - the failure mode already measured on the student grid.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "employee", "employeeattendance", "employeepayroll",
        "department", "designation", "attendancestatus", "payrollperiod"
    };

    // The nine statuses the product ships, and the only vocabulary the attendance grid
    // renders. `Present` is what the bulk of the generated rows uses.
    private static readonly string[] AttendanceStatusNames =
    {
        "Present", "Absent", "Late", "Half Day", "On Leave",
        "Holiday", "Weekend", "Work From Home", "Official Duty"
    };

    private static readonly string[] DepartmentNames =
    {
        "Academic", "Administration", "Finance", "Human Resources", "Operations", "Support"
    };

    private static readonly string[] DesignationNames =
    {
        "Teacher", "Senior Teacher", "Head of Department", "Coordinator",
        "Accountant", "Administrator", "Librarian", "Counselor"
    };

    private static readonly string[] FirstNames =
    {
        "Ahmed", "Sara", "Omar", "Layla", "Hassan", "Noor", "Yusuf", "Mariam",
        "Khalid", "Fatima", "Ali", "Zainab", "Tariq", "Hana", "Bilal", "Aisha"
    };

    private static readonly string[] LastNames =
    {
        "Khan", "Ali", "Hussain", "Siddiqui", "Rahman", "Malik", "Farooq",
        "Iqbal", "Sheikh", "Ansari", "Qureshi", "Baig"
    };

    public async Task<HrSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, HrSeedOptions options, bool verbose = true)
    {
        var result = new HrSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM employee
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.Employees = (int)existing;
            if (verbose)
            {
                Console.WriteLine(
                    $"  HR: campus {campusId} already holds {existing:N0} employees - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 0. A FORCED re-seed clears first. The codes below are deterministic
        //    (`PERF-DEPT-{campus}-1`, `PERF-EMP-{campus}-00001`, ...) so appending a second
        //    copy would either collide on a unique constraint or double the volume while
        //    every count still looked plausible. Order is children-first: a plain DELETE of
        //    `employee` while `employeeattendance` points at it is a 23503.
        // ------------------------------------------------------------------
        if (options.Force && existing > 0)
        {
            foreach (var table in new[]
                     {
                         "employeepayroll", "employeeattendance", "employee",
                         "payrollperiod", "designation", "department"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
            {
                Console.WriteLine($"  HR: campus {campusId} cleared for a forced re-seed");
            }
        }

        // ------------------------------------------------------------------
        // 1. Department + designation. `employee.departmentid` / `designationid`
        //    are nullable but FK-constrained, and every HR grid joins them for its
        //    display columns - so they have to exist for the join to be exercised.
        // ------------------------------------------------------------------
        var departmentIds = new List<long>();
        for (var i = 0; i < DepartmentNames.Length; i++)
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO department
                      (tenantid, schoolid, campusid, name, code, displayorder, isactive,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @code, @order, true,
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId,
                    name = DepartmentNames[i],
                    code = $"PERF-DEPT-{campusId}-{i + 1}",
                    order = i + 1,
                    now
                });
            departmentIds.Add(id);
        }
        result.Departments = departmentIds.Count;

        // ⚠️ `canteach` is FALSE on purpose. `EmployeeProvisioningService` turns an
        // employee whose DESIGNATION has canteach=true into a Teacher, which writes to
        // `teacher` - a table with NOT NULL `dob`/`nic`/`address`/`photo` columns and its
        // own provisioning rules. The teacher module has its own concerns; seeding it here
        // would make this seeder responsible for a module it is not measuring.
        var designationIds = new List<long>();
        for (var i = 0; i < DesignationNames.Length; i++)
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO designation
                      (tenantid, schoolid, campusid, departmentid, name, code,
                       canlogin, canteach, ismanager, isactive,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @departmentId, @name, @code,
                          false, false, @isManager, true,
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId,
                    departmentId = departmentIds[i % departmentIds.Count],
                    name = DesignationNames[i],
                    code = $"PERF-DESIG-{campusId}-{i + 1}",
                    isManager = DesignationNames[i].StartsWith("Head"),
                    now
                });
            designationIds.Add(id);
        }
        result.Designations = designationIds.Count;

        // ------------------------------------------------------------------
        // 2. Attendance statuses, scoped to THIS campus (see the class comment).
        // ------------------------------------------------------------------
        var statusIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM attendancestatus
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (statusIds.Count == 0)
        {
            foreach (var statusName in AttendanceStatusNames)
            {
                var id = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO attendancestatus
                          (tenantid, schoolid, campusid, name, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @name, @now, @now)
                      RETURNING id",
                    new { tenantId, schoolId, campusId, name = statusName, now });
                statusIds.Add(id);
            }
        }
        result.AttendanceStatuses = statusIds.Count;

        // Index 0 is `Present` in the canonical order; the rest follow it.
        var presentStatusId = statusIds[0];

        // ------------------------------------------------------------------
        // 3. Employees - the root every HR grid joins.
        // ------------------------------------------------------------------
        var employeeIds = await SeedEmployeesAsync(
            conn, tenantId, schoolId, campusId, options.EmployeesPerCampus,
            departmentIds, designationIds, now, verbose);
        result.Employees = employeeIds.Count;

        if (employeeIds.Count == 0)
        {
            // Nothing below here can be written without an employee, and silently
            // returning zeros would read as "seeded" in the run report.
            throw new InvalidOperationException(
                $"HR seeding wrote no employees for campus {campusId}, so its attendance and " +
                "payroll cannot be seeded either.");
        }

        // ------------------------------------------------------------------
        // 4. Payroll periods - `employeepayroll.payrollperiodid` is NOT NULL.
        //    The newest is left OPEN (the state a payroll run needs) and the rest closed.
        // ------------------------------------------------------------------
        var periodIds = new List<long>();
        var firstOfThisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        for (var i = 0; i < options.PayrollPeriods; i++)
        {
            var start = firstOfThisMonth.AddMonths(-i);
            // The LAST day of the month is start + 1 month - 1 day; using AddMonths(-i)
            // on `firstOfThisMonth` from the end of a month is how a period accidentally
            // spans two months, so the boundary is computed explicitly.
            var end = start.AddMonths(1).AddDays(-1);

            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO payrollperiod
                      (tenantid, schoolid, campusid, name, startdate, enddate,
                       status, locked, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @start, @end,
                          @status, @locked, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId,
                    name = start.ToString("MMM yyyy"),
                    start = start.Date,
                    end = end.Date,
                    status = i == 0 ? "Open" : "Closed",
                    locked = i != 0,
                    now
                });
            periodIds.Add(id);
        }
        result.PayrollPeriods = periodIds.Count;

        // ------------------------------------------------------------------
        // 5. Employee attendance - employee x working day. This is the HR volume table.
        // ------------------------------------------------------------------
        result.Attendance = await SeedAttendanceAsync(
            conn, tenantId, schoolId, campusId, employeeIds, presentStatusId,
            statusIds, options.AttendanceDaysPerEmployee, now, verbose);

        // ------------------------------------------------------------------
        // 6. Payroll records - employee x period.
        // ------------------------------------------------------------------
        result.PayrollRecords = await SeedPayrollAsync(
            conn, tenantId, schoolId, campusId, employeeIds, periodIds, now, verbose);

        if (verbose)
        {
            Console.WriteLine(
                $"  HR: campus {campusId} -> {result.Departments} departments, " +
                $"{result.Designations} designations, {result.Employees:N0} employees, " +
                $"{result.Attendance:N0} attendance rows, " +
                $"{result.PayrollPeriods} periods, {result.PayrollRecords:N0} payroll records");
        }

        return result;
    }

    private async Task<List<long>> SeedEmployeesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        int count, List<long> departmentIds, List<long> designationIds,
        DateTime now, bool verbose)
    {
        var ids = new List<long>(count);
        var batch = new List<EmployeeRow>(1000);

        for (var i = 0; i < count; i++)
        {
            var first = FirstNames[i % FirstNames.Length];
            var last = LastNames[(i / FirstNames.Length) % LastNames.Length];
            batch.Add(new EmployeeRow
            {
                EmployeeCode = $"PERF-EMP-{campusId}-{i + 1:D5}",
                FirstName = first,
                LastName = last,
                Gender = i % 2 == 0 ? "Male" : "Female",
                Email = $"perf.emp{campusId}.{i + 1}@perf.test",
                Phone = $"05{campusId:D2}{i:D6}",
                DepartmentId = departmentIds[i % departmentIds.Count],
                DesignationId = designationIds[i % designationIds.Count],
                JoiningDate = new DateTime(2020 + (i % 6), 1 + (i % 12), 1 + (i % 27)),
            });

            if (batch.Count >= 1000)
            {
                ids.AddRange(await InsertEmployeesAsync(
                    conn, tenantId, schoolId, campusId, batch, now));
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            ids.AddRange(await InsertEmployeesAsync(conn, tenantId, schoolId, campusId, batch, now));
        }

        if (verbose)
        {
            LogProgress($"  HR employees (campus {campusId})", ids.Count, count);
        }

        return ids;
    }

    private sealed class EmployeeRow
    {
        public string EmployeeCode { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Gender { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public long DepartmentId { get; set; }
        public long DesignationId { get; set; }
        public DateTime JoiningDate { get; set; }
    }

    private static async Task<List<long>> InsertEmployeesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<EmployeeRow> batch, DateTime now)
    {
        // `unnest(...) RETURNING id` - one round trip per batch, and the ids come back in
        // INSERT order, which is what the callers below key their rows on.
        const string sql = @"
            INSERT INTO employee
                (tenantid, schoolid, campusid, employeecode, firstname, lastname,
                 gender, email, phone, departmentid, designationid, joiningdate,
                 employmentstatus, isactive, createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                   unnest(@Codes), unnest(@FirstNames), unnest(@LastNames),
                   unnest(@Genders), unnest(@Emails), unnest(@Phones),
                   unnest(@DepartmentIds), unnest(@DesignationIds),
                   unnest(@JoiningDates), 'Active', true, 1, 1, @now, @now
            RETURNING id";

        var ids = await conn.QueryAsync<long>(sql, new
        {
            TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
            SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
            CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
            Codes = batch.Select(b => b.EmployeeCode).ToArray(),
            FirstNames = batch.Select(b => b.FirstName).ToArray(),
            LastNames = batch.Select(b => b.LastName).ToArray(),
            Genders = batch.Select(b => b.Gender).ToArray(),
            Emails = batch.Select(b => b.Email).ToArray(),
            Phones = batch.Select(b => b.Phone).ToArray(),
            DepartmentIds = batch.Select(b => b.DepartmentId).ToArray(),
            DesignationIds = batch.Select(b => b.DesignationId).ToArray(),
            JoiningDates = batch.Select(b => b.JoiningDate.Date).ToArray(),
            now
        });

        return ids.ToList();
    }

    private async Task<int> SeedAttendanceAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> employeeIds, long presentStatusId, List<long> allStatusIds,
        int daysPerEmployee, DateTime now, bool verbose)
    {
        var total = employeeIds.Count * daysPerEmployee;
        var inserted = 0;
        var batch = new List<AttendanceRow>(2000);

        // ⚠️ The window ENDS TODAY, not yesterday. `AddDays(-daysPerEmployee)` made the range
        // [today-60, today-1], so the day the attendance screen opens on by DEFAULT had no rows -
        // measured as `hr-attendance-daily ... rows 0` on a campus holding 5,040 attendance rows.
        // A perf spec that measures an empty result is measuring the wrong thing, and the same
        // off-by-one is exactly what made the original student dataset unreadable.
        var startDay = DateTime.Today.AddDays(-(daysPerEmployee - 1));

        for (var day = 0; day < daysPerEmployee; day++)
        {
            var date = startDay.AddDays(day);
            // The HR attendance grid is a date range over working days, so weekends are
            // skipped the way the product's own records are.
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var employeeId in employeeIds)
            {
                // Weighted the way real attendance looks: mostly present. The variety
                // matters because the grid has a status filter, and a single-status dataset
                // makes that filter measure one branch.
                var roll = Random.Shared.Next(100);
                var statusId = roll switch
                {
                    < 88 => presentStatusId,                                  // Present
                    < 93 => allStatusIds[Math.Min(1, allStatusIds.Count - 1)], // Absent
                    < 97 => allStatusIds[Math.Min(2, allStatusIds.Count - 1)], // Late
                    _ => allStatusIds[Math.Min(4, allStatusIds.Count - 1)]     // On Leave
                };

                batch.Add(new AttendanceRow
                {
                    EmployeeId = employeeId,
                    AttendanceDate = date,
                    AttendanceStatusId = statusId,
                    CheckInTime = date.AddHours(8).AddMinutes(Random.Shared.Next(0, 25)),
                    CheckOutTime = date.AddHours(16).AddMinutes(Random.Shared.Next(0, 40)),
                });
            }

            if (batch.Count >= 2000)
            {
                inserted += await InsertAttendanceAsync(conn, tenantId, schoolId, campusId, batch, now);
                batch.Clear();
                if (verbose) LogProgress($"  HR attendance (campus {campusId})", inserted, total);
            }
        }

        if (batch.Count > 0)
        {
            inserted += await InsertAttendanceAsync(conn, tenantId, schoolId, campusId, batch, now);
        }

        if (verbose) LogProgress($"  HR attendance (campus {campusId})", inserted, total);
        return inserted;
    }

    private sealed class AttendanceRow
    {
        public long EmployeeId { get; set; }
        public DateTime AttendanceDate { get; set; }
        public long AttendanceStatusId { get; set; }

        // ⚠️ DateTime, NOT TimeSpan. `employeeattendance.checkintime` / `checkouttime` are
        // `timestamp without time zone` - not `time` - despite their names. Binding a
        // TimeSpan sends a `time` and PostgreSQL refuses it outright:
        //   42804: column "checkintime" is of type timestamp without time zone
        //          but expression is of type time without time zone
        // The time of day is the part that carries meaning; the date half is the row's own
        // attendance date.
        public DateTime CheckInTime { get; set; }
        public DateTime CheckOutTime { get; set; }
    }

    private static async Task<int> InsertAttendanceAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<AttendanceRow> batch, DateTime now)
    {
        // ⚠️ The `::date` cast is required. `unnest` of a DateTime[] binds `timestamp` while
        // `attendancedate` is `date`, and PostgreSQL refuses the narrower column rather than
        // widening it. `checkintime`/`checkouttime` are `timestamp` already, so they need no
        // cast - casting them `::time` is what produced 42804 in the first version.
        const string sql = @"
            INSERT INTO employeeattendance
                (tenantid, schoolid, campusid, employeeid, attendancedate, attendancestatusid,
                 checkintime, checkouttime, source, islocked, createdby, modifiedby,
                 createdon, modifiedon)
            SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                   unnest(@EmployeeIds), unnest(@Dates)::date, unnest(@StatusIds),
                   unnest(@CheckIns), unnest(@CheckOuts),
                   'Manual', false, 1, 1, @now, @now";

        return await conn.ExecuteAsync(sql, new
        {
            TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
            SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
            CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
            EmployeeIds = batch.Select(b => b.EmployeeId).ToArray(),
            Dates = batch.Select(b => b.AttendanceDate.Date).ToArray(),
            StatusIds = batch.Select(b => b.AttendanceStatusId).ToArray(),
            CheckIns = batch.Select(b => b.CheckInTime).ToArray(),
            CheckOuts = batch.Select(b => b.CheckOutTime).ToArray(),
            now
        });
    }

    private async Task<int> SeedPayrollAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> employeeIds, List<long> periodIds, DateTime now, bool verbose)
    {
        var inserted = 0;
        var total = employeeIds.Count * periodIds.Count;
        var batch = new List<PayrollRow>(2000);

        foreach (var periodId in periodIds)
        {
            foreach (var employeeId in employeeIds)
            {
                // A spread of salaries, so the grid's aggregate columns are not all equal.
                var basic = 4000m + Random.Shared.Next(0, 60) * 100m;
                var allowance = Math.Round(basic * 0.15m, 2);
                var deduction = Math.Round(basic * 0.05m, 2);

                batch.Add(new PayrollRow
                {
                    PeriodId = periodId,
                    EmployeeId = employeeId,
                    Gross = basic + allowance,
                    Allowance = allowance,
                    Deduction = deduction,
                    Net = basic + allowance - deduction,
                });
            }

            if (batch.Count >= 2000)
            {
                inserted += await InsertPayrollAsync(conn, tenantId, schoolId, campusId, batch, now);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            inserted += await InsertPayrollAsync(conn, tenantId, schoolId, campusId, batch, now);
        }

        if (verbose) LogProgress($"  HR payroll (campus {campusId})", inserted, total);
        return inserted;
    }

    private sealed class PayrollRow
    {
        public long PeriodId { get; set; }
        public long EmployeeId { get; set; }
        public decimal Gross { get; set; }
        public decimal Allowance { get; set; }
        public decimal Deduction { get; set; }
        public decimal Net { get; set; }
    }

    private static async Task<int> InsertPayrollAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<PayrollRow> batch, DateTime now)
    {
        const string sql = @"
            INSERT INTO employeepayroll
                (tenantid, schoolid, campusid, payrollperiodid, employeeid,
                 grosssalary, totalallowance, totaldeduction, netsalary,
                 status, totalworkingdays, presentdays, absentdays,
                 createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                   unnest(@PeriodIds), unnest(@EmployeeIds),
                   unnest(@Gross), unnest(@Allowances), unnest(@Deductions), unnest(@Nets),
                   'Finalized', 22, 21, 1, 1, 1, @now, @now";

        return await conn.ExecuteAsync(sql, new
        {
            TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
            SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
            CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
            PeriodIds = batch.Select(b => b.PeriodId).ToArray(),
            EmployeeIds = batch.Select(b => b.EmployeeId).ToArray(),
            Gross = batch.Select(b => b.Gross).ToArray(),
            Allowances = batch.Select(b => b.Allowance).ToArray(),
            Deductions = batch.Select(b => b.Deduction).ToArray(),
            Nets = batch.Select(b => b.Net).ToArray(),
            now
        });
    }
}
