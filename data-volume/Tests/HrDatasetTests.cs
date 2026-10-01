using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the HR module's volume tables for SEVERAL campuses, and asserts the shape that makes
/// them measurable.
///
/// ⚠️ WHY SEVERAL CAMPUSES, NOT ONE. The first version seeded a single campus, and the run
/// proved itself wrong: it reported "campus 8 holds 98.36% of all employees" and failed its own
/// distribution check. That check is not a formality - an index on `(tenantid, schoolid,
/// campusid)` cannot be judged on a scope that holds the whole table, which is the exact trap
/// V130 recorded on `student`. A module seeded into one scope can be READ but not MEASURED.
/// So the HR rows are spread across the campuses that already hold students, the same way the
/// fee spine runs for every campus.
///
/// ⚠️ WHY NOT ALL TWENTY. `PerfDatasetSeeder` does run HR for every campus, and that is the
/// right behaviour for a full rebuild. Here the default is a handful, because the property
/// under test - "no single scope owns the table" - is satisfied well before twenty and each
/// campus costs its own employees x days of attendance.
///
/// It is opt-in through the SAME flag as the dataset seeder, because it seeds data rather than
/// asserting application behaviour:
///
///   SCUBE_PERF_DATASET=1 dotnet test SchoolDataVolume.csproj \
///       --filter "FullyQualifiedName~HrDataset"
///
/// Size it with SCUBE_PERF_HR_CAMPUSES (default 6), SCUBE_PERF_HR_EMPLOYEES (default 120),
/// SCUBE_PERF_HR_ATTENDANCE_DAYS (default 60) and SCUBE_PERF_HR_PAYROLL_PERIODS (default 6).
/// </summary>
[Collection("Sequential")]
public class HrDatasetTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;

    private const long TenantId = 1;
    private const long SchoolId = 1;

    public HrDatasetTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = "Server=localhost;Database=ayra_perf;User ID=postgres;Password=whitewolf1234";
    }

    public void Dispose()
    {
        // Nothing to clean up: this fixture is the point of the database.
    }

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SCUBE_PERF_DATASET") == "1";

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    [Fact]
    public async Task HrDataset_seeds_the_hr_volume_tables_across_campuses()
    {
        if (!Enabled)
        {
            _output.WriteLine(
                "SKIPPED: set SCUBE_PERF_DATASET=1 to build the HR perf dataset. " +
                "It is opt-in because it seeds data rather than asserting it.");
            return;
        }

        var options = new HrSeedOptions
        {
            EmployeesPerCampus = EnvInt("SCUBE_PERF_HR_EMPLOYEES", 120),
            AttendanceDaysPerEmployee = EnvInt("SCUBE_PERF_HR_ATTENDANCE_DAYS", 60),
            PayrollPeriods = EnvInt("SCUBE_PERF_HR_PAYROLL_PERIODS", 6),
            Force = Environment.GetEnvironmentVariable("SCUBE_PERF_FORCE") == "1",
        };

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // ⚠️ THE CAMPUS SELECTION IS NOT ARBITRARY, and the first version got it wrong in a way
        // that produced four SKIPs. It took the campuses holding the MOST students - which in this
        // database are the ARTIFACTS of an early single-campus seed (campus 1 holds 843,414 of
        // 879,514 students; a handful of others hold 8,000-10,000), while `db-report` measures the
        // TYPICAL campus, the one at the MEDIAN student count (2,000). Seeding the outliers put all
        // the HR rows exactly where nothing is measured.
        //
        // The rule below therefore EXCLUDES a campus holding more than 10% of the table: a scope
        // that owns the whole table is an artifact by definition, and it is also the one scope on
        // which a scope-column index cannot be judged. What remains is the typical population the
        // tool actually reads - and seeding a spread of it keeps the distribution assertion honest.
        var explicitList = Environment.GetEnvironmentVariable("SCUBE_PERF_HR_CAMPUS_LIST");

        var campusIds = !string.IsNullOrWhiteSpace(explicitList)
            ? explicitList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse).ToList()
            : (await conn.QueryAsync<long>(
                @"WITH counts AS (
                      SELECT campusid, COUNT(*) AS n FROM student
                       WHERE tenantid = @tenantId AND schoolid = @schoolId
                    GROUP BY campusid
                  )
                  SELECT campusid FROM counts
                   WHERE n::numeric / NULLIF((SELECT SUM(n) FROM counts), 0) <= 0.10
                   ORDER BY campusid
                   LIMIT @limit",
                new { tenantId = TenantId, schoolId = SchoolId, limit = EnvInt("SCUBE_PERF_HR_CAMPUSES", 12) }))
                .ToList();

        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed HR into - " +
            "run PerfDatasetTests first");

        _output.WriteLine(
            $"Seeding HR for {campusIds.Count} campus(es) [{string.Join(", ", campusIds)}]: " +
            $"{options.EmployeesPerCampus} employees, {options.AttendanceDaysPerEmployee} attendance days each, " +
            $"{options.PayrollPeriods} payroll periods");
        _output.WriteLine("");

        var seeder = new HrModuleSeeder(_connectionString);
        var totalEmployees = 0;
        var totalAttendance = 0;
        var totalPayroll = 0;

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(TenantId, SchoolId, campusId, options);

            totalEmployees += result.Employees;
            totalAttendance += result.Attendance;
            totalPayroll += result.PayrollRecords;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.Employees,6:N0} employees {result.Attendance,8:N0} attendance " +
                $"{result.PayrollRecords,7:N0} payroll" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalEmployees:N0} employees, {totalAttendance:N0} attendance rows, " +
                          $"{totalPayroll:N0} payroll records");

        // 1. The HR module has volume now. This is the whole point: before this seeder the
        //    employee table held zero rows and every HR spec reported SKIP.
        Assert.True(totalEmployees > 0, "no employees were seeded, so no HR grid is measurable");
        Assert.True(totalAttendance > 0, "no staff attendance was seeded");
        Assert.True(totalPayroll > 0, "no payroll records were seeded");

        // 2. THE REACHABILITY RULE. `AttendanceStatusRepository` matches tenant/school/campus
        //    EXACTLY, and the statuses every database already ships sit at (1,1,1) - so a
        //    campus other than 1 sees an EMPTY list and employeeattendance has nothing to
        //    point at. Every seeded row must reference a status ITS OWN campus can read.
        var unreachableStatusScopes = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM (
                  SELECT DISTINCT ea.tenantid, ea.schoolid, ea.campusid
                    FROM employeeattendance ea
                   WHERE ea.tenantid = @tenantId
                     AND ea.campusid = ANY(@campusIds)
                     AND NOT EXISTS (SELECT 1 FROM attendancestatus s
                                      WHERE s.id = ea.attendancestatusid
                                        AND s.tenantid = ea.tenantid
                                        AND s.schoolid = ea.schoolid
                                        AND s.campusid = ea.campusid)
              ) x",
            new { tenantId = TenantId, campusIds = campusIds.ToArray() });

        Assert.Equal(0, unreachableStatusScopes);

        // 3. The salaries are not all identical, so a SUM/AVG column measures real work
        //    rather than a constant the planner could collapse.
        var distinctNets = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(DISTINCT netsalary) FROM employeepayroll
               WHERE tenantid = @tenantId AND campusid = ANY(@campusIds)",
            new { tenantId = TenantId, campusIds = campusIds.ToArray() });

        Assert.True(distinctNets > 1,
            "every payroll row carries the same net salary, so the grid's money columns are not exercised");

        // 4. ⚠️ THE ASSERTION THAT DECIDES WHETHER ANY OF THE ABOVE CAN BE MEASURED. A
        //    scope-column index is only selective on a campus holding a FRACTION of the
        //    table, and db-report's per-campus specs run against one campus. If a single
        //    campus owns the employees, an index on the scope columns matches ~every row
        //    there and the advice regresses to the useless ~1.0x this whole fixture exists to
        //    prevent. The MEDIAN is what is asserted, not the smallest: the smallest is tiny
        //    in both the good and the bad shape, so it would pass while every reading it
        //    guards was meaningless.
        var distribution = (await conn.QueryAsync<DistributionRow>(
            @"WITH counts AS (
                  SELECT campusid, COUNT(*) AS employees
                    FROM employee
                   WHERE tenantid = @tenantId AND schoolid = @schoolId
                GROUP BY 1
              )
              SELECT campusid,
                     employees,
                     employees::numeric / NULLIF(SUM(employees) OVER (), 0) AS ShareOfEmployees
                FROM counts
            ORDER BY employees",
            new { tenantId = TenantId, schoolId = SchoolId })).ToList();

        var median = distribution[distribution.Count / 2];
        _output.WriteLine("");
        _output.WriteLine(
            $"median campus: {median.CampusId} holding {median.Employees:N0} employees " +
            $"= {median.ShareOfEmployees:P2} of the table");

        Assert.True(median.ShareOfEmployees < 0.50m,
            $"the MEDIAN campus ({median.CampusId}) holds {median.ShareOfEmployees:P2} of all employees - " +
            "a scope-column index cannot be selective on it, so an HR reading would be meaningless. " +
            "Raise SCUBE_PERF_HR_CAMPUSES.");
    }

    private sealed class DistributionRow
    {
        public long CampusId { get; set; }
        public long Employees { get; set; }
        public decimal ShareOfEmployees { get; set; }
    }
}
