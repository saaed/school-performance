using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="EventFinanceSeeder"/>.</summary>
public sealed class EventFinanceSeedOptions
{
    /// <summary>
    /// Charges per event. Four, one per `EventChargeStatus` - the detail screen's tabs separate
    /// exactly those four (Draft / Open / Approved / Rejected) and its status-count tiles count
    /// them, so a single charge would leave three tabs and three tiles reading zero.
    /// </summary>
    public int ChargesPerEvent { get; set; } = 4;

    /// <summary>
    /// Participants the event's audience expands to. The detail screen PAGES these, so this is the
    /// row count its first page pages over - and the participant list is what the invoice
    /// fan-out is driven from, so it is also the multiplier on the per-student charges.
    /// </summary>
    public int ParticipantsPerEvent { get; set; } = 120;

    /// <summary>
    /// Per-student charges raised against the APPROVED charge. One per participant is the real
    /// shape (a charge fans out over everybody who registered), and the unique index
    /// `ux_eventstudentcharge_chargeenrollment` makes any other arrangement impossible for the
    /// same (charge, enrollment) pair. The knob caps it so a campus with thousands of enrolments
    /// still seeds in a second.
    /// </summary>
    public int StudentChargesPerEvent { get; set; } = 120;

    /// <summary>Re-seed even when the campus already holds event charges.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's event-finance seed produced.</summary>
public sealed class EventFinanceSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int SchoolEvents { get; set; }
    public int EventCharges { get; set; }
    public int EventParticipants { get; set; }
    public int EventStudentCharges { get; set; }
}

