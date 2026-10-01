using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="EmptyGridTablesSeeder"/>.</summary>
public sealed class EmptyGridTablesSeedOptions
{
    /// <summary>
    /// Parents per campus. The `parent` grid is PAGED and its predicate is an `INNER JOIN users` on
    /// the SAME scope triple, so each row needs its own LOGIN at this scope - which is what the
    /// seeder creates. A campus holds tens of parents; three is the honest size for a reachability
    /// spec, and every one of them carries a DISTINCT user so the join is really exercised.
    /// </summary>
    public int ParentsPerCampus { get; set; } = 3;

    /// <summary>
    /// Grading schemes per campus, plus <see cref="RulesPerScheme"/> rules each. Two, so the grid's
    /// status/name column has something to differ over and the LEFT JOIN to `gradingrule` is real.
    /// </summary>
    public int GradingSchemesPerCampus { get; set; } = 2;

    /// <summary>
    /// `gradingrule` rows per scheme. ⚠️ TWO IS DELIBERATE: the scheme's PAGE statement LEFT JOINs
    /// `gradingrule` while its COUNT does not, so with rules the page returns scheme x rule rows while
    /// the count reports schemes - a real row-multiplication the spec records.
    /// </summary>
    public int RulesPerScheme { get; set; } = 2;

    /// <summary>
    /// Fee assignments per campus, one per DISTINCT enrolment (the partial unique index allows one
    /// ACTIVE row per enrolment). A campus assigns a structure to every enrolled student, so a handful
    /// is a page, not the population - the spec's volume is what matters.
    /// </summary>
    public int FeeAssignmentsPerCampus { get; set; } = 6;

    /// <summary>
    /// Student fee discounts per campus, spread across `DiscountApprovalStatus` (Pending/Approved/
    /// Rejected) because the grid separates them.
    /// </summary>
    public int FeeDiscountsPerCampus { get; set; } = 6;

    /// <summary>
    /// Late-fee charges per campus, spread across `LateFeeChargeStatus` (Posted/Paid/Waived/Credited/
    /// Cancelled) - the same reason, and the one Waived row carries a waive stamp so the screen's
    /// waived branch is not blank.
    /// </summary>
    public int LateFeeChargesPerCampus { get; set; } = 5;

    /// <summary>Re-seed even when the campus already holds parents.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's empty-grid-tables seed produced.</summary>
public sealed class EmptyGridTablesSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int Parents { get; set; }
    public int ParentUsers { get; set; }
    public int GradingSchemes { get; set; }
    public int GradingRules { get; set; }
    public int FeeAssignments { get; set; }
    public int FeeDiscounts { get; set; }
    public int LateFeeSettings { get; set; }
    public int LateFeeAssessments { get; set; }
    public int LateFeeCharges { get; set; }
}

