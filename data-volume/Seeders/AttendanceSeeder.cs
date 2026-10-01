using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

public class AttendanceSeeder : BaseSeeder
{
    public AttendanceSeeder(string connectionString) : base(connectionString) { }

    public async Task SeedAsync(int count, long tenantId, long schoolId, long campusId)
    {
        Console.WriteLine($"Seeding {count:N0} attendance records for Tenant={tenantId}, School={schoolId}, Campus={campusId}...");
        
        using var conn = await OpenConnectionAsync();
        
        // Get student IDs for this campus
        var studentIds = (await conn.QueryAsync<long>(
            @"SELECT Id FROM student 
              WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId AND IsActive = true",
            new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId })).ToList();

        if (studentIds.Count == 0)
        {
            Console.WriteLine("No students found. Seed students first.");
            return;
        }

        Console.WriteLine($"Found {studentIds.Count:N0} students to generate attendance for");

        // Get classroom IDs for this campus
        var classroomIds = (await conn.QueryAsync<long>(
            @"SELECT Id FROM Classroom 
              WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId",
            new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId })).ToList();

        if (classroomIds.Count == 0)
        {
            Console.WriteLine("No classrooms found for this campus. Cannot seed attendance.");
            return;
        }

        // ------------------------------------------------------------------
        // ⚠️ RESOLVE EACH STUDENT'S ENROLMENT — this is NOT optional bookkeeping.
        //
        // The first version of this seeder never wrote `attendance.studentenrollmentid`.
        // The column is NULLABLE, so PostgreSQL accepted 14,286,500 rows with it NULL and
        // nothing complained. The cost was invisible and total: `vw_student_attendance`
        // reaches every fact row through `JOIN studentenrollment se ON se.id =
        // a.studentenrollmentid`, so with NULL it returned **0 rows on every campus
        // while still scanning the table** — the attendance report looked like "this
        // campus has no attendance", and the perf catalogue measured a query that could
        // never return anything.
        //
        // Attendance is recorded against an ENROLMENT (a student's membership of a
        // classroom for a year), not against a bare student, so the fix is to emit rows
        // only for students who have one and to stamp it. A student with no enrolment is
        // reported rather than silently mis-seeded.
        // ------------------------------------------------------------------
        var enrollmentRows = await conn.QueryAsync<dynamic>(
            @"SELECT DISTINCT ON (studentid) studentid, id
                FROM studentenrollment
               WHERE tenantid = @TenantId AND schoolid = @SchoolId AND campusid = @CampusId
                 AND studentid = ANY(@StudentIds)
               ORDER BY studentid, id DESC",
            new
            {
                TenantId = tenantId,
                SchoolId = schoolId,
                CampusId = campusId,
                StudentIds = studentIds.ToArray()
            });

        var enrollmentByStudent = new Dictionary<long, long>();
        foreach (var row in enrollmentRows)
        {
            enrollmentByStudent[(long)row.studentid] = (long)row.id;
        }

        var enrolledStudentIds = studentIds.Where(id => enrollmentByStudent.ContainsKey(id)).ToList();
        if (enrolledStudentIds.Count == 0)
        {
            Console.WriteLine(
                $"None of this campus's {studentIds.Count:N0} students has a studentenrollment row, so " +
                "attendance would be unreachable by vw_student_attendance. Seed enrolments first " +
                "(FeesModuleSeeder).");
            return;
        }

        if (enrolledStudentIds.Count < studentIds.Count)
        {
            Console.WriteLine(
                $"⚠️  {studentIds.Count - enrolledStudentIds.Count:N0} of {studentIds.Count:N0} students have " +
                "no enrolment and are skipped — attendance against them would be invisible to the views.");
        }

        studentIds = enrolledStudentIds;

        // Get a subject ID (attendance.subjectid is NOT NULL)
        var subjectId = await conn.QueryFirstOrDefaultAsync<long?>(
            @"SELECT Id FROM subject WHERE TenantId = @TenantId AND SchoolId = @SchoolId LIMIT 1",
            new { TenantId = tenantId, SchoolId = schoolId });

        if (subjectId == null || subjectId == 0)
        {
            Console.WriteLine("No subjects found. Cannot seed attendance (subjectid is required).");
            return;
        }

        Console.WriteLine($"Using classroom={classroomIds[0]}, subject={subjectId}");

        // Clear existing attendance (skip on FK violation)
        await ClearTableAsync(conn, "attendance", tenantId, schoolId, campusId);
        
        var batch = new List<object>();
        int inserted = 0;
        var baseDate = DateTime.Today.AddDays(-180); // 6 months of data

        for (int i = 0; i < count; i++)
        {
            var studentId = studentIds[i % studentIds.Count];
            var classroomId = classroomIds[i % classroomIds.Count];
            var attendanceDate = baseDate.AddDays(i % 180);
            
            // Skip weekends
            if (attendanceDate.DayOfWeek == DayOfWeek.Saturday || 
                attendanceDate.DayOfWeek == DayOfWeek.Sunday)
            {
                attendanceDate = attendanceDate.AddDays(2);
            }

            // Weighted random: 80% Present, 10% Absent, 7% Late, 3% Excused
            var rand = Random.Shared.Next(100);
            bool isPresent = rand < 80; // 80% present
            
            batch.Add(new
            {
                StudentId = studentId,
                StudentEnrollmentId = enrollmentByStudent[studentId],
                AttendanceDate = attendanceDate,
                Remarks = (string?)null,
                SubjectId = subjectId,
                ClassroomId = classroomId,
                IsPresent = isPresent,
                TenantId = tenantId,
                SchoolId = schoolId,
                CampusId = campusId,
                CreatedBy = (long)1,
                ModifiedBy = (long)1,
                CreatedOn = DateTime.UtcNow,
                ModifiedOn = DateTime.UtcNow
            });

            if (batch.Count >= 1000)
            {
                await InsertBatchAsync(conn, batch);
                inserted += batch.Count;
                LogProgress("Attendance", inserted, count);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await InsertBatchAsync(conn, batch);
            inserted += batch.Count;
            LogProgress("Attendance", inserted, count);
        }

        Console.WriteLine($"Seeded {inserted:N0} attendance records");
    }

    private async Task InsertBatchAsync(NpgsqlConnection conn, List<object> batch)
    {
        var studentIds = new List<long>();
        var studentEnrollmentIds = new List<long>();
        var dates = new List<DateTime>();
        var remarks = new List<string?>();
        var subjectIds = new List<long>();
        var classroomIds = new List<long>();
        var isPresentArr = new List<bool>();
        var tenantIds = new List<long>();
        var schoolIds = new List<long>();
        var campusIds = new List<long>();
        var createdByArr = new List<long>();
        var modifiedByArr = new List<long>();
        var createdOns = new List<DateTime>();
        var modifiedOns = new List<DateTime>();

        foreach (dynamic item in batch)
        {
            studentIds.Add((long)item.StudentId);
            studentEnrollmentIds.Add((long)item.StudentEnrollmentId);
            dates.Add((DateTime)item.AttendanceDate);
            remarks.Add((string?)item.Remarks);
            subjectIds.Add((long)item.SubjectId);
            classroomIds.Add((long)item.ClassroomId);
            isPresentArr.Add((bool)item.IsPresent);
            tenantIds.Add((long)item.TenantId);
            schoolIds.Add((long)item.SchoolId);
            campusIds.Add((long)item.CampusId);
            createdByArr.Add((long)item.CreatedBy);
            modifiedByArr.Add((long)item.ModifiedBy);
            createdOns.Add((DateTime)item.CreatedOn);
            modifiedOns.Add((DateTime)item.ModifiedOn);
        }

        var sql = @"INSERT INTO attendance 
            (StudentId, StudentEnrollmentId, AttendanceDate, Remarks, SubjectId, ClassroomId, IsPresent,
             TenantId, SchoolId, CampusId, CreatedBy, ModifiedBy, CreatedOn, ModifiedOn)
            SELECT unnest(@StudentIds), unnest(@StudentEnrollmentIds), unnest(@Dates), unnest(@Remarks), 
                   unnest(@SubjectIds), unnest(@ClassroomIds), unnest(@IsPresent), unnest(@TenantIds), 
                   unnest(@SchoolIds), unnest(@CampusIds), unnest(@CreatedByArr), unnest(@ModifiedByArr), 
                   unnest(@CreatedOns), unnest(@ModifiedOns)";
        
        await conn.ExecuteAsync(sql, new
        {
            StudentIds = studentIds.ToArray(),
            StudentEnrollmentIds = studentEnrollmentIds.ToArray(),
            Dates = dates.ToArray(),
            Remarks = remarks.ToArray(),
            SubjectIds = subjectIds.ToArray(),
            ClassroomIds = classroomIds.ToArray(),
            IsPresent = isPresentArr.ToArray(),
            TenantIds = tenantIds.ToArray(),
            SchoolIds = schoolIds.ToArray(),
            CampusIds = campusIds.ToArray(),
            CreatedByArr = createdByArr.ToArray(),
            ModifiedByArr = modifiedByArr.ToArray(),
            CreatedOns = createdOns.ToArray(),
            ModifiedOns = modifiedOns.ToArray()
        });
    }

    public async Task<int> GetCountAsync(long tenantId, long schoolId, long campusId)
    {
        using var conn = await OpenConnectionAsync();
        return await GetRowCountAsync(conn, "attendance", tenantId, schoolId, campusId);
    }
}
