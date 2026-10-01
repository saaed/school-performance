using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the FIVE grid-driving tables that hold ZERO rows on the measured campus (15) - `parent`,
/// `gradingscheme` (+ `gradingrule`), `studentfeeassignment`, `studentfeediscount` and
/// `latefeecharges` (+ the `latefeesettings` / `latefeeassessments` rows its FKs demand) - and then
/// asserts the joins that decide whether their screens measure anything.
///
/// ⚠️ ALL FIVE ARE PAGED SERVER-SIDE GRIDS AND ALL FIVE WERE EMPTY HERE, so a spec over any of them
/// reported SKIP - which reads as "not measured yet" for a screen the application ships. That is the
/// exact failure this tool exists to prevent, and the reason a spec was not written first.
///
/// ⚠️ EVERY ASSERTION IS SCOPED TO THE ROWS THIS SEEDER WROTE, BY ITS OWN STAMP
/// (`PERF-PAR-%` / `PERF-GS-%` / its `remarks` / its rule name). Two reasons, both measured:
///
///   * campus 15 ALREADY holds one `parent` row whose login lives at ANOTHER scope, which the grid's
///     `INNER JOIN` legitimately cannot see - so a scope-wide assertion would fail on data this seeder
///     neither wrote nor owns;
///   * a campus that already runs the late-fee engine holds assessment/charge rows that are the
///     ENGINE's, not a fixture's, and asserting on those would report a seeder bug for an app state.
///
/// ⚠️ AND THE ASSERTIONS ARE THE READS' OWN PREDICATES AND JOINS, NOT COUNTS, because every one of these
/// grids is reached through a JOIN whose shape is what makes a row reachable:
///
///   * `parent` - `ParentRepository.GetAllParentUserInfo`'s predicate is an `INNER JOIN users` on the
///     SAME (tenant, school, campus) triple. **This method was a guaranteed 500 until it was fixed in
///     this same change** (its SELECT list named `u.Role`, a column `users` does not have, so PostgreSQL
///     rejected the statement at PARSE time on every campus, empty or not). The project's role now comes
///     from a correlated subquery over `userrole` -> `roles`, which is asserted here;
///   * `gradingscheme` - the page statement `LEFT JOIN gradingrule` (so a scheme with no rules still
///     renders, with an empty band column) while its COUNT does not join at all, and the result is
///     collapsed through a `Dictionary<long, GradingSchemeDto>` keyed on the scheme id;
///   * `studentfeeassignment` - `INNER JOIN studentenrollment -> student -> classroom -> academicyear ->
///     academicgrade` and `INNER JOIN feestructure`, so a row whose enrolment, classroom or structure is
///     at another campus (or does not exist) is counted and invisible, and `se.classroomid` is NULLABLE -
///     which makes the `INNER JOIN classroom` a real reachability gate rather than a formality;
///   * `studentfeediscount` - `INNER JOIN student` AND `INNER JOIN discount` (projected `splitOn:
///     "StudentId,DiscountId"`) with the assignment -> structure -> year chain LEFT joined;
///   * `latefeecharges` - `INNER JOIN invoices` TWICE (the charge's own invoice, `ci`, and the ORIGINAL,
///     `oi`) plus `student`, and nothing on the table forces `originalinvoiceid` to exist at all - so
///     this seeder points it at a real invoice rather than at a NULL/0 the grid would hide.
///
/// Opt in with the same flag the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~EmptyGridTablesDataset"
///
/// ⚠️ IT DEPENDS ON the dataset's own students/enrolments (`PerfDatasetTests`), `FeesModuleSeeder`
/// (`feestructure`, `invoices`) and `MasterDataSeeder` (`discount`). A campus missing a prerequisite is
/// REPORTED in a sentence rather than thrown, so the fixture seeds what it can and says why.
/// </summary>
public sealed class EmptyGridTablesDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public EmptyGridTablesDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Empty_grid_tables_dataset_makes_every_one_of_the_five_screens_reachable()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the empty-grid-tables dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed into - run PerfDatasetTests first");

        var options = new EmptyGridTablesSeedOptions
        {
            ParentsPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_EMPTY_PARENTS", 3),
            GradingSchemesPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_EMPTY_GRADING_SCHEMES", 2),
            RulesPerScheme = SeedCampuses.EnvInt("SCUBE_PERF_EMPTY_GRADING_RULES", 2),
            FeeAssignmentsPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_EMPTY_FEE_ASSIGNMENTS", 6),
            FeeDiscountsPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_EMPTY_FEE_DISCOUNTS", 6),
            LateFeeChargesPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_EMPTY_LATE_FEE_CHARGES", 5),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding the FIVE EMPTY GRID TABLES for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.ParentsPerCampus} parents, " +
                          $"{options.GradingSchemesPerCampus} schemes x {options.RulesPerScheme} rules, " +
                          $"{options.FeeAssignmentsPerCampus} fee assignments, " +
                          $"{options.FeeDiscountsPerCampus} discounts, " +
                          $"{options.LateFeeChargesPerCampus} late-fee charges");
        _output.WriteLine("");

        var seeder = new EmptyGridTablesSeeder(_connectionString);
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                // ⚠️ A SKIPPED CAMPUS IS STILL ASSERTED WHEN IT ALREADY CARRIES THIS SEEDER'S ROWS - the
                // skip path reads the stamped counts back, so the assertions run against what is really
                // there rather than five zeroes that look like a failed seed. On a re-run the whole
                // fixture therefore still proves the joins.
                if (result.Parents > 0) seededCampusIds.Add(campusId);
                continue;
            }

            _output.WriteLine(
                $"  campus {campusId,-5} parents {result.Parents,3} (+{result.ParentUsers,3} logins) " +
                $"schemes {result.GradingSchemes,3} rules {result.GradingRules,3} " +
                $"fee assignments {result.FeeAssignments,3} discounts {result.FeeDiscounts,3} " +
                $"late-fee settings {result.LateFeeSettings,2} assessments {result.LateFeeAssessments,3} " +
                $"charges {result.LateFeeCharges,3}");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        Assert.True(seededCampusIds.Count > 0,
            "no campus holds this seeder's `parent` rows - every campus was skipped for a missing " +
            "prerequisite, so the five specs would still measure an empty table");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. Every one of these tables held ZERO rows, so the planner's
        // statistics describe an empty table - and this repo has already paid for that twice.
        foreach (var table in EmptyGridTablesSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertEverySeededParentRowResolvesToALoginAtItsOwnScopeAsync(conn, seededCampusIds);
        await AssertEverySeededGradingSchemeIsScopedAndHasRulesAsync(conn, seededCampusIds);
        await AssertEverySeededFeeAssignmentChainResolvesAtItsCampusAsync(conn, seededCampusIds);
        await AssertEverySeededDiscountResolvesAndSetsExactlyOneKindOfValueAsync(conn, seededCampusIds);
        await AssertEverySeededLateFeeChargeResolvesAndMirrorsItsWaiveStampAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// ⚠️ THIS IS THE ONE THAT MATTERS MOST, because it is the shape that made a campus look seeded while
    /// the screen was empty: the grid's predicate is an `INNER JOIN users` on the SAME scope triple, so a
    /// parent whose login lives at another scope is counted by the table and unreachable on the screen.
    /// Campus 15 has exactly such a row, which is why `parent` looked non-empty.
    ///
    /// The ROLE half is asserted too, because the reason this method 500'd was a role projection: the
    /// subquery over `userrole` -> `roles` has to resolve for the row to carry the name the DTO declares.
    /// </summary>
    private static async Task AssertEverySeededParentRowResolvesToALoginAtItsOwnScopeAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var reachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM parent t
                    INNER JOIN users u ON t.userid = u.id
                     AND t.tenantid = u.tenantid AND t.schoolid = u.schoolid AND t.campusid = u.campusid
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.nic LIKE 'PERF-PAR-%'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(reachable > 0,
                $"campus {campusId} holds this seeder's `parent` rows but NONE of them resolves to a " +
                "`users` row at its OWN (tenant, school, campus) - which is the grid's `INNER JOIN`, so " +
                "the screen is empty while the table looks seeded. A login at another scope is the cause");

            var unresolved = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM parent t
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.nic LIKE 'PERF-PAR-%'
                     AND NOT EXISTS (
                         SELECT 1 FROM users u
                          WHERE u.id = t.userid AND u.tenantid = t.tenantid
                            AND u.schoolid = t.schoolid AND u.campusid = t.campusid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unresolved == 0,
                $"campus {campusId} holds {unresolved} seeded parent row(s) whose login does not resolve " +
                "at the same scope - those are counted by the table and invisible on the screen");

            // ⚠️ THE REGRESSION GUARD FOR THE 500. `GetAllParentUserInfo` used to select `u.Role`, a
            // column `users` does not have, so the statement failed at PARSE time on EVERY campus; the
            // projection is a subquery over `userrole` -> `roles` now, and this is the statement itself.
            var roleRows = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM parent t
                    INNER JOIN users u ON t.userid = u.id
                     AND t.tenantid = u.tenantid AND t.schoolid = u.schoolid AND t.campusid = u.campusid
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.nic LIKE 'PERF-PAR-%'
                     AND (SELECT r.name FROM userrole ur INNER JOIN roles r ON ur.roleid = r.id
                           WHERE ur.userid = u.id AND ur.roleid = 5 AND ur.isactive = true
                           ORDER BY ur.id LIMIT 1) IS NOT NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(roleRows == reachable,
                $"campus {campusId}: {reachable - roleRows} of this seeder's {reachable} reachable parent " +
                "row(s) resolve no Parent role through `userrole` -> `roles`. The grid projects that name " +
                "as a subquery (the column it used to name does not exist), so the role column renders blank");
        }
    }

    /// <summary>
    /// ⚠️ THE PAGE AND ITS COUNT RETURN THE TWO DIFFERENT THINGS ON PURPOSE. The page statement
    /// `LEFT JOIN gradingrule`, so the raw row set is scheme x rule; Dapper collapses it through a
    /// `Dictionary&lt;long, GradingSchemeDto&gt;` keyed on the scheme id, so both halves report SCHEMES.
    /// A scheme with NO rules is therefore REACHABLE (it renders with an empty band column) - which is
    /// why the assertion below is about the enum vocabulary and the rule count, not about reachability.
    /// </summary>
    private static async Task AssertEverySeededGradingSchemeIsScopedAndHasRulesAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var schemes = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM gradingscheme
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND code LIKE 'PERF-GS-%'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            if (schemes == 0) continue;

            // The band column is fed by the LEFT JOIN, so a scheme with no rules renders a scheme with an
            // EMPTY band list - a screen that loads and shows nothing to grade against.
            var ruleless = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM gradingscheme g
                   WHERE g.tenantid = @tenantId AND g.schoolid = @schoolId AND g.campusid = @campusId
                     AND g.code LIKE 'PERF-GS-%'
                     AND NOT EXISTS (SELECT 1 FROM gradingrule r WHERE r.gradingschemeid = g.id)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(ruleless == 0,
                $"campus {campusId} holds {ruleless} seeded scheme(s) with no `gradingrule` - the grid's " +
                "page LEFT JOINs the rules and renders the bands, so the band column is empty");

            // The vocabulary the DTO's enums describe. A value outside it renders as a blank badge.
            var outsideEnum = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM gradingscheme
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND code LIKE 'PERF-GS-%'
                     AND (calculationmethod NOT IN ('Percentage','GradePoint','Points')
                          OR roundingmethod NOT IN ('None','Up','Down','Nearest'))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(outsideEnum == 0,
                $"campus {campusId} holds {outsideEnum} seeded scheme(s) whose calculation/rounding method " +
                "is outside the vocabulary the DTO's enum describes - the screen renders a blank badge");

            // ⚠️ The page orders the BANDS, but the grid's own sort is the scheme's name; the seeded codes
            // are campus-stamped because the scheme grid is scoped and a re-seed must not collide.
            var unstamped = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM gradingscheme
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND code LIKE 'PERF-GS-%' AND code NOT LIKE @stamp",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId,
                      stamp = $"PERF-GS-{campusId}-%" });

            Assert.True(unstamped == 0,
                $"campus {campusId} holds {unstamped} seeded scheme code(s) that are not stamped with this " +
                "campus - the code is the seeder's own handle and the grid scopes by campus");
        }
    }

    /// <summary>
    /// The grid's own INNER JOIN chain, verbatim: the enrolment, its student, its CLASSROOM (nullable on
    /// the table, so this join is a real gate), the year and the grade the classroom maps to, and the fee
    /// structure. Plus the partial unique index - ONE ACTIVE assignment per enrolment, which is what keeps
    /// the invoice engine from billing a student twice.
    /// </summary>
    private static async Task AssertEverySeededFeeAssignmentChainResolvesAtItsCampusAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var rows = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeeassignment
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - the fee structure%'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            if (rows == 0) continue;

            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeeassignment sfa
                   WHERE sfa.tenantid = @tenantId AND sfa.schoolid = @schoolId AND sfa.campusid = @campusId
                     AND sfa.remarks LIKE 'Perf dataset - the fee structure%'
                     AND NOT EXISTS (
                         SELECT 1
                           FROM studentenrollment se
                           JOIN student s ON s.id = se.studentid
                           JOIN classroom c ON c.id = se.classroomid
                           JOIN academicyear ay ON ay.id = se.academicyearid
                           JOIN academicgrade ag ON ag.id = c.academicgradeid
                           JOIN feestructure fs ON fs.id = sfa.feestructureid
                          WHERE se.id = sfa.studentenrollmentid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} seeded fee assignment(s) whose enrolment, " +
                "classroom, year, grade or fee structure does not resolve through the grid's own INNER " +
                "JOIN chain - the row is counted and invisible. A NULL `se.classroomid` is one cause");

            // ⚠️ The assignment grid is scoped by `academicYearId` too, and the chain above reaches it
            // only through the enrolment - so a row whose year is not the campus's active/published one
            // is missing from the filtered grid even though the unfiltered grid shows it.
            var noYearAnchor = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeeassignment sfa
                   WHERE sfa.tenantid = @tenantId AND sfa.schoolid = @schoolId AND sfa.campusid = @campusId
                     AND sfa.remarks LIKE 'Perf dataset - the fee structure%'
                     AND NOT EXISTS (SELECT 1 FROM studentenrollment se
                                      WHERE se.id = sfa.studentenrollmentid AND se.academicyearid > 0)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(noYearAnchor == 0,
                $"campus {campusId} holds {noYearAnchor} seeded fee assignment(s) whose enrolment carries " +
                "no academic year - the grid's year filter joins through `se.academicyearid`");

            var duplicateActive = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM (
                      SELECT studentenrollmentid FROM studentfeeassignment
                       WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                         AND status = 1
                       GROUP BY studentenrollmentid HAVING COUNT(*) > 1) d",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(duplicateActive == 0,
                $"campus {campusId} holds {duplicateActive} enrolment(s) with more than one ACTIVE fee " +
                "assignment - the partial unique index refuses that, and the invoice engine would double-bill");
        }
    }

    /// <summary>
    /// The discount grid's base query is `inner join Student s` AND `inner join Discount d` (it maps both
    /// through `splitOn: "StudentId,DiscountId"`) with the assignment -> structure -> year chain LEFT
    /// joined. So the two INNER joins are what decide reachability, and the money paths read `percentage`
    /// XOR `amount`.
    /// </summary>
    private static async Task AssertEverySeededDiscountResolvesAndSetsExactlyOneKindOfValueAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var rows = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeediscount
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - a student fee discount%'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            if (rows == 0) continue;

            var unreachableEither = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeediscount sd
                   WHERE sd.tenantid = @tenantId AND sd.schoolid = @schoolId AND sd.campusid = @campusId
                     AND sd.remarks LIKE 'Perf dataset - a student fee discount%'
                     AND (NOT EXISTS (SELECT 1 FROM student s WHERE s.id = sd.studentid)
                          OR NOT EXISTS (SELECT 1 FROM discount d WHERE d.id = sd.discountid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachableEither == 0,
                $"campus {campusId} holds {unreachableEither} seeded discount(s) whose `student` or " +
                "`discount` does not resolve - the grid INNER JOINs BOTH, so the row cannot render at all");

            var bothValues = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeediscount
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - a student fee discount%'
                     AND percentage IS NOT NULL AND amount IS NOT NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(bothValues == 0,
                $"campus {campusId} holds {bothValues} seeded discount(s) carrying BOTH a percentage and a " +
                "flat amount - the money paths read one or the other, so the grant would be counted twice");

            var noValue = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeediscount
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - a student fee discount%'
                     AND percentage IS NULL AND amount IS NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(noValue == 0,
                $"campus {campusId} holds {noValue} seeded discount(s) with neither a percentage nor an " +
                "amount - a grant that reduces nothing, which the grid renders as a blank value column");

            // `DiscountApprovalStatus`: Pending 1 / Approved 2 / Rejected 3. The status tabs separate
            // them, so an all-one-status seed cannot exercise the screen.
            var statuses = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT approvalstatus) FROM studentfeediscount
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - a student fee discount%'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(statuses >= 2,
                $"campus {campusId} holds this seeder's discounts at {statuses} distinct approval " +
                "status(es) - the grid's tabs separate them");

            var approvalsOutOfRange = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeediscount
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - a student fee discount%'
                     AND approvalstatus NOT IN (1,2,3)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(approvalsOutOfRange == 0,
                $"campus {campusId} holds {approvalsOutOfRange} seeded discount(s) whose `approvalstatus` " +
                "is outside `DiscountApprovalStatus` (1 Pending / 2 Approved / 3 Rejected)");

            // The grid's `Approved by` cell is fed from `approvedby` - a row claiming approval with no
            // approver renders blank, and one claiming rejection while naming an approver claims a person
            // who never acted.
            var unmirrored = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentfeediscount
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND remarks LIKE 'Perf dataset - a student fee discount%'
                     AND ((approvalstatus = 2 AND (approvedby IS NULL OR approvedon IS NULL))
                          OR (approvalstatus <> 2 AND approvedby IS NOT NULL))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unmirrored == 0,
                $"campus {campusId} holds {unmirrored} seeded discount(s) whose approver does not mirror " +
                "its status (Approved = 2 names an approver and a date; nothing else may)");
        }
    }

    /// <summary>
    /// The late-fee grid INNER JOINs `invoices` TWICE - `ci` on the charge's own invoice and `oi` on the
    /// ORIGINAL one - plus `student`, so every one of those three FKs must resolve. Nothing on the table
    /// forces `originalinvoiceid` to be real, which is exactly why a fixture that points it at 0 would
    /// look seeded and render nothing.
    /// </summary>
    private static async Task AssertEverySeededLateFeeChargeResolvesAndMirrorsItsWaiveStampAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        // The seeder's charges are reached through the setting it created (they carry no free text of
        // their own), so this fragment is the handle that separates them from the engine's own rows.
        const string seeded =
            @"lc.assessmentid IN (SELECT a.id FROM latefeeassessments a WHERE a.tenantid = @tenantId
                                   AND a.schoolid = @schoolId AND a.campusid = @campusId
                                   AND a.settingid IN (SELECT id FROM latefeesettings
                                                        WHERE tenantid = @tenantId AND schoolid = @schoolId
                                                          AND campusid = @campusId
                                                          AND name LIKE 'Perf Late Fee %'))";

        foreach (var campusId in campusIds)
        {
            var rows = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM latefeecharges lc
                   WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                     AND {seeded}",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            if (rows == 0) continue;

            var unreachable = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM latefeecharges lc
                   WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                     AND {seeded}
                     AND (NOT EXISTS (SELECT 1 FROM invoices ci WHERE ci.id = lc.invoiceid)
                          OR NOT EXISTS (SELECT 1 FROM invoices oi WHERE oi.id = lc.originalinvoiceid)
                          OR NOT EXISTS (SELECT 1 FROM student s WHERE s.id = lc.studentid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} seeded late-fee charge(s) whose own invoice, " +
                "ORIGINAL invoice or student does not resolve - the grid INNER JOINs all three, so the row " +
                "is counted and unrenderable");

            // `ux_latefeecharges_assessment (assessmentid)` - one charge per assessment, so a shared
            // assessment would be a 23505 the generator hits rather than a test finding.
            var sharedAssessments = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM (
                      SELECT assessmentid FROM latefeecharges lc
                       WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId
                         AND lc.campusid = @campusId AND {seeded}
                       GROUP BY assessmentid HAVING COUNT(*) > 1) d",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(sharedAssessments == 0,
                $"campus {campusId} holds {sharedAssessments} assessment(s) shared by more than one " +
                "late-fee charge - `ux_latefeecharges_assessment` is on the assessment alone");

            // ⚠️ `ux_latefeeassessments_setting_invoice_period` is UNIQUE on (tenantid, schoolid,
            // campusid, settingid, invoiceid, period) - so two assessments may share an invoice ONLY if
            // their PERIOD differs. A seeder that reuses one invoice without advancing the period is
            // refused, which is why the period is what the loop counts.
            var sharedPeriod = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM (
                      SELECT a.invoiceid, a.period FROM latefeeassessments a
                       JOIN latefeecharges lc ON lc.assessmentid = a.id
                       WHERE a.tenantid = @tenantId AND a.schoolid = @schoolId
                         AND a.campusid = @campusId AND {seeded}
                       GROUP BY a.invoiceid, a.period HAVING COUNT(*) > 1) d",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(sharedPeriod == 0,
                $"campus {campusId} holds {sharedPeriod} (invoice, period) pair(s) shared by more than one " +
                "seeded assessment - the unique index is on that exact tuple");

            // ⚠️ `LateFeeChargeStatus.Waived = 3`. A waived row with no stamp renders an empty waive
            // column; a NON-waived row carrying a stamp claims a waiver that never happened. `waivedby`
            // is NOT NULL with a default of 0, so 0 is the "nobody waived it" value, not NULL.
            var unmirrored = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM latefeecharges lc
                   WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                     AND {seeded}
                     AND ((lc.status = 3 AND (lc.waivedon IS NULL OR lc.waivedby = 0))
                          OR (lc.status <> 3 AND (lc.waivedon IS NOT NULL OR lc.waivedby <> 0)))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unmirrored == 0,
                $"campus {campusId} holds {unmirrored} seeded charge(s) whose waive stamp does not mirror " +
                "its status (Waived = 3 carries the stamp and an actor; nothing else may)");

            var statuses = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(DISTINCT lc.status) FROM latefeecharges lc
                   WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                     AND {seeded}",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(statuses >= 2,
                $"campus {campusId} holds this seeder's late-fee charges at {statuses} distinct status(es) " +
                "- the grid's tabs separate them, so an all-one-status campus cannot exercise them");

            // ⚠️ The charge's window must be REAL, because the grid's status tabs and the engine both read
            // `chargedate`. A `-infinity` (Npgsql's `DateTime.MinValue`) sorts before every real stamp and
            // ties every row on `ORDER BY chargedate`, which is a documented defect class in this repo.
            var degenerate = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM latefeecharges lc
                   WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                     AND {seeded}
                     AND (lc.chargedate < '1900-01-01' OR lc.chargedate > '2100-01-01')",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(degenerate == 0,
                $"campus {campusId} holds {degenerate} seeded charge(s) whose `chargedate` is degenerate " +
                "(-infinity / +infinity / MinValue) - it ties every row under `ORDER BY chargedate`");
        }
    }
}
