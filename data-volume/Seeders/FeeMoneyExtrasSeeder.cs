using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="FeeMoneyExtrasSeeder"/>.</summary>
public sealed class FeeMoneyExtrasSeedOptions
{
    /// <summary>
    /// Ad-hoc charges per campus. Four, one per status the grid's tabs separate (Draft / Submitted /
    /// Approved / Issued) - a single row would leave every other tab empty and the status vocabuulary
    /// unexercised. A campus raises charges a term, not thousands, so this is the honest size.
    /// </summary>
    public int AdHocChargesPerCampus { get; set; } = 4;

    /// <summary>
    /// Student rows per charge - the charge's detail dialog, which is the only place these are read.
    /// Six is one classroom's worth, which is what a charge is normally raised against.
    /// </summary>
    public int StudentsPerCharge { get; set; } = 6;

    /// <summary>Classroom rows per charge - the classroom-scoped variant of the same dialog.</summary>
    public int ClassroomsPerCharge { get; set; } = 2;

    /// <summary>
    /// Tax exemptions per campus. The grid is PAGED, so this is the row count its first page pages
    /// over; an exemption is per PERSON, so a school holds tens, not thousands.
    /// </summary>
    public int TaxExemptionsPerCampus { get; set; } = 8;

    /// <summary>
    /// Discounts applied to an invoice. One per invoice is the real shape (an invoice carries the
    /// discounts its fee structure resolved), and the read is keyed on the invoice.
    /// </summary>
    public int DiscountedInvoicesPerCampus { get; set; } = 12;

    /// <summary>
    /// Refunds per campus, one per status the dashboard separates (Pending / Approved / Completed /
    /// Rejected). A refund is an exceptional event - four is already a busy term.
    /// </summary>
    public int RefundsPerCampus { get; set; } = 4;

    /// <summary>Re-seed even when the campus already holds ad-hoc charges.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's fee-money extras seed produced.</summary>
public sealed class FeeMoneyExtrasSeedResult
{
    public bool Skipped { get; set; }

    /// <summary>Why a campus was skipped, in a sentence a fixture can print.</summary>
    public string? SkipReason { get; set; }

    public int AdHocCharges { get; set; }
    public int StudentAdHocCharges { get; set; }
    public int ClassroomAdHocCharges { get; set; }
    public int TaxExemptions { get; set; }
    public int DiscountInvoices { get; set; }
    public int Refunds { get; set; }
}

/// <summary>
/// Seeds the FEE MONEY tables that six shipped screens read and that held ZERO rows in every database
/// here: `adhoccharge` (+ its `studentadhoccharge` and `classroomadhoccharge` rows),
/// `discountinvoices`, `taxexemption` and `refund`.
///
/// WHY THIS EXISTS
/// ---------------
/// Three of these are PAGED GRIDS (`fee.adhocCharge.html`, `student.tax.exemption.html` and
/// `school.refund.html` all drive server-side DataTables), and the other three are the per-parent
/// reads their dialogs make. A spec over an empty table reports SKIP, which reads as "not measured
/// yet" for a screen the application ships - the failure this tool exists to prevent.
///
/// ⚠️ THESE ARE THE MONEY TABLES AND THEY ARE ALL IN A DIFFERENT STATE OF THE LIFECYCLE ON PURPOSE.
/// A grid whose rows are all `Draft` measures one predicate; the screens separate statuses into tabs
/// and stat tiles, so the seed spreads the rows across the statuses the enums actually define
/// (`AdHocChargeStatus` 1 Draft / 2 Submitted / 3 Approved / 5 Issued, `RefundStatus` 1 Pending /
/// 2 Approved / 5 Completed / 3 Rejected). The per-charge student rows are mostly `Pending` (status 1,
/// `invoiceid` NULL) because that is what the charge dialog's own "pending" read filters on.
///
/// ⚠️ EVERY PARENT IS RESOLVED, NEVER INVENTED. `adhoccharge.feeTypeId` -> `feetype`,
/// `.academicYearId` -> `academicyear`, `taxexemption.taxCodeId` -> `taxcode`,
/// `discountinvoices.discountId` -> `discount` and `.invoiceId` -> `invoices`, and `refund.paymentId`
/// -> `payment` (ON DELETE RESTRICT) + `.studentId` -> `student`. A campus missing any of them is
/// REPORTED in a sentence rather than seeded into an FK violation - the shape
/// `CommunicationWorkspaceSeeder` and `ExamToolingSeeder` both established.
///
/// ⚠️ THE UNIQUE INDEXES ARE THE TRAP, AND THEY ARE ALL PARTIAL.
///   * `ux_studentadhoccharge_chargeenrollment (adhocchargeid, studentenrollmentid) WHERE
///     studentenrollmentid IS NOT NULL` - so a charge may carry ONE row per enrolment, which is why
///     the student rows walk distinct enrolments rather than wrapping.
///   * `ux_studentadhoccharge_invoice (invoiceid) WHERE invoiceid IS NOT NULL` - GLOBALLY unique, not
///     per charge, so the single invoiced row per charge takes a distinct invoice id.
///   * `ux_refund_number (tenantid, refundnumber)` - so the number is campus-stamped.
///   * `ux_refund_requestreference (tenantid, paymentid, requestreference) WHERE requestreference IS
///     NOT NULL` - one refund per (payment, reference), which is why each refund takes its own payment.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds ad-hoc charges is skipped unless
/// <see cref="FeeMoneyExtrasSeedOptions.Force"/> is set.
/// </summary>
public sealed class FeeMoneyExtrasSeeder : BaseSeeder
{
    public FeeMoneyExtrasSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk fee-money load invalidates, so a fixture can ANALYZE them. `invoices`,
    /// `payment`, `studentenrollment`, `discount` and `taxcode` are in the list because the reads
    /// JOIN them - fresh row counts on the fact table alone would leave the planner reasoning about
    /// the pre-seed join sizes.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "adhoccharge", "studentadhoccharge", "classroomadhoccharge",
        "discountinvoices", "taxexemption", "refund",
        "invoices", "payment", "studentenrollment", "discount", "taxcode",
    };

