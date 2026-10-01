using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>
/// Options for <see cref="CommunicationWorkspaceSeeder"/>. The defaults give one
/// median-sized campus a realistically populated communication workspace - a term's worth of
/// homework and moments, not a token row count.
/// </summary>
public sealed class CommWorkspaceOptions
{
    /// <summary>
    /// Employees turned into Teachers. Every one of them gets a `teacher` row, a login and the
    /// Teacher role, because BOTH grids need all three (see the class comment).
    /// </summary>
    public int Teachers { get; set; } = 15;

    /// <summary>Homework set over the term. This is the homework grid's row count.</summary>
    public int Homeworks { get; set; } = 60;

    /// <summary>Moments (learning/behaviour updates) posted over the term.</summary>
    public int Moments { get; set; } = 60;

    /// <summary>Staff meetings. Small in reality and small here.</summary>
    public int Meetings { get; set; } = 24;

    /// <summary>Students a homework is assigned to (`homeworkstudent` rows per homework).</summary>
    public int StudentsPerHomework { get; set; } = 40;

    /// <summary>Students who actually submit (`homeworksubmission` rows per homework).</summary>
    public int SubmissionsPerHomework { get; set; } = 25;

    /// <summary>Attachments on each homework, and comments on each submission.</summary>
    public int AttachmentsPerItem { get; set; } = 2;
    public int CommentsPerSubmission { get; set; } = 2;

    /// <summary>Parent responses recorded per event, and read receipts.</summary>
    public int ResponsesPerEvent { get; set; } = 20;
    public int ReadsPerEvent { get; set; } = 20;

    /// <summary>Re-seed even when the campus already holds workspace rows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's workspace seed produced. Returned so a run can report real counts.</summary>
public sealed class CommWorkspaceSeedResult
{
    public bool Skipped { get; set; }
    public int Teachers { get; set; }
    public int AttachmentFiles { get; set; }
    public int Homeworks { get; set; }
    public int HomeworkStudents { get; set; }
    public int HomeworkSubmissions { get; set; }
    public int HomeworkAttachments { get; set; }
    public int SubmissionAttachments { get; set; }
    public int SubmissionComments { get; set; }
    public int Moments { get; set; }
    public int MomentStudents { get; set; }
    public int MomentAttachments { get; set; }
    public int MomentComments { get; set; }
    public int EventReads { get; set; }
    public int EventResponses { get; set; }
    public int EventAudience { get; set; }
    public int EventAttachments { get; set; }
    public int Meetings { get; set; }
}

/// <summary>
/// Seeds the COMMUNICATION WORKSPACE - `homework` and its five children, `moment` and its three,
/// the school-event response tables, and `hrmeeting` - for one campus.
///
/// WHY THESE TABLES WERE EMPTY, AND WHY IT IS ONE PROBLEM RATHER THAN SIXTEEN
/// -------------------------------------------------------------------------
/// Every one of the sixteen tables in this seeder is a GRID TARGET that held ZERO rows, so its
/// spec would have reported SKIP and a SKIP cannot be told from an unmeasured grid. The cause is
/// not sixteen oversights; it is ONE missing prerequisite:
///
///     homework.teacherid  NOT NULL  -> teacher
///     moment.teacherid    NOT NULL  -> teacher
///
/// and **`teacher` was EMPTY ON EVERY PERF CAMPUS - deliberately.** `HrModuleSeeder` writes its
/// designations with `canteach = false` on purpose, and says so in a comment there:
///
///     "`canteach` is FALSE on purpose. `EmployeeProvisioningService` turns an employee whose
///      DESIGNATION has canteach=true into a Teacher, which writes to `teacher` - a table with
///      NOT NULL dob/nic/address/photo columns and its own provisioning rules. The teacher module
///      has its own concerns; seeding it here would make this seeder responsible for a module it
///      is not measuring."
///
/// That is a sound boundary for the HR seeder, and it is exactly why this one has to exist: the
/// communication workspace is the module that CANNOT be measured without a teacher. So this seeder
/// owns the teaching staff, and the HR seeder keeps its deliberate exclusion.
///
/// ⚠️ AND A `teacher` ROW IS NOT ENOUGH - THE GRID NEEDS THREE MORE THINGS, ALL OF WHICH THIS
/// SEEDER WRITES. `TeacherRepository.GetAll` is:
///
///     FROM Teacher t
///     INNER JOIN Users u    ON t.UserId = u.Id AND t.TenantId = u.TenantId AND ...
///     INNER JOIN UserRole ur ON u.Id = ur.UserId AND ur.RoleId = 4   -- Teacher
///
/// so a teacher row with no login, or a login with no Teacher role, is INVISIBLE to the grid that
/// exists to list it. That is the documented "a seeded row no query can reach is worse than a
/// missing one" defect, and it would have looked like a working seed with an empty grid.
///
/// WHICH TABLES
/// ------------
///     teacher                 the prerequisite (see above)
///     attachmentfile          the second prerequisite (Homework=1 / Moment=2 / Event=3)
///     homework                + homeworkstudent, homeworksubmission,
///                               homeworksubmissionattachment, homeworksubmissioncomment,
///                               homeworkattachment
///     moment                  + momentstudent, momentattachment, momentcomment
///     schooleventread / schooleventresponse / schooleventaudience / schooleventattachment
///     hrmeeting
///
/// ⚠️ THE ACADEMIC YEAR AND TERM COME FROM THE ENROLLMENTS, NOT FROM THE CLASSROOM.
/// The natural way to place a homework is `classroom -> academicgrade -> academicyear`, and on
/// `ayra_perf` that chain is WRONG: the perf classrooms all point at `academicgrade` **1**, which
/// belongs to campus **1** (campus 15 has no `academicgrade` of its own at all - there are two rows
/// in the whole table, for years 1 and 2). The classroom's own enrollments, by contrast, carry the
/// campus's real year (14) and its real term (14). So the year/term are resolved from
/// `studentenrollment`, which is the only place the campus's actual academic year is recorded, and
/// the classroom is still used for `classroomid` because that is where the enrollments are.
/// ⚠️ This classroom->grade cross-campus link is a PRE-EXISTING incoherence in the perf dataset,
/// not something this seeder introduced; it is recorded here because any future seeder that walks
/// that chain will silently place its rows in another campus's year.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds homework is skipped unless Force is set.
/// </summary>
public sealed class CommunicationWorkspaceSeeder : BaseSeeder
{
    public CommunicationWorkspaceSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk workspace load invalidates, so <see cref="PerfDatasetSeeder"/> can ANALYZE
    /// them. Seeding without this leaves the planner reasoning from the row counts that described
    /// an EMPTY table.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "teacher", "attachmentfile", "homework", "homeworkstudent", "homeworksubmission",
        "homeworkattachment", "homeworksubmissionattachment", "homeworksubmissioncomment",
        "moment", "momentstudent", "momentattachment", "momentcomment",
        "schooleventread", "schooleventresponse", "schooleventaudience", "schooleventattachment",
        "hrmeeting"
    };

