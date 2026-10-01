using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the EXAM TOOLING module (`assessmenttool` + `assessmenttoolitem`, `rubriccriterionlevel`,
/// `examinvigilator`) and then asserts the joins that decide whether its four screens measure
/// anything.
///
/// ⚠️ ALL FOUR TABLES WERE EMPTY IN EVERY DATABASE HERE, WHILE FOUR SHIPPED SCREENS PAGE OVER THEM.
/// `examination/assessmentTool` (paged, with a per-row `ItemCount` sub-select), `…/assessmentToolItem/
/// tool/{id}`, `…/rubricCriterionLevel` (paged over a three-table join) + `/item/{id}` and
/// `…/examInvigilator/list` all read zero rows, so a spec over any of them would report SKIP - the
/// "reads as coverage while measuring nothing" failure this tool exists to prevent.
///
/// ⚠️ THE ASSERTIONS ARE THE QUERIES' OWN PREDICATES, NOT COUNTS. Three of the four reads reach their
/// scope through a JOIN, so a row can exist in its table and be invisible on the screen that lists it:
///
///   * `AssessmentToolRepository.GetAll(page, t, s, c)` filters the TOOL's own three columns - a tool
///     at another scope is unreachable;
///   * `RubricCriterionLevelRepository` takes the scope from `AssessmentTool` through
///     `AssessmentToolItem`, so an item or a level whose TOOL is elsewhere is unreachable even though
///     `assessmenttoolitem` and `rubriccriterionlevel` carry no scope of their own;
///   * the rubric read also `LEFT JOIN`s `PerformanceScaleLevel`, so a level with no
///     `performancescalelevelid` renders a nameless row;
///   * `ExamInvigilatorRepository.GetAll` joins `Teacher -> Users` for the name it orders by, so a
///     roster row whose teacher has no login renders a blank.
///
/// Opt in with the same flag the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~ExamToolingDataset"
///
/// ⚠️ IT DEPENDS ON `ExamsModuleSeeder` (the `examschedule` rows an invigilator hangs off) and on
/// `CommunicationWorkspaceSeeder`/`HrModuleSeeder` (the `teacher` rows). Both are reported per campus
/// rather than thrown, so this fixture seeds the campuses that can carry a roster.
/// </summary>
public sealed class ExamToolingDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public ExamToolingDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Exam_tooling_dataset_fills_every_read_the_tooling_and_invigilator_screens_make()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the exam-tooling perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed exam tooling into - " +
            "run PerfDatasetTests first");

        var options = new ExamToolingSeedOptions
        {
            ItemsPerTool = SeedCampuses.EnvInt("SCUBE_PERF_EXAMTOOL_ITEMS", 4),
            LevelsPerItem = SeedCampuses.EnvInt("SCUBE_PERF_EXAMTOOL_LEVELS", 4),
            InvigilatorsPerSchedule = SeedCampuses.EnvInt("SCUBE_PERF_EXAMTOOL_INVIGILATORS", 2),
            Force = SeedCampuses.Force,
        };

        // ⚠️ The counts are NOT uniform and the message must not imply they are - the rubric tool gets
        // `ItemsPerTool` criteria and the level count VARIES per criterion, on purpose (both are how the
        // "busiest row" resolutions in `db-report` stay falsifiable). Reported as the knob values they are.
        _output.WriteLine($"Seeding EXAM TOOLING for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: rubric tool {options.ItemsPerTool} criteria, " +
                          $"up to {options.LevelsPerItem} levels/criterion, " +
                          $"other methods vary, {options.InvigilatorsPerSchedule} invigilators/sitting");
        _output.WriteLine("");

        var seeder = new ExamToolingSeeder(_connectionString);
        var seededCampusIds = new List<long>();
        var totalTools = 0;
        var totalRubricLevels = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                // A campus that HOLDS tools is still worth asserting - the skip path reads the counts
                // back, so the assertions below run against what it really has.
                if (result.AssessmentTools > 0) seededCampusIds.Add(campusId);
                continue;
            }

            totalTools += result.AssessmentTools;
            totalRubricLevels += result.RubricCriterionLevels;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.AssessmentTools,3} tools {result.AssessmentToolItems,4} criteria " +
                $"{result.RubricCriterionLevels,4} rubric levels {result.ExamInvigilators,4} invigilators " +
                $"{result.PerformanceScaleLevels,2} scale levels");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalTools} assessment tools, {totalRubricLevels} rubric levels");

        Assert.True(seededCampusIds.Count > 0,
            "no campus holds assessment tools - every campus was skipped for a missing prerequisite, so the " +
            "tooling and rubric specs would still measure an empty table");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. These tables held ZERO rows, so the planner's statistics
        // describe an empty table - and this repo has already paid for that twice (`V132`'s index
        // looked useless on un-analyzed statistics, and the enrolment picker looked acceptable on
        // them). The list is the SEEDER's own declaration, so what is analyzed is what it wrote.
        foreach (var table in ExamToolingSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertTheToolGridCanResolveItsOwnColumnsAsync(conn, seededCampusIds);
        await AssertEveryCriterionIsReachableThroughItsToolAsync(conn, seededCampusIds);
        await AssertTheRubricGridsJoinsAllResolveAsync(conn, seededCampusIds);
        await AssertTheInvigilatorRosterNamesItsTeachersAsync(conn, seededCampusIds);
        await AssertNoSentinelTimestampsAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// ⚠️ THE PAGED TOOL GRID PROJECTS TWO JOINED COLUMNS, SO BOTH HAVE TO RESOLVE OR THE ROW IS A
    /// BLANK. `PerformanceScaleName` comes from `LEFT JOIN PerformanceScale` and `ItemCount` from a
    /// correlated `(SELECT COUNT(*) FROM AssessmentToolItem WHERE AssessmentToolId = at.Id)`. A tool
    /// whose scale id does not resolve still APPEARS - with an empty name and an editor that offers
    /// no levels - which is precisely why "the grid returned rows" is not the assertion.
    ///
    /// The method id is the other half: it is the enum the factory switches on, so a tool carrying a
    /// value outside 1-6 is a configuration the marks engine cannot resolve a strategy for.
    /// </summary>
    private static async Task AssertTheToolGridCanResolveItsOwnColumnsAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM assessmenttool
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `assessmenttool` row at its own scope - " +
                "`GET examination/assessmentTool` filters the tool's tenant/school/campus, so the grid is empty");

            // The grid's own LEFT JOIN, repeated verbatim so the assertion cannot drift from it.
            var namelessScale = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM assessmenttool at
                   WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM performancescale ps WHERE ps.id = at.performancescaleid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(namelessScale == 0,
                $"campus {campusId} holds {namelessScale} tool(s) whose `performancescaleid` resolves to nothing - " +
                "the grid's `COALESCE(ps.Name, '')` renders a blank scale and the editor offers no levels");

            var unknownMethod = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM assessmenttool at
                   WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId
                     AND (at.assessmentmethodid < 1 OR at.assessmentmethodid > 6)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unknownMethod == 0,
                $"campus {campusId} holds {unknownMethod} tool(s) whose `assessmentmethodid` is outside the " +
                "`AssessmentMethod` enum (1-6) - `AssessmentMethodCalculatorFactory` has no strategy for it");

            // The grid's own correlated ItemCount, which is what makes the column non-blank.
            var withNoItems = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM assessmenttool at
                   WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM assessmenttoolitem i WHERE i.assessmenttoolid = at.id)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(withNoItems == 0,
                $"campus {campusId} holds {withNoItems} tool(s) with ZERO criteria - the grid's ItemCount column " +
                "reads 0 and the tool's own item screen (`assessmentToolItem/tool/{id}`) opens empty");
        }
    }

    /// <summary>
    /// ⚠️ A CRITERION'S SCOPE COMES FROM ITS TOOL, NOT FROM ITSELF. `assessmenttoolitem` carries only
    /// `assessmenttoolid`, and every read that lists criteria - the tool editor's item list and the
    /// rubric grid's INNER JOIN - reaches the campus through `AssessmentTool`. An item whose tool is
    /// at another scope therefore EXISTS and can never be rendered: the "a seeded row no query can
    /// reach" defect, one table down.
    /// </summary>
    private static async Task AssertEveryCriterionIsReachableThroughItsToolAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM assessmenttoolitem i
                   WHERE NOT EXISTS (
                           SELECT 1 FROM assessmenttool at
                            WHERE at.id = i.assessmenttoolid
                              AND at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} criterion row(s) whose TOOL is outside the campus scope - " +
                "the rubric grid reaches the scope through `AssessmentToolItem -> AssessmentTool`, so these are " +
                "counted in the table and invisible on every screen");
        }
    }

    /// <summary>
    /// THE RUBRIC READS ARE TWO JOINS DEEP AND THE GRID PAGES OVER THE RESULT. The paged read is
    /// `RubricCriterionLevel -> AssessmentToolItem -> AssessmentTool` (for the scope) plus a LEFT JOIN
    /// to `PerformanceScaleLevel` (for the label, score and colour), and `/item/{id}` reads the same
    /// level set for one criterion. So three things have to resolve for a row to be useful: its
    /// criterion, that criterion's tool scope, and its level.
    /// </summary>
    private static async Task AssertTheRubricGridsJoinsAllResolveAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            // The exact chain the paged read builds, repeated so the predicate cannot drift.
            var reachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM rubriccriterionlevel rcl
                    INNER JOIN assessmenttoolitem ati ON ati.id = rcl.assessmenttoolitemid
                    INNER JOIN assessmenttool at ON at.id = ati.assessmenttoolid
                   WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(reachable > 0,
                $"campus {campusId} holds no `rubriccriterionlevel` row reachable through its tool - " +
                "`GET examination/rubricCriterionLevel` pages over exactly that join, so the grid is empty");

            // The join the row's LABEL comes from - a NULL here is a nameless level on a rubric.
            var namelessLevel = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM rubriccriterionlevel rcl
                    INNER JOIN assessmenttoolitem ati ON ati.id = rcl.assessmenttoolitemid
                    INNER JOIN assessmenttool at ON at.id = ati.assessmenttoolid
                   WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId
                     AND (rcl.performancescalelevelid IS NULL
                          OR NOT EXISTS (SELECT 1 FROM performancescalelevel psl
                                          WHERE psl.id = rcl.performancescalelevelid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(namelessLevel == 0,
                $"campus {campusId} holds {namelessLevel} rubric level row(s) whose `performancescalelevelid` does " +
                "not resolve - the grid's `LevelLabel`/`LevelScore`/`LevelColor` are three LEFT JOINed columns, so " +
                "the row renders as a nameless, colourless level");

            // The per-criterion read the evidence screen opens (`GetAllByItemId`), which is keyed on the
            // criterion rather than on the scope - so at least one criterion must carry a full level set.
            var criteriaWithLevels = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT rcl.assessmenttoolitemid) FROM rubriccriterionlevel rcl
                    INNER JOIN assessmenttoolitem ati ON ati.id = rcl.assessmenttoolitemid
                    INNER JOIN assessmenttool at ON at.id = ati.assessmenttoolid
                   WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(criteriaWithLevels > 0,
                $"campus {campusId} has no criterion carrying a level set - " +
                "`GET examination/rubricCriterionLevel/item/{id}` would return nothing for every criterion");
        }
    }

    /// <summary>
    /// ⚠️ THE ROSTER IS THE ONE READ THAT INNER JOINs NOTHING BUT STILL RENDERS A BLANK. Its name
    /// column is `COALESCE(tu.FirstName || ' ' || tu.LastName, '')` over
    /// `LEFT JOIN Teacher t … LEFT JOIN Users tu`, and it orders by that same expression - so an
    /// invigilator whose teacher has no login sorts under the blanks and shows nobody. The sitting
    /// must also belong to the SAME campus, or the roster is a list of the wrong exam.
    /// </summary>
    private static async Task AssertTheInvigilatorRosterNamesItsTeachersAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM examinvigilator
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `examinvigilator` row - `GET examination/examInvigilator/list` " +
                "filters the scope, so the desk is empty");

            var blankName = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM examinvigilator ei
                   WHERE ei.tenantid = @tenantId AND ei.schoolid = @schoolId AND ei.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM teacher t
                             INNER JOIN users u ON u.id = t.userid
                            WHERE t.id = ei.teacherid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(blankName == 0,
                $"campus {campusId} holds {blankName} invigilator row(s) whose teacher has no `users` row - the " +
                "read builds `TeacherName` from `Teacher -> Users`, so the roster renders a blank and sorts it " +
                "to the top");

            var wrongCampus = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM examinvigilator ei
                   WHERE ei.tenantid = @tenantId AND ei.schoolid = @schoolId AND ei.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM examschedule es
                            WHERE es.id = ei.examscheduleid
                              AND es.tenantid = ei.tenantid AND es.schoolid = ei.schoolid
                              AND es.campusid = ei.campusid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(wrongCampus == 0,
                $"campus {campusId} holds {wrongCampus} invigilator row(s) naming a sitting that does not exist " +
                "at the same scope - the desk is opened from a published schedule, so those rows belong to " +
                "another campus's exam");

            // A roster of one person repeated is not a roster: the desk's date read groups by sitting.
            var distinctSittings = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT ei.examscheduleid) FROM examinvigilator ei
                   WHERE ei.tenantid = @tenantId AND ei.schoolid = @schoolId AND ei.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(distinctSittings > 0,
                $"campus {campusId} has invigilators but no distinct sitting - a read that groups by " +
                "`ExamScheduleId` would return a single row");
        }
    }

    /// <summary>
    /// The `-infinity` guard. `assessmenttool` / `examinvigilator` carry `createdon`/`modifiedon` and
    /// the grids' default ordering walks them; a `DateTime.MinValue` reaching PostgreSQL is
    /// `-infinity`, which sorts BEFORE every real timestamp and makes "the newest tool" arbitrary.
    /// The seeder writes explicit stamps; this holds that promise.
    /// </summary>
    private static async Task AssertNoSentinelTimestampsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var bad = await conn.ExecuteScalarAsync<long>(
                @"SELECT
                    (SELECT COUNT(*) FROM assessmenttool
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM examinvigilator
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM performancescale
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(bad == 0,
                $"campus {campusId} holds {bad} row(s) stamped `-infinity` - the sentinel that ties every " +
                "`ORDER BY CreatedOn` and makes the newest row arbitrary");
        }
    }
}
