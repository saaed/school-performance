using Dapper;

namespace SchoolPerformance.Seeders;

/// <summary>
/// How much of a multi-campus world to build.
/// </summary>
/// <remarks>
/// ⚠️ WHY THIS EXISTS. Every other seeder fills ONE campus, and the tests called it
/// with (1, 1, 1) over and over, so ayra_perf accumulated 842,614 students and
/// 4,369,000 attendance rows all in CAMPUS 1. That shape makes scope-column index
/// advice meaningless: an index on (tenantid, campusid) matches every row in the
/// table, so it measures ~1.0x no matter how good it really is, while a real
/// deployment would see it narrow to one campus in N.
///
/// So this seeder builds the shape a real deployment has - MANY campuses, each with
/// a realistic student count - on top of whatever volume is already there. A campus
/// with 2,000 students sitting inside a table of millions of rows is exactly the
/// question "does a scope index help?", asked honestly.
/// </remarks>
public sealed class PerfDatasetOptions
{
    public long TenantId { get; set; } = 1;
    public long SchoolId { get; set; } = 1;

    /// <summary>How many campuses to fill.</summary>
    public int CampusCount { get; set; } = 20;

    /// <summary>Students in each campus. A real campus holds thousands, not hundreds of thousands.</summary>
    public int StudentsPerCampus { get; set; } = 2000;

    /// <summary>Attendance rows per campus. 0 = derive from the student count (5 rows per student).</summary>
    public int AttendancePerCampus { get; set; }

    /// <summary>
    /// Whether to build the FEE / ENROLMENT spine as well (academic year, term, fee type,
    /// fee structure, an enrolment per student, invoices, lines and payments).
    ///
    /// ⚠️ Without it only five tables of this schema hold rows, so every fee, enrolment, exam,
    /// library and accounting spec reports SKIP. Coverage of a module is gated on its VOLUME, not
    /// on how many specs exist.
    /// </summary>
    public bool IncludeFees { get; set; } = true;

    /// <summary>Invoices generated per enrolled student.</summary>
    public int InvoicesPerStudent { get; set; } = 4;

    /// <summary>
    /// Whether to build the HR module's volume tables as well (department, designation,
    /// employee, employeeattendance, payrollperiod, employeepayroll).
    ///
    /// ⚠️ The same rule as <see cref="IncludeFees"/>: a module is measurable only when its
    /// tables hold rows. Before this existed `ayra_perf` had zero employees, so every HR grid
    /// SKIPPED - not because a spec was missing but because there was nothing to scan.
    /// </summary>
    public bool IncludeHr { get; set; } = true;

    /// <summary>Staff members per campus.</summary>
    public int EmployeesPerCampus { get; set; } = 120;

    /// <summary>Working days of attendance recorded per employee (the HR attendance volume).</summary>
    public int HrAttendanceDaysPerEmployee { get; set; } = 60;

    /// <summary>Monthly payroll periods generated per campus.</summary>
    public int PayrollPeriods { get; set; } = 6;

    /// <summary>
    /// Whether to build the INVENTORY module's volume tables (invcategory, invuom, invlocation,
    /// invsupplier, invitem, invitemuom, invstock, invitemcost, invmovement, invpurchaseorder,
    /// invpoline, invgrn, invgrnline, invasset, invdepreciation, invstockrequest,
    /// invstockadjustment, invstockadjustmentline, invreservation).
    ///
    /// ⚠️ The same rule as <see cref="IncludeFees"/> and <see cref="IncludeHr"/>: those tables
    /// held ZERO rows, so every `inv.*` grid SKIPPED - a module is measurable only when its tables
    /// hold rows.
    /// </summary>
    public bool IncludeInventory { get; set; } = true;

    /// <summary>Catalogue items per campus (the inventory grid's own scale).</summary>
    public int InventoryItemsPerCampus { get; set; } = 200;

    /// <summary>Stock movements per item - the inventory module's volume axis.</summary>
    public int InventoryMovementsPerItem { get; set; } = 30;

    /// <summary>
    /// Whether to build the LIBRARY module's tables (librarycategory, libraryauthor,
    /// librarypublisher, libraryvendor, librarymembershiptier, librarybook, librarybookauthor,
    /// librarybookcopy, librarymember, libraryissue, libraryfine, libraryreservation,
    /// libraryreadinglist, libraryreadinglistitem, libraryacquisition, libraryinventoryaudit).
    /// </summary>
    public bool IncludeLibrary { get; set; } = true;

