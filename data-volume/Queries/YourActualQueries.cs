using Dapper;
using Npgsql;
using SchoolPerformance.QueryShapes;

namespace SchoolPerformance.Queries;

/// <summary>
/// Runs the APPLICATION's query shapes against the perf database.
///
/// ⚠️ THIS CLASS HOLDS NO SQL OF ITS OWN FOR APP QUERIES. Every shape comes from
/// <see cref="QueryCatalog"/> (the QueryShapes project), which is the single source of truth
/// shared with `db-report`. That is deliberate: the hand-copied SQL that used to live here had
/// drifted from the repositories, and the drift was not cosmetic - it made the measurements
/// describe queries the application does not send:
///
///   * the attendance "list" was copied with `INNER JOIN Student` and a projected column list,
///     while the real path (`AttendanceRepository.GetAll` -> `GenericRepository.GetAllAsync`)
///     emits `SELECT * FROM attendance WHERE TenantId=... AND CampusId=... ORDER BY ... LIMIT
///     ... OFFSET ...` with NO joins. The joins are why this class used to be able to fill
///     <see cref="AttendanceRow.StudentName"/> and <see cref="AttendanceRow.ClassroomName"/> -
///     and why those properties now come back empty, because the real query does not fetch
///     them. An empty StudentName here is the fix, not a regression.
///   * the student list was copied with an explicit column list that had lost the
///     `COALESCE(sc.StudentStatus, 0) AS EnrollmentStatus` the repository added later.
///
/// If a query looks wrong, fix it in QueryCatalog - never here.
/// </summary>
public class YourActualQueries
{
    private readonly string _connectionString;

    public YourActualQueries(string connectionString)
    {
        _connectionString = connectionString;
    }

    private static ScopeVars Scope(long tenantId, long schoolId, long campusId, long classroomId = 1, DateTime? date = null)
        => new()
        {
            TenantId = tenantId,
            SchoolId = schoolId,
            CampusId = campusId,
            ClassroomId = classroomId,
            AttendanceDate = date ?? DateTime.Today,
        };

    // ============================================================
    // STUDENT LIST - StudentRepository.GetAllStudentInfo
    // ============================================================

    /// <summary>
    /// The student grid: a count plus one page of rows, both filtered by the scope and
    /// optionally by the grid's search box.
    /// </summary>
    public async Task<StudentListResult> GetStudentListAsync(
        long tenantId, long schoolId, long campusId,
        string? search = null, int pageSize = QueryCatalog.DefaultPageSize, int offset = QueryCatalog.DefaultOffset)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var specs = QueryCatalog.Build(Scope(tenantId, schoolId, campusId));
        var searching = !string.IsNullOrEmpty(search);

        var countSpec = QueryCatalog.Require(specs, searching ? "student-count-search" : "student-count");
        var listSpec = QueryCatalog.Require(specs, searching ? "student-search" : "student-list-page");

        var countParams = QueryCatalog.ParametersOf(countSpec, tenantId, schoolId, campusId);
        var listParams = QueryCatalog.ParametersOf(listSpec, tenantId, schoolId, campusId);
        listParams["pageSize"] = pageSize;
        listParams["offset"] = offset;

        if (searching)
        {
            var like = $"%{search}%";
            countParams["search"] = like;
            listParams["search"] = like;
        }

        // Two round trips rather than one QueryMultiple: each shape must stay runnable on its
        // own, because db-report measures them individually. Combining them here would make the
        // catalogue's specs depend on each other and would hide the count's own cost.
        var count = await conn.ExecuteScalarAsync<long>(countSpec.Sql, countParams);
        var students = (await conn.QueryAsync<StudentRow>(listSpec.Sql, listParams)).ToList();

