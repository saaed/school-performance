using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>
/// Options for <see cref="ExamsModuleSeeder"/>. The defaults give one campus a full assessment
/// cycle: every enrolled student, assessed against a real component set, plus the term result the
/// grade/subject performance reports read.
/// </summary>
public sealed class ExamsSeedOptions
{
    /// <summary>
    /// Assessment components the subject is graded on (Quiz / Assignment / Final Exam). Three is
    /// what a real term carries, and it is the axis that makes the assessment report a real read
    /// rather than a single row per student.
    /// </summary>
    public int ComponentsPerSubject { get; set; } = 3;

    /// <summary>
    /// Scored items behind each assessment. The view sums them in a LATERAL
    /// (`SUM(sai.numericvalue)`), so a submission with no items scores ZERO - which is why the
    /// seeder must write them and why the report would otherwise read a campus of empty marks.
    /// </summary>
    public int ItemsPerAssessment { get; set; } = 2;

    /// <summary>Re-seed even when the campus already holds results.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's exams seed produced. Returned so a run can report real counts.</summary>
public sealed class ExamsSeedResult
{
    public bool Skipped { get; set; }
    public int AssessmentComponents { get; set; }
    public int AcademicGradeSubjects { get; set; }
    public int SubjectAssessmentComponents { get; set; }
    public int ExamSchedules { get; set; }
    public int StudentExams { get; set; }
    public int StudentAssessments { get; set; }
    public int AssessmentItems { get; set; }
    public int SubjectResults { get; set; }
    public int FinalResults { get; set; }
    public int ReportCards { get; set; }
}

/// <summary>
/// Seeds the EXAMS / ASSESSMENT / RESULT spine and its facts for one campus.
///
/// WHY THIS EXISTS
/// ---------------
/// `studentsubjectresult`, `studentassessment`, `studentexam`, `examschedule`,
/// `subjectassessmentcomponent` and `academicgradesubject` all held ZERO rows, so four shipped
/// reports - `GRADE_PERFORMANCE`, `SUBJECT_PERFORMANCE`, `ASSESSMENT_PERFORMANCE` and (through the
/// curriculum tree) `LEARNING_OUTCOME_PERFORMANCE` - reported SKIP. A spec over an empty table is
/// honest and useless at the same time: it reads as coverage while measuring nothing.
///
/// ⚠️ THE SPINE IS THE POINT, NOT THE ROW COUNT. `vw_student_subject_results` and
/// `vw_assessment_performance` are INNER JOIN chains six and eight tables deep, so a fact row whose
/// `academicgradesubjectid` or `subjectassessmentcomponentid` does not resolve is INVISIBLE to the
/// view - the table looks populated and the report returns nothing. That is exactly the defect the
/// attendance seeder shipped once (`attendance.studentenrollmentid` NULL on all 14.2M rows, while
/// `vw_student_attendance` joins on that column). Every row written here is therefore written
/// through a resolved parent, never with a placeholder id:
///
///     academicgrade  ->  academicgradesubject          (the campus's teachable subject)
///     curriculumgrade/subject  ->  curriculumgradesubject
///     assessmentcomponent  ->  subjectassessmentcomponent  ->  examschedule
///                          ->  studentexam  ->  studentassessment  ->  studentassessmentitem
///     studentenrollment  ->  studentsubjectresult
///
/// ⚠️ `academicgradesubject` IS KEYED ON THE CLASSROOM'S OWN `academicgradeid`. On `ayra_perf` the
/// decorated campuses' classrooms reference an `academicgrade` row that belongs to campus 1 (the
/// dataset's pre-existing cross-scope quirk), and the views join `classroom -> academicgrade`
/// WITHOUT a scope check. So the subject mapping is built from the classroom the students are
/// enrolled in rather than from a freshly invented grade - inventing one would produce a spine the
/// views cannot reach.
///
/// ⚠️ `assessmentcomponent` IS A CAMPUS-SCOPED REFERENCE TABLE with ZERO rows in every database
/// here, while `subjectassessmentcomponent.assessmentcomponentid` is NOT NULL. It therefore has to
/// be created, the same way the HR seeder creates `attendancestatus` and `payrollperiod`.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds subject results is skipped unless Force is
/// set, so re-running cannot silently double the volume.
/// </summary>
public sealed class ExamsModuleSeeder : BaseSeeder
{
    public ExamsModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk exams load invalidates, so <see cref="PerfDatasetSeeder"/> can ANALYZE them.
    /// Without fresh statistics the planner reasons from the row counts that described an EMPTY
    /// table - the failure mode already measured on `classroom` and on the student grid.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "assessmentcomponent", "academicgradesubject", "subjectassessmentcomponent",
        "examschedule", "studentexam", "studentassessment", "studentassessmentitem",
        "studentsubjectresult", "studentfinalresult", "studentreportcard"
    };