/// <summary>
/// Seeds the FIVE grid-driving tables that hold ZERO rows on the measured campus (15) while their
/// screens ship: `parent`, `gradingscheme` (+ `gradingrule`), `studentfeeassignment`,
/// `studentfeediscount` and `latefeecharges` (+ the `latefeesettings` / `latefeeassessments` rows its
/// FKs demand).
///
/// WHY THIS EXISTS
/// ---------------
/// A spec over an EMPTY table reports SKIP, which reads as "not measured yet" for a screen the
/// application ships - the failure this tool exists to prevent. All five are PAGED server-side grids
/// (`school.parents`/`parent.html`, `school.grading.html`, `student.fee.assignment.html`,
/// `student.discount.html`, `student.late.fee.html`), so with no data their statements are never
/// measured at all.
///
/// ⚠️ WHY THE TABLES ARE EMPTY IS NOT ONE STORY, AND EACH IS RESOLVED RATHER THAN INVENTED.
///   * `parent` - campus 15 holds ONE `parent` row and its login lives at a DIFFERENT scope, while
///     the grid's predicate is an `INNER JOIN users` on the SAME (tenant, school, campus) triple. So
///     the row exists and the grid is EMPTY anyway. The seeder therefore creates the LOGIN too, at
///     this campus, which is the only way the row can be reachable.
///   * `gradingscheme` - genuinely 0 rows on this campus (its one row lives on campus 1).
///   * `studentfeeassignment` / `studentfeediscount` / `latefeecharges` - 0 on this campus while their
///     prerequisites (enrolments, fee structure, discounts, invoices) all EXIST here, so the seeder
///     resolves them from the campus's own rows.
///
/// ⚠️ THE PARTIAL UNIQUE INDEXES ARE THE TRAP, and every one of them is honoured:
///   * `ux_studentfeeassignment_activeenrollment (tenantid, studentenrollmentid) WHERE status = 1` -
///     ONE active assignment per enrolment, so the rows walk DISTINCT enrolments.
///   * `ux_latefeecharges_assessment (assessmentid)` - one charge per assessment, so each charge gets
///     its own `latefeeassessments` row rather than sharing one.
///   * `parent` has NO unique index on `userid`, but a parent is a person with ONE login, so each row
///     takes its own user.
///
/// ⚠️ TWO SCHEMA TRAPS THAT COST CYCLES ELSEWHERE AND ARE OBSERVED HERE:
///   * `parent.id`, `gradingscheme.id`, `gradingrule.id`, `studentfeediscount.id`,
///     `latefeesettings.id` and `users.id` are `GENERATED ALWAYS AS IDENTITY` and refuse a supplied
///     id (`428C9`) - while `studentfeeassignment.id`, `latefeecharges.id` and `latefeeassessments.id`
///     are sequence-backed. The seeder therefore OMITS the id everywhere and reads it back with
///     `RETURNING id`, which is correct for both.
///   * `latefeecharges.chargedate`, `latefeeassessments.assessmentdate`/`.duedate` are
///     `timestamp WITH time zone`, while their audit columns are not always - so the instants are
///     passed as `DateTime.UtcNow` (Kind=Utc), which is what a `timestamptz` bind requires.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds parents is skipped unless
/// <see cref="EmptyGridTablesSeedOptions.Force"/> is set, and the skip path still READS the counts so
/// the numbers it reports are real rather than zero.
/// </summary>
public sealed class EmptyGridTablesSeeder : BaseSeeder
{
    public EmptyGridTablesSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a load here invalidates, so a fixture can ANALYZE them. `users`, `studentenrollment`,
    /// `student`, `feestructure`, `discount` and `invoices` are included because the five grids JOIN
    /// them - fresh counts on the five fact tables alone would leave the planner reasoning about the
    /// pre-seed join sizes, which is the "a performance reading on un-analyzed statistics is not a
    /// reading" rule.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "parent", "users", "gradingscheme", "gradingrule",
        "studentfeeassignment", "studentfeediscount",
        "latefeecharges", "latefeeassessments", "latefeesettings",
        "studentenrollment", "student", "feestructure", "discount", "invoices",
    };

    /// <summary>
    /// `DiscountApprovalStatus`: Pending 1 / Approved 2 / Rejected 3. Rotated so every branch of the
    /// discount grid's status column and its tab counts has a row.
    /// </summary>
    private static readonly short[] DiscountApprovals = { 1, 2, 2, 3, 2, 1 };

    /// <summary>
    /// `LateFeeChargeStatus`: Posted 1 / Paid 2 / Waived 3 / Credited 4 / Cancelled 5 - one per status
    /// the grid's tabs separate, and the order the enum declares them.
    /// </summary>
    private static readonly short[] ChargeStatuses = { 1, 2, 3, 4, 5 };

    /// <summary>
    /// `SystemRoles.Parent` (the enum in `SchoolResourceServer/StaticEntities/SystemRoles.cs`). Spelled
    /// as a literal because this project does not reference the API - and `roles` is a GLOBAL catalogue
    /// indexed by id, so the grant is keyed on the role's id alone.
    /// </summary>
    private const int ParentRoleId = 5;

    public async Task<EmptyGridTablesSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId,
        EmptyGridTablesSeedOptions options, bool verbose = false)
    {
        var result = new EmptyGridTablesSeedResult();
        await using var conn = await OpenConnectionAsync();

        var existing = await GetRowCountAsync(conn, "parent", tenantId, schoolId, campusId);
        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} already holds {existing} parent(s) - pass Force to re-seed";
            // ⚠️ THE SKIP PATH STILL READS, so the numbers a fixture prints are the campus's real
            // counts rather than five zeroes that look like a failed seed.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
                Console.WriteLine($"  Empty grid tables: {result.SkipReason}");
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 0. The prerequisites, resolved from the campus's OWN rows. A missing one is REPORTED - a
        //    campus with no enrolment cannot assign a fee structure, and a campus with no invoice
        //    cannot carry a late-fee charge.
        // ------------------------------------------------------------------
        var enrollments = (await conn.QueryAsync<(long Id, long StudentId, long AcademicYearId)>(
            @"SELECT se.id AS Id, se.studentid AS StudentId, se.academicyearid AS AcademicYearId
                FROM studentenrollment se
               WHERE se.tenantid = @tenantId AND se.schoolid = @schoolId AND se.campusid = @campusId
                 AND se.studentstatus NOT IN (4,5,6,7)
               ORDER BY se.id",
            new { tenantId, schoolId, campusId })).ToList();

        var feeStructureId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM feestructure
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var discountIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM discount
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var invoices = (await conn.QueryAsync<(long Id, long StudentId)>(
            @"SELECT id AS Id, studentid AS StudentId FROM invoices
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (enrollments.Count == 0 || feeStructureId is null or 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} has {enrollments.Count} live enrolment(s) and " +
                $"{(feeStructureId is null or 0 ? "no fee structure" : "a fee structure")} - a fee " +
                "assignment needs both (`studentfeeassignment.studentenrollmentid` and `.feestructureid` " +
                "are FKs). Run the dataset seeder and the fees fixture first.";
            if (verbose) Console.WriteLine($"  Empty grid tables: {result.SkipReason}");
            return result;
        }

        if (discountIds.Count == 0 || invoices.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} has {discountIds.Count} discount(s) and {invoices.Count} invoice(s) - " +
                "a student discount needs a discount type and a late-fee charge needs an invoice. Run the " +
                "master-data seeder and the fees module seeder first.";
            if (verbose) Console.WriteLine($"  Empty grid tables: {result.SkipReason}");
            return result;
        }

        var academicYearId = enrollments[0].AcademicYearId;

        // ------------------------------------------------------------------
        // 1. A forced re-seed clears first, parents before the rows that reference them. The children
        //    that carry no scope of their own (`gradingrule` under its scheme) go through their parent.
        // ------------------------------------------------------------------
        if (options.Force && existing > 0)
        {
            // ⚠️ EVERY DELETE HERE IS SCOPED BY THIS SEEDER'S OWN STAMP, NEVER BY SCOPE. `CampusesAsync`
            // returns a spread of campuses INCLUDING ones that already carry real data (a campus with
            // fee assignments of its own, or one already running the late-fee engine), so a scoped
            // `DELETE FROM studentfeeassignment WHERE …campusid = @campusId` would destroy rows this
            // seeder neither wrote nor owns. The stamps are the same ones `ReadCountsAsync` counts by.
            var args = new { tenantId, schoolId, campusId };

            // gradingrule carries no scope of its own, so it goes through its scheme's stamp.
            await conn.ExecuteAsync(
                "DELETE FROM gradingrule WHERE gradingschemeid IN (SELECT id FROM gradingscheme " +
                "WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId " +
                "AND code LIKE 'PERF-GS-%')", args);
            await conn.ExecuteAsync(
                "DELETE FROM gradingscheme WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                "AND campusid = @campusId AND code LIKE 'PERF-GS-%'", args);

            // The late-fee children hang off the setting this seeder created, which is the only handle
            // that separates them from the charges the ENGINE writes itself.
            const string mySetting =
                "settingid IN (SELECT id FROM latefeesettings WHERE tenantid = @tenantId " +
                "AND schoolid = @schoolId AND campusid = @campusId AND name LIKE 'Perf Late Fee %')";
            await conn.ExecuteAsync(
                $"DELETE FROM latefeecharges WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                $"AND campusid = @campusId AND assessmentid IN (SELECT id FROM latefeeassessments " +
                $"WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND {mySetting})",
                args);
            await conn.ExecuteAsync(
                $"DELETE FROM latefeeassessments WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                $"AND campusid = @campusId AND {mySetting}", args);
            await conn.ExecuteAsync(
                "DELETE FROM latefeesettings WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                "AND campusid = @campusId AND name LIKE 'Perf Late Fee %'", args);

            await conn.ExecuteAsync(
                "DELETE FROM studentfeediscount WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                "AND campusid = @campusId AND remarks LIKE 'Perf dataset - a student fee discount%'", args);
            await conn.ExecuteAsync(
                "DELETE FROM studentfeeassignment WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                "AND campusid = @campusId AND remarks LIKE 'Perf dataset - the fee structure%'", args);

            await conn.ExecuteAsync(
                "DELETE FROM parent WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                "AND campusid = @campusId AND nic LIKE 'PERF-PAR-%'", args);
            // The parent LOGINS go by their own address stamp, never by scope: a scoped DELETE on
            // `users` would take every real user of the campus with it.
            await conn.ExecuteAsync(
                "DELETE FROM users WHERE tenantid = @tenantId AND schoolid = @schoolId " +
                "AND campusid = @campusId AND email LIKE 'perf.parent.%@scube.test'", args);

            if (verbose)
                Console.WriteLine($"  Empty grid tables: campus {campusId} cleared (stamped rows only) " +
                                  "for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 2. `parent` + the LOGIN it takes its name from. The grid's predicate is the `users` INNER
        //    JOIN on the SAME scope triple, so a login at another scope is invisible - which is
        //    exactly why this campus's one pre-existing parent row never shows.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.ParentsPerCampus; i++)
        {
            var email = $"perf.parent.{campusId}.{i + 1}@scube.test";

            // ⚠️ LOOK THE LOGIN UP FIRST, so a re-seed of a campus whose parent rows were cleaned
            // separately does not stack a second login on the same address. `users` carries no unique
            // index on email (the app legitimises several rows per address - one per scope), so a
            // duplicate would be silent rather than refused.
            var userId = await conn.ExecuteScalarAsync<long?>(
                @"SELECT id FROM users
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND email = @email ORDER BY id LIMIT 1",
                new { tenantId, schoolId, campusId, email });

            if (userId is null or 0)
            {
                userId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO users
                          (tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon,
                           firstname, lastname, email, password, confirmemail, confirmmobile, mobile, status)
                      VALUES (@tenantId, @schoolId, @campusId, 1, 1, @now, @now,
                              @firstName, @lastName, @email, @password, true, true, @mobile, 'Active')
                      RETURNING id",
                    new
                    {
                        tenantId, schoolId, campusId, now,
                        firstName = $"Perf Parent {i + 1}",
                        lastName = $"C{campusId}",
                        email,
                        // The legacy plaintext column, which is how the e2e baseline seeds too - the
                        // hash is written on first successful login. A seeder does not reimplement PBKDF2.
                        password = "Perf-Parent-2026!",
                        mobile = $"050{campusId:D3}{i + 1000:D4}",
                    });
            }

            // ⚠️ THE PARENT ROLE GRANT IS PART OF THE APP'S OWN CREATE PATH, NOT DECORATION.
            // `ParentController.Post` calls `UserRoleRepository.SetObject((int)SystemRoles.Parent, userId)`
            // for every parent it creates, and the grid PROJECTS that role name - so a parent seeded
            // without a grant is a state the application cannot produce, and renders a blank role column.
            // `userrole` carries NO scope columns (its scope is its user's), so it is keyed purely on
            // (userId, roleId) and the guard is on that pair.
            var hasGrant = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM userrole WHERE userid = @userId AND roleid = @roleId AND isactive = true",
                new { userId = userId!.Value, roleId = ParentRoleId });

            if (hasGrant == 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO userrole (userid, roleid, isactive, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@userId, @roleId, true, 1, 1, @now, @now)",
                    new { userId = userId.Value, roleId = ParentRoleId, now });
            }

            result.ParentUsers++;

            // ⚠️ `nic` IS THE SEEDER'S OWN STAMP - the fixture scopes its assertions by it, so the
            // campus's pre-existing parent rows (campus 15 holds one, at ANOTHER scope, which the grid
            // legitimately cannot see) are neither seeded over nor asserted on.
            var nic = $"PERF-PAR-{campusId}-{i + 1}";
            var already = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM parent
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND nic = @nic",
                new { tenantId, schoolId, campusId, nic });

            if (already == 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO parent
                          (nic, address, userid, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@nic, @address, @userId, @tenantId, @schoolId, @campusId,
                              1, 1, @now, @now)",
                    new
                    {
                        nic,
                        address = $"Perf Street {i + 1}, Campus {campusId}",
                        userId = userId!.Value, tenantId, schoolId, campusId, now,
                    });
            }

            result.Parents++;
        }

        // ------------------------------------------------------------------
        // 3. `gradingscheme` + its `gradingrule` rows. The rules are what the grid's PAGE statement
        //    LEFT JOINs while its COUNT does not - so the page multiplies rows by rule count and the
        //    total does not, which is a real shape the spec records rather than a tidy-up.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.GradingSchemesPerCampus; i++)
        {
            var schemeId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO gradingscheme
                      (name, code, description, calculationmethod, roundingmethod, passingscore,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@name, @code, @description, @calculationMethod, @roundingMethod, @passingScore,
                          @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    name = $"Perf Grading Scheme {i + 1}",
                    code = $"PERF-GS-{campusId}-{i + 1}",
                    description = "Perf dataset - a campus grading scheme.",
                    calculationMethod = "Percentage",
                    roundingMethod = "None",
                    passingScore = 50m,
                    tenantId, schoolId, campusId, now,
                });
            result.GradingSchemes++;

            for (var r = 0; r < options.RulesPerScheme; r++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO gradingrule
                          (gradingschemeid, grade, minimumscore, maximumscore, gradepoint, displayorder, color)
                      VALUES (@schemeId, @grade, @min, @max, @points, @order, @color)",
                    new
                    {
                        schemeId,
                        grade = r == 0 ? "A" : "B",
                        min = r == 0 ? 80m : 60m,
                        max = r == 0 ? 100m : 79m,
                        points = r == 0 ? 4m : 3m,
                        order = (short)(r + 1),
                        color = r == 0 ? "#16a34a" : "#2563eb",
                    });
                result.GradingRules++;
            }
        }

        // ------------------------------------------------------------------
        // 4. `studentfeeassignment` - ONE active row per DISTINCT enrolment (the partial unique index
        //    is on (tenantid, studentenrollmentid) WHERE status = 1), so the rows walk the campus's
        //    enrolments rather than wrapping.
        // ------------------------------------------------------------------
        var assignedEnrollments = new List<(long EnrollmentId, long StudentId)>();
        foreach (var enrollment in enrollments.Take(options.FeeAssignmentsPerCampus))
        {
            await conn.ExecuteAsync(
                @"INSERT INTO studentfeeassignment
                      (tenantid, schoolid, campusid, studentenrollmentid, feestructureid,
                       startdate, status, remarks, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @enrollmentId, @feeStructureId,
                          @startDate, @status, @remarks, 1, 1, @now, @now)",
                new
                {
                    tenantId, schoolId, campusId,
                    enrollmentId = enrollment.Id,
                    feeStructureId = feeStructureId!.Value,
                    startDate = now,
                    // `StudentFeeAssignmentStatus.Active = 1`. The grid and the invoice engine both
                    // read the ACTIVE row, so an all-Inactive seed would be invisible.
                    status = (short)1,
                    remarks = "Perf dataset - the fee structure assigned to this enrolment.",
                    now,
                });
            assignedEnrollments.Add((enrollment.Id, enrollment.StudentId));
            result.FeeAssignments++;
        }

        // ------------------------------------------------------------------
        // 5. `studentfeediscount`, spread across `DiscountApprovalStatus`. Its read INNER JOINs
        //    `student` AND `discount` and LEFT JOINs the assignment -> structure -> year chain, so a
        //    row whose student or discount does not resolve is invisible on the screen.
        // ------------------------------------------------------------------
        for (var i = 0; i < options.FeeDiscountsPerCampus && i < assignedEnrollments.Count; i++)
        {
            var (enrollmentId, studentId) = assignedEnrollments[i % assignedEnrollments.Count];
            var approval = DiscountApprovals[i % DiscountApprovals.Length];
            var isAmount = i % 2 == 1;

            // The assignment this discount hangs off, when the campus has one - the read LEFT JOINs it,
            // so a NULL is legal and is what a discount raised without an assignment looks like.
            var assignmentId = await conn.ExecuteScalarAsync<long?>(
                @"SELECT id FROM studentfeeassignment
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND studentenrollmentid = @enrollmentId AND status = 1
                   ORDER BY id LIMIT 1",
                new { tenantId, schoolId, campusId, enrollmentId });

            await conn.ExecuteAsync(
                @"INSERT INTO studentfeediscount
                      (studentid, discountid, studentfeeassignmentid, feestructuredetailid,
                       percentage, amount, startdate, enddate, reason,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon,
                       approvalstatus, approvedby, approvedon, remarks)
                  VALUES (@studentId, @discountId, @assignmentId, NULL,
                          @percentage, @amount, @startDate, @endDate, @reason,
                          @tenantId, @schoolId, @campusId, 1, 1, @now, @now,
                          @approvalStatus, @approvedBy, @approvedOn, @remarks)",
                new
                {
                    studentId,
                    discountId = discountIds[i % discountIds.Count],
                    assignmentId,
                    // ⚠️ EXACTLY ONE OF THE TWO IS SET, mirroring the screen: a percentage discount or
                    // a flat amount, never both, or the money paths double-count the grant.
                    percentage = isAmount ? (short?)null : (short)(5 + i),
                    amount = isAmount ? 100m + (i * 25m) : (decimal?)null,
                    startDate = now.AddDays(-30),
                    endDate = now.AddDays(150),
                    reason = $"Perf discount {i + 1}",
                    tenantId, schoolId, campusId, now,
                    approvalStatus = approval,
                    // Mirrors the status: an approved row names an approver, an unapproved one is NULL,
                    // so the screen's "Approved by" cell is not blank on the rows that claim approval.
                    approvedBy = approval == 2 ? (long?)1 : null,
                    approvedOn = approval == 2 ? (DateTime?)now : null,
                    remarks = "Perf dataset - a student fee discount.",
                });
            result.FeeDiscounts++;
        }

        // ------------------------------------------------------------------
        // 6. `latefeesettings` (ONE row per campus - the screen is a form, not a grid) then, per
        //    charge, its OWN `latefeeassessments` row (the unique index is on the charge's assessment,
        //    so assessments are never shared) and the charge itself.
        // ------------------------------------------------------------------
        var settingId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM latefeesettings
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        if (settingId is null or 0)
        {
            settingId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO latefeesettings
                      (graceperioddays, maxlatefee, isactive, tenantid, schoolid, campusid,
                       createdby, modifiedby, createdon, modifiedon,
                       name, description, calculationtype, fixedamount, percentage,
                       minimumoutstandingamount, calculationbase, frequency)
                  VALUES (@graceDays, @maxLateFee, true, @tenantId, @schoolId, @campusId,
                          1, 1, @now, @now,
                          @name, @description, 2, 0, @percentage,
                          @minOutstanding, 1, 1)
                  RETURNING id",
                new
                {
                    graceDays = 5,
                    maxLateFee = 500m,
                    tenantId, schoolId, campusId, now,
                    name = $"Perf Late Fee {campusId}",
                    description = "Perf dataset - the campus late-fee policy.",
                    // `calculationtype` 2 = Percentage (the enum's Percentage branch), 5% on the
                    // outstanding amount, capped by maxlatefee.
                    percentage = 5m,
                    minOutstanding = 0m,
                });
            result.LateFeeSettings++;
        }

        for (var i = 0; i < options.LateFeeChargesPerCampus; i++)
        {
            var invoice = invoices[i % invoices.Count];
            var status = ChargeStatuses[i % ChargeStatuses.Length];
            var baseAmount = 1000m + (i * 250m);
            var amount = Math.Round(baseAmount * 0.05m, 2);

            var assessmentId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO latefeeassessments
                      (settingid, invoiceid, studentid, academicyearid,
                       assessmentdate, duedate, graceperioddays, frequency, period, calculationbase,
                       baseamount, amount, status, tenantid, schoolid, campusid,
                       createdon, modifiedon, createdby, modifiedby)
                  VALUES (@settingId, @invoiceId, @studentId, @academicYearId,
                          @assessmentDate, @dueDate, 5, 1, @period, 1,
                          @baseAmount, @amount, @status, @tenantId, @schoolId, @campusId,
                          @now, @now, 1, 1)
                  RETURNING id",
                new
                {
                    settingId,
                    invoiceId = invoice.Id,
                    studentId = invoice.StudentId,
                    academicYearId,
                    // ⚠️ `timestamptz` COLUMNS: these must be Kind=Utc, which is why every instant
                    // here is DateTime.UtcNow rather than an Unspecified value.
                    assessmentDate = now.AddDays(-(30 + i)),
                    dueDate = now.AddDays(-(10 + i)),
                    period = i + 1,
                    baseAmount,
                    amount,
                    // `LateFeeAssessmentStatus.Posted = 2` - the charge below exists because the
                    // assessment was posted.
                    status = (short)2,
                    tenantId, schoolId, campusId, now,
                });
            result.LateFeeAssessments++;

            // `LateFeeChargeStatus` 3 = Waived, which is the only status that carries a waive stamp -
            // so the screen's waived columns are derived from the row rather than left blank.
            var waived = status == 3;

            await conn.ExecuteAsync(
                @"INSERT INTO latefeecharges
                      (assessmentid, originalinvoiceid, originalinvoicelineid, invoiceid, studentid,
                       academicyearid, chargedate, baseamount, amount, taxamount, totalamount, status,
                       tenantid, schoolid, campusid, createdon, modifiedon, createdby, modifiedby,
                       waivedby, waivedon, waiveremarks)
                  VALUES (@assessmentId, @invoiceId, NULL, @invoiceId, @studentId,
                          @academicYearId, @chargeDate, @baseAmount, @amount, 0, @totalAmount, @status,
                          @tenantId, @schoolId, @campusId, @now, @now, 1, 1,
                          @waivedBy, @waivedOn, @waiveRemarks)",
                new
                {
                    assessmentId,
                    invoiceId = invoice.Id,
                    studentId = invoice.StudentId,
                    academicYearId,
                    chargeDate = now.AddDays(-(20 + i)),
                    baseAmount,
                    amount,
                    totalAmount = amount,
                    status,
                    tenantId, schoolId, campusId, now,
                    waivedBy = waived ? (long?)1 : 0L,
                    waivedOn = waived ? (DateTime?)now : null,
                    waiveRemarks = waived ? "Perf dataset - waived as a goodwill credit." : null,
                });
            result.LateFeeCharges++;
        }

        await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);

        if (verbose)
        {
            Console.WriteLine(
                $"  Empty grid tables: campus {campusId} -> parent {result.Parents} (+{result.ParentUsers} " +
                $"login(s)), grading scheme {result.GradingSchemes} (+{result.GradingRules} rule(s)), " +
                $"fee assignment {result.FeeAssignments}, fee discount {result.FeeDiscounts}, " +
                $"late-fee settings {result.LateFeeSettings}, assessment {result.LateFeeAssessments}, " +
                $"charge {result.LateFeeCharges}");
        }

        return result;
    }

    /// <summary>
    /// THIS SEEDER'S OWN contribution to each table, counted by the STAMP it writes (never by scope).
    /// Read on BOTH paths (seeded and skipped), so a skipped campus reports what this seeder already put
    /// there instead of five zeroes that read as a failed seed.
    ///
    /// ⚠️ COUNT BY STAMP, NOT BY SCOPE, AND THE REASON IS MEASURED: campus 15 already holds ONE `parent`
    /// row at this scope whose LOGIN lives at another scope - a row the grid's `INNER JOIN users` can
    /// never resolve. A scope-count reports it as seeded and hides the very defect these five specs exist
    /// to make visible; a stamp-count reports 3 and names the truth.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, EmptyGridTablesSeedResult result)
    {
        result.Parents = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM parent WHERE tenantid = @t AND schoolid = @s AND campusid = @c " +
            "AND nic LIKE 'PERF-PAR-%'",
            tenantId, schoolId, campusId);
        result.ParentUsers = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM users WHERE tenantid = @t AND schoolid = @s AND campusid = @c " +
            "AND email LIKE 'perf.parent.%@scube.test'",
            tenantId, schoolId, campusId);
        result.GradingSchemes = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM gradingscheme WHERE tenantid = @t AND schoolid = @s AND campusid = @c " +
            "AND code LIKE 'PERF-GS-%'",
            tenantId, schoolId, campusId);
        result.GradingRules = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM gradingrule gr JOIN gradingscheme gs ON gs.id = gr.gradingschemeid " +
            "WHERE gs.tenantid = @t AND gs.schoolid = @s AND gs.campusid = @c AND gs.code LIKE 'PERF-GS-%'",
            tenantId, schoolId, campusId);
        result.FeeAssignments = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM studentfeeassignment WHERE tenantid = @t AND schoolid = @s " +
            "AND campusid = @c AND remarks LIKE 'Perf dataset - the fee structure%'",
            tenantId, schoolId, campusId);
        result.FeeDiscounts = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM studentfeediscount WHERE tenantid = @t AND schoolid = @s " +
            "AND campusid = @c AND remarks LIKE 'Perf dataset - a student fee discount%'",
            tenantId, schoolId, campusId);
        result.LateFeeSettings = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM latefeesettings WHERE tenantid = @t AND schoolid = @s AND campusid = @c " +
            "AND name LIKE 'Perf Late Fee %'",
            tenantId, schoolId, campusId);
        // The two late-fee children carry no free text of their own, so they are reached through the
        // setting this seeder created - which is also the only handle that distinguishes them from the
        // rows the late-fee engine writes itself on a campus that already runs one.
        result.LateFeeAssessments = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM latefeeassessments a WHERE a.tenantid = @t AND a.schoolid = @s " +
            "AND a.campusid = @c AND a.settingid IN (SELECT id FROM latefeesettings " +
            "WHERE tenantid = @t AND schoolid = @s AND campusid = @c AND name LIKE 'Perf Late Fee %')",
            tenantId, schoolId, campusId);
        result.LateFeeCharges = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM latefeecharges lc WHERE lc.tenantid = @t AND lc.schoolid = @s " +
            "AND lc.campusid = @c AND lc.assessmentid IN (SELECT a.id FROM latefeeassessments a " +
            "WHERE a.tenantid = @t AND a.schoolid = @s AND a.campusid = @c AND a.settingid IN " +
            "(SELECT id FROM latefeesettings WHERE tenantid = @t AND schoolid = @s AND campusid = @c " +
            "AND name LIKE 'Perf Late Fee %'))",
            tenantId, schoolId, campusId);
    }

    private static Task<int> ScalarAsync(NpgsqlConnection conn, string sql,
        long tenantId, long schoolId, long campusId) =>
        conn.QueryFirstOrDefaultAsync<int>(sql,
            new { t = tenantId, s = schoolId, c = campusId });
}