        return new StudentListResult { Count = (int)count, Students = students };
    }

    // ============================================================
    // ATTENDANCE LIST - AttendanceRepository.GetAll
    // ============================================================

    /// <summary>
    /// The attendance grid: a count plus one page of rows, newest first.
    ///
    /// Note the shape filters on TenantId + CampusId only - there is no SchoolId in
    /// <c>GenericRepository.GetAllAsync</c>'s predicate, which is why this signature has no
    /// schoolId parameter.
    /// </summary>
    public async Task<AttendanceListResult> GetAttendanceListAsync(
        long tenantId, long campusId,
        int pageSize = QueryCatalog.DefaultPageSize, int offset = QueryCatalog.DefaultOffset)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var specs = QueryCatalog.Build(Scope(tenantId, 0, campusId));
        var countSpec = QueryCatalog.Require(specs, "attendance-count");
        var listSpec = QueryCatalog.Require(specs, "attendance-list-page");

        var countParams = QueryCatalog.ParametersOf(countSpec, tenantId, 0, campusId);
        var listParams = new Dictionary<string, object>(countParams)
        {
            ["pageSize"] = pageSize,
            ["offset"] = offset,
        };

        var count = await conn.ExecuteScalarAsync<long>(countSpec.Sql, countParams);
        var records = (await conn.QueryAsync<AttendanceRow>(listSpec.Sql, listParams)).ToList();

        return new AttendanceListResult { Count = (int)count, Records = records };
    }

    // ============================================================
    // INVOICE LIST - InvoiceRepository.GetAll
    // ============================================================

    /// <summary>
    /// Every invoice for the campus.
    ///
    /// ⚠️ This shape is UNBOUNDED: <c>GenericRepository.FindAsync</c> emits
    /// `SELECT * FROM Invoices WHERE <scope>` with no ORDER BY and no LIMIT, so it returns
    /// every matching row. The old copy here paginated and joined `Student` for a
    /// <see cref="InvoiceRow.StudentName"/> - neither of which the real query does, which is
    /// why StudentName is now empty.
    /// </summary>
    public async Task<List<InvoiceRow>> GetInvoiceListAsync(long tenantId, long schoolId, long campusId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var specs = QueryCatalog.Build(Scope(tenantId, schoolId, campusId));
        var spec = QueryCatalog.Require(specs, "invoice-list-page");

        var rows = await conn.QueryAsync<InvoiceRow>(
            spec.Sql, QueryCatalog.ParametersOf(spec, tenantId, schoolId, campusId));
        return rows.ToList();
    }

    // ==========================================================================================
    // DIAGNOSTIC QUERIES - ⚠️ THESE ARE NOT APPLICATION QUERIES.
    //
    // They are invented aggregates kept because they are useful for asking "how does this shape
    // of work scale?", and they have no repository counterpart to be faithful to. That is why
    // their SQL lives here and NOT in QueryCatalog, whose contract is that every spec cites the
    // repository method it came from.
    //
    // Do not add an application query below this line. It belongs in QueryCatalog.
    // ==========================================================================================

    /// <summary>
    /// Diagnostic: attendance summary per student over a date range. Not an app query.
    /// </summary>
    /// <summary>
    /// The SQL behind <see cref="GetAttendanceSummaryAsync"/>, exposed so a test can EXPLAIN the
    /// exact statement it runs instead of a second hand-copied variant of it.
    /// </summary>
    public const string AttendanceSummarySql = @"
            SELECT 
                s.Id AS StudentId,
                s.Name AS StudentName,
                COUNT(CASE WHEN a.IsPresent = true THEN 1 END) AS PresentDays,
                COUNT(CASE WHEN a.IsPresent = false THEN 1 END) AS AbsentDays,
                COUNT(*) AS TotalDays,
                ROUND(
                    COUNT(CASE WHEN a.IsPresent = true THEN 1 END)::numeric / 
                    NULLIF(COUNT(*), 0)::numeric * 100, 2
                ) AS AttendancePercentage
            FROM Student s
            INNER JOIN StudentEnrollment se ON se.StudentId = s.Id
                AND se.TenantId = s.TenantId AND se.CampusId = s.CampusId
            LEFT JOIN attendance a ON a.StudentId = s.Id 
                AND a.AttendanceDate::date BETWEEN @FromDate::date AND @ToDate::date
                AND a.TenantId = @TenantId AND a.CampusId = @CampusId
            WHERE s.TenantId = @TenantId 
              AND s.CampusId = @CampusId
              AND s.IsActive = true
            GROUP BY s.Id, s.Name
            ORDER BY s.Name";

    public async Task<List<AttendanceSummaryRow>> GetAttendanceSummaryAsync(
        long tenantId, long campusId, DateTime fromDate, DateTime toDate)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        return (await conn.QueryAsync<AttendanceSummaryRow>(AttendanceSummarySql, new
        {
            TenantId = tenantId,
            CampusId = campusId,
            FromDate = fromDate,
            ToDate = toDate
        })).ToList();
    }

    /// <summary>
    /// Diagnostic: a student's overdue balance. Not an app query.
    /// </summary>
    public async Task<StudentDuesRow?> GetStudentDuesAsync(
        long tenantId, long schoolId, long campusId, long studentId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"SELECT StudentId, Sum(TotalAmount - AmountPaid) as Amount 
                    FROM Invoices 
                    WHERE TenantId = @TenantId 
                      AND SchoolId = @SchoolId
                      AND CampusId = @CampusId 
                      AND StudentId = @StudentId
                      AND Date(DueDate) < @Today
                      AND BalanceAmount > 0
                    GROUP BY StudentId";

        return await conn.QueryFirstOrDefaultAsync<StudentDuesRow>(sql, new
        {
            TenantId = tenantId,
            SchoolId = schoolId,
            CampusId = campusId,
            StudentId = studentId,
            Today = DateTime.UtcNow.Date
        });
    }

    /// <summary>
    /// Diagnostic: fee collection aggregated per student. Not an app query.
    /// </summary>
    public async Task<List<FeeCollectionRow>> GetFeeCollectionReportAsync(
        long tenantId, long campusId, DateTime fromDate, DateTime toDate)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT 
                i.StudentId,
                s.Name AS StudentName,
                COUNT(i.Id) AS InvoiceCount,
                SUM(i.TotalAmount) AS TotalAmount,
                SUM(i.AmountPaid) AS PaidAmount,
                SUM(i.BalanceAmount) AS BalanceAmount,
                CASE 
                    WHEN SUM(i.TotalAmount) > 0 
                    THEN ROUND(SUM(i.AmountPaid)::numeric / SUM(i.TotalAmount)::numeric * 100, 2)
                    ELSE 0 
                END AS CollectionPercentage
            FROM invoices i
            INNER JOIN Student s ON s.Id = i.StudentId
            WHERE i.TenantId = @TenantId 
              AND i.CampusId = @CampusId
              AND i.CreatedOn BETWEEN @FromDate AND @ToDate
            GROUP BY i.StudentId, s.Name
            ORDER BY CollectionPercentage ASC";

        return (await conn.QueryAsync<FeeCollectionRow>(sql, new
        {
            TenantId = tenantId,
            CampusId = campusId,
            FromDate = fromDate,
            ToDate = toDate
        })).ToList();
    }
}

