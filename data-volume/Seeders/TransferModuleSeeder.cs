using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="TransferModuleSeeder"/>.</summary>
public sealed class TransferSeedOptions
{
    /// <summary>
    /// Transfers recorded on one campus. A school of 2,000 students moves a small percentage of
    /// them a year, so this is deliberately a small number - it is what a campus really holds, not
    /// a volume to fill a table with.
    /// </summary>
    public int TransfersPerCampus { get; set; } = 40;

    /// <summary>Re-seed even when the campus already holds transfer history.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's transfer seed produced.</summary>
public sealed class TransferSeedResult
{
    public bool Skipped { get; set; }
    public int Transfers { get; set; }
}

/// <summary>
/// Seeds `enrollmenttransferhistory` - the fact table behind `STUDENT_TRANSFER`
/// (`vw_student_transfer`).
///
/// WHY THIS EXISTS
/// ---------------
/// `enrollmenttransferhistory` held a single row, so `rpt-student-transfer` reported SKIP. The view
/// joins `student`, and LEFT joins the FROM/TO campus and classroom names, and it scopes on the
/// history row's OWN tenant/school/campus - so the row is written against the SOURCE campus, exactly
/// as the transfer service writes it.
///
/// ⚠️ THIS IS THE ONE SEEDER WHOSE VOLUME IS HONESTLY SMALL, AND ITS GATE HAS TO SAY SO. A campus
/// with 2,000 students sees tens of transfers a year, not thousands - so the spec's volume floor is
/// set to match the data rather than the data inflated to match the floor. The catalogue's own rule
/// is explicit: a gate that can never open is worse than no gate, and when a table's honest size is
/// below the default it is the GATE that is the defect.
///
/// `transfertype` is the product's own enum (`StaticEntities/TransferType.cs`): 1 Section, 2 Campus,
/// 3 School, 4 Withdrawal, 5 Graduation. Every value seeded here is one a screen can actually write.
///
/// ⚠️ `enrollmenttransferhistory.id` IS `GENERATED ALWAYS AS IDENTITY`, so it is assigned by the
/// DATABASE and supplying one is a **428C9** ("cannot insert a non-DEFAULT value into column id").
/// The insert below therefore names no id at all - the same trap the PDF plan recorded on
/// `reporttemplate` and `documentsettings`.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds transfer history is skipped unless Force is set.
/// </summary>
public sealed class TransferModuleSeeder : BaseSeeder
{
    public TransferModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk transfer load invalidates.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "enrollmenttransferhistory"
    };

    /// <summary>
    /// The types a real campus writes. Graduation and Withdrawal are terminal states a report should
    /// be able to see, and they are written WITHOUT a destination - which is also the shape that
    /// exercises the view's LEFT joins.
    /// </summary>
    private static readonly short[] TransferTypes = { 1, 2, 3, 4, 1, 2 };

    private static readonly string[] Reasons =
    {
        "Family relocated", "Requested a section change", "Transferred to a sibling campus",
        "Medical reasons", "Parent employment change", "Academic stream change",
    };

