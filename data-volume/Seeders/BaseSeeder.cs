using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

public abstract class BaseSeeder
{
    protected readonly string ConnectionString;
    protected readonly Random Random = new();

    protected BaseSeeder(string connectionString)
    {
        ConnectionString = connectionString;
    }

    protected async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    protected async Task<int> GetMaxIdAsync(NpgsqlConnection conn, string table, string idColumn = "Id")
    {
        try
        {
            return await conn.QueryFirstOrDefaultAsync<int>(
                $"SELECT COALESCE(MAX({idColumn}), 0) FROM {table}");
        }
        catch
        {
            return 0;
        }
    }

    protected async Task ClearTableAsync(NpgsqlConnection conn, string table, long tenantId, long schoolId, long campusId)
    {
        try
        {
            // Set a short statement timeout — cascading deletes on student can take minutes
            await conn.ExecuteAsync("SET statement_timeout = '5000'");
            await conn.ExecuteAsync(
                $"DELETE FROM {table} WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId",
                new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId });
        }
        catch (Exception ex) when (
            (ex is NpgsqlException npgsqlEx && npgsqlEx.SqlState == "23503") || // FK violation
            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("statement_timeout", StringComparison.OrdinalIgnoreCase))
        {
            // Skip clearing — just append new data with unique IDs
            Console.WriteLine($"  Skipping clear of {table} (FK/timeout). Appending new data.");
        }
        finally
        {
            try { await conn.ExecuteAsync("SET statement_timeout = '0'"); } catch { }
        }
    }

    /// <summary>
    /// Clear a CHILD table that has no scope columns of its own, by the parent it belongs to.
    ///
    /// ⚠️ WHY THIS IS SEPARATE FROM <see cref="ClearTableAsync"/>. A few child tables carry no
    /// tenant/school/campus at all - `journalentryline` has only `journalentryid`,
    /// `librarybookauthor` only `bookid`, `libraryreadinglistitem` only `readinglistid` - so the
    /// scoped DELETE is a **42703 (column "tenantid" does not exist)**, and unlike the FK refusal
    /// the other helper tolerates, that exception is NOT caught: it aborts the whole seed. Clear
    /// these through their parent instead, before the parent itself is cleared.
    /// </summary>
    protected async Task ClearTableByParentAsync(NpgsqlConnection conn, string childTable,
        string parentColumn, string parentTable, long tenantId, long schoolId, long campusId)
    {
        try
        {
            await conn.ExecuteAsync("SET statement_timeout = '5000'");
            await conn.ExecuteAsync(
                $@"DELETE FROM {childTable} WHERE {parentColumn} IN (
                      SELECT Id FROM {parentTable}
                       WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId)",
                new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId });
        }
        catch (Exception ex) when (
            (ex is NpgsqlException npgsqlEx && npgsqlEx.SqlState == "23503") ||
            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("statement_timeout", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Skipping clear of {childTable} (FK/timeout). Appending new data.");
        }
        finally
        {
            try { await conn.ExecuteAsync("SET statement_timeout = '0'"); } catch { }
        }
    }

    protected async Task<int> GetRowCountAsync(NpgsqlConnection conn, string table, long tenantId, long schoolId, long campusId)
    {
        try
        {
            return await conn.QueryFirstOrDefaultAsync<int>(
                $"SELECT COUNT(*) FROM {table} WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId",
                new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId });
        }
        catch
        {
            return 0;
        }
    }

    protected void LogProgress(string operation, int current, int total)
    {
        var percent = (double)current / total * 100;
        Console.Write($"\r{operation}: {current:N0}/{total:N0} ({percent:F1}%)");
        if (current == total) Console.WriteLine();
    }
}
