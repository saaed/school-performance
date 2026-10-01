using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="JobQueueSeeder"/>.</summary>
public sealed class JobQueueSeedOptions
{
    /// <summary>
    /// Finished jobs per campus - the queue's HISTORY.
    ///
    /// ⚠️ THIS IS THE BULK OF A REAL QUEUE AND IT IS ALSO THE PART NOTHING PRUNES. `backgroundjob` has
    /// no retention job anywhere in the API (`grep -rni "delete from backgroundjob"` over
    /// `SchoolResourceServer/` returns nothing), so this set only ever grows. 400 is roughly three years
    /// of a campus's own scheduled work: one billing job a month, the late-fee run, and the ad-hoc /
    /// event / export jobs the modules enqueue as people use them - about 0.4 jobs a day, NOT a busy
    /// system.
    /// </summary>
    public int CompletedPerCampus { get; set; } = 400;

    /// <summary>
    /// Queued, DUE NOW - what a sweep has just enqueued and the worker has not drained yet. A handful,
    /// because the worker runs every minute and `/jobs/billing` queues one job per campus.
    /// </summary>
    public int PendingDuePerCampus { get; set; } = 5;

    /// <summary>
    /// Queued but NOT YET DUE - the calendar reminders.
    ///
    /// ⚠️ THIS SET IS THE WHOLE POINT OF THE FIXTURE, and it is real rather than padding:
    /// `CalendarController.EnqueueReminder` writes one `CalendarReminder` job per calendar item that
    /// carries a reminder, with `RunAfter = start - reminderMinutesBefore` (a time in the FUTURE), and
    /// the row sits at `Status = 1` until then. So a school that has filled its calendar for the year
    /// holds hundreds of not-yet-due queued rows, and
    /// `BackgroundJobRepository.ClaimNextAsync` must walk past EVERY one of them before it reaches a
    /// job it can run. That walk is the claim's entire cost, and a spec gated on the queued count SKIPS
    /// without it.
    /// </summary>
    public int FutureQueuedPerCampus { get; set; } = 480;

    /// <summary>
    /// `Status = 2` rows holding a LIVE lease - what the worker is running right now. Two, so the
    /// claim's `LeaseUntil < @Now` half has rows on both sides: a lease that has NOT expired must be
    /// skipped, and one that has expired must be claimable again after a crash.
    /// </summary>
    public int ProcessingPerCampus { get; set; } = 2;

    /// <summary>
    /// `Status = 4` rows that exhausted `JobService.MaxRetry` (3). A queue with no failures is a queue
    /// nobody reads - `errormessage` is the field the retry path writes and the UI shows.
    /// </summary>
    public int FailedPerCampus { get; set; } = 8;

    /// <summary>Re-seed even when this seeder's stamped rows are already present.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's job-queue seed produced.</summary>
public sealed class JobQueueSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int Completed { get; set; }
    public int PendingDue { get; set; }
    public int FutureQueued { get; set; }
    public int Processing { get; set; }
    public int Failed { get; set; }

    /// <summary>Every `Status = 1` row this seeder wrote - the set the claim's candidate scan walks.</summary>
    public int QueuedTotal => PendingDue + FutureQueued;

    /// <summary>Rows this seeder wrote (`Status = 1`'s share of the whole stamped set).</summary>
    public int Total => Completed + PendingDue + FutureQueued + Processing + Failed;
}