    /// <summary>The designation the app's own provisioning rule keys on (see the class comment).</summary>
    private const string TeachingDesignationName = "Teacher";

    /// <summary>`SystemRoles.Teacher` - the role `TeacherRepository.GetAll` inner-joins on.</summary>
    private const int TeacherRoleId = 4;

    /// <summary>`AttachmentEntityType` - the value the orphan sweep matches a reference by.</summary>
    private const short EntityHomework = 1;
    private const short EntityMoment = 2;
    private const short EntityEvent = 3;

    /// <summary>`HomeworkStatus` - the vocabulary the homework grid renders.</summary>
    private static readonly short[] HomeworkStatuses = { 1, 2, 3, 4, 5 };

    /// <summary>`EventResponseStatus` - Pending / Accepted / Declined / Paid.</summary>
    private static readonly short[] EventStatuses = { 0, 1, 2, 3 };

    /// <summary>
    /// `moment.status` is a bare `short` with NO enum and NO check constraint anywhere in the
    /// server - the controller never interprets it. The values below are therefore a plausible
    /// spread rather than a vocabulary, and this note is the honest record of that.
    /// </summary>
    private static readonly short[] MomentStatuses = { 1, 2 };

    private static readonly string[] MomentPayloads =
    {
        "{\"text\":\"Completed today's reading task.\"}",
        "{\"text\":\"Helped a classmate during group work.\"}",
        "{\"text\":\"Needs to bring the art supplies tomorrow.\"}",
        "{\"text\":\"Excellent participation in the science project.\"}"
    };

    private static readonly string[] HomeworkTitles =
    {
        "Reading comprehension set", "Fractions worksheet", "Science observation log",
        "Arabic handwriting practice", "Islamic studies reflection", "Social studies map work",
        "Computer science algorithms", "Multiplication drill", "Spelling list revision",
        "Lab report write-up"
    };

    private static readonly string[] MeetingTitles =
    {
        "Weekly academic review", "Term planning session", "Parent-teacher coordination",
        "Department heads sync", "Assessment moderation"
    };

    private static readonly string[] Comments =
    {
        "Good work, please check the last question.", "Well presented.",
        "Please revise and resubmit.", "Submitted on time."
    };