    /// <summary>Titles in the library catalogue per campus.</summary>
    public int LibraryBooksPerCampus { get; set; } = 300;

    /// <summary>Loans per library member - the library module's volume axis.</summary>
    public int LibraryIssuesPerMember { get; set; } = 4;

    /// <summary>
    /// Whether to build the TRANSPORT module's tables (transportvehicle, transportdriver,
    /// transportattendant, transportroute, transportroutestop, transportvehicleassignment,
    /// transportstudentassignment).
    /// </summary>
    public bool IncludeTransport { get; set; } = true;

    /// <summary>Students riding the bus, drawn from the campus's own enrolments.</summary>
    public int TransportRidersPerCampus { get; set; } = 500;

    /// <summary>
    /// Whether to build the ACCOUNTING module's tables (fiscalyear, fiscalperiod, accountgroup,
    /// account, journalentry, journalentryline, financialposting).
    /// </summary>
    public bool IncludeAccounting { get; set; } = true;

    /// <summary>Journal entries per campus - the accounting module's volume axis.</summary>
    public int JournalEntriesPerCampus { get; set; } = 1500;

    /// <summary>The academic year the fee spine is built for.</summary>
    public int AcademicYearStartYear { get; set; } = 2026;

    public string CampusNamePrefix { get; set; } = "PERF Campus";

    /// <summary>Re-seed a campus that already holds students instead of skipping it.</summary>
    public bool Force { get; set; }

    public int EffectiveAttendancePerCampus =>
        AttendancePerCampus > 0 ? AttendancePerCampus : StudentsPerCampus * 5;
}

public sealed class PerfCampusSummary
{
    public long CampusId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Students { get; set; }
    public int Attendance { get; set; }
    public bool SkippedBecauseItAlreadyHadData { get; set; }
    public bool CreatedNow { get; set; }
}

public sealed class PerfDatasetReport
{
    public List<PerfCampusSummary> Campuses { get; } = new();
    public int CampusesCreated { get; set; }
    public int StudentsSeeded { get; set; }
    public int AttendanceSeeded { get; set; }
    public int CampusesSkipped { get; set; }

    // The fee/enrolment spine, so "which modules does the dataset cover?" has a number.
    public int EnrollmentsSeeded { get; set; }
    public int InvoicesSeeded { get; set; }
    public int InvoiceLinesSeeded { get; set; }
    public int PaymentsSeeded { get; set; }
    public int CampusesSkippedFees { get; set; }

    public int EmployeesSeeded { get; set; }
    public int EmployeeAttendanceSeeded { get; set; }
    public int PayrollRecordsSeeded { get; set; }
    public int CampusesSkippedHr { get; set; }

    public int InventoryItemsSeeded { get; set; }
    public int InventoryMovementsSeeded { get; set; }
    public int CampusesSkippedInventory { get; set; }

    public int LibraryBooksSeeded { get; set; }
    public int LibraryIssuesSeeded { get; set; }
    public int CampusesSkippedLibrary { get; set; }

    public int TransportRidersSeeded { get; set; }
    public int CampusesSkippedTransport { get; set; }

    public int JournalEntriesSeeded { get; set; }
    public int CampusesSkippedAccounting { get; set; }

    public string Describe() =>
        $"{Campuses.Count} campus(es): {CampusesCreated} created, {CampusesSkipped} already had data; " +
        $"seeded {StudentsSeeded:N0} students, {AttendanceSeeded:N0} attendance rows, " +
        $"{EnrollmentsSeeded:N0} enrolments, {InvoicesSeeded:N0} invoices, {PaymentsSeeded:N0} payments, " +
        $"{EmployeesSeeded:N0} employees, {EmployeeAttendanceSeeded:N0} staff-attendance rows, " +
        $"{PayrollRecordsSeeded:N0} payroll records, " +
        $"{InventoryItemsSeeded:N0} inventory items, {InventoryMovementsSeeded:N0} stock movements, " +
        $"{LibraryBooksSeeded:N0} library books, {LibraryIssuesSeeded:N0} library issues, " +
        $"{TransportRidersSeeded:N0} transport riders, {JournalEntriesSeeded:N0} journal entries";
}

