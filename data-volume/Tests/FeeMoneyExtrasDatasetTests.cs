using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the FEE MONEY module's six empty tables (`adhoccharge` + its student/classroom rows,
/// `discountinvoices`, `taxexemption`, `refund`) and then asserts the joins that decide whether their
/// screens measure anything.
///
/// ⚠️ THREE OF THE SIX ARE PAGED GRIDS AND ALL SIX WERE EMPTY IN EVERY DATABASE HERE, so a spec over
/// any of them reported SKIP - which reads as "not measured yet" for a screen the application ships.
///
/// ⚠️ THE ASSERTIONS ARE THE READS' OWN PREDICATES, NOT COUNTS, because every one of these tables is
/// reached through a JOIN:
///
///   * `studentadhoccharge` is read by `sa.adhocchargeid` and the scope comes from the CHARGE, so a
///     row whose charge is elsewhere is counted and unreachable;
///   * the same read INNER JOINs `Student` (for the name) and LEFT JOINs `invoices` (for the number),
///     so a row with no student renders a blank;
///   * `taxexemption`'s grid INNER JOINs `Student` AND `TaxCode`, so a row missing either is invisible
///     no matter how many exemptions the table holds;
///   * `discountinvoices` is read BY INVOICE (`GetDiscountByInvoiceId`), so a row whose invoice is at
///     another scope belongs to a screen that will never open it;
///   * `refund`'s list INNER JOINs `Student` and `Payment` (`ON DELETE RESTRICT`), so a refund whose
///     pair does not resolve cannot be rendered at all.
///
/// Opt in with the same flag the other dataset fixtures use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~FeeMoneyExtrasDataset"
///
/// ⚠️ IT DEPENDS ON `FeesModuleSeeder` (`feetype`, `invoices`, `payment`) and `MasterDataSeeder`
/// (`taxcode`, `discount`), plus the dataset's own students/enrolments. A campus missing a
/// prerequisite is REPORTED in a sentence rather than thrown, so this fixture seeds what it can.
/// </summary>
public sealed class FeeMoneyExtrasDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public FeeMoneyExtrasDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Fee_money_dataset_fills_every_read_the_adhoc_exemption_and_refund_screens_make()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the fee-money perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed fee money into - " +
            "run PerfDatasetTests first");

        var options = new FeeMoneyExtrasSeedOptions
        {
            AdHocChargesPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_FEE_CHARGES", 4),
            StudentsPerCharge = SeedCampuses.EnvInt("SCUBE_PERF_FEE_CHARGE_STUDENTS", 6),
            ClassroomsPerCharge = SeedCampuses.EnvInt("SCUBE_PERF_FEE_CHARGE_CLASSROOMS", 2),
            TaxExemptionsPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_FEE_EXEMPTIONS", 8),
            DiscountedInvoicesPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_FEE_DISCOUNTS", 12),
            RefundsPerCampus = SeedCampuses.EnvInt("SCUBE_PERF_FEE_REFUNDS", 4),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding FEE MONEY for {campusIds.Count} campus(es) [{string.Join(", ", campusIds)}]: " +
                          $"{options.AdHocChargesPerCampus} charges x {options.StudentsPerCharge} students, " +
                          $"{options.TaxExemptionsPerCampus} exemptions, " +
                          $"{options.DiscountedInvoicesPerCampus} invoice discounts, " +
                          $"{options.RefundsPerCampus} refunds");
        _output.WriteLine("");

        var seeder = new FeeMoneyExtrasSeeder(_connectionString);
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            if (result.Skipped)
            {
                _output.WriteLine($"  campus {campusId,-5} SKIPPED: {result.SkipReason ?? "already seeded"}");
                // A campus that HOLDS charges is still worth asserting - the skip path reads the
                // counts back, so the assertions run against what it really has.
                if (result.AdHocCharges > 0) seededCampusIds.Add(campusId);
                continue;
            }

            _output.WriteLine(
                $"  campus {campusId,-5} {result.AdHocCharges,3} charges {result.StudentAdHocCharges,4} student rows " +
                $"{result.ClassroomAdHocCharges,3} classroom rows {result.TaxExemptions,3} exemptions " +
                $"{result.DiscountInvoices,3} invoice discounts {result.Refunds,3} refunds");

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        Assert.True(seededCampusIds.Count > 0,
            "no campus holds ad-hoc charges - every campus was skipped for a missing prerequisite, so the " +
            "adhoc/refund specs would still measure an empty table");

        // ⚠️ ANALYZE BEFORE ANYONE MEASURES. Every one of these tables held ZERO rows, so the planner's
        // statistics describe an empty table - and this repo has already paid for that twice.
        foreach (var table in FeeMoneyExtrasSeeder.TablesToAnalyze)
        {
            await conn.ExecuteAsync($"ANALYZE {table}");
        }

        await AssertTheChargeGridsStatusesAndReferencesAsync(conn, seededCampusIds);
        await AssertEveryStudentChargeRowIsReachableAsync(conn, seededCampusIds);
        await AssertTheClassroomChargeRowsResolveAsync(conn, seededCampusIds);
        await AssertTheExemptionGridsJoinsResolveAsync(conn, seededCampusIds);
        await AssertEveryInvoiceDiscountBelongsToThisCampusAsync(conn, seededCampusIds);
        await AssertTheRefundListJoinsAndIndexesHoldAsync(conn, seededCampusIds);
    }

    /// <summary>
    /// The charge grid is PAGED and its tabs separate the statuses, so a campus whose charges are all
    /// one status has tabs that can never be exercised. ⚠️ Both FKs are checked too - the grid's row
    /// itself renders `FeeTypeName`, and the invoice generator resolves the fee type by id.
    /// </summary>
    private static async Task AssertTheChargeGridsStatusesAndReferencesAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var total = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM adhoccharge
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(total > 0,
                $"campus {campusId} holds no `adhoccharge` row at its own scope - " +
                "`POST fee/adhocCharge/list` filters the scope, so the grid is empty");

            // ⚠️ THE STATUS VOCABULARY IS THE SCREEN'S TABS. `AdHocChargeStatus` defines 1..6 and the
            // page separates Draft / Submitted / Approved / Issued; a value outside the enum renders a
            // blank badge.
            var unknownStatus = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM adhoccharge
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND (status < 1 OR status > 6)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unknownStatus == 0,
                $"campus {campusId} holds {unknownStatus} charge(s) whose `status` is outside the " +
                "`AdHocChargeStatus` enum (1-6) - the grid's status badge renders blank");

            var distinctStatuses = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT status) FROM adhoccharge
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(distinctStatuses >= 2,
                $"campus {campusId} spread its charges over only {distinctStatuses} status - every status tab " +
                "of the grid but one is empty, so the filters measure a single predicate");

            var brokenFk = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM adhoccharge ac
                   WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId
                     AND ((ac.feetypeid IS NOT NULL
                           AND NOT EXISTS (SELECT 1 FROM feetype f WHERE f.id = ac.feetypeid))
                       OR (ac.academicyearid IS NOT NULL
                           AND NOT EXISTS (SELECT 1 FROM academicyear ay WHERE ay.id = ac.academicyearid)))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(brokenFk == 0,
                $"campus {campusId} holds {brokenFk} charge row(s) whose `feetypeid` or `academicyearid` does " +
                "not resolve - the invoice generator resolves the fee type by id and refuses the charge");
        }
    }

    /// <summary>
    /// ⚠️ THE CHARGE'S DETAIL READ REACHES THE SCOPE THROUGH THE CHARGE, AND ITS NAME THROUGH
    /// `Student`. `GetWithDetailsByChargeId` is `studentadhoccharge INNER JOIN student
    /// INNER JOIN adhoccharge` + `LEFT JOIN invoices`, so a row whose student does not resolve is
    /// INVISIBLE (an INNER JOIN drops it) while the table looks populated.
    /// </summary>
    private static async Task AssertEveryStudentChargeRowIsReachableAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var reachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentadhoccharge sa
                    INNER JOIN student s ON sa.studentid = s.id
                    INNER JOIN adhoccharge ac ON ac.id = sa.adhocchargeid
                   WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(reachable > 0,
                $"campus {campusId} holds no `studentadhoccharge` row reachable through its charge AND student - " +
                "the charge dialog's own read INNER JOINs both, so it lists nothing");

            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentadhoccharge sa
                   WHERE NOT EXISTS (
                           SELECT 1 FROM adhoccharge ac
                            WHERE ac.id = sa.adhocchargeid
                              AND ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId)
                      OR NOT EXISTS (SELECT 1 FROM student s WHERE s.id = sa.studentid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} student-charge row(s) whose CHARGE or STUDENT does not " +
                "resolve - the read joins both, so these are counted in the table and dropped from the list");

            // Every charge the grid pages over must have rows, or the dialog it opens is empty.
            var chargesWithNoStudents = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM adhoccharge ac
                   WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM studentadhoccharge sa WHERE sa.adhocchargeid = ac.id)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(chargesWithNoStudents == 0,
                $"campus {campusId} holds {chargesWithNoStudents} charge(s) with ZERO student rows - the row's " +
                "own detail dialog opens empty");

            // The invoiced row is what exercises the LEFT JOIN and the partial unique index.
            var invoiced = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentadhoccharge sa
                    JOIN adhoccharge ac ON ac.id = sa.adhocchargeid
                   WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId
                     AND sa.invoiceid IS NOT NULL",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(invoiced > 0,
                $"campus {campusId} has no INVOICED student-charge row - the read's `LEFT JOIN invoices` " +
                "resolves for every row, so a column that can never be filled goes untested");

            var danglingInvoice = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentadhoccharge sa
                    JOIN adhoccharge ac ON ac.id = sa.adhocchargeid
                   WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId
                     AND sa.invoiceid IS NOT NULL
                     AND NOT EXISTS (SELECT 1 FROM invoices i WHERE i.id = sa.invoiceid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(danglingInvoice == 0,
                $"campus {campusId} holds {danglingInvoice} student-charge row(s) whose `invoiceid` does not " +
                "resolve - the list's invoice number renders blank");
        }
    }

    /// <summary>
    /// The classroom variant is read by charge id with BOTH foreign keys NOT NULL, so a row whose
    /// classroom is at another scope is a row the classroom filter can never return.
    /// </summary>
    private static async Task AssertTheClassroomChargeRowsResolveAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var broken = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM classroomadhoccharge ca
                    JOIN adhoccharge ac ON ac.id = ca.adhocchargeid
                   WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId
                     AND NOT EXISTS (
                           SELECT 1 FROM classroom c
                            WHERE c.id = ca.classroomid
                              AND c.tenantid = @tenantId AND c.schoolid = @schoolId AND c.campusid = @campusId)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(broken == 0,
                $"campus {campusId} holds {broken} classroom-charge row(s) whose CLASSROOM is outside the " +
                "campus scope - the classroom-scoped dialog can never return them");
        }
    }

    /// <summary>
    /// ⚠️ THE EXEMPTION GRID INNER JOINs `Student` AND `TaxCode` AND LEFT JOINs TWO MORE TABLES, so it
    /// is the read with the most ways to be silently empty. `GetTaxExemptionByStudentId` is the
    /// per-student read the fee screens use, keyed on the student rather than the scope.
    /// </summary>
    private static async Task AssertTheExemptionGridsJoinsResolveAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var reachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT te.id) FROM taxexemption te
                    INNER JOIN student s ON te.studentid = s.id
                    INNER JOIN taxcode t ON te.taxcodeid = t.id
                   WHERE te.tenantid = @tenantId AND te.schoolid = @schoolId AND te.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(reachable > 0,
                $"campus {campusId} holds no `taxexemption` row reachable through BOTH its student and its tax " +
                "code - `GET taxExemption` pages over exactly that double INNER JOIN, so the grid is empty");

            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM taxexemption te
                   WHERE te.tenantid = @tenantId AND te.schoolid = @schoolId AND te.campusid = @campusId
                     AND (NOT EXISTS (SELECT 1 FROM student s WHERE s.id = te.studentid)
                          OR NOT EXISTS (SELECT 1 FROM taxcode t WHERE t.id = te.taxcodeid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} exemption row(s) whose student or tax code does not " +
                "resolve - both are INNER JOINs, so those rows are dropped from the grid entirely");

            // The per-student read is keyed on the student, so at least one must carry a row.
            var studentsWithExemption = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT te.studentid) FROM taxexemption te
                   WHERE te.tenantid = @tenantId AND te.schoolid = @schoolId AND te.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(studentsWithExemption > 0,
                $"campus {campusId} has no student carrying an exemption - `GetTaxExemptionByStudentId` " +
                "returns nothing for every student on the fee screens");
        }
    }

    /// <summary>
    /// The invoice-discount read is `FindAsync(x =&gt; x.InvoiceId == invoiceId)` - BY INVOICE, with no
    /// scope predicate - so the only thing that keeps it inside the campus is the invoice it hangs off.
    /// </summary>
    private static async Task AssertEveryInvoiceDiscountBelongsToThisCampusAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var reachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM discountinvoices di
                    INNER JOIN invoices i ON i.id = di.invoiceid
                   WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(reachable > 0,
                $"campus {campusId} holds no `discountinvoices` row against one of its own invoices - the " +
                "invoice detail's discount list is empty");

            var broken = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM discountinvoices di
                    INNER JOIN invoices i ON i.id = di.invoiceid
                   WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM discount d WHERE d.id = di.discountid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(broken == 0,
                $"campus {campusId} holds {broken} invoice-discount row(s) whose DISCOUNT does not resolve - " +
                "the detail list renders a nameless discount");
        }
    }

    /// <summary>
    /// ⚠️ THE REFUND LIST IS THE MOST JOIN-DEPENDENT READ IN THE BATCH: `refund INNER JOIN student
    /// INNER JOIN payment`, plus the two PARTIAL UNIQUE indexes that make a naive seed a `23505`.
    /// Both are asserted here as INVARIANTS rather than left to the seeder's care, because a
    /// duplicate is the failure a re-run produces and it aborts the whole load.
    /// </summary>
    private static async Task AssertTheRefundListJoinsAndIndexesHoldAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var reachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM refund r
                    INNER JOIN student s ON s.id = r.studentid
                    INNER JOIN payment p ON p.id = r.paymentid
                   WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(reachable > 0,
                $"campus {campusId} holds no `refund` row reachable through BOTH its student and its payment - " +
                "the dashboard's list INNER JOINs both, so it is empty");

            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM refund r
                   WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                     AND (NOT EXISTS (SELECT 1 FROM student s WHERE s.id = r.studentid)
                          OR NOT EXISTS (SELECT 1 FROM payment p WHERE p.id = r.paymentid))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} refund row(s) whose student or payment does not resolve " +
                "- both are INNER JOINs, so those rows never appear");

            // The refund's student MUST be the payment's own student, or the list describes a refund
            // of somebody else's money.
            var mismatched = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM refund r
                    INNER JOIN payment p ON p.id = r.paymentid
                   WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                     AND p.studentid <> r.studentid",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(mismatched == 0,
                $"campus {campusId} holds {mismatched} refund row(s) whose student differs from the payment's " +
                "own student - the list renders one person's name against another person's receipt");

            // ⚠️ `ux_refund_number` and the two partial indexes are the re-run trap. Asserted as three
            // separate invariants so a failure names WHICH unique key was violated.
            var duplicateNumbers = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM (
                      SELECT tenantid, refundnumber FROM refund
                       WHERE tenantid = @tenantId
                       GROUP BY tenantid, refundnumber HAVING COUNT(*) > 1) d",
                new { tenantId = SeedCampuses.TenantId });

            Assert.True(duplicateNumbers == 0,
                $"tenant {SeedCampuses.TenantId} holds {duplicateNumbers} duplicated `refundnumber` value(s) - " +
                "`ux_refund_number` is UNIQUE(tenantid, refundnumber), so a re-run raises 23505");

            var duplicateReferences = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM (
                      SELECT tenantid, paymentid, requestreference FROM refund
                       WHERE requestreference IS NOT NULL
                       GROUP BY tenantid, paymentid, requestreference HAVING COUNT(*) > 1) d");

            Assert.True(duplicateReferences == 0,
                $"the refund table holds {duplicateReferences} duplicated (payment, request reference) pair(s) - " +
                "`ux_refund_requestreference` is a PARTIAL UNIQUE index over exactly that pair");

            // The status spread is what the dashboard's stat tiles and its status filter read.
            var distinctStatuses = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT status) FROM refund
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(distinctStatuses >= 2,
                $"campus {campusId} spread its refunds over only {distinctStatuses} status - the dashboard's " +
                "status filter and its stat tiles measure a single bucket");

            var unknownStatus = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM refund
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND (status < 1 OR status > 7)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unknownStatus == 0,
                $"campus {campusId} holds {unknownStatus} refund(s) whose `status` is outside `RefundStatus` " +
                "(1-7) - the grid's status text renders blank");
        }
    }
}
