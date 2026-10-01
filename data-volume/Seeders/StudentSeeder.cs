using Dapper;
using Npgsql;
using Bogus;

namespace SchoolPerformance.Seeders;

public class StudentSeeder : BaseSeeder
{
    public StudentSeeder(string connectionString) : base(connectionString) { }

    public async Task SeedAsync(int count, long tenantId, long schoolId, long campusId)
    {
        Console.WriteLine($"Seeding {count:N0} students for Tenant={tenantId}, School={schoolId}, Campus={campusId}...");
        
        using var conn = await OpenConnectionAsync();
        
        // Get existing parent IDs for this scope (student.parentid is NOT NULL)
        var parentIds = (await conn.QueryAsync<long>(
            @"SELECT Id FROM parent WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId",
            new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId })).ToList();

        if (parentIds.Count == 0)
        {
            // ⚠️ parent.userid carries a FOREIGN KEY to users (fk_p_u_userid), and this
            // used to be a hardcoded `1`. That only ever worked because campus 1 already
            // had parents, so this branch never ran: any OTHER campus failed with
            // `23503 insert or update on table "parent" violates foreign key constraint
            // fk_p_u_userid`. Resolve a real user instead of assuming one, and say so
            // plainly when there is none rather than surfacing a raw FK error.
            var userId = await conn.ExecuteScalarAsync<long?>("SELECT MIN(id) FROM users");

            if (userId == null)
                throw new InvalidOperationException(
                    "Cannot seed students: the users table is empty, so no placeholder parent can be " +
                    "created (parent.userid is NOT NULL with a foreign key to users). Seed users first.");

            Console.WriteLine($"No parents found. Seeding a placeholder parent (userid={userId.Value})...");
            await conn.ExecuteAsync(
                @"INSERT INTO parent (Nic, Address, UserId, TenantId, SchoolId, CampusId, CreatedBy, ModifiedBy, CreatedOn, ModifiedOn)
                  VALUES (@Nic, 'Seed Address', @UserId, @TenantId, @SchoolId, @CampusId, 1, 1, @Now, @Now)",
                new
                {
                    Nic = $"SEED-{tenantId}-{schoolId}-{campusId}",
                    UserId = userId.Value,
                    TenantId = tenantId,
                    SchoolId = schoolId,
                    CampusId = campusId,
                    Now = DateTime.UtcNow,
                });
            parentIds.Add(await conn.QueryFirstOrDefaultAsync<long>(
                @"SELECT Id FROM parent WHERE TenantId = @TenantId AND SchoolId = @SchoolId AND CampusId = @CampusId ORDER BY Id DESC LIMIT 1",
                new { TenantId = tenantId, SchoolId = schoolId, CampusId = campusId }));
        }

        Console.WriteLine($"Using {parentIds.Count} parent(s) for student FK");

        // Clear existing test data (skip on FK violation)
        await ClearTableAsync(conn, "student", tenantId, schoolId, campusId);
        
        // Get max id to avoid conflicts with GENERATED ALWAYS AS IDENTITY
        // (we omit id from INSERT and let the sequence handle it)
        
        var batch = new List<object>();
        int inserted = 0;

        for (int i = 0; i < count; i++)
        {
            var f = new Faker();
            var fullName = f.Name.FullName();
            var dob = f.Date.Between(DateTime.Today.AddYears(-18), DateTime.Today.AddYears(-5));
            
            batch.Add(new
            {
                Name = fullName,
                Age = DateTime.UtcNow.Year - dob.Year,
                Dob = dob.ToString("yyyy-MM-dd"),
                Gender = f.PickRandom("Male", "Female"),
                Photo = new byte[0], // empty bytea
                ParentId = parentIds[i % parentIds.Count],
                IsActive = true,
                AdmissionNumber = $"PERF-{tenantId}-{schoolId}-{campusId}-{DateTimeOffset.UtcNow.Ticks}-{i + 1:D7}",
                EnrollmentDate = f.Date.Between(DateTime.UtcNow.AddYears(-2), DateTime.UtcNow),
                Status = 1,
                TenantId = tenantId,
                SchoolId = schoolId,
                CampusId = campusId,
                CreatedBy = (long)1,
                ModifiedBy = (long)1,
                CreatedOn = f.Date.Between(DateTime.UtcNow.AddYears(-2), DateTime.UtcNow),
                ModifiedOn = DateTime.UtcNow
            });

            if (batch.Count >= 1000)
            {
                await InsertBatchAsync(conn, batch);
                inserted += batch.Count;
                LogProgress("Students", inserted, count);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await InsertBatchAsync(conn, batch);
            inserted += batch.Count;
            LogProgress("Students", inserted, count);
        }

        Console.WriteLine($"Seeded {inserted:N0} students");
    }

