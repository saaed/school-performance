using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the SIX reports that reported SKIP because their fact tables were EMPTY, and asserts the
/// shape that makes each one measurable.
///
///   rpt-grade-performance / rpt-subject-performance  -> studentsubjectresult
///   rpt-assessment-performance                       -> studentassessment
///   rpt-learning-outcome                             -> curriculumgradesubjecttopic (+ plans)
///   rpt-leave-summary                                -> employeeleaverequest
///   rpt-student-transfer                             -> enrollmenttransferhistory
///
/// ⚠️ WHY A SPEC OVER AN EMPTY TABLE IS WORSE THAN NO SPEC. The catalogue is honest - an empty
/// table reports SKIP rather than a fake pass - but SKIP reads as "not measured yet", and six
/// shipped reports sat in that state while the modules looked covered. The unlock is the DATA
/// (exactly as it was for HR, inventory, library, transport and accounting), and this fixture is
/// what proves the data landed in a shape the reports can actually reach.
///
/// ⚠️ EVERY ASSERTION HERE IS A JOIN, NOT A ROW COUNT. `vw_*` reports are INNER JOIN chains six to
/// nine tables deep, so a fact row whose parent id does not resolve is INVISIBLE to the report while
/// the table looks populated. That is precisely the defect the attendance seeder shipped once
/// (`attendance.studentenrollmentid` NULL on all 14.2M rows while `vw_student_attendance` joins on
/// that column, so the report returned 0 rows and still scanned the table). So each module asserts
/// that the VIEW returns exactly what the TABLE holds at the same scope - an equality, which only
/// holds when every join resolves.
///
/// Opt in with the same flag the other dataset fixtures use, because this seeds data rather than
/// asserting application behaviour:
///
///   SCUBE_PERF_DATASET=1 dotnet test SchoolDataVolume.csproj \
///       --filter "FullyQualifiedName~ReportingDataset"
///
/// Size it with SCUBE_PERF_MODULE_CAMPUSES (default 6) and the per-module knobs
/// (SCUBE_PERF_EXAM_COMPONENTS, SCUBE_PERF_CURR_GRADES, SCUBE_PERF_LEAVE_REQUESTS,
/// SCUBE_PERF_TRANSFERS). SCUBE_PERF_FORCE=1 re-seeds campuses that already hold rows.
/// </summary>
[Collection("Sequential")]
public class ReportingDatasetTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;

    public ReportingDatasetTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";
    }

    public void Dispose()
    {
        // Nothing to clean up: this fixture is the point of the database.
    }

    /// <summary>
    /// The `vw_*` views read their scope from the SESSION, not from bind parameters
    /// (`current_setting('app.tenant_id', true)`), which is how the reporting engine isolates them.
    /// A view queried without this fails CLOSED - it answers zero rows rather than raising - so an
    /// unset scope makes a correct seed look broken and a broken one look empty.
    /// </summary>
    private static async Task SetScopeAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId)
    {
        await conn.ExecuteAsync(
            @"SELECT set_config('app.tenant_id', @tenantId, false),
                     set_config('app.school_id', @schoolId, false),
                     set_config('app.campus_id', @campusId, false)",
            new
            {
                tenantId = tenantId.ToString(),
                schoolId = schoolId.ToString(),
                campusId = campusId.ToString()
            });
    }

    /// <summary>
    /// The load-bearing assertion: the report view returns exactly what the fact table holds at this
    /// scope. Any row whose parent id does not resolve is dropped by an INNER JOIN, so a strict
    /// decrease here names the join that would silently lose report rows.
    /// </summary>
    private static async Task AssertViewReachesEveryRowAsync(
        NpgsqlConnection conn, string table, string view, long campusId)
    {
        var tableRows = await conn.ExecuteScalarAsync<long>(
            $@"SELECT COUNT(*) FROM {table}
                WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

        var viewRows = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {view}");

        Assert.True(tableRows > 0,
            $"{table} holds no rows for campus {campusId}, so {view} cannot be measured at all");

        Assert.True(viewRows == tableRows,
            $"{view} returns {viewRows:N0} rows for the {table} rows {tableRows:N0} hold on campus " +
            $"{campusId} - {tableRows - viewRows:N0} row(s) are unreachable through the view's joins, " +
            "which is how a report returns nothing over a populated table.");
    }

    [Fact]
    public async Task Exams_dataset_seeds_the_assessment_spine_and_the_term_results()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the reporting perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed exams into - " +
            "run PerfDatasetTests first");

        var options = new ExamsSeedOptions
        {
            ComponentsPerSubject = SeedCampuses.EnvInt("SCUBE_PERF_EXAM_COMPONENTS", 3),
            ItemsPerAssessment = SeedCampuses.EnvInt("SCUBE_PERF_EXAM_ITEMS", 2),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding EXAMS/ASSESSMENTS for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.ComponentsPerSubject} components, " +
                          $"{options.ItemsPerAssessment} scored items each");
        _output.WriteLine("");

        var seeder = new ExamsModuleSeeder(_connectionString);
        var totalResults = 0;
        var totalAssessments = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalResults += result.SubjectResults;
            totalAssessments += result.StudentAssessments;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.AssessmentComponents,3} components " +
                $"{result.AcademicGradeSubjects,3} grade-subjects {result.SubjectAssessmentComponents,3} subject components " +
                $"{result.ExamSchedules,4} sittings {result.StudentExams,7:N0} registrations " +
                $"{result.StudentAssessments,7:N0} assessments {result.AssessmentItems,7:N0} items " +
                $"{result.SubjectResults,7:N0} results" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            // The two views the four reports page over, asserted on the campus just seeded.
            await SetScopeAsync(conn, SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId);

            if (!result.Skipped)
            {
                await AssertViewReachesEveryRowAsync(
                    conn, "studentsubjectresult", "vw_student_subject_results", campusId);
                await AssertViewReachesEveryRowAsync(
                    conn, "studentassessment", "vw_assessment_performance", campusId);
            }
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalResults:N0} term results, {totalAssessments:N0} assessments");

        Assert.True(totalResults > 0,
            "no subject results were seeded, so `rpt-grade-performance` and `rpt-subject-performance` " +
            "would still report SKIP");
        Assert.True(totalAssessments > 0,
            "no assessments were seeded, so `rpt-assessment-performance` would still report SKIP");

        // ⚠️ `assessmentcomponent` IS NOT NULL ON `subjectassessmentcomponent`, and it held ZERO rows
        // before this seeder. An assessment whose component does not resolve is dropped by the view's
        // INNER JOIN, which is why the spine is asserted rather than the component count alone.
        var componentless = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM subjectassessmentcomponent
               WHERE tenantid = @tenantId
                 AND NOT EXISTS (SELECT 1 FROM assessmentcomponent ac
                                  WHERE ac.id = subjectassessmentcomponent.assessmentcomponentid)",
            new { tenantId = SeedCampuses.TenantId });
        Assert.Equal(0, componentless);

        // A sitting must name the component it examines - the view joins
        // `examschedule -> subjectassessmentcomponent` and drops the row when it is NULL.
        var sittinglessComponents = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM examschedule
               WHERE tenantid = @tenantId AND subjectassessmentcomponentid IS NULL",
            new { tenantId = SeedCampuses.TenantId });
        Assert.Equal(0, sittinglessComponents);

        // `studentassessmentitem` is what the view's LATERAL sums. An assessment with no items scores
        // ZERO, so a campus of empty marks would report every student at 0% and passing nothing.
        var itemlessAssessments = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM studentassessment sa
               WHERE sa.tenantid = @tenantId
                 AND NOT EXISTS (SELECT 1 FROM studentassessmentitem i
                                  WHERE i.studentassessmentid = sa.id)",
            new { tenantId = SeedCampuses.TenantId });
        Assert.Equal(0, itemlessAssessments);

        await SeedCampuses.AssertScopeIsSelectiveAsync(
            conn, _output, "studentsubjectresult", "Exams");
    }

    [Fact]
    public async Task Curriculum_dataset_seeds_the_learning_outcome_tree()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the reporting perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var options = new CurriculumSeedOptions
        {
            GradesPerVersion = SeedCampuses.EnvInt("SCUBE_PERF_CURR_GRADES", 10),
            SubjectsPerGrade = SeedCampuses.EnvInt("SCUBE_PERF_CURR_SUBJECTS", 7),
            TopicsPerSubject = SeedCampuses.EnvInt("SCUBE_PERF_CURR_TOPICS", 15),
            PlansPerTopic = SeedCampuses.EnvInt("SCUBE_PERF_CURR_PLANS", 2),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding CURRICULUM (school {SeedCampuses.SchoolId}): " +
                          $"{options.GradesPerVersion} grades x {options.SubjectsPerGrade} subjects x " +
                          $"{options.TopicsPerSubject} topics, {options.PlansPerTopic} plans each");
        _output.WriteLine("");

        var seeder = new CurriculumModuleSeeder(_connectionString);
        var result = await seeder.SeedAsync(
            SeedCampuses.TenantId, SeedCampuses.SchoolId, options, verbose: false);

        _output.WriteLine(
            $"  version {result.CurriculumVersionId}: {result.Subjects} subjects, " +
            $"{result.CurriculumGrades} grades, {result.CurriculumGradeSubjects} grade-subjects, " +
            $"{result.Topics:N0} topics, {result.TopicPlans:N0} plans" +
            $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

        // ⚠️ The view is SCHOOL-scoped (tenant + school, NO campus), so the scope is set on a campus
        // the school owns and the count that matters is the school's.
        await SetScopeAsync(conn, SeedCampuses.TenantId, SeedCampuses.SchoolId, 1);

        var viewRows = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM vw_learning_outcome_performance");

        _output.WriteLine("");
        _output.WriteLine($"vw_learning_outcome_performance returns {viewRows:N0} rows");

        Assert.True(viewRows > 0,
            "vw_learning_outcome_performance returns nothing, so `rpt-learning-outcome` reports " +
            "an empty campus - the topic must resolve through curriculumgradesubject -> " +
            "curriculumgrade -> curriculumversion(status 5) -> curriculum, and its subject through " +
            "`subject`");

        // The tree only counts while the version is PUBLISHED - the view's own filter. A version
        // that is later unpublished would silently take every row with it, so the gate is asserted.
        var publishedTopics = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM curriculumgradesubjecttopic tp
                JOIN curriculumgradesubject cgs ON cgs.id = tp.curriculumgradesubjectid
                JOIN curriculumgrade cg ON cg.id = cgs.curriculumgradeid
                JOIN curriculumversion cv ON cv.id = cg.curriculumversionid
               WHERE cv.curriculumstatus = 5
                 AND cv.id = @versionId",
            new { versionId = result.CurriculumVersionId });

        Assert.True(publishedTopics > 0,
            "the version this seeder attached to is no longer PUBLISHED, so every topic it wrote is " +
            "invisible to the report");

        if (!result.Skipped)
        {
            Assert.True(result.TopicPlans > 0,
                "no term plans were seeded, so the view's LATERAL reports plancount 0 and " +
                "plannedminutes 0 for every topic");
        }
    }

    [Fact]
    public async Task Leave_dataset_seeds_the_leave_summary_facts_across_campuses()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the reporting perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed leave into");

        var options = new LeaveSeedOptions
        {
            RequestsPerEmployee = SeedCampuses.EnvInt("SCUBE_PERF_LEAVE_REQUESTS", 4),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding LEAVE for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.RequestsPerEmployee} requests per employee");
        _output.WriteLine("");

        var seeder = new HrLeaveSeeder(_connectionString);
        var totalRequests = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRequests += result.LeaveRequests;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.LeaveTypes,3} leave types " +
                $"{result.LeaveRequests,6:N0} requests" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (!result.Skipped)
            {
                // ⚠️ `leavetype` IS SEEDED PER CAMPUS because `GetActiveLeaveTypes` matches
                // tenant/school/campus EXACTLY while the eight canonical types sit at (1,1,1) - so
                // on any other campus the Apply dialog's dropdown is EMPTY and a request has nothing
                // to point at. Asserted here so the seeder cannot quietly fall back to the (1,1,1)
                // rows and leave the screen unreachable.
                var campusTypes = await conn.ExecuteScalarAsync<long>(
                    @"SELECT COUNT(*) FROM leavetype
                       WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                         AND isactive = true",
                    new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });
                Assert.True(campusTypes > 0,
                    $"campus {campusId} offers no ACTIVE leave type of its own, so its leave screen " +
                    "opens empty even though requests exist");

                await SetScopeAsync(conn, SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId);
                await AssertViewReachesEveryRowAsync(
                    conn, "employeeleaverequest", "vw_leave_summary", campusId);
            }
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRequests:N0} leave requests");

        Assert.True(totalRequests > 0,
            "no leave requests were seeded, so `rpt-leave-summary` would still report SKIP");

        // The view's three money columns are CASE expressions over the STATUS STRING, so a campus
        // seeded with one status measures one branch of every CASE and nothing else.
        var distinctStatuses = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(DISTINCT lower(status)) FROM employeeleaverequest
               WHERE tenantid = @tenantId",
            new { tenantId = SeedCampuses.TenantId });
        Assert.True(distinctStatuses >= 2,
            "every seeded leave request carries the same status, so the view's approved/pending/" +
            "rejected day columns all measure the same CASE branch");
    }

    [Fact]
    public async Task Transfer_dataset_seeds_the_transfer_history_across_campuses()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the reporting perf datasets.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed transfers into");

        var options = new TransferSeedOptions
        {
            TransfersPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_TRANSFERS", 40),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding TRANSFERS for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.TransfersPerCampus} per campus");
        _output.WriteLine("");

        var seeder = new TransferModuleSeeder(_connectionString);
        var totalTransfers = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalTransfers += result.Transfers;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Transfers,5} transfers" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (!result.Skipped)
            {
                await SetScopeAsync(conn, SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId);
                await AssertViewReachesEveryRowAsync(
                    conn, "enrollmenttransferhistory", "vw_student_transfer", campusId);
            }
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalTransfers:N0} transfer rows");

        Assert.True(totalTransfers > 0,
            "no transfer history was seeded, so `rpt-student-transfer` would still report SKIP");

        // ⚠️ THE TRANSFER TYPE MUST BE ONE THE PRODUCT CAN PRODUCE. `TransferType` is
        // Section=1 / Campus=2 / School=3 / Withdrawal=4 / Graduation=5, and a value outside that
        // set renders as a blank type in the report - a row that exists and says nothing.
        var invalidTypes = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM enrollmenttransferhistory
               WHERE tenantid = @tenantId AND transfertype NOT IN (1, 2, 3, 4, 5)",
            new { tenantId = SeedCampuses.TenantId });
        Assert.Equal(0, invalidTypes);

        // A relocation must name a destination the view can resolve, or the report prints a blank
        // campus name for a move that plainly went somewhere.
        var danglingDestinations = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM enrollmenttransferhistory eth
               WHERE eth.tenantid = @tenantId
                 AND eth.tocampusid IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM campus c WHERE c.id = eth.tocampusid)",
            new { tenantId = SeedCampuses.TenantId });
        Assert.Equal(0, danglingDestinations);
    }
}