// ============================================================
// RESULT DTOs (match the API's response shapes)
// ============================================================

public class StudentListResult
{
    public int Count { get; set; }
    public List<StudentRow> Students { get; set; } = new();
}

public class StudentRow
{
    public long Id { get; set; }
    public string AdmissionNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string Dob { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ClassroomName { get; set; }

    /// <summary>From the active-year enrollment join; 0 when the student has no enrollment.</summary>
    public int EnrollmentStatus { get; set; }

    public long? StudentEnrollmentId { get; set; }
}

public class AttendanceListResult
{
    public int Count { get; set; }
    public List<AttendanceRow> Records { get; set; } = new();
}

public class AttendanceRow
{
    /// <summary>
    /// ⚠️ Always empty: the real attendance list is `SELECT * FROM attendance` with no joins,
    /// so there is no student row to take a name from. Present only so the shape of this DTO
    /// matches what a caller might expect.
    /// </summary>
    public string StudentName { get; set; } = string.Empty;

    /// <summary>Always empty, for the same reason as <see cref="StudentName"/>.</summary>
    public string ClassroomName { get; set; } = string.Empty;

    public long Id { get; set; }
    public long StudentId { get; set; }
    public long ClassroomId { get; set; }
    public long SubjectId { get; set; }
    public DateTime AttendanceDate { get; set; }
    public bool IsPresent { get; set; }
    public string? Remarks { get; set; }
}

public class AttendanceSummaryRow
{
    public long StudentId { get; set; }
    public string StudentName { get; set; } = string.Empty;
    public int PresentDays { get; set; }
    public int AbsentDays { get; set; }
    public int TotalDays { get; set; }
    public decimal AttendancePercentage { get; set; }
}

public class InvoiceRow
{
    public long Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public long StudentId { get; set; }

    /// <summary>⚠️ Always empty: the real invoice list does not join Student.</summary>
    public string StudentName { get; set; } = string.Empty;

    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal BalanceAmount { get; set; }
    public int Status { get; set; }
    public DateTime DueDate { get; set; }
}

public class StudentDuesRow
{
    public long StudentId { get; set; }
    public decimal Amount { get; set; }
}

public class FeeCollectionRow
{
    public long StudentId { get; set; }
    public string StudentName { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount { get; set; }
    public decimal CollectionPercentage { get; set; }
}