    /// <summary>
    /// The statuses an ad-hoc charge is seeded at, in the order the grid's tabs separate them:
    /// `AdHocChargeStatus` Draft 1 / Submitted 2 / Approved 3 / Issued 5. `Issued` is the state the
    /// generator moves a charge to once it is billed, so its row is the one the "already invoiced"
    /// branch of the list sees.
    /// </summary>
    private static readonly short[] ChargeStatuses = { 1, 2, 3, 5 };

    /// <summary>
    /// `DiscountApprovalStatus` per charge row: Pending 1 for the two unapproved ones, Approved 2 for
    /// the approved/issued pair. ⚠️ The approval status is what the charge's own guard reads before it
    /// may generate invoices, so an all-Pending seed would make the billing path unreachable.
    /// </summary>
    private static readonly short[] ChargeApprovals = { 1, 1, 2, 2 };

    /// <summary>`RefundStatus` Pending 1 / Approved 2 / Completed 5 / Rejected 3.</summary>
    private static readonly short[] RefundStatuses = { 1, 2, 5, 3 };

    public async Task<FeeMoneyExtrasSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, FeeMoneyExtrasSeedOptions options, bool verbose = true)
    {
        var result = new FeeMoneyExtrasSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM adhoccharge
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // ⚠️ A SKIPPED CAMPUS STILL REPORTS WHAT IT HOLDS. A total of zero over a campus that
            // already carries the rows reads as a broken fixture rather than an idempotent one.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  Fee money extras: campus {campusId} already holds {existing} ad-hoc charge(s) - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 0. The prerequisites. Every one is an FK target this seeder does not own, so a missing one
        //    is REPORTED - a campus with no fee type cannot raise an ad-hoc charge, and a campus with
        //    no payment cannot refund one.
        // ------------------------------------------------------------------
        var academicYearId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM academicyear
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY isactive DESC, id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var feeTypeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM feetype
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var taxCodeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM taxcode
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        var discountId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM discount
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        // ⚠️ THE ENROLMENTS COME FROM THE CAMPUS'S OWN STUDENTS AT A STATUS THE READERS ACCEPT.
        // `studentadhoccharge.studentenrollmentid` and the invoice-generation paths all treat
        // `StudentStatus` 4-7 (Transferred/Withdrawn/Graduated/…) as finished, so those rows are
        // seeded but invisible - the same `NOT IN (4,5,6,7)` the enrolment picker's anti-join uses.
        var enrollments = (await conn.QueryAsync<(long Id, long StudentId)>(
            @"SELECT se.id AS Id, se.studentid AS StudentId
                FROM studentenrollment se
                JOIN student s ON s.id = se.studentid
               WHERE se.tenantid = @tenantId AND se.schoolid = @schoolId AND se.campusid = @campusId
                 AND se.studentstatus NOT IN (4,5,6,7)
               ORDER BY se.id",
            new { tenantId, schoolId, campusId })).ToList();

        var classrooms = (await conn.QueryAsync<long>(
            @"SELECT id FROM classroom
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        var invoices = (await conn.QueryAsync<(long Id, long StudentId)>(
            @"SELECT id AS Id, studentid AS StudentId FROM invoices
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        // ⚠️ A REFUND'S PAYMENT IS `ON DELETE RESTRICT` AND ITS STUDENT IS `ON DELETE RESTRICT` TOO,
        // so the pair has to be real and consistent - the payment's OWN student is the refund's.
        var payments = (await conn.QueryAsync<(long Id, long StudentId, long? InvoiceId)>(
            @"SELECT id AS Id, studentid AS StudentId, invoiceid AS InvoiceId FROM payment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (academicYearId is null or 0 || feeTypeId is null or 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} has {(academicYearId is null or 0 ? "no academic year" : "an academic year")} " +
                $"and {(feeTypeId is null or 0 ? "no fee type" : "a fee type")} - an ad-hoc charge needs both " +
                "(`adhoccharge.feetypeid` and `.academicyearid` are FKs to tables this seeder does not own). " +
                "Run the fees fixture (FeesModuleSeeder) first.";
            if (verbose) Console.WriteLine($"  Fee money extras: {result.SkipReason}");
            return result;
        }

        if (taxCodeId is null or 0 || enrollments.Count == 0)
        {
            result.Skipped = true;
            result.SkipReason =
                $"campus {campusId} has {(taxCodeId is null or 0 ? "no tax code" : "a tax code")} and " +
                $"{enrollments.Count} live enrolment(s) - a tax exemption needs a tax code and a student to " +
                "exempt. Run the master-data fixture and the dataset seeder first.";
            if (verbose) Console.WriteLine($"  Fee money extras: {result.SkipReason}");
            return result;
        }

        // ------------------------------------------------------------------
        // 1. A forced re-seed clears first, CHILDREN BEFORE PARENTS. All three child tables carry no
        //    scope columns of their own, so they go through a parent that HAS them - `adhoccharge` for
        //    the ad-hoc rows and `invoices` for the discounts. (Unlike the rubric levels, both parents
        //    here really do carry the triple, so `ClearTableByParentAsync` is the right helper.)
        // ------------------------------------------------------------------
        if (options.Force && existing > 0)
        {
            await ClearTableByParentAsync(conn, "studentadhoccharge", "adhocchargeid", "adhoccharge",
                tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "classroomadhoccharge", "adhocchargeid", "adhoccharge",
                tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "discountinvoices", "invoiceid", "invoices",
                tenantId, schoolId, campusId);

            await ClearTableAsync(conn, "refund", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "taxexemption", tenantId, schoolId, campusId);
            await ClearTableAsync(conn, "adhoccharge", tenantId, schoolId, campusId);

            if (verbose) Console.WriteLine($"  Fee money extras: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 2. `adhoccharge` - one row per status, all three scope columns stamped from the ROUTE's
        //    scope rather than a column default (0 would put the row outside every screen's predicate).
        // ------------------------------------------------------------------
        var chargeIds = new List<long>();
        var chargeIndex = 0;

        foreach (var status in ChargeStatuses.Take(options.AdHocChargesPerCampus))
        {
            var approval = ChargeApprovals[Math.Min(chargeIndex, ChargeApprovals.Length - 1)];
            var amount = 250m + (chargeIndex * 175m);
            // A 5% rate is what most kindergarten fee heads carry; the treatment token is the
            // `discount`/tax master's own vocabulary, mirrored rather than referenced.
            const short standardRated = 1;

            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO adhoccharge
                      (amount, isinvoicegenerated, tenantid, schoolid, campusid,
                       createdby, modifiedby, createdon, modifiedon,
                       chargenumber, name, description, feetypeid, taxcodeid,
                       taxtreatment, taxrate, duedate, chargedate,
                       status, approvalstatus, approvedby, approvedon, academicyearid,
                       isoptional, paymentfor, feetypename)
                  VALUES (@amount, @isInvoiceGenerated, @tenantId, @schoolId, @campusId,
                          1, 1, @now, @now,
                          @chargeNumber, @name, @description, @feeTypeId, @taxCodeId,
                          @taxTreatment, @taxRate, @dueDate, @chargeDate,
                          @status, @approvalStatus, @approvedBy, @approvedOn, @academicYearId,
                          false, @paymentFor, @feeTypeName)
                  RETURNING id",
                new
                {
                    amount,
                    isInvoiceGenerated = status == 5,
                    tenantId, schoolId, campusId, now,
                    chargeNumber = $"PERF-ADH-{campusId}-{chargeIndex + 1:00}",
                    name = $"Perf Ad-Hoc Charge {chargeIndex + 1}",
                    description = "Perf dataset - an ad-hoc charge raised for the campus.",
                    feeTypeId,
                    taxCodeId,
                    taxTreatment = standardRated,
                    taxRate = 5m,
                    dueDate = now.AddDays(30),
                    chargeDate = now,
                    status,
                    approvalStatus = approval,
                    // ⚠️ MIRRORS THE STATUS: an approved row names an approver, an unapproved one is
                    // NULL - so the screen's "Approved by" column is not blank on the very rows that
                    // claim to have been approved.
                    approvedBy = approval == 2 ? (long?)1 : null,
                    approvedOn = approval == 2 ? (DateTime?)now : null,
                    academicYearId,
                    paymentFor = $"Perf Charge {chargeIndex + 1}",
                    feeTypeName = "Perf Ad-Hoc Fee",
                });

            chargeIds.Add(id);
            result.AdHocCharges++;
            chargeIndex++;
        }

        // ------------------------------------------------------------------
        // 3. `studentadhoccharge` - the rows the charge's own detail dialog lists. EACH ROW TAKES A
        //    DISTINCT ENROLMENT of this charge's own campus (the partial unique index is
        //    (adhocchargeid, studentenrollmentid)), and exactly one row per charge is left
        //    `Invoiced` against a DISTINCT invoice so the LEFT JOIN to `invoices` resolves and the
        //    `ux_studentadhoccharge_invoice` partial index is exercised rather than avoided.
        // ------------------------------------------------------------------
        var enrollmentCursor = 0;
        var invoiceCursor = 0;

        for (var c = 0; c < chargeIds.Count; c++)
        {
            var chargeId = chargeIds[c];
            var count = Math.Min(options.StudentsPerCharge, enrollments.Count);
            var invoicedRow = c % count;   // one row per charge carries an invoice

            for (var i = 0; i < count; i++)
            {
                var enrollment = enrollments[enrollmentCursor % enrollments.Count];
                enrollmentCursor++;

                long? invoiceId = null;
                short status = 1;
                if (i == invoicedRow && invoices.Count > 0 && invoiceCursor < invoices.Count)
                {
                    invoiceId = invoices[invoiceCursor].Id;
                    invoiceCursor++;
                    status = 2;   // StudentChargeStatus.Invoiced
                }

                var gross = 250m + (c * 175m);

                await conn.ExecuteAsync(
                    @"INSERT INTO studentadhoccharge
                          (adhocchargeid, studentid, createdby, modifiedby, createdon, modifiedon,
                           invoiceid, grossamount, discountamount, taxableamount, taxamount, totalamount,
                           status, taxexemptionpercentage, studentenrollmentid, taxcodeid,
                           taxrateapplied, taxsnapshot)
                      VALUES (@chargeId, @studentId, 1, 1, @now, @now,
                              @invoiceId, @gross, 0, @gross, @tax, @total,
                              @status, 0, @enrollmentId, @taxCodeId,
                              5, @taxSnapshot)",
                    new
                    {
                        chargeId,
                        studentId = enrollment.StudentId,
                        now,
                        invoiceId,
                        gross,
                        tax = Math.Round(gross * 0.05m, 2),
                        total = Math.Round(gross * 1.05m, 2),
                        status,
                        enrollmentId = enrollment.Id,
                        taxCodeId,
                        taxSnapshot = "{\"rate\":5,\"treatment\":\"StandardRated\"}",
                    });

                result.StudentAdHocCharges++;
            }
        }

        // ------------------------------------------------------------------
        // 4. `classroomadhoccharge` - the classroom-scoped variant of the same charge. Both FKs are
        //    NOT NULL, so a campus with no classroom simply gets none of these rather than a failure.
        // ------------------------------------------------------------------
        if (classrooms.Count > 0)
        {
            var classroomCursor = 0;
            foreach (var chargeId in chargeIds)
            {
                for (var i = 0; i < options.ClassroomsPerCharge && classrooms.Count > 0; i++)
                {
                    // ⚠️ NO UNIQUE INDEX GUARDS THIS PAIR, so a wrap would silently double a row the
                    // dialog renders twice. Stop rather than repeat.
                    if (classroomCursor >= classrooms.Count) break;

                    await conn.ExecuteAsync(
                        @"INSERT INTO classroomadhoccharge
                              (adhocchargeid, classroomid, createdby, modifiedby)
                          VALUES (@chargeId, @classroomId, 1, 1)",
                        new { chargeId, classroomId = classrooms[classroomCursor] });

                    classroomCursor++;
                    result.ClassroomAdHocCharges++;
                }
            }
        }

        // ------------------------------------------------------------------
        // 5. `taxexemption` - one row per student, anchored on the permanent fee type (V39) and a
        //    real tax code. The grid's own query INNER JOINs `Student` and `TaxCode`, so a row that
        //    does not resolve is a row the screen never shows.
        // ------------------------------------------------------------------
        var exemptRows = Math.Min(options.TaxExemptionsPerCampus, enrollments.Count);
        var exemptionCursor = 0;
        for (var i = 0; i < exemptRows; i++)
        {
            var enrollment = enrollments[exemptionCursor % enrollments.Count];
            exemptionCursor++;

            await conn.ExecuteAsync(
                @"INSERT INTO taxexemption
                      (studentid, taxcodeid, taxexemptionvalue, reason, startdate, enddate,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon,
                       feetypeid)
                  VALUES (@studentId, @taxCodeId, @value, @reason, @start, @end,
                          @tenantId, @schoolId, @campusId, 1, 1, @now, @now,
                          @feeTypeId)",
                new
                {
                    studentId = enrollment.StudentId,
                    taxCodeId,
                    // `taxexemptionvalue` is a `real`, not a numeric - a decimal binds as 22P02.
                    value = 50f,
                    reason = "Perf dataset - partial exemption granted to this household.",
                    start = now.AddDays(-30),
                    end = now.AddDays(180),
                    tenantId, schoolId, campusId, now, feeTypeId,
                });

            result.TaxExemptions++;
        }

        // ------------------------------------------------------------------
        // 6. `discountinvoices` - the discounts the invoice engine resolved onto an invoice. The read
        //    (`GetDiscountByInvoiceId`) is keyed on the invoice, so the rows have to spread over
        //    several invoices; the discount type comes from the campus's own catalogue.
        // ------------------------------------------------------------------
        if (discountId is { } did && did > 0 && invoices.Count > 0)
        {
            var rows = Math.Min(options.DiscountedInvoicesPerCampus, invoices.Count);
            for (var i = 0; i < rows; i++)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO discountinvoices
                          (invoiceid, feestructuredetailid, discountid, amount,
                           createdby, modifiedby, createdon, modifiedon, feetypeid)
                      VALUES (@invoiceId, NULL, @discountId, @amount, 1, 1, @now, @now, @feeTypeId)",
                    new
                    {
                        invoiceId = invoices[i].Id,
                        discountId = did,
                        amount = 25m + i,
                        now,
                        feeTypeId,
                    });

                result.DiscountInvoices++;
            }
        }

        // ------------------------------------------------------------------
        // 7. `refund` - one per status, each against its OWN payment (the request-reference index is
        //    per (payment, reference), and the number is unique per tenant). `refundmethodid` has no
        //    FK, so 0 is a real "not recorded yet" value rather than a broken reference.
        // ------------------------------------------------------------------
        if (payments.Count > 0)
        {
            var rows = Math.Min(options.RefundsPerCampus, payments.Count);
            for (var i = 0; i < rows; i++)
            {
                var payment = payments[i];
                var status = RefundStatuses[Math.Min(i, RefundStatuses.Length - 1)];
                var amount = 100m + (i * 50m);
                var approved = status is 2 or 5;

                await conn.ExecuteAsync(
                    @"INSERT INTO refund
                          (refundnumber, studentid, paymentid, invoiceid, requestreference,
                           requestedamount, amount, taxamount, totalamount, refundmethodid, reason,
                           status, requestedby, requestedon, approvedby, approvedon,
                           rejectedby, rejectedon, processedby, processedon, completedon,
                           externalreference, failurereason, workflowid, academicyearid,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@refundNumber, @studentId, @paymentId, @invoiceId, @requestReference,
                              @amount, @amount, 0, @amount, 0, @reason,
                              @status, 1, @now, @approvedBy, @approvedOn,
                              0, NULL, @processedBy, @processedOn, @completedOn,
                              NULL, NULL, 0, @academicYearId,
                              @tenantId, @schoolId, @campusId, 1, 1, @now, @now)",
                    new
                    {
                        refundNumber = $"PERF-RF-{campusId}-{i + 1:000}",
                        studentId = payment.StudentId,
                        paymentId = payment.Id,
                        invoiceId = payment.InvoiceId,
                        requestReference = $"PERF-REQ-{campusId}-{i + 1:000}",
                        amount,
                        reason = "Perf dataset - refund raised against a recorded payment.",
                        status,
                        now,
                        // ⚠️ `approvedby` / `rejectedby` / `processedby` are NOT NULL with a default of
                        // **0** - so "nobody has done this yet" is the NUMBER zero, not NULL. Passing a
                        // null here is a `23502` on a column whose default would never have been applied,
                        // because this INSERT names every column explicitly.
                        approvedBy = approved ? 1L : 0L,
                        approvedOn = approved ? (DateTime?)now : null,
                        // Only a COMPLETED refund names a processor and a completion stamp; an
                        // approved one is not yet paid out.
                        processedBy = status == 5 ? 1L : 0L,
                        processedOn = status == 5 ? (DateTime?)now : null,
                        completedOn = status == 5 ? (DateTime?)now : null,
                        academicYearId = academicYearId!.Value,
                        tenantId, schoolId, campusId,
                    });

                result.Refunds++;
            }
        }

        await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);

        if (verbose)
        {
            Console.WriteLine(
                $"  Fee money extras: campus {campusId} -> {result.AdHocCharges} charges, " +
                $"{result.StudentAdHocCharges} student rows, {result.ClassroomAdHocCharges} classroom rows, " +
                $"{result.TaxExemptions} exemptions, {result.DiscountInvoices} invoice discounts, " +
                $"{result.Refunds} refunds");
        }

        return result;
    }

    /// <summary>
    /// Fills <paramref name="result"/> from what the campus already holds, so a skipped campus is
    /// reported exactly like a freshly seeded one. The three child tables carry no scope columns, so
    /// they are counted through the parent the reads themselves reach them through.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, FeeMoneyExtrasSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $@"SELECT COUNT(*) FROM {table}
                    WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.AdHocCharges = await ScopedAsync("adhoccharge");
        result.TaxExemptions = await ScopedAsync("taxexemption");
        result.Refunds = await ScopedAsync("refund");

        result.StudentAdHocCharges = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM studentadhoccharge sa
                JOIN adhoccharge ac ON ac.id = sa.adhocchargeid
               WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.ClassroomAdHocCharges = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM classroomadhoccharge ca
                JOIN adhoccharge ac ON ac.id = ca.adhocchargeid
               WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.DiscountInvoices = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM discountinvoices di
                JOIN invoices i ON i.id = di.invoiceid
               WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId",
            new { tenantId, schoolId, campusId });
    }
}
