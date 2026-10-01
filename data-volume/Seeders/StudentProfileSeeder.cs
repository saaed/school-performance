using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="StudentProfileSeeder"/>.</summary>
public sealed class StudentProfileSeedOptions
{
    /// <summary>Students that receive a profile (one guardian each).</summary>
    public int Students { get; set; } = 400;

    /// <summary>Emergency / Pickup contact rows per student.</summary>
    public int ContactsPerStudent { get; set; } = 2;

    /// <summary>Vaccination rows per student.</summary>
    public int ImmunizationsPerStudent { get; set; } = 2;

    /// <summary>Term results per student (capped by the campus's term count).</summary>
    public int TermResultsPerStudent { get; set; } = 2;

    public bool Force { get; set; }
}

/// <summary>What one campus's student-profile seed produced.</summary>
public sealed class StudentProfileSeedResult
{
    public bool Skipped { get; set; }
    public int Guardians { get; set; }
    public int StudentGuardians { get; set; }
    public int Contacts { get; set; }
    public int HealthRecords { get; set; }
    public int Immunizations { get; set; }
    public int TermResults { get; set; }
    public int Settlements { get; set; }
}

/// <summary>
/// Seeds the STUDENT PROFILE workspace for one campus: the guardian register and the student-guardian
/// links, emergency/pickup contacts, the health record, vaccinations, term results and the closed
/// account statement (the settlement snapshot).
///
/// WHY THIS EXISTS
/// ---------------
/// Every one of these tables held ZERO rows in every database here, so the specs over them could only
/// report **SKIP** - honest, and useless at once: `student.create.html`'s Contacts / Guardian / Health
/// / Immunization tabs, the term-result grid and `student.settlement.html` are screens the application
/// ships, and a SKIP reads as "not measured yet".
///
/// ⚠️ THE DATA ALREADY EXISTS. A campus in this dataset holds thousands of `student` rows and an
/// enrollment per student (879k students / 9.4k `studentenrollment` rows campus-wide), so this seeder
/// only writes the PROFILE rows hanging off them - it never invents a student. That is what makes it
/// cheap and what makes the resulting specs measure a real shape rather than a fixture.
///
/// ⚠️ `studentguardian` HAS NO SCOPE COLUMNS (`studentid`, `guardianid`, `relationshipid` and the audit
/// columns only), so the scoped DELETE is a **42703** and it must be cleared through its PARENT - the
/// `ClearTableByParentAsync` path. Clearing it with `ClearTableAsync` would abort the whole seed.
///
/// ⚠️ THE TWO ENUM COLUMNS ARE INTS WITH NO LOOKUP TABLE. `studentcontact.ContactTypeId` is the
/// `StaticEntities.ContactType` enum (Emergency = 1, Pickup = 2, Other = 3) and
/// `studentguardian.RelationshipId` is `StaticEntities.Relationship` (1..17) - both are read from an
/// ENUM NAMED IN CODE, not from a table, and neither carries a foreign key. The values below are the
/// enums the create screens offer, so a row here is one the screen could have written.
///
/// IDEMPOTENT: a campus that already holds guardians is skipped unless Force is set (and a skipped
/// campus still REPORTS what it holds, so a fixture's own assertion is true on a re-run).
/// </summary>
public sealed class StudentProfileSeeder : BaseSeeder
{
    public StudentProfileSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a student-profile load invalidates statistics for.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "guardian", "studentguardian", "studentcontact", "studenthealth",
        "studentimmunization", "studenttermresult", "studentsettlement"
    };

    /// <summary>The guardian register is what this dataset exists for, so it is the skip marker.</summary>
    private const string SkipMarkerTable = "guardian";

    /// <summary>`StaticEntities.ContactType` - the three the create screen offers.</summary>
    private static readonly short[] ContactTypes = { 1, 2, 3 };

    /// <summary>`StaticEntities.Relationship` - seventeen values, cycled rather than picked.</summary>
    private const short RelationshipCount = 17;

    /// <summary>`StaticEntities.DietPreference` - Healthy = 1, Vegitarian = 2, Anything = 3.</summary>
    private const short DietPreferenceCount = 3;

    private static readonly string[] BloodGroups =
        { "A+", "A-", "B+", "B-", "O+", "O-", "AB+", "AB-" };

    private static readonly string[] Vaccines =
    {
        "BCG", "Hepatitis B", "Polio (OPV)", "DTP", "MMR", "Varicella", "Tetanus"
    };

    /// <summary>
    /// `StaticEntities.ImmunizationStatus` - **Pending = 1, Done = 2**, and the column is a
    /// `smallint`. There is no "Overdue" member: a vaccination is either recorded or not.
    /// </summary>
    private static readonly short[] ImmunizationStatuses = { 1, 2 };

    /// <summary>
    /// `StaticEntities.StudentSettlementStatus` - the DERIVED lifecycle the repository's own
    /// comment describes: Open = 1, Settled = 2, PartiallySettled = 3.
    /// </summary>
    private const short SettlementOpen = 1;
    private const short SettlementSettled = 2;
    private const short SettlementPartiallySettled = 3;

    private static readonly string[] Grades = { "A", "B", "C", "D" };

    private static readonly string[] PromotionStatuses = { "Promoted", "Promoted", "Pending" };

    public async Task<StudentProfileSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, StudentProfileSeedOptions options, bool verbose = true)
    {
        var result = new StudentProfileSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {SkipMarkerTable}"
            + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
                Console.WriteLine($"  Student profile: campus {campusId} already holds {existing:N0} guardians - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // A student and its enrollment, so the term-result and settlement rows have a real parent.
        // One row per student rather than per enrollment: the profile tabs are keyed on the STUDENT.
        var students = (await conn.QueryAsync<(long StudentId, long EnrollmentId)>(
            @"SELECT s.id AS StudentId, e.id AS EnrollmentId
                FROM student s
                JOIN studentenrollment e ON e.studentid = s.id AND e.campusid = s.campusid
               WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                 AND s.isactive = true
               ORDER BY s.id
               LIMIT @limit",
            new { tenantId, schoolId, campusId, limit = options.Students })).ToList();

        if (students.Count == 0)
            throw new InvalidOperationException(
                $"campus {campusId} holds no enrolled student, so no profile row could reference one - "
                + "run PerfDatasetTests first.");

        // `guardian.nationality` is a SMALLINT - the id of the `country` row, NOT its name (the
        // `country` table's own `shortname`/`nationality` columns carry the labels the dropdown
        // shows). Resolved by ISO code so the fixture cannot depend on the identity ordering.
        var nationalityId = await conn.ExecuteScalarAsync<short?>(
            "SELECT id FROM country WHERE code = 'AE' ORDER BY id LIMIT 1") ?? 1;

        // The campus's terms, through its academic year - `terms` has NO scope columns of its own.
        var termIds = (await conn.QueryAsync<long>(
            @"SELECT t.id FROM terms t
                JOIN academicyear ay ON ay.id = t.academicyearid
               WHERE ay.tenantid = @tenantId AND ay.schoolid = @schoolId AND ay.campusid = @campusId
               ORDER BY t.id",
            new { tenantId, schoolId, campusId })).ToList();

        if (options.Force && existing > 0)
        {
            // `studentguardian` has NO scope columns, so it is cleared through its PARENT - a scoped
            // DELETE there is a 42703 that `ClearTableAsync` does NOT tolerate (it only catches the FK
            // refusal), which would abort the whole seed.
            await ClearTableByParentAsync(conn, "studentguardian", "studentid", "student",
                tenantId, schoolId, campusId);

            foreach (var table in new[]
                     {
                         "studentimmunization", "studenthealth", "studentcontact",
                         "studenttermresult", "studentsettlement", "guardian"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose) Console.WriteLine($"  Student profile: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. The guardian register, one row per covered student, plus its link row.
        // ------------------------------------------------------------------
        for (var i = 0; i < students.Count; i++)
        {
            var student = students[i];
            var guardianName = $"Perf Guardian {campusId}-{i + 1}";

            var guardianId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO guardian
                      (name, mobile, email, nationality, tenantid, schoolid, campusid,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@name, @mobile, @email, @nationality, @tenantId, @schoolId, @campusId,
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    name = guardianName,
                    nationality = nationalityId,
                    mobile = $"050{campusId:D3}{(100000 + i):D7}"[..12],
                    email = $"perf.guardian.{campusId}.{i + 1}@perf.test",
                    tenantId, schoolId, campusId, now
                });
            result.Guardians++;

            await conn.ExecuteAsync(
                @"INSERT INTO studentguardian
                      (studentid, guardianid, relationshipid, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@studentId, @guardianId, @relationshipId, 1, 1, @now, @now)",
                new
                {
                    studentId = student.StudentId,
                    guardianId,
                    // `StaticEntities.Relationship` - cycled across the whole enum so the grid shows
                    // more than one relationship label.
                    relationshipId = (short)((i % RelationshipCount) + 1),
                    now
                });
            result.StudentGuardians++;
        }

        // ------------------------------------------------------------------
        // 2. Emergency / pickup contacts. `ContactTypeId` is the ContactType enum, cycled.
        // ------------------------------------------------------------------
        for (var i = 0; i < students.Count; i++)
        {
            var student = students[i];
            for (var c = 0; c < options.ContactsPerStudent; c++)
            {
                var contactType = ContactTypes[c % ContactTypes.Length];
                await conn.ExecuteAsync(
                    @"INSERT INTO studentcontact
                          (contacttypeid, name, mobile, studentid, tenantid, schoolid, campusid,
                           createdby, modifiedby, createdon, modifiedon)
                      VALUES (@contactTypeId, @name, @mobile, @studentId, @tenantId, @schoolId, @campusId,
                              1, 1, @now, @now)",
                    new
                    {
                        contactTypeId = contactType,
                        name = $"Perf Contact {campusId}-{i + 1}-{c + 1}",
                        mobile = $"055{campusId:D3}{(200000 + i * 4 + c):D7}"[..12],
                        studentId = student.StudentId,
                        tenantId, schoolId, campusId, now
                    });
                result.Contacts++;
            }
        }

        // ------------------------------------------------------------------
        // 3. The health record - ONE row per student, which is what the Health tab renders.
        // ------------------------------------------------------------------
        for (var i = 0; i < students.Count; i++)
        {
            var student = students[i];
            await conn.ExecuteAsync(
                @"INSERT INTO studenthealth
                      (height, weight, bloodgroup, dietpreference, remarks, studentid,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@height, @weight, @bloodGroup, @diet, NULL, @studentId,
                          @tenantId, @schoolId, @campusId, 1, 1, @now, @now)",
                new
                {
                    // ⚠️ `height` and `weight` are SMALLINT (and `dietpreference` is the
                    // `DietPreference` enum, not free text) - a decimal would fail the bind.
                    height = (short)(100 + (i % 60)),
                    weight = (short)(20 + (i % 40)),
                    bloodGroup = BloodGroups[i % BloodGroups.Length],
                    diet = (short)((i % DietPreferenceCount) + 1),
                    studentId = student.StudentId,
                    tenantId, schoolId, campusId, now
                });
            result.HealthRecords++;
        }

        // ------------------------------------------------------------------
        // 4. Vaccinations.
        //
        // ⚠️ TWO COLUMNS ARE NOT WHAT THEIR NAMES SUGGEST. `status` is the `ImmunizationStatus`
        // enum (Pending = 1, Done = 2) as a `smallint`, and `fileproof` is a **bytea** - the file
        // bytes, not a path - and it is NOT NULL. An empty `byte[]` is what an "upload optional"
        // row carries (the same legacy-ORM artifact the teacher table has for dob/nic/address).
        // ------------------------------------------------------------------
        for (var i = 0; i < students.Count; i++)
        {
            var student = students[i];
            for (var v = 0; v < options.ImmunizationsPerStudent; v++)
            {
                var administered = DateTime.Today.AddMonths(-(6 + v * 4));
                await conn.ExecuteAsync(
                    @"INSERT INTO studentimmunization
                          (vaccinename, administrateddate, expirydate, status, fileproof, studentid,
                           tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@vaccine, @administered, @expiry, @status, @fileProof, @studentId,
                              @tenantId, @schoolId, @campusId, 1, 1, @now, @now)",
                    new
                    {
                        vaccine = Vaccines[(i + v) % Vaccines.Length],
                        administered,
                        expiry = administered.AddYears(5),
                        status = ImmunizationStatuses[(i + v) % ImmunizationStatuses.Length],
                        fileProof = Array.Empty<byte>(),
                        studentId = student.StudentId,
                        tenantId, schoolId, campusId, now
                    });
                result.Immunizations++;
            }
        }

        // ------------------------------------------------------------------
        // 5. Term results - one per enrollment per term. `termid` must belong to THIS campus's year;
        //    the result pipeline writes these, and the term-result grid reads them per term.
        // ------------------------------------------------------------------
        var termsToUse = Math.Min(options.TermResultsPerStudent, termIds.Count);
        if (termsToUse > 0)
        {
            for (var i = 0; i < students.Count; i++)
            {
                var student = students[i];
                for (var t = 0; t < termsToUse; t++)
                {
                    var total = 500m;
                    var obtained = 300m + (i % 190);
                    var percentage = Math.Round(obtained / total * 100m, 2);

                    await conn.ExecuteAsync(
                        @"INSERT INTO studenttermresult
                              (tenantid, schoolid, campusid, studentenrollmentid, termid,
                               totalmarks, obtained, percentage, grade, rank, promotionstatus,
                               gradepoint, createdby, modifiedby, createdon, modifiedon)
                          VALUES (@tenantId, @schoolId, @campusId, @enrollmentId, @termId,
                                  @total, @obtained, @percentage, @grade, @rank, @promotionStatus,
                                  @gradePoint, 1, 1, @now, @now)",
                        new
                        {
                            tenantId, schoolId, campusId,
                            enrollmentId = student.EnrollmentId,
                            termId = termIds[t],
                            total, obtained, percentage,
                            grade = Grades[(int)(percentage / 25) > 3 ? 3 : (int)(percentage / 25)],
                            rank = (i % 40) + 1,
                            promotionStatus = PromotionStatuses[i % PromotionStatuses.Length],
                            gradePoint = Math.Round(percentage / 25m, 2),
                            now
                        });
                    result.TermResults++;
                }
            }
        }

        // ------------------------------------------------------------------
        // 6. The closed account statement - one snapshot per student.
        //
        // `settlementnumber` is NOT unique in the schema, but the application generates one per
        // snapshot, so the campus id is stamped into it: a duplicate would only ever be a fixture
        // colliding with itself on a re-run.
        // ------------------------------------------------------------------
        for (var i = 0; i < students.Count; i++)
        {
            var student = students[i];
            var charges = 8_000m + (i % 20) * 250m;
            var discounts = i % 4 == 0 ? 500m : 0m;
            var tax = Math.Round((charges - discounts) * 0.05m, 2);
            var adjustments = i % 6 == 0 ? -200m : 0m;

            await conn.ExecuteAsync(
                @"INSERT INTO studentsettlement
                      (settlementnumber, studentid, studentenrollmentid, settlementdate,
                       totalcharges, totaldiscounts, totaltax, totalpayments, totalcredits,
                       totalrefunds, totaladjustments, outstandingamount, creditamount, status, remarks,
                       tenantid, schoolid, campusid, createdby, createdon, modifiedby, modifiedon)
                  VALUES (@number, @studentId, @enrollmentId, @settlementDate,
                          @charges, @discounts, @tax, @payments, @credits,
                          0, @adjustments, @outstanding, 0, @status, NULL,
                          @tenantId, @schoolId, @campusId, 1, @now, 1, @now)",
                new
                {
                    number = $"PERF-SET-{campusId}-{i + 1:D5}",
                    studentId = student.StudentId,
                    enrollmentId = student.EnrollmentId,
                    settlementDate = DateTime.Today.AddDays(-(i % 90)),
                    charges, discounts, tax,
                    // The snapshot is a statement, so payments never exceed what is owed.
                    payments = charges - discounts + tax + adjustments - (i % 3 == 0 ? 0m : 100m),
                    credits = 0m,
                    adjustments,
                    outstanding = i % 3 == 0 ? charges - discounts + tax + adjustments : 0m,
                    // ⚠️ `status` is the `StudentSettlementStatus` enum as a `smallint` - the
                    // repository DERIVES it from the ledger and its own comment freezes the numbers.
                    // A fully-paid snapshot is Settled (2); one still owing is PartiallySettled (3)
                    // rather than Open (1), because a computed snapshot always has a ledger behind it.
                    status = i % 3 == 0 ? SettlementPartiallySettled : SettlementSettled,
                    tenantId, schoolId, campusId, now
                });
            result.Settlements++;
        }

        if (verbose)
        {
            Console.WriteLine(
                $"  Student profile: campus {campusId} -> {result.Guardians:N0} guardians + "
                + $"{result.StudentGuardians:N0} links, {result.Contacts:N0} contacts, "
                + $"{result.HealthRecords:N0} health, {result.Immunizations:N0} vaccinations, "
                + $"{result.TermResults:N0} term results, {result.Settlements:N0} settlements");
        }

        return result;
    }

    /// <summary>Fills <paramref name="result"/> from what the campus already holds (the skip path).</summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, StudentProfileSeedResult result)
    {
        async Task<int> ScopedAsync(string table)
        {
            return await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table}"
                + " WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId, schoolId, campusId });
        }

        result.Guardians = await ScopedAsync("guardian");
        result.Contacts = await ScopedAsync("studentcontact");
        result.HealthRecords = await ScopedAsync("studenthealth");
        result.Immunizations = await ScopedAsync("studentimmunization");
        result.TermResults = await ScopedAsync("studenttermresult");
        result.Settlements = await ScopedAsync("studentsettlement");

        // `studentguardian` has no scope columns of its own, so it is counted through its parent.
        result.StudentGuardians = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM studentguardian sg
               WHERE sg.studentid IN (
                     SELECT id FROM student
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)",
            new { tenantId, schoolId, campusId });
    }
}