    /// <summary>
    /// The component set a term carries. `maxmarks` / `passingmarks` are load-bearing: the view
    /// derives `percentage` from `maxmarks` and `ispass` from `passingmarks`, so a campus seeded
    /// with zeros would report every student at 0% and failing.
    /// </summary>
    private static readonly (string Name, decimal MaxMarks, decimal PassingMarks)[] Components =
    {
        ("Quiz", 10m, 4m),
        ("Assignment", 20m, 8m),
        ("Mid Term", 50m, 20m),
        ("Final Exam", 100m, 40m),
    };

    private const long TermResultTotalMarks = 100L;

    public async Task<ExamsSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, ExamsSeedOptions options, bool verbose = true)
    {
        var result = new ExamsSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM studentsubjectresult
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            // ⚠️ THE YEAR-END ROWS ARE BACKFILLED EVEN ON A SKIP - and that is the whole reason a
            // skipped campus is not "done". `studentfinalresult`/`studentreportcard` were added to
            // this seeder AFTER the subject results were first written, so the campuses that carry
            // results are exactly the ones missing them. Both writes are `NOT EXISTS`-guarded, so a
            // campus that already holds them comes back unchanged; running it here means a fixture
            // run does not need `SCUBE_PERF_FORCE=1` (and a full 6,000-assessment rebuild) just to
            // pick up the two new tables.
            var backfillNow = DateTime.UtcNow;
            await InsertFinalResultsAsync(conn, tenantId, schoolId, campusId, backfillNow);
            await InsertReportCardsAsync(conn, tenantId, schoolId, campusId, backfillNow);

            result.Skipped = true;

            // ⚠️ A SKIPPED CAMPUS MUST STILL REPORT WHAT IT HOLDS. Returning early with zeros makes
            // a caller's total describe what THIS RUN wrote rather than what the campus has - and
            // since the whole point of the fixture is "the report has data to measure", a truthful
            // count is the only useful answer. Measured: the second run of the fixture summed 0
            // assessments over six campuses that held 30,300.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);

            if (verbose)
            {
                Console.WriteLine(
                    $"  Exams: campus {campusId} already holds {existing:N0} subject results - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 0. A forced re-seed clears first, CHILDREN FIRST. `studentassessmentitem` carries no
        //    scope columns of its own, so it goes through its parent (a scoped DELETE on it is a
        //    42703 that would abort the whole seed).
        // ------------------------------------------------------------------
        if (options.Force && existing > 0)
        {
            await ClearTableByParentAsync(
                conn, "studentassessmentitem", "studentassessmentid", "studentassessment",
                tenantId, schoolId, campusId);

            foreach (var table in new[]
                     {
                         "studentassessment", "studentexam", "examschedule",
                         "subjectassessmentcomponent", "studentsubjectresult",
                         "academicgradesubject", "assessmentcomponent",
                         "studentreportcard", "studentfinalresult"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
            {
                Console.WriteLine($"  Exams: campus {campusId} cleared for a forced re-seed");
            }
        }

        // ------------------------------------------------------------------
        // 1. The campus's classrooms, and the students enrolled in them. The classroom carries the
        //    `academicgradeid` the whole spine hangs off; the enrollment is what a result is
        //    recorded against, so both are read rather than invented.
        // ------------------------------------------------------------------
        var classrooms = (await conn.QueryAsync<ClassroomRow>(
            @"SELECT id AS ClassroomId, academicgradeid AS AcademicGradeId
                FROM classroom
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND academicgradeid IS NOT NULL
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (classrooms.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId} holds no classroom, so its assessment spine cannot be seeded - " +
                "run PerfDatasetSeeder first.");
        }

        var enrollments = (await conn.QueryAsync<EnrollmentRow>(
            @"SELECT id AS EnrollmentId, classroomid AS ClassroomId, academicyearid AS AcademicYearId
                FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND classroomid IS NOT NULL
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (enrollments.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId} holds no enrolled student, so no result or assessment could " +
                "reference one.");
        }

        // ------------------------------------------------------------------
        // 2. The TERM every assessment and result is dated against. `subjectassessmentcomponent`,
        //    `curriculumtopicplan` and `studentsubjectresult` all carry a NOT NULL / FK `termid`,
        //    and it must be a real row in `terms` - so it is resolved through the enrollment's own
        //    academic year rather than assumed.
        // ------------------------------------------------------------------
        var termId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT t.id FROM terms t
                JOIN studentenrollment se ON se.academicyearid = t.academicyearid
               WHERE se.tenantid = @tenantId AND se.schoolid = @schoolId AND se.campusid = @campusId
               ORDER BY t.id LIMIT 1",
            new { tenantId, schoolId, campusId });

        if (termId is null || termId == 0)
        {
            throw new InvalidOperationException(
                $"the academic year campus {campusId} enrolls into holds no term, and both the " +
                "assessment component and the subject result require one.");
        }

        // ------------------------------------------------------------------
        // 3. The PUBLISHED curriculum's subject for each grade the campus teaches. The views reach
        //    a subject through `curriculumgradesubject` (or `campussubject`), so a result whose
        //    subject is not mapped in the curriculum resolves to a NULL subject name.
        // ------------------------------------------------------------------
        var gradeIds = classrooms.Select(c => c.AcademicGradeId).Distinct().ToList();

        // ⚠️ The ids are INLINED, not bound as a list parameter. Npgsql binds a `List<>` as a single
        // PostgreSQL ARRAY, so `IN @GradeIds` reaches the server as `IN ($3)` - a syntax error, not a
        // type error - and Dapper's array expansion is NOT applied for Npgsql. The ids come from the
        // database, so inlining is safe and is the house pattern (`CurriculumVersionRepository`).
        var gradeIdList = string.Join(", ", gradeIds);

        var curriculumSubjects = (await conn.QueryAsync<CurriculumSubjectRow>(
            $@"SELECT cg.id AS CurriculumGradeId, cgs.id AS CurriculumGradeSubjectId
                FROM curriculumgradesubject cgs
                JOIN curriculumgrade cg ON cg.id = cgs.curriculumgradeid
                JOIN curriculumversion cv ON cv.id = cg.curriculumversionid
                JOIN curriculum c ON c.id = cv.curriculumid
               WHERE c.tenantid = @tenantId AND c.schoolid = @schoolId
                 AND cv.curriculumstatus = 5
                 AND cg.id IN ({gradeIdList})
               ORDER BY cgs.id",
            new { tenantId, schoolId })).ToList();

        if (curriculumSubjects.Count == 0)
        {
            throw new InvalidOperationException(
                $"no PUBLISHED curriculum maps a subject for campus {campusId}'s grade(s) " +
                $"({string.Join(", ", gradeIds)}), so the result views would return a NULL subject " +
                "name and no report row would be reachable.");
        }

        // ------------------------------------------------------------------
        // 4. `assessmentcomponent` - the reference rows `subjectassessmentcomponent` cannot live
        //    without. Campus-scoped, and read back when they already exist so a re-seed that only
        //    cleared the FACTS does not collide.
        // ------------------------------------------------------------------
        var componentIds = new List<long>();
        foreach (var component in Components.Take(options.ComponentsPerSubject))
        {
            var id = await conn.ExecuteScalarAsync<long?>(
                @"SELECT id FROM assessmentcomponent
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND name = @name",
                new { tenantId, schoolId, campusId, name = component.Name });

            if (id is null || id == 0)
            {
                id = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO assessmentcomponent
                          (tenantid, schoolid, campusid, name, weightage, maxmarks, passingmarks,
                           sequence, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @campusId, @name, @weightage, @maxmarks,
                              @passingmarks, @sequence, 1, 1, @now, @now)
                      RETURNING id",
                    new
                    {
                        tenantId, schoolId, campusId,
                        name = component.Name,
                        weightage = component.MaxMarks,
                        maxmarks = component.MaxMarks,
                        passingmarks = component.PassingMarks,
                        sequence = componentIds.Count + 1,
                        now
                    });
            }

            componentIds.Add(id.Value);
        }
        result.AssessmentComponents = componentIds.Count;

        // ------------------------------------------------------------------
        // 5. `academicgradesubject` - the campus's teachable (grade, subject) pairs. This is the
        //    join `vw_student_subject_results` needs, and the parent every subject assessment
        //    component hangs off.
        // ------------------------------------------------------------------
        var agsIds = new List<long>();
        var agsByGrade = new Dictionary<long, long>();

        foreach (var classroom in classrooms)
        {
            foreach (var mapping in curriculumSubjects.Where(m => m.CurriculumGradeId == classroom.AcademicGradeId))
            {
                var id = await conn.ExecuteScalarAsync<long?>(
                    @"SELECT id FROM academicgradesubject
                       WHERE academicgradeid = @academicGradeId
                         AND curriculumgradesubjectid = @curriculumGradeSubjectId",
                    new
                    {
                        academicGradeId = classroom.AcademicGradeId,
                        curriculumGradeSubjectId = mapping.CurriculumGradeSubjectId
                    });

                if (id is null || id == 0)
                {
                    id = await conn.ExecuteScalarAsync<long>(
                        @"INSERT INTO academicgradesubject
                              (tenantid, schoolid, campusid, academicgradeid, curriculumgradesubjectid,
                               weeklyperiods, ismandatory, displayorder, isactive,
                               createdby, modifiedby, createdon, modifiedon)
                          VALUES (@tenantId, @schoolId, @campusId, @academicGradeId,
                                  @curriculumGradeSubjectId, 5, true, @displayOrder, true,
                                  1, 1, @now, @now)
                          RETURNING id",
                        new
                        {
                            tenantId, schoolId, campusId,
                            academicGradeId = classroom.AcademicGradeId,
                            curriculumGradeSubjectId = mapping.CurriculumGradeSubjectId,
                            displayOrder = mapping.CurriculumGradeSubjectId,
                            now
                        });
                }

                agsIds.Add(id.Value);
                agsByGrade[classroom.AcademicGradeId] = id.Value;
            }
        }

        if (agsIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"no academicgradesubject row could be built for campus {campusId}, so no result " +
                "would reach the subject-results view.");
        }
        result.AcademicGradeSubjects = agsIds.Count;

        // ------------------------------------------------------------------
        // 6. `subjectassessmentcomponent` - one per (subject, component, term). The unique index
        //    `ux_subjectassessment_comp_subject_term` is exactly this triple, so the shape is
        //    dictated by the schema.
        // ------------------------------------------------------------------
        var sacRows = new List<SacRow>();
        foreach (var agsId in agsIds)
        {
            foreach (var componentId in componentIds)
            {
                var component = Components[componentIds.IndexOf(componentId)];
                sacRows.Add(new SacRow
                {
                    AcademicGradeSubjectId = agsId,
                    AssessmentComponentId = componentId,
                    TermId = termId.Value,
                    MaxMarks = component.MaxMarks,
                    PassingMarks = component.PassingMarks,
                    Weightage = component.MaxMarks,
                });
            }
        }

        var sacIds = await InsertSubjectAssessmentComponentsAsync(
            conn, tenantId, schoolId, campusId, sacRows, now);
        result.SubjectAssessmentComponents = sacIds.Count;

        // ------------------------------------------------------------------
        // 7. `examschedule` - one sitting per subject assessment component, dated inside the term.
        //    `examschedule.status` is left at the product's own `Published` value: the marks
        //    workspace and the teacher desk read published schedules only, and a Draft would make
        //    the module look empty from every screen that matters.
        // ------------------------------------------------------------------
        var scheduleRows = new List<ScheduleRow>();
        var examDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(7);
        for (var i = 0; i < sacIds.Count; i++)
        {
            scheduleRows.Add(new ScheduleRow
            {
                ClassroomId = classrooms[i % classrooms.Count].ClassroomId,
                SubjectAssessmentComponentId = sacIds[i],
                ExamDate = examDate.AddDays(i),
                StartTime = examDate.AddDays(i).AddHours(9),
                EndTime = examDate.AddDays(i).AddHours(11),
            });
        }

        var scheduleIds = await InsertSchedulesAsync(
            conn, tenantId, schoolId, campusId, scheduleRows, now);
        result.ExamSchedules = scheduleIds.Count;

        // ------------------------------------------------------------------
        // 8. `studentexam` - one registration per student per sitting, then `studentassessment` for
        //    each of those, then the scored items behind it. This is the module's volume.
        // ------------------------------------------------------------------
        var examRows = new List<StudentExamRow>();
        foreach (var scheduleId in scheduleIds)
        {
            foreach (var enrollment in enrollments)
            {
                examRows.Add(new StudentExamRow
                {
                    ExamScheduleId = scheduleId,
                    StudentEnrollmentId = enrollment.EnrollmentId,
                });
            }
        }

        var studentExamIds = await InsertStudentExamsAsync(
            conn, tenantId, schoolId, campusId, examRows, now, verbose);
        result.StudentExams = studentExamIds.Count;

        var assessmentIds = await InsertStudentAssessmentsAsync(
            conn, tenantId, schoolId, campusId, examRows, studentExamIds, now, verbose);
        result.StudentAssessments = assessmentIds.Count;

        result.AssessmentItems = await InsertAssessmentItemsAsync(
            conn, tenantId, campusId, assessmentIds, options.ItemsPerAssessment, now, verbose);

        // ------------------------------------------------------------------
        // 9. `studentsubjectresult` - the term result per enrollment per subject. This is what
        //    `GRADE_PERFORMANCE` and `SUBJECT_PERFORMANCE` page over, and it is written LAST so a
        //    failure above leaves the campus in the cheap-to-detect "no results" state rather than
        //    a half-built one.
        // ------------------------------------------------------------------
        result.SubjectResults = await InsertSubjectResultsAsync(
            conn, tenantId, schoolId, campusId, enrollments, agsByGrade, termId.Value, now, verbose);

        // ------------------------------------------------------------------
        // 10. `studentfinalresult` + `studentreportcard` - the YEAR-END result the report-card grid
        //     pages over. A report card is NOT NULL against a final result, so a campus with term
        //     results but no final results reports **SKIP** on `student-report-card-page` while every
        //     other exams spec is green - a grid the application ships, sitting unmeasured.
        //
        //     Both are derived from the SUBJECT results just written (summed per enrollment), so the
        //     numbers are the campus's own rather than an invented constant, and a term result the
        //     report cannot see is impossible by construction.
        // ------------------------------------------------------------------
        result.FinalResults = await InsertFinalResultsAsync(conn, tenantId, schoolId, campusId, now);
        result.ReportCards = await InsertReportCardsAsync(conn, tenantId, schoolId, campusId, now);

        if (verbose)
        {
            Console.WriteLine(
                $"  Exams: campus {campusId} -> {result.AssessmentComponents} components, " +
                $"{result.AcademicGradeSubjects} grade-subjects, " +
                $"{result.SubjectAssessmentComponents} subject components, " +
                $"{result.ExamSchedules} schedules, {result.StudentExams:N0} registrations, " +
                $"{result.StudentAssessments:N0} assessments, " +
                $"{result.AssessmentItems:N0} scored items, " +
                $"{result.SubjectResults:N0} term results, " +
                $"{result.FinalResults:N0} final results, {result.ReportCards:N0} report cards");
        }

        return result;
    }

    /// <summary>
    /// One `studentfinalresult` per enrollment, aggregated over that enrollment's OWN subject
    /// results. `NOT EXISTS` makes it re-runnable: a campus that already holds a final result for an
    /// enrollment is left alone rather than duplicated (there is no unique index to catch it).
    /// </summary>
    private static async Task<int> InsertFinalResultsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, DateTime now)
    {
        const string sql = @"
            INSERT INTO studentfinalresult
                (tenantid, schoolid, campusid, studentenrollmentid, academicyearid,
                 totalmarks, obtained, percentage, overallgrade, rank, promotiondecision, gradepoint,
                 createdby, modifiedby, createdon, modifiedon)
            SELECT @tenantId, @schoolId, @campusId, se.id, se.academicyearid,
                   agg.totalmarks, agg.obtained,
                   CASE WHEN agg.totalmarks > 0
                        THEN ROUND(agg.obtained * 100.0 / agg.totalmarks, 2) ELSE 0 END,
                   CASE WHEN agg.totalmarks > 0 AND agg.obtained * 100.0 / agg.totalmarks >= 40
                        THEN 'Pass' ELSE 'Fail' END,
                   NULL,
                   CASE WHEN agg.totalmarks > 0 AND agg.obtained * 100.0 / agg.totalmarks >= 40
                        THEN 'Promoted' ELSE 'Repeat' END,
                   0,
                   1, 1, @now, @now
              FROM studentenrollment se
              JOIN (
                    SELECT ssr.studentenrollmentid,
                           SUM(ssr.totalmarks)    AS totalmarks,
                           SUM(ssr.obtainedmarks) AS obtained
                      FROM studentsubjectresult ssr
                     WHERE ssr.tenantid = @tenantId AND ssr.schoolid = @schoolId
                       AND ssr.campusid = @campusId
                  GROUP BY ssr.studentenrollmentid
                   ) agg ON agg.studentenrollmentid = se.id
             WHERE se.tenantid = @tenantId AND se.schoolid = @schoolId AND se.campusid = @campusId
               AND NOT EXISTS (
                       SELECT 1 FROM studentfinalresult fr
                        WHERE fr.studentenrollmentid = se.id)";

        return await conn.ExecuteAsync(sql, new { tenantId, schoolId, campusId, now });
    }

    /// <summary>
    /// One `studentreportcard` per final result - the row `StudentReportCardRepository.GetAll`
    /// pages over. `published = true` because an unpublished card is a DRAFT on the document, and
    /// this fixture's job is to make the GRID measurable.
    /// </summary>
    private static async Task<int> InsertReportCardsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, DateTime now)
    {
        const string sql = @"
            INSERT INTO studentreportcard
                (tenantid, schoolid, campusid, studentfinalresultid, generateddate, generatedby,
                 published, createdby, modifiedby, createdon, modifiedon)
            SELECT fr.tenantid, fr.schoolid, fr.campusid, fr.id, @now, 'perf-seed',
                   true, 1, 1, @now, @now
              FROM studentfinalresult fr
             WHERE fr.tenantid = @tenantId AND fr.schoolid = @schoolId
               AND fr.campusid = @campusId
               AND NOT EXISTS (
                       SELECT 1 FROM studentreportcard rc WHERE rc.studentfinalresultid = fr.id)";

        return await conn.ExecuteAsync(sql, new { tenantId, schoolId, campusId, now });
    }

    /// <summary>
    /// Fills <paramref name="result"/> from what the campus already holds, so a skipped campus is
    /// reported the same way as a freshly seeded one.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, ExamsSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $@"SELECT COUNT(*) FROM {table}
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.AssessmentComponents = await ScopedAsync("assessmentcomponent");
        result.AcademicGradeSubjects = await ScopedAsync("academicgradesubject");
        result.SubjectAssessmentComponents = await ScopedAsync("subjectassessmentcomponent");
        result.ExamSchedules = await ScopedAsync("examschedule");
        result.StudentExams = await ScopedAsync("studentexam");
        result.StudentAssessments = await ScopedAsync("studentassessment");
        result.SubjectResults = await ScopedAsync("studentsubjectresult");
        result.FinalResults = await ScopedAsync("studentfinalresult");
        result.ReportCards = await ScopedAsync("studentreportcard");

        // `studentassessmentitem` carries no scope columns, so it is counted through its parent.
        result.AssessmentItems = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM studentassessmentitem i
                JOIN studentassessment sa ON sa.id = i.studentassessmentid
               WHERE sa.tenantid = @tenantId AND sa.schoolid = @schoolId AND sa.campusid = @campusId",
            new { tenantId, schoolId, campusId });
    }

    // ----------------------------------------------------------------------
    // Row shapes. A private NESTED class can fail to instantiate under Dapper at runtime, so
    // these are all internal-shaped and simple.
    // ----------------------------------------------------------------------

    internal sealed class ClassroomRow
    {
        public long ClassroomId { get; set; }
        public long AcademicGradeId { get; set; }
    }

    internal sealed class EnrollmentRow
    {
        public long EnrollmentId { get; set; }
        public long ClassroomId { get; set; }
        public long AcademicYearId { get; set; }
    }

    internal sealed class CurriculumSubjectRow
    {
        public long CurriculumGradeId { get; set; }
        public long CurriculumGradeSubjectId { get; set; }
    }

    internal sealed class SacRow
    {
        public long AcademicGradeSubjectId { get; set; }
        public long AssessmentComponentId { get; set; }
        public long TermId { get; set; }
        public decimal MaxMarks { get; set; }
        public decimal PassingMarks { get; set; }
        public decimal Weightage { get; set; }
    }

    internal sealed class ScheduleRow
    {
        public long ClassroomId { get; set; }
        public long SubjectAssessmentComponentId { get; set; }
        public DateTime ExamDate { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    internal sealed class StudentExamRow
    {
        public long ExamScheduleId { get; set; }
        public long StudentEnrollmentId { get; set; }
    }

    private sealed class ScheduleComponentRow
    {
        public long ScheduleId { get; set; }
        public long SacId { get; set; }
    }

    // ----------------------------------------------------------------------
    // Batched inserts. Each returns its ids in INSERT ORDER, which is what lets the caller pair a
    // child row with the parent it was generated for.
    // ----------------------------------------------------------------------

    private static async Task<List<long>> InsertSubjectAssessmentComponentsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<SacRow> rows, DateTime now)
    {
        if (rows.Count == 0) return new List<long>();

        const string sql = @"
            INSERT INTO subjectassessmentcomponent
                (tenantid, schoolid, campusid, assessmentcomponentid, academicgradesubjectid,
                 termid, weightage, maxmarks, passingmarks,
                 teachercanedit, attendancerequired, converttograde, showrawresult,
                 createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                   unnest(@ComponentIds), unnest(@AgsIds),
                   unnest(@TermIds), unnest(@Weightages), unnest(@MaxMarks), unnest(@PassingMarks),
                   true, false, true, true, 1, 1, @now, @now
            RETURNING id";

        var ids = await conn.QueryAsync<long>(sql, new
        {
            TenantIds = System.Linq.Enumerable.Repeat(tenantId, rows.Count).ToArray(),
            SchoolIds = System.Linq.Enumerable.Repeat(schoolId, rows.Count).ToArray(),
            CampusIds = System.Linq.Enumerable.Repeat(campusId, rows.Count).ToArray(),
            ComponentIds = rows.Select(r => r.AssessmentComponentId).ToArray(),
            AgsIds = rows.Select(r => r.AcademicGradeSubjectId).ToArray(),
            TermIds = rows.Select(r => r.TermId).ToArray(),
            Weightages = rows.Select(r => r.Weightage).ToArray(),
            MaxMarks = rows.Select(r => r.MaxMarks).ToArray(),
            PassingMarks = rows.Select(r => r.PassingMarks).ToArray(),
            now
        });

        return ids.ToList();
    }

    private static async Task<List<long>> InsertSchedulesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<ScheduleRow> rows, DateTime now)
    {
        if (rows.Count == 0) return new List<long>();

        // `starttime` / `endtime` are `time without time zone` while the C# values are DateTime,
        // so the cast is explicit - the same one-sided-type trap `employeeattendance.checkintime`
        // recorded (`42804: column is of type time ... but expression is of type timestamp`).
        const string sql = @"
            INSERT INTO examschedule
                (tenantid, schoolid, campusid, classroomid, subjectassessmentcomponentid,
                 examdate, starttime, endtime, status, createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                   unnest(@ClassroomIds), unnest(@SacIds),
                   unnest(@ExamDates)::date, unnest(@StartTimes)::time, unnest(@EndTimes)::time,
                   'Published', 1, 1, @now, @now
            RETURNING id";

        var ids = await conn.QueryAsync<long>(sql, new
        {
            TenantIds = System.Linq.Enumerable.Repeat(tenantId, rows.Count).ToArray(),
            SchoolIds = System.Linq.Enumerable.Repeat(schoolId, rows.Count).ToArray(),
            CampusIds = System.Linq.Enumerable.Repeat(campusId, rows.Count).ToArray(),
            ClassroomIds = rows.Select(r => r.ClassroomId).ToArray(),
            SacIds = rows.Select(r => r.SubjectAssessmentComponentId).ToArray(),
            ExamDates = rows.Select(r => r.ExamDate.Date).ToArray(),
            StartTimes = rows.Select(r => r.StartTime).ToArray(),
            EndTimes = rows.Select(r => r.EndTime).ToArray(),
            now
        });

        return ids.ToList();
    }

    private async Task<List<long>> InsertStudentExamsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<StudentExamRow> rows, DateTime now, bool verbose)
    {
        var ids = new List<long>(rows.Count);
        const int batchSize = 2000;

        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var batch = rows.Skip(offset).Take(batchSize).ToList();

            const string sql = @"
                INSERT INTO studentexam
                    (tenantid, schoolid, campusid, examscheduleid, studentenrollmentid,
                     seatnumber, attendance, status, createdby, modifiedby, createdon, modifiedon)
                SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                       unnest(@ScheduleIds), unnest(@EnrollmentIds),
                       unnest(@SeatNumbers), 'Present', 'Registered', 1, 1, @now, @now
                RETURNING id";

            var batchIds = await conn.QueryAsync<long>(sql, new
            {
                TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
                SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
                CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
                ScheduleIds = batch.Select(r => r.ExamScheduleId).ToArray(),
                EnrollmentIds = batch.Select(r => r.StudentEnrollmentId).ToArray(),
                SeatNumbers = batch.Select((_, i) => $"P-{offset + i + 1}").ToArray(),
                now
            });

            ids.AddRange(batchIds);
            if (verbose) LogProgress($"  Exams registrations (campus {campusId})", ids.Count, rows.Count);
        }

        return ids;
    }

    private async Task<List<long>> InsertStudentAssessmentsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<StudentExamRow> examRows, List<long> studentExamIds, DateTime now, bool verbose)
    {
        // One assessment per registration, carrying the component the sitting examined. The view
        // INNER JOINs `examschedule -> subjectassessmentcomponent`, so a NULL here is invisible.
        //
        // ⚠️ A named row class, not a ValueTuple. Dapper maps a tuple by POSITION and the shape is
        // easy to get silently wrong (the API's own catalogue records the same trap), and a
        // `private` nested class maps fine for a read like this - it is only Dapper's
        // ROW-CONSTRUCTION path that can refuse one.
        var sacBySchedule = await conn.QueryAsync<ScheduleComponentRow>(
            @"SELECT id AS ScheduleId, subjectassessmentcomponentid AS SacId
                FROM examschedule
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });
        var sacLookup = sacBySchedule.ToDictionary(x => x.ScheduleId, x => x.SacId);

        var ids = new List<long>(studentExamIds.Count);
        const int batchSize = 2000;
        var index = 0;

        for (var offset = 0; offset < studentExamIds.Count; offset += batchSize)
        {
            var batchExamIds = studentExamIds.Skip(offset).Take(batchSize).ToList();
            var batchSacIds = new List<long>(batchExamIds.Count);

            foreach (var _ in batchExamIds)
            {
                var scheduleId = examRows[index].ExamScheduleId;
                batchSacIds.Add(sacLookup[scheduleId]);
                index++;
            }

            const string sql = @"
                INSERT INTO studentassessment
                    (tenantid, schoolid, campusid, studentexamid, subjectassessmentcomponentid,
                     isabsent, isexempt, enteredby, entereddate, submittedby, submittedon,
                     createdby, modifiedby, createdon, modifiedon)
                SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                       unnest(@ExamIds), unnest(@SacIds),
                       false, false, 1, @now, 1, @now, 1, 1, @now, @now
                RETURNING id";

            var batchIds = await conn.QueryAsync<long>(sql, new
            {
                TenantIds = System.Linq.Enumerable.Repeat(tenantId, batchExamIds.Count).ToArray(),
                SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batchExamIds.Count).ToArray(),
                CampusIds = System.Linq.Enumerable.Repeat(campusId, batchExamIds.Count).ToArray(),
                ExamIds = batchExamIds.ToArray(),
                SacIds = batchSacIds.ToArray(),
                now
            });

            ids.AddRange(batchIds);
            if (verbose) LogProgress($"  Exams assessments (campus {campusId})", ids.Count, studentExamIds.Count);
        }

        return ids;
    }

    private async Task<int> InsertAssessmentItemsAsync(
        NpgsqlConnection conn, long tenantId, long campusId,
        List<long> assessmentIds, int itemsPerAssessment, DateTime now, bool verbose)
    {
        if (itemsPerAssessment <= 0) return 0;

        var inserted = 0;
        var total = assessmentIds.Count * itemsPerAssessment;
        const int batchSize = 4000;
        var rows = new List<(long AssessmentId, decimal Value)>(batchSize);

        foreach (var assessmentId in assessmentIds)
        {
            for (var i = 0; i < itemsPerAssessment; i++)
            {
                // A spread inside a passing range, so the summed LATERAL produces a percentage that
                // is neither all-zero nor all-perfect - a single value makes the report's aggregate
                // columns measure one branch.
                rows.Add((assessmentId, Random.Shared.Next(60, 100) / 10m));
            }

            if (rows.Count >= batchSize)
            {
                inserted += await InsertItemBatchAsync(conn, rows, now);
                rows.Clear();
                if (verbose) LogProgress($"  Exams scored items (campus {campusId})", inserted, total);
            }
        }

        if (rows.Count > 0)
        {
            inserted += await InsertItemBatchAsync(conn, rows, now);
        }

        if (verbose) LogProgress($"  Exams scored items (campus {campusId})", inserted, total);
        return inserted;
    }

    private static async Task<int> InsertItemBatchAsync(
        NpgsqlConnection conn, List<(long AssessmentId, decimal Value)> rows, DateTime now)
    {
        const string sql = @"
            INSERT INTO studentassessmentitem
                (studentassessmentid, numericvalue, createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@AssessmentIds), unnest(@Values), 1, 1, @now, @now";

        return await conn.ExecuteAsync(sql, new
        {
            AssessmentIds = rows.Select(r => r.AssessmentId).ToArray(),
            Values = rows.Select(r => r.Value).ToArray(),
            now
        });
    }

    private async Task<int> InsertSubjectResultsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<EnrollmentRow> enrollments, Dictionary<long, long> agsByGrade,
        long termId, DateTime now, bool verbose)
    {
        // `resultstatus` is the vocabulary `vw_student_subject_results` translates into
        // ispass/isfail/isexempt/isabsent - 'Pass' / 'Fail' / 'Exempt' / 'Absent' and nothing else.
        //
        // ⚠️ The result is recorded against the subject of the grade the STUDENT'S OWN CLASSROOM
        // belongs to, resolved through `classroom.academicgradeid`. Writing every subject in
        // `agsByGrade` for every student would put a grade-7 student's name against a grade-12
        // subject, and the report groups by grade - so the wrong mapping reads as a plausible
        // cohort until someone looks at the names.
        var classroomGrade = await conn.QueryAsync<ClassroomRow>(
            @"SELECT id AS ClassroomId, academicgradeid AS AcademicGradeId
                FROM classroom
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND academicgradeid IS NOT NULL",
            new { tenantId, schoolId, campusId });
        var gradeByClassroom = classroomGrade.ToDictionary(c => c.ClassroomId, c => c.AcademicGradeId);

        var rows = new List<ResultRow>(enrollments.Count);

        foreach (var enrollment in enrollments)
        {
            var subjectIds = new List<long>();
            if (gradeByClassroom.TryGetValue(enrollment.ClassroomId, out var gradeId) &&
                agsByGrade.TryGetValue(gradeId, out var matchedAgs))
            {
                subjectIds.Add(matchedAgs);
            }
            else
            {
                // A classroom whose grade the curriculum does not map would otherwise write NO
                // result - a student silently missing from the report. Fall back to what the
                // campus does teach, so the row exists and the mapping gap is visible as a
                // mis-grouping rather than as an absence.
                subjectIds.AddRange(agsByGrade.Values.Distinct());
            }

            // A result per subject the campus teaches for that grade, spread the way a real cohort
            // is: mostly passing, with a failing tail the report can find.
            foreach (var agsId in subjectIds)
            {
                var obtained = Random.Shared.Next(35, 99);
                var percentage = Math.Round((decimal)obtained / TermResultTotalMarks * 100m, 2);
                var (grade, point, status) = obtained switch
                {
                    >= 90 => ("A", 4.0m, "Pass"),
                    >= 80 => ("B", 3.0m, "Pass"),
                    >= 70 => ("C", 2.0m, "Pass"),
                    >= 60 => ("D", 1.0m, "Pass"),
                    _ => ("F", 0.0m, "Fail"),
                };

                rows.Add(new ResultRow
                {
                    EnrollmentId = enrollment.EnrollmentId,
                    AcademicGradeSubjectId = agsId,
                    TermId = termId,
                    TotalMarks = TermResultTotalMarks,
                    ObtainedMarks = obtained,
                    Percentage = percentage,
                    Grade = grade,
                    GradePoint = point,
                    ResultStatus = status,
                });
            }
        }

        var inserted = 0;
        const int batchSize = 2000;

        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var batch = rows.Skip(offset).Take(batchSize).ToList();

            const string sql = @"
                INSERT INTO studentsubjectresult
                    (tenantid, schoolid, campusid, studentenrollmentid, academicgradesubjectid,
                     termid, totalmarks, obtainedmarks, percentage, grade, gradepoint, resultstatus,
                     createdby, modifiedby, createdon, modifiedon)
                SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                       unnest(@EnrollmentIds), unnest(@AgsIds),
                       unnest(@TermIds), unnest(@Totals), unnest(@Obtained), unnest(@Percentages),
                       unnest(@Grades), unnest(@GradePoints), unnest(@Statuses),
                       1, 1, @now, @now";

            inserted += await conn.ExecuteAsync(sql, new
            {
                TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
                SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
                CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
                EnrollmentIds = batch.Select(r => r.EnrollmentId).ToArray(),
                AgsIds = batch.Select(r => r.AcademicGradeSubjectId).ToArray(),
                TermIds = batch.Select(r => r.TermId).ToArray(),
                Totals = batch.Select(r => r.TotalMarks).ToArray(),
                Obtained = batch.Select(r => r.ObtainedMarks).ToArray(),
                Percentages = batch.Select(r => r.Percentage).ToArray(),
                Grades = batch.Select(r => r.Grade).ToArray(),
                GradePoints = batch.Select(r => r.GradePoint).ToArray(),
                Statuses = batch.Select(r => r.ResultStatus).ToArray(),
                now
            });

            if (verbose) LogProgress($"  Exams term results (campus {campusId})", inserted, rows.Count);
        }

        return inserted;
    }

    private sealed class ResultRow
    {
        public long EnrollmentId { get; set; }
        public long AcademicGradeSubjectId { get; set; }
        public long TermId { get; set; }
        public decimal TotalMarks { get; set; }
        public int ObtainedMarks { get; set; }
        public decimal Percentage { get; set; }
        public string Grade { get; set; } = string.Empty;
        public decimal GradePoint { get; set; }
        public string ResultStatus { get; set; } = "Pass";
    }
}
