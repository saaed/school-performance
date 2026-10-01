using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="TimetableModuleSeeder"/>.</summary>
public sealed class TimetableOptions
{
    /// <summary>Classrooms built for the campus. Each one gets its own Published timetable.</summary>
    public int Classrooms { get; set; } = 6;

    /// <summary>Teaching periods in the campus period template.</summary>
    public int Periods { get; set; } = 7;

    /// <summary>Breaks appended AFTER the teaching periods (that is the order every reader numbers in).</summary>
    public int Breaks { get; set; } = 2;

    /// <summary>Teaching days per week. Sunday..Thursday, which is the app's own `CalendarService` order.</summary>
    public int TeachingDays { get; set; } = 5;

    /// <summary>
    /// Subject assignments written per (classroom, subject). Two is the point: one is enough for a grid to
    /// render, but the workload share reads (`GetTeacherWorkloadSourceRows`) walk the rows that SHARE a
    /// (classroom, subject) with the teacher, so a single assignment makes that read return one row and
    /// measures nothing.
    /// </summary>
    public int AssignmentsPerSubject { get; set; } = 2;

    public int Absences { get; set; } = 60;
    public int Reliefs { get; set; } = 60;

    /// <summary>Audit rows for the teacher History modal - the repository's own read is capped at 300.</summary>
    public int AuditRows { get; set; } = 300;

    public bool Force { get; set; }
}

/// <summary>What one campus's seed produced, plus the ids the fixture and the specs point at.</summary>
public sealed class TimetableSeedResult
{
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public long AcademicYearId { get; set; }
    public long AcademicGradeId { get; set; }
    public long SectionId { get; set; }
    public long ClassroomId { get; set; }
    public long TimeTableSetupId { get; set; }
    public long TimetableId { get; set; }
    public long TeacherId { get; set; }
    public string WeekDay { get; set; } = string.Empty;
    public int AcademicGradeSubjects { get; set; }
    public int Classrooms { get; set; }
    public int SetupDetails { get; set; }
    public int SetupOffDays { get; set; }
    public int Timetables { get; set; }
    public int Entries { get; set; }
    public int Assignments { get; set; }
    public int Absences { get; set; }
    public int Reliefs { get; set; }
    public int AuditRows { get; set; }
}