/// <summary>
/// Seeds the EVENT FINANCE tables - `eventcharge`, `eventparticipant` and `eventstudentcharge` -
/// for one campus, on a PAID event the seeder owns.
///
/// WHY THIS EXISTS
/// ---------------
/// All three tables held ZERO rows in every database here, while three shipped screens read them:
/// `school.event.finance.html` (the charges + their status tiles), `school.event.detail.html` (the
/// paged participant list) and the invoice-generation job (which fans a charge out over the event's
/// participants). The three tables are also the module's whole money chain - a charge is only
/// invoiceable once Approved, and only participants who have not cancelled receive one - so a spec
/// over an empty table would report SKIP on the exact queries the module is built around.
///
/// ⚠️ THE EVENT IS FOUND-OR-CREATED, AND `ispaidevent` IS THE POINT. The twelve `PERF School Event N`
/// rows a previous seeder leaves behind are all `ispaidevent = false` with an `amount` of 0.00, and
/// `school.event.finance.html` bounces a FREE event back to the detail page. Seeding charges against
/// one of those would produce rows that no screen can reach - the "a seeded row no query can reach"
/// defect - so this seeder carries its own paid event, keyed by its `code` so a re-run reuses it.
///
/// ⚠️ THE PARTICIPANTS ARE REAL ENROLMENTS OF THE SAME CAMPUS, AND THAT IS A JOIN REQUIREMENT, NOT A
/// PREFERENCE. `EventParticipantRepository.GetByEventId`/`GetByEventIdPaged` INNER JOIN
/// `StudentEnrollment -> Student` (and LEFT JOIN `Classroom`), so a participant whose enrollment is
/// at another campus exists in the table and can never appear in the list. The statuses are spread
/// across `EventParticipantStatus` (Invited / Registered / Confirmed / Cancelled) because the screen
/// renders a status pill per row and the invoice fan-out skips the cancelled ones - a campus of
/// all-Confirmed rows would leave both branches unexercised.
/// </summary>
public sealed class EventFinanceSeeder : BaseSeeder
{
    public EventFinanceSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk event-finance load invalidates, so a fixture can ANALYZE them exactly as
    /// <see cref="PerfDatasetSeeder"/> does. Without fresh statistics the planner reasons from the
    /// row counts that described an EMPTY table.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "eventcharge", "eventparticipant", "eventstudentcharge", "schoolevent"
    };

    /// <summary>
    /// The event a campus's event-finance rows hang off. Deterministic per campus so a re-run
    /// reuses the event instead of growing a new one every time - `schoolevent.code` has no unique
    /// index here, so the code is a convention this seeder enforces by LOOKING FIRST.
    /// </summary>
    public static string EventCodeFor(long campusId) => $"PERF-EVT-FIN-{campusId}";

    /// <summary>The amount the paid event charges. Set so the APPROVED charge reconciles exactly to
    /// it - the invoice button's `chargesTotalMismatch` guard refuses to open otherwise, and a spec
    /// that measured the guard stuck closed would be measuring the fixture, not the screen.</summary>
    public const decimal EventAmount = 250.00m;

    public async Task<EventFinanceSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, EventFinanceSeedOptions options, bool verbose = true)
    {
        var result = new EventFinanceSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM eventcharge
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // ⚠️ A SKIPPED CAMPUS MUST STILL REPORT WHAT IT HOLDS, not what this run wrote - a
            // total of zero over a campus that holds rows reads as a broken fixture.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  Event finance: campus {campusId} already holds {existing} event charge(s) - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // The PREREQUISITES a campus must satisfy, REPORTED rather than thrown - the shape
        // `CommunicationWorkspaceSeeder` established for a missing teacher.
        // ------------------------------------------------------------------
        var enrollments = (await conn.QueryAsync<long>(
            @"SELECT id FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND studentstatus = 3
               ORDER BY id
               LIMIT @limit",
            new { tenantId, schoolId, campusId, limit = options.ParticipantsPerEvent })).ToList();

        if (enrollments.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} holds no ENROLLED student (studentenrollment.studentstatus = 3) - an event " +
                "participant is a studentenrollmentid and the participant list INNER JOINs StudentEnrollment -> " +
                "Student, so this campus cannot carry an audience. Run PerfDatasetTests first.";
            return result;
        }

        // The event's year and term. An existing event on the campus already carries a valid pair
        // (its own controller resolved them through the campus's CURRENT active term); a campus with
        // no event falls back to its active+published year, whose first term the controller would
        // have picked too.
        var eventPeriod = await conn.QueryFirstOrDefaultAsync<EventPeriodRow>(
            @"SELECT academicyearid AS AcademicYearId, termid AS TermId FROM schoolevent
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        if (eventPeriod is null)
        {
            eventPeriod = await conn.QueryFirstOrDefaultAsync<EventPeriodRow>(
                @"SELECT ay.id AS AcademicYearId,
                         (SELECT t.id FROM terms t WHERE t.academicyearid = ay.id ORDER BY t.id LIMIT 1) AS TermId
                    FROM academicyear ay
                   WHERE ay.tenantid = @tenantId AND ay.schoolid = @schoolId AND ay.campusid = @campusId
                     AND ay.isactive = TRUE AND ay.ispublished = TRUE
                   ORDER BY ay.id DESC LIMIT 1",
                new { tenantId, schoolId, campusId });
        }

        if (eventPeriod is null || eventPeriod.TermId == 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} holds no school event and no ACTIVE + PUBLISHED academic year with a term - " +
                "`schoolevent.academicyearid`/`.termid` are NOT NULL and `SchoolEventController.Post` refuses an " +
                "event whose campus has no current term. Run the academic fixture (J2's spine) first.";
            return result;
        }

        // ------------------------------------------------------------------
        // THE PAID EVENT. `ispaidevent` is what makes `school.event.finance.html` render at all.
        // ------------------------------------------------------------------
        var eventCode = EventCodeFor(campusId);
        var eventId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM schoolevent
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND code = @code
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId, code = eventCode });

        if (eventId is null or 0)
        {
            // ⚠️ `schoolevent.id` is sequence-backed (NOT `GENERATED ALWAYS`), but the id is still
            // left to the sequence and read back - the same rule the other seeders here follow.
            var start = now.Date.AddDays(-7);
            var end = now.Date.AddDays(7);
            eventId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO schoolevent
                      (code, title, academicyearid, termid, description, startdate, enddate, starttime, endtime,
                       creatortype, scope, ispaidevent, amount, duedate, isactive,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon,
                       eventtype, registrationstartdate, registrationenddate, isoptional, status)
                  VALUES
                      (@code, @title, @academicYearId, @termId, @description, @startdate, @enddate, @starttime, @endtime,
                       1, 1, TRUE, @amount, @duedate, TRUE,
                       @tenantId, @schoolId, @campusId, 0, 0, @now, @now,
                       2, @registrationStart, @registrationEnd, FALSE, 2)
                  RETURNING id",
                new
                {
                    code = eventCode,
                    title = $"PERF Paid Event {campusId}",
                    academicYearId = eventPeriod.AcademicYearId,
                    termId = eventPeriod.TermId,
                    description = "Perf dataset: the event the event-finance screens read.",
                    startdate = start,
                    enddate = end,
                    starttime = new TimeSpan(9, 0, 0),
                    endtime = new TimeSpan(15, 0, 0),
                    amount = EventAmount,
                    duedate = end,
                    registrationStart = start,
                    registrationEnd = now.Date,
                    // ⚠️ THE SCOPE IS PASSED EXPLICITLY. Dapper creates parameters only for the
                    // properties an anonymous object DECLARES, and a placeholder the parameter bag
                    // never supplied is left in the statement verbatim - PostgreSQL then reads
                    // `@tenantId` as the unary `@` (absolute value) operator applied to a COLUMN
                    // named `tenantid` and answers `42703: column "tenantid" does not exist`,
                    // naming the column rather than the missing parameter.
                    tenantId,
                    schoolId,
                    campusId,
                    now,
                });
        }

        result.SchoolEvents = 1;

        // ------------------------------------------------------------------
        // CLEAR, CHILDREN FIRST. All three tables carry the scope triple, so the scope-based
        // helper works - but the order matters: `eventstudentcharge` references BOTH
        // `eventparticipant` and `eventcharge`, so clearing a parent first is a 23503.
        // ------------------------------------------------------------------
        if (options.Force)
        {
            await ClearTableAsync(conn, "eventstudentcharge", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "eventparticipant", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "eventcharge", tenantId, schoolId, campusId);
        }

        var feeTypeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM feetype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        // A tax code the charge can be classified against. `eventcharge.taxcodeid` is nullable and
        // the read LEFT JOINs it for display, so a campus with none still works - but a real charge
        // carries one, and `EventFinanceController.SnapshotTaxAsync` is what fills the rate from it.
        var taxCodeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM taxcode
               WHERE tenantid IN (0, @tenantId)
               ORDER BY tenantid DESC, id LIMIT 1",
            new { tenantId });

        // ------------------------------------------------------------------
        // THE CHARGES - one per `EventChargeStatus`, so all four tabs and all four status tiles
        // read a row. The APPROVED one carries the event's whole amount: the finance screen's
        // `chargesTotalMismatch` guard only opens the invoice button when the approved charges
        // reconcile to the event's amount, and a fixture whose guard is stuck closed measures the
        // fixture rather than the screen.
        // ------------------------------------------------------------------
        var chargeNumbers = new[] { "EVTCHG-DRAFT", "EVTCHG-OPEN", "EVTCHG-APPROVED", "EVTCHG-REJECTED" };
        var descriptions = new[]
        {
            "Bus fare (draft)", "Entry fee", "Activity contribution", "Optional photograph pack",
        };
        var statuses = new short[] { 1, 2, 3, 4 };
        var amounts = new decimal[] { 120.00m, EventAmount, EventAmount, 75.00m };

        var chargeIds = new List<long>();
        var approvedChargeId = 0L;

        for (var i = 0; i < options.ChargesPerEvent && i < statuses.Length; i++)
        {
            var status = statuses[i];
            var amount = amounts[i];
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO eventcharge
                      (eventid, feetypeid, description, amount, taxcodeid, taxtreatment, taxrate, duedate,
                       ismandatory, sortorder, status, approvalstatus, approvedby, approvedon,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon, chargenumber)
                  VALUES
                      (@eventId, @feeTypeId, @description, @amount, @taxCodeId, @taxTreatment, @taxRate, @dueDate,
                       @isMandatory, @sortOrder, @status, @approvalStatus, @approvedBy, @approvedOn,
                       @tenantId, @schoolId, @campusId, 0, 0, @now, @now, @chargeNumber)
                  RETURNING id",
                new
                {
                    eventId,
                    feeTypeId,
                    description = descriptions[i],
                    amount,
                    taxCodeId = status == 3 ? taxCodeId : null,
                    taxTreatment = status == 3 ? (short)2 : (short?)null,
                    taxRate = status == 3 ? 5.00m : (decimal?)null,
                    dueDate = now.Date.AddDays(7),
                    isMandatory = i == 0,
                    sortOrder = i + 1,
                    status,
                    approvalStatus = status,
                    approvedBy = status == 3 ? 1L : (long?)null,
                    approvedOn = status == 3 ? now : (DateTime?)null,
                    chargeNumber = $"{chargeNumbers[i]}-{campusId}",
                    tenantId,
                    schoolId,
                    campusId,
                    now,
                });

            chargeIds.Add(id);
            if (status == 3) approvedChargeId = id;
        }

        result.EventCharges = chargeIds.Count;

        // ------------------------------------------------------------------
        // THE PARTICIPANTS - real enrolments of THIS campus, spread across the status vocabulary.
        // The invoice fan-out reads only the non-cancelled ones, and the screen renders a pill per
        // status, so both branches need rows.
        // ------------------------------------------------------------------
        var participantStatuses = new short[] { 1, 2, 3, 4, 5, 6 }; // Invited/Registered/Confirmed/Cancelled/Attended/NoShow
        var participantIds = new List<(long Id, long EnrollmentId, short Status)>();

        for (var i = 0; i < enrollments.Count; i++)
        {
            var enrollmentId = enrollments[i];
            var status = participantStatuses[i % participantStatuses.Length];

            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO eventparticipant
                      (eventid, studentenrollmentid, status, registeredon, cancelledon, remarks,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@eventId, @enrollmentId, @status, @registeredOn, @cancelledOn, @remarks,
                       @tenantId, @schoolId, @campusId, 0, 0, @now, @now)
                  RETURNING id",
                new
                {
                    eventId,
                    enrollmentId,
                    status,
                    registeredOn = now.Date.AddDays(-14 + (i % 10)),
                    cancelledOn = status == 4 ? now.Date.AddDays(-3) : (DateTime?)null,
                    remarks = status == 4 ? "Withdrew before the payment window closed" : null,
                    tenantId,
                    schoolId,
                    campusId,
                    now,
                });

            participantIds.Add((id, enrollmentId, status));
        }

        result.EventParticipants = participantIds.Count;

        // ------------------------------------------------------------------
        // THE PER-STUDENT CHARGES. Raised against the APPROVED charge only - `GetForInvoicing`
        // filters `status = 3 AND amount > 0`, and the fan-out `EventAdHocInvoiceBl` refuses a
        // charge that is not approved. Every row is Pending(1) with a NULL invoice, which is the
        // state `GetPendingByEventId` reads and the only state the generator consumes - an
        // Invoiced row would leave that read empty while the table looked populated.
        // ------------------------------------------------------------------
        if (approvedChargeId > 0)
        {
            var raised = 0;

            for (var i = 0; i < participantIds.Count && raised < options.StudentChargesPerEvent; i++)
            {
                var participant = participantIds[i];

                // Cancelled participants are charged nothing - that is the module's rule, not a
                // simplification, and seeding one would make the read disagree with the engine.
                if (participant.Status == 4) continue;

                await conn.ExecuteAsync(
                    @"INSERT INTO eventstudentcharge
                          (eventparticipantid, eventchargeid, studentenrollmentid,
                           grossamount, discountamount, taxableamount, taxamount, totalamount,
                           invoiceid, status, taxexemptionpercentage,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES
                          (@participantId, @chargeId, @enrollmentId,
                           @gross, 0, @gross, 0, @gross,
                           NULL, 1, NULL,
                           @tenantId, @schoolId, @campusId, 0, 0, @now, @now)
                      ON CONFLICT (eventchargeid, studentenrollmentid) DO NOTHING",
                    new
                    {
                        participantId = participant.Id,
                        chargeId = approvedChargeId,
                        enrollmentId = participant.EnrollmentId,
                        gross = EventAmount,
                        tenantId,
                        schoolId,
                        campusId,
                        now,
                    });

                raised++;
            }

            result.EventStudentCharges = raised;
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  Event finance: campus {campusId} event {eventId} - {result.EventCharges} charges, " +
                $"{result.EventParticipants} participants, {result.EventStudentCharges} student charges");
        }

        return result;
    }

    /// <summary>
    /// A campus's own counts, so the skip path reports what it holds rather than zero.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, EventFinanceSeedResult result)
    {
        var counts = await conn.QueryFirstOrDefaultAsync<EventFinanceCountsRow>(
            @"SELECT
                  (SELECT COUNT(*) FROM eventcharge
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS EventCharges,
                  (SELECT COUNT(*) FROM eventparticipant
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS EventParticipants,
                  (SELECT COUNT(*) FROM eventstudentcharge
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) AS EventStudentCharges",
            new { tenantId, schoolId, campusId });

        if (counts is null) return;

        result.SchoolEvents = 1;
        result.EventCharges = counts.EventCharges;
        result.EventParticipants = counts.EventParticipants;
        result.EventStudentCharges = counts.EventStudentCharges;
    }

    private sealed class EventFinanceCountsRow
    {
        public int EventCharges { get; set; }
        public int EventParticipants { get; set; }
        public int EventStudentCharges { get; set; }
    }

    private sealed class EventPeriodRow
    {
        public long AcademicYearId { get; set; }
        public long TermId { get; set; }
    }
}
