using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the STUDENT PROFILE workspace (`guardian` + `studentguardian`, `studentcontact`,
/// `studenthealth`, `studentimmunization`, `studenttermresult`, `studentsettlement`) and asserts the
/// shape that makes their specs measure instead of report SKIP.
///
/// ⚠️ WHY THESE SEVEN LIVE IN ONE FIXTURE. Every one of them held ZERO rows in every database here, so
/// their specs reported **SKIP**: honest, and useless at once - `student.create.html`'s Contacts /
/// Guardian / Health / Immunization tabs, the term-result grid and `student.settlement.html` are all
/// screens the application ships.
///
/// ⚠️ IT DEPENDS ON `PerfDatasetTests` ONLY - the student and its enrollment already exist in bulk
/// (879k students / 9.4k `studentenrollment` campus-wide). This seeder never invents a student.
///
/// ⚠️ EVERY ASSERTION IS A JOIN, A CONSTRAINT, OR AN ENUM RANGE - never a row count. The six that
/// matter:
///   * `studentguardian` HAS NO SCOPE COLUMNS, so the only thing that proves a link belongs to this
///     campus is that its GUARDIAN's campus equals its STUDENT's - a mismatch is a guardian of
///     another campus attached to a student here, i.e. data that leaks across the scope model;
///   * the four scope-carrying children (contact / health / immunization / settlement) must share
///     their student's scope triple, because each repository filters on the row's OWN columns;
///   * `studenttermresult.termid` must belong to an academic year of the SAME campus - `terms` has no
///     scope columns and hangs off `academicyear`, so a term from another campus's year is a
///     contradiction the result grid would render against the wrong year;
///   * `studentcontact.contacttypeid` must be one of `StaticEntities.ContactType` (1..3) and
///     `studentguardian.relationshipid` one of `StaticEntities.Relationship` (1..17) - both are read
///     from an ENUM IN CODE with no lookup table and no foreign key, so an out-of-range value is a row
///     the screen renders with no label at all and the database would never object;
///   * no settlement may report a NEGATIVE outstanding or credit amount.
///
/// Opt in with the same flag the other dataset seeders use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~StudentProfileDataset"
/// </summary>
public sealed class StudentProfileDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public StudentProfileDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Student_profile_dataset_fills_the_parent_health_and_result_tables_their_grids_read()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the student-profile perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed profile rows into - " +
            "run PerfDatasetTests first");

        var options = new StudentProfileSeedOptions
        {
            Students = SeedCampuses.EnvInt("SCUBE_PERF_PROFILE_STUDENTS", 400),
            ContactsPerStudent = SeedCampuses.EnvInt("SCUBE_PERF_PROFILE_CONTACTS", 2),
            ImmunizationsPerStudent = SeedCampuses.EnvInt("SCUBE_PERF_PROFILE_IMMUNIZATIONS", 2),
            TermResultsPerStudent = SeedCampuses.EnvInt("SCUBE_PERF_PROFILE_TERM_RESULTS", 2),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding STUDENT PROFILE for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.Students} students, " +
                          $"{options.ContactsPerStudent} contacts each, " +
                          $"{options.ImmunizationsPerStudent} vaccinations each, " +
                          $"{options.TermResultsPerStudent} term results each");
        _output.WriteLine("");

        var seeder = new StudentProfileSeeder(_connectionString);
        var totalRows = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRows += result.Guardians + result.StudentGuardians + result.Contacts +
                         result.HealthRecords + result.Immunizations + result.TermResults +
                         result.Settlements;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Guardians,4} guardians {result.StudentGuardians,4} links " +
                $"{result.Contacts,4} contacts {result.HealthRecords,4} health " +
                $"{result.Immunizations,4} vaccinations {result.TermResults,4} term results " +
                $"{result.Settlements,4} settlements" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            await AssertProfileRowsAreReachableAsync(conn, campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRows:N0} student-profile rows");

        Assert.True(totalRows > 0,
            "no profile rows were seeded, so the grids over these tables would still report SKIP");

        await AssertEnumColumnsAreInRangeAsync(conn, campusIds);
        await AssertNoNegativeSettlementAmountAsync(conn, campusIds);
    }

    /// <summary>
    /// The joins each seeded row has to satisfy for the APPLICATION to see it - not for the INSERT to
    /// succeed.
    /// </summary>
    private static async Task AssertProfileRowsAreReachableAsync(NpgsqlConnection conn, long campusId)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        // `studentguardian` has NO scope columns at all, so this is the ONLY check that can catch a
        // guardian of another campus attached to a student here.
        var crossCampusLinks = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM studentguardian sg
                JOIN student s ON s.id = sg.studentid
                JOIN guardian g ON g.id = sg.guardianid
               WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
                 AND (g.tenantid, g.schoolid, g.campusid)
                     IS DISTINCT FROM (s.tenantid, s.schoolid, s.campusid)",
            new { tenantId, schoolId, campusId });

        Assert.True(crossCampusLinks == 0,
            $"campus {campusId} has {crossCampusLinks} student-guardian link(s) whose guardian belongs to " +
            "another campus - the link table carries no scope of its own, so nothing else can catch this");

        // The four children that DO carry the triple: each repository filters on the row's own columns.
        foreach (var (table, label) in new[]
                 {
                     ("studentcontact", "contact"),
                     ("studenthealth", "health record"),
                     ("studentimmunization", "vaccination"),
                     ("studentsettlement", "settlement"),
                 })
        {
            var foreign = await conn.ExecuteScalarAsync<long>(
                $@"SELECT COUNT(*) FROM {table} r
                     JOIN student s ON s.id = r.studentid
                    WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                      AND (s.tenantid, s.schoolid, s.campusid)
                          IS DISTINCT FROM (r.tenantid, r.schoolid, r.campusid)",
                new { tenantId, schoolId, campusId });

            Assert.True(foreign == 0,
                $"campus {campusId} holds {foreign} {label}(s) whose scope triple disagrees with their own " +
                "student's - the tab filters on the row's columns and would never list them");
        }

        // `terms` hangs off `academicyear` and has NO scope columns, so the term is checked through
        // its year - a term of another campus's year is a result filed against the wrong year.
        var badTerms = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM studenttermresult r
                JOIN student s ON s.id IN (
                        SELECT id FROM studentenrollment WHERE id = r.studentenrollmentid)
               WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                 AND NOT EXISTS (
                     SELECT 1 FROM terms t
                       JOIN academicyear ay ON ay.id = t.academicyearid
                      WHERE t.id = r.termid AND ay.campusid = @campusId)",
            new { tenantId, schoolId, campusId });

        Assert.True(badTerms == 0,
            $"campus {campusId} holds {badTerms} term result(s) whose term belongs to another campus's " +
            "academic year");
    }

    /// <summary>
    /// ⚠️ TWO COLUMNS ARE ENUMS IN CODE, with no lookup table and NO foreign key:
    /// `studentcontact.contacttypeid` is `StaticEntities.ContactType` (Emergency = 1, Pickup = 2,
    /// Other = 3) and `studentguardian.relationshipid` is `StaticEntities.Relationship` (1..17). The
    /// DATABASE cannot refuse an out-of-range value, so a seeder typo produces a row the screen renders
    /// with no label at all - which is exactly what this asserts against.
    /// </summary>
    private static async Task AssertEnumColumnsAreInRangeAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        const int contactTypeMax = 3;      // StaticEntities.ContactType
        const int relationshipMax = 17;    // StaticEntities.Relationship

        foreach (var campusId in campusIds)
        {
            var badContactTypes = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentcontact
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND (contacttypeid < 1 OR contacttypeid > @max)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId, max = contactTypeMax });

            Assert.True(badContactTypes == 0,
                $"campus {campusId} holds {badContactTypes} contact(s) whose contacttypeid is outside " +
                $"StaticEntities.ContactType (1..{contactTypeMax}) - the Contacts tab would show a blank type");

            var badRelationships = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentguardian sg
                   WHERE sg.studentid IN (
                         SELECT id FROM student
                          WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
                     AND (sg.relationshipid < 1 OR sg.relationshipid > @max)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId, max = relationshipMax });

            Assert.True(badRelationships == 0,
                $"campus {campusId} holds {badRelationships} guardian link(s) whose relationshipid is " +
                $"outside StaticEntities.Relationship (1..{relationshipMax})");
        }
    }

    /// <summary>
    /// A statement is a financial document: a negative outstanding or credit amount is a row that
    /// disagrees with itself, and the settlement screen prints both without a sign guard.
    /// </summary>
    private static async Task AssertNoNegativeSettlementAmountAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var negative = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM studentsettlement
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND (outstandingamount < 0 OR creditamount < 0)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(negative == 0,
                $"campus {campusId} holds {negative} settlement(s) with a negative outstanding or credit " +
                "amount - the statement screen renders both without a sign guard");
        }
    }
}
