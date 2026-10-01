using Dapper;

namespace SchoolPerformance.Seeders;

/// <summary>How much of the fee/enrolment world to build for one campus.</summary>
public sealed class FeesSeedOptions
{
    /// <summary>Invoices generated per enrolled student.</summary>
    public int InvoicesPerStudent { get; set; } = 4;

    /// <summary>The academic year to create when the campus has none.</summary>
    public int AcademicYearStartYear { get; set; } = 2026;

    public decimal InvoiceAmount { get; set; } = 500m;

    /// <summary>Re-seed a campus that already holds enrolments/invoices.</summary>
    public bool Force { get; set; }
}

public sealed class FeesSeedSummary
{
    public long AcademicYearId { get; set; }
    public long TermId { get; set; }
    public long FeeTypeId { get; set; }
    public long FeeStructureId { get; set; }
    public long PaymentMethodId { get; set; }
    public int Enrollments { get; set; }
    public int Invoices { get; set; }
    public int InvoiceLines { get; set; }
    public int Payments { get; set; }
    public int Allocations { get; set; }
    public bool Skipped { get; set; }

    public string Describe() =>
        Skipped
            ? "skipped (this campus already holds enrolments)"
            : $"{Enrollments:N0} enrolments, {Invoices:N0} invoices, {InvoiceLines:N0} invoice lines, " +
              $"{Payments:N0} payments, {Allocations:N0} allocations";
}

/// <summary>
/// Builds the FEE / ENROLMENT spine for one campus, so the fee screens have volume to measure.
/// </summary>
/// <remarks>
/// ⚠️ WHY THIS EXISTS. `ayra_perf` held rows in FIVE tables of 246 - student, attendance, parent,
/// classroom, campus. Every fee, enrolment, exam, library, inventory and accounting spec would
/// therefore report SKIP ("this scope holds too few rows"), so adding specs for those modules
/// would produce a catalogue that LOOKS like module coverage and measures nothing.
///
/// Volume is the gate on coverage, not spec-writing. This builds the cheapest spine that makes
/// a whole module measurable: an academic year and term (an enrolment cannot exist without one),
/// a fee type and structure, then one enrolment per student, N invoices per enrolment and a
/// payment against the first of them.
///
/// ⚠️ EVERY INSERT IS SET-BASED AND GUARDED BY NOT EXISTS. The dataset is re-run often, and the
/// campus that reached 842,614 students got there by seeding that was not idempotent. A second
/// run must add nothing - which is also what lets the tests assert an exact row count.
///
/// It deliberately does NOT try to be a faithful fee engine: no fee-structure details, discounts
/// or tax. What the planner and the scope filters see is ROW COUNTS on the tables the grids
/// read, and those are the tables this fills.
/// </remarks>
public sealed class FeesModuleSeeder : BaseSeeder
{
    public FeesModuleSeeder(string connectionString) : base(connectionString) { }

    public const string FeeTypeName = "PERF Tuition";

    public async Task<FeesSeedSummary> SeedAsync(
        long tenantId,
        long schoolId,
        long campusId,
        long classroomId,
        long academicGradeId,
        FeesSeedOptions options,
        bool verbose = true)
    {
        void Log(string message)
        {
            if (verbose) Console.WriteLine("    " + message);
        }

        var summary = new FeesSeedSummary();
        using var conn = await OpenConnectionAsync();
        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // Idempotency gate. Enrolments are the spine: if the campus already has
        // them, everything downstream does too.
        // ------------------------------------------------------------------
        var existingEnrollments = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND campusid = @campusId",
            new { tenantId, campusId });

