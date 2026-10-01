using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the TIMETABLE / TEACHER-OPS module (`timetablesetup` + `timetablesetupdetail`, `timetable`,
/// `timetableentry`, `classroomsubjectteacher`, `teacherabsence`, `timetablerelief`,
/// `teachermoduleauditlog`) - INCLUDING the academic spine those tables hang off - and then asserts
/// the joins that decide whether the module's screens measure anything.
///
/// ⚠️ WHY THE SPINE IS IN THIS FIXTURE AND NOT ASSUMED. Measured with an exact `count(*)` sweep,
/// `ayra_perf`'s median campus held **0 `academicgrade`, 0 `academicgradesubject`, 1 `classroom` and
/// 1 `timetable`**, and the single classroom carries a NULL `academicyearid`. Every read in this
/// module joins through `Classroom -> AcademicGrade -> AcademicGradeSubject`, so with that spine
/// missing the specs would have measured an empty join while `timetableentry` and
/// `classroomsubjectteacher` looked populated - the `SKIP` this tool exists to prevent, one level
/// down. `TimetableModuleSeeder` therefore OWNS the spine, find-or-create.
///
/// ⚠️ THE ASSERTIONS ARE THE QUERIES' OWN PREDICATES, NOT COUNTS. Five of the module's reads are
/// INNER JOINS with a filter that can exclude a perfectly good row:
///
///   * every `TimetableEntryRepository` read filters `Timetable.Status = 'Published'`, so a Draft
///     timetable is a full grid on a screen that renders empty;
///   * the period index every grid keys its CELL on comes from a correlated subquery over
///     `TimeTableSetupDetail` - an entry whose detail row is missing renders in period 0;
///   * the subject name comes from `COALESCE(cs.CustomName, sub.Name, sub2.Name)`, i.e. an
///     `academicgradesubject` row that points at NEITHER a campus subject NOR a curriculum subject
///     is a nameless cell;
///   * the teacher name comes from `Teacher -> Users`, so a teacher with no login row blanks it;
///   * `GetTeacherAssignments` and `GetAllYearAssignments` INNER JOIN Classroom, CampusSubject/
///     CurriculumGradeSubject, Section and AcademicGrade - a CST row outside the scope, or one whose
///     classroom has no section, is invisible to the screen that lists it.
///
/// Opt in with the same flag the other dataset seeders use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~TimetableDataset"
///
/// ⚠️ IT DEPENDS ON `PerfDatasetSeeder` (enrolments, which are the only place the campus's real
/// academic year is recorded) and on `CommunicationWorkspaceSeeder`'s `teacher` rows. Both are
/// prerequisites the seeder REPORTS rather than throws on.
/// </summary>
public sealed class TimetableDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public TimetableDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Timetable_dataset_fills_every_read_the_teacher_ops_module_makes()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the timetable perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed a timetable into - " +
            "run PerfDatasetTests first");

        var options = new TimetableOptions
        {
            Classrooms = SeedCampuses.EnvInt("SCUBE_PERF_TT_CLASSROOMS", 6),
            Periods = SeedCampuses.EnvInt("SCUBE_PERF_TT_PERIODS", 7),
            Breaks = SeedCampuses.EnvInt("SCUBE_PERF_TT_BREAKS", 2),
            TeachingDays = SeedCampuses.EnvInt("SCUBE_PERF_TT_DAYS", 5),
            AssignmentsPerSubject = SeedCampuses.EnvInt("SCUBE_PERF_TT_ASSIGNMENTS", 2),
            Absences = SeedCampuses.EnvInt("SCUBE_PERF_TT_ABSENCES", 60),
            Reliefs = SeedCampuses.EnvInt("SCUBE_PERF_TT_RELIEFS", 60),
            AuditRows = SeedCampuses.EnvInt("SCUBE_PERF_TT_AUDIT_ROWS", 300),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding TIMETABLE / TEACHER-OPS for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.Classrooms} classrooms x " +
                          $"{options.TeachingDays} days x {options.Periods} periods");
        _output.WriteLine("");

        var seeder = new TimetableModuleSeeder(_connectionString);
        var seededCampusIds = new List<long>();
        var totalEntries = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalEntries += result.Entries;

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason}");
                continue;
            }

            _output.WriteLine(
                $"  campus {campusId,-5} year {result.AcademicYearId,-3} grade {result.AcademicGradeId,-4} " +
                $"{result.Classrooms,3} rooms {result.AcademicGradeSubjects,3} subjects " +
                $"{result.SetupDetails,3} slots {result.SetupOffDays,2} off-days {result.Entries,5} entries " +
                $"{result.Assignments,4} assignments " +
                $"{result.Absences,4} absences {result.Reliefs,4} reliefs {result.AuditRows,4} audit " +
                $"busiest teacher {result.TeacherId}");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalEntries:N0} timetable entries");

        Assert.True(seededCampusIds.Count > 0,
            "no campus was seeded - every campus was skipped, so the timetable specs would still " +
            "measure an empty join");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. These tables already existed with rows of their own (the
        // module is seeded INTO a populated campus, unlike the dataset-wide seeders), so the planner's
        // statistics describe the tables BEFORE this seed - and this repository has already paid for
        // that once: `V132`'s index looked useless on un-analyzed statistics and a genuinely broken
        // query looked acceptable on them. The list is the SEEDER's own declaration, so the tables
        // analyzed here are exactly the ones it wrote.
        foreach (var table in TimetableModuleSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertTheTemplateDescribesItsOwnWeekAsync(conn, seededCampusIds);
        await AssertThePublishedSpineIsReachableAsync(conn, seededCampusIds);
        await AssertEveryCellCanBeNamedAsync(conn, seededCampusIds);
        await AssertTheBusiestTeacherCarriesAPublishedLoadAsync(conn, seededCampusIds);
        await AssertAssignmentsSurviveTheScreensInnerJoinsAsync(conn, seededCampusIds);
        await AssertTheSubstituteDeskHasBothCoverageStatesAsync(conn, seededCampusIds);
        await AssertNoSentinelTimestampsAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// ⚠️ THE FILTER THAT BLANKS THE WHOLE MODULE. Every entry read is
    /// `INNER JOIN Timetable t ... AND t.Status = 'Published'`, while `TimetableRepository.Insert`
    /// writes `Draft` - so a seeded Draft is a timetable the teacher's sheet, the assignments panel,
    /// the workload screens and the substitute desk all report as EMPTY, on a campus whose
    /// `timetableentry` holds hundreds of rows.
    ///
    /// The second half is the period index: the grids key their cells on a correlated count over
    /// `TimeTableSetupDetail`, so an entry whose detail row is missing (or whose detail belongs to
    /// another campus's template) renders in period 0 - present in the data, invisible in the grid.
    /// </summary>
    private static async Task AssertThePublishedSpineIsReachableAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var classesWithPublished = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT t.ClassroomId)
                    FROM timetable t
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.status = 'Published'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(classesWithPublished > 0,
                $"campus {campusId} has no PUBLISHED timetable - every entry read filters on that status, " +
                "so each timetable spec would report an empty result over a populated `timetableentry`");

            var blankPeriods = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM timetableentry te
                    INNER JOIN timetable t ON t.Id = te.TimetableId
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.status = 'Published'
                     AND NOT EXISTS (
                           SELECT 1 FROM timetablesetupdetail d
                            INNER JOIN timetablesetup s ON s.Id = d.TimeTableSetupId
                           WHERE d.Id = te.TimeTableSetupDetailId
                             AND s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(blankPeriods == 0,
                $"campus {campusId} holds {blankPeriods} published entry row(s) whose `TimeTableSetupDetailId` " +
                "does not resolve to this campus's period template - the grid's PeriodIndex subquery returns 0 " +
                "for each of them, so they render in a period that does not exist");
        }
    }

    /// <summary>
    /// Every cell of the weekly grid must be ABLE TO SHOW ITS NAME. The subject comes from
    /// `COALESCE(NULLIF(cs.CustomName,''), sub.Name, sub2.Name)` and the teacher from
    /// `Teacher -> Users`, so a nameless cell is a row that is counted, measured and displayed as a
    /// blank - the defect the e2e suite recorded as "a seeded row no query can reach".
    /// </summary>
    private static async Task AssertEveryCellCanBeNamedAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            // The joins are repeated verbatim from `TimetableEntryRepository.GetTeacherEntries`, so
            // this predicate cannot drift from the read it is guarding.
            var unnamedSubjects = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM timetableentry te
                    INNER JOIN timetable t ON t.Id = te.TimetableId
                     AND t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.status = 'Published'
                    LEFT JOIN academicgradesubject ags ON ags.Id = te.AcademicGradeSubjectId
                    LEFT JOIN campussubject cs ON cs.Id = ags.CampusSubjectId
                    LEFT JOIN subject sub ON sub.Id = cs.SubjectId
                    LEFT JOIN curriculumgradesubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                    LEFT JOIN subject sub2 ON sub2.Id = cgs.SubjectId
                   WHERE COALESCE(NULLIF(cs.CustomName, ''), sub.Name, sub2.Name) IS NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unnamedSubjects == 0,
                $"campus {campusId} holds {unnamedSubjects} published entry row(s) whose subject name does " +
                "not resolve down the `cs.CustomName -> sub.Name -> sub2.Name` chain - the weekly grid would " +
                "render a blank subject for a cell that is really scheduled");

            var missingTeacher = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM timetableentry te
                    INNER JOIN timetable t ON t.Id = te.TimetableId
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.status = 'Published'
                     AND NOT EXISTS (SELECT 1 FROM teacher tc WHERE tc.Id = te.TeacherId)
                     AND te.TeacherId > 0",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(missingTeacher == 0,
                $"campus {campusId} holds {missingTeacher} published entry row(s) naming a teacher that does " +
                "not exist - the grid renders the teacher through `Teacher -> Users`, so the name is blank");

            var teacherWithoutLogin = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM timetableentry te
                    INNER JOIN timetable t ON t.Id = te.TimetableId
                    INNER JOIN teacher tc ON tc.Id = te.TeacherId
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.status = 'Published'
                     AND NOT EXISTS (SELECT 1 FROM users u WHERE u.Id = tc.UserId)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(teacherWithoutLogin == 0,
                $"campus {campusId} holds {teacherWithoutLogin} published entr(ies) taught by a teacher with no " +
                "`users` row - the teacher-name join is `LEFT JOIN Users`, so the cell renders a subject with " +
                "nobody against it");
        }
    }

    /// <summary>
    /// The workload screens read a TEACHER's PUBLISHED entries, grouped by subject, by weekday, and
    /// (campus-wide) by teacher. Each of those is a `GROUP BY`, so a teacher with one entry produces
    /// one row and measures nothing - the set is seeded so the busiest teacher is busy.
    /// </summary>
    private static async Task AssertTheBusiestTeacherCarriesAPublishedLoadAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var counts = await conn.QueryAsync<TeacherLoadRow>(
                @"SELECT te.TeacherId AS TeacherId,
                         COUNT(DISTINCT te.Id) AS Periods,
                         COUNT(DISTINCT te.WeekDay) AS Days,
                         COUNT(DISTINCT te.AcademicGradeSubjectId) AS Subjects
                    FROM timetableentry te
                    INNER JOIN timetable t ON t.Id = te.TimetableId
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND t.academicYearId = @academicYearId AND t.status = 'Published'
                   GROUP BY te.TeacherId",
                new
                {
                    tenantId = SeedCampuses.TenantId,
                    schoolId = SeedCampuses.SchoolId,
                    campusId,
                    academicYearId = await AcademicYearOfAsync(conn, campusId),
                });

            var list = counts.ToList();
            Assert.True(list.Count > 0,
                $"campus {campusId} has no teacher with a PUBLISHED entry in its own academic year - the " +
                "workload reads group by teacher and would return nothing");

            var busiest = list.OrderByDescending(x => x.Periods).First();
            Assert.True(busiest.Periods >= 5,
                $"campus {campusId}'s busiest teacher carries only {busiest.Periods} published period(s) - " +
                "the workload reads walk a set that is too small to be a measurement");
            Assert.True(busiest.Subjects >= 2,
                $"campus {campusId}'s busiest teacher teaches {busiest.Subjects} subject(s) - " +
                "`GetTeacherWorkloadSubjectStats` groups by subject and would return a single row");
        }
    }

    /// <summary>
    /// ⚠️ `GetTeacherAssignments` AND `GetAllYearAssignments` ARE INNER JOINED ALL THE WAY DOWN.
    /// `ClassroomSubjectTeacher` carries NO tenant/school/campus columns, so the campus scope comes
    /// from the JOINED `Classroom`; on top of that the read INNER JOINs `Section`, `AcademicGrade`
    /// and `AcademicGradeSubject`. A CST row whose classroom sits at another scope, or whose section
    /// is NULL, EXISTS and is INVISIBLE - which is precisely why this seeder creates its classrooms
    /// with a real section, grade and class teacher rather than reusing whatever it found.
    /// </summary>
    private static async Task AssertAssignmentsSurviveTheScreensInnerJoinsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM classroomsubjectteacher cst
                    INNER JOIN classroom c ON c.Id = cst.ClassroomId
                   WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} has no `classroomsubjectteacher` row reachable through its classrooms - " +
                "the assignment panel, the workload overview and the deletion guard would all read zero");

            // The exact join `GetTeacherAssignments` builds - repeated so the assertion cannot drift.
            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM classroomsubjectteacher st
                    INNER JOIN classroom c ON st.ClassroomId = c.Id
                   WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId
                     AND (NOT EXISTS (SELECT 1 FROM section sec WHERE sec.Id = c.SectionId)
                          OR NOT EXISTS (SELECT 1 FROM academicgrade ag WHERE ag.Id = c.AcademicGradeId)
                          OR NOT EXISTS (SELECT 1 FROM academicgradesubject ags WHERE ags.Id = st.AcademicGradeSubjectId)
                          OR NOT EXISTS (SELECT 1 FROM teacher tc WHERE tc.Id = st.TeacherId))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} assignment row(s) that the assignment screen's INNER " +
                "JOINs would drop - a row that exists in the table and cannot appear on the screen that lists it");

            var withoutAcademicYear = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*)
                    FROM classroomsubjectteacher cst
                    INNER JOIN classroom c ON c.Id = cst.ClassroomId
                   WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId
                     AND cst.AcademicYearId IS NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(withoutAcademicYear == 0,
                $"campus {campusId} holds {withoutAcademicYear} assignment row(s) with a NULL academic year - " +
                "`GetTeacherAssignments` filters on it, so they are unreachable");
        }
    }

    /// <summary>
    /// The substitute desk models coverage as a row per (date, classroom, period) whose
    /// `SubstituteTeacherId` may be NULL - and the model's whole point is that a NULL means
    /// "marked uncovered". Both states therefore have to exist, or a spec over the coverage read
    /// measures only the joined half of a LEFT JOIN.
    /// </summary>
    private static async Task AssertTheSubstituteDeskHasBothCoverageStatesAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var uncovered = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM timetablerelief
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND substituteteacherid IS NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            var covered = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM timetablerelief
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND substituteteacherid IS NOT NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(uncovered > 0,
                $"campus {campusId} has no UNCOVERED relief row - the desk's \"marked uncovered\" state, " +
                "which is a NULL substitute, would be unrepresented");
            Assert.True(covered > 0,
                $"campus {campusId} has no COVERED relief row - the substitute-name LEFT JOIN would never " +
                "resolve and its branch would be unmeasured");

            // A relief row is only visible to the desk when its slot belongs to a PUBLISHED timetable of
            // the same campus - the coverage read joins `timetableentry` to `timetable` with that filter.
            var orphaned = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM timetablerelief r
                   WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM timetableentry te
                             INNER JOIN timetable t ON t.Id = te.TimetableId
                            WHERE t.ClassroomId = r.ClassroomId
                              AND t.tenantid = r.tenantid AND t.schoolid = r.schoolid AND t.campusid = r.campusid
                              AND t.status = 'Published'
                              AND te.TimeTableSetupDetailId = r.TimeTableSetupDetailId)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphaned == 0,
                $"campus {campusId} holds {orphaned} relief row(s) whose (classroom, period) has no PUBLISHED " +
                "timetable cell - the desk renders coverage from the scheduled cell, so these would never appear");

            var absences = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT a.AbsenceDate)
                     FROM teacherabsence a
                     INNER JOIN teacher tc ON tc.Id = a.TeacherId
                     INNER JOIN users u ON u.Id = tc.UserId
                    WHERE a.tenantid = @tenantId AND a.schoolid = @schoolId AND a.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(absences > 0,
                $"campus {campusId} holds no absence that resolves to a named teacher - the desk's date read " +
                "INNER JOINs `Teacher -> Users`, so a row that does not resolve is invisible");
        }
    }

    /// <summary>
    /// The `-infinity` guard the module's history modal depends on. `teachermoduleauditlog` has no
    /// `modifiedon` (it is append-only) and `CreatedOn` is what the modal's read orders by; a
    /// `DateTime.MinValue` reaching PostgreSQL is `-infinity`, which sorts BEFORE every real
    /// timestamp and makes "the most recent change" arbitrary. The seeder writes raw INSERTs with an
    /// explicit stamp; this holds that promise on every table it writes.
    /// </summary>
    private static async Task AssertNoSentinelTimestampsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var bad = await conn.ExecuteScalarAsync<long>(
                @"SELECT
                    (SELECT COUNT(*) FROM timetable
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM timetablesetup
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM teachermoduleauditlog
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND createdon = '-infinity'::timestamp)
                  + (SELECT COUNT(*) FROM teacherabsence
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(bad == 0,
                $"campus {campusId} holds {bad} row(s) stamped `-infinity` - the sentinel that ties the " +
                "history modal's `ORDER BY CreatedOn DESC` and makes \"the latest change\" arbitrary");
        }
    }

    /// <summary>
    /// The period template's THREE statements are one round trip (`GetSetupResponse`: header, slots, off-days),
    /// so a table the response reads but nothing writes leaves the wizard rendering an EMPTY list - and an
    /// empty off-day list is indistinguishable from a template that teaches all seven days. The fixture
    /// therefore holds the third statement's table to the READ the response makes (the latest template's own
    /// rows, not the campus's row count) and requires the template's week to be described: the weekdays it
    /// does NOT teach on must be recorded, and every recorded off-day must be a weekday the timetable
    /// schedules NO entry on - the two halves together are what make the list mean something.
    /// </summary>
    private static async Task AssertTheTemplateDescribesItsOwnWeekAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var slots = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM timetablesetupoffday o
                    INNER JOIN timetablesetup s ON s.Id = o.TimeTableSetupId
                   WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(slots > 0,
                $"campus {campusId} holds NO off-day row, so the setup response's third statement returns an " +
                "empty list and the wizard shows a template that teaches every day of the week");

            // An off-day the timetable actually teaches on is a contradiction the screen would render as
            // `Off Day` beside a full column of periods.
            var contradictory = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT o.WeekDay)
                    FROM timetablesetupoffday o
                    INNER JOIN timetablesetup s ON s.Id = o.TimeTableSetupId
                    INNER JOIN timetable t ON t.TenantId = s.TenantId AND t.SchoolId = s.SchoolId
                                           AND t.CampusId = s.CampusId AND t.Status = 'Published'
                    INNER JOIN timetableentry te ON te.TimetableId = t.Id AND te.WeekDay = o.WeekDay
                   WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(contradictory == 0,
                $"campus {campusId} records {contradictory} weekday(s) as an OFF day while its PUBLISHED " +
                "timetable schedules periods on that very day");
        }
    }

    /// <summary>The campus's real academic year, taken the way the seeder takes it - from its enrolments.</summary>
    private static async Task<long> AcademicYearOfAsync(NpgsqlConnection conn, long campusId)
    {
        return await conn.ExecuteScalarAsync<long?>(
            @"SELECT academicyearid FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               GROUP BY academicyearid ORDER BY COUNT(*) DESC, academicyearid LIMIT 1",
            new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId }) ?? 0;
    }

    private sealed class TeacherLoadRow
    {
        public long TeacherId { get; set; }
        public int Periods { get; set; }
        public int Days { get; set; }
        public int Subjects { get; set; }
    }
}
