using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="MasterDataSeeder"/>. The defaults give one campus a normal set.</summary>
public sealed class MasterDataSeedOptions
{
    public int Sections { get; set; } = 6;
    public int Rooms { get; set; } = 8;
    public int Holidays { get; set; } = 12;
    public int Discounts { get; set; } = 6;
    public int TaxCodes { get; set; } = 5;
    public int CustomRoles { get; set; } = 4;
    public int SchoolEvents { get; set; } = 12;
    public bool Force { get; set; }
}

/// <summary>What one campus's master-data seed produced.</summary>
public sealed class MasterDataSeedResult
{
    public bool Skipped { get; set; }
    public int Sections { get; set; }
    public int Rooms { get; set; }
    public int Holidays { get; set; }
    public int Discounts { get; set; }
    public int TaxCodes { get; set; }
    public int ApprovalTemplates { get; set; }
    public int CustomRoles { get; set; }
    public int CampusSubjects { get; set; }
    public int Timetables { get; set; }
    public int SchoolEvents { get; set; }
}

/// <summary>
/// Seeds the CAMPUS-LEVEL MASTER DATA the perf dataset never created: `section`, `room`, `holiday`,
/// `discount`, `taxcode`, `approvaltemplate`, `roles` (custom only), `campussubject`, `timetable`
/// and `schoolevent`.
///
/// WHY THIS EXISTS
/// ---------------
/// The generic scope grids (`GenericRepository.GetAllAsync`) for these ten tables all reported
/// **SKIP**, honestly: the tables were empty in EVERY database here, so the specs had nothing to
/// measure and a SKIP reads as "not measured yet" for a grid the application ships. That is the
/// same defect the HR and six-report seeders were written for, one tier down - the unlock is DATA,
/// not a spec.
///
/// ⚠️ THIS IS NOT A PERFORMANCE FIXTURE, IT IS A PLAUSIBILITY ONE. Every table here holds TENS of
/// rows on a real campus, and a few dozen rows will never be slow. What the specs then prove is
/// that the grid is REACHABLE and its query is well-formed - which is why the specs carry
/// `MinVolume = 1`. Do not pad these counts to make a number look impressive; a school with 400
/// rooms is not a school.
///
/// ⚠️ THREE REAL CONSTRAINTS THIS SEEDER HAS TO RESPECT (all measured against `ayra_perf`):
///   * `ux_discount_tenantcode` and `ux_taxcode_tenantcode` are UNIQUE on (tenantid, code), so the
///     codes are stamped with the CAMPUS id - two campuses seeding the same tenant must not collide.
///   * `ux_approvaltemplate_one_active_per_module` is a PARTIAL unique on
///     (tenantid, schoolid, campusid, lower(modulename)) WHERE isactive - so at most ONE ACTIVE
///     template per module per campus. The seeder writes the eight canonical modules and nothing
///     more; a ninth would be refused, correctly.
///   * `discount`, `taxcode`, `section`, `room`, `holiday`, `roles`, `timetable` and `schoolevent`
///     have **no id default at all** (not even a sequence), unlike `approvaltemplate`,
///     `campussubject` and every modern table. Their ids are supplied from `MAX(id)+1`.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds any of these rows is skipped unless Force is
/// set. A forced re-seed clears its own scope first.
/// </summary>
public sealed class MasterDataSeeder : BaseSeeder
{
    public MasterDataSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a master-data load invalidates statistics for.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "section", "room", "holiday", "discount", "taxcode", "approvaltemplate", "roles",
        "campussubject", "timetable", "schoolevent"
    };

    /// <summary>
    /// The eight canonical approval modules, verbatim from `ApprovalTemplateRepository`'s module
    /// catalogue (`GET /approvalTemplate/modules`). A module name outside this set would be a
    /// template no single-select flow ever resolves.
    /// </summary>
    private static readonly string[] ApprovalModules =
    {
        "AttendanceCorrection", "EmployeeLeave", "EmployeeOvertime", "EmploymentContract",
        "Loan", "Payroll", "Refund", "Settlement"
    };

    private static readonly (string Name, int Type, decimal Value)[] Discounts =
    {
        ("Sibling Discount", 1, 10m),
        ("Staff Ward Discount", 1, 25m),
        ("Early Payment Discount", 1, 5m),
        ("Scholarship Discount", 1, 50m),
        ("Merit Discount", 2, 500m),
        ("Transport Adjustment", 3, -100m),
    };

    private static readonly (string Name, string Code, int Treatment)[] TaxCodes =
    {
        ("Standard Rated", "SR", 1),
        ("Zero Rated", "ZR", 2),
        ("Exempt", "EX", 3),
        ("Out Of Scope", "OS", 4),
        ("Reverse Charge", "RC", 1),
    };

    private static readonly string[] CustomRoles =
    {
        "Examinations Coordinator", "Transport Supervisor", "Library Assistant", "Store Keeper"
    };

    public async Task<MasterDataSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, MasterDataSeedOptions options, bool verbose = true)
    {
        var result = new MasterDataSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = 0;
        foreach (var table in TablesToAnalyze)
            existing += (int)await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {table} WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;

            // ⚠️ A SKIPPED CAMPUS MUST STILL REPORT WHAT IT HOLDS. Returning early with zeros makes a
            // caller's total describe what THIS RUN wrote rather than what the campus has - so a
            // fixture's own "did anything get seeded" assertion fails on a re-run against a campus
            // that is fully seeded, which reads as "the seeder did nothing" for a seeder that did
            // everything the first time. (`ExamsModuleSeeder` records the same lesson.)
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);

            if (verbose)
                Console.WriteLine($"  Master data: campus {campusId} already holds {existing:N0} rows - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        if (options.Force && existing > 0)
        {
            // `campussubject`/`timetable`/`approvaltemplate` have identity ids; the rest do not. The
            // scoped DELETE works on all ten because every one of them carries the scope triple.
            foreach (var table in TablesToAnalyze)
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);

            if (verbose)
                Console.WriteLine($"  Master data: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // The campus's own academic scaffolding - a timetable and a school event are both NOT NULL
        // against it, and inventing an id would leave a row nothing can join.
        // ------------------------------------------------------------------
        var classroomIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM classroom WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var academicYearIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM academicyear WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        // ⚠️ `terms` HAS NO SCOPE COLUMNS - it hangs off `academicyear`, so a campus's terms are the
        // terms of ITS years. Filtering on `terms.campusid` is a 42703 (`column "campusid" does not
        // exist`), and inventing a term id would give an event a term nothing can join.
        var terms = (await conn.QueryAsync<TermRow>(
            @"SELECT tm.id AS TermId, tm.academicyearid AS YearId
                FROM terms tm
                JOIN academicyear ay ON ay.id = tm.academicyearid
               WHERE ay.tenantid = @tenantId AND ay.schoolid = @schoolId AND ay.campusid = @campusId
               ORDER BY tm.id",
            new { tenantId, schoolId, campusId })).ToList();

        // ⚠️ EVERY id IN THIS FILE IS `GENERATED ALWAYS AS IDENTITY` (`is_identity = ALWAYS`, which
        // is why `information_schema.column_default` is empty for them - a null default does NOT
        // mean "supply one"). Writing `MAX(id)+1` answers
        //    428C9: cannot insert a non-DEFAULT value into column "id"
        // so every INSERT below OMITS `id` and lets the sequence assign it. `GetMaxIdAsync` is not
        // used here at all.

        // ------------------------------------------------------------------
        // 1. Sections - the campus's class divisions.
        // ------------------------------------------------------------------
        foreach (var name in new[] { "A", "B", "C", "D", "E", "F" }.Take(options.Sections))
        {
            await conn.ExecuteAsync(
                @"INSERT INTO section (tenantid, schoolid, campusid, name, description,
                                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @description, 1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, name = $"Section {name}", description = "Seeded section", now });
            result.Sections++;
        }

        // ------------------------------------------------------------------
        // 2. Rooms.
        // ------------------------------------------------------------------
        for (var i = 1; i <= options.Rooms; i++)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO room (tenantid, schoolid, campusid, name, description,
                                    createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @description, 1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, name = $"Room {i:00}", description = "Seeded room", now });
            result.Rooms++;
        }

        // ------------------------------------------------------------------
        // 3. Holidays - walked forward through the current year so a date filter has something to
        //    narrow, and so `startdate`/`enddate` are ordered the way the grid shows them.
        // ------------------------------------------------------------------
        var yearStart = new DateTime(DateTime.Today.Year, 1, 1);
        for (var i = 0; i < options.Holidays; i++)
        {
            var start = yearStart.AddDays(14 + i * 29);
            await conn.ExecuteAsync(
                @"INSERT INTO holiday (tenantid, schoolid, campusid, name, description, startdate, enddate,
                                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @description, @start, @end, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId,
                    name = $"PERF Holiday {i + 1}", description = "Seeded holiday",
                    start, end = start.AddDays(1), now
                });
            result.Holidays++;
        }

        // ------------------------------------------------------------------
        // 4. Discounts. The CODE is campus-stamped because (tenantid, code) is UNIQUE.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.Discounts && i < Discounts.Length; i++)
        {
            var d = Discounts[i];
            await conn.ExecuteAsync(
                @"INSERT INTO discount (tenantid, schoolid, campusid, name, code, description,
                                        discounttype, value, isactive, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @code, @description,
                          @type, @value, true, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, name = d.Name,
                    code = $"PERF-D{i + 1}-{campusId}", type = d.Type, value = d.Value,
                    description = "Seeded discount type", now
                });
            result.Discounts++;
        }

        // ------------------------------------------------------------------
        // 5. Tax codes. `istaxable` follows the treatment: only Standard Rated and Reverse Charge
        //    actually carry tax, which is the rule the fee engine reads.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.TaxCodes && i < TaxCodes.Length; i++)
        {
            var t = TaxCodes[i];
            await conn.ExecuteAsync(
                @"INSERT INTO taxcode (tenantid, schoolid, campusid, name, code, description,
                                       taxtreatment, istaxable, isactive, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @code, @description,
                          @treatment, @isTaxable, true, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, name = t.Name,
                    code = $"PERF-{t.Code}-{campusId}", treatment = t.Treatment,
                    isTaxable = t.Treatment == 1, description = "Seeded tax code", now
                });
            result.TaxCodes++;
        }

        // ------------------------------------------------------------------
        // 6. Approval templates - ONE ACTIVE per module per campus (the partial unique index), so
        //    the eight canonical modules and no more.
        // ------------------------------------------------------------------
        foreach (var module in ApprovalModules)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO approvaltemplate (tenantid, schoolid, campusid, modulename, name,
                                                isactive, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @module, @name, true, 1, 1, @now, @now)",
                new { tenantId, schoolId, campusId, module, name = $"{module} Approval (seeded)", now });
            result.ApprovalTemplates++;
        }

        // ------------------------------------------------------------------
        // 7. Roles - CUSTOM only (`issystemrole = false`), because that is the set the roles grid
        //    shows. The eight seeded system roles are deliberately not duplicated here.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.CustomRoles && i < CustomRoles.Length; i++)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO roles (tenantid, schoolid, campusid, name, description,
                                     isactive, issystemrole, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @name, @description,
                          true, false, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId,
                    name = $"{CustomRoles[i]}", description = "Seeded custom role", now
                });
            result.CustomRoles++;
        }

        // ------------------------------------------------------------------
        // 8. Campus subjects - one per SUBJECT THE SCHOOL OWNS. `subject` is school-scoped, so the
        //    campus "offers" the school's catalogue; inventing a subject id would make a row whose
        //    join resolves to nothing, which is the defect this seeder exists to avoid.
        // ------------------------------------------------------------------
        var subjectIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM subject WHERE tenantid = @tenantId AND schoolid = @schoolId ORDER BY id",
            new { tenantId, schoolId })).ToList();

        foreach (var subjectId in subjectIds)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO campussubject (tenantid, schoolid, campusid, subjectid, isoffered,
                                             customname, customcode, customcategory, isactive,
                                             createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @subjectId, true,
                          @customName, @customCode, 1, true, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId, subjectId,
                    customName = $"PERF Subject {subjectId}",
                    customCode = $"PERF-{campusId}-{subjectId}",
                    now
                });
            result.CampusSubjects++;
        }

        // ------------------------------------------------------------------
        // 9. Timetables - one per classroom per academic year the campus owns. `classroomid` and
        //    `academicyearid` are both NOT NULL FKs, and a campus with no classroom gets none
        //    (rather than an invented link).
        // ------------------------------------------------------------------
        foreach (var classroomId in classroomIds)
        {
            foreach (var yearId in academicYearIds.Take(3))
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO timetable (tenantid, schoolid, campusid, academicyearid, classroomid,
                                             status, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @yearId, @classroomId,
                              'Draft', 1, 1, @now, @now)",
                    new { tenantId, schoolId, campusId, yearId, classroomId, now });
                result.Timetables++;
            }
        }

        // ------------------------------------------------------------------
        // 10. School events. Needs a term AND an academic year - both read from the campus, and
        //     skipped rather than invented when the campus has none.
        // ------------------------------------------------------------------
        if (terms.Count > 0)
        {
            for (var i = 0; i < options.SchoolEvents; i++)
            {
                var term = terms[i % terms.Count];
                var start = DateTime.Today.AddDays(-90 + i * 15);
                await conn.ExecuteAsync(
                    @"INSERT INTO schoolevent (tenantid, schoolid, campusid, title,
                                               academicyearid, termid, startdate, enddate, starttime, endtime,
                                               creatortype, scope, ispaidevent, amount, duedate, isactive,
                                               eventtype, isoptional, status,
                                               createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @title,
                              @yearId, @termId, @start, @end, @startTime::time, @endTime::time,
                              1, 1, false, 0, @dueDate, true,
                              1, false, 2,
                              1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId, campusId,
                        title = $"PERF School Event {i + 1}",
                        // ⚠️ The year comes from the TERM, not from an independent list: a term
                        // belongs to exactly one year, and pairing them by index would write an
                        // event whose (year, term) pair does not exist.
                        yearId = term.YearId,
                        termId = term.TermId,
                        start, end = start,
                        // `starttime`/`endtime` are `time`, the C# values are DateTime - the explicit
                        // `::time` cast is the one `ExamsModuleSeeder` established for this exact
                        // one-sided type (otherwise: 42804, expression is of type timestamp).
                        startTime = new DateTime(2000, 1, 1, 9, 0, 0),
                        endTime = new DateTime(2000, 1, 1, 12, 0, 0),
                        dueDate = start.AddDays(-7), now
                    });
                result.SchoolEvents++;
            }
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  Master data: campus {campusId} -> {result.Sections} sections, {result.Rooms} rooms, " +
                $"{result.Holidays} holidays, {result.Discounts} discounts, {result.TaxCodes} tax codes, " +
                $"{result.ApprovalTemplates} approval templates, {result.CustomRoles} custom roles, " +
                $"{result.CampusSubjects} campus subjects, {result.Timetables} timetables, {result.SchoolEvents} events");
        }

        return result;
    }

    /// <summary>
    /// Fills <paramref name="result"/> from what the campus ALREADY holds, so a skipped campus is
    /// reported the same way as a freshly seeded one. `roles` is counted with `issystemrole = false`
    /// because that is the set the seeder writes AND the set the roles grid lists.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, MasterDataSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table}"
                + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.Sections = await ScopedAsync("section");
        result.Rooms = await ScopedAsync("room");
        result.Holidays = await ScopedAsync("holiday");
        result.Discounts = await ScopedAsync("discount");
        result.TaxCodes = await ScopedAsync("taxcode");
        result.ApprovalTemplates = await ScopedAsync("approvaltemplate");
        result.CampusSubjects = await ScopedAsync("campussubject");
        result.Timetables = await ScopedAsync("timetable");
        result.SchoolEvents = await ScopedAsync("schoolevent");

        result.CustomRoles = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM roles
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND issystemrole = false",
            new { tenantId, schoolId, campusId });
    }

    /// <summary>A campus term and the academic year it hangs off (`terms` has no scope columns of its own).</summary>
    private sealed class TermRow
    {
        public long TermId { get; set; }
        public long YearId { get; set; }
    }
}
