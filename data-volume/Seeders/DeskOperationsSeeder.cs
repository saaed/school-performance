using Dapper;
using Npgsql;
using System.Security.Cryptography;
using System.Text.Json;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="DeskOperationsSeeder"/>.</summary>
public sealed class DeskOperationsSeedOptions
{
    /// <summary>Saved views written against the campus's report definition.</summary>
    public int SavedViews { get; set; } = 3;

    /// <summary>
    /// Report run-log rows. This is the batch's page-heavy table: the run history is read
    /// per definition with DataTables paging, so it needs enough rows that a page is a page.
    /// </summary>
    public int RunLogRows { get; set; } = 60;

    /// <summary>Completed exports, one per format.</summary>
    public int Exports { get; set; } = 3;

    /// <summary>Accounting posting batches - one a month for two years.</summary>
    public int PostingBatches { get; set; } = 24;

    /// <summary>Rollover audit rows (the campus's year-end history).</summary>
    public int RolloverLogs { get; set; } = 6;

    /// <summary>Bank file templates.</summary>
    public int BankTemplates { get; set; } = 3;

    /// <summary>Audience rows per meeting.</summary>
    public int AudiencePerMeeting { get; set; } = 5;

    /// <summary>Password-history rows for the enrolled user (the reuse check scans the last five).</summary>
    public int PasswordHistoryRows { get; set; } = 6;

    /// <summary>Re-seed even when the campus already holds saved report views.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's desk/operations seed produced.</summary>
public sealed class DeskOperationsSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int SavedViews { get; set; }
    public int RunLogRows { get; set; }
    public int Exports { get; set; }
    public int PostingBatches { get; set; }
    public int RolloverLogs { get; set; }
    public int Subscriptions { get; set; }
    public int BankTemplates { get; set; }
    public int BankExports { get; set; }
    public int MeetingAudienceRows { get; set; }
    public int TwoFactorRows { get; set; }
    public int PasswordHistoryRows { get; set; }
}

/// <summary>
/// Seeds the DESK / ADMINISTRATIVE tables that held zero rows in every database here -
/// `reportview` + `reportrunlog` + `reportexport`, `postingbatch`, `rolloverauditlog`,
/// `tenantsubscription`, `curriculumtopiclearningmaterial`, `bankfiletemplate` + `bankfileexport`,
/// `hrmeetingaudience`, `usertwofactor` and `userpasswordhistory`.
///
/// WHY THIS EXISTS
/// ---------------
/// These tables are not one module. What they share is that each is a TABLE WITH A REAL READ
/// SURFACE whose parents were already populated - so every one of those reads returned an empty
/// result from a fully configured campus, which is the failure this tool exists to prevent. Some
/// are worse than others: `reportview`/`reportrunlog` are the reporting desk's OWN history (a
/// campus that has run reports for a year has both), the posting batch is the accounting monitor's
/// audit trail, and `employeepayrolldetail`'s sibling `bankfileexport` is what a bursar hands to a
/// bank.
///
/// ⚠️ THREE TABLES ON THE WORKLIST ARE DELIBERATELY NOT SEEDED, AND RECORDED HERE RATHER THAN FILLED - all
/// three for the same reason: SEEDING A ROW NO QUERY READS is the documented "a seeded row no query can
/// reach" defect, and no spec could measure it either.
///
///   * `calendarreminderlog` IS A WRITE-ONLY OUTBOX. `CalendarReminderService.SendReminder` is its
///     only writer and it stamps `Channel = 'Mock'`; NOTHING in either server reads the table, there
///     is no endpoint and no screen. When a real channel is implemented (the service's own TODO) the
///     reader lands with it, and the spec is owed THEN.
///   * `teacherparentaction` HAS NO CODE REFERENCE ANYWHERE in either server (`grep -rn` over
///     `--include=*.cs` and over `school-web` returns nothing). It is a dead table.
///   * `curriculumtopiclearningmaterial` HAS A REPOSITORY BUT NO CALLER - which was measured, not
///     assumed (`grep -rn "GetByTopic|CurriculumTopicLearningMaterialRepository"` outside its own file
///     returns only an unrelated `CurriculumTopicPlanRepository.GetByTopic`, and `school-web` never
///     names the table). Its only other appearance is `AttachmentFileRepository`'s orphan sweep, which
///     EXCLUDES its file ids - so the app knows the table's files exist while nothing ever lists them.
///     `CurriculumTopicPlanDto.LearningMaterials` is a property no repository populates.
///     ⚠️ THAT IS AN APP GAP, NOT A PERF FIXTURE ONE: the screen's "Learning Materials" panel is always
///     empty because nothing fills it. Seeding rows would hide it behind plausible data.
///
/// ⚠️ IT DOES NOT TOUCH A PARENT ROW. Report definitions, curriculum topics/plans, payroll periods,
/// meetings, academic years and users all belong to fixtures that already ran; every column this
/// seeder writes is either its own scope triple or a reference to one of those existing rows. The
/// one exception is the `backgroundjob` row that owns each `reportexport` - which is the ONLY shape
/// the application can produce (an export is created by the export job and found by its job id), and
/// it is asserted in the fixture.
/// </summary>
public sealed class DeskOperationsSeeder : BaseSeeder
{
    public DeskOperationsSeeder(string connectionString) : base(connectionString) { }