    private async Task InsertBatchAsync(NpgsqlConnection conn, List<object> batch)
    {
        // Convert anonymous objects to typed lists for unnest-based bulk insert
        var names = new List<string>();
        var ages = new List<int>();
        var dobs = new List<string>();
        var genders = new List<string>();
        var photos = new List<byte[]>();
        var parentIds = new List<long>();
        var isActiveArr = new List<bool>();
        var admNums = new List<string>();
        var enrollDates = new List<DateTime>();
        var statuses = new List<int>();
        var tenantIds = new List<long>();
        var schoolIds = new List<long>();
        var campusIds = new List<long>();
        var createdByArr = new List<long>();
        var modifiedByArr = new List<long>();
        var createdOns = new List<DateTime>();
        var modifiedOns = new List<DateTime>();

        foreach (dynamic item in batch)
        {
            names.Add((string)item.Name);
            ages.Add((int)item.Age);
            dobs.Add((string)item.Dob);
            genders.Add((string)item.Gender);
            photos.Add((byte[])item.Photo);
            parentIds.Add((long)item.ParentId);
            isActiveArr.Add((bool)item.IsActive);
            admNums.Add((string)item.AdmissionNumber);
            enrollDates.Add((DateTime)item.EnrollmentDate);
            statuses.Add((int)item.Status);
            tenantIds.Add((long)item.TenantId);
            schoolIds.Add((long)item.SchoolId);
            campusIds.Add((long)item.CampusId);
            createdByArr.Add((long)item.CreatedBy);
            modifiedByArr.Add((long)item.ModifiedBy);
            createdOns.Add((DateTime)item.CreatedOn);
            modifiedOns.Add((DateTime)item.ModifiedOn);
        }

        var sql = @"INSERT INTO student 
            (Name, Age, Dob, Gender, Photo, ParentId, IsActive, AdmissionNumber, 
             EnrollmentDate, Status, TenantId, SchoolId, CampusId, 
             CreatedBy, ModifiedBy, CreatedOn, ModifiedOn)
            SELECT unnest(@Names), unnest(@Ages), unnest(@Dobs), unnest(@Genders), 
                   unnest(@Photos), unnest(@ParentIds), unnest(@IsActive), unnest(@AdmNums),
                   unnest(@EnrollDates), unnest(@Statuses), unnest(@TenantIds), unnest(@SchoolIds), 
                   unnest(@CampusIds), unnest(@CreatedByArr), unnest(@ModifiedByArr), 
                   unnest(@CreatedOns), unnest(@ModifiedOns)
            ON CONFLICT (admissionnumber) DO NOTHING";
        
        await conn.ExecuteAsync(sql, new
        {
            Names = names.ToArray(),
            Ages = ages.ToArray(),
            Dobs = dobs.ToArray(),
            Genders = genders.ToArray(),
            Photos = photos.ToArray(),
            ParentIds = parentIds.ToArray(),
            IsActive = isActiveArr.ToArray(),
            AdmNums = admNums.ToArray(),
            EnrollDates = enrollDates.ToArray(),
            Statuses = statuses.ToArray(),
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
        return await GetRowCountAsync(conn, "student", tenantId, schoolId, campusId);
    }
}