/// <summary>
/// Builds a MULTI-CAMPUS performance dataset.
/// </summary>
public sealed class PerfDatasetSeeder : BaseSeeder
{
    public PerfDatasetSeeder(string connectionString) : base(connectionString) { }

    public async Task<PerfDatasetReport> SeedAsync(PerfDatasetOptions options, bool verbose = true)
    {
        void Log(string message)
        {
            if (verbose) Console.WriteLine(message);
        }

        var report = new PerfDatasetReport();
        using var conn = await OpenConnectionAsync();

        // ------------------------------------------------------------------
        // The reference rows a new campus's classroom needs.
        //
        // section / academic grade / class teacher are re-used from an EXISTING
        // classroom in scope rather than fabricated. That is deliberate: this is a
        // CARDINALITY fixture, and the numbers db-report measures depend on row
        // counts and scope columns - not on which section a seeded classroom
        // belongs to. Fabricating sections, grades and teachers would mean
        // building most of the HR module to measure an index.
        // ------------------------------------------------------------------
        var template = await conn.QueryFirstOrDefaultAsync<ClassroomTemplateRow>(
            @"SELECT sectionid        AS SectionId,
                     academicgradeid  AS AcademicGradeId,
                     classteacherid   AS ClassTeacherId
                FROM classroom
               WHERE tenantid = @tenantId AND schoolid = @schoolId
               ORDER BY id LIMIT 1",
            new { tenantId = options.TenantId, schoolId = options.SchoolId });

        if (template == null)
            throw new InvalidOperationException(
                $"No classroom exists for tenant {options.TenantId} / school {options.SchoolId}, so there is no " +
                "section/grade/teacher to build a campus classroom from. Seed one classroom first (the existing " +
                "StudentQueryTests/AttendanceQueryTests do this).");

        var subjectId = await conn.ExecuteScalarAsync<long?>(
            @"SELECT id FROM subject WHERE tenantid = @tenantId AND schoolid = @schoolId ORDER BY id LIMIT 1",
            new { tenantId = options.TenantId, schoolId = options.SchoolId });

        if (subjectId == null || subjectId == 0)
            throw new InvalidOperationException(
                $"No subject exists for tenant {options.TenantId} / school {options.SchoolId}; " +
                "attendance.subjectid is NOT NULL, so attendance cannot be seeded without one.");

        // Copy the convention rather than inventing one: campus.timezone stores a
        // WINDOWS id in this schema (see AGENTS.md), never an IANA one.
        var timezone = await conn.ExecuteScalarAsync<string?>(
            @"SELECT timezone FROM campus WHERE tenantid = @tenantId AND schoolid = @schoolId ORDER BY id LIMIT 1",
            new { tenantId = options.TenantId, schoolId = options.SchoolId })
            ?? "Arabian Standard Time";

        // ------------------------------------------------------------------
        // 1. Campuses
        // ------------------------------------------------------------------
        var existing = (await conn.QueryAsync<CampusRow>(
            @"SELECT id AS Id, name AS Name FROM campus
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND name LIKE @prefix
               ORDER BY id",
            new { tenantId = options.TenantId, schoolId = options.SchoolId, prefix = options.CampusNamePrefix + "%" }))
            .ToList();

        var createdIds = new HashSet<long>();

        for (var i = existing.Count; i < options.CampusCount; i++)
        {
            var name = $"{options.CampusNamePrefix} {i + 1:D3}";
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO campus
                      (tenantid, schoolid, name, timezone, createdby, modifiedby, createdon, modifiedon, isactive)
                  VALUES
                      (@tenantId, @schoolId, @name, @timezone, 1, 1, @now, @now, true)
                  RETURNING id",
                new
                {
                    tenantId = options.TenantId,
                    schoolId = options.SchoolId,
                    name,
                    timezone,
                    now = DateTime.UtcNow,
                });

            existing.Add(new CampusRow { Id = id, Name = name });
            createdIds.Add(id);
            report.CampusesCreated++;
            Log($"  created campus {id} '{name}'");
        }

