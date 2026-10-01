using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="ExamToolingSeeder"/>.</summary>
public sealed class ExamToolingSeedOptions
{
    /// <summary>
    /// Criteria (items) for the RUBRIC tool - the one whose criteria carry a level set and therefore
    /// the axis the rubric editor pages over. Four is what a term's rubric carries; one would make
    /// the read measure a single row. ⚠️ The other five tools get their own counts from
    /// <see cref="CriteriaFor"/> - see the note there on why they are UNEQUAL.
    /// </summary>
    public int ItemsPerTool { get; set; } = 4;

    /// <summary>
    /// The MOST levels a rubric criterion is scored against - one per entry of the campus's
    /// performance scale, so the editor's `PerformanceScaleLevel` join resolves for every row it
    /// renders. ⚠️ It is a CAP, not a uniform count: the criterion's own level set varies below it
    /// (see the rubric-level loop), because `db-report` picks "the criterion with the most levels"
    /// for the per-criterion read and equal counts would make that resolution unfalsifiable.
    /// </summary>
    public int LevelsPerItem { get; set; } = 4;

    /// <summary>
    /// Invigilators per exam sitting. Two is the smallest number that makes the desk's roster a
    /// roster rather than a name - and the read orders by the joined teacher name, so a single row
    /// would not exercise the sort.
    /// </summary>
    public int InvigilatorsPerSchedule { get; set; } = 2;

    /// <summary>Re-seed even when the campus already holds assessment tools.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's exam-tooling seed produced.</summary>
public sealed class ExamToolingSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int PerformanceScales { get; set; }
    public int PerformanceScaleLevels { get; set; }
    public int AssessmentTools { get; set; }
    public int AssessmentToolItems { get; set; }
    public int RubricCriterionLevels { get; set; }
    public int ExamInvigilators { get; set; }
}

