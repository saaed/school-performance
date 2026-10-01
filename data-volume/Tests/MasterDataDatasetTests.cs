using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the CAMPUS-LEVEL MASTER DATA the perf dataset leaves empty (`section`, `room`, `holiday`,
/// `discount`, `taxcode`, `approvaltemplate`, `roles`, `campussubject`, `timetable`, `schoolevent`)
/// and asserts the shape that makes their generic grid specs measure instead of report SKIP.
///
/// ⚠️ WHY THESE TEN LIVE IN ONE FIXTURE. Each table held ZERO rows in every database here, so its
/// `GenericRepository.GetAllAsync` spec reported **SKIP** - honestly, and uselessly: a SKIP reads as
/// "not measured yet" for a grid the application ships. The seeding rule they share is stated once
/// here rather than ten times, and the campus selection is the shared helper all the other dataset
/// fixtures use (so a scope that `db-report` does not measure cannot creep in).
///
/// ⚠️ EVERY ASSERTION IS A JOIN OR A CONSTRAINT, NOT A ROW COUNT. A count proves the INSERT ran; it
/// cannot see a row the application cannot reach. The three that matter here:
///   * `campussubject.subjectid` must resolve to a subject of the SAME SCHOOL - a campus "offering"
///     an invented subject id is the documented "a seeded row no query can reach" defect.
///   * `discount`/`taxcode` codes are UNIQUE per tenant, so a seeder that stamps the same code twice
///     fails on the second campus.
///   * `roles` rows must ALL be custom (`issystemrole = false`), because the roles grid shows custom
///     roles only - a seeded system role is invisible to the very screen this exists to measure.
///
/// Opt in with the same flag the other dataset seeders use (it seeds data rather than asserting
/// application behaviour):
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~MasterDataDataset"
/// </summary>
public sealed class MasterDataDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public MasterDataDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Master_data_dataset_fills_the_campus_master_tables_their_grids_read()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the master-data perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed master data into - " +
            "run PerfDatasetTests first");

        var options = new MasterDataSeedOptions
        {
            Sections = SeedCampuses.EnvInt("SCUBE_PERF_SECTIONS", 6),
            Rooms = SeedCampuses.EnvInt("SCUBE_PERF_ROOMS", 8),
            Holidays = SeedCampuses.EnvInt("SCUBE_PERF_HOLIDAYS", 12),
            Discounts = SeedCampuses.EnvInt("SCUBE_PERF_DISCOUNTS", 6),
            TaxCodes = SeedCampuses.EnvInt("SCUBE_PERF_TAXCODES", 5),
            CustomRoles = SeedCampuses.EnvInt("SCUBE_PERF_ROLES", 4),
            SchoolEvents = SeedCampuses.EnvInt("SCUBE_PERF_EVENTS", 12),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding MASTER DATA for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: {options.Sections} sections, {options.Rooms} rooms, " +
                          $"{options.Holidays} holidays, {options.Discounts} discounts, {options.TaxCodes} tax codes, " +
                          $"{options.CustomRoles} custom roles, {options.SchoolEvents} events");
        _output.WriteLine("");

        var seeder = new MasterDataSeeder(_connectionString);
        var totalRows = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalRows += result.Sections + result.Rooms + result.Holidays + result.Discounts +
                         result.TaxCodes + result.ApprovalTemplates + result.CustomRoles +
                         result.CampusSubjects + result.Timetables + result.SchoolEvents;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Sections,3} sections {result.Rooms,3} rooms " +
                $"{result.Holidays,3} holidays {result.Discounts,3} discounts {result.TaxCodes,3} tax codes " +
                $"{result.ApprovalTemplates,3} templates {result.CustomRoles,3} custom roles " +
                $"{result.CampusSubjects,3} campus subjects {result.Timetables,3} timetables " +
                $"{result.SchoolEvents,3} events" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            await AssertCampusRowsAreReachableAsync(conn, campusId, result);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalRows:N0} master-data rows");

        Assert.True(totalRows > 0,
            "no master data was seeded, so the ten scope grids over these tables would still report SKIP");

        await AssertCodesAreUniquePerTenantAsync(conn);
        await AssertBulkRolesAreCustomOnlyAsync(conn);
    }

    /// <summary>
    /// The joins each seeded row has to satisfy for the APPLICATION to see it - not for the INSERT
    /// to succeed. A campus subject pointing at a subject of another school, or a timetable pointing
    /// at another campus's classroom, is a row that exists and is unreachable.
    /// </summary>
    private async Task AssertCampusRowsAreReachableAsync(NpgsqlConnection conn, long campusId,
        MasterDataSeedResult result)
    {
        const long tenantId = SeedCampuses.TenantId;
        const long schoolId = SeedCampuses.SchoolId;

        if (result.CampusSubjects > 0)
        {
            // `subject` is SCHOOL-scoped, so the campus may only offer the school's own catalogue.
            var orphanSubjects = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM campussubject cs
                    WHERE cs.tenantid = @tenantId AND cs.schoolid = @schoolId AND cs.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM subject s
                           WHERE s.id = cs.subjectid AND s.tenantid = @tenantId AND s.schoolid = @schoolId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanSubjects == 0,
                $"campus {campusId} offers {orphanSubjects} subject(s) the school does not own, so those " +
                "rows are attached to nothing - the campus-subject grid would render them with a blank subject");
        }

        if (result.Timetables > 0)
        {
            var orphanClassrooms = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM timetable t
                    WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM classroom c
                           WHERE c.id = t.classroomid AND c.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(orphanClassrooms == 0,
                $"campus {campusId} has {orphanClassrooms} timetable(s) pointing at a classroom of another " +
                "campus - the timetable grid filters by scope and the row would never be listed");
        }

        if (result.SchoolEvents > 0)
        {
            // An event carries BOTH a year and a term, and the controller derives the term from the
            // campus's ACTIVE year - a term from a different campus's year is a contradiction.
            // ⚠️ `terms` HAS NO SCOPE COLUMNS - it hangs off `academicyear`, so the row is checked
            // through its year. (Filtering `terms.campusid` would be a 42703, which is the defect
            // this assertion was written to catch in the seeder in the first place.)
            var badTerms = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM schoolevent e
                    WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
                      AND NOT EXISTS (
                          SELECT 1 FROM terms tm
                            JOIN academicyear ay ON ay.id = tm.academicyearid
                           WHERE tm.id = e.termid AND ay.campusid = @campusId)",
                new { tenantId, schoolId, campusId });

            Assert.True(badTerms == 0,
                $"campus {campusId} has {badTerms} event(s) whose term belongs to another campus");
        }

        if (result.ApprovalTemplates > 0)
        {
            // `ux_approvaltemplate_one_active_per_module` is a PARTIAL unique index - at most one
            // ACTIVE template per module. The seeder writes the eight canonical modules; a duplicate
            // would have been refused by the index, so a mismatch here means the index is missing.
            var distinctModules = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(DISTINCT lower(modulename)) FROM approvaltemplate
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND isactive = true",
                new { tenantId, schoolId, campusId });

            Assert.True(distinctModules == result.ApprovalTemplates,
                $"campus {campusId} holds {result.ApprovalTemplates} templates across only {distinctModules} " +
                "module(s) - two ACTIVE templates for one module flip which approval chain runs");
        }
    }

    /// <summary>
    /// `ux_discount_tenantcode` and `ux_taxcode_tenantcode` are UNIQUE on (tenantid, code), which is
    /// what stops two campuses colliding - the seeder stamps the campus id into every code.
    /// </summary>
    private static async Task AssertCodesAreUniquePerTenantAsync(NpgsqlConnection conn)
    {
        foreach (var (table, column) in new[] { ("discount", "code"), ("taxcode", "code") })
        {
            var duplicates = await conn.QueryAsync<string>(
                $@"SELECT {column} FROM {table}
                    WHERE tenantid = @tenantId
                    GROUP BY {column} HAVING COUNT(*) > 1",
                new { tenantId = SeedCampuses.TenantId });

            var list = duplicates.ToList();
            Assert.True(list.Count == 0,
                $"{table}.{column} is UNIQUE per tenant (ux_{table}_tenantcode) but the seeder wrote " +
                $"{list.Count} duplicated code(s): {string.Join(", ", list)}");
        }
    }

    /// <summary>
    /// The roles grid shows CUSTOM roles only. A seeded row with `issystemrole = true` would be
    /// invisible to the very screen this dataset exists to make measurable.
    /// </summary>
    private static async Task AssertBulkRolesAreCustomOnlyAsync(NpgsqlConnection conn)
    {
        // The four names are the ones this seeder writes. A role carrying one of them and
        // `issystemrole = true` is a row the roles grid (which filters IsSystemRole == false) would
        // never list - it would make the spec's row count look right while the screen stayed empty.
        var mislabelled = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM roles
               WHERE issystemrole = true
                 AND (name LIKE '%Coordinator%' OR name LIKE '%Supervisor%'
                      OR name LIKE '%Assistant%' OR name LIKE '%Store Keeper%')");

        Assert.True(mislabelled == 0,
            $"{mislabelled} seeded role(s) are marked as SYSTEM roles, so the roles grid " +
            "(which filters IsSystemRole == false) will never list them");
    }
}