        if (existingEnrollments > 0 && !options.Force)
        {
            summary.Skipped = true;
            summary.Enrollments = (int)existingEnrollments;
            summary.Invoices = (int)await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM invoices WHERE tenantid = @tenantId AND campusid = @campusId",
                new { tenantId, campusId });
            Log($"campus {campusId} already holds {existingEnrollments:N0} enrolments - skipped");
            return summary;
        }

        // ------------------------------------------------------------------
        // 1. Academic year. Required by studentenrollment AND invoices, and the
        //    grids filter on it, so it is not optional scaffolding.
        // ------------------------------------------------------------------
        summary.AcademicYearId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM academicyear
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId }) ?? 0;

        if (summary.AcademicYearId == 0)
        {
            summary.AcademicYearId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO academicyear
                      (startyear, endyear, isactive, ispublished,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@startYear, @endYear, true, true,
                       @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    startYear = options.AcademicYearStartYear,
                    endYear = options.AcademicYearStartYear + 1,
                    tenantId,
                    schoolId,
                    campusId,
                    now,
                });
        }

        // ------------------------------------------------------------------
        // 2. Term. `invoices.termid` is NOT NULL and carries an FK to `terms`.
        // ------------------------------------------------------------------
        summary.TermId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM terms WHERE academicyearid = @yearId ORDER BY id LIMIT 1",
            new { yearId = summary.AcademicYearId }) ?? 0;

        if (summary.TermId == 0)
        {
            summary.TermId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO terms
                      (academicyearid, name, startdate, enddate, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@yearId, @name, @startDate, @endDate, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    yearId = summary.AcademicYearId,
                    name = "PERF Term 1",
                    startDate = new DateTime(options.AcademicYearStartYear, 9, 1, 0, 0, 0, DateTimeKind.Unspecified),
                    endDate = new DateTime(options.AcademicYearStartYear + 1, 6, 30, 0, 0, 0, DateTimeKind.Unspecified),
                    now,
                });
        }

        // ------------------------------------------------------------------
        // 3. Payment method. `payment.paymentmethodid` is NOT NULL + FK.
        // ------------------------------------------------------------------
        summary.PaymentMethodId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM paymentmethod WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId }) ?? 0;

        if (summary.PaymentMethodId == 0)
        {
            summary.PaymentMethodId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO paymentmethod
                      (name, tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      ('PERF Cash', @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new { tenantId, schoolId, campusId, now });
        }

        // ------------------------------------------------------------------
        // 4. Fee type + fee structure. The structure is per (year, grade), which
        //    is exactly the key the fee screens look it up by.
        // ------------------------------------------------------------------
        summary.FeeTypeId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM feetype WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND name = @name ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId, name = FeeTypeName }) ?? 0;

        if (summary.FeeTypeId == 0)
        {
            summary.FeeTypeId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO feetype
                      (name, tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@name, @tenantId, @schoolId, @campusId, 1, 1, @now, @now)
                  RETURNING id",
                new { name = FeeTypeName, tenantId, schoolId, campusId, now });
        }

        summary.FeeStructureId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM feestructure
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND academicyearid = @yearId AND academicgradeid = @gradeId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId, yearId = summary.AcademicYearId, gradeId = academicGradeId }) ?? 0;

        if (summary.FeeStructureId == 0)
        {
            summary.FeeStructureId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO feestructure
                      (tenantid, schoolid, campusid, academicyearid, academicgradeid, name,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@tenantId, @schoolId, @campusId, @yearId, @gradeId, @name, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId,
                    schoolId,
                    campusId,
                    yearId = summary.AcademicYearId,
                    gradeId = academicGradeId,
                    name = "PERF Fee Structure",
                    now,
                });
        }

        // `payment.receivedby` / `voidedby` / `refundedby` are all NOT NULL, so even a payment
        // nobody voided needs an actor. Resolve one real user rather than hardcoding 1 - the
        // hardcoded id is what broke StudentSeeder against fk_p_u_userid (23503).
        var actorId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT MIN(id) FROM users")
            ?? throw new InvalidOperationException(
                "`users` is empty, so there is no id to stamp as the payment actor (receivedby/voidedby/refundedby are NOT NULL).");

        // ------------------------------------------------------------------
        // 5. Enrolments - one per active student of the campus.
        //
        // ⚠️ classroomid IS SET, even though the column is nullable, because
        // StudentEnrollmentRepository.GetAllGradeStudents INNER JOINs Classroom and
        // filters on c.AcademicGradeId - an enrolment with a NULL classroom is
        // invisible to that grid, so seeding one would leave the screen empty and the
        // spec SKIPping.
        // ------------------------------------------------------------------
        summary.Enrollments = await conn.ExecuteAsync(
            @"INSERT INTO studentenrollment
                  (studentid, rollnumber, studentstatus, classroomid, academicyearid,
                   tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
              SELECT s.id,
                     'R-' || s.id,
                     3,                      -- StudentStatus.Enrolled
                     @classroomId,
                     @yearId,
                     @tenantId, @schoolId, @campusId, 1, 1, @now, @now
                FROM student s
               WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                 AND s.isactive = true
                 AND NOT EXISTS (
                     SELECT 1 FROM studentenrollment e
                      WHERE e.studentid = s.id AND e.academicyearid = @yearId)",
            new { tenantId, schoolId, campusId, classroomId, yearId = summary.AcademicYearId, now });

        // ------------------------------------------------------------------
        // 6. Invoices - N per student, so the grid has several pages and the
        //    "outstanding balance" guard has a real sum to compute.
        //
        //    amountpaid varies on purpose: a campus where every invoice is settled
        //    would make the outstanding-balance query trivially cheap and hide what
        //    it costs when there is something to sum.
        // ------------------------------------------------------------------
        summary.Invoices = await conn.ExecuteAsync(
            @"INSERT INTO invoices
                  (feeid, invoicenumber, studentid, totalamount, baseamount, amountpaid,
                   invoicedate, duedate, status, academicyearid, termid,
                   tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
              SELECT @feeStructureId,
                     'PERF-INV-' || s.id || '-' || g,
                     s.id,
                     @amount,
                     @amount,
                     CASE WHEN g % 2 = 0 THEN 0 ELSE @amount / 2 END,
                     @now - (g || ' months')::interval,
                     @now - (g || ' months')::interval + interval '30 days',
                     1,                      -- invoice status: open
                     @yearId,
                     @termId,
                     @tenantId, @schoolId, @campusId, 1, 1, @now, @now
                FROM student s
                CROSS JOIN generate_series(0, @invoicesPerStudent - 1) AS g
               WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                 AND s.isactive = true
                 AND NOT EXISTS (
                     SELECT 1 FROM invoices i
                      WHERE i.studentid = s.id AND i.academicyearid = @yearId)",
            new
            {
                feeStructureId = summary.FeeStructureId,
                amount = options.InvoiceAmount,
                invoicesPerStudent = options.InvoicesPerStudent,
                tenantId,
                schoolId,
                campusId,
                yearId = summary.AcademicYearId,
                termId = summary.TermId,
                now,
            });

        // ------------------------------------------------------------------
        // 7. One line per invoice. `InvoiceLine.SourceType` is what the payment
        //    screen's Type column is computed from, so it is set (1 = RegularFee).
        // ------------------------------------------------------------------
        summary.InvoiceLines = await conn.ExecuteAsync(
            @"INSERT INTO invoiceline
                  (invoiceid, amount, sourcetype, createdby, modifiedby, createdon, modifiedon)
              SELECT i.id, i.baseamount, 1, 1, 1, @now, @now
                FROM invoices i
               WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId
                 AND i.academicyearid = @yearId
                 AND NOT EXISTS (SELECT 1 FROM invoiceline il WHERE il.invoiceid = i.id)",
            new { tenantId, schoolId, campusId, yearId = summary.AcademicYearId, now });

        // ------------------------------------------------------------------
        // 8. One payment per student, against their oldest invoice.
        //
        //    `amountpaid > 0` is a filter in PaymentRepository.GetAllTransactions, so a
        //    zero-amount payment would be invisible to the grid it exists to exercise.
        // ------------------------------------------------------------------
        summary.Payments = await conn.ExecuteAsync(
            @"INSERT INTO payment
                  (studentid, invoiceid, paymentmethodid, amountpaid, transactiondate, transactionstatus,
                   receiptnumber, receivedby, voidedby, refundedby,
                   academicyearid, termid, tenantid, schoolid, campusid,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT DISTINCT ON (i.studentid)
                     i.studentid,
                     i.id,
                     @paymentMethodId,
                     @amount / 2,
                     @now,
                     1,                          -- TransactionStatus: 1 = Completed (2 = Failed)
                     'PERF-RCPT-' || i.studentid,
                     @actorId, @actorId, @actorId,
                     @yearId,
                     @termId,
                     @tenantId, @schoolId, @campusId, 1, 1, @now, @now
                FROM invoices i
               WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId
                 AND i.academicyearid = @yearId
                 AND NOT EXISTS (SELECT 1 FROM payment p WHERE p.studentid = i.studentid AND p.academicyearid = @yearId)
               ORDER BY i.studentid, i.id",
            new
            {
                paymentMethodId = summary.PaymentMethodId,
                amount = options.InvoiceAmount,
                actorId,
                yearId = summary.AcademicYearId,
                termId = summary.TermId,
                tenantId,
                schoolId,
                campusId,
                now,
            });

        // ------------------------------------------------------------------
        // 9. Allocate each payment to the invoice it settled.
        //
        //    ⚠️ A PAYMENT WITH NO ALLOCATION IS INVISIBLE TO THE FEE COLLECTION VIEW, and
        //    the first version of this seeder never wrote one. `vw_fee_collection` reaches
        //    its fact rows through
        //
        //        FROM payment p JOIN paymentallocation pa ON pa.transactionid = p.id
        //
        //    so it returned **0 rows while 2,000 Completed payments sat on the campus**
        //    (and 3 stray allocation rows existed in the whole database). The Fee
        //    Collection report — one of the shipped reports — therefore measured nothing
        //    at all, and its "0 rows" reading looked like "this campus took no payments".
        //
        //    `latefeeamount` and `amountapply` are both NOT NULL, so both are supplied:
        //    the whole payment is applied to the invoice, with no late fee. The NOT EXISTS
        //    guard keeps a re-run idempotent (there is a unique index on
        //    (tenantid, campusid, transactionid, invoiceid), but guarding here means a
        //    second run reports 0 added rather than raising).
        // ------------------------------------------------------------------
        summary.Allocations = await conn.ExecuteAsync(
            @"INSERT INTO paymentallocation
                  (transactionid, invoiceid, latefeeamount, amountapply,
                   tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
              SELECT p.id, p.invoiceid, 0, p.amountpaid,
                     p.tenantid, p.schoolid, p.campusid, 1, 1, @now, @now
                FROM payment p
               WHERE p.tenantid = @tenantId AND p.schoolid = @schoolId AND p.campusid = @campusId
                 AND p.academicyearid = @yearId
                 AND p.invoiceid IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM paymentallocation pa WHERE pa.transactionid = p.id)",
            new
            {
                tenantId,
                schoolId,
                campusId,
                yearId = summary.AcademicYearId,
                now,
            });

        Log($"campus {campusId}: {summary.Describe()}");
        return summary;
    }

    /// <summary>
    /// Refresh planner statistics for the tables this seeder fills.
    ///
    /// ⚠️ Same reason as PerfDatasetSeeder: a bulk load leaves the planner reasoning about
    /// pre-seed row counts, and autovacuum will not have caught up by the time anyone
    /// measures. The symptom is a plan chosen for a table that no longer looks like that.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "studentenrollment", "invoices", "invoiceline", "payment", "paymentallocation",
        "feetype", "feestructure", "terms",
    };
}
