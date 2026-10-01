using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>
/// Options for <see cref="HrLeaveSeeder"/>. The defaults give one campus a year of leave paperwork
/// for its whole staff.
/// </summary>
public sealed class LeaveSeedOptions
{
    /// <summary>Leave types the campus offers (the reference rows a request cannot exist without).</summary>
    public int LeaveTypesPerCampus { get; set; } = 8;

    /// <summary>Requests filed per employee. Four is a realistic year for a school's staff.</summary>
    public int RequestsPerEmployee { get; set; } = 4;

    /// <summary>Re-seed even when the campus already holds leave requests.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's leave seed produced.</summary>
public sealed class LeaveSeedResult
{
    public bool Skipped { get; set; }
    public int LeaveTypes { get; set; }
    public int LeaveRequests { get; set; }
}

/// <summary>
/// Seeds `employeeleaverequest` - the fact table behind `LEAVE_SUMMARY` (`vw_leave_summary`).
///
/// WHY THIS EXISTS
/// ---------------
/// `employeeleaverequest` held ZERO rows, so `rpt-leave-summary` reported SKIP. The view joins
/// `employee` and `leavetype`, and it derives its three money columns from the STATUS STRING
/// (`approveddays` / `pendingdays` / `rejecteddays` are CASE expressions over
/// `lower(elr.status)`), so the seeded status vocabulary is part of the contract rather than a
/// detail: a campus seeded entirely `Pending` measures one branch of every CASE.
///
/// ⚠️ `leavetype` IS SEEDED PER CAMPUS, AND THAT IS NOT PADDING. The eight canonical types ship at
/// scope **(1,1,1)**, while `LeaveRepository.GetActiveLeaveTypes` matches tenant/school/campus
/// **EXACTLY** - so any other campus sees an EMPTY list and `employeeleaverequest.leavetypeid` has
/// nothing to point at. This is the documented \"a seeded row no query can reach is worse than a
/// missing one\" defect, and it is the same reason `HrModuleSeeder` seeds `attendancestatus` per
/// campus.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds leave requests is skipped unless Force is set.
/// </summary>
public sealed class HrLeaveSeeder : BaseSeeder
{
    public HrLeaveSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk leave load invalidates.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "leavetype", "employeeleaverequest"
    };

    /// <summary>
    /// The types a school actually offers. `requiresattachment` is false on all of them because a
    /// request whose type demands a document is refused without one by the Apply dialog, and this
    /// dataset is not here to exercise that rule.
    /// </summary>
    private static readonly (string Name, string Code, bool IsPaid)[] LeaveTypes =
    {
        ("Annual Leave", "AL", true),
        ("Sick Leave", "SL", true),
        ("Emergency Leave", "EL", true),
        ("Maternity Leave", "ML", true),
        ("Unpaid Leave", "UL", false),
        ("Hajj Leave", "HL", true),
        ("Study Leave", "STL", true),
        ("Compassionate Leave", "CL", true),
    };

    /// <summary>
    /// The three states `vw_leave_summary` translates into approved / pending / rejected days, plus
    /// the workflow's own intermediate. Weighted so the majority are settled - a campus of nothing
    /// but Pending rows makes the approved and rejected columns measure nothing.
    /// </summary>
    private static readonly string[] Statuses = { "Approved", "Approved", "Pending", "Rejected", "PendingApproval" };

