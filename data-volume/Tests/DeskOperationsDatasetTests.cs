using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the desk / administrative tables and then asserts the joins that decide whether their reads
/// return anything at all.
///
/// ⚠️ THEY ARE NOT ONE MODULE - what they share is that each is a TABLE WITH A REAL READ SURFACE whose
/// parents were already populated, so every one of those reads returned an empty result from a fully
/// configured campus: `reportview` + `reportrunlog` + `reportexport` (the reporting desk's own
/// history), `postingbatch` (the accounting monitor's audit trail), `rolloverauditlog` (the year-end
/// History panel), `tenantsubscription`, `curriculumtopiclearningmaterial`, `bankfiletemplate` +
/// `bankfileexport`, `hrmeetingaudience`, `usertwofactor` and `userpasswordhistory`.
///
/// ⚠️ THE ASSERTIONS ARE THE QUERIES' OWN PREDICATES, NOT COUNTS:
///
///   * a report definition is reachable at the campus's scope OR at the shared 0/0/0 default, which is
///     how `ReportDefinitionRepository` resolves every read - a `reportview` pointing at a definition
///     the campus cannot see is a saved view whose definition vanishes from the runner;
///   * `ReportExportRepository.GetByJobId` is the ONLY read of `reportexport`, so an export whose
///     `jobid` matches no job is a row nothing can ever fetch (`jobid` is UNIQUE with NO foreign key,
///     so the database would not complain);
///   * a `bankfileexport` must resolve BOTH its period and its template, and the template must belong
///     to the SAME campus - the repository LEFT JOINs both for the names it renders;
///   * `HrMeetingRepository.GetAudience` takes the meeting, so an audience row whose meeting belongs to
///     another campus is unreachable AND miscounted by the campus's own dashboard;
///   * `UserPasswordHistoryRepository.IsReuseAsync` filters `UserId = ANY(@UserIds)` and scans
///     `ORDER BY CreatedOn DESC, Id DESC LIMIT 5` - so the rows must be DISTINCT in time for the window
///     to be a window, and each hash must be the 48-byte PBKDF2 form `PasswordHasher.VerifyPassword`
///     accepts (anything else returns false for the wrong reason);
///   * `usertwofactor` is keyed on `userid` ALONE (its primary key), so a row for a user another campus
///     owns is invisible to that campus's grid.
///
/// ⚠️ TWO TABLES ON THE WORKLIST ARE DELIBERATELY LEFT EMPTY, and the fixture asserts the fact that
/// makes it correct rather than enforcing a count on them:
///   * `calendarreminderlog` is a WRITE-ONLY outbox (`CalendarReminderService` is its only writer,
///     `Channel = 'Mock'`, and nothing in either server reads it) - so a row in it is unreachable by
///     construction and no spec could measure it;
///   * `teacherparentaction` has NO code reference anywhere in either server and no screen.
/// See <see cref="DeskOperationsSeeder"/>'s class comment; both are recorded there, not silently skipped.
///
/// Opt in with the same flag the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~DeskOperationsDataset"
/// </summary>
public sealed class DeskOperationsDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public DeskOperationsDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Desk_operations_dataset_fills_every_empty_desk_table_that_has_a_read_surface()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the desk/operations dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed desk rows into - " +
            "run PerfDatasetTests first");

        var options = new DeskOperationsSeedOptions
        {
            SavedViews = SeedCampuses.EnvInt("SCUBE_PERF_DESK_VIEWS", 3),
            RunLogRows = SeedCampuses.EnvInt("SCUBE_PERF_DESK_RUNLOG", 60),
            Exports = SeedCampuses.EnvInt("SCUBE_PERF_DESK_EXPORTS", 3),
            PostingBatches = SeedCampuses.EnvInt("SCUBE_PERF_DESK_BATCHES", 24),
            RolloverLogs = SeedCampuses.EnvInt("SCUBE_PERF_DESK_ROLLOVERS", 6),
            BankTemplates = SeedCampuses.EnvInt("SCUBE_PERF_DESK_BANK_TEMPLATES", 3),
            AudiencePerMeeting = SeedCampuses.EnvInt("SCUBE_PERF_DESK_AUDIENCE", 5),
            PasswordHistoryRows = SeedCampuses.EnvInt("SCUBE_PERF_DESK_PASSWORDS", 6),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding DESK OPERATIONS for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]");
        _output.WriteLine("");

        var seeder = new DeskOperationsSeeder(_connectionString);
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                if (result.SavedViews > 0) seededCampusIds.Add(campusId);
                continue;
            }

            _output.WriteLine(
                $"  campus {campusId,-5} views {result.SavedViews,2} runlog {result.RunLogRows,3} exports {result.Exports,2} " +
                $"batches {result.PostingBatches,3} rollovers {result.RolloverLogs,2} " +
                $"bank tpl {result.BankTemplates,2} bank exp {result.BankExports,2} audience {result.MeetingAudienceRows,4} " +
                $"2fa {result.TwoFactorRows,2} pwd history {result.PasswordHistoryRows,2}");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");

        Assert.True(seededCampusIds.Count > 0,
            "no campus holds a saved report view - every campus was skipped for a missing prerequisite, so " +
            "the desk specs would still measure empty tables");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES: every one of these tables held ZERO rows, so the planner's
        // statistics describe an empty table. The list is the SEEDER's own declaration.
        foreach (var table in DeskOperationsSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertEverySavedViewResolvesAVisibleDefinitionAsync(conn, seededCampusIds);
        await AssertEveryExportResolvesItsJobAndDefinitionAsync(conn, seededCampusIds);
        await AssertPostingBatchesCarryACoherentLifecycleAsync(conn, seededCampusIds);
        await AssertRolloverLogsPointAtRealYearsAsync(conn, seededCampusIds);
        await AssertOneSubscriptionPerTenantAsync(conn);
        await AssertEveryBankExportResolvesItsOwnCampusesPeriodAndTemplateAsync(conn, seededCampusIds);
        await AssertEveryAudienceRowBelongsToItsOwnCampusMeetingAsync(conn, seededCampusIds);
        await AssertTheIdentityTablesBelongToTheCampusAsync(conn, seededCampusIds);
        await AssertTheWriteOnlyTablesAreStillEmptyAsync(conn);
        await AssertNoSentinelTimestampsAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// ⚠️ A DEFINITION IS REACHABLE AT THE CAMPUS'S SCOPE **OR** AT 0/0/0.
    /// `ReportDefinitionRepository` resolves every read as `(scoped) OR (tenantid = 0 AND schoolid = 0
    /// AND campusid = 0)`, with a scoped row overriding the shared default. A `reportview` pointing at a
    /// definition the campus cannot see is a saved view whose definition silently disappears from the
    /// runner - the desktop shortcut still lists it and clicking it loads nothing.
    /// </summary>
    private static async Task AssertEverySavedViewResolvesAVisibleDefinitionAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var orphans = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM reportview rv
                   WHERE rv.tenantid = @tenantId AND rv.schoolid = @schoolId AND rv.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM reportdefinition rd
                            WHERE rd.id = rv.reportdefinitionid
                              AND ((rd.tenantid = rv.tenantid AND rd.schoolid = rv.schoolid AND rd.campusid = rv.campusid)
                                OR (rd.tenantid = 0 AND rd.schoolid = 0 AND rd.campusid = 0)))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphans == 0,
                $"campus {campusId} holds {orphans} saved report view(s) whose definition is neither scoped to " +
                "the campus nor at the shared 0/0/0 default - the runner resolves a definition that way, so " +
                "these views list a report nobody can open");

            var runs = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM reportrunlog rl
                   WHERE rl.tenantid = @tenantId AND rl.schoolid = @schoolId AND rl.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM reportdefinition rd
                            WHERE rd.id = rl.reportdefinitionid
                              AND ((rd.tenantid = rl.tenantid AND rd.schoolid = rl.schoolid AND rd.campusid = rl.campusid)
                                OR (rd.tenantid = 0 AND rd.schoolid = 0 AND rd.campusid = 0)))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(runs == 0,
                $"campus {campusId} holds {runs} run-log row(s) whose definition the campus cannot resolve - " +
                "the run history renders them against a definition it cannot name");

            // ⚠️ THE USER MATTERS TOO: the history grid shows WHO ran the report, and a user from another
            // campus renders as an unattributable row.
            var foreignUsers = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM reportrunlog rl
                   WHERE rl.tenantid = @tenantId AND rl.schoolid = @schoolId AND rl.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM users u
                            WHERE u.id = rl.userid
                              AND u.tenantid = rl.tenantid AND u.schoolid = rl.schoolid AND u.campusid = rl.campusid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(foreignUsers == 0,
                $"campus {campusId} holds {foreignUsers} run-log row(s) attributable to a user that does not " +
                "belong to the campus - the history grid renders the run without a name");
        }
    }

    /// <summary>
    /// ⚠️ `GetByJobId` IS THE ONLY READ OF `reportexport`, AND `jobid` HAS NO FOREIGN KEY.
    /// The column is UNIQUE but unconstrained, so an export written with a fabricated job id is a row
    /// nothing can ever fetch - and because the download endpoint is reached FROM the job, no screen
    /// would show the loss either. This asserts the shape the application can actually produce.
    /// </summary>
    private static async Task AssertEveryExportResolvesItsJobAndDefinitionAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        var jobless = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM reportexport re
               WHERE NOT EXISTS (SELECT 1 FROM backgroundjob j WHERE j.id = re.jobid)");

        Assert.True(jobless == 0,
            $"{jobless} `reportexport` row(s) point at a `jobid` that is not a background job - " +
            "`ReportExportRepository.GetByJobId` is the only read of this table and the download is reached " +
            "through the job, so these rows are unreachable by construction");

        var defless = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM reportexport re
               WHERE NOT EXISTS (SELECT 1 FROM reportdefinition rd WHERE rd.id = re.reportdefinitionid)");

        Assert.True(defless == 0,
            $"{defless} `reportexport` row(s) point at a missing report definition - the export names the " +
            "report it was run from, and the FK is the only thing that catches it");

        // The artifact must declare which format it IS, because the download reads the content type off
        // the row (the CSV and PDF exporters share this table).
        var undeclared = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM reportexport
               WHERE format IS NULL OR contenttype IS NULL");

        Assert.True(undeclared == 0,
            $"{undeclared} `reportexport` row(s) carry no `format`/`contenttype` - the download endpoint " +
            "serves the bytes with the row's own declared media type, so an undeclared export downloads as " +
            "an untyped blob");

        // ⚠️ A PDF EXPORT CARRIES BYTES, A CSV ONE CARRIES TEXT - and the format decides which column is
        // populated. `content` is nullable exactly because of this (V124).
        var wronglyStored = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM reportexport
               WHERE (format = 'pdf' AND contentbytes IS NULL)
                  OR (format = 'csv' AND content IS NULL)");

        Assert.True(wronglyStored == 0,
            $"{wronglyStored} `reportexport` row(s) store their artifact in the wrong column for their " +
            "format - a PDF lives in `contentbytes` and a CSV in `content`");
    }

    /// <summary>
    /// ⚠️ THE STATUS VOCABULARY IS A FILTER, AND `completedon` MUST NOT PRECEDE `startedon`.
    /// The posting monitor's tabs filter on this column; a value outside the vocabulary makes the row
    /// invisible to every tab (`All` is the only place it would appear) while the summary counts it.
    /// </summary>
    private static async Task AssertPostingBatchesCarryACoherentLifecycleAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM postingbatch
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `postingbatch` row - the accounting monitor's audit trail is empty");

            var unknown = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM postingbatch
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status NOT IN ('Running', 'Completed', 'PartiallyFailed', 'Failed')",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unknown == 0,
                $"campus {campusId} holds {unknown} posting batch(es) with a status outside the app's own " +
                "vocabulary (Running/Completed/PartiallyFailed/Failed) - the monitor's status tabs filter on " +
                "those tokens, so these rows are reachable only from the All tab");

            var impossible = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM postingbatch
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND completedon IS NOT NULL AND completedon < startedon",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(impossible == 0,
                $"campus {campusId} holds {impossible} posting batch(es) that completed BEFORE they started");

            // A batch must reconcile: processed + failed is what it was asked to do, and a failed share
            // with no error text is a failure the monitor cannot explain.
            var unreconciled = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM postingbatch
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND totalfailed > 0 AND (errormessage IS NULL OR errormessage = '')",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreconciled == 0,
                $"campus {campusId} holds {unreconciled} posting batch(es) reporting failed postings with no " +
                "message - the monitor shows the batch's error text, so a failure with none is unexplained");
        }
    }

    /// <summary>
    /// ⚠️ THE HISTORY PANEL AND THE ROLLBACK CONTROL READ THESE ROWS. `GetByCampus`/`GetLatest` order by
    /// id, the panel prints the two year LABELS, and the Roll Back action is offered from
    /// `isrolledback = false` while the rolled-back rows are what prove the undo ran. So the year ids
    /// must resolve to real academic years (the labels are only readable because of that) and the two
    /// states must be mutually consistent.
    /// </summary>
    private static async Task AssertRolloverLogsPointAtRealYearsAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM rolloverauditlog
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `rolloverauditlog` row - the year-end History panel and the " +
                "Roll Back action both read this table");

            var danglingYears = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM rolloverauditlog r
                   WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                     AND (NOT EXISTS (SELECT 1 FROM academicyear y WHERE y.id = r.sourceacademicyearid)
                       OR NOT EXISTS (SELECT 1 FROM academicyear y WHERE y.id = r.newacademicyearid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(danglingYears == 0,
                $"campus {campusId} holds {danglingYears} rollover row(s) pointing at an academic year that " +
                "does not exist - the panel renders the source/target year from that id");

            // The undo stamp is a PAIR: a rollback that says it happened but not who did it cannot be
            // attributed, and one attributed to nobody is not a state the endpoint produces.
            var lopsided = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM rolloverauditlog
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND ((isrolledback = TRUE  AND (rolledbackon IS NULL OR rolledbackby IS NULL))
                       OR (isrolledback = FALSE AND (rolledbackon IS NOT NULL OR rolledbackby IS NOT NULL)))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(lopsided == 0,
                $"campus {campusId} holds {lopsided} rollover row(s) whose rollback state and stamp disagree - " +
                "`MarkRolledBack` writes the flag and the stamp together");
        }
    }

    /// <summary>
    /// ⚠️ `ux_tenantsubscription_tenant` IS A UNIQUE INDEX ON `(tenantid)` - ONE row per tenant, ever.
    /// The screen is a form, not a grid, so a second row is not "another subscription": it is a state
    /// the index refuses, and a fixture that tried to write one would fail at the INSERT rather than at
    /// an assertion. This asserts the shape the index permits AND that the row is usable.
    /// </summary>
    private static async Task AssertOneSubscriptionPerTenantAsync(NpgsqlConnection conn)
    {
        var duplicated = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM (
                  SELECT tenantid FROM tenantsubscription GROUP BY tenantid HAVING COUNT(*) > 1) d");

        Assert.True(duplicated == 0,
            $"{duplicated} tenant(s) hold more than one `tenantsubscription` row - " +
            "`ux_tenantsubscription_tenant` permits one, and the tenant settings screen reads a single row");

        var existing = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tenantsubscription WHERE tenantid = @tenantId",
            new { tenantId = SeedCampuses.TenantId });

        Assert.True(existing == 1,
            $"tenant {SeedCampuses.TenantId} holds {existing} subscription row(s) - the subscription screen " +
            "reads one row for the tenant it is signed in as, so a missing row renders an unconfigured plan");

        var unusable = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tenantsubscription
               WHERE renewaldate IS NULL OR status IS NULL OR planname IS NULL OR planname = ''");

        Assert.True(unusable == 0,
            $"{unusable} subscription row(s) carry no plan/renewal/status - the screen renders all three and " +
            "the renewal reminder is computed from the date");
    }

    /// <summary>
    /// ⚠️ A `bankfileexport` LEFT JOINs BOTH its period and its template for the names it renders, and the
    /// template's own scope is what makes it offerable. An export whose template belongs to ANOTHER CAMPUS
    /// renders a bank the campus is not configured with - which is the more dangerous half, because the
    /// file it points at was generated against another campus's wage rules.
    /// </summary>
    private static async Task AssertEveryBankExportResolvesItsOwnCampusesPeriodAndTemplateAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var templates = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM bankfiletemplate
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(templates > 0,
                $"campus {campusId} holds no ACTIVE `bankfiletemplate` row - the WPS/bank-file desk offers " +
                "nothing to generate a file with");

            var unmapped = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM bankfiletemplate
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND (columnmappingjson IS NULL OR columnmappingjson::text IN ('{}', 'null'))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unmapped == 0,
                $"campus {campusId} holds {unmapped} bank template(s) with an empty `columnmappingjson` - a " +
                "template with no mapping generates a file with no columns");

            var exports = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM bankfileexport
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(exports > 0,
                $"campus {campusId} holds no `bankfileexport` row - the desk's own history of generated files " +
                "is empty");

            var dangling = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM bankfileexport e
                   WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
                     AND (NOT EXISTS (SELECT 1 FROM payrollperiod p WHERE p.id = e.payrollperiodid)
                       OR NOT EXISTS (SELECT 1 FROM bankfiletemplate t
                                       WHERE t.id = e.bankfiletemplateid
                                         AND t.tenantid = e.tenantid AND t.schoolid = e.schoolid
                                         AND t.campusid = e.campusid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(dangling == 0,
                $"campus {campusId} holds {dangling} bank export(s) whose period or template does not resolve " +
                "WITHIN the campus - the desk renders the period and bank name from those joins, and an export " +
                "generated against another campus's template is not this campus's file");
        }
    }

    /// <summary>
    /// ⚠️ `HrMeetingRepository.GetAudience` TAKES THE MEETING ID AND DOES NOT FILTER BY SCOPE. So an
    /// audience row whose meeting belongs to another campus is reachable from THAT campus's screen, and
    /// this campus's own dashboard counts it. The subject columns are FREEFORM (no foreign keys at all on
    /// this table), which is exactly why the check has to be here rather than left to the database.
    /// </summary>
    private static async Task AssertEveryAudienceRowBelongsToItsOwnCampusMeetingAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM hrmeetingaudience
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `hrmeetingaudience` row - every meeting's invite list is empty");

            var foreignMeetings = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM hrmeetingaudience a
                   WHERE a.tenantid = @tenantId AND a.schoolid = @schoolId AND a.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM hrmeeting m
                            WHERE m.id = a.meetingid
                              AND m.tenantid = a.tenantid AND m.schoolid = a.schoolid AND m.campusid = a.campusid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(foreignMeetings == 0,
                $"campus {campusId} holds {foreignMeetings} audience row(s) whose meeting belongs to another " +
                "campus - `GetAudience(meetingId)` does not filter by scope, so those rows surface on the " +
                "other campus's screen");

            // ⚠️ A ROW WITH NO SUBJECT AT ALL IS AN EMPTY PANEL. The meeting screen renders a separate
            // list per subject kind, so a row carrying only the scope is invisible everywhere.
            var subjectless = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM hrmeetingaudience
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND classroomid IS NULL AND teacherid IS NULL AND studentid IS NULL AND gradeid IS NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(subjectless == 0,
                $"campus {campusId} holds {subjectless} audience row(s) with no subject of any kind - each of " +
                "the meeting's panels lists one kind, so a row with none is rendered nowhere");

            // Every meeting the campus owns must be reachable, or the panel is empty for that meeting.
            var meetingsWithout = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM hrmeeting m
                   WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM hrmeetingaudience a WHERE a.meetingid = m.id)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(meetingsWithout == 0,
                $"campus {campusId} holds {meetingsWithout} meeting(s) with no audience row at all - opening " +
                "one shows an invite list that is empty for a meeting nobody was invited to");
        }
    }

    /// <summary>
    /// ⚠️ BOTH TABLES ARE KEYED ON `userid` ALONE (no scope columns), so they can only be read through
    /// the user they belong to. `usertwofactor` is that user's PRIMARY KEY, and the password history's
    /// reuse check scans `ORDER BY CreatedOn DESC, Id DESC LIMIT 5` - a set of rows sharing one
    /// timestamp makes that LIMIT pick whatever the engine returns, which is the same ordering trap the
    /// rollover audit logged.
    /// </summary>
    private static async Task AssertTheIdentityTablesBelongToTheCampusAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            // ⚠️⚠️ THE USERS GRID MUST LIST SOMEBODY, OR `identity-user-page` READS GREEN FROM AN EMPTY
            // RESULT. The grid filters `AND u.Status <> 'Disabled'`, and `NULL <> 'Disabled'` is NULL in
            // SQL - so every NULL-status account is EXCLUDED. Measured on `ayra_perf` before this check:
            // all 15 users of the measured campus held NULL, and the spec answered 0 rows for a campus
            // with 15 users. This assertion is what turns that into a red fixture instead of a green spec.
            var listed = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM users u
                   WHERE u.TenantId = @tenantId AND u.SchoolId = @schoolId AND u.CampusId = @campusId
                     AND u.Status <> 'Disabled'",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(listed > 0,
                $"campus {campusId}'s users are ALL excluded by the users grid's own filter " +
                "(`u.Status <> 'Disabled'`, which a NULL status fails) - the screen renders an empty page for " +
                "a campus that has users, and `identity-user-page` would measure nothing while reading OK. " +
                "The perf seeder writes 'Active'; rows written before that fix need the backfill");

            var enrolled = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM usertwofactor tf
                    JOIN users u ON u.id = tf.userid
                   WHERE u.tenantid = @tenantId AND u.schoolid = @schoolId AND u.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(enrolled > 0,
                $"campus {campusId} owns no `usertwofactor` row - the users grid's IsTwoFactorEnabled column " +
                "is FALSE for every row it lists, so the 2FA badge and the reset action never appear");

            var historyRows = await conn.QueryAsync<(long UserId, string PasswordHash, DateTime CreatedOn)>(
                @"SELECT h.userid, h.passwordhash, h.createdon FROM userpasswordhistory h
                    JOIN users u ON u.id = h.userid
                   WHERE u.tenantid = @tenantId AND u.schoolid = @schoolId AND u.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            var list = historyRows.ToList();
            Assert.True(list.Count > 0,
                $"campus {campusId} owns no `userpasswordhistory` row - the reuse check has nothing to scan, " +
                "so a user can set the same password forever");

            // ⚠️ THE HASH FORMAT IS WHAT MAKES `PasswordHasher.VerifyPassword` MEAN ANYTHING. It accepts
            // exactly base64 of 48 bytes (16 salt + 32 hash) and returns FALSE for anything else - so a
            // malformed row would make the reuse check report \"not reused\" for the wrong reason.
            var malformed = list.Count(r =>
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(r.PasswordHash); }
                catch { return true; }
                return bytes.Length != 48;
            });

            Assert.True(malformed == 0,
                $"campus {campusId} owns {malformed} password-history row(s) whose hash is not the app's own " +
                "48-byte base64 form - `VerifyPassword` returns false for those, so reuse protection silently " +
                "stops applying");

            // ⚠️ DISTINCT timestamps, or `LIMIT 5` scans an arbitrary set.
            var distinct = list.Select(r => r.CreatedOn).Distinct().Count();
            Assert.True(distinct == list.Count,
                $"campus {campusId}'s password history holds {list.Count} row(s) across only {distinct} " +
                "timestamp(s) - `IsReuseAsync` scans `ORDER BY CreatedOn DESC LIMIT 5`, and ties make that " +
                "window arbitrary");
        }
    }

    /// <summary>
    /// ⚠️ THE THREE DELIBERATE EMPTIES, ASSERTED RATHER THAN ASSUMED - each for the same reason:
    /// SEEDING A ROW NO QUERY READS is the documented "a seeded row no query can reach" defect, and no
    /// spec could measure it either.
    ///
    ///   * `calendarreminderlog` - a WRITE-ONLY outbox (`CalendarReminderService` is its only writer,
    ///     `Channel = 'Mock'`, and nothing in either server reads it).
    ///   * `teacherparentaction` - no code reference anywhere, and no screen.
    ///   * `curriculumtopiclearningmaterial` - a repository with NO CALLER. Its only other appearance is
    ///     `AttachmentFileRepository`'s orphan sweep, which EXCLUDES its file ids: the app knows the
    ///     table's files exist while nothing ever lists them. `CurriculumTopicPlanDto.LearningMaterials`
    ///     is a property no repository populates, so the screen's "Learning Materials" panel is empty
    ///     because nothing fills it - an APP gap that seeding would hide behind plausible rows.
    ///
    /// This does NOT fail if a future implementation starts reading them - it FAILS IF THIS FIXTURE HAS
    /// STARTED SEEDING THEM, because that would mean rows nothing can read. A reader lands with its own
    /// fixture, and then this check moves to that fixture.
    /// </summary>
    private static async Task AssertTheWriteOnlyTablesAreStillEmptyAsync(NpgsqlConnection conn)
    {
        var reminders = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM calendarreminderlog");
        Assert.True(reminders == 0,
            $"`calendarreminderlog` holds {reminders} row(s) - it is a WRITE-ONLY outbox (no reader in either " +
            "server, `Channel = 'Mock'`), so any row in it is unreachable. When a real channel adds a reader, " +
            "the seed and its spec belong with it - not before");

        var actions = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM teacherparentaction");
        Assert.True(actions == 0,
            $"`teacherparentaction` holds {actions} row(s) - the table has NO code reference anywhere in either " +
            "server and no screen, so seeding it would be decoration that no query can reach");

        var materials = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM curriculumtopiclearningmaterial");
        Assert.True(materials == 0,
            $"`curriculumtopiclearningmaterial` holds {materials} row(s) - `GetByTopic` has NO CALLER (measured " +
            "by grep outside its own repository) and `CurriculumTopicPlanDto.LearningMaterials` is a property " +
            "nothing populates, so a row here is data no endpoint can list. The screen's panel is empty for an " +
            "APP reason; seeding rows would hide that");
    }

    /// <summary>
    /// ⚠️ `-infinity` IS Npgsql's ENCODING OF `DateTime.MinValue` (0001-01-01) and it SORTS BEFORE every
    /// real timestamp, so `ORDER BY CreatedOn DESC` TIES across those rows and "the newest row" becomes
    /// whatever the engine returns. The GenericRepository-wide `EnsureTimestampsSet` fix covers writes
    /// that go through it; a SEEDER writes its own SQL, so it is the seeder that owes this.
    /// </summary>
    private static async Task AssertNoSentinelTimestampsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        var checks = new (string Table, string Predicate)[]
        {
            ("reportview", "createdon"),
            ("reportrunlog", "ranon"),
            ("postingbatch", "startedon"),
            ("rolloverauditlog", "createdon"),
            ("bankfiletemplate", "createdon"),
            ("bankfileexport", "createdon"),
            ("hrmeetingaudience", "createdon"),
        };

        foreach (var (table, column) in checks)
        {
            var sentinels = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {table} t
                    WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                      AND (t.{column} IS NULL OR t.{column} = '-infinity'::timestamp OR t.{column} = 'infinity'::timestamp)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId = campusIds[0] });

            Assert.True(sentinels == 0,
                $"{table}.{column} holds {sentinels} sentinel/NULL timestamp(s) - `-infinity` sorts BEFORE " +
                "every real timestamp, so `ORDER BY` on it ties and the newest row becomes arbitrary");
        }

        var historySentinels = await conn.ExecuteScalarAsync<long>(
            $@"SELECT COUNT(*) FROM userpasswordhistory h
                 JOIN users u ON u.id = h.userid
                WHERE u.tenantid = @tenantId AND u.schoolid = @schoolId
                  AND (h.createdon IS NULL OR h.createdon = '-infinity'::timestamp)",
            new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId });

        Assert.True(historySentinels == 0,
            $"userpasswordhistory holds {historySentinels} sentinel timestamp(s) - the reuse window is a " +
            "`LIMIT` over that ordering, so ties make it arbitrary");
    }
}
