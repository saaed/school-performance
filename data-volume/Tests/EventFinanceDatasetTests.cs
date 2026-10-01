using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the EVENT FINANCE chain (`schoolevent` -> `eventcharge` -> `eventparticipant` ->
/// `eventstudentcharge`) and then asserts the joins that decide whether its three screens measure
/// anything.
///
/// ⚠️ ALL THREE FINANCE TABLES WERE EMPTY IN EVERY DATABASE HERE, WHILE THREE SHIPPED SCREENS READ
/// THEM. `school.event.finance.html` pages the charges and counts them into four status tiles,
/// `school.event.detail.html` pages the participants, and the invoice-generation job fans an
/// APPROVED charge out over the event's non-cancelled participants. A spec over any of them would
/// report SKIP - the "reads as coverage while measuring nothing" failure this tool exists to prevent.
///
/// ⚠️ THE ASSERTIONS ARE THE QUERIES' OWN PREDICATES, NOT COUNTS. Every one of the three reads
/// reaches its subject through a JOIN, so a row can exist in its table and be invisible on the
/// screen that lists it:
///
///   * `EventParticipantRepository.GetByEventId`/`GetByEventIdPaged` INNER JOIN
///     `StudentEnrollment -> Student` (LEFT JOIN `Classroom`), so a participant whose enrollment is
///     at another campus, or whose enrollment has no student, EXISTS and can never render;
///   * `EventStudentChargeRepository.GetByEventId` INNER JOINs BOTH `EventCharge` (for the
///     description) and `StudentEnrollment -> Student` (for the name) and LEFT JOINs `Invoices`
///     (for the number) - a row whose charge was deleted renders nothing;
///   * `GetPendingByEventId` adds `status = Pending AND invoiceid IS NULL`, so a table of Invoiced
///     rows leaves the generator's own read empty while the table looks populated;
///   * `EventChargeRepository.GetForInvoicing` adds `amount > 0 AND status = Approved`, and the
///     finance screen's `chargesTotalMismatch` guard only opens when the APPROVED charges
///     reconcile to the event's amount - a fixture whose guard is stuck closed measures the fixture.
///
/// Opt in with the same flag the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~EventFinanceDataset"
///
/// ⚠️ IT DEPENDS ON THE ENROLMENTS (`PerfDatasetTests`). A campus with no enrolled student is
/// REPORTED rather than thrown, so a fixture can seed the campuses that can be seeded.
/// </summary>
public sealed class EventFinanceDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public EventFinanceDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Event_finance_dataset_fills_every_read_the_charge_participant_and_invoice_paths_make()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the event-finance perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed event finance into - " +
            "run PerfDatasetTests first");

        var options = new EventFinanceSeedOptions
        {
            ChargesPerEvent = SeedCampuses.EnvInt("SCUBE_PERF_EVENTFIN_CHARGES", 4),
            ParticipantsPerEvent = SeedCampuses.EnvInt("SCUBE_PERF_EVENTFIN_PARTICIPANTS", 120),
            StudentChargesPerEvent = SeedCampuses.EnvInt("SCUBE_PERF_EVENTFIN_STUDENTCHARGES", 120),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding EVENT FINANCE for {campusIds.Count} campus(es) [{string.Join(", ", campusIds)}]: " +
                          $"{options.ChargesPerEvent} charges, {options.ParticipantsPerEvent} participants, " +
                          $"{options.StudentChargesPerEvent} student charges");
        _output.WriteLine("");

        var seeder = new EventFinanceSeeder(_connectionString);
        var seededCampusIds = new List<long>();
        var totalCharges = 0;
        var totalStudentCharges = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                // A campus that HOLDS charges is still worth asserting - the skip path reads the
                // counts back, so the assertions below run against what it really has.
                if (result.EventCharges > 0) seededCampusIds.Add(campusId);
                continue;
            }

            totalCharges += result.EventCharges;
            totalStudentCharges += result.EventStudentCharges;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.SchoolEvents,2} event(s) {result.EventCharges,3} charges " +
                $"{result.EventParticipants,5} participants {result.EventStudentCharges,5} student charges");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalCharges} event charges, {totalStudentCharges} per-student charges");

        Assert.True(seededCampusIds.Count > 0,
            "no campus holds event charges - every campus was skipped for a missing prerequisite, so the " +
            "event-finance specs would still measure an empty table");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. These tables held ZERO rows, so the planner's statistics
        // describe an empty table - and this repo has already paid for that twice. The list is the
        // SEEDER's own declaration, so what is analyzed is what it wrote.
        foreach (var table in EventFinanceSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertThePaidEventCanReachItsChargesAsync(conn, seededCampusIds);
        await AssertTheInvoicingGuardsAreSatisfiableAsync(conn, seededCampusIds);
        await AssertEveryParticipantResolvesToThisCampusAsync(conn, seededCampusIds);
        await AssertEveryStudentChargeResolvesItsWholeChainAsync(conn, seededCampusIds);
        await AssertNoSentinelTimestampsAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// ⚠️ THE CHARGES ARE FOUND THROUGH THE EVENT, SO THE EVENT MUST BE ONE THE SCREEN CAN OPEN.
    /// `EventChargeRepository.GetByEventId` filters `ec.eventid` alone, and
    /// `school.event.finance.html` is reached from the event DETAIL page - which only renders for a
    /// PAID event (`ispaidevent`), because a free event bounces back. So the assertion is both
    /// halves: the charge's event resolves at this scope, and that event is paid.
    /// </summary>
    private static async Task AssertThePaidEventCanReachItsChargesAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventcharge
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `eventcharge` row at its own scope - " +
                "`school.event.finance.html` pages the charges of the event it was opened from");

            var orphans = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventcharge ec
                   WHERE ec.tenantid = @tenantId AND ec.schoolid = @schoolId AND ec.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM schoolevent e WHERE e.id = ec.eventid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphans == 0,
                $"campus {campusId} holds {orphans} event charge(s) whose `eventid` resolves to nothing - " +
                "the charges are read BY EVENT, so these are unreachable rows");

            // The finance screen's own entry gate: a FREE event bounces back to the detail page.
            var freeEventCharges = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventcharge ec
                    JOIN schoolevent e ON e.id = ec.eventid
                   WHERE ec.tenantid = @tenantId AND ec.schoolid = @schoolId AND ec.campusid = @campusId
                     AND ec.eventid = (SELECT id FROM schoolevent
                                        WHERE code = @code
                                          AND tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
                     AND e.ispaidevent = FALSE",
                new
                {
                    tenantId = SeedCampuses.TenantId,
                    schoolId = SeedCampuses.SchoolId,
                    campusId,
                    code = EventFinanceSeeder.EventCodeFor(campusId),
                });

            Assert.True(freeEventCharges == 0,
                $"campus {campusId}'s perf event is FREE (`ispaidevent = FALSE`) while carrying " +
                $"{freeEventCharges} charge(s) - `school.event.finance.html` bounces a free event back, so no " +
                "screen can reach them");

            // The status vocabulary the four tiles + four tabs read. A value outside 1-4 lands in no
            // tile and no tab: the counts add up to less than the total.
            var unknownStatus = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventcharge
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status NOT IN (1, 2, 3, 4)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unknownStatus == 0,
                $"campus {campusId} holds {unknownStatus} charge(s) whose `status` is outside the " +
                "`EventChargeStatus` enum (1-4) - `GetByEventId`'s CASE renders the bare number and the " +
                "tiles' four counts no longer add up to the total");
        }
    }

    /// <summary>
    /// ⚠️ A GUARD THAT CAN NEVER OPEN IS WORSE THAN NO GUARD, and this module has two of them.
    /// `GetForInvoicing` needs a charge with `amount > 0 AND status = Approved`, and the finance
    /// screen's Generate button additionally needs the APPROVED charges to RECONCILE to the event's
    /// amount (`chargesTotalMismatch`). Seeding an approved charge of zero, or one that disagrees
    /// with the event, leaves a correct screen permanently disabled - and the spec would then measure
    /// a closed guard rather than the query.
    /// </summary>
    private static async Task AssertTheInvoicingGuardsAreSatisfiableAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var eventId = await conn.ExecuteScalarAsync<long>(
                @"SELECT id FROM schoolevent
                   WHERE code = @code
                     AND tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new
                {
                    tenantId = SeedCampuses.TenantId,
                    schoolId = SeedCampuses.SchoolId,
                    campusId,
                    code = EventFinanceSeeder.EventCodeFor(campusId),
                });

            Assert.True(eventId > 0,
                $"campus {campusId} holds no perf event with code `{EventFinanceSeeder.EventCodeFor(campusId)}`");

            // `GetForInvoicing`'s own predicate, repeated verbatim.
            var invoiceable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventcharge
                   WHERE eventid = @eventId AND amount > 0 AND status = 3",
                new { eventId });

            Assert.True(invoiceable > 0,
                $"campus {campusId}'s perf event holds no charge with `amount > 0 AND status = Approved` - " +
                "`EventChargeRepository.GetForInvoicing` returns nothing, so the invoice button can never open");

            var approvedTotal = await conn.ExecuteScalarAsync<decimal>(
                @"SELECT COALESCE(SUM(amount), 0) FROM eventcharge WHERE eventid = @eventId AND status = 3",
                new { eventId });

            var eventAmount = await conn.ExecuteScalarAsync<decimal>(
                @"SELECT amount FROM schoolevent WHERE id = @eventId", new { eventId });

            Assert.True(approvedTotal == eventAmount,
                $"campus {campusId}'s APPROVED charges total {approvedTotal:0.00} while the event's amount is " +
                $"{eventAmount:0.00} - `school.event.finance.html`'s `chargesTotalMismatch` guard keeps Generate " +
                "disabled until they reconcile");

            // The four status tiles / tabs. Fewer distinct statuses than the enum means a tab that is
            // correct AND empty, which reads as a broken screen.
            var distinctStatuses = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT status) FROM eventcharge WHERE eventid = @eventId", new { eventId });

            Assert.True(distinctStatuses >= 4,
                $"campus {campusId}'s perf event carries only {distinctStatuses} distinct charge status(es) - " +
                "the finance screen renders one tab and one count tile per `EventChargeStatus`, so the rest " +
                "read a correct zero");
        }
    }

    /// <summary>
    /// ⚠️ A PARTICIPANT'S SCOPE COMES FROM ITS ENROLMENT, NOT FROM ITSELF, AND THE LIST INNER JOINS
    /// IT. `eventparticipant` carries the scope triple, but the rows that matter are reached through
    /// `INNER JOIN StudentEnrollment se ON ep.studentenrollmentid = se.id INNER JOIN Student s ON
    /// se.studentid = s.id` - so a participant pointing at a deleted enrolment (or an enrolment with
    /// no student) is a row the event detail page can never render, and the table looks populated.
    /// The status vocabulary is asserted for the same reason: the fan-out skips Cancelled rows, so a
    /// campus of all-Cancelled participants leaves the generator with nothing to bill.
    /// </summary>
    private static async Task AssertEveryParticipantResolvesToThisCampusAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventparticipant ep
                   WHERE ep.tenantid = @tenantId AND ep.schoolid = @schoolId AND ep.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM studentenrollment se
                             JOIN student s ON s.id = se.studentid
                            WHERE se.id = ep.studentenrollmentid
                              AND se.tenantid = @tenantId AND se.schoolid = @schoolId
                              AND se.campusid = @campusId)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} participant row(s) whose `studentenrollmentid` does not " +
                "resolve to a student of THIS campus - the participant list INNER JOINs " +
                "StudentEnrollment -> Student, so these are unreachable while the table looks populated");

            var billable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventparticipant
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status <> 4",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(billable > 0,
                $"campus {campusId}'s participants are ALL Cancelled (status = 4) - the invoice fan-out skips " +
                "them, so there is nothing for the money chain to bill");
        }
    }

    /// <summary>
    /// ⚠️ THE PER-STUDENT CHARGE IS A THREE-WAY JOIN AND A PENDING STATE, AND BOTH HALVES ARE
    /// ASSERTED. `GetByEventId` reaches the scope through `EventCharge` and the name through
    /// `StudentEnrollment -> Student`; `GetPendingByEventId` additionally requires
    /// `status = Pending AND invoiceid IS NULL`. A row whose participant's enrolment disagrees with
    /// its own `studentenrollmentid` renders the WRONG student's name - the read resolves the name
    /// off the enrollment, not off the charge's participant - which is a silent data defect a count
    /// cannot see.
    /// </summary>
    private static async Task AssertEveryStudentChargeResolvesItsWholeChainAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventstudentcharge
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `eventstudentcharge` row - `EventStudentChargeRepository.GetByEventId` " +
                "returns nothing, so the invoice fan-out has nothing to consume");

            var brokenChain = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventstudentcharge esc
                   WHERE esc.tenantid = @tenantId AND esc.schoolid = @schoolId AND esc.campusid = @campusId
                     AND (NOT EXISTS (SELECT 1 FROM eventcharge ec WHERE ec.id = esc.eventchargeid)
                       OR NOT EXISTS (SELECT 1 FROM eventparticipant ep WHERE ep.id = esc.eventparticipantid)
                       OR NOT EXISTS (SELECT 1 FROM studentenrollment se
                                        JOIN student s ON s.id = se.studentid
                                       WHERE se.id = esc.studentenrollmentid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(brokenChain == 0,
                $"campus {campusId} holds {brokenChain} per-student charge(s) whose charge, participant or " +
                "enrollment does not resolve - the detail read INNER JOINs all three, so these rows never render");

            // ⚠️ The charge's enrollment and its participant's enrollment must be THE SAME ROW. The
            // read takes the student's name off `esc.studentenrollmentid` while the participant is
            // the row the fan-out wrote - a mismatch renders one student's name against another
            // student's charge, and `ux_eventstudentcharge_chargeenrollment` does not catch it.
            var mismatched = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventstudentcharge esc
                    JOIN eventparticipant ep ON ep.id = esc.eventparticipantid
                   WHERE esc.tenantid = @tenantId AND esc.schoolid = @schoolId AND esc.campusid = @campusId
                     AND ep.studentenrollmentid <> esc.studentenrollmentid",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(mismatched == 0,
                $"campus {campusId} holds {mismatched} per-student charge(s) whose `studentenrollmentid` " +
                "disagrees with its participant's - the read renders the student named by the CHARGE and the " +
                "participant named by the PARTICIPANT, so the row would show the wrong student");

            // `GetPendingByEventId`'s own predicate: the read the invoice generator consumes.
            var eventId = await conn.ExecuteScalarAsync<long>(
                @"SELECT id FROM schoolevent
                   WHERE code = @code
                     AND tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new
                {
                    tenantId = SeedCampuses.TenantId,
                    schoolId = SeedCampuses.SchoolId,
                    campusId,
                    code = EventFinanceSeeder.EventCodeFor(campusId),
                });

            var pending = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM eventstudentcharge esc
                    JOIN eventcharge ec ON ec.id = esc.eventchargeid
                   WHERE ec.eventid = @eventId AND esc.status = 1 AND esc.invoiceid IS NULL",
                new { eventId });

            Assert.True(pending > 0,
                $"campus {campusId}'s perf event holds no PENDING, un-invoiced student charge - " +
                "`GetPendingByEventId` (the read the invoice generator consumes) returns nothing, so the " +
                "table is populated while the money chain is empty");
        }
    }

    /// <summary>
    /// The `-infinity` sentinel ties every `ORDER BY CreatedOn` and makes "the newest row"
    /// arbitrary - the defect this repo already fixed once in <c>GenericRepository</c>. A seeding
    /// path is exactly where it comes back, because ids and timestamps are written by hand.
    /// </summary>
    private static async Task AssertNoSentinelTimestampsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var bad = await conn.ExecuteScalarAsync<long>(
                @"SELECT
                    (SELECT COUNT(*) FROM eventcharge
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM eventparticipant
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM eventstudentcharge
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (createdon = '-infinity'::timestamp OR modifiedon = '-infinity'::timestamp))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(bad == 0,
                $"campus {campusId} holds {bad} row(s) stamped `-infinity` - the sentinel that ties every " +
                "`ORDER BY CreatedOn` and makes the newest row arbitrary");
        }
    }
}