    /// <summary>The tables a bulk desk load invalidates, so a fixture can ANALYZE them.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "reportview", "reportrunlog", "reportexport", "postingbatch", "rolloverauditlog",
        "tenantsubscription", "bankfiletemplate", "bankfileexport", "hrmeetingaudience",
        "usertwofactor", "userpasswordhistory",
    };

    private static readonly string[] ViewNames =
    {
        "Term fee collection - my grades",
        "Outstanding fees - overdue only",
        "Attendance below 90%",
    };

    private static readonly string[] BankFormats =
    {
        "WPS", "CSV", "Fixed-Width",
    };

    /// <summary>
    /// The password-history rows. Each is a genuine PBKDF2 hash of a distinct throwaway passphrase -
    /// see <see cref="HashAsync"/>. ⚠️ They are NOT hashes of any real credential: the perf dataset
    /// exists to be MEASURED, not logged into, and a fixture whose history verified against a
    /// working password would be a credential in a test database.
    /// </summary>
    private static readonly string[] HistoricalPassphrases =
    {
        "perf-history-1", "perf-history-2", "perf-history-3",
        "perf-history-4", "perf-history-5", "perf-history-6",
    };

    public async Task<DeskOperationsSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, DeskOperationsSeedOptions options, bool verbose = true)
    {
        var result = new DeskOperationsSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM reportview
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  Desk operations: campus {campusId} already holds {existing} saved view(s) - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // PREREQUISITES, REPORTED RATHER THAN THROWN.
        //
        // ⚠️ EACH ONE NAMES THE FIXTURE THAT OWNS IT, because a skip with no next step reads like a
        // defect in this seeder instead of a missing parent.
        // ------------------------------------------------------------------
        // The report catalog is seeded per deployment (17 rows at 0/0/0) rather than per campus, and a
        // scoped definition OVERRIDES the system default - so a campus definition is preferred and the
        // system row is the fallback, which is exactly how ReportDefinitionRepository.GetByCode resolves.
        var definitionId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM reportdefinition
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId })
            ?? await conn.ExecuteScalarAsync<long?>(
                @"SELECT id FROM reportdefinition WHERE tenantid = 0 AND schoolid = 0 AND campusid = 0
                   ORDER BY id LIMIT 1");

        var periodIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM payrollperiod
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var meetingIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM hrmeeting
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var yearIds = (await conn.QueryAsync<long>(
            @"SELECT DISTINCT academicyearid FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY academicyearid",
            new { tenantId, schoolId, campusId })).ToList();

        // ⚠️ `rolloverauditlog.curriculumversionid` IS NOT NULL even though its parent column has a
        // DEFAULT - a default only applies when the column is OMITTED, and this seeder names its
        // columns. The audit row is the year-end bridge's own record of WHAT it cloned, so a fabricated
        // id would make the History panel name a curriculum version nobody has.
        var curriculumVersionId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM curriculumversion
               WHERE tenantid = @tenantId AND schoolid = @schoolId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId });

        // ⚠️ THE 2FA / HISTORY SUBJECT. Both tables are keyed on `userid` ALONE and carry no scope
        // columns, so they are cleared through `users` (see below) - which means the seeder must pick
        // a user that CAMPUS owns, not "the lowest user in the database".
        var userId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM users
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var missing = new List<string>();
        if (definitionId == null) missing.Add("no `reportdefinition` row (the reporting catalog is seeded per deployment)");
        if (periodIds.Count == 0) missing.Add("no payroll period (run the HR module fixture)");
        if (meetingIds.Count == 0) missing.Add("no hr meeting (run the communication fixture)");
        if (yearIds.Count == 0) missing.Add("no student enrolment, so no academic year (run the student/dataset fixture)");
        if (userId == null) missing.Add("no user on this campus (run the platform fixture)");
        if (curriculumVersionId == null) missing.Add("no curriculum version for this school (run the curriculum module fixture)");

        if (missing.Count > 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} cannot host the desk seed: {string.Join("; ", missing)}. " +
                "This seeder writes only desk rows and touches no parent, so it needs each parent to exist.";
            return result;
        }

        // ------------------------------------------------------------------
        // CLEAR, CHILDREN FIRST.
        //
        // ⚠️ `usertwofactor`, `userpasswordhistory` and `curriculumtopiclearningmaterial` HAVE NO
        // SCOPE COLUMNS AT ALL - a scoped DELETE on them is a 42703 and, unlike an FK refusal, that
        // is NOT caught and would abort the whole seed. The first two are cleared through `users`
        // (the documented helper); the material rows are cleared through the curriculum tree, which
        // is school-scoped rather than campus-scoped, so the subquery is written out.
        // ------------------------------------------------------------------
        if (options.Force)
        {
            await ClearTableByParentAsync(conn, "usertwofactor", "userid", "users", tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "userpasswordhistory", "userid", "users", tenantId, schoolId, campusId);
            // ⚠️ `reportexport` HAS NO SCOPE COLUMNS EITHER, and its reachability is through the JOB
            // (`GetByJobId` is the only read). So it is cleared by the jobs this seeder owns - a scoped
            // DELETE here would be the same 42703 as the identity tables, and clearing it by
            // `reportdefinition` would wrongly reach every campus that shares the system definition.
            await conn.ExecuteAsync(
                @"DELETE FROM reportexport
                   WHERE jobid IN (
                         SELECT id FROM backgroundjob
                          WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                            AND jobtype = 'ReportExport')",
                new { tenantId, schoolId, campusId });
            await conn.ExecuteAsync(
                @"DELETE FROM backgroundjob
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND jobtype = 'ReportExport'",
                new { tenantId, schoolId, campusId });
            await ClearTableAsync(conn, "reportrunlog", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "reportview", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "postingbatch", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "rolloverauditlog", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "bankfileexport", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "bankfiletemplate", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "hrmeetingaudience", tenantId, schoolId, campusId);
            // `tenantsubscription` is TENANT-scoped (one row per tenant, `ux_tenantsubscription_tenant`)
            // and carries no school/campus at all.
            try
            {
                await conn.ExecuteAsync(
                    "DELETE FROM tenantsubscription WHERE tenantid = @tenantId", new { tenantId });
            }
            catch (Exception ex) when (ex is NpgsqlException)
            {
                Console.WriteLine("  Skipping clear of tenantsubscription (FK). Appending new data.");
            }
        }

        var defId = definitionId!.Value;

        // ------------------------------------------------------------------
        // THE REPORTING DESK.
        // ------------------------------------------------------------------
        var viewCount = 0;
        foreach (var (index, name) in ViewNames.Take(options.SavedViews).Select((n, i) => (i, n)))
        {
            await conn.ExecuteAsync(
                @"INSERT INTO reportview
                      (tenantid, schoolid, campusid, reportdefinitionid, name, filters, sort,
                       createdby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @definitionId, @name, @filters::jsonb, NULL,
                       0, @now, @now)",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    definitionId = defId,
                    name = $"Perf view {index + 1}: {name}",
                    filters = JsonSerializer.Serialize(new
                    {
                        academicYearId = (long?)null,
                        classroomId = (long?)null,
                        savedBy = index + 1,
                    }),
                    now,
                });
            viewCount++;
        }

        result.SavedViews = viewCount;

        var runCount = 0;
        for (var i = 0; i < options.RunLogRows; i++)
        {
            // ⚠️ MOSTLY SUCCEEDED, WITH A REAL FAILURE SHARE. A run log whose every row is Succeeded
            // makes the failure filter measure nothing, and the reporting desk's own history exists to
            // show why a run failed.
            var failed = i % 7 == 6;
            var ranOn = now.AddMinutes(-i * 37);
            await conn.ExecuteAsync(
                @"INSERT INTO reportrunlog
                      (tenantid, schoolid, campusid, reportdefinitionid, userid, params, rowsreturned,
                       elapsedms, status, error, ranon, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @definitionId, @userId, @runParams::jsonb, @rows,
                       @elapsedMs, @status, @error, @ranOn, 0, 0, @ranOn, @ranOn)",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    definitionId = defId,
                    userId = userId!.Value,
                    // ⚠️ NAMED `runParams`, NOT `params`: `params` is a C# KEYWORD and cannot be an
                    // anonymous-object member (CS1041).
                    runParams = JsonSerializer.Serialize(new
                    {
                        academicYearId = yearIds[0],
                        pageSize = 50,
                    }),
                    rows = failed ? 0 : 50 * ((i % 9) + 1),
                    elapsedMs = 40 + (i % 23) * 17,
                    status = failed ? "Failed" : "Succeeded",
                    error = failed ? "The report timed out while reading its datasource." : null,
                    ranOn,
                });
            runCount++;
        }

        result.RunLogRows = runCount;

        // ⚠️ AN EXPORT IS REACHABLE ONLY THROUGH ITS JOB, so the job row is written too - a
        // `reportexport` row with a fabricated `jobid` is exactly the "a seeded row no query can
        // reach" defect, and `jobid` is UNIQUE with no foreign key, so nothing would have complained.
        var exportCount = 0;
        for (var i = 0; i < options.Exports; i++)
        {
            var format = i % 2 == 0 ? "csv" : "pdf";
            var contentType = format == "csv" ? "text/csv" : "application/pdf";
            var payload = format == "csv"
                ? "admissionnumber,name,amount\nE2E-1,Perf Student,1000.00\n"
                : null;
            // ⚠️ `contentbytes` IS `bytea` - the artifact is the DOCUMENT, not its size, so the row has
            // to carry real bytes. A PDF export with a NULL blob is a file the download endpoint serves
            // as nothing, and the fixture's own format/column pairing check would be asserting a shape
            // the application never writes.
            byte[]? bytes = format == "pdf" ? MinimalPdfBytes($"Perf report {i + 1}") : null;

            var jobId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO backgroundjob
                      (tenantid, schoolid, campusid, jobtype, entityid, status, payload, progress,
                       retrycount, startedon, completedon, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, 'ReportExport', @definitionId, 3, @payload::jsonb, 100,
                       0, @now, @now, 0, 0, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    definitionId = defId,
                    payload = JsonSerializer.Serialize(new { reportDefinitionId = defId, format }),
                    now,
                });

            await conn.ExecuteAsync(
                @"INSERT INTO reportexport
                      (jobid, reportdefinitionid, filename, content, rowcount, format, contenttype,
                       contentbytes, createdby, createdon, modifiedon)
                  VALUES
                      (@jobId, @definitionId, @filename, @content, @rowCount, @format, @contentType,
                       @contentBytes, 0, @now, @now)",
                new
                {
                    jobId,
                    definitionId = defId,
                    filename = $"perf-report-{i + 1}.{format}",
                    content = payload,
                    rowCount = 1_250 * (i + 1),
                    format,
                    contentType,
                    contentBytes = bytes,
                    now,
                });
            exportCount++;
        }

        result.Exports = exportCount;

        // ------------------------------------------------------------------
        // ACCOUNTING POSTING BATCHES - one a month for two years, with a real failure share
        // (the monitor's Failed tab is a filter, and a filter with nothing behind it measures nothing).
        // ------------------------------------------------------------------
        var batchCount = 0;
        for (var i = 0; i < options.PostingBatches; i++)
        {
            var startedOn = now.AddDays(-(options.PostingBatches - i) * 30);
            var failedCount = i % 11 == 10 ? 3 : 0;
            var total = 20 + (i % 7) * 5;
            var status = failedCount == 0 ? "Completed" : "PartiallyFailed";
            await conn.ExecuteAsync(
                @"INSERT INTO postingbatch
                      (tenantid, schoolid, campusid, batchnumber, startedon, completedon,
                       totalprocessed, totalfailed, status, errormessage,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @batchNumber, @startedOn, @completedOn,
                       @totalProcessed, @totalFailed, @status, @errorMessage,
                       0, 0, @startedOn, @completedOn)",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    batchNumber = $"PB-{campusId:D3}-{startedOn:yyyyMM}-{i + 1:D4}",
                    startedOn,
                    completedOn = startedOn.AddMinutes(3 + (i % 5)),
                    totalProcessed = total - failedCount,
                    totalFailed = failedCount,
                    status,
                    errorMessage = failedCount == 0
                        ? null
                        : "A batch of postings was rejected: no open fiscal period covers the entry date.",
                });
            batchCount++;
        }

        result.PostingBatches = batchCount;

        // ------------------------------------------------------------------
        // ROLLOVER AUDIT - the campus's year-end history. `isrolledback` is a REAL state, so a
        // share of the rows carry it (the History panel exists to show exactly that).
        // ------------------------------------------------------------------
        var rolloverCount = 0;
        for (var i = 0; i < options.RolloverLogs; i++)
        {
            var sourceYear = yearIds[i % yearIds.Count];
            var newYear = yearIds[(i + 1) % yearIds.Count];
            var rolledBack = i % 4 == 3;
            var createdOn = now.AddDays(-(options.RolloverLogs - i) * 365);
            await conn.ExecuteAsync(
                @"INSERT INTO rolloverauditlog
                      (tenantid, schoolid, campusid, sourceacademicyearid, newacademicyearid,
                       sourceyearlabel, newyearlabel, userid, termscloned, gradescloned, subjectscloned,
                       classroomscloned, timetableentriescloned, feestructurescloned, curriculumversionid,
                       copyoptionsjson, isrolledback, rolledbackon, rolledbackby, durationms,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @sourceYear, @newYear,
                       @sourceLabel, @newLabel, @userId, @terms, @grades, @subjects,
                       @classrooms, @entries, @feeStructures, @curriculumVersionId,
                       @copyOptions::jsonb, @rolledBack, @rolledBackOn, @rolledBackBy, @durationMs,
                       0, 0, @createdOn, @createdOn)",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    sourceYear,
                    newYear,
                    sourceLabel = $"Perf year {sourceYear}",
                    newLabel = $"Perf year {newYear}",
                    userId = userId!.Value,
                    terms = 3,
                    grades = 6,
                    subjects = 12,
                    classrooms = 14,
                    entries = 240,
                    feeStructures = 6,
                    curriculumVersionId = curriculumVersionId!.Value,
                    copyOptions = JsonSerializer.Serialize(new { transport = true, feePlan = true }),
                    rolledBack,
                    rolledBackOn = rolledBack ? (DateTime?)createdOn.AddDays(2) : null,
                    rolledBackBy = rolledBack ? userId!.Value : (long?)null,
                    durationMs = 4_500 + i * 750,
                    createdOn,
                });
            rolloverCount++;
        }

        result.RolloverLogs = rolloverCount;

        // ------------------------------------------------------------------
        // TENANT SUBSCRIPTION - ONE row per tenant (`ux_tenantsubscription_tenant`), so this is not a
        // volume table and a second row is refused by the index rather than by the code.
        // ------------------------------------------------------------------
        await conn.ExecuteAsync(
            @"INSERT INTO tenantsubscription
                  (tenantid, planname, billingcycle, seatcount, status, renewaldate, graceperioddays,
                   paymentmethod, billingcontactemail, billingcontactphone, taxid, notificationemail,
                   reminderdaysbeforerenewal, createdby, modifiedby, createdon, modifiedon)
              VALUES
                  (@tenantId, @planName, @billingCycle, @seatCount, @status, @renewalDate, @gracePeriodDays,
                   @paymentMethod, @billingEmail, @billingPhone, @taxId, @notificationEmail,
                   @reminderDays, 0, 0, @now, @now)
              ON CONFLICT (tenantid) DO NOTHING",
            new
            {
                tenantId,
                planName = "Enterprise Annual",
                billingCycle = "Annual",
                seatCount = 1_200,
                status = "Active",
                renewalDate = now.AddMonths(4).Date,
                gracePeriodDays = 15,
                paymentMethod = "BankTransfer",
                billingEmail = "bursar@perf.example",
                billingPhone = "+971-4-000-0000",
                taxId = "PERF-TRN-0001",
                notificationEmail = "billing@perf.example",
                reminderDays = 30,
                now,
            });
        result.Subscriptions = 1;

        // ------------------------------------------------------------------
        // ⚠️ NO `curriculumtopiclearningmaterial` HERE - see the class comment: the table has a
        // repository but no caller, so a row in it is data nothing reads.
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // BANK FILE TEMPLATES + THE EXPORTS RAISED FROM THEM.
        // ⚠️ `columnmappingjson` and the three template columns are NOT NULL in spirit - a template with
        // no mapping produces a file with no columns - so they carry a real (if short) definition.
        // ------------------------------------------------------------------
        var templateIds = new List<long>();
        for (var i = 0; i < options.BankTemplates; i++)
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO bankfiletemplate
                      (tenantid, schoolid, campusid, name, format, bankcode, bankname, fileextension,
                       encoding, delimiter, columnmappingjson, headertemplatejson, detailtemplatejson,
                       trailertemplatejson, isactive, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @name, @format, @bankCode, @bankName, @fileExtension,
                       @encoding, @delimiter, @columnMapping::jsonb, @header::jsonb, @detail::jsonb,
                       @trailer::jsonb, TRUE, 0, 0, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    name = $"Perf {BankFormats[i % BankFormats.Length]} template {i + 1}",
                    format = BankFormats[i % BankFormats.Length],
                    bankCode = $"PERFBANK{i + 1}",
                    bankName = $"Perf Bank {i + 1}",
                    fileExtension = i % 3 == 2 ? "txt" : BankFormats[i % BankFormats.Length].ToLowerInvariant(),
                    encoding = "UTF-8",
                    delimiter = i % 3 == 2 ? "" : ",",
                    columnMapping = JsonSerializer.Serialize(new
                    {
                        employeeCode = 1,
                        employeeName = 2,
                        accountNumber = 3,
                        amount = 4,
                    }),
                    header = JsonSerializer.Serialize(new { line = "HEADER,{{periodName}},{{bankCode}}" }),
                    detail = JsonSerializer.Serialize(new { line = "{{employeeCode}},{{employeeName}},{{amount}}" }),
                    trailer = JsonSerializer.Serialize(new { line = "TRAILER,{{recordCount}},{{totalAmount}}" }),
                    now,
                });
            templateIds.Add(id);
        }

        result.BankTemplates = templateIds.Count;

        var bankExportCount = 0;
        foreach (var (index, periodId) in periodIds.Select((p, i) => (i, p)))
        {
            await conn.ExecuteAsync(
                @"INSERT INTO bankfileexport
                      (tenantid, schoolid, campusid, payrollperiodid, bankfiletemplateid, filename,
                       filepath, totalrecords, totalamount, status, filechecksum, notes,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @periodId, @templateId, @fileName,
                       @filePath, @totalRecords, @totalAmount, @status, @checksum, NULL,
                       0, 0, @now, @now)",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    periodId,
                    templateId = templateIds[index % templateIds.Count],
                    fileName = $"wps-perf-campus{campusId}-period{periodId}.csv",
                    // ⚠️ `filepath` IS NOT NULL - the desk stores where the generated file lives, and a
                    // row with no path is a download the screen offers and cannot serve.
                    filePath = $"perf/bankfiles/campus{campusId}/period{periodId}/wps-{periodId}.csv",
                    totalRecords = 120,
                    totalAmount = 480_000.00m,
                    status = index % 4 == 3 ? "Draft" : "Generated",
                    // ⚠️ A 64-character hex digest: the column is a checksum, and an empty string would
                    // make "was this file verified?" indistinguishable from "no file".
                    checksum = Convert.ToHexString(
                        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"perf-{campusId}-{periodId}"))),
                    now,
                });
            bankExportCount++;
        }

        result.BankExports = bankExportCount;

        // ------------------------------------------------------------------
        // MEETING AUDIENCE - the invite list of each meeting, read BY MEETING.
        // ⚠️ At least one row per meeting PER SUBJECT KIND where the campus has the ids, because the
        // screen renders a separate panel per kind; a meeting whose audience is only classrooms shows
        // two empty panels that look like a missing feature.
        // ------------------------------------------------------------------
        var classroomIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM classroom
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 5",
            new { tenantId, schoolId, campusId })).ToList();

        var teacherIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM teacher
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 5",
            new { tenantId, schoolId, campusId })).ToList();

        var studentIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM student
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 5",
            new { tenantId, schoolId, campusId })).ToList();

        var audienceCount = 0;
        foreach (var meetingId in meetingIds)
        {
            for (var i = 0; i < options.AudiencePerMeeting; i++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO hrmeetingaudience
                          (tenantid, schoolid, campusid, meetingid, classroomid, teacherid, studentid,
                           gradeid, createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@tenantId, @schoolId, @campusId, @meetingId, @classroomId, @teacherId, @studentId,
                           NULL, 0, 0, @now, @now)",
                    new
                    {
                        tenantId,
                        schoolId,
                        campusId,
                        meetingId,
                        // One kind per row - the screen's panels are separate lists, so a row that
                        // carried all three ids would fill one panel and leave two empty.
                        classroomId = i % 3 == 0 && classroomIds.Count > 0
                            ? classroomIds[i % classroomIds.Count] : (long?)null,
                        teacherId = i % 3 == 1 && teacherIds.Count > 0
                            ? teacherIds[i % teacherIds.Count] : (long?)null,
                        studentId = i % 3 == 2 && studentIds.Count > 0
                            ? studentIds[i % studentIds.Count] : (long?)null,
                        now,
                    });
                audienceCount++;
            }
        }

        result.MeetingAudienceRows = audienceCount;

        // ------------------------------------------------------------------
        // TWO-FACTOR ENROLMENT + PASSWORD HISTORY for ONE campus user.
        //
        // ⚠️ `isenabled = FALSE` AND THAT IS THE POINT. `UserTwoFactorRepository.VerifyCode` treats
        // enrolment and enforcement separately, and the users grid renders `isTwoFactorEnabled` as a
        // badge - so the honest perf fixture is a user who has STARTED enrolment without a working
        // authenticator. A `TRUE` row would claim the fixture can produce a valid TOTP, which it cannot.
        // ------------------------------------------------------------------
        await conn.ExecuteAsync(
            @"INSERT INTO usertwofactor
                  (userid, secret, isenabled, backupcodes, createdat, failedattempts, lockeduntil)
              VALUES
                  (@userId, @secret, FALSE, NULL, @now, 0, NULL)
              ON CONFLICT (userid) DO NOTHING",
            new
            {
                userId = userId!.Value,
                secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
                now,
            });
        result.TwoFactorRows = 1;

        var historyCount = 0;
        foreach (var passphrase in HistoricalPassphrases.Take(options.PasswordHistoryRows))
        {
            await conn.ExecuteAsync(
                @"INSERT INTO userpasswordhistory (userid, passwordhash, createdon)
                  VALUES (@userId, @passwordHash, @createdOn)",
                new
                {
                    userId = userId!.Value,
                    passwordHash = HashPassphrase(passphrase),
                    // ⚠️ SPREAD OVER TIME, AND NEWER THAN THE OLDEST FIRST: `IsReuseAsync` scans
                    // `ORDER BY CreatedOn DESC, Id DESC LIMIT @lookback`, so a set of rows sharing one
                    // timestamp would make the LIMIT pick whatever the engine returned - the same
                    // `-infinity` ordering trap the rollover audit logged.
                    createdOn = now.AddDays(-(historyCount + 1) * 90),
                });
            historyCount++;
        }

        result.PasswordHistoryRows = historyCount;

        await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);

        if (verbose)
        {
            Console.WriteLine(
                $"  Desk operations: views {result.SavedViews}, run log {result.RunLogRows}, exports {result.Exports}, " +
                $"posting batches {result.PostingBatches}, rollovers {result.RolloverLogs}, " +
                $"bank templates {result.BankTemplates}, " +
                $"bank exports {result.BankExports}, audience {result.MeetingAudienceRows}, " +
                $"2FA {result.TwoFactorRows}, password history {result.PasswordHistoryRows}");
        }

        return result;
    }

    /// <summary>
    /// A REAL, openable one-page PDF - the smallest document `PdfService` could plausibly have produced,
    /// with a correct cross-reference table so a viewer accepts it.
    ///
    /// ⚠️ WHY NOT JUST A FEW RANDOM BYTES WITH A `%PDF-` HEADER. `PdfController`'s download serves the
    /// row's bytes straight to the browser, and a fixture whose "PDF" cannot be opened makes every later
    /// question about that endpoint unanswerable ("the download is broken" and "the fixture wrote a
    /// non-document" look identical). The xref offsets are computed as the objects are emitted rather
    /// than hardcoded, which is the half that makes it valid.
    /// </summary>
    private static byte[] MinimalPdfBytes(string text)
    {
        var body = new System.Text.StringBuilder();
        var offsets = new List<int>();

        void Object(string content)
        {
            offsets.Add(body.Length);
            body.Append(content);
        }

        body.Append("%PDF-1.4\n");
        Object("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        Object("2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n");
        Object("3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 120]/Contents 4 0 R" +
               "/Resources<</Font<</F1 5 0 R>>>>>>endobj\n");

        // An escaped parenthesised string is what a PDF text operator requires; the text this dataset
        // writes is ASCII, so only `(`/`)`/`\` need escaping.
        var escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var stream = $"BT /F1 14 Tf 20 60 Td ({escaped}) Tj ET";
        Object($"4 0 obj<</Length {stream.Length}>>stream\n{stream}\nendstream\nendobj\n");
        Object("5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n");

        var xrefOffset = body.Length;
        body.Append("xref\n0 6\n");
        body.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            body.Append(offset.ToString("D10").PadLeft(10, '0')).Append(" 00000 n \n");
        }

        body.Append("trailer<</Size 6/Root 1 0 R>>\n");
        body.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        return System.Text.Encoding.ASCII.GetBytes(body.ToString());
    }

    /// <summary>
    /// A genuine PBKDF2/SHA1 hash in the application's own format (16-byte salt + 32-byte hash,
    /// base64) - see `PasswordHasher`. The passphrase is a throwaway string, so the row is a real
    /// history entry the reuse check can walk WITHOUT being a credential anything could log in with.
    /// </summary>
    private static string HashPassphrase(string passphrase)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(passphrase), salt, 10_000, HashAlgorithmName.SHA1, 32);
        var combined = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, combined, salt.Length, hash.Length);
        return Convert.ToBase64String(combined);
    }

    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, DeskOperationsSeedResult result)
    {
        result.SavedViews = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM reportview WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.RunLogRows = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM reportrunlog WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.Exports = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM reportexport");
        result.PostingBatches = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM postingbatch WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.RolloverLogs = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM rolloverauditlog WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.Subscriptions = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tenantsubscription WHERE tenantid=@tenantId", new { tenantId });
        result.BankTemplates = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM bankfiletemplate WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.BankExports = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM bankfileexport WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.MeetingAudienceRows = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM hrmeetingaudience WHERE tenantid=@tenantId AND schoolid=@schoolId AND campusid=@campusId",
            tenantId, schoolId, campusId);
        result.TwoFactorRows = await ScalarAsync(conn,
            @"SELECT COUNT(*) FROM usertwofactor tf JOIN users u ON u.id = tf.userid
               WHERE u.tenantid=@tenantId AND u.schoolid=@schoolId AND u.campusid=@campusId",
            tenantId, schoolId, campusId);
        result.PasswordHistoryRows = await ScalarAsync(conn,
            @"SELECT COUNT(*) FROM userpasswordhistory h JOIN users u ON u.id = h.userid
               WHERE u.tenantid=@tenantId AND u.schoolid=@schoolId AND u.campusid=@campusId",
            tenantId, schoolId, campusId);
    }

    private static Task<int> ScalarAsync(NpgsqlConnection conn, string sql, long tenantId, long schoolId, long campusId)
        => conn.ExecuteScalarAsync<int>(sql, new { tenantId, schoolId, campusId });
}