    public async Task<LeaveSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, LeaveSeedOptions options, bool verbose = true)
    {
        var result = new LeaveSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM employeeleaverequest
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.LeaveRequests = (int)existing;
            if (verbose)
            {
                Console.WriteLine(
                    $"  Leave: campus {campusId} already holds {existing:N0} requests - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        if (options.Force && existing > 0)
        {
            await ClearTableAsync(conn, "employeeleaverequest", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "leavetype", tenantId, schoolId, campusId);

            if (verbose)
            {
                Console.WriteLine($"  Leave: campus {campusId} cleared for a forced re-seed");
            }
        }

        // ------------------------------------------------------------------
        // 1. Leave types for THIS campus (see the class comment). Read first so an existing set is
        //    reused rather than duplicated.
        // ------------------------------------------------------------------
        var typeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM leavetype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (typeIds.Count < options.LeaveTypesPerCampus)
        {
            for (var i = typeIds.Count; i < options.LeaveTypesPerCampus; i++)
            {
                var type = LeaveTypes[i % LeaveTypes.Length];
                var id = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO leavetype
                          (tenantid, schoolid, campusid, name, code, description,
                           ispaid, requiresattachment, requiresapproval, isactive,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @name, @code, @description,
                              @isPaid, false, true, true, 1, 1, @now, @now)
                      RETURNING id",
                    new
                    {
                        tenantId, schoolId, campusId,
                        name = type.Name,
                        code = $"PERF-{type.Code}-{campusId}",
                        description = "Seeded leave type for the leave-summary report dataset",
                        isPaid = type.IsPaid,
                        now
                    });
                typeIds.Add(id);
            }
        }
        result.LeaveTypes = typeIds.Count;

        // ------------------------------------------------------------------
        // 2. The employees a request belongs to. `employeeid` is a NOT NULL FK, and the view joins
        //    `employee` for the name and the code - so the requests are written against the campus's
        //    real staff, not invented ids.
        // ------------------------------------------------------------------
        var employeeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM employee
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (employeeIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId} holds no employee, so no leave request could reference one - " +
                "run HrModuleSeeder first.");
        }

        // ------------------------------------------------------------------
        // 3. The requests. Dates walk back through the current year so the report's date filters
        //    have something to narrow, and the day count is derived from the dates exactly as the
        //    service derives it.
        // ------------------------------------------------------------------
        var rows = new List<LeaveRow>();
        var yearStart = new DateTime(DateTime.Today.Year, 1, 1);

        for (var i = 0; i < employeeIds.Count; i++)
        {
            for (var r = 0; r < options.RequestsPerEmployee; r++)
            {
                var offset = (i * 7 + r * 61) % 330;
                var from = yearStart.AddDays(offset);
                if (from > DateTime.Today) from = DateTime.Today.AddDays(-1);

                var days = 1 + ((i + r) % 5);
                var to = from.AddDays(days - 1);
                var status = Statuses[(i + r) % Statuses.Length];

                rows.Add(new LeaveRow
                {
                    EmployeeId = employeeIds[i],
                    LeaveTypeId = typeIds[(i + r) % typeIds.Count],
                    FromDate = from,
                    ToDate = to,
                    TotalDays = days,
                    Status = status,
                    Reason = $"PERF LEAVE {campusId} {i + 1} {r + 1}",
                    // Only a settled request carries an approver - the columns are what an audit
                    // reads, and a Rejected row with an approval stamp is a contradiction.
                    ApprovedBy = status is "Approved" or "Rejected" ? 1 : (long?)null,
                    ApprovedDate = status is "Approved" or "Rejected" ? from.AddDays(-2) : (DateTime?)null,
                });
            }
        }

        var inserted = 0;
        const int batchSize = 1000;

        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var batch = rows.Skip(offset).Take(batchSize).ToList();

            const string sql = @"
                INSERT INTO employeeleaverequest
                    (tenantid, schoolid, campusid, employeeid, leavetypeid, fromdate, todate,
                     totaldays, reason, status, approvedby, approveddate,
                     createdby, modifiedby, createdon, modifiedon)
                SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                       unnest(@EmployeeIds), unnest(@LeaveTypeIds),
                       unnest(@FromDates)::date, unnest(@ToDates)::date, unnest(@TotalDays),
                       unnest(@Reasons), unnest(@Statuses),
                       unnest(@ApprovedBy)::bigint, unnest(@ApprovedDates)::timestamp,
                       0, 0, @now, @now";

            inserted += await conn.ExecuteAsync(sql, new
            {
                TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
                SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
                CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
                EmployeeIds = batch.Select(r => r.EmployeeId).ToArray(),
                LeaveTypeIds = batch.Select(r => r.LeaveTypeId).ToArray(),
                // `fromdate` / `todate` are `date` while the C# values are DateTime, so the cast is
                // required - `unnest` of a DateTime[] binds `timestamp` and PostgreSQL refuses the
                // narrower column instead of widening it.
                FromDates = batch.Select(r => r.FromDate.Date).ToArray(),
                ToDates = batch.Select(r => r.ToDate.Date).ToArray(),
                TotalDays = batch.Select(r => (decimal)r.TotalDays).ToArray(),
                Reasons = batch.Select(r => r.Reason).ToArray(),
                Statuses = batch.Select(r => r.Status).ToArray(),
                // Both are NULL unless the request is settled, and an ALL-NULL array is exactly the
                // shape Npgsql cannot type-infer - so the casts above are load-bearing, not tidy.
                ApprovedBy = batch.Select(r => r.ApprovedBy).ToArray(),
                ApprovedDates = batch.Select(r => r.ApprovedDate).ToArray(),
                now
            });

            if (verbose) LogProgress($"  Leave requests (campus {campusId})", inserted, rows.Count);
        }

        result.LeaveRequests = inserted;

        if (verbose)
        {
            Console.WriteLine(
                $"  Leave: campus {campusId} -> {result.LeaveTypes} types, " +
                $"{result.LeaveRequests:N0} requests");
        }

        return result;
    }

    private sealed class LeaveRow
    {
        public long EmployeeId { get; set; }
        public long LeaveTypeId { get; set; }
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public int TotalDays { get; set; }
        public string Status { get; set; } = "Pending";
        public string Reason { get; set; } = string.Empty;
        public long? ApprovedBy { get; set; }
        public DateTime? ApprovedDate { get; set; }
    }
}