/// <summary>
/// Seeds the TIMETABLE / TEACHER-OPS module - `timetablesetup` + `timetablesetupdetail`,
/// `timetable`, `timetableentry`, `classroomsubjectteacher`, `teacherabsence`,
/// `timetablerelief` and `teachermoduleauditlog` - for one campus, INCLUDING the academic spine
/// they hang off.
///
/// ⚠️ THE SPINE IS PART OF THE MODULE, AND THIS IS THE PASS THAT PROVES IT.
/// `academicgrade`, `academicgradesubject` and `classroom` are EXPECTED to exist (MasterDataSeeder and
/// the exams/curriculum seeders write some of them), but on `ayra_perf`'s measured campus they did not:
/// measured with an exact `count(*)` sweep, campus 15 held **0 `academicgrade`, 0
/// `academicgradesubject`, 1 `classroom` and 1 `timetable`** - and the single classroom carries a NULL
/// `academicyearid` and the single timetable a NULL-ish `TimeTableSetupId`, i.e. both are debris from
/// an earlier seed rather than a usable spine. So every read in this module would have measured an
/// empty join while both of its tables looked populated, which is the `SKIP` this tool exists to
/// prevent, one level down.
/// <para>
/// The spine this seeder builds is deliberately FIND-OR-CREATE and NEVER cleared, for two reasons:
/// </para>
/// <list type="bullet">
///   <item>a scoped `DELETE FROM classroom` would remove the campus's only other classroom, and that
///   row is what `db-report`'s `ResolveScopeAsync` reads for `ScopeVars.AcademicGradeId`
///   (`SELECT academicgradeid FROM classroom WHERE scope ORDER BY id LIMIT 1`) - deleting it would
///   silently re-point every `fee-*` spec at a different grade. Adding rows leaves that resolution
///   untouched because the debris row keeps the lowest id.</item>
///   <item>the timetable specs do not need the resolver's generic picks at all: they are handed
///   dedicated ids (`ScopeVars.TimetableClassroomId` and friends) resolved from the rows THIS seeder
///   stamped, so nothing depends on which classroom happens to sort first.</item>
/// </list>
///
/// ⚠️ THE ACADEMIC YEAR COMES FROM THE ENROLMENTS, NOT FROM THE CLASSROOM - the same measurement
/// `CommunicationWorkspaceSeeder` recorded. The campus's classrooms inherit `academicgrade` rows that
/// belong to ANOTHER campus's year (campus 15 has no `academicgrade` of its own), while
/// `studentenrollment` carries the campus's real year (14 on the measured campus). The seeder takes the
/// enrolment year, and creates the campus's OWN `academicgrade` for it.
///
/// ⚠️ EVERY `TimetableEntryRepository` READ FILTERS `Timetable.Status = 'Published'`, AND A DRAFT IS
/// THE SCREEN'S OWN DEFAULT (`TimetableRepository.Insert` writes `Status = "Draft"`). A seeded
/// `timetable` row in any other status leaves the teacher's sheet, the assignments panel, the workload
/// screens and the substitute desk all empty while `timetableentry` holds hundreds of rows - so the
/// status is written explicitly and the fixture asserts the Published half.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds a period template is skipped unless Force is set.
/// </summary>
public sealed class TimetableModuleSeeder : BaseSeeder
{
    /// <summary>
    /// The tables this seeder fills, handed to `ANALYZE`. Kept here rather than in a per-seeder list so
    /// the runner cannot analyze a different set from the one that was written.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "academicgrade", "academicgradesubject", "classroom", "timetablesetup",
        "timetablesetupdetail", "timetablesetupoffday", "timetable", "timetableentry",
        "classroomsubjectteacher", "teacherabsence", "timetablerelief", "teachermoduleauditlog",
    };

    /// <summary>The week the app's own `CalendarService` numbers from - Sunday is day 0.</summary>
    private static readonly string[] WeekDays = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    private const long SystemUser = 1;

    public TimetableModuleSeeder(string connectionString) : base(connectionString)
    {
    }

    public async Task<TimetableSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, TimetableOptions options, bool verbose = true)
    {
        using var conn = await OpenConnectionAsync();
        return await SeedCoreAsync(conn, tenantId, schoolId, campusId, options, verbose);
    }

    private async Task<TimetableSeedResult> SeedCoreAsync(NpgsqlConnection conn, long tenantId, long schoolId,
        long campusId, TimetableOptions options, bool verbose)
    {
        var result = new TimetableSeedResult();

        var existingSetups = await conn.QueryFirstOrDefaultAsync<int>(
            @"SELECT COUNT(*) FROM timetablesetup
               WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
            new { tenantId, schoolId, campusId });

        if (existingSetups > 0 && !options.Force)
        {
            var counts = await ReadCountsAsync(conn, tenantId, schoolId, campusId);
            counts.Skipped = true;
            counts.SkipReason = $"campus already holds {existingSetups} timetable setup header(s) - pass Force to re-seed";
            if (verbose)
            {
                Console.WriteLine(
                    $"  TimetableModule: campus {campusId} already holds {existingSetups} period template(s) - skipped");
            }

            return counts;
        }

        if (existingSetups > 0)
        {
            // Children first. `timetableentry` and `classroomsubjectteacher` carry NO scope columns, so
            // the scoped DELETE would be a 42703 and the whole seed would abort - clear them through
            // their parent instead, before the parent goes.
            await ClearTableAsync(conn, "timetablerelief", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "teacherabsence", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "teachermoduleauditlog", tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "timetableentry", "TimetableId", "timetable", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "timetable", tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "classroomsubjectteacher", "ClassroomId", "classroom", tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "timetablesetupdetail", "TimeTableSetupId", "timetablesetup", tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "timetablesetupoffday", "TimeTableSetupId", "timetablesetup", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "timetablesetup", tenantId, schoolId, campusId);
            // ⚠️ `classroom`, `academicgrade` and `academicgradesubject` are deliberately NOT cleared -
            // see the class comment (the resolver reads the campus's first classroom, and these are
            // shared with other modules).
        }

        // ------------------------------------------------------------------
        // 1. The campus's REAL academic year, taken from its enrolments.
        // ------------------------------------------------------------------
        var academicYearId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT academicyearid FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               GROUP BY academicyearid ORDER BY COUNT(*) DESC, academicyearid LIMIT 1",
            new { tenantId, schoolId, campusId }) ?? 0;

        if (academicYearId == 0)
        {
            result.Skipped = true;
            result.SkipReason = "campus has no enrolment, so its real academic year is unknown (a timetable cannot be placed)";
            return result;
        }

        result.AcademicYearId = academicYearId;

        // ------------------------------------------------------------------
        // 2. Prerequisites owned elsewhere.
        // ------------------------------------------------------------------
        var teachers = (await conn.QueryAsync<long>(
            @"SELECT Id FROM teacher
               WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId ORDER BY Id",
            new { tenantId, schoolId, campusId })).ToList();

        if (teachers.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason = "campus has no teacher row - the communication-workspace seeder owns that prerequisite";
            return result;
        }

        var campusSubjects = (await conn.QueryAsync<long>(
            @"SELECT Id FROM campussubject
               WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId AND IsActive = TRUE
               ORDER BY Id",
            new { tenantId, schoolId, campusId })).ToList();

        if (campusSubjects.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason = "campus offers no subject, so an AcademicGradeSubject cannot be named";
            return result;
        }

        var curriculumGradeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT cg.Id FROM curriculumgrade cg
                INNER JOIN curriculumversion cv ON cv.Id = cg.CurriculumVersionId
               WHERE cv.TenantId = @tenantId AND cv.SchoolId = @schoolId
               ORDER BY cg.Id LIMIT 1",
            new { tenantId, schoolId }) ?? 0;

        if (curriculumGradeId == 0)
        {
            result.Skipped = true;
            result.SkipReason = "school has no curriculum grade, so an AcademicGrade cannot be created";
            return result;
        }

        var sectionIds = await ResolveOrCreateSectionsAsync(conn, tenantId, schoolId, campusId, options.Classrooms);
        var roomIds = await ResolveOrCreateRoomsAsync(conn, tenantId, schoolId, campusId, options.Classrooms);

        // ------------------------------------------------------------------
        // 3. The spine: one AcademicGrade for THIS campus and year, one
        //    AcademicGradeSubject per subject it offers.
        // ------------------------------------------------------------------
        var now = DateTime.UtcNow;

        var academicGradeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT Id FROM academicgrade
               WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                 AND AcademicYearId = @year AND CurriculumGradeId = @curriculumGradeId
               ORDER BY Id LIMIT 1",
            new { tenantId, schoolId, campusId, year = academicYearId, curriculumGradeId });

        if (academicGradeId is null or 0)
        {
            academicGradeId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO academicgrade
                      (academicyearid, curriculumgradeid, capacity, tenantid, schoolid, campusid,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@year, @curriculumGradeId, 40, @tenantId, @schoolId, @campusId,
                          @user, @user, @now, @now)
                  RETURNING Id",
                new { year = academicYearId, curriculumGradeId, tenantId, schoolId, campusId, user = SystemUser, now });
        }

        result.AcademicGradeId = academicGradeId!.Value;

        // One subject row per OFFERED subject, keyed on `campussubject` so the display name resolves down
        // the `cs.CustomName -> sub.Name -> sub2.Name` chain every reader uses.
        foreach (var campusSubjectId in campusSubjects)
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM academicgradesubject
                   WHERE AcademicGradeId = @academicGradeId AND CampusSubjectId = @campusSubjectId",
                new { academicGradeId, campusSubjectId });

            if (exists > 0) continue;

            await conn.ExecuteAsync(
                @"INSERT INTO academicgradesubject
                      (academicgradeid, campussubjectid, weeklyperiods, ismandatory, tenantid, schoolid,
                       campusid, createdby, modifiedby, createdon, modifiedon, isactive)
                  VALUES (@academicGradeId, @campusSubjectId, 4, TRUE, @tenantId, @schoolId, @campusId,
                          @user, @user, @now, @now, TRUE)",
                new { academicGradeId, campusSubjectId, tenantId, schoolId, campusId, user = SystemUser, now });

            result.AcademicGradeSubjects++;
        }

        var subjectIds = (await conn.QueryAsync<long>(
            @"SELECT Id FROM academicgradesubject
               WHERE AcademicGradeId = @academicGradeId AND TenantId = @tenantId AND SchoolId = @schoolId
                 AND CampusId = @campusId
               ORDER BY Id",
            new { academicGradeId, tenantId, schoolId, campusId })).ToList();

        // ------------------------------------------------------------------
        // 4. The period template (teaching periods first, then breaks - that
        //    ordering is what every reader derives a period index from).
        // ------------------------------------------------------------------
        var setupId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO timetablesetup
                  (name, totalperiods, tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
              VALUES (@name, @total, @tenantId, @schoolId, @campusId, @user, @user, @now, @now)
              RETURNING Id",
            new { name = "PERF Period Template", total = options.Periods, tenantId, schoolId, campusId, user = SystemUser, now });

        result.TimeTableSetupId = setupId;

        var teachingDetailIds = new List<long>();
        var allDetailIds = new List<long>();

        for (var i = 0; i < options.Periods + options.Breaks; i++)
        {
            var isBreak = i >= options.Periods;
            var slot = isBreak ? i - options.Periods + 1 : i + 1;
            var startHour = 8 + i; // 08:00 -> breaks are interleaved by position, which is what the order means

            var detailId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO timetablesetupdetail
                      (timetablesetupid, periodnumber, name, starttime, endtime, isbreak,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@setupId, @periodNumber, @name, @start, @end, @isBreak, @user, @user, @now, @now)
                  RETURNING Id",
                new
                {
                    setupId,
                    periodNumber = i + 1,
                    name = isBreak ? $"Break {slot}" : $"Period {slot}",
                    start = TimeSpan.FromHours(startHour),
                    end = TimeSpan.FromHours(startHour) + TimeSpan.FromMinutes(45),
                    isBreak,
                    user = SystemUser,
                    now,
                });

            allDetailIds.Add(detailId);
            if (!isBreak) teachingDetailIds.Add(detailId);
            result.SetupDetails++;
        }

        // The off-days are the weekdays the template does NOT teach on, written the way the wizard's
        // own save writes them (`INSERT INTO timetablesetupoffday (TimeTableSetupId, WeekDay)`), so the
        // setup response's THIRD statement - the one `TimeTableSetupRepository.GetSetupResponse` reads
        // in the same round trip as the header and the slots - returns real rows for a reader that had
        // nothing to read. A campus with no off-day row is not "every day is a school day": the reader
        // returns an empty list either way, so an empty table is indistinguishable from a template that
        // teaches all seven days, which is the `SKIP` this seeder exists to prevent.
        foreach (var day in WeekDays.Skip(options.TeachingDays))
        {
            await conn.ExecuteAsync(
                @"INSERT INTO timetablesetupoffday (timetablesetupid, weekday)
                  VALUES (@setupId, @day)",
                new { setupId, day });
            result.SetupOffDays++;
        }

        // ------------------------------------------------------------------
        // 5. Classrooms, one PUBLISHED timetable each, and their entries.
        // ------------------------------------------------------------------
        var dayNames = WeekDays.Take(options.TeachingDays).ToList();

        var classroomIds = new List<long>();
        var timetableIds = new List<long>();

        for (var ci = 0; ci < options.Classrooms; ci++)
        {
            var sectionId = sectionIds[ci % sectionIds.Count];
            var roomId = roomIds[ci % roomIds.Count];
            var classTeacherId = teachers[ci % teachers.Count];
            var classroomName = $"PERF-TT-{sectionId}";

            var classroomId = await conn.ExecuteScalarAsync<long?>(
                @"SELECT Id FROM classroom
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                     AND ClassroomName = @classroomName
                   ORDER BY Id LIMIT 1",
                new { tenantId, schoolId, campusId, classroomName });

            if (classroomId is null or 0)
            {
                classroomId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO classroom
                          (sectionid, academicgradeid, classteacherid, classroomname, capacity, tenantid,
                           schoolid, campusid, createdby, modifiedby, createdon, modifiedon,
                           academicyearid, roomid, status)
                      VALUES (@sectionId, @academicGradeId, @classTeacherId, @classroomName, 40, @tenantId,
                              @schoolId, @campusId, @user, @user, @now, @now, @year, @roomId, @status)
                      RETURNING Id",
                    new
                    {
                        sectionId,
                        academicGradeId,
                        classTeacherId,
                        classroomName,
                        tenantId,
                        schoolId,
                        campusId,
                        user = SystemUser,
                        now,
                        year = academicYearId,
                        roomId,
                        // The token the only other classroom row in this database uses.
                        status = "Active",
                    });
            }

            classroomIds.Add(classroomId.Value);

            var timetableId = await conn.ExecuteScalarAsync<long?>(
                @"SELECT Id FROM timetable
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                     AND ClassroomId = @classroomId AND AcademicYearId = @year
                   ORDER BY Id LIMIT 1",
                new { tenantId, schoolId, campusId, classroomId, year = academicYearId });

            if (timetableId is null or 0)
            {
                timetableId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO timetable
                          (academicyearid, classroomid, status, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon, timetablesetupid)
                      VALUES (@year, @classroomId, @status, @tenantId, @schoolId, @campusId,
                              @user, @user, @now, @now, @setupId)
                      RETURNING Id",
                    new
                    {
                        year = academicYearId,
                        classroomId,
                        // ⚠️ Published, not Draft: every entry read filters on it (see the class comment).
                        status = "Published",
                        tenantId,
                        schoolId,
                        campusId,
                        user = SystemUser,
                        now,
                        setupId,
                    });

                result.Timetables++;
            }

            timetableIds.Add(timetableId.Value);

            // The week's grid. Every (day, period) cell gets a subject, a teacher and a room, because a
            // grid with holes is not what any of these screens render.
            for (var di = 0; di < dayNames.Count; di++)
            {
                for (var pi = 0; pi < teachingDetailIds.Count; pi++)
                {
                    var teacherId = teachers[(ci * 11 + di * teachingDetailIds.Count + pi) % teachers.Count];
                    var subjectId = subjectIds[(di * teachingDetailIds.Count + pi) % subjectIds.Count];
                    var entryRoomId = roomIds[(ci + di) % roomIds.Count];

                    await conn.ExecuteAsync(
                        @"INSERT INTO timetableentry
                              (timetableid, weekday, academicgradesubjectid, teacherid, roomid,
                               createdby, modifiedby, createdon, modifiedon, timetablesetupdetailid)
                          VALUES (@timetableId, @weekDay, @subjectId, @teacherId, @roomId,
                                  @user, @user, @now, @now, @detailId)",
                        new
                        {
                            timetableId,
                            weekDay = dayNames[di],
                            subjectId,
                            teacherId,
                            roomId = entryRoomId,
                            user = SystemUser,
                            now,
                            detailId = teachingDetailIds[pi],
                        });

                    result.Entries++;
                }
            }

            // Subject assignments: one row per subject (plus the extras the workload share reads walk).
            foreach (var subjectId in subjectIds)
            {
                for (var k = 0; k < options.AssignmentsPerSubject; k++)
                {
                    var teacherId = teachers[(ci + subjectIds.IndexOf(subjectId) + k) % teachers.Count];

                    await conn.ExecuteAsync(
                        @"INSERT INTO classroomsubjectteacher
                              (teacherid, classroomid, isprimaryteacher, academicgradesubjectid,
                               academicyearid, createdby, modifiedby, createdon, modifiedon)
                          VALUES (@teacherId, @classroomId, @isPrimary, @subjectId,
                                  @year, @user, @user, @now, @now)",
                        new
                        {
                            teacherId,
                            classroomId,
                            isPrimary = k == 0,
                            subjectId,
                            year = academicYearId,
                            user = SystemUser,
                            now,
                        });

                    result.Assignments++;
                }
            }

            LogProgress("  classrooms", ci + 1, options.Classrooms);
            if (ci + 1 == options.Classrooms) Console.WriteLine();
        }

        result.Classrooms = classroomIds.Count;
        result.SectionId = sectionIds[0];
        result.ClassroomId = classroomIds[0];
        result.TimetableId = timetableIds[0];
        result.WeekDay = dayNames[0];

        // ------------------------------------------------------------------
        // 6. The teacher who carries the MOST published entries. The specs
        //    point at this one: a teacher with one entry measures the index
        //    seek, not the read.
        // ------------------------------------------------------------------
        var busiest = await conn.QueryFirstOrDefaultAsync<TeacherCountRow>(
            @"SELECT te.TeacherId AS TeacherId, COUNT(*) AS Entries
                FROM timetableentry te
                INNER JOIN timetable t ON t.Id = te.TimetableId
               WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                 AND t.AcademicYearId = @year AND t.Status = 'Published'
               GROUP BY te.TeacherId
               ORDER BY COUNT(*) DESC, te.TeacherId LIMIT 1",
            new { tenantId, schoolId, campusId, year = academicYearId });

        result.TeacherId = busiest?.TeacherId ?? teachers[0];

        // ------------------------------------------------------------------
        // 7. Absences, relief, audit - the three desks that read a date or a teacher.
        // ------------------------------------------------------------------
        var today = DateTime.Today;

        for (var i = 0; i < options.Absences; i++)
        {
            var teacherId = teachers[i % teachers.Count];
            var absenceDate = today.AddDays(-(i % 60)).Date;

            await conn.ExecuteAsync(
                @"INSERT INTO teacherabsence
                      (tenantid, schoolid, campusid, academicyearid, teacherid, absencedate, reason,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @year, @teacherId, @date, @reason,
                          @user, @user, @now, @now)
                  ON CONFLICT DO NOTHING",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    year = academicYearId,
                    teacherId,
                    date = absenceDate,
                    reason = i % 3 == 0 ? "Sick leave" : "Personal",
                    user = SystemUser,
                    now,
                });

            result.Absences++;
        }

        for (var i = 0; i < options.Reliefs; i++)
        {
            var classroomId = classroomIds[i % classroomIds.Count];
            var detailId = teachingDetailIds[i % teachingDetailIds.Count];
            var subjectId = subjectIds[i % subjectIds.Count];
            var absentTeacherId = teachers[i % teachers.Count];
            // Every third slot is left UNCOVERED on purpose: the substitute desk's model is that a row
            // with a NULL substitute means "marked uncovered", so both states have to exist.
            var substituteTeacherId = i % 3 == 0 ? (long?)null : teachers[(i + 1) % teachers.Count];

            await conn.ExecuteAsync(
                @"INSERT INTO timetablerelief
                      (tenantid, schoolid, campusid, academicyearid, reliefdate, classroomid,
                       timetablesetupdetailid, academicgradesubjectid, absentteacherid,
                       substituteteacherid, notes, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @year, @date, @classroomId,
                          @detailId, @subjectId, @absentTeacherId,
                          @substituteTeacherId, @notes, @user, @user, @now, @now)
                  ON CONFLICT DO NOTHING",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    year = academicYearId,
                    date = today.AddDays(-(i % 30)).Date,
                    classroomId,
                    detailId,
                    subjectId,
                    absentTeacherId,
                    substituteTeacherId,
                    notes = "PERF substitute coverage",
                    user = SystemUser,
                    now,
                });

            result.Reliefs++;
        }

        string[] entities = { "Timetable", "Assignment", "Absence", "Relief", "Workload" };
        string[] actions = { "Create", "Update", "Delete", "Publish", "Transfer" };
        var busiestTeacherId = result.TeacherId;

        for (var i = 0; i < options.AuditRows; i++)
        {
            // A real history is mostly about a HANDFUL of teachers, not spread evenly - so the busiest
            // teacher owns half the log and the rest is spread. That is what makes the modal's read
            // (ordered, capped at 300) the read a user actually triggers.
            var teacherId = i % 2 == 0 ? busiestTeacherId : teachers[i % teachers.Count];

            await conn.ExecuteAsync(
                @"INSERT INTO teachermoduleauditlog
                      (tenantid, schoolid, campusid, teacherid, academicyearid, entity, action, detail,
                       createdby, createdon)
                  VALUES (@tenantId, @schoolId, @campusId, @teacherId, @year, @entity, @action, @detail,
                          @user, @createdOn)",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    teacherId,
                    year = academicYearId,
                    entity = entities[i % entities.Length],
                    action = actions[i % actions.Length],
                    detail = $"PERF teacher-module audit row {i}",
                    user = SystemUser,
                    createdOn = now.AddMinutes(-i),
                });

            result.AuditRows++;
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  TimetableModule: campus {campusId} year {academicYearId} grade {result.AcademicGradeId} " +
                $"{result.Classrooms} classrooms {result.SetupDetails} slots {result.Entries} entries " +
                $"{result.Assignments} assignments {result.Absences} absences {result.Reliefs} reliefs " +
                $"{result.AuditRows} audit rows (busiest teacher {result.TeacherId})");
        }

        return result;
    }

    /// <summary>
    /// Sections to hang classrooms on. Reused when the campus already has them (the master-data seeder
    /// writes six), created only when the campus has NONE - a classroom's `sectionid` is NOT NULL, so a
    /// campus without a section cannot express a classroom at all.
    /// </summary>
    private async Task<List<long>> ResolveOrCreateSectionsAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int wanted)
    {
        var ids = (await conn.QueryAsync<long>(
            @"SELECT Id FROM section
               WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId ORDER BY Id",
            new { tenantId, schoolId, campusId })).ToList();

        if (ids.Count > 0) return ids;

        var now = DateTime.UtcNow;
        for (var i = 0; i < Math.Max(wanted, 1); i++)
        {
            ids.Add(await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO section (name, description, tenantid, schoolid, campusid,
                                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@name, '', @tenantId, @schoolId, @campusId, @user, @user, @now, @now)
                  RETURNING Id",
                new { name = $"PERF Section {(char)('A' + i)}", tenantId, schoolId, campusId, user = SystemUser, now }));
        }

        return ids;
    }

    /// <summary>The same rule for rooms - reused when present, created only when the campus has none.</summary>
    private async Task<List<long>> ResolveOrCreateRoomsAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int wanted)
    {
        var ids = (await conn.QueryAsync<long>(
            @"SELECT Id FROM room
               WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId ORDER BY Id",
            new { tenantId, schoolId, campusId })).ToList();

        if (ids.Count > 0) return ids;

        var now = DateTime.UtcNow;
        for (var i = 0; i < Math.Max(wanted, 1); i++)
        {
            ids.Add(await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO room (name, description, tenantid, schoolid, campusid,
                                    createdby, modifiedby, createdon, modifiedon)
                  VALUES (@name, '', @tenantId, @schoolId, @campusId, @user, @user, @now, @now)
                  RETURNING Id",
                new { name = $"PERF Room {i + 1:00}", tenantId, schoolId, campusId, user = SystemUser, now }));
        }

        return ids;
    }

    /// <summary>
    /// REAL counts for the skip path. `timetableentry` and `classroomsubjectteacher` carry no scope
    /// columns, so they are counted through their parents - the same rule the clears follow.
    /// </summary>
    private async Task<TimetableSeedResult> ReadCountsAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId)
    {
        return new TimetableSeedResult
        {
            Classrooms = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM classroom
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            SetupDetails = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM timetablesetupdetail d
                    INNER JOIN timetablesetup s ON s.Id = d.TimeTableSetupId
                   WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            SetupOffDays = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM timetablesetupoffday o
                    INNER JOIN timetablesetup s ON s.Id = o.TimeTableSetupId
                   WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            Timetables = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM timetable
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            Entries = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM timetableentry te
                    INNER JOIN timetable t ON t.Id = te.TimetableId
                   WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            Assignments = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM classroomsubjectteacher cst
                    INNER JOIN classroom c ON c.Id = cst.ClassroomId
                   WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            Absences = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM teacherabsence
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            Reliefs = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM timetablerelief
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
            AuditRows = await conn.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*) FROM teachermoduleauditlog
                   WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
                new { tenantId, schoolId, campusId }),
        };
    }

    private sealed class TeacherCountRow
    {
        public long TeacherId { get; set; }
        public int Entries { get; set; }
    }
}