/// <summary>
/// Fills `backgroundjob` to a REALISTIC shape, so the `job-*` specs measure the queue's hot path
/// instead of reporting SKIP against a queue that happens to be drained.
///
/// WHY THIS EXISTS
/// ---------------
/// `ayra_perf` holds SEVEN `backgroundjob` rows and every one of them is `Status = 3` (Succeeded) -
/// the queue is empty because the perf dataset never enqueued anything and the worker never ran.
/// `job-queue-claim` gates on the number of QUEUED rows (that is what its candidate scan walks), so it
/// reported `SKIP (scope holds 0 rows < 1)`: "not measured yet" for the one statement the application
/// executes every sixty seconds for the life of the install. A spec over an empty table is worse than
/// no spec, so the data is the unlock here - not another spec.
///
/// WHAT IS HONEST ABOUT THESE NUMBERS
/// ----------------------------------
/// Nothing here is padded to satisfy a threshold. The queue's size is `history x time`, and the three
/// sets that matter have three different reasons for their size:
///   * COMPLETED grows without bound because nothing prunes, and 400 is three years of a campus's own
///     monthly / ad-hoc work (see <see cref="JobQueueSeedOptions.CompletedPerCampus"/>);
///   * FUTURE-QUEUED is the calendar's own reminders, one row per reminder, written up to a year ahead
///     (`CalendarController.EnqueueReminder`), which is what a school that uses the calendar holds;
///   * DUE is small on purpose - the worker drains it every minute.
/// A real deployment's claim therefore scans the FUTURE set and runs one row from the DUE set, which is
/// exactly the shape seeded here.
///
/// ⚠️ THE COLUMN FRAMES ARE NOT UNIFORM, AND THAT IS THE APPLICATION'S OWN MIXING - REPRODUCED, NOT
/// INVENTED. `createdon` is written by SQL (`BackgroundJobRepository.Insert` uses `NOW()`), so it is
/// the DATABASE'S LOCAL time, while `leaseuntil` / `startedon` / `completedon` are written from C#
/// (`DateTime.UtcNow` in `ClaimNextAsync` and `JobService`) and are therefore UTC in the SAME
/// `timestamp without time zone` columns. `ClaimNextAsync` compares `RunAfter <= @Now` where `@Now` is
/// `DateTime.UtcNow`, so `runafter` has to be written in the UTC frame for the fixture to mean
/// anything: a `RunAfter` written from `now()` (local, UTC+4 here) would sit four hours in the
/// future and the "due" rows would never be claimable. `createdon` stays local so the date spread
/// matches what the app's own inserts produce.
///
/// ⚠️ THE CLEAR IS STAMPED, NEVER SCOPED. `ClearTableAsync` would `DELETE ... WHERE campusid = @campusId`
/// and `backgroundjob` is a table the APPLICATION writes at this scope - campus 15 holds three real
/// `ReportExport` jobs of its own. A scoped delete here would destroy rows this seeder neither wrote
/// nor owns, so the clear is `payload LIKE 'PERF-JOB %'` AND the scope, and every read-back counts the
/// same stamp.
/// </summary>
public sealed class JobQueueSeeder : BaseSeeder
{
    public JobQueueSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a load here invalidates, so a fixture can ANALYZE them.
    ///
    /// ⚠️ ONE TABLE, AND THAT IS CORRECT RATHER THAN LAZY: the claim reads nothing else. `backgroundjob`
    /// is not joined by any of these statements, and the campus sweep's `campus` table is not touched
    /// here at all.
    /// </summary>
    public static readonly string[] TablesToAnalyze = { "backgroundjob" };

    /// <summary>
    /// The prefix every row this seeder writes carries in `payload`. It is the ONLY handle that
    /// separates these rows from the application's own, and both the clear and the read-back use it.
    /// </summary>
    public const string Stamp = "PERF-JOB";

    /// <summary>
    /// `BackgroundJobStatus`: Queued 1 / Processing 2 / Succeeded 3 / Failed 4. Spelled as literals
    /// because this project does not reference the API.
    /// </summary>
    private const short StatusQueued = 1;
    private const short StatusProcessing = 2;
    private const short StatusSucceeded = 3;
    private const short StatusFailed = 4;

    /// <summary>
    /// `JobService.MaxRetry`. A failed row at this count is terminal, which is what the grid's
    /// error column is read for.
    /// </summary>
    private const short MaxRetry = 3;

    /// <summary>
    /// The job types, in the rotation the buckets use. Spelled as literals for the same reason as the
    /// statuses; they are the values `BackgroundJobType` declares and `JobService.Dispatch` switches on,
    /// so a row here is a row the worker has a handler for.
    /// </summary>
    private static readonly string[] JobTypes =
    {
        "RegularFeeInvoice", "LateFeeRun", "CalendarReminder",
        "ReportExport", "AdHocInvoice", "EventInvoice", "AcademicYearRollover",
    };