/// <summary>
/// Seeds the EXAM TOOLING tables - `assessmenttool` + its `assessmenttoolitem` criteria, the
/// `rubriccriterionlevel` rows a rubric scores against, and the `examinvigilator` roster - for one
/// campus.
///
/// WHY THIS EXISTS
/// ---------------
/// All four tables held ZERO rows in every database here, while four shipped screens page over
/// them (`examination/assessmentTool`, `examination/assessmentToolItem/tool/{id}`,
/// `examination/rubricCriterionLevel` + `/item/{id}` and `examination/examInvigilator/list`). A
/// spec over an empty table reports SKIP, which reads as "not measured yet" for a grid the
/// application really ships - the same defect the fourth pass recorded for the six `rpt-*` reports.
///
/// ⚠️ WHY <see cref="ExamsModuleSeeder"/> DID NOT COVER THIS. That seeder builds the ASSESSMENT
/// SPINE (`assessmentcomponent` -> `subjectassessmentcomponent` -> `examschedule` -> `studentexam`
/// -> marks -> results), and `assessmentcomponent` is a DIFFERENT table from `assessmenttool`. The
/// exams tier's six methods (`AssessmentMethod` 1-6: Numeric / PassFail / Rubric / DirectGradeEntry
/// / Checklist / Observation) are configured through `assessmenttool`, which nothing ever wrote -
/// so the tooling screen and the rubric editor were empty on every campus.
///
/// ⚠️ THE SCOPE IS NOT ON EVERY TABLE, AND THAT DECIDES WHAT "REACHABLE" MEANS.
/// `assessmenttool` carries the tenant/school/campus triple; `assessmenttoolitem` and
/// `rubriccriterionlevel` carry NONE. Both reads reach the scope THROUGH the tool
/// (`RubricCriterionLevelRepository` joins `RubricCriterionLevel -> AssessmentToolItem ->
/// AssessmentTool` and filters on the tool's three columns), so an item or a level whose tool sits
/// at another scope EXISTS and is INVISIBLE - which is why every row here is written against a tool
/// THIS seeder created for THIS campus, never against a tool id read from somewhere else.
///
/// ⚠️ `assessmenttool.performancescaleid` IS AN FK TO `performancescale`, so the campus needs one.
/// Campus 15 already holds "Standard Performance Scale" (written by `HrWorkflowSeeder`), and this
/// seeder ADOPTS it by name rather than inserting a second one - a duplicate scale is a config the
/// rubric editor would offer twice. A campus with no scale gets one, with the four levels a
/// kindergarten-to-grade-12 school actually uses.
///
/// ⚠️ AN INVIGILATOR NEEDS A PUBLISHED SITTING AND A REAL TEACHER. `examinvigilator.examscheduleid`
/// and `.teacherid` are both NOT NULL, and the desk's read INNER JOINs `Teacher -> Users` for the
/// name - so a campus with no `examschedule` (or no `teacher`) cannot be seeded and this seeder
/// REPORTS that in a sentence rather than throwing, the way the other module seeders do.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds assessment tools is skipped unless
/// <see cref="ExamToolingSeedOptions.Force"/> is set.
/// </summary>
public sealed class ExamToolingSeeder : BaseSeeder
{
    public ExamToolingSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk exam-tooling load invalidates, so a fixture can ANALYZE them exactly as
    /// <see cref="PerfDatasetSeeder"/> does. Without fresh statistics the planner reasons from the
    /// row counts that described an EMPTY table - the failure mode already measured on `classroom`
    /// and on the enrolment picker.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "assessmenttool", "assessmenttoolitem", "rubriccriterionlevel", "examinvigilator",
        "performancescale", "performancescalelevel"
    };

    /// <summary>
    /// The scale a school actually grades against. Shared with <see cref="HrWorkflowSeeder"/>'s own
    /// four levels (`Needs Improvement` / `Meets Expectations` / `Exceeds Expectations` /
    /// `Outstanding`) so a campus seeded by either one presents the SAME scale to the rubric editor
    /// and the performance-review screen.
    /// </summary>
    private static readonly (string Label, decimal Score, string Color)[] ScaleLevels =
    {
        ("Needs Improvement", 1m, "#dc2626"),
        ("Meets Expectations", 2m, "#f59e0b"),
        ("Exceeds Expectations", 3m, "#0ea5e9"),
        ("Outstanding", 4m, "#16a34a"),
    };

    /// <summary>
    /// ONE TOOL PER ASSESSMENT METHOD, named by the method's own `Description` - that is how the
    /// module is shaped: `AssessmentMethodCalculatorFactory` maps six methods onto six strategies,
    /// and a tool is a method plus the scale it scores against. Seeding only "Numeric Marks" would
    /// leave the five other strategies with no configuration to be selected from.
    /// </summary>
    private static readonly (int MethodId, string Name, string Description)[] Tools =
    {
        (1, "Numeric Marks", "Marks scored out of a maximum - the default method for graded subjects."),
        (2, "Pass/Fail", "A binary outcome with no arithmetic."),
        (3, "Rubric Assessment", "Scored against a criterion and a level on the performance scale."),
        (4, "Direct Grade Entry", "The teacher's own grade, with no intermediate marks."),
        (5, "Checklist", "A list of items, scored as ticked over total."),
        (6, "Observation", "A level per observed item, averaged."),
    };

    /// <summary>
    /// The criteria names a tool's items carry. The rubric tool reads them as its CRITERIA column,
    /// so the names are the vocabulary a teacher recognises rather than "Item 1".
    /// </summary>
    private static readonly string[] CriterionNames =
    {
        "Knowledge and Understanding",
        "Application of Skills",
        "Communication",
        "Collaboration and Participation",
        "Critical Thinking",
        "Presentation",
        "Independent Work",
        "Effort and Attitude",
    };

    /// <summary>
    /// Criteria for one assessment METHOD.
    ///
    /// ⚠️ THE COUNTS ARE DELIBERATELY UNEQUAL, AND THAT IS THE POINT. The dataset's whole claim is
    /// that the specs measure the WORST CASE, and `db-report` resolves its subject by COUNT ("the
    /// tool holding the MOST criteria"). If every tool carried <see cref="ExamToolingSeedOptions.ItemsPerTool"/>
    /// the resolution would be unfalsifiable: a resolver that picked the FIRST tool instead of the
    /// busiest would read the same number of rows and look correct. So the spread is the one a real
    /// school has - a direct grade is a single judgement, a checklist is a list of ticks - and the
    /// busiest tool (Checklist) is not the first one the spec could accidentally pick.
    ///
    /// The RUBRIC tool's count comes from the knob: it is the one whose criteria carry a level set,
    /// so it is the axis the rubric editor pages over and the one worth scaling.
    /// </summary>
    private static int CriteriaFor(int methodId, int itemsPerTool) => methodId switch
    {
        1 => Math.Max(2, itemsPerTool - 1),  // Numeric Marks - a mark per criterion, one fewer than the rubric
        2 => 1,                              // Pass/Fail - a binary outcome has nothing to break down
        3 => itemsPerTool,                   // Rubric Assessment - the level set hangs off these
        4 => 1,                              // Direct Grade Entry - one judgement, no criteria
        5 => itemsPerTool + 2,               // Checklist - a list of ticks is the longest, and never the first tool
        6 => itemsPerTool,                   // Observation - a level per observed item
        _ => itemsPerTool,
    };

    public async Task<ExamToolingSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, ExamToolingSeedOptions options, bool verbose = true)
    {
        var result = new ExamToolingSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM assessmenttool
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // ⚠️ A SKIPPED CAMPUS MUST STILL REPORT WHAT IT HOLDS, not what this run wrote - the
            // fixture's whole point is "the screens have something to measure", and a total of zero
            // over a campus that holds twelve tools reads as a broken fixture.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  Exam tooling: campus {campusId} already holds {existing} assessment tool(s) - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // The PREREQUISITES a campus must satisfy. Both are REPORTED rather than thrown, so a
        // fixture can seed the campuses that can be seeded and say why the others could not - the
        // shape `CommunicationWorkspaceSeeder` established for a missing teacher.
        // ------------------------------------------------------------------
        var schedules = (await conn.QueryAsync<long>(
            @"SELECT id FROM examschedule
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var teachers = (await conn.QueryAsync<long>(
            @"SELECT id FROM teacher
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (schedules.Count == 0 || teachers.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} holds {(schedules.Count == 0 ? "no exam schedule" : $"{schedules.Count} schedule(s)")} " +
                $"and {(teachers.Count == 0 ? "no teacher" : $"{teachers.Count} teacher(s)")} - the invigilator " +
                "desk joins both (`examinvigilator.examscheduleid` and `.teacherid` are NOT NULL), so this campus " +
                "cannot carry a roster. Run the exams fixture (ReportingDatasetTests) and the communication " +
                "workspace fixture first.";
            if (verbose) Console.WriteLine($"  Exam tooling: {result.SkipReason}");
            return result;
        }

        // ------------------------------------------------------------------
        // 0. A forced re-seed clears first. `assessmenttoolitem` and `rubriccriterionlevel` carry NO
        //    scope columns, so a scoped DELETE on either is a 42703 that would abort the whole seed -
        //    they go through their parent, children first. (The third instance of that rule in this
        //    repo, after `journalentryline` and `librarybookauthor`.)
        // ------------------------------------------------------------------
        if (options.Force && existing > 0)
        {
            // ⚠️ THE RUBRIC LEVELS NEED A TWO-LEVEL CLEAR, NOT `ClearTableByParentAsync`. That helper
            // deletes `child WHERE parentColumn IN (SELECT Id FROM parent WHERE <scope triple>)` - so it
            // needs the PARENT to carry the scope. Here the parent is `assessmenttoolitem`, which carries
            // NONE (the scope lives on `assessmenttool`), so the helper's own query is a **42703** - and
            // 42703 is deliberately NOT in its tolerated set, so it aborted the whole seed. The scope
            // reaches a level through `rubriccriterionlevel -> assessmenttoolitem -> assessmenttool`, so
            // the clear walks that same chain. Children first, still: the levels go before the items.
            await ClearRubricLevelsThroughToolAsync(conn, tenantId, schoolId, campusId);

            await ClearTableByParentAsync(
                conn, "assessmenttoolitem", "assessmenttoolid", "assessmenttool",
                tenantId, schoolId, campusId);

            await ClearTableAsync(conn, "examinvigilator", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "assessmenttool", tenantId, schoolId, campusId);

            if (verbose) Console.WriteLine($"  Exam tooling: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. The performance scale the tools score against. ADOPTED by name when it exists, so a
        //    campus seeded by `HrWorkflowSeeder` is not given a second one.
        // ------------------------------------------------------------------
        var scaleId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM performancescale
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        if (scaleId is null or 0)
        {
            scaleId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO performancescale
                      (name, description, tenantid, schoolid, campusid,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@name, @description, @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    name = "Standard Performance Scale",
                    description = "Seeded scale - the levels an assessment tool scores against.",
                    tenantId, schoolId, campusId, now
                });
            result.PerformanceScales++;
        }

        var scaleLevelIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM performancescalelevel
               WHERE performancescaleid = @scaleId ORDER BY displayorder, id",
            new { scaleId })).ToList();

        if (scaleLevelIds.Count == 0)
        {
            var order = 0;
            foreach (var (label, score, color) in ScaleLevels.Take(options.LevelsPerItem))
            {
                order++;
                var levelId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO performancescalelevel
                          (performancescaleid, label, score, description, displayorder, color)
                      VALUES (@scaleId, @label, @score, @description, @order, @color)
                      RETURNING id",
                    new
                    {
                        scaleId,
                        label,
                        score,
                        description = $"{label} - {score:0} of {ScaleLevels.Length}",
                        order,
                        color
                    });
                scaleLevelIds.Add(levelId);
                result.PerformanceScaleLevels++;
            }
        }

        // ------------------------------------------------------------------
        // 2. `assessmenttool` - one row per assessment method, all three scope columns stamped from
        //    the ROUTE's scope rather than a default (the columns default to 0, which would put the
        //    tool outside every screen's predicate).
        // ------------------------------------------------------------------
        var toolIdsByMethod = new Dictionary<int, long>();

        foreach (var (methodId, name, description) in Tools)
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO assessmenttool
                      (name, description, tenantid, schoolid, campusid, assessmentmethodid,
                       performancescaleid, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@name, @description, @tenantId, @schoolId, @campusId, @methodId,
                          @scaleId, 1, 1, @now, @now)
                  RETURNING id",
                new { name, description, tenantId, schoolId, campusId, methodId, scaleId, now });

            toolIdsByMethod[methodId] = id;
            result.AssessmentTools++;
        }

        // ------------------------------------------------------------------
        // 3. `assessmenttoolitem` - the criteria each tool scores. Every tool gets them: the rubric
        //    tool reads them as criteria, the checklist tool as ticks and the observation tool as
        //    observed items, and the paged tool grid renders an ItemCount per row - so a tool with
        //    no items is a row whose `ItemCount` column is 0 and whose editor opens empty.
        // ------------------------------------------------------------------
        var itemIdsByTool = new Dictionary<long, List<long>>();

        foreach (var (methodId, _, _) in Tools)
        {
            var toolId = toolIdsByMethod[methodId];
            var items = new List<long>();
            var order = 0;

            foreach (var criterion in CriterionNames.Take(CriteriaFor(methodId, options.ItemsPerTool)))
            {
                order++;
                var itemId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO assessmenttoolitem
                          (assessmenttoolid, name, description, displayorder)
                      VALUES (@toolId, @name, @description, @order)
                      RETURNING id",
                    new
                    {
                        toolId,
                        name = criterion,
                        description = $"{criterion} - assessed with {Tools.First(t => t.MethodId == methodId).Name}.",
                        order
                    });

                items.Add(itemId);
                result.AssessmentToolItems++;
            }

            itemIdsByTool[toolId] = items;
        }

        // ------------------------------------------------------------------
        // 4. `rubriccriterionlevel` - the level set each CRITERION is scored against.
        //
        //    ⚠️ ONLY THE RUBRIC TOOL'S CRITERIA GET LEVELS, and that is the module's own semantics:
        //    a rubric scores a criterion by choosing a LEVEL (`performanceScaleLevelId`), while a
        //    numeric tool scores a mark and a checklist tool counts ticks. Writing levels for every
        //    tool's items would triple the table with rows no screen ever renders.
        // ------------------------------------------------------------------
        var rubricToolId = toolIdsByMethod[(int)StaticAssessmentMethod.Rubric];

        var rubricCriterionIndex = 0;
        foreach (var itemId in itemIdsByTool[rubricToolId])
        {
            var order = 0;

            // ⚠️ THE LEVEL SET VARIES PER CRITERION, and the FIRST criterion is deliberately NOT the
            // fullest. `db-report` resolves `AssessmentToolItemId` as "the criterion holding the MOST
            // levels" for the per-criterion read, so uniform counts would make that resolution
            // unfalsifiable - a resolver that took the first criterion would measure the same rows and
            // look right. A rubric really does score some criteria on a coarse high/low pair and others
            // across the whole scale, so the spread is the module's own shape, not a fixture trick.
            // `LevelsPerItem` stays the CAP (it can shrink the spread on a campus with few scale levels,
            // which is honest - with two levels every criterion uses two).
            var levelCount = Math.Min(
                Math.Min(scaleLevelIds.Count, options.LevelsPerItem),
                2 + (rubricCriterionIndex % 3));
            rubricCriterionIndex++;

            foreach (var levelId in scaleLevelIds.Take(levelCount))
            {
                order++;
                await conn.ExecuteAsync(
                    @"INSERT INTO rubriccriterionlevel
                          (assessmenttoolitemid, description, displayorder, performancescalelevelid)
                      VALUES (@itemId, @description, @order, @levelId)",
                    new
                    {
                        itemId,
                        description = $"{ScaleLevels[Math.Min(order - 1, ScaleLevels.Length - 1)].Label} - demonstrated consistently.",
                        order,
                        levelId
                    });
                result.RubricCriterionLevels++;
            }
        }

        // ------------------------------------------------------------------
        // 5. `examinvigilator` - the roster. A teacher is assigned round-robin so every sitting
        //    carries a DISTINCT pair where the campus has enough staff (a duplicated (schedule,
        //    teacher) pair is a roster the desk would render twice). Only PUBLISHED sittings are
        //    used: the desk is opened from a published schedule.
        // ------------------------------------------------------------------
        var teacherCursor = 0;
        foreach (var scheduleId in schedules)
        {
            for (var i = 0; i < options.InvigilatorsPerSchedule; i++)
            {
                var teacherId = teachers[teacherCursor % teachers.Count];
                teacherCursor++;

                await conn.ExecuteAsync(
                    @"INSERT INTO examinvigilator
                          (tenantid, schoolid, campusid, examscheduleid, teacherid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @scheduleId, @teacherId, 1, 1, @now, @now)",
                    new { tenantId, schoolId, campusId, scheduleId, teacherId, now });

                result.ExamInvigilators++;
            }
        }

        await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);

        if (verbose)
        {
            Console.WriteLine(
                $"  Exam tooling: campus {campusId} -> {result.AssessmentTools} tools, " +
                $"{result.AssessmentToolItems} criteria, {result.RubricCriterionLevels} rubric levels, " +
                $"{result.ExamInvigilators} invigilators, {result.PerformanceScaleLevels} scale levels");
        }

        return result;
    }

    /// <summary>
    /// Clear this campus's rubric levels. ⚠️ NOT `ClearTableByParentAsync`: a level's parent is an
    /// `assessmenttoolitem`, which carries no scope of its own, so the scope has to be resolved one
    /// link further up at `assessmenttool`. Getting this wrong is not a soft failure - the helper
    /// tolerates an FK refusal but not a **42703**, which is what a scoped DELETE on a scope-less
    /// table raises, and it aborts the whole seed.
    /// </summary>
    private static async Task ClearRubricLevelsThroughToolAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId)
    {
        await conn.ExecuteAsync(
            @"DELETE FROM rubriccriterionlevel
               WHERE assessmenttoolitemid IN (
                   SELECT i.id FROM assessmenttoolitem i
                     JOIN assessmenttool at ON at.id = i.assessmenttoolid
                    WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId)",
            new { tenantId, schoolId, campusId });
    }

    /// <summary>
    /// Fills <paramref name="result"/> from what the campus already holds, so a skipped campus is
    /// reported exactly like a freshly seeded one.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, ExamToolingSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $@"SELECT COUNT(*) FROM {table}
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.AssessmentTools = await ScopedAsync("assessmenttool");
        result.ExamInvigilators = await ScopedAsync("examinvigilator");
        result.PerformanceScales = await ScopedAsync("performancescale");

        // `assessmenttoolitem` and `rubriccriterionlevel` carry no scope columns, so they are counted
        // through the tool the reads themselves reach them through.
        result.AssessmentToolItems = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM assessmenttoolitem i
                JOIN assessmenttool t ON t.id = i.assessmenttoolid
               WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.RubricCriterionLevels = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM rubriccriterionlevel l
                JOIN assessmenttoolitem i ON i.id = l.assessmenttoolitemid
                JOIN assessmenttool t ON t.id = i.assessmenttoolid
               WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.PerformanceScaleLevels = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM performancescalelevel l
                JOIN performancescale s ON s.id = l.performancescaleid
               WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId",
            new { tenantId, schoolId, campusId });
    }

    /// <summary>
    /// The one enum member this seeder reads, mirrored rather than referenced: `data-volume` does
    /// not project the API, and a member that is renamed there must be renamed here (the same note
    /// the reporting seeder carries for its own status tokens).
    /// </summary>
    private enum StaticAssessmentMethod
    {
        Rubric = 3
    }
}