        // ------------------------------------------------------------------
        // 2. One classroom per campus - AttendanceSeeder refuses a campus that
        //    has none, and attendance.classroomid is NOT NULL.
        // ------------------------------------------------------------------
        foreach (var campus in existing)
        {
            var hasClassroom = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM classroom WHERE tenantid = @tenantId AND campusid = @campusId",
                new { tenantId = options.TenantId, campusId = campus.Id });

            if (hasClassroom > 0) continue;

            await conn.ExecuteAsync(
                @"INSERT INTO classroom
                      (sectionid, academicgradeid, classteacherid, classroomname, capacity,
                       tenantid, schoolid, campusid, createdby, modifiedby, createdon, modifiedon)
                  VALUES
                      (@sectionId, @gradeId, @teacherId, @classroomName, 40,
                       @tenantId, @schoolId, @campusId, 1, 1, @now, @now)",
                new
                {
                    sectionId = template.SectionId,
                    gradeId = template.AcademicGradeId,
                    teacherId = template.ClassTeacherId,
                    classroomName = $"PERF Classroom {campus.Id}",
                    tenantId = options.TenantId,
                    schoolId = options.SchoolId,
                    campusId = campus.Id,
                    now = DateTime.UtcNow,
                });

            Log($"  created a classroom for campus {campus.Id}");
        }

        // ------------------------------------------------------------------
        // 3. Students, then attendance, per campus
        // ------------------------------------------------------------------
        var studentSeeder = new StudentSeeder(ConnectionString);
        var attendanceSeeder = new AttendanceSeeder(ConnectionString);

        foreach (var campus in existing)
        {
            var already = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND campusid = @campusId",
                new { tenantId = options.TenantId, campusId = campus.Id });

            var summary = new PerfCampusSummary
            {
                CampusId = campus.Id,
                Name = campus.Name,
                CreatedNow = createdIds.Contains(campus.Id),
            };

            // Idempotent by default: re-running must not silently double the volume,
            // which is how the single campus reached 842k rows in the first place.
            if (already > 0 && !options.Force)
            {
                summary.SkippedBecauseItAlreadyHadData = true;
                summary.Students = (int)already;
                summary.Attendance = (int)await conn.ExecuteScalarAsync<long>(
                    @"SELECT COUNT(*) FROM attendance WHERE tenantid = @tenantId AND campusid = @campusId",
                    new { tenantId = options.TenantId, campusId = campus.Id });
                report.CampusesSkipped++;
                Log($"  campus {campus.Id} '{campus.Name}' already holds {already:N0} students - skipped");
                report.Campuses.Add(summary);
                continue;
            }

            Log($"  seeding campus {campus.Id} '{campus.Name}': {options.StudentsPerCampus:N0} students, " +
                $"{options.EffectiveAttendancePerCampus:N0} attendance rows");

            await studentSeeder.SeedAsync(options.StudentsPerCampus, options.TenantId, options.SchoolId, campus.Id);
            await attendanceSeeder.SeedAsync(
                options.EffectiveAttendancePerCampus, options.TenantId, options.SchoolId, campus.Id);

            summary.Students = options.StudentsPerCampus;
            summary.Attendance = options.EffectiveAttendancePerCampus;
            report.StudentsSeeded += options.StudentsPerCampus;
            report.AttendanceSeeded += options.EffectiveAttendancePerCampus;
            report.Campuses.Add(summary);
        }

        // ------------------------------------------------------------------
        // 4. The fee / enrolment spine, per campus.
        //
        // ⚠️ It runs for EVERY campus, including the ones skipped above. A campus that
        // already had students from an earlier run (before this seeder existed) still
        // needs its enrolments - otherwise "already had data" silently means "and no fee
        // volume", which is the state that made every fee spec SKIP.
        // ------------------------------------------------------------------
        if (options.IncludeFees)
        {
            var feesSeeder = new FeesModuleSeeder(ConnectionString);
            var feesOptions = new FeesSeedOptions
            {
                InvoicesPerStudent = options.InvoicesPerStudent,
                AcademicYearStartYear = options.AcademicYearStartYear,
                Force = options.Force,
            };

            foreach (var campus in existing)
            {
                var classroom = await conn.QueryFirstOrDefaultAsync<CampusClassroomRow>(
                    @"SELECT id AS Id, academicgradeid AS AcademicGradeId
                        FROM classroom
                       WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                       ORDER BY id LIMIT 1",
                    new { tenantId = options.TenantId, schoolId = options.SchoolId, campusId = campus.Id });

                if (classroom == null)
                {
                    // Every campus gets a classroom in step 2, so this is a real inconsistency.
                    throw new InvalidOperationException(
                        $"Campus {campus.Id} has no classroom, so its students cannot be enrolled " +
                        "(studentenrollment.classroomid is what the fee and enrolment grids join on).");
                }

                var fees = await feesSeeder.SeedAsync(
                    options.TenantId, options.SchoolId, campus.Id,
                    classroom.Id, classroom.AcademicGradeId,
                    feesOptions, verbose);

                if (fees.Skipped) report.CampusesSkippedFees++;
                report.EnrollmentsSeeded += fees.Enrollments;
                report.InvoicesSeeded += fees.Invoices;
                report.InvoiceLinesSeeded += fees.InvoiceLines;
                report.PaymentsSeeded += fees.Payments;
            }
        }

        // ------------------------------------------------------------------
        // 5. The HR volume tables, per campus.
        //
        // Like the fee spine, this runs for EVERY campus - including the ones step 3
        // skipped. "Already had students" must not silently mean "and no HR data",
        // which is exactly how the fee seeder's own gap behaved before step 4 existed.
        // ------------------------------------------------------------------
        if (options.IncludeHr)
        {
            var hrSeeder = new HrModuleSeeder(ConnectionString);
            var hrOptions = new HrSeedOptions
            {
                EmployeesPerCampus = options.EmployeesPerCampus,
                AttendanceDaysPerEmployee = options.HrAttendanceDaysPerEmployee,
                PayrollPeriods = options.PayrollPeriods,
                Force = options.Force,
            };

            foreach (var campus in existing)
            {
                var hr = await hrSeeder.SeedAsync(
                    options.TenantId, options.SchoolId, campus.Id, hrOptions, verbose);

                if (hr.Skipped) report.CampusesSkippedHr++;
                report.EmployeesSeeded += hr.Employees;
                report.EmployeeAttendanceSeeded += hr.Attendance;
                report.PayrollRecordsSeeded += hr.PayrollRecords;
            }
        }

        // ------------------------------------------------------------------
        // 6. The INVENTORY / PROCUREMENT module, per campus.
        //
        // Same shape as the fee spine and the HR module: it runs for EVERY campus, including the
        // ones step 3 skipped, so "already had students" never silently means "and no inventory".
        // ------------------------------------------------------------------
        if (options.IncludeInventory)
        {
            var inventorySeeder = new InventoryModuleSeeder(ConnectionString);
            var inventoryOptions = new InventorySeedOptions
            {
                ItemsPerCampus = options.InventoryItemsPerCampus,
                MovementsPerItem = options.InventoryMovementsPerItem,
                Force = options.Force,
            };

            foreach (var campus in existing)
            {
                var inventory = await inventorySeeder.SeedAsync(
                    options.TenantId, options.SchoolId, campus.Id, inventoryOptions, verbose);

                if (inventory.Skipped) report.CampusesSkippedInventory++;
                report.InventoryItemsSeeded += inventory.Items;
                report.InventoryMovementsSeeded += inventory.Movements;
            }
        }

        // ------------------------------------------------------------------
        // 7. The LIBRARY module, per campus. It needs the campus's OWN students (members are
        //    drawn from them), which step 3 has already written by now.
        // ------------------------------------------------------------------
        if (options.IncludeLibrary)
        {
            var librarySeeder = new LibraryModuleSeeder(ConnectionString);
            var libraryOptions = new LibrarySeedOptions
            {
                BooksPerCampus = options.LibraryBooksPerCampus,
                IssuesPerMember = options.LibraryIssuesPerMember,
                Force = options.Force,
            };

            foreach (var campus in existing)
            {
                var library = await librarySeeder.SeedAsync(
                    options.TenantId, options.SchoolId, campus.Id, libraryOptions, verbose);

                if (library.Skipped) report.CampusesSkippedLibrary++;
                report.LibraryBooksSeeded += library.Books;
                report.LibraryIssuesSeeded += library.Issues;
            }
        }

        // ------------------------------------------------------------------
        // 8. The TRANSPORT module, per campus. Its riders are keyed on the campus's own
        //    ENROLMENTS, which the fee spine in step 4 wrote.
        // ------------------------------------------------------------------
        if (options.IncludeTransport)
        {
            var transportSeeder = new TransportModuleSeeder(ConnectionString);
            var transportOptions = new TransportSeedOptions
            {
                RidersPerCampus = options.TransportRidersPerCampus,
                Force = options.Force,
            };

            foreach (var campus in existing)
            {
                var transport = await transportSeeder.SeedAsync(
                    options.TenantId, options.SchoolId, campus.Id, transportOptions, verbose);

                if (transport.Skipped) report.CampusesSkippedTransport++;
                report.TransportRidersSeeded += transport.StudentAssignments;
            }
        }

        // ------------------------------------------------------------------
        // 9. The ACCOUNTING module, per campus. Independent of the tiers above except that it
        //    describes the same campus - the journal is written from scratch, not derived.
        // ------------------------------------------------------------------
        if (options.IncludeAccounting)
        {
            var accountingSeeder = new AccountingModuleSeeder(ConnectionString);
            var accountingOptions = new AccountingSeedOptions
            {
                JournalEntriesPerCampus = options.JournalEntriesPerCampus,
                Force = options.Force,
            };

            foreach (var campus in existing)
            {
                var accounting = await accountingSeeder.SeedAsync(
                    options.TenantId, options.SchoolId, campus.Id, accountingOptions, verbose);

                if (accounting.Skipped) report.CampusesSkippedAccounting++;
                report.JournalEntriesSeeded += accounting.JournalEntries;
            }
        }

        // ------------------------------------------------------------------
        // ⚠️ ANALYZE, because bulk-loading changes the numbers the planner reasons
        // from and autovacuum will not have caught up by the time anyone measures.
        // Without this the next db-report run plans against statistics describing the
        // table BEFORE the seed, which shows up as bizarre plan choices (an index scan
        // chosen for a scope that holds 0.2% of the rows) and timings that do not
        // reproduce. Measured the hard way: the student grid planned a parallel sort
        // over the WHOLE table after this seeder had added 36,000 rows.
        // ------------------------------------------------------------------
        // ⚠️ The ACADEMIC-STRUCTURE tables are listed here rather than in a module's
        // TablesToAnalyze, because `classroom` is inserted by THIS seeder (step 2, one per
        // campus) and the rest are the campus scaffold every academic view joins -
        // `studentenrollment` and `terms` are the only ones a module covers, and only when
        // that module is seeded.
        //
        // Measured: with no statistics on `classroom` the planner nested-looped
        // `classroom_pkey` once per row (10,000 times) instead of hash-joining a 22-row
        // table, and `vw_student_attendance`'s report read **120,870 buffer accesses
        // against 40,875 after ANALYZE** - the same query, the same data, five per-row
        // nested loops instead of two. That is the difference between a report that seems
        // slow for no reason and one that meets its budget, so these tables are analyzed
        // unconditionally, not per module.
        var analyze = new List<string>
        {
            "student", "attendance", "parent",
            "classroom", "academicgrade", "curriculumgrade", "academicyear", "section",
        };
        if (options.IncludeFees) analyze.AddRange(FeesModuleSeeder.TablesToAnalyze);
        if (options.IncludeHr) analyze.AddRange(HrModuleSeeder.TablesToAnalyze);
        if (options.IncludeInventory) analyze.AddRange(InventoryModuleSeeder.TablesToAnalyze);
        if (options.IncludeLibrary) analyze.AddRange(LibraryModuleSeeder.TablesToAnalyze);
        if (options.IncludeTransport) analyze.AddRange(TransportModuleSeeder.TablesToAnalyze);
        if (options.IncludeAccounting) analyze.AddRange(AccountingModuleSeeder.TablesToAnalyze);

        Log($"  ANALYZE {string.Join(" / ", analyze)} (so the planner sees the new row counts)");
        foreach (var table in analyze)
            await conn.ExecuteAsync($"ANALYZE {table}");

        return report;
    }

    private sealed class ClassroomTemplateRow
    {
        public long SectionId { get; set; }
        public long AcademicGradeId { get; set; }
        public long ClassTeacherId { get; set; }
    }

    private sealed class CampusRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CampusClassroomRow
    {
        public long Id { get; set; }
        public long AcademicGradeId { get; set; }
    }
}