    public async Task<JobQueueSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId,
        JobQueueSeedOptions options, bool verbose = false)
    {
        var result = new JobQueueSeedResult();
        await using var conn = await OpenConnectionAsync();

        var existing = await ReadStampedCountAsync(conn, tenantId, schoolId, campusId);
        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} already holds {existing} stamped job row(s) - pass Force to re-seed";
            // ⚠️ THE SKIP PATH STILL READS, so the numbers a fixture prints describe what is really
            // there rather than five zeroes that look like a failed seed.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose) Console.WriteLine($"  Job queue: {result.SkipReason}");
            return result;
        }

        // The acting user, so the rows look like the application's rather than a fixture's. Falls back
        // to the system user id the repo's other seeders use.
        //
        // ⚠️ `users` HAS NO `isactive` COLUMN - it is `status` (the account lifecycle: Active/Pending/
        // Disabled, see `AccountStatusPolicy`), so an active-filter here is a `42703`, not a
        // narrowed query. Any user at this scope is a fine actor for a `createdby` stamp.
        var createdBy = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM users
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId }) ?? 1;

        if (options.Force && existing > 0)
        {
            // STAMPED *and* scoped - see the class comment. The scope alone would delete the campus's
            // own real jobs.
            await conn.ExecuteAsync(
                @"DELETE FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND payload LIKE @stamp",
                new { tenantId, schoolId, campusId, stamp = Stamp + " %" });
        }

        var args = new
        {
            tenantId,
            schoolId,
            campusId,
            createdBy,
            // The BARE prefix: the inserts build the marker as `@stamp || ' completed #' || i`, and the
            // read-backs match `@stamp + " %"`. Two spellings, one constant.
            stamp = Stamp,
            completed = options.CompletedPerCampus,
            due = options.PendingDuePerCampus,
            future = options.FutureQueuedPerCampus,
            processing = options.ProcessingPerCampus,
            failed = options.FailedPerCampus,
        };

        // ------------------------------------------------------------------
        // 1. COMPLETED - the history. `createdon` spreads backwards about three years
        //    (`CompletedPerCampus x 65 hours`), one bucket at a time.
        // ------------------------------------------------------------------
        if (options.CompletedPerCampus > 0)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO backgroundjob
                    (tenantid, schoolid, campusid, jobtype, entityid, status, payload, progress,
                     errormessage, retrycount, leaseuntil, startedon, completedon,
                     createdby, modifiedby, createdon, modifiedon, runafter)
                  SELECT @tenantId, @schoolId, @campusId,
                         (ARRAY['RegularFeeInvoice','LateFeeRun','CalendarReminder','ReportExport',
                                'AdHocInvoice','EventInvoice','AcademicYearRollover'])[1 + (i % 7)],
                         0, 3,
                         @stamp || ' completed #' || i || ' - ' ||
                            (ARRAY['Campus billing run','Scheduled global late-fee run','Reminder delivered',
                                   'Export ready','Ad-hoc charge invoiced','Event charges invoiced',
                                   'Academic year rolled over'])[1 + (i % 7)],
                         100, NULL, 0, NULL,
                         now() - interval '2 minutes', now() - interval '1 minute',
                         @createdBy, @createdBy,
                         now() - ((i + 1) * interval '65 hours'),
                         now() - ((i + 1) * interval '65 hours'),
                         NULL
                    FROM generate_series(0, @completed - 1) AS i",
                args);
        }

        // ------------------------------------------------------------------
        // 2. QUEUED AND DUE - what the claim is supposed to hand the worker. `createdon` is minutes
        //    ago (a sweep has just enqueued them) and `runafter` is NULL, which is the "no delay"
        //    an immediate sweep writes.
        // ------------------------------------------------------------------
        if (options.PendingDuePerCampus > 0)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO backgroundjob
                    (tenantid, schoolid, campusid, jobtype, entityid, status, payload, progress,
                     errormessage, retrycount, leaseuntil, startedon, completedon,
                     createdby, modifiedby, createdon, modifiedon, runafter)
                  SELECT @tenantId, @schoolId, @campusId,
                         (ARRAY['RegularFeeInvoice','LateFeeRun','AdHocInvoice'])[1 + (i % 3)],
                         0, 1,
                         @stamp || ' due #' || i || ' - waiting for the worker',
                         0, NULL, 0, NULL, NULL, NULL,
                         @createdBy, @createdBy,
                         now() - ((i + 1) * interval '3 minutes'),
                         now() - ((i + 1) * interval '3 minutes'),
                         NULL
                    FROM generate_series(0, @due - 1) AS i",
                args);
        }

        // ------------------------------------------------------------------
        // 3. QUEUED BUT NOT YET DUE - the calendar reminders, spread over the coming year.
        //
        //    ⚠️ `runafter` IS WRITTEN IN THE UTC FRAME because that is the frame `ClaimNextAsync`
        //    compares in (`@Now = DateTime.UtcNow`). Writing it from `now()` would put every row four
        //    hours further out than intended on this host and the "due" bucket would stop being the
        //    only claimable set - the fixture asserts exactly that, so it would fail rather than
        //    quietly measure nothing.
        // ------------------------------------------------------------------
        if (options.FutureQueuedPerCampus > 0)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO backgroundjob
                    (tenantid, schoolid, campusid, jobtype, entityid, status, payload, progress,
                     errormessage, retrycount, leaseuntil, startedon, completedon,
                     createdby, modifiedby, createdon, modifiedon, runafter)
                  SELECT @tenantId, @schoolId, @campusId,
                         'CalendarReminder', 0, 1,
                         @stamp || ' future reminder #' || i || ' - scheduled',
                         0, NULL, 0, NULL, NULL, NULL,
                         @createdBy, @createdBy,
                         now() - ((i + 1) * interval '1 hour'),
                         now() - ((i + 1) * interval '1 hour'),
                         (now() AT TIME ZONE 'utc') + ((i + 1) * interval '18 hours')
                    FROM generate_series(0, @future - 1) AS i",
                args);
        }

        // ------------------------------------------------------------------
        // 4. PROCESSING - a live lease. The claim must NOT take these, and after the lease expires it
        //    must take them again (that is what makes a crashed worker recoverable).
        // ------------------------------------------------------------------
        if (options.ProcessingPerCampus > 0)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO backgroundjob
                    (tenantid, schoolid, campusid, jobtype, entityid, status, payload, progress,
                     errormessage, retrycount, leaseuntil, startedon, completedon,
                     createdby, modifiedby, createdon, modifiedon, runafter)
                  SELECT @tenantId, @schoolId, @campusId,
                         'RegularFeeInvoice', 0, 2,
                         @stamp || ' in flight #' || i || ' - leased by a worker',
                         40, NULL, 0,
                         (now() AT TIME ZONE 'utc') + interval '8 minutes',
                         (now() AT TIME ZONE 'utc') - interval '2 minutes',
                         NULL, @createdBy, @createdBy,
                         now() - ((i + 1) * interval '5 minutes'),
                         now() - ((i + 1) * interval '5 minutes'),
                         NULL
                    FROM generate_series(0, @processing - 1) AS i",
                args);
        }

        // ------------------------------------------------------------------
        // 5. FAILED - retries exhausted. `errormessage` is what `JobService.RunNext` records and the
        //    status UI renders, so a queue without one cannot show that branch.
        // ------------------------------------------------------------------
        if (options.FailedPerCampus > 0)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO backgroundjob
                    (tenantid, schoolid, campusid, jobtype, entityid, status, payload, progress,
                     errormessage, retrycount, leaseuntil, startedon, completedon,
                     createdby, modifiedby, createdon, modifiedon, runafter)
                  SELECT @tenantId, @schoolId, @campusId,
                         (ARRAY['RegularFeeInvoice','EventInvoice','AdHocInvoice'])[1 + (i % 3)],
                         0, 4,
                         @stamp || ' failed #' || i || ' - retries exhausted',
                         0,
                         @stamp || ' failed after ' || @maxRetry || ' attempts',
                         @maxRetry, NULL,
                         (now() AT TIME ZONE 'utc') - ((i + 1) * interval '20 minutes'),
                         (now() AT TIME ZONE 'utc') - ((i + 1) * interval '18 minutes'),
                         @createdBy, @createdBy,
                         now() - ((i + 1) * interval '7 hours'),
                         now() - ((i + 1) * interval '7 hours'),
                         NULL
                    FROM generate_series(0, @failed - 1) AS i",
                new
                {
                    args.tenantId, args.schoolId, args.campusId, args.createdBy,
                    stamp = Stamp,
                    args.failed, maxRetry = MaxRetry,
                });
        }

        await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
        if (verbose)
            Console.WriteLine(
                $"  Job queue: {result.Completed} completed, {result.PendingDue} due, " +
                $"{result.FutureQueued} future, {result.Processing} in flight, {result.Failed} failed");

        return result;
    }

    /// <summary>The rows THIS seeder owns at one campus - the stamp, never the scope alone.</summary>
    private static Task<long> ReadStampedCountAsync(NpgsqlConnection conn, long tenantId, long schoolId, long campusId)
        => conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM backgroundjob
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND payload LIKE @stamp",
            new { tenantId, schoolId, campusId, stamp = Stamp + " %" });

    /// <summary>
    /// Reads the five buckets back by their own payload marker, so a fixture's printed numbers are the
    /// ROWS rather than the options it asked for.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, JobQueueSeedResult result)
    {
        var counts = await conn.QuerySingleAsync<(long Completed, long Due, long Future, long Processing, long Failed)>(
            @"SELECT
                  COUNT(*) FILTER (WHERE status = 3 AND payload LIKE @completed) AS Completed,
                  COUNT(*) FILTER (WHERE status = 1 AND payload LIKE @due)       AS Due,
                  COUNT(*) FILTER (WHERE status = 1 AND payload LIKE @future)    AS Future,
                  COUNT(*) FILTER (WHERE status = 2 AND payload LIKE @processing) AS Processing,
                  COUNT(*) FILTER (WHERE status = 4 AND payload LIKE @failed)    AS Failed
                FROM backgroundjob
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND payload LIKE @stamp",
            new
            {
                tenantId, schoolId, campusId,
                stamp = Stamp + " %",
                completed = Stamp + " completed %",
                due = Stamp + " due %",
                future = Stamp + " future reminder %",
                processing = Stamp + " in flight %",
                failed = Stamp + " failed %",
            });

        result.Completed = (int)counts.Completed;
        result.PendingDue = (int)counts.Due;
        result.FutureQueued = (int)counts.Future;
        result.Processing = (int)counts.Processing;
        result.Failed = (int)counts.Failed;
    }
}
