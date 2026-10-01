using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Fills `backgroundjob` to a realistic shape and then asserts the ONE thing that decides whether the
/// `job-*` specs measure anything: that the claim's candidate scan has a queue with the right rows in
/// the right states.
///
/// ⚠️ WHY THE FIXTURE EXISTS AT ALL: `ayra_perf` held SEVEN `backgroundjob` rows, all `Status = 3`
/// (Succeeded). `job-queue-claim` gates on the QUEUED count - the set its scan walks - so it reported
/// `SKIP (scope holds 0 rows &lt; 1)`, i.e. "not measured yet" for the statement the application runs
/// every sixty seconds. A spec over an empty table is worse than no spec, so this is the data pass that
/// comes first.
///
/// ⚠️ AND THE ASSERTIONS ARE THE CLAIM'S OWN PREDICATE, NOT COUNTS. A count proves the INSERT ran; it
/// cannot see a row the claim would skip. The predicate asserted here is transcribed from
/// `BackgroundJobRepository.ClaimNextAsync`:
///
///     Status = 1
///       AND (LeaseUntil IS NULL OR LeaseUntil &lt; @Now)
///       AND (RunAfter IS NULL OR RunAfter &lt;= @Now)
///     ORDER BY CreatedOn ASC
///     LIMIT 1
///
/// The three halves that matter, each of which a wrong fixture would break silently:
///
///   * the DUE rows ARE claimable - otherwise the fixture seeds a queue the worker can never drain;
///   * the FUTURE rows are NOT claimable - **this is the assertion that catches the timestamp frame**.
///     `runafter` is compared against `DateTime.UtcNow`, while `now()` in this database is LOCAL
///     (UTC+4 here); a fixture that wrote the reminders from `now()` would leave every one of them
///     four hours further out than intended and would still pass a COUNT. Asserting the PREDICATE is
///     what makes the frame a testable property instead of a comment;
///   * a LIVE lease is not claimable, and an EXPIRED one is - the pair that lets a crashed worker
///     recover without two workers ever running one job.
///
/// ⚠️ IT IS ALSO NOT A BULK LOAD FOR ITS OWN SAKE. The queue's size is `history x time`: 400 finished
/// jobs is about three years of one campus's monthly billing plus the ad-hoc/event/export jobs the
/// modules enqueue, and 480 not-yet-due rows is the calendar's own reminders - one per reminder,
/// written up to a year ahead by `CalendarController.EnqueueReminder`. The DUE set is small because the
/// worker drains it every minute.
///
/// ⚠️ NO PLAN ASSERTION, ON PURPOSE. At ~900 rows the planner will SEQUENTIAL-SCAN `backgroundjob`
/// rather than use `ix_backgroundjob_status_lease`: a 900-row table is cheaper to read whole than to
/// index-scan and sort. That is the correct plan for this size and the index is still the right one -
/// the index question was answered separately at 320,207 rows, where the shipped claim read 5.269 ms
/// while the obvious `(status, runafter, createdon)` candidate read 13.700 ms (2.6x WORSE) and was not
/// even chosen. Pinning a plan here would assert a plan the application does not run at this size.
///
/// Opt in with the same flags the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~JobQueueDataset"
/// </summary>
public sealed class JobQueueDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public JobQueueDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Job_queue_dataset_gives_the_claim_a_realistic_queue_in_every_state()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the job-queue dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed into - run PerfDatasetTests first");

        var options = new JobQueueSeedOptions
        {
            CompletedPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_JOBS_COMPLETED", 400),
            PendingDuePerCampus = SeedCampuses.EnvInt("SCUBE_PERF_JOBS_DUE", 5),
            FutureQueuedPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_JOBS_FUTURE", 480),
            ProcessingPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_JOBS_PROCESSING", 2),
            FailedPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_JOBS_FAILED", 8),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding the JOB QUEUE for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.CompletedPerCampus} completed, " +
                          $"{options.PendingDuePerCampus} due, {options.FutureQueuedPerCampus} future, " +
                          $"{options.ProcessingPerCampus} in flight, {options.FailedPerCampus} failed");
        _output.WriteLine("");

        var seeder = new JobQueueSeeder(_connectionString);
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                // ⚠️ A SKIPPED CAMPUS IS STILL ASSERTED WHEN IT ALREADY CARRIES THIS SEEDER'S ROWS, so a
                // re-run proves the same things instead of printing five zeroes.
                if (result.Total > 0) seededCampusIds.Add(campusId);
                continue;
            }

            _output.WriteLine(
                $"  campus {campusId,-5} completed {result.Completed,4} due {result.PendingDue,3} " +
                $"future {result.FutureQueued,4} in flight {result.Processing,2} failed {result.Failed,3}");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        Assert.True(seededCampusIds.Count > 0,
            "no campus holds this seeder's `backgroundjob` rows, so `job-queue-claim` would still " +
            "report SKIP against an empty queue");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. The table held seven rows, so the planner's statistics
        // described an empty queue - and a performance reading on un-analyzed statistics is not a
        // reading.
        foreach (var table in JobQueueSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertTheClaimableSetIsExactlyTheDueRowsAsync(conn, seededCampusIds);
        await AssertTheFutureRemindersAreNotClaimableYetAsync(conn, seededCampusIds);
        await AssertALiveLeaseIsNotClaimableButAnExpiredOneIsAsync(conn, seededCampusIds);
        await AssertTheClaimPicksTheOldestClaimableRowAsync(conn, seededCampusIds);
        await AssertEveryFinishedAndFailedRowCarriesItsOwnEndStateAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// THE CLAIM'S PREDICATE, over this seeder's own rows, is exactly the DUE bucket - and nothing else
    /// this seeder wrote is inside it.
    ///
    /// ⚠️ THE PREDICATE IS TRANSCRIBED FROM `ClaimNextAsync` RATHER THAN APPROXIMATED. `(LeaseUntil IS
    /// NULL OR LeaseUntil &lt; @Now)` and `(RunAfter IS NULL OR RunAfter &lt;= @Now)` are the two halves
    /// that decide claimability, and the `IS NULL` guard on each is what makes them a disjunction rather
    /// than a range - dropping either one here would assert a query the application does not issue.
    /// `@Now` is spelled as `(now() AT TIME ZONE 'utc')` because the repository binds `DateTime.UtcNow`.
    /// </summary>
    private async Task AssertTheClaimableSetIsExactlyTheDueRowsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var dueRows = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status = 1 AND payload LIKE @due",
                P(campusId));

            Assert.True(dueRows > 0,
                $"campus {campusId} holds no DUE queued job, so the claim's scan has nothing it may run - " +
                "the fixture seeded a queue the worker can never drain");

            var claimableDue = await conn.ExecuteScalarAsync<long>(
                ClaimPredicate + " AND payload LIKE @due", P(campusId));
            Assert.Equal(dueRows, claimableDue);

            // THE INVARIANT: everything this seeder wrote that the claim may take is a due row. This is
            // the assertion that fails when a bucket is added without a state that excludes it - the
            // future and in-flight buckets are asserted individually below, and this covers the rest.
            var claimableStamped = await conn.ExecuteScalarAsync<long>(
                ClaimPredicate + " AND payload LIKE @stamp", P(campusId));
            Assert.Equal(claimableDue, claimableStamped);
        }
    }

    /// <summary>
    /// ⚠️ THE ASSERTION THAT MAKES THE TIMESTAMP FRAME TESTABLE. Every `future reminder` row is written
    /// with `runafter` in the UTC frame; if it were written from `now()` it would sit four hours further
    /// out on this host and the claim would skip it anyway - so a COUNT would pass while the fixture
    /// quietly stopped exercising the scan. Asserting that the claim's own predicate REJECTS all of them
    /// is what turns that into a failure.
    ///
    /// It also asserts the rows are genuinely QUEUED (`Status = 1`), because a reminder in any other
    /// state is not part of the set the candidate scan walks.
    /// </summary>
    private async Task AssertTheFutureRemindersAreNotClaimableYetAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var notYetDue = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status = 1 AND runafter IS NOT NULL
                     AND runafter > (now() AT TIME ZONE 'utc')
                     AND payload LIKE @future",
                P(campusId));

            Assert.True(notYetDue > 0,
                $"campus {campusId} holds no NOT-YET-DUE queued job, so the claim's candidate scan walks " +
                "past nothing and the spec measures a queue one pass could drain. On this campus every " +
                "reminder already came due - check the `runafter` frame in JobQueueSeeder: it must be " +
                "UTC, because that is the frame ClaimNextAsync compares in");

            var claimableFuture = await conn.ExecuteScalarAsync<long>(
                ClaimPredicate + " AND payload LIKE @future", P(campusId));
            Assert.Equal(0L, claimableFuture);
        }
    }

    /// <summary>
    /// The lease half of the guard, isolated from the status half.
    ///
    /// ⚠️ A `Status = 2` row is skipped for TWO independent reasons - its status is not 1, and its lease
    /// has not expired - so a predicate that requires `Status = 1` proves nothing about the lease. The
    /// two statements below DROP the status condition and vary only the lease comparison, which is what
    /// makes this an assertion about `LeaseUntil`.
    /// </summary>
    private async Task AssertALiveLeaseIsNotClaimableButAnExpiredOneIsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var inFlight = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status = 2 AND leaseuntil IS NOT NULL
                     AND payload LIKE @processing",
                P(campusId));

            Assert.True(inFlight > 0,
                $"campus {campusId} holds no PROCESSING job, so the claim's `LeaseUntil < @Now` half has " +
                "no row it must skip");

            var withLiveLease = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND payload LIKE @processing
                     AND (leaseuntil IS NULL OR leaseuntil < (now() AT TIME ZONE 'utc'))",
                P(campusId));
            Assert.Equal(0L, withLiveLease);

            var withExpiredLease = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND payload LIKE @processing
                     AND (leaseuntil IS NULL OR leaseuntil < (now() AT TIME ZONE 'utc') + interval '30 minutes')",
                P(campusId));
            Assert.Equal(inFlight, withExpiredLease);
        }
    }

    /// <summary>
    /// `ORDER BY CreatedOn ASC LIMIT 1` must land inside the claimable set, and it must be the OLDEST
    /// such row. This is the ordering half of the claim - without it the fixture could satisfy every
    /// state assertion above and still describe a queue whose scan returns nothing at all.
    /// </summary>
    private async Task AssertTheClaimPicksTheOldestClaimableRowAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var picked = await conn.QueryFirstOrDefaultAsync<string>(
                @"SELECT payload FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status = 1
                     AND (leaseuntil IS NULL OR leaseuntil < (now() AT TIME ZONE 'utc'))
                     AND (runafter IS NULL OR runafter <= (now() AT TIME ZONE 'utc'))
                   ORDER BY createdon ASC
                   LIMIT 1",
                P(campusId));

            Assert.False(string.IsNullOrWhiteSpace(picked),
                $"campus {campusId}: the claim's ordered candidate scan returned NO row, so the worker " +
                "idles on a campus that holds queued work");

            Assert.Contains(JobQueueSeeder.Stamp + " due ", picked);

            // And it is the OLDEST one, with an explicit tie-break so the expectation is a total order
            // rather than however the heap happens to order two equal `createdon` values.
            var oldest = await conn.QueryFirstOrDefaultAsync<string>(
                @"SELECT payload FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status = 1
                     AND (leaseuntil IS NULL OR leaseuntil < (now() AT TIME ZONE 'utc'))
                     AND (runafter IS NULL OR runafter <= (now() AT TIME ZONE 'utc'))
                   ORDER BY createdon ASC, id ASC
                   LIMIT 1",
                P(campusId));

            Assert.Equal(oldest, picked);
        }
    }

    /// <summary>
    /// The end states: a finished row carries its stamps and NO lease, and a failed row carries the
    /// message and the retry count the status UI renders. Both are fields a spec's `SELECT *` reads, so
    /// a row missing them is a row that would render an empty branch.
    /// </summary>
    private async Task AssertEveryFinishedAndFailedRowCarriesItsOwnEndStateAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var finishedWithoutStamps = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND payload LIKE @completed
                     AND (startedon IS NULL OR completedon IS NULL OR leaseuntil IS NOT NULL)",
                P(campusId));
            Assert.Equal(0L, finishedWithoutStamps);

            var failedWithoutMessage = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND payload LIKE @failed
                     AND (errormessage IS NULL OR errormessage = '' OR retrycount < 3)",
                P(campusId));
            Assert.Equal(0L, failedWithoutMessage);

            // And nothing this seeder wrote carries a state the enum does not declare - a `Status = 5`
            // would be a row the worker's `Status = 1` scan can never see and the UI cannot label.
            var unknownStatus = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND payload LIKE @stamp AND status NOT IN (1, 2, 3, 4)",
                P(campusId));
            Assert.Equal(0L, unknownStatus);
        }
    }

    /// <summary>
    /// `ClaimNextAsync`'s predicate over the queue, spelled the way the repository spells it.
    /// `@Now` is `(now() AT TIME ZONE 'utc')` because the repository binds `DateTime.UtcNow`.
    /// </summary>
    private const string ClaimPredicate =
        @"SELECT COUNT(*) FROM backgroundjob
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
             AND status = 1
             AND (leaseuntil IS NULL OR leaseuntil < (now() AT TIME ZONE 'utc'))
             AND (runafter IS NULL OR runafter <= (now() AT TIME ZONE 'utc'))";

    /// <summary>
    /// One parameter bag for every statement in this fixture. Dapper passes all of them and PostgreSQL
    /// ignores the unused ones, which keeps the stamp spellings in ONE place - a bucket's marker written
    /// twice is how a fixture stops counting the rows it seeded.
    /// </summary>
    private static object P(long campusId) => new
    {
        tenantId = SeedCampuses.TenantId,
        schoolId = SeedCampuses.SchoolId,
        campusId,
        stamp = JobQueueSeeder.Stamp + " %",
        due = JobQueueSeeder.Stamp + " due %",
        future = JobQueueSeeder.Stamp + " future reminder %",
        completed = JobQueueSeeder.Stamp + " completed %",
        failed = JobQueueSeeder.Stamp + " failed %",
        processing = JobQueueSeeder.Stamp + " in flight %",
    };
}
