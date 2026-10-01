using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the COMMUNICATION WORKSPACE (`teacher`, `attachmentfile`, `homework` + its five children,
/// `moment` + its three, the four `schoolevent*` response tables, and `hrmeeting`) and asserts the
/// shape that makes their grid specs measure instead of report SKIP.
///
/// ⚠️ WHY THESE SIXTEEN LIVE IN ONE FIXTURE. Every one of them held ZERO rows in every database
/// here, and the cause is ONE missing prerequisite rather than sixteen oversights:
///
///     homework.teacherid  NOT NULL -> teacher
///     moment.teacherid    NOT NULL -> teacher
///
/// and `teacher` was empty on every perf campus DELIBERATELY (`HrModuleSeeder` writes designations
/// with `canteach = false` and says so). The communication workspace is the module that cannot be
/// measured without a teacher, so this seeder owns the teaching staff and the HR seeder keeps its
/// documented exclusion.
///
/// ⚠️ AND A `teacher` ROW IS NOT ENOUGH - THE GRID NEEDS THREE THINGS. `TeacherRepository.GetAll`
/// is `Teacher t INNER JOIN Users u ON t.UserId = u.Id INNER JOIN UserRole ur ON u.Id = ur.UserId
/// AND ur.RoleId = 4`, so a teacher row with no login, or a login with no Teacher role, is
/// INVISIBLE to the grid that exists to list it. The assertions below hold all three together.
///
/// ⚠️ EVERY ASSERTION IS A JOIN, A CONSTRAINT, OR THE APPLICATION'S OWN RULE - never a row count.
/// A count proves the INSERT ran; it cannot see a row the application cannot reach. The six that
/// matter:
///   * every `homework.teacherid` / `moment.teacherid` must resolve to a `teacher` row whose
///     `userid` carries the Teacher role AND whose `users` row shares the teacher's scope triple -
///     that join is the teacher grid's whole WHERE clause;
///   * every `homeworkstudent.homeworkid`, `homeworksubmission.homeworkid` and
///     `homeworkattachment.homeworkid` must resolve to a homework IN THIS SCOPE (the children carry
///     no scope columns of their own, so their spec has to join the parent - a child pointing at
///     another campus's homework is a row the scoped spec can never return);
///   * every attachment actually referenced by a child must EXIST, because
///     `homeworkattachmentfileid` / `homeworksubmissionattachmentfileid` /
///     `momentattachmentfileid` / `eventattachmentfileid` are NOT NULL and the orphan sweep decides
///     what to delete by matching exactly these references;
///   * every `schooleventread.parentid` / `.eventid` and `schooleventresponse.studentid` must
///     resolve, because neither table carries scope columns and both are read through their parent;
///   * `hrmeeting.startdatetime < enddatetime` - a meeting that ends before it starts is not a row
///     a grid can order meaningfully;
///   * every one of the four event children must resolve to a `schoolevent` on THIS campus.
///
/// ⚠️ IT DEPENDS ON `PerfDatasetSeeder` (students + enrollments) AND `MasterDataSeeder` (a
/// classroom and the school events). It does NOT depend on `HrModuleSeeder`'s employees existing in
/// any particular count - it flips the campus's own "Teacher" designation and provisions whatever
/// it finds there.
///
/// Opt in with the same flag the other dataset seeders use (it seeds data rather than asserting
/// application behaviour):
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~CommunicationWorkspaceDataset"
/// </summary>
public sealed class CommunicationWorkspaceDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public CommunicationWorkspaceDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Communication_workspace_dataset_fills_the_homework_moment_event_and_meeting_grids()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the communication workspace perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed workspace rows into - " +
            "run PerfDatasetTests first");

        var options = new CommWorkspaceOptions
        {
            Teachers = SeedCampuses.EnvInt("SCUBE_PERF_CW_TEACHERS", 15),
            Homeworks = SeedCampuses.EnvInt("SCUBE_PERF_CW_HOMEWORKS", 60),
            Moments = SeedCampuses.EnvInt("SCUBE_PERF_CW_MOMENTS", 60),
            Meetings = SeedCampuses.EnvInt("SCUBE_PERF_CW_MEETINGS", 24),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding COMMUNICATION WORKSPACE for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.Teachers} teachers, " +
                          $"{options.Homeworks} homework, {options.Moments} moments, {options.Meetings} meetings");
        _output.WriteLine("");

        var seeder = new CommunicationWorkspaceSeeder(_connectionString);
        var totalRows = 0;
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRows += result.Teachers + result.AttachmentFiles + result.Homeworks +
                         result.HomeworkStudents + result.HomeworkSubmissions + result.HomeworkAttachments +
                         result.SubmissionAttachments + result.SubmissionComments + result.Moments +
                         result.MomentStudents + result.MomentAttachments + result.MomentComments +
                         result.EventReads + result.EventResponses + result.EventAudience +
                         result.EventAttachments + result.Meetings;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Teachers,3} teachers {result.AttachmentFiles,3} files " +
                $"{result.Homeworks,3} homework ({result.HomeworkStudents,5} rolls, {result.HomeworkSubmissions,4} submissions, " +
                $"{result.HomeworkAttachments,3} attach, {result.SubmissionAttachments,4} sub-attach, {result.SubmissionComments,4} comments) " +
                $"{result.Moments,3} moments ({result.MomentStudents,5} rolls, {result.MomentAttachments,3} attach, {result.MomentComments,4} comments) " +
                $"{result.EventReads,4} reads {result.EventResponses,4} responses {result.EventAudience,3} audience {result.EventAttachments,3} event-attach " +
                $"{result.Meetings,3} meetings" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            seededCampusIds.Add(campusId);
            await AssertForeignKeyClosureAsync(conn, campusId, _output);
            await AssertTeacherGridJoinResolvesAsync(conn, campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRows:N0} communication workspace rows");

        Assert.True(totalRows > 0,
            "no communication workspace rows were seeded, so the homework / moment / event / meeting " +
            "grids over these tables would still report SKIP");

        if (seededCampusIds.Count > 0)
        {
            await AssertMeetingsEndAfterTheyStartAsync(conn, seededCampusIds);
            await AssertEveryAttachmentIsReferencedAsync(conn, seededCampusIds);
        }
    }

    /// <summary>
    /// The teacher grid's own WHERE clause, held together: a homework's teacher must have a
    /// `teacher` row, that row must carry a `userid`, that user must exist at the SAME scope, and it
    /// must hold the Teacher role. Break any one of the four and the teacher row exists while the
    /// grid that lists teachers is empty - the documented "a seeded row no query can reach" defect.
    ///
    /// ⚠️ The scope triple on `users` matters because `TeacherRepository.GetAll` compares
    /// `t.TenantId = u.TenantId AND t.SchoolId = u.SchoolId AND t.CampusId = u.CampusId`; a login
    /// written at another scope would satisfy the FK and fail the join.
    /// </summary>
    private static async Task AssertTeacherGridJoinResolvesAsync(NpgsqlConnection conn, long campusId)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        // (a) Every homework/moment teacher resolves to a teacher row in this scope.
        foreach (var (table, label) in new[] { ("homework", "homework"), ("moment", "moment") })
        {
            var orphans = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {table} x
                     LEFT JOIN teacher t ON t.id = x.teacherid
                    WHERE x.tenantid = @tenantId AND x.schoolid = @schoolId AND x.campusid = @campusId
                      AND t.id IS NULL",
                new { tenantId, schoolId, campusId });

            Assert.True(orphans == 0,
                $"campus {campusId} holds {orphans} {label} row(s) whose teacherid resolves to no `teacher` " +
                "row - `homework.teacherid`/`moment.teacherid` are NOT NULL with an FK, so this means the " +
                "teacher was deleted out from under them");
        }

        // (b) Every teacher this seeder wrote is visible to the teacher GRID: the three-way join the
        //     repository uses. A teacher row without any of the three is unreachable.
        var invisible = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM teacher t
                WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                  AND NOT EXISTS (
                        SELECT 1 FROM users u
                          JOIN userrole ur ON ur.userid = u.id AND ur.roleid = 4
                         WHERE u.id = t.userid
                           AND u.tenantid = t.tenantid
                           AND u.schoolid = t.schoolid
                           AND u.campusid = t.campusid)",
            new { tenantId, schoolId, campusId });

        Assert.True(invisible == 0,
            $"campus {campusId} holds {invisible} teacher row(s) that `TeacherRepository.GetAll` cannot " +
            "reach - each needs a `users` row at the SAME scope AND a `userrole` grant of the Teacher " +
            "role (id 4), or the teacher grid lists nobody while `teacher` holds rows");
    }

    /// <summary>
    /// The child tables carry NO scope columns, so their grid spec has to reach them through the
    /// parent - which means a child pointing at another campus's parent is a row the scoped spec can
    /// never return. This asserts the whole closure: every child resolves, and it resolves to a
    /// parent in THIS scope.
    /// </summary>
    private static async Task AssertForeignKeyClosureAsync(NpgsqlConnection conn, long campusId,
        ITestOutputHelper output)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        // (a) homework children -> a homework in this scope.
        var homeworkChildren = new[]
        {
            ("homeworkstudent", "homeworkid", "homework rolls"),
            ("homeworksubmission", "homeworkid", "homework submissions"),
            ("homeworkattachment", "homeworkid", "homework attachments"),
        };

        foreach (var (child, column, label) in homeworkChildren)
        {
            var foreign = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {child} c
                     JOIN homework h ON h.id = c.{column}
                    WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId
                      AND (h.tenantid, h.schoolid, h.campusid) IS DISTINCT FROM (@tenantId, @schoolId, @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(foreign == 0,
                $"campus {campusId} holds {foreign} {label} pointing at a homework outside its scope");

            var orphaned = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {child} c
                     LEFT JOIN homework h ON h.id = c.{column}
                    WHERE c.{column} IN (SELECT id FROM homework
                                          WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
                      AND h.id IS NULL",
                new { tenantId, schoolId, campusId });

            Assert.True(orphaned == 0,
                $"campus {campusId} holds {orphaned} {label} whose parent homework does not resolve at all");
        }

        // (b) moment children -> a moment in this scope.
        foreach (var (child, column, label) in new[]
                 {
                     ("momentstudent", "momentid", "moment rolls"),
                     ("momentattachment", "momentid", "moment attachments"),
                     ("momentcomment", "momentid", "moment comments"),
                 })
        {
            var foreign = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {child} c
                     JOIN moment m ON m.id = c.{column}
                    WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId
                      AND (m.tenantid, m.schoolid, m.campusid) IS DISTINCT FROM (@tenantId, @schoolId, @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(foreign == 0,
                $"campus {campusId} holds {foreign} {label} pointing at a moment outside its scope");
        }

        // (c) the four schoolevent children -> a schoolevent on this campus.
        foreach (var (child, column, label) in new[]
                 {
                     ("schooleventread", "eventid", "read receipts"),
                     ("schooleventresponse", "eventid", "event responses"),
                     ("schooleventaudience", "eventid", "audience rows"),
                     ("schooleventattachment", "eventid", "event attachments"),
                 })
        {
            var foreign = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {child} c
                     JOIN schoolevent e ON e.id = c.{column}
                    WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
                      AND (e.tenantid, e.schoolid, e.campusid) IS DISTINCT FROM (@tenantId, @schoolId, @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(foreign == 0,
                $"campus {campusId} holds {foreign} {label} pointing at an event outside its scope");

            var orphaned = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {child} c
                     LEFT JOIN schoolevent e ON e.id = c.{column}
                    WHERE c.{column} IN (SELECT id FROM schoolevent
                                          WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
                      AND e.id IS NULL",
                new { tenantId, schoolId, campusId });

            Assert.True(orphaned == 0,
                $"campus {campusId} holds {orphaned} {label} whose parent event does not resolve at all");
        }

        // (d) `schooleventread.parentid` and `schooleventresponse.studentid` are NOT NULL and both
        //     have an FK, but nothing about the FK says the target is readable at THIS scope. A read
        //     receipt for another campus's parent is a row the campus's own read can never join.
        var badParents = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM schooleventread r
                 JOIN schoolevent e ON e.id = r.eventid
                WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
                  AND NOT EXISTS (SELECT 1 FROM parent p WHERE p.id = r.parentid)",
            new { tenantId, schoolId, campusId });

        if (badParents > 0)
        {
            // The seeder falls back to `users` when the campus holds no `parent` row, so a
            // parentid that is not a parent is expected there - but it must at least be a user.
            var alsoNotAUser = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM schooleventread r
                     JOIN schoolevent e ON e.id = r.eventid
                    WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
                      AND NOT EXISTS (SELECT 1 FROM parent p WHERE p.id = r.parentid)
                      AND NOT EXISTS (SELECT 1 FROM users u WHERE u.id = r.parentid)",
                new { tenantId, schoolId, campusId });

            Assert.True(alsoNotAUser == 0,
                $"campus {campusId} holds {alsoNotAUser} read receipt(s) whose parentid resolves to neither " +
                "a `parent` nor a `users` row");
        }

        output.WriteLine(
            $"  campus {campusId,-5} FK closure verified across 10 child tables");
    }

    /// <summary>
    /// `hrmeeting` is the only self-contained table in this batch, and the one rule its grid's
    /// ordering depends on: a meeting ends after it starts. Cheap, and it is the kind of defect a
    /// bulk generator produces silently.
    /// </summary>
    private static async Task AssertMeetingsEndAfterTheyStartAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var backwards = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM hrmeeting
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND startdatetime >= enddatetime",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(backwards == 0,
                $"campus {campusId} holds {backwards} meeting(s) that end at or before they start");
        }
    }

    /// <summary>
    /// Every attachment file the workspace wrote must be REACHABLE from a child row.
    ///
    /// ⚠️ This is not tidiness. `AttachmentFileRepository`'s orphan sweep decides what to delete by
    /// matching a reference from the tables that own files, so a file this batch wrote and forgot to
    /// link is a row the next `attachment/clean` deletes - and the grid then renders a broken link on
    /// a row that looks perfectly healthy. The file must ALSO carry the entity type its owner uses,
    /// which is the other half of the sweep's decision.
    /// </summary>
    private static async Task AssertEveryAttachmentIsReferencedAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var orphanFiles = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM attachmentfile af
                   WHERE af.tenantid = @tenantId AND af.schoolid = @schoolId AND af.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM homeworkattachment ha
                                      WHERE ha.homeworkattachmentfileid = af.id
                                         OR ha.videoattachmentfileid = af.id)
                     AND NOT EXISTS (SELECT 1 FROM homeworksubmissionattachment hsa
                                      WHERE hsa.homeworksubmissionattachmentfileid = af.id
                                         OR hsa.videosubmissionattachmentfileid = af.id)
                     AND NOT EXISTS (SELECT 1 FROM momentattachment ma
                                      WHERE ma.momentattachmentfileid = af.id
                                         OR ma.momentvideothumbnailfileid = af.id)
                     AND NOT EXISTS (SELECT 1 FROM schooleventattachment ea
                                      WHERE ea.eventattachmentfileid = af.id)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphanFiles == 0,
                $"campus {campusId} holds {orphanFiles} attachmentfile row(s) no child references - the " +
                "orphan sweep deletes exactly these, so a grid would render a broken link on a healthy row");
        }
    }
}