    public async Task<CommWorkspaceSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, CommWorkspaceOptions options, bool verbose = true)
    {
        var result = new CommWorkspaceSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM homework
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // ⚠️ REPORT what the campus holds rather than returning zeros. A zero-return made the
            // fixture's own "did anything get seeded" assertion fail on a re-run - the rule
            // ExamsModuleSeeder recorded.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  CommWorkspace: campus {campusId} already holds {existing:N0} homework - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 0. A forced re-seed clears first. Every id in this file is
        //    `GENERATED ALWAYS AS IDENTITY`, so nothing is ever written by id and a
        //    re-seed cannot collide - but appending would still double the volume,
        //    which is the thing a perf seeder must never do.
        //
        //    ⚠️ EVERY CHILD TABLE HERE CASCADES FROM ITS SCOPED PARENT (verified:
        //    all twelve child FKs are ON DELETE CASCADE), so one scoped DELETE clears
        //    each tree and `ClearTableByParentAsync` is needed only for the four
        //    `schoolevent*` children - whose parent, `schoolevent`, is another
        //    seeder's data and must NOT be deleted.
        // ------------------------------------------------------------------
        if (options.Force)
        {
            if (verbose) Console.WriteLine("  CommWorkspace: clearing this campus's workspace rows...");

            // homework cascades: homeworkstudent, homeworksubmission,
            // homeworksubmissionattachment, homeworksubmissioncomment, homeworkattachment
            await ClearTableAsync(conn, "homework", tenantId, schoolId, campusId);
            // moment cascades: momentstudent, momentattachment, momentcomment
            await ClearTableAsync(conn, "moment", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "hrmeeting", tenantId, schoolId, campusId);

            // The four event children have NO scope columns of their own, so they are cleared
            // through their scoped parent. `schoolevent` itself belongs to MasterDataSeeder.
            await ClearTableByParentAsync(conn, "schooleventread", "eventid", "schoolevent",
                tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "schooleventresponse", "eventid", "schoolevent",
                tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "schooleventaudience", "eventid", "schoolevent",
                tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "schooleventattachment", "eventid", "schoolevent",
                tenantId, schoolId, campusId);

            await ClearTableAsync(conn, "attachmentfile", tenantId, schoolId, campusId);
            // teacher LAST - homework/moment reference it, and both are now empty.
            await ClearTableAsync(conn, "teacher", tenantId, schoolId, campusId);
        }

        // ------------------------------------------------------------------
        // 1. Resolve the scaffold. Everything below is READ, not invented.
        // ------------------------------------------------------------------
        var scaffold = await ResolveScaffoldAsync(conn, tenantId, schoolId, campusId, options, verbose);
        if (scaffold == null)
        {
            if (verbose)
            {
                Console.WriteLine(
                    "  CommWorkspace: this campus has no enrolled classroom, so there is nowhere to " +
                    "place a homework. Run PerfDatasetSeeder/ExamsModuleSeeder for this campus first.");
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 2. The PREREQUISITE: teaching staff.
        //
        //    The app's rule is "an employee whose DESIGNATION has canteach=true becomes a
        //    Teacher", so the rule is satisfied by flipping ONE boolean on the campus's existing
        //    "Teacher" designation rather than by inventing a parallel one - the designation is
        //    HrModuleSeeder's row, the employees on it are already HR's data, and the only thing
        //    that was withheld is the flag. Everything after that is what
        //    `EmployeeProvisioningService` would have written.
        // ------------------------------------------------------------------
        await EnsureTeachersAsync(conn, tenantId, schoolId, campusId, scaffold, options, result, now, verbose);

        if (scaffold.TeacherIds.Count == 0)
        {
            if (verbose)
            {
                Console.WriteLine(
                    "  CommWorkspace: no teaching staff could be resolved, so homework/moment have no " +
                    "`teacherid` to point at - nothing seeded.");
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 3. The SECOND prerequisite: files. `homeworkattachment`, `homeworksubmissionattachment`,
        //    `momentattachment` and `schooleventattachment` all carry a NOT NULL file id, so the
        //    workspace cannot exist without rows in `attachmentfile`.
        //
        //    ⚠️ THE ENTITY TYPE IS LOAD-BEARING, not decoration. `AttachmentFileRepository`'s
        //    orphan sweep decides what to keep by matching a REFERENCE from the tables that own
        //    files, so these rows are stamped with the type that owns them AND are linked from a
        //    child row below - which is what keeps them out of the sweep.
        // ------------------------------------------------------------------
        var files = await EnsureAttachmentFilesAsync(conn, tenantId, schoolId, campusId, now, result);
        if (files == null)
        {
            if (verbose) Console.WriteLine("  CommWorkspace: could not create attachment files - nothing seeded.");
            return result;
        }

        // ------------------------------------------------------------------
        // 4-7. The workspace itself.
        // ------------------------------------------------------------------
        await SeedHomeworkAsync(conn, tenantId, schoolId, campusId, scaffold, files, options, result, now, verbose);
        await SeedMomentsAsync(conn, tenantId, schoolId, campusId, scaffold, files, options, result, now, verbose);
        await SeedEventResponsesAsync(conn, tenantId, schoolId, campusId, scaffold, files, options, result, now, verbose);
        await SeedMeetingsAsync(conn, tenantId, schoolId, campusId, scaffold, options, result, now);

        if (verbose)
        {
            Console.WriteLine(
                $"  CommWorkspace: campus {campusId} - {result.Teachers} teachers, " +
                $"{result.Homeworks} homework, {result.Moments} moments, {result.Meetings} meetings");
        }

        return result;
    }

    // ======================================================================
    // SCAFFOLD
    // ======================================================================

    /// <summary>
    /// A campus's own classroom + the year/term its enrollments belong to + the students in it +
    /// the topic plans reachable from its curriculum grade. Returns null when the campus has no
    /// enrolled classroom, because there is then nowhere to place a homework.
    /// </summary>
    private sealed class Scaffold
    {
        public long ClassroomId { get; set; }
        public long AcademicYearId { get; set; }
        public long TermId { get; set; }
        public long CurriculumGradeId { get; set; }
        public List<long> EnrollmentIds { get; set; } = new();
        public List<long> StudentIds { get; set; } = new();
        public List<long> TopicPlanIds { get; set; } = new();
        public List<long> MomentTypeIds { get; set; } = new();
        public List<long> EventIds { get; set; } = new();
        public List<long> EventTermIds { get; set; } = new();
        public List<long> ParentIds { get; set; } = new();
        public List<long> SubjectIds { get; set; } = new();
        public long AdminUserId { get; set; }
        public List<TeacherCandidate> TeacherCandidates { get; set; } = new();
        public List<long> TeacherIds { get; set; } = new();
    }

    private sealed class TeacherCandidate
    {
        public long EmployeeId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? NationalId { get; set; }
        public string? Address { get; set; }
        public byte[]? Photo { get; set; }
        public long? UserId { get; set; }
    }

    private async Task<Scaffold?> ResolveScaffoldAsync(NpgsqlConnection conn, long tenantId, long schoolId,
        long campusId, CommWorkspaceOptions options, bool verbose)
    {
        // ⚠️ THE YEAR AND TERM COME FROM THE ENROLLMENTS. The classroom's own
        // `academicgrade -> academicyear` chain points at ANOTHER CAMPUS on this dataset (see the
        // class comment), so it cannot be used to place a row.
        var enrollmentRows = (await conn.QueryAsync<EnrollmentRow>(
            @"SELECT se.id AS EnrollmentId, se.studentid AS StudentId, se.classroomid AS ClassroomId,
                     se.academicyearid AS AcademicYearId
                FROM studentenrollment se
               WHERE se.tenantid = @tenantId AND se.schoolid = @schoolId AND se.campusid = @campusId
                 AND se.classroomid IS NOT NULL
               ORDER BY se.id",
            new { tenantId, schoolId, campusId })).ToList();

        if (enrollmentRows.Count == 0) return null;

        var scaffold = new Scaffold
        {
            ClassroomId = enrollmentRows[0].ClassroomId,
            AcademicYearId = enrollmentRows[0].AcademicYearId,
        };
        scaffold.EnrollmentIds = enrollmentRows.Select(r => r.EnrollmentId).ToList();
        scaffold.StudentIds = enrollmentRows.Select(r => r.StudentId).Distinct().ToList();

        // The campus's term for THAT year. `terms` has no scope columns, so a campus's terms are
        // the terms of its years - `terms.campusid` is a 42703.
        scaffold.TermId = await conn.ExecuteScalarAsync<long>(
            @"SELECT tm.id
                FROM terms tm
                JOIN academicyear ay ON ay.id = tm.academicyearid
               WHERE ay.tenantid = @tenantId AND ay.schoolid = @schoolId AND ay.campusid = @campusId
                 AND tm.academicyearid = @yearId
               ORDER BY tm.id
               LIMIT 1",
            new { tenantId, schoolId, campusId, yearId = scaffold.AcademicYearId });

        if (scaffold.TermId == 0) return null;

        // The curriculum grade the classroom teaches, and the topic plans reachable from it.
        // Read through the classroom (the app's own path) even though the GRADE's year belongs to
        // another campus - the grade TREE is shared, and `curriculumtopicplanid` is NOT NULL on
        // homework, so a plan link is required.
        scaffold.CurriculumGradeId = await conn.ExecuteScalarAsync<long>(
            @"SELECT COALESCE(ag.curriculumgradeid, 0)
                FROM classroom c
                JOIN academicgrade ag ON ag.id = c.academicgradeid
               WHERE c.id = @classroomId",
            new { classroomId = scaffold.ClassroomId });

        scaffold.TopicPlanIds = (await conn.QueryAsync<long>(
            @"SELECT ctp.id
                FROM curriculumtopicplan ctp
                JOIN curriculumgradesubjecttopic t ON t.id = ctp.curriculumtopicid
                JOIN curriculumgradesubject cgs ON cgs.id = t.curriculumgradesubjectid
               WHERE cgs.curriculumgradeid = @gradeId
               ORDER BY ctp.id",
            new { gradeId = scaffold.CurriculumGradeId })).ToList();

        // `momenttype` ships as reference data at scope (0,0,0) - readable by every campus.
        scaffold.MomentTypeIds = (await conn.QueryAsync<long>(
            "SELECT id FROM momenttype ORDER BY id")).ToList();

        // The events, and each one's own term (matching what MasterDataSeeder stamped).
        var events = (await conn.QueryAsync<EventRow>(
            @"SELECT id AS EventId, COALESCE(termid, 0) AS TermId
                FROM schoolevent
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();
        scaffold.EventIds = events.Select(e => e.EventId).ToList();
        scaffold.EventTermIds = events.Select(e => e.TermId == 0 ? scaffold.TermId : e.TermId).ToList();

        // Parents, for the read receipts. `schooleventread.parentid` is NOT NULL.
        scaffold.ParentIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM parent
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();
        if (scaffold.ParentIds.Count == 0)
        {
            // A campus with no `parent` row cannot have a read receipt. Fall back to the
            // campus's users so the table is still reachable rather than silently empty.
            scaffold.ParentIds = (await conn.QueryAsync<long>(
                @"SELECT id FROM users
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                   ORDER BY id",
                new { tenantId, schoolId, campusId })).ToList();
        }

        scaffold.SubjectIds = (await conn.QueryAsync<long>(
            @"SELECT subjectid FROM curriculumgradesubject
               WHERE curriculumgradeid = @gradeId ORDER BY subjectid",
            new { gradeId = scaffold.CurriculumGradeId })).ToList();

        // The audit columns every table here requires. `1` is the system user, matching the e2e
        // baseline and MasterDataSeeder.
        scaffold.AdminUserId = 1;

        // Teaching candidates: the employees on the campus's "Teacher" designation.
        scaffold.TeacherCandidates = (await conn.QueryAsync<TeacherCandidate>(
            @"SELECT e.id AS EmployeeId, e.email AS Email, e.firstname AS FirstName,
                     e.lastname AS LastName, e.phone AS Phone, e.dateofbirth AS DateOfBirth,
                     e.nationalid AS NationalId, e.address AS Address, e.photo AS Photo,
                     e.userid AS UserId
                FROM employee e
                JOIN designation d ON d.id = e.designationid
               WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
                 AND LOWER(d.name) = @designation
                 AND e.email IS NOT NULL AND e.email <> ''
               ORDER BY e.id
               LIMIT @take",
            new
            {
                tenantId, schoolId, campusId,
                designation = TeachingDesignationName.ToLowerInvariant(),
                take = options.Teachers
            })).ToList();

        if (verbose)
        {
            Console.WriteLine(
                $"  CommWorkspace scaffold: classroom {scaffold.ClassroomId}, year {scaffold.AcademicYearId}, " +
                $"term {scaffold.TermId} - {scaffold.EnrollmentIds.Count:N0} enrollments, " +
                $"{scaffold.TopicPlanIds.Count:N0} topic plans, {scaffold.EventIds.Count} events, " +
                $"{scaffold.TeacherCandidates.Count} teaching candidates");
        }

        return scaffold;
    }

    private sealed class EnrollmentRow
    {
        public long EnrollmentId { get; set; }
        public long StudentId { get; set; }
        public long ClassroomId { get; set; }
        public long AcademicYearId { get; set; }
    }

    private sealed class EventRow
    {
        public long EventId { get; set; }
        public long TermId { get; set; }
    }

    // ======================================================================
    // 2. THE PREREQUISITE - teaching staff
    // ======================================================================

    /// <summary>
    /// Flip the campus's "Teacher" designation to `canteach`, then write exactly what
    /// `EmployeeProvisioningService` writes for each of its employees: a `users` row, the
    /// `employee.userid` link, a `teacher` row, and the Teacher role.
    /// </summary>
    private async Task EnsureTeachersAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        Scaffold scaffold, CommWorkspaceOptions options, CommWorkspaceSeedResult result, DateTime now, bool verbose)
    {
        if (scaffold.TeacherCandidates.Count == 0) return;

        // The app's own switch. Without it an employee legitimately has no teacher profile.
        await conn.ExecuteAsync(
            @"UPDATE designation
                 SET canteach = true, canlogin = true
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND LOWER(name) = @designation",
            new
            {
                tenantId, schoolId, campusId,
                designation = TeachingDesignationName.ToLowerInvariant()
            });

        foreach (var candidate in scaffold.TeacherCandidates)
        {
            var userId = candidate.UserId;

            // 2a. A login account. `users.email` is deliberately NOT unique (one person holds one
            //     row per scope they administer), so this looks the row up by email + scope.
            if (userId == null || userId == 0)
            {
                userId = await conn.ExecuteScalarAsync<long?>(
                    @"SELECT id FROM users
                       WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                         AND LOWER(email) = @email",
                    new { tenantId, schoolId, campusId, email = candidate.Email.ToLowerInvariant() });

                if (userId == null)
                {
                    // ⚠️ `dob`/`nic`/`address`/`photo` are NOT NULL with no default on `teacher`,
                    //    and `maritalstatus` too. That is a legacy ORM artifact: the old model wrote
                    //    an EMPTY STRING (and a zero-length blob) for an unset property instead of
                    //    NULL, while both screens treat those fields as optional. Passing NULL here
                    //    is what `EmployeeProvisioningService` says made every teaching hire fail
                    //    with 23502 - so the empties below are the faithful value, not padding.
                    userId = await conn.ExecuteScalarAsync<long>(
                        // ⚠️ `status` IS WRITTEN, AND LEAVING IT NULL MADE THE USERS GRID LIST NOBODY.
                        // `UserRepository.GetAll` filters `AND u.Status <> 'Disabled'`, and in SQL
                        // `NULL <> 'Disabled'` is NULL - so a NULL-status account is excluded from the
                        // screen that administers it, while `AccountStatusPolicy.CanSignIn` (which admits
                        // empty/NULL) lets it sign in. Measured on `ayra_perf`: all 15 users of the
                        // measured campus carried NULL, so the grid rendered an EMPTY page for a campus
                        // with 15 users and the `usertwofactor` spec measured zero rows.
                        // The application's own create path writes 'Pending' then 'Active' on activation,
                        // so 'Active' is the status these accounts would really carry.
                        @"INSERT INTO users
                              (tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon,
                               firstname, lastname, email, password, confirmemail, confirmmobile, status)
                          VALUES (@tenantId, @schoolId, @campusId, 1, 1, @now, @now,
                                  @firstName, @lastName, @email, 'Perf-Seed-2026!', true, true, 'Active')
                          RETURNING id",
                        new
                        {
                            tenantId, schoolId, campusId, now,
                            firstName = candidate.FirstName,
                            lastName = candidate.LastName,
                            email = candidate.Email
                        });

                    await conn.ExecuteAsync(
                        "UPDATE employee SET userid = @userId WHERE id = @employeeId",
                        new { userId, employeeId = candidate.EmployeeId });
                }
            }

            // 2b. The teacher profile.
            var existingTeacher = await conn.ExecuteScalarAsync<long?>(
                "SELECT id FROM teacher WHERE employeeid = @employeeId",
                new { employeeId = candidate.EmployeeId });

            long teacherId;
            if (existingTeacher == null)
            {
                teacherId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO teacher
                          (dob, nic, address, maritalstatus, religion, degree, photo,
                           userid, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon,
                           employeeid, qualification, teachinglicense, yearsexperience,
                           specialization, biography, notes)
                      VALUES (@dob, @nic, @address, false, NULL, NULL, @photo,
                              @userId, @tenantId, @schoolId, @campusId,
                              1, 1, @now, @now,
                              @employeeId, NULL, NULL, NULL, NULL, NULL, NULL)
                      RETURNING id",
                    new
                    {
                        dob = candidate.DateOfBirth?.ToString("yyyy-MM-dd") ?? string.Empty,
                        nic = candidate.NationalId ?? string.Empty,
                        address = candidate.Address ?? string.Empty,
                        photo = candidate.Photo ?? Array.Empty<byte>(),
                        userId, tenantId, schoolId, campusId, now,
                        employeeId = candidate.EmployeeId
                    });
            }
            else
            {
                teacherId = existingTeacher.Value;
            }

            // 2c. THE ROLE. `TeacherRepository.GetAll` INNER JOINs `userrole` on the Teacher role,
            //     so without this the teacher row exists and the teacher GRID is still empty.
            var hasRole = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM userrole WHERE userid = @userId AND roleid = @roleId",
                new { userId, roleId = TeacherRoleId });

            if (hasRole == 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO userrole (userid, roleid, isactive, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@userId, @roleId, true, 1, 1, @now, @now)",
                    new { userId, roleId = TeacherRoleId, now });
            }

            scaffold.TeacherIds.Add(teacherId);
        }

        result.Teachers = scaffold.TeacherIds.Count;
        if (verbose) Console.WriteLine($"  CommWorkspace: {result.Teachers} teachers provisioned");
    }

    // ======================================================================
    // 3. THE SECOND PREREQUISITE - files
    // ======================================================================

    /// <summary>
    /// A small pool of `attachmentfile` rows, three per entity type, that the workspace children
    /// point at. The entity type is what the orphan sweep reads, so it is not decoration.
    /// </summary>
    private async Task<List<long>?> EnsureAttachmentFilesAsync(NpgsqlConnection conn, long tenantId,
        long schoolId, long campusId, DateTime now, CommWorkspaceSeedResult result)
    {
        var ids = new List<long>();
        var perEntity = 3;
        var entities = new[] { EntityHomework, EntityMoment, EntityEvent };

        foreach (var entityType in entities)
        {
            for (var i = 0; i < perEntity; i++)
            {
                var id = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO attachmentfile
                          (filename, filepath, filetype, entitytype,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@filename, @filepath, @filetype, @entitytype,
                              @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                      RETURNING id",
                    new
                    {
                        filename = $"PERF-CW-{entityType}-{i + 1}.pdf",
                        filepath = $"/perf/cw/{entityType}/{i + 1}.pdf",
                        filetype = (short)1,
                        entitytype = entityType,
                        tenantId, schoolId, campusId, now
                    });
                ids.Add(id);
            }
        }

        result.AttachmentFiles = ids.Count;
        return ids.Count == 0 ? null : ids;
    }

    // ======================================================================
    // 4. HOMEWORK
    // ======================================================================

    private async Task SeedHomeworkAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        Scaffold scaffold, List<long> files, CommWorkspaceOptions options,
        CommWorkspaceSeedResult result, DateTime now, bool verbose)
    {
        if (scaffold.TopicPlanIds.Count == 0)
        {
            if (verbose)
            {
                Console.WriteLine(
                    "  CommWorkspace: no curriculumtopicplan is reachable from this classroom, and " +
                    "`homework.curriculumtopicplanid` is NOT NULL - homework skipped.");
            }
            return;
        }

        var homeworkFiles = files.Take(3).ToList();

        for (var h = 0; h < options.Homeworks; h++)
        {
            var teacherId = scaffold.TeacherIds[h % scaffold.TeacherIds.Count];
            var planId = scaffold.TopicPlanIds[h % scaffold.TopicPlanIds.Count];
            var subjectId = scaffold.SubjectIds.Count == 0
                ? (long?)null
                : scaffold.SubjectIds[h % scaffold.SubjectIds.Count];

            var homeworkId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO homework
                      (title, description, academicyearid, termid, classroomid, teacherid, subjectid,
                       curriculumtopicplanid, duedate, iswholeclassroom, isstudentaddattachment,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@title, @description, @academicYearId, @termId, @classroomId, @teacherId, @subjectId,
                          @planId, @dueDate, @isWholeClassroom, true,
                          @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    title = $"{HomeworkTitles[h % HomeworkTitles.Length]} - PERF-CW-{campusId}-{h + 1}",
                    description = "Seeded for the performance dataset.",
                    academicYearId = scaffold.AcademicYearId,
                    termId = scaffold.TermId,
                    classroomId = scaffold.ClassroomId,
                    teacherId,
                    subjectId,
                    planId,
                    dueDate = now.AddDays(-(h % 45)).AddDays(7),
                    // `iswholeclassroom` true on most rows, so `homeworkstudent` is the minority
                    // path - which is the shape the grid actually meets.
                    isWholeClassroom = h % 4 != 0,
                    tenantId, schoolId, campusId, now
                });
            result.Homeworks++;

            // 4a. The roll.
            var rollCount = Math.Min(options.StudentsPerHomework, scaffold.StudentIds.Count);
            for (var s = 0; s < rollCount; s++)
            {
                var studentId = scaffold.StudentIds[(h * 7 + s) % scaffold.StudentIds.Count];
                await conn.ExecuteAsync(
                    @"INSERT INTO homeworkstudent
                          (homeworkid, studentid, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@homeworkId, @studentId, 1, 1, @now, @now)",
                    new { homeworkId, studentId, now });
                result.HomeworkStudents++;
            }

            // 4b. Attachments. `homeworkattachmentfileid` is NOT NULL.
            for (var a = 0; a < options.AttachmentsPerItem && homeworkFiles.Count > 0; a++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO homeworkattachment
                          (homeworkid, homeworkattachmentfileid, videoattachmentfileid, instructions,
                           stepnumber, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@homeworkId, @fileId, NULL, @instructions, @step, 1, 1, @now, @now)",
                    new
                    {
                        homeworkId,
                        fileId = homeworkFiles[a % homeworkFiles.Count],
                        instructions = "Seeded attachment.",
                        step = (short)(a + 1),
                        now
                    });
                result.HomeworkAttachments++;
            }

            // 4c. Submissions, then each submission's own attachment and comments. The submission
            //     is where the STUDENT's work is, so it is a subset of the roll - which is the
            //     real distribution and also what makes the grid's status column varied.
            var submissionCount = Math.Min(options.SubmissionsPerHomework, rollCount);
            for (var s = 0; s < submissionCount; s++)
            {
                var studentId = scaffold.StudentIds[(h * 7 + s) % scaffold.StudentIds.Count];
                var status = HomeworkStatuses[(h + s) % HomeworkStatuses.Length];

                var submissionId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO homeworksubmission
                          (homeworkid, studentid, homeworkstatus, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@homeworkId, @studentId, @status, 1, 1, @now, @now)
                      RETURNING id",
                    new { homeworkId, studentId, status, now });
                result.HomeworkSubmissions++;

                await conn.ExecuteAsync(
                    @"INSERT INTO homeworksubmissionattachment
                          (homeworksubmissionid, homeworksubmissionattachmentfileid,
                           videosubmissionattachmentfileid, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@submissionId, @fileId, NULL, 1, 1, @now, @now)",
                    new { submissionId, fileId = homeworkFiles[(h + s) % homeworkFiles.Count], now });
                result.SubmissionAttachments++;

                for (var c = 0; c < options.CommentsPerSubmission; c++)
                {
                    await conn.ExecuteAsync(
                        @"INSERT INTO homeworksubmissioncomment
                              (homeworksubmissionid, commentedby, comment, createdby, modifiedby, createdon, modifiedon)
                          VALUES (@submissionId, @commentedBy, @comment, 1, 1, @now, @now)",
                        new
                        {
                            submissionId,
                            commentedBy = scaffold.AdminUserId,
                            comment = Comments[(h + c) % Comments.Length],
                            now
                        });
                    result.SubmissionComments++;
                }
            }

            if (h % 10 == 0) LogProgress("  CommWorkspace homework", h + 1, options.Homeworks);
        }
    }

    // ======================================================================
    // 5. MOMENTS
    // ======================================================================

    private async Task SeedMomentsAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        Scaffold scaffold, List<long> files, CommWorkspaceOptions options,
        CommWorkspaceSeedResult result, DateTime now, bool verbose)
    {
        if (scaffold.MomentTypeIds.Count == 0)
        {
            if (verbose) Console.WriteLine("  CommWorkspace: `momenttype` is empty - moments skipped.");
            return;
        }

        var momentFiles = files.Skip(3).Take(3).ToList();

        for (var m = 0; m < options.Moments; m++)
        {
            var teacherId = scaffold.TeacherIds[m % scaffold.TeacherIds.Count];
            var subjectId = scaffold.SubjectIds.Count == 0
                ? (long?)null
                : scaffold.SubjectIds[m % scaffold.SubjectIds.Count];

            var momentId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO moment
                      (teacherid, classroomid, subjectid, momenttypeid, academicyearid, termid,
                       payloadjson, status, iswholeclassroom,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@teacherId, @classroomId, @subjectId, @momentTypeId, @academicYearId, @termId,
                          @payload, @status, @isWholeClassroom,
                          @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    teacherId,
                    classroomId = scaffold.ClassroomId,
                    subjectId,
                    momentTypeId = scaffold.MomentTypeIds[m % scaffold.MomentTypeIds.Count],
                    academicYearId = scaffold.AcademicYearId,
                    termId = scaffold.TermId,
                    payload = MomentPayloads[m % MomentPayloads.Length],
                    status = MomentStatuses[m % MomentStatuses.Length],
                    isWholeClassroom = m % 3 != 0,
                    tenantId, schoolId, campusId, now
                });
            result.Moments++;

            var rollCount = Math.Min(options.StudentsPerHomework, scaffold.StudentIds.Count);
            for (var s = 0; s < rollCount; s++)
            {
                var studentId = scaffold.StudentIds[(m * 5 + s) % scaffold.StudentIds.Count];
                await conn.ExecuteAsync(
                    @"INSERT INTO momentstudent
                          (momentid, studentid, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@momentId, @studentId, 1, 1, @now, @now)",
                    new { momentId, studentId, now });
                result.MomentStudents++;
            }

            if (momentFiles.Count > 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO momentattachment
                          (momentid, momentattachmentfileid, momentvideothumbnailfileid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@momentId, @fileId, NULL, 1, 1, @now, @now)",
                    new { momentId, fileId = momentFiles[m % momentFiles.Count], now });
                result.MomentAttachments++;
            }

            for (var c = 0; c < options.CommentsPerSubmission; c++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO momentcomment
                          (momentid, commentedby, comment, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@momentId, @commentedBy, @comment, 1, 1, @now, @now)",
                    new
                    {
                        momentId,
                        commentedBy = scaffold.AdminUserId,
                        comment = Comments[(m + c) % Comments.Length],
                        now
                    });
                result.MomentComments++;
            }

            if (m % 10 == 0) LogProgress("  CommWorkspace moments", m + 1, options.Moments);
        }
    }

    // ======================================================================
    // 6. EVENT RESPONSES
    // ======================================================================

    private async Task SeedEventResponsesAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        Scaffold scaffold, List<long> files, CommWorkspaceOptions options,
        CommWorkspaceSeedResult result, DateTime now, bool verbose)
    {
        if (scaffold.EventIds.Count == 0)
        {
            if (verbose) Console.WriteLine("  CommWorkspace: no school events on this campus - responses skipped.");
            return;
        }

        if (scaffold.ParentIds.Count == 0)
        {
            // `schooleventread.parentid` is NOT NULL and there is no parent to point at. Recorded
            // rather than skipped silently, because an empty read-receipt grid is otherwise
            // indistinguishable from one that was never seeded.
            if (verbose)
            {
                Console.WriteLine(
                    "  CommWorkspace: no `parent` (or fallback user) on this campus, and " +
                    "`schooleventread.parentid` is NOT NULL - read receipts skipped.");
            }
        }

        var eventFiles = files.Skip(6).Take(3).ToList();

        for (var e = 0; e < scaffold.EventIds.Count; e++)
        {
            var eventId = scaffold.EventIds[e];

            // 6a. Read receipts.
            for (var r = 0; r < options.ReadsPerEvent && scaffold.ParentIds.Count > 0; r++)
            {
                var parentId = scaffold.ParentIds[(e * 3 + r) % scaffold.ParentIds.Count];
                await conn.ExecuteAsync(
                    @"INSERT INTO schooleventread (eventid, parentid, readon)
                      VALUES (@eventId, @parentId, @readOn)",
                    new { eventId, parentId, readOn = now.AddDays(-(r % 30)) });
                result.EventReads++;
            }

            // 6b. Responses. `studentid` is NOT NULL and has an FK, so a student is required.
            for (var r = 0; r < options.ResponsesPerEvent && scaffold.StudentIds.Count > 0; r++)
            {
                var studentId = scaffold.StudentIds[(e * 11 + r) % scaffold.StudentIds.Count];
                await conn.ExecuteAsync(
                    @"INSERT INTO schooleventresponse
                          (eventid, studentid, status, paymentreceiptattachment, remarks, responsedate,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@eventId, @studentId, @status, NULL, @remarks, @responseDate,
                              1, 1, @now, @now)",
                    new
                    {
                        eventId, studentId,
                        status = EventStatuses[(e + r) % EventStatuses.Length],
                        remarks = "Seeded response.",
                        responseDate = now.AddDays(-(r % 30)),
                        now
                    });
                result.EventResponses++;
            }

            // 6c. Audience: a classroom, a subject or a teacher - all three columns are nullable,
            //     which is what makes this the event's targeting table rather than a roll.
            if (scaffold.SubjectIds.Count > 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO schooleventaudience (eventid, classroomid, subjectid, teacherid)
                      VALUES (@eventId, @classroomId, @subjectId, NULL)",
                    new
                    {
                        eventId,
                        classroomId = scaffold.ClassroomId,
                        subjectId = scaffold.SubjectIds[e % scaffold.SubjectIds.Count]
                    });
                result.EventAudience++;

                await conn.ExecuteAsync(
                    @"INSERT INTO schooleventaudience (eventid, classroomid, subjectid, teacherid)
                      VALUES (@eventId, NULL, NULL, @teacherId)",
                    new { eventId, teacherId = scaffold.TeacherIds[e % scaffold.TeacherIds.Count] });
                result.EventAudience++;
            }

            // 6d. Attachment. `eventattachmentfileid` is NOT NULL.
            if (eventFiles.Count > 0)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO schooleventattachment (eventid, eventattachmentfileid, createdon)
                      VALUES (@eventId, @fileId, @now)",
                    new { eventId, fileId = eventFiles[e % eventFiles.Count], now });
                result.EventAttachments++;
            }
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  CommWorkspace events: {result.EventReads} reads, {result.EventResponses} responses, " +
                $"{result.EventAudience} audience rows, {result.EventAttachments} attachments");
        }
    }

    // ======================================================================
    // 7. MEETINGS
    // ======================================================================

    private async Task SeedMeetingsAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        Scaffold scaffold, CommWorkspaceOptions options, CommWorkspaceSeedResult result, DateTime now)
    {
        for (var i = 0; i < options.Meetings; i++)
        {
            // `hrmeeting` is the only table in this seeder that is entirely self-contained: its
            // `id` has a `nextval` default (not an identity), and `isactive` / the audit columns
            // have real defaults - but they are written explicitly anyway so every row in this
            // file has one shape.
            await conn.ExecuteAsync(
                @"INSERT INTO hrmeeting
                      (tenantid, schoolid, campusid, title, agenda, startdatetime, enddatetime,
                       organizeremployeeid, location, isactive, createdby, modifiedby, createdon, modifiedon,
                       recurrencerule, recurrenceend)
                  VALUES (@tenantId, @schoolId, @campusId, @title, @agenda, @start, @end,
                          NULL, @location, true, 1, 1, @now, @now, NULL, NULL)",
                new
                {
                    tenantId, schoolId, campusId,
                    title = $"{MeetingTitles[i % MeetingTitles.Length]} - PERF-CW-{campusId}-{i + 1}",
                    agenda = "Seeded meeting agenda.",
                    start = now.AddDays(-(i % 60)).Date.AddHours(9),
                    end = now.AddDays(-(i % 60)).Date.AddHours(10),
                    location = "Main Hall",
                    now
                });
            result.Meetings++;
        }
    }

    // ======================================================================
    // SKIP-PATH REPORTING
    // ======================================================================

    /// <summary>
    /// Report what the campus already holds. The SKIP path must produce REAL numbers, or the
    /// fixture's own "did anything get seeded" assertion fails on a re-run - the rule
    /// ExamsModuleSeeder recorded.
    /// </summary>
    private async Task ReadCountsAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        CommWorkspaceSeedResult result)
    {
        async Task<int> CountAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table} WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.Teachers = await CountAsync("teacher");
        result.AttachmentFiles = await CountAsync("attachmentfile");
        result.Homeworks = await CountAsync("homework");
        result.Moments = await CountAsync("moment");
        result.Meetings = await CountAsync("hrmeeting");

        // The children have no scope columns, so they are counted through their parent.
        result.HomeworkStudents = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM homeworkstudent hs
                JOIN homework h ON h.id = hs.homeworkid
               WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.HomeworkSubmissions = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM homeworksubmission hs
                JOIN homework h ON h.id = hs.homeworkid
               WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.HomeworkAttachments = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM homeworkattachment ha
                JOIN homework h ON h.id = ha.homeworkid
               WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.SubmissionAttachments = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM homeworksubmissionattachment hsa
                JOIN homeworksubmission hs ON hs.id = hsa.homeworksubmissionid
                JOIN homework h ON h.id = hs.homeworkid
               WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.SubmissionComments = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM homeworksubmissioncomment hsc
                JOIN homeworksubmission hs ON hs.id = hsc.homeworksubmissionid
                JOIN homework h ON h.id = hs.homeworkid
               WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.MomentStudents = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM momentstudent ms
                JOIN moment m ON m.id = ms.momentid
               WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.MomentAttachments = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM momentattachment ma
                JOIN moment m ON m.id = ma.momentid
               WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.MomentComments = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM momentcomment mc
                JOIN moment m ON m.id = mc.momentid
               WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.EventReads = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM schooleventread er
                JOIN schoolevent e ON e.id = er.eventid
               WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.EventResponses = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM schooleventresponse er
                JOIN schoolevent e ON e.id = er.eventid
               WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.EventAudience = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM schooleventaudience ea
                JOIN schoolevent e ON e.id = ea.eventid
               WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId",
            new { tenantId, schoolId, campusId });
        result.EventAttachments = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM schooleventattachment ea
                JOIN schoolevent e ON e.id = ea.eventid
               WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId",
            new { tenantId, schoolId, campusId });
    }
}