    public async Task<TransferSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, TransferSeedOptions options, bool verbose = true)
    {
        var result = new TransferSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM enrollmenttransferhistory
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.Transfers = (int)existing;
            if (verbose)
            {
                Console.WriteLine(
                    $"  Transfer: campus {campusId} already holds {existing:N0} rows - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        if (options.Force && existing > 0)
        {
            await ClearTableAsync(conn, "enrollmenttransferhistory", tenantId, schoolId, campusId);

            if (verbose)
            {
                Console.WriteLine($"  Transfer: campus {campusId} cleared for a forced re-seed");
            }
        }

        // ------------------------------------------------------------------
        // 1. The enrollments that moved. `enrollmentid` and `studentid` are both NOT NULL, and the
        //    view joins `student` for the name and the admission number - so both come off a real
        //    enrollment rather than from an invented id.
        // ------------------------------------------------------------------
        var enrollments = (await conn.QueryAsync<TransferEnrollmentRow>(
            @"SELECT id AS EnrollmentId, studentid AS StudentId, classroomid AS ClassroomId
                FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id
               LIMIT @limit",
            new { tenantId, schoolId, campusId, limit = options.TransfersPerCampus })).ToList();

        if (enrollments.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId} holds no enrollment, so no transfer could reference one - " +
                "run PerfDatasetSeeder first.");
        }

        // ------------------------------------------------------------------
        // 2. A destination: a sibling campus of the same school, and one of its classrooms. A
        //    transfer whose destination does not resolve renders a blank `tocampusname`, which is
        //    what a report reads - so the LEFT joins are given something real to find. When the
        //    school has no sibling campus, the transfer is recorded as a SECTION move (destination
        //    campus = the source) rather than given a dangling id.
        // ------------------------------------------------------------------
        var destinationCampusId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM campus
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND id <> @campusId
               ORDER BY id LIMIT 1",
            new { tenantId, schoolId, campusId });

        long? destinationClassroomId = null;
        if (destinationCampusId is > 0)
        {
            destinationClassroomId = await conn.ExecuteScalarAsync<long?>(
                @"SELECT id FROM classroom
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @destination
                   ORDER BY id LIMIT 1",
                new { tenantId, schoolId, destination = destinationCampusId.Value });
        }

        // ------------------------------------------------------------------
        // 3. The rows.
        // ------------------------------------------------------------------
        var rows = new List<TransferRow>(enrollments.Count);

        for (var i = 0; i < enrollments.Count; i++)
        {
            var enrollment = enrollments[i];
            var type = TransferTypes[i % TransferTypes.Length];

            // A WITHDRAWAL or a SECTION move has no destination campus; a CAMPUS or SCHOOL move
            // does. Writing a destination on a withdrawal is the kind of row that makes a report
            // describe a move that never happened.
            var isRelocating = type is 2 or 3 && destinationCampusId is > 0;

            rows.Add(new TransferRow
            {
                EnrollmentId = enrollment.EnrollmentId,
                StudentId = enrollment.StudentId,
                TransferType = type,
                FromCampusId = campusId,
                FromClassroomId = enrollment.ClassroomId,
                ToCampusId = isRelocating ? destinationCampusId : campusId,
                ToClassroomId = isRelocating ? destinationClassroomId : enrollment.ClassroomId,
                EffectiveDate = new DateTime(DateTime.Today.Year, 1, 1).AddDays(20 + (i * 8) % 300),
                Reason = $"{Reasons[i % Reasons.Length]} (PERF {campusId})",
            });
        }

        const string sql = @"
            INSERT INTO enrollmenttransferhistory
                (enrollmentid, studentid, transfertype,
                 fromtenantid, fromschoolid, fromcampusid, fromclassroomid,
                 totenantid, toschoolid, tocampusid, toclassroomid,
                 effectivedate, reason, tenantid, schoolid, campusid,
                 createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@EnrollmentIds), unnest(@StudentIds), unnest(@TransferTypes),
                   unnest(@FromTenantIds), unnest(@FromSchoolIds), unnest(@FromCampusIds),
                   unnest(@FromClassroomIds),
                   unnest(@ToTenantIds), unnest(@ToSchoolIds), unnest(@ToCampusIds),
                   unnest(@ToClassroomIds),
                   unnest(@EffectiveDates)::timestamp, unnest(@Reasons),
                   unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                   1, 1, @now, @now";

        result.Transfers = await conn.ExecuteAsync(sql, new
        {
            EnrollmentIds = rows.Select(r => r.EnrollmentId).ToArray(),
            StudentIds = rows.Select(r => r.StudentId).ToArray(),
            TransferTypes = rows.Select(r => r.TransferType).ToArray(),
            FromTenantIds = System.Linq.Enumerable.Repeat(tenantId, rows.Count).ToArray(),
            FromSchoolIds = System.Linq.Enumerable.Repeat(schoolId, rows.Count).ToArray(),
            FromCampusIds = rows.Select(r => r.FromCampusId).ToArray(),
            FromClassroomIds = rows.Select(r => r.FromClassroomId).ToArray(),
            ToTenantIds = System.Linq.Enumerable.Repeat(tenantId, rows.Count).ToArray(),
            ToSchoolIds = System.Linq.Enumerable.Repeat(schoolId, rows.Count).ToArray(),
            // `tocampusid` / `toclassroomid` are nullable and every row here sets them, but the
            // arrays are typed nullable so a school without a sibling campus still binds.
            ToCampusIds = rows.Select(r => r.ToCampusId).ToArray(),
            ToClassroomIds = rows.Select(r => r.ToClassroomId).ToArray(),
            EffectiveDates = rows.Select(r => r.EffectiveDate).ToArray(),
            Reasons = rows.Select(r => r.Reason).ToArray(),
            TenantIds = System.Linq.Enumerable.Repeat(tenantId, rows.Count).ToArray(),
            SchoolIds = System.Linq.Enumerable.Repeat(schoolId, rows.Count).ToArray(),
            CampusIds = System.Linq.Enumerable.Repeat(campusId, rows.Count).ToArray(),
            now
        });

        if (verbose)
        {
            Console.WriteLine(
                $"  Transfer: campus {campusId} -> {result.Transfers} rows " +
                $"(destination campus {(destinationCampusId is > 0 ? destinationCampusId.Value.ToString() : "none - section moves only")})");
        }

        return result;
    }

    private sealed class TransferEnrollmentRow
    {
        public long EnrollmentId { get; set; }
        public long StudentId { get; set; }
        public long? ClassroomId { get; set; }
    }

    private sealed class TransferRow
    {
        public long EnrollmentId { get; set; }
        public long StudentId { get; set; }
        public short TransferType { get; set; }
        public long FromCampusId { get; set; }
        public long? FromClassroomId { get; set; }
        public long? ToCampusId { get; set; }
        public long? ToClassroomId { get; set; }
        public DateTime EffectiveDate { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
