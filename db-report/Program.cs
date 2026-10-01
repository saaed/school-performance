using Dapper;
using Microsoft.Extensions.Configuration;
using SchoolPerformance.DbReport;
using SchoolPerformance.QueryShapes;

var opts = Options.Parse(args);

if (opts.Help)
{
    Options.PrintUsage();
    return 0;
}

if (opts.Errors.Count > 0)
{
    foreach (var error in opts.Errors) Console.Error.WriteLine($"error: {error}");
    Console.Error.WriteLine();
    Options.PrintUsage();
    return 2;
}

var connectionString = ResolveConnectionString();
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("error: no connection string. Set SCUBE_PERF_DB, or ConnectionStrings:PerfTestDatabase in appsettings.json.");
    return 2;
}

var runner = new BenchmarkRunner(connectionString, opts.StatementTimeoutMs);

Console.WriteLine();
Console.WriteLine("=== SCube database performance report ===");
Console.WriteLine($"target    : {Redact(connectionString)}");
Console.WriteLine($"runs      : {opts.Runs} timed iterations per query, after 1 discarded warm-up");
Console.WriteLine($"timeout   : {opts.StatementTimeoutMs} ms statement timeout");

ScopeVars scope;
try
{
    scope = await ResolveScopeAsync(runner, opts.Busiest);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: cannot read the database: {ex.Message}");
    return 2;
}

var volume = await DescribeVolumeAsync(runner, scope);
var scopeSize = await DescribeScopeSizeAsync(runner, scope);
var scopeDescription = $"tenant {scope.TenantId} / school {scope.SchoolId} / campus {scope.CampusId} " +
                       (opts.Busiest
                           ? "(the BUSIEST campus - worst case)"
                           : "(a TYPICAL campus - the median by student count)");

Console.WriteLine($"volume    : {volume}");
Console.WriteLine($"scope     : {scopeDescription}");
Console.WriteLine($"scope size: {scopeSize}");
Console.WriteLine($"register  : classroom {scope.ClassroomId} on {scope.AttendanceDate:yyyy-MM-dd}");
Console.WriteLine($"fee scope : academic year {scope.AcademicYearId}, grade {scope.AcademicGradeId}, enrolment {scope.EnrollmentId}");
Console.WriteLine($"library   : book {scope.LibraryBookId} (the campus's most-copied book)");

var specs = QueryCatalog.Build(scope);
if (opts.Only != null)
{
    specs = specs.Where(s => s.Key.Contains(opts.Only, StringComparison.OrdinalIgnoreCase)
                          || s.Title.Contains(opts.Only, StringComparison.OrdinalIgnoreCase)).ToList();
    if (specs.Count == 0)
    {
        Console.Error.WriteLine($"error: no query matched --only '{opts.Only}'.");
        return 2;
    }
}

var results = new List<Measurement>();
foreach (var spec in specs)
{
    Console.Write($"\r  measuring {spec.Key,-32}");
    results.Add(await runner.MeasureAsync(spec, opts.Runs));
}
Console.Write($"\r{"",-45}\r");

PrintTable(specs, results);
PrintDetails(specs, results);

// Only a probe can prove an index, so --write-migrations implies --advise.
var proven = opts.Advise
    ? await PrintIndexImpactAsync(runner, specs, results, opts)
    : new List<IndexEvidence>();

var context = new MigrationContext(Redact(connectionString), volume, scopeDescription, scopeSize);

if (opts.WriteMigrations)
    WriteMigrations(proven, context, opts);
else
    PrintMigrationHint(proven, opts);

var failures = results.Count(r => r.Verdict is "FAIL" or "ERROR");
var skipped = results.Count(r => r.Verdict == "SKIP");
var passed = results.Count(r => r.Verdict == "OK");
var undecided = results.Count(r => r.Verdict == "UNSTABLE");

Console.WriteLine();
Console.WriteLine(undecided > 0
    ? $"=== {passed} OK / {failures} FAIL / {skipped} SKIP / {undecided} UNSTABLE ==="
    : $"=== {passed} OK / {failures} FAIL / {skipped} SKIP ===");
if (skipped > 0)
    Console.WriteLine("   A SKIP means the measured SCOPE holds too few rows for the timing to mean anything.");
if (undecided > 0)
{
    Console.WriteLine("   An UNSTABLE row is NOT a FAIL and NOT a pass: its samples disagree by 3x+ and its slow end");
    Console.WriteLine("   misses the budget, so this tool cannot decide. It is left in the exit code on purpose -");
    Console.WriteLine("   re-run it alone with more runs, and check the PLAN on both a fast and a slow sample");
    Console.WriteLine("   (`auto_explain.log_min_duration = 0`) before blaming the query.");
}

if (failures + undecided > 0 && !opts.Advise)
    Console.WriteLine("   Re-run with --advise to measure what the suggested index would do (probed, then rolled back).");

// ⚠️ UNSTABLE IS COUNTED IN THE EXIT CODE. A latent incident is not a pass, and the tool's
// exit code is what a gate reads; the headline keeps it in its own bucket so a bimodal
// measurement never reads as "this query is broken".
return opts.ExitZero ? 0 : failures + undecided;

// ---------------------------------------------------------------------------
// Scope resolution - the tool finds the busiest campus itself, so it works on
// any database instead of assuming 1/1/1.
// ---------------------------------------------------------------------------

/// <summary>
/// Pick the campus to measure.
///
/// ⚠️ WHY THIS IS "TYPICAL" AND NOT "BUSIEST" BY DEFAULT. It used to take the
/// busiest campus, which quietly invalidated most of the index advice: if one campus
/// holds every row in the table (which is how ayra_perf was seeded - 842,614
/// students, all in campus 1), then an index on (tenantid, campusid) matches ALL the
/// rows and measures ~1.0x however good it really is. The tool then reports "this
/// index will not help" for an index that helps enormously in a real deployment,
/// where a campus is 1 of N.
///
/// The median campus answers the question a real deployment asks - "a typical campus,
/// inside a table this big" - while --busiest still gives the worst case on demand.
/// </summary>
static async Task<ScopeVars> ResolveScopeAsync(BenchmarkRunner runner, bool busiest)
{
    await using var session = await runner.OpenAsync();
    var scope = new ScopeVars();

    var chosen = busiest
        ? await session.Connection.QueryFirstOrDefaultAsync<ScopeRow>(
            @"SELECT tenantid AS TenantId, schoolid AS SchoolId, campusid AS CampusId
              FROM student GROUP BY 1, 2, 3 ORDER BY COUNT(*) DESC LIMIT 1")
        : await session.Connection.QueryFirstOrDefaultAsync<ScopeRow>(
            @"WITH counts AS (
                  SELECT tenantid, schoolid, campusid, COUNT(*) AS n
                    FROM student GROUP BY 1, 2, 3
              )
              SELECT tenantid AS TenantId, schoolid AS SchoolId, campusid AS CampusId
                FROM counts
            ORDER BY n
              OFFSET (SELECT COUNT(*) / 2 FROM counts)
               LIMIT 1");

    if (chosen != null)
    {
        scope.TenantId = chosen.TenantId;
        scope.SchoolId = chosen.SchoolId;
        scope.CampusId = chosen.CampusId;
    }

    // ------------------------------------------------------------------
    // ⚠️ EVERY query below resolves THIS SCOPE, so every one filters the full
    // tenant + school + campus triple.
    //
    // They used to filter tenant + campus only, which is a different set: the
    // catalogue's own specs and the server's scope filter both compare all three, so
    // a probe that dropped SchoolId was describing a scope nothing else measures. It
    // reads as harmless because campusid is a unique PK and no campus spans two
    // schools - but a row with schoolid = 0 (which this schema does contain; the write
    // paths are known to store 0 rather than NULL) is counted by one and not the other.
    // Verified on ayra_perf: every row carries schoolid = 1, so tightening these changed
    // no number - it removed the latent disagreement.
    //
    // Note this is the OPPOSITE rule to the catalogue's attendance specs, which must
    // keep filtering tenant + campus because that is genuinely what
    // AttendanceRepository.GetAll(page, tenantId, campusId) sends. A probe describes the
    // scope; a spec reproduces the application.
    // ------------------------------------------------------------------
    var classroomId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT classroomid FROM attendance
          WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
          GROUP BY classroomid ORDER BY COUNT(*) DESC LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId });
    scope.ClassroomId = classroomId ?? 0;

    var date = await session.Connection.ExecuteScalarAsync<DateTime?>(
        "SELECT MAX(attendancedate) FROM attendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId });
    if (date.HasValue) scope.AttendanceDate = date.Value.Date;

    // The fee / enrolment grids all filter on the campus's academic year, and reach the grade
    // through Classroom. Both fall back to 0 on a campus that has neither, which is not an error:
    // those specs then report SKIP, because a scope with no year genuinely has nothing to measure.
    scope.AcademicYearId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT id FROM academicyear
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           ORDER BY isactive DESC, id DESC LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    scope.AcademicGradeId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT academicgradeid FROM classroom
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           ORDER BY id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // One real enrolment of this campus, for the per-enrolment outstanding-balance guard.
    scope.EnrollmentId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT id FROM studentenrollment
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           ORDER BY id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's CURRENT ACTIVE TERM, resolved with the application's OWN statement
    // (`TermRepository.GetCurrentActiveTerm`, mirrored verbatim below). The homework and moment
    // grids filter on (classroomId, academicYearId, termId) and the controller fills the last two
    // from this query, so resolving it any other way would describe a set the screen never asks for.
    scope.TermId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT terms.id
            FROM terms
            INNER JOIN academicyear ON terms.academicyearid = academicyear.id
           WHERE CURRENT_DATE BETWEEN terms.startdate AND terms.enddate
             AND academicyear.isactive = true
             AND academicyear.ispublished = true
             AND academicyear.tenantid = @tenantId
             AND academicyear.schoolid = @schoolId
             AND academicyear.campusid = @campusId
           LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's school event holding the MOST responses, for the event detail screen's paged
    // responses - that repository takes the event id the user opened rather than filtering by scope,
    // so the spec has to be handed one, and the busiest event is the worst case rather than the first.
    scope.SchoolEventId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT r.eventid FROM schooleventresponse r
            JOIN schoolevent e ON e.id = r.eventid
           WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
           GROUP BY r.eventid ORDER BY COUNT(*) DESC, r.eventid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId })
        // Falls back to the campus's first event, so a campus whose events have no responses (yet)
        // still measures the read rather than reporting SKIP for a screen that renders.
        ?? await session.Connection.ExecuteScalarAsync<long?>(
            @"SELECT id FROM schoolevent
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT 1",
            new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's PAID event holding the most charges, for the EVENT FINANCE reads (the charges grid,
    // the participant list and the per-student charges).
    //
    // ⚠️ THEY CANNOT REUSE `SchoolEventId`, AND ON A REAL CAMPUS THEY NEVER WOULD. That one is the
    // event with the most RESPONSES - the event detail page's subject - while these reads need one with
    // CHARGES. Measured on `ayra_perf`: twelve events carry 240 responses and zero charges, and the one
    // paid event carries all four charges. Pointed at the response-busiest event every finance spec
    // would read zero rows from a fully configured campus, and the run would look green with four
    // screens unmeasured - a COUNT of 0 is exactly the outcome a broken read also produces.
    scope.EventFinanceEventId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT ec.eventid FROM eventcharge ec
            JOIN schoolevent e ON e.id = ec.eventid
           WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId
           GROUP BY ec.eventid ORDER BY COUNT(*) DESC, ec.eventid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's busiest payroll period, for the payroll RECORDS grid - that repository takes a
    // `payrollPeriodId` from the page rather than filtering by scope, so the spec has to be handed
    // one. Ordered by record count, not by date: a period the payroll run has not reached yet is a
    // real period with nothing in it, and measuring it would time an empty table.
    scope.PayrollPeriodId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT payrollperiodid FROM employeepayroll
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY payrollperiodid ORDER BY COUNT(*) DESC, payrollperiodid DESC LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's book holding the MOST copies, for the library dashboard's per-book copy counts.
    // The dashboard runs that statement twice for EVERY book it lists, so the worst-case book (the
    // one with the most copies) is the one worth pointing the spec at - it is also the book whose
    // count has the most index entries to walk.
    scope.LibraryBookId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT bookid FROM librarybookcopy
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY bookid ORDER BY COUNT(*) DESC, bookid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's loan with the LONGEST schedule, for the loan detail modal's installment read -
    // that repository takes the loan id the user clicked rather than filtering by scope, so the
    // spec has to be handed one. The longest schedule walks the most index entries, so it is the
    // worst case rather than the first row.
    scope.LoanId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT employeeloanid FROM employeeloaninstallment
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY employeeloanid ORDER BY COUNT(*) DESC, employeeloanid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's performance scale with the MOST levels, for its editor's level read - same
    // reason as the loan above: the repository takes the scale id, not the scope.
    scope.PerformanceScaleId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT performancescaleid FROM performancescalelevel
           WHERE performancescaleid IN (
                     SELECT id FROM performancescale
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
           GROUP BY performancescaleid ORDER BY COUNT(*) DESC, performancescaleid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's asset with the LONGEST maintenance log, for the asset detail modal's per-asset
    // read - that repository takes the asset id rather than filtering by scope, so the spec has to
    // be handed one. The longest log is the worst case rather than the first row.
    scope.InvAssetId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT assetid FROM invmaintenance
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY assetid ORDER BY COUNT(*) DESC, assetid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's employee holding the MOST tax records, for the three PER-EMPLOYEE reads (the
    // salary-structure panel, the leave-balance rings and the payroll tax history) - each takes the
    // employee id the user picked rather than filtering by scope, so the spec has to be handed one,
    // and the busiest person is the worst case rather than the first row.
    scope.EmployeeId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT employeeid FROM employeetaxrecord
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY employeeid ORDER BY COUNT(*) DESC, employeeid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // A separation of this campus that HAS a settlement, for the exit desk's detail read - same
    // reason as above: `GetBySeparation` takes the id, not the scope.
    scope.SeparationId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT s.separationid FROM employeesettlement s
           WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
           ORDER BY s.separationid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's PROGRESSIVE income-tax config, for its slab editor read. Chosen by name of type
    // rather than by id so the spec points at the config that actually carries slabs.
    scope.TaxConfigId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT id FROM taxconfig
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
             AND taxtype = 'IncomeTax' AND isactive = true
           ORDER BY id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's student that ACTUALLY HOLDS a profile, for the four per-student tab reads. Not
    // "the first student": a student with no contact or health row would make those reads return
    // nothing, and an empty result is fast for a reason that has nothing to do with the query.
    scope.StudentId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT s.id FROM student s
           WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
             AND EXISTS (SELECT 1 FROM studentcontact c WHERE c.studentid = s.id)
             AND EXISTS (SELECT 1 FROM studenthealth h WHERE h.studentid = s.id)
           ORDER BY s.id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's assessment tool holding the MOST criteria, for `AssessmentToolItemRepository
    // .GetAll(toolId)` - the tool editor's criterion list, which takes the tool the user opened
    // rather than filtering by scope. The longest list is the worst case rather than the first row.
    // ⚠️ The tool is resolved at the scope the GRID filters on (the tool's own triple), because the
    // items themselves carry no scope columns - an item list is only reachable through a tool the
    // campus's own grid can show.
    scope.AssessmentToolId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT i.assessmenttoolid FROM assessmenttoolitem i
           INNER JOIN assessmenttool at ON at.id = i.assessmenttoolid
           WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId
           GROUP BY i.assessmenttoolid ORDER BY COUNT(*) DESC, i.assessmenttoolid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's rubric criterion holding the MOST levels, for the rubric evidence screen's
    // per-criterion read (`RubricCriterionLevelRepository.GetAllByItemId(itemId)`) - keyed on the
    // criterion rather than on the scope. The fullest level set is the worst case, and it is also
    // the one that proves the read has something to return: an empty result is fast for a reason
    // that has nothing to do with the query.
    scope.AssessmentToolItemId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT l.assessmenttoolitemid FROM rubriccriterionlevel l
           INNER JOIN assessmenttoolitem i ON i.id = l.assessmenttoolitemid
           INNER JOIN assessmenttool at ON at.id = i.assessmenttoolid
           WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId
           GROUP BY l.assessmenttoolitemid ORDER BY COUNT(*) DESC, l.assessmenttoolitemid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's salary structure carrying the MOST component lines, for the structure editor's
    // line read - that repository takes the STRUCTURE id the user opened rather than filtering by
    // scope, so the spec has to be handed one. The fullest structure is both the worst case and the
    // only shape that proves the read has anything to return.
    scope.SalaryStructureId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT salarystructureid FROM employeesalarystructuredetail
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY salarystructureid ORDER BY COUNT(*) DESC, salarystructureid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's performance review holding the MOST KPI score lines, for the review dialog's
    // score table AND its recommendation list - both are read by REVIEW id rather than by scope, so
    // one value serves two specs. Ordered by the SCORE table's line count, which is the read that
    // walks the most rows of the two.
    scope.PerformanceReviewId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT employeeperformancereviewid FROM employeeperformancedetail
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY employeeperformancereviewid
           ORDER BY COUNT(*) DESC, employeeperformancereviewid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The campus's payroll record carrying the MOST payslip lines, for the payslip's detail and
    // adjustment reads - both take the record id, not the scope.
    //
    // ⚠️ RESOLVED FROM `employeepayrolldetail`, THE TABLE THE SPEC MEASURES, not from the payroll
    // grid - the two are different questions and only one of them guarantees the record has lines.
    scope.EmployeePayrollId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT employeepayrollid FROM employeepayrolldetail
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY employeepayrollid ORDER BY COUNT(*) DESC, employeepayrollid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The report definition carrying the MOST run-log rows, for the reporting desk's history + saved
    // views - both are read per definition, so the spec has to be handed one. Ordered by history length
    // rather than by id: a definition with no runs is a definition nobody has run, and measuring it
    // would time an empty page.
    scope.ReportDefinitionId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT reportdefinitionid FROM reportrunlog
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY reportdefinitionid ORDER BY COUNT(*) DESC, reportdefinitionid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // A background job that OWNS an export. `GetByJobId` is the only read of `reportexport` and the
    // download is reached through the job, so the spec must be handed a job id that really owns one.
    scope.ReportJobId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT jobid FROM reportexport ORDER BY id LIMIT 1") ?? 0;

    // ------------------------------------------------------------------
    // THE TRANSPORT ROUTE-STOP GRID. `transport/route/{routeId}/stop` is the only grid in the
    // transport module that is keyed on a PARENT rather than on the scope alone, so the spec needs a
    // real route id. The route carrying the MOST stops is the worst case - a campus owns a dozen
    // routes and the stop list is what the screen opens.
    // ------------------------------------------------------------------
    scope.TransportRouteId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT routeid FROM transportroutestop
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY routeid ORDER BY COUNT(*) DESC, routeid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The measured campus's NEWEST background job, for `JobsController.GetJob` - the UI's status poll
    // is reached by the id it is polling, not by a scope filter, so the spec has to be handed a row.
    // ⚠️ `backgroundjob` is the ONE table here whose route (`api/jobs`) carries NO scope segments and
    // whose hot read (the claim) is global; this is only a row that exists. A campus with none
    // resolves to 0 and the status spec reports SKIP.
    scope.JobId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT id FROM backgroundjob
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           ORDER BY id DESC LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The meeting with the MOST audience rows, for `GetAudience(meetingId)` - keyed on the meeting the
    // user opened rather than on the scope. The fullest invite list is the worst case.
    scope.HrMeetingId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT meetingid FROM hrmeetingaudience
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           GROUP BY meetingid ORDER BY COUNT(*) DESC, meetingid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // ------------------------------------------------------------------
    // THE CURRICULUM TREE. Three grids are keyed on a PARENT the user picked rather than on the
    // scope, and two of the three tables (`curriculumgrade`, `curriculumgradesubject`) carry NO scope
    // columns at all - they hang off `curriculumversion`, which is the only one holding tenant/school.
    // So the chain is resolved at the SCHOOL level (the curriculum tables are school-scoped), from
    // the parent down, and each level picks the child with the MOST rows because that is the worst
    // case for the read that pages it.
    // ------------------------------------------------------------------
    scope.CurriculumId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT curriculumid FROM curriculumversion
           WHERE tenantid = @tenantId AND schoolid = @schoolId
           GROUP BY curriculumid ORDER BY COUNT(*) DESC, curriculumid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId }) ?? 0;

    scope.CurriculumVersionId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT cv.id FROM curriculumversion cv
           LEFT JOIN curriculumgrade cg ON cg.curriculumversionid = cv.id
           WHERE cv.tenantid = @tenantId AND cv.schoolid = @schoolId
           GROUP BY cv.id ORDER BY COUNT(cg.id) DESC, cv.id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId }) ?? 0;

    scope.CurriculumGradeId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT cg.id FROM curriculumgrade cg
           JOIN curriculumgradesubject cgs ON cgs.curriculumgradeid = cg.id
           JOIN curriculumversion cv ON cv.id = cg.curriculumversionid
           WHERE cv.tenantid = @tenantId AND cv.schoolid = @schoolId
           GROUP BY cg.id ORDER BY COUNT(cgs.id) DESC, cg.id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId }) ?? 0;

    // ⚠️ `LoanId` IS DELIBERATELY REUSED and NOT re-derived here. The HR-loan payment read keys on
    // the LOAN, and the value above resolved from `employeeloaninstallment` is already "the loan with
    // the longest schedule" - the same worst case. A second resolver over the same table would be a
    // second definition of one value, and the day the two drifted the two specs would disagree about
    // which loan they measured.

    // ------------------------------------------------------------------
    // THE APPROVAL ENGINE. One identity, its role ids, and the two parent ids its child reads
    // need.
    //
    // ⚠️ THE USER RULE IS THE DATASET SEEDER'S OWN RULE - "the campus's lowest-numbered user" -
    // COPIED RATHER THAN RE-DERIVED. The inbox matches `s.ApproverUserId = @userId` (or a role the
    // user holds) while "my requests" matches `w.RequestedBy = @userId`, so the seeder writes its
    // seed around one identity and this must resolve THAT identity; two different rules would make
    // the inbox spec report SKIP for a queue a real approver sees populated. The perf campus's
    // users hold the Teacher role and the templates name HR Manager / HOD, so the role branch of
    // the inbox matches nothing and the first branch is the one that has to be right.
    // ------------------------------------------------------------------
    scope.UserId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT id FROM users
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
           ORDER BY id LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The role ids that identity holds, in `userrole`'s own order. `HasPermissionAttribute` resolves
    // exactly this array and then calls `roleIds.Contains(x.RoleId)`, which `SqlBuilder` renders as
    // `RoleId = ANY(@p0)` - so the spec is handed the array the filter would build.
    scope.RoleIds = (await session.Connection.QueryAsync<long>(
        @"SELECT roleid FROM userrole WHERE userid = @userId AND isactive = TRUE ORDER BY roleid",
        new { userId = scope.UserId })).ToArray();

    // The campus's approval cycle with the LONGEST step chain, plus the (module, entity) pair that
    // identifies it. `GetWorkflowSteps` is keyed on the cycle id and `GetWorkflowsByEntity` on the
    // pair - neither filters by scope - so both specs have to be handed one, and the longest chain
    // is the worst case rather than the first row. An OPEN cycle is preferred: a finished one is a
    // real read too, but the pending queue is what a campus actually looks at.
    var approval = await session.Connection.QueryFirstOrDefaultAsync<ApprovalScopeRow>(
        @"SELECT w.id AS WorkflowId, w.modulename AS ModuleName, w.entityid AS EntityId
            FROM approvalworkflow w
           WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
        ORDER BY (SELECT COUNT(*) FROM approvalworkflowstep s
                   WHERE s.approvalworkflowid = w.id) DESC,
                 CASE WHEN w.workflowstatus IN ('Pending', 'InProgress') THEN 0 ELSE 1 END,
                 w.id
           LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId });

    scope.ApprovalWorkflowId = approval?.WorkflowId ?? 0;
    scope.ApprovalModuleName = approval?.ModuleName ?? string.Empty;
    scope.ApprovalEntityId = approval?.EntityId ?? 0;

    // The campus's approval TEMPLATE with the most steps, for the template editor's chain read.
    scope.ApprovalTemplateId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT ts.approvaltemplateid
            FROM approvaltemplatestep ts
            JOIN approvaltemplate t ON t.id = ts.approvaltemplateid
           WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
        GROUP BY ts.approvaltemplateid
        ORDER BY COUNT(*) DESC, ts.approvaltemplateid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // ------------------------------------------------------------------
    // The TIMETABLE / TEACHER-OPS module.
    //
    // ⚠️ NONE OF THESE CAN COME FROM `ScopeVars.ClassroomId` OR `ScopeVars.AcademicGradeId`, AND THAT
    // IS THE WHOLE POINT OF RESOLVING THEM SEPARATELY. `ClassroomId` is resolved from `attendance`
    // and `AcademicGradeId` from the campus's first `classroom` row - and on `ayra_perf` that first
    // classroom points at `academicgrade` **1**, which belongs to CAMPUS 1 (campus 15 has no
    // `academicgrade` of its own). A timetable spec placed through that grade would filter on a grade
    // none of its entries names and report an empty first page while `timetableentry` held 210 rows.
    // `TimetableModuleSeeder` therefore writes the campus's OWN grade, classrooms and period
    // template, and each variable below is resolved from those rows: the PUBLISHED timetable with the
    // most entries, the classroom it belongs to, and that classroom's grade and section.
    //
    // ⚠️ A PUBLISHED ROW IS REQUIRED, NOT PREFERRED. Every `TimetableEntryRepository` read filters
    // `Timetable.Status = 'Published'`, so resolving a Draft would make the specs measure the empty
    // side of a join. A campus with no published timetable resolves these to 0 and those specs report
    // SKIP, which is honest.
    // ------------------------------------------------------------------
    var timetableScope = await session.Connection.QueryFirstOrDefaultAsync<TimetableScopeRow>(
        @"SELECT t.classroomid AS ClassroomId, t.id AS TimetableId,
                 c.academicgradeid AS AcademicGradeId, c.sectionid AS SectionId
            FROM timetable t
            INNER JOIN classroom c ON c.id = t.classroomid
           WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
             AND t.status = 'Published'
        ORDER BY (SELECT COUNT(*) FROM timetableentry te WHERE te.timetableid = t.id) DESC, t.id
           LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId });

    scope.TimetableClassroomId = timetableScope?.ClassroomId ?? 0;
    scope.TimetableId = timetableScope?.TimetableId ?? 0;
    scope.TimetableGradeId = timetableScope?.AcademicGradeId ?? 0;
    scope.TimetableSectionId = timetableScope?.SectionId ?? 0;

    // The campus's period template, with the most slots - a template with one period is not the grid
    // a school draws, and the PeriodIndex subquery every entry spec runs walks it once per row.
    scope.TimetableSetupId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT s.id FROM timetablesetup s
           WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId
        ORDER BY (SELECT COUNT(*) FROM timetablesetupdetail d WHERE d.timetablesetupid = s.id) DESC, s.id
           LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The teacher carrying the MOST published periods in the campus's own academic year. The four
    // workload reads take a teacher id rather than filtering by scope, so the spec has to be handed
    // one - and the busiest teacher is the worst case rather than the first row.
    scope.TimetableTeacherId = await session.Connection.ExecuteScalarAsync<long?>(
        @"SELECT te.teacherid FROM timetableentry te
            INNER JOIN timetable t ON t.id = te.timetableid
           WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
             AND t.status = 'Published'
        GROUP BY te.teacherid ORDER BY COUNT(*) DESC, te.teacherid LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId }) ?? 0;

    // The weekday the campus actually teaches on, for the substitute desk's coverage read - it takes
    // a weekday NAME, and the grid's cells are keyed on it.
    scope.TimetableWeekDay = await session.Connection.ExecuteScalarAsync<string?>(
        @"SELECT te.weekday FROM timetableentry te
            INNER JOIN timetable t ON t.id = te.timetableid
           WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
             AND t.status = 'Published'
        GROUP BY te.weekday ORDER BY COUNT(*) DESC, te.weekday LIMIT 1",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId })
        ?? string.Empty;

    // A date the campus has substitute coverage on. The coverage read joins relief on the DATE and the
    // scheduled cell on the WEEKDAY, so a date with no relief row measures the LEFT JOIN's empty half.
    scope.TimetableDate = await session.Connection.ExecuteScalarAsync<DateTime?>(
        @"SELECT MAX(reliefdate) FROM timetablerelief
           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
        new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId })
        ?? DateTime.Today;

    return scope;
}

/// <summary>
/// How much of the table the measured campus actually holds. This is the number that
/// decides whether a scope-column index can be selective: 100% means it cannot, and
/// 1% means it is exactly what the index is for.
/// </summary>
static async Task<string> DescribeScopeSizeAsync(BenchmarkRunner runner, ScopeVars scope)
{
    await using var session = await runner.OpenAsync();

    async Task<(long Scoped, long Total)> Pair(string table)
    {
        try
        {
            // The full triple, like every other statement about this scope. A percentage
            // computed over a DIFFERENT set than the specs filter on is the number the
            // index advice rests on being wrong.
            var scoped = await runner.ScalarAsync(session,
                $"SELECT COUNT(*) FROM {table} WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
                new { tenantId = scope.TenantId, schoolId = scope.SchoolId, campusId = scope.CampusId });
            var total = await runner.ScalarAsync(session, $"SELECT COUNT(*) FROM {table}");
            return (scoped, total);
        }
        catch { return (-1, -1); }
    }

    var students = await Pair("student");
    var attendance = await Pair("attendance");
    return $"this campus holds {Percent(students.Scoped, students.Total)} of students " +
           $"({students.Scoped:N0}/{students.Total:N0}), {Percent(attendance.Scoped, attendance.Total)} of attendance " +
           $"({attendance.Scoped:N0}/{attendance.Total:N0})";
}

static string Percent(long part, long whole) =>
    whole > 0 && part >= 0 ? (part / (double)whole).ToString("P1") : "?";

static async Task<string> DescribeVolumeAsync(BenchmarkRunner runner, ScopeVars scope)
{
    await using var session = await runner.OpenAsync();

    async Task<long> Count(string table)
    {
        try { return await runner.ScalarAsync(session, $"SELECT COUNT(*) FROM {table}"); }
        catch { return -1; }
    }

    var students = await Count("student");
    var attendance = await Count("attendance");
    var invoices = await Count("invoices");

    return $"student {students:N0} | attendance {attendance:N0} | invoices {invoices:N0}";
}

// ---------------------------------------------------------------------------
// Output
// ---------------------------------------------------------------------------

static void PrintTable(List<QuerySpec> specs, List<Measurement> results)
{
    Console.WriteLine();
    Console.WriteLine($"{"query",-32}{"min",9}{"p50",9}{"p95",9}{"p95 (s)",10}{"max",9}{"plan exec",11}{"rows",9}   verdict");
    Console.WriteLine(new string('-', 32 + 9 + 9 + 9 + 10 + 9 + 11 + 9 + 3 + 30));

    for (var i = 0; i < specs.Count; i++)
    {
        var spec = specs[i];
        var m = results[i];

        if (m.Error != null)
        {
            Console.WriteLine($"{spec.Key,-32}{"-",9}{"-",9}{"-",9}{"-",10}{"-",9}{"-",11}{"-",9}   ERROR");
            continue;
        }

        var exec = m.Plan?.ExecutionMs ?? 0;
        var rows = m.ScalarValue?.ToString("N0") ?? m.RowsReturned.ToString("N0");
        var verdict = m.Verdict switch
        {
            "FAIL" => $"FAIL (budget {spec.P95BudgetMs:F0}ms)",
            "SKIP" => $"SKIP (scope holds {m.Volume:N0} rows < {spec.MinVolume:N0})",
            _ => "OK",
        };

        if (m.IsUnstable) verdict += $"  ~ UNSTABLE {m.Max / Math.Max(m.Min, 0.01):F0}x";

        Console.WriteLine($"{spec.Key,-32}{Ms(m.Min),9}{Ms(m.P50),9}{Ms(m.P95),9}{Secs(m.P95),10}{Ms(m.Max),9}{Ms(exec),11}{rows,9}   {verdict}");
    }
}

static void PrintDetails(List<QuerySpec> specs, List<Measurement> results)
{
    for (var i = 0; i < specs.Count; i++)
    {
        var spec = specs[i];
        var m = results[i];
        if (m.Verdict == "OK" || m.Verdict == "SKIP") continue;

        Console.WriteLine();
        Console.WriteLine($"--- {spec.Key} : {spec.Title}");
        Console.WriteLine($"    source         : {spec.Source}");

        if (m.Error != null)
        {
            Console.WriteLine($"    error          : {m.Error}");
            continue;
        }

        Console.WriteLine($"    p95            : {Secs(m.P95)} s  ({Ms(m.P95)} ms, budget {spec.P95BudgetMs:F0} ms)");
        Console.WriteLine($"    samples        : {string.Join(", ", m.SamplesMs.Select(s => Ms(s) + "ms"))}");
        if (m.IsUnstable)
        {
            Console.WriteLine($"    ⚠ unstable     : {m.Max / Math.Max(m.Min, 0.01):F0}x spread across identical runs (min {Ms(m.Min)} ms, max {Ms(m.Max)} ms).");
            // ⚠️ THIS USED TO ASSERT A CAUSE - "the planner is choosing different plans" - AND THAT
            // CLAIM IS NOT ALWAYS TRUE. Measured on `rpt-student-attendance`: `auto_explain` at
            // `log_min_duration = 0` captured a 85 ms and a 187 ms execution of the same statement
            // with BYTE-IDENTICAL plans (same costs, same rows, same join order) and every node
            // proportionally slower. A tool must not print a diagnosis it has not verified, so this
            // states the observation and names the two things that actually distinguish them.
            Console.WriteLine("                     Either the plan changed between samples, or the machine did - the two are");
            Console.WriteLine("                     indistinguishable from the timings alone. Diff the EXPLAIN of a fast and a");
            Console.WriteLine("                     slow sample (auto_explain.log_min_duration = 0) before changing anything.");
        }
        if (m.Plan != null)
        {
            Console.WriteLine($"    planner exec   : {Secs(m.Plan.ExecutionMs)} s  ({Ms(m.Plan.ExecutionMs)} ms, planning {Ms(m.Plan.PlanningMs)} ms)");
            Console.WriteLine($"    heaviest node  : {m.Plan.HeaviestNodeDescription}");
            Console.WriteLine($"    seq scan       : {(m.Plan.HasSeqScan ? $"YES on {m.Plan.WorstSeqScanRelation} (~{m.Plan.WorstSeqScanRows:N0} rows)" : $"no scan over {PlanInfo.SeqScanNoiseRows:N0} rows")}");
            Console.WriteLine($"    indexes used   : {(m.Plan.Indexes.Count > 0 ? string.Join(", ", m.Plan.Indexes.Distinct()) : "none")}");
            Console.WriteLine($"    buffers        : {m.Plan.SharedHitBlocks:N0} hit / {m.Plan.SharedReadBlocks:N0} read");
        }

        var candidate = QueryCatalog.IndexFor(spec);
        if (candidate != null)
        {
            Console.WriteLine($"    suggested index: {IndexAdvice.CreateDdl(candidate)}");
            Console.WriteLine($"    why            : {candidate.Rationale}");
        }
        else
        {
            Console.WriteLine("    suggested index: (none - this query is slow for a reason an index will not fix)");
        }
    }
}

/// <summary>
/// Probes each FAILED query's candidate index and returns the ones that were PROVEN -
/// meaning the index was created inside a rolled-back transaction and the query then came
/// back inside its budget. Everything else stays a suggestion, and a suggestion is never
/// written out as a migration.
/// </summary>
static async Task<List<IndexEvidence>> PrintIndexImpactAsync(
    BenchmarkRunner runner, List<QuerySpec> specs, List<Measurement> results, Options opts)
{
    Console.WriteLine();
    Console.WriteLine("=== index impact (each probe runs in a ROLLED-BACK transaction - nothing is changed) ===");

    var proven = new List<IndexEvidence>();
    var any = false;

    for (var i = 0; i < specs.Count; i++)
    {
        var spec = specs[i];
        var before = results[i];

        var candidate = QueryCatalog.IndexFor(spec);
        if (candidate == null) continue;
        if (before.Verdict is "OK" or "SKIP" or "ERROR") continue;

        any = true;
        Console.WriteLine();
        Console.WriteLine($"{spec.Key}");

        var after = await runner.ProbeIndexAsync(spec, opts.Runs);
        if (after == null || after.Error != null)
        {
            Console.WriteLine($"    probe failed   : {after?.Error ?? "no result"}");
            continue;
        }

        var meetsBudget = after.P95 <= spec.P95BudgetMs;
        var speedup = after.P95 > 0 ? before.P95 / after.P95 : 0;
        Console.WriteLine($"    p95            : {Secs(before.P95)} s -> {Secs(after.P95)} s   ({Ms(before.P95)} ms -> {Ms(after.P95)} ms, {speedup:F1}x faster)");
        Console.WriteLine($"    planner exec   : {Secs(before.Plan?.ExecutionMs ?? 0)} s -> {Secs(after.Plan?.ExecutionMs ?? 0)} s   ({Ms(before.Plan?.ExecutionMs ?? 0)} ms -> {Ms(after.Plan?.ExecutionMs ?? 0)} ms)");
        Console.WriteLine($"    heaviest node  : {before.Plan?.HeaviestNodeDescription} -> {after.Plan?.HeaviestNodeDescription}");
        Console.WriteLine($"    seq scan       : {(after.Plan?.HasSeqScan == true ? "still YES" : "gone")}");
        Console.WriteLine($"    indexes used   : {(after.Plan?.Indexes.Count > 0 ? string.Join(", ", after.Plan.Indexes.Distinct()) : "none")}");
        Console.WriteLine($"    verdict        : {(meetsBudget ? "meets the budget - PROVEN" : $"still over the {spec.P95BudgetMs:F0}ms budget - NOT proven, not written")}");
        Console.WriteLine($"    DDL            : {IndexAdvice.CreateDdl(candidate)}");

        if (meetsBudget)
            proven.Add(new IndexEvidence(
                candidate, spec.Key, spec.Title, spec.Source,
                before.P95, after.P95, spec.P95BudgetMs,
                before.Plan?.HeaviestNodeDescription, after.Plan?.HeaviestNodeDescription));
    }

    if (!any)
        Console.WriteLine("   Nothing failed, so there was nothing to probe.");

    return proven;
}

/// <summary>
/// Record the proven indexes as a migration.
///
/// ⚠️ An index that a migration ALREADY declares is skipped, so running this repeatedly cannot
/// pile up duplicate migrations. Index names are unique per schema, so a name match is exact
/// evidence that the index is already shipped.
/// </summary>
static void WriteMigrations(List<IndexEvidence> proven, MigrationContext context, Options opts)
{
    Console.WriteLine();
    Console.WriteLine("=== migration ===");

    var candidates = IndexAdvice.Reduce(proven.Select(item => item.Candidate)).ToList();
    if (candidates.Count == 0)
    {
        Console.WriteLine("   No index was PROVEN, so there is nothing to write.");
        Console.WriteLine("   (Only a probe that comes back inside its budget is evidence for a migration.)");
        return;
    }

    var directory = MigrationWriter.Locate(opts.MigrationsDirectory);
    if (directory == null)
    {
        Console.Error.WriteLine(opts.MigrationsDirectory == null
            ? "   error: no school-db/migrations above this binary - pass --migrations-dir <path>."
            : $"   error: --migrations-dir '{opts.MigrationsDirectory}' does not exist.");
        return;
    }

    var declared = MigrationWriter.DeclaredIndexNames(directory);
    var missing = candidates.Where(candidate => !declared.Contains(candidate.Name)).ToList();

    Console.WriteLine($"   dir            : {directory}");

    if (missing.Count == 0)
    {
        Console.WriteLine("   already declared by an existing migration (nothing written):");
        foreach (var candidate in candidates) Console.WriteLine($"     {candidate.Name}");
        return;
    }

    var evidence = proven
        .Where(item => missing.Any(candidate =>
            candidate.Name.Equals(item.Candidate.Name, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    var path = MigrationWriter.Write(directory, evidence, context);
    Console.WriteLine($"   wrote          : {Path.GetFileName(path)}");
    foreach (var candidate in missing)
        Console.WriteLine($"     {candidate.Name} on {candidate.Table} ({candidate.Columns})");
    Console.WriteLine("   Nothing was applied to any database - this only writes the file.");
}

/// <summary>
/// Say which proven indexes are NOT yet recorded, so the machinery is discoverable without
/// reading the source. Printing a DDL string and leaving the reader to hand-copy it is how the
/// first index migration was written - with the probe's throwaway name on it.
/// </summary>
static void PrintMigrationHint(List<IndexEvidence> proven, Options opts)
{
    var candidates = IndexAdvice.Reduce(proven.Select(item => item.Candidate)).ToList();
    if (candidates.Count == 0) return;

    var directory = MigrationWriter.Locate(opts.MigrationsDirectory);
    var declared = directory == null
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : MigrationWriter.DeclaredIndexNames(directory);

    var missing = candidates.Where(candidate => !declared.Contains(candidate.Name)).ToList();

    Console.WriteLine();
    if (missing.Count == 0)
    {
        Console.WriteLine($"   {candidates.Count} index(es) PROVEN, and every one is already declared by a migration.");
        return;
    }

    Console.WriteLine($"   {missing.Count} PROVEN index(es) are not yet recorded as a migration:");
    foreach (var candidate in missing)
        Console.WriteLine($"     {candidate.Name} on {candidate.Table} ({candidate.Columns})");
    Console.WriteLine("   Re-run with --write-migrations to write them (nothing is applied to any database).");
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static string Ms(double value) => value >= 100 ? value.ToString("F0") : value.ToString("F1");

/// <summary>
/// The same duration in SECONDS.
///
/// Milliseconds are the right unit for a BUDGET - `P95BudgetMs` is declared in them, and a plan
/// is discussed in them - but they are the wrong unit for reporting how long something took: a
/// 40-second run reads as "40000". So the durations are printed in seconds, with the millisecond
/// value kept beside them wherever the number is actually being reasoned about.
/// </summary>
static string Secs(double milliseconds) => (milliseconds / 1000.0).ToString("F3");

static string ResolveConnectionString()
{
    var fromEnvironment = Environment.GetEnvironmentVariable("SCUBE_PERF_DB");
    if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    return configuration.GetConnectionString("PerfTestDatabase") ?? string.Empty;
}

/// <summary>Never print a password, even in a log that gets pasted into a chat.</summary>
static string Redact(string connectionString)
{
    var parts = connectionString
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(part =>
        {
            var separator = part.IndexOf('=');
            if (separator < 0) return part.Trim();
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            return key.Equals("Password", StringComparison.OrdinalIgnoreCase) ? $"{key}=***" : $"{key}={value}";
        });
    return string.Join("; ", parts);
}

/// <summary>The busiest (tenant, school, campus) triple.</summary>
public sealed class ScopeRow
{
    public long TenantId { get; set; }
    public long SchoolId { get; set; }
    public long CampusId { get; set; }
}

public sealed class Options
{
    public List<string> Errors { get; } = new();
    public bool Help { get; private set; }
    public bool Advise { get; private set; }
    public bool Busiest { get; private set; }
    public bool WriteMigrations { get; private set; }
    public bool ExitZero { get; private set; }
    public int Runs { get; private set; } = 5;
    public int StatementTimeoutMs { get; private set; } = 120_000;
    public string? Only { get; private set; }
    public string? MigrationsDirectory { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    options.Help = true;
                    break;
                case "--advise":
                    options.Advise = true;
                    break;
                case "--busiest":
                    options.Busiest = true;
                    break;
                case "--write-migrations":
                    options.WriteMigrations = true;
                    // An index can only be PROVEN by a probe, so writing implies advising.
                    options.Advise = true;
                    break;
                case "--migrations-dir":
                    if (i + 1 < args.Length) options.MigrationsDirectory = args[++i];
                    else options.Errors.Add("--migrations-dir needs a path");
                    break;
                case "--exit-zero":
                    options.ExitZero = true;
                    break;
                case "--runs":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var runs) && runs > 0)
                        options.Runs = runs;
                    else
                        options.Errors.Add("--runs needs a positive integer");
                    break;
                case "--timeout":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var timeout) && timeout > 0)
                        options.StatementTimeoutMs = timeout;
                    else
                        options.Errors.Add("--timeout needs a positive integer (milliseconds)");
                    break;
                case "--only":
                    if (i + 1 < args.Length) options.Only = args[++i];
                    else options.Errors.Add("--only needs a search term");
                    break;
                default:
                    options.Errors.Add($"unknown argument '{arg}'");
                    break;
            }
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            SCube database performance report.

              dotnet run --project school-performance/db-report
              dotnet run --project school-performance/db-report -- --advise
              dotnet run --project school-performance/db-report -- --advise --write-migrations
              dotnet run --project school-performance/db-report -- --only attendance --runs 10

            Options
              --advise          for each FAILED query, create its suggested index inside a
                                ROLLED-BACK transaction, re-measure, then undo. The database
                                is never changed - not even if this process is killed.
              --busiest         measure the BUSIEST campus (worst case) instead of a typical
                                one. The default is the median campus by student count,
                                because on the busiest campus a scope-column index matches
                                every row and so measures ~1.0x however good it is.
              --write-migrations
                                write each PROVEN index out as a migration under
                                school-db/migrations, with the measurement that justified it
                                in the file header (implies --advise). An index that an
                                existing migration already declares is skipped, so repeated
                                runs cannot pile up duplicates. NOTHING is applied to any
                                database - it only writes the file.
              --migrations-dir <path>
                                write the migration here instead of the default
                                school-db/migrations (the one above this binary).
              --runs <n>        timed iterations per query (default 5; a warm-up run is
                                always discarded first).
              --only <term>     measure only queries whose key or title contains <term>.
              --timeout <ms>    statement timeout (default 120000).
              --exit-zero       always exit 0 (report mode; for CI use the exit code).
              -h, --help        this text.

            Connection string
              SCUBE_PERF_DB, else ConnectionStrings:PerfTestDatabase from appsettings.json.
              The default targets the ayra_perf database.

            Exit code
              The number of FAILED queries (0 = every query met its budget), so this can be
              a CI gate. --exit-zero overrides it.
            """);
    }
}

/// <summary>The (module, entity) pair that identifies one approval cycle - see `ResolveScopeAsync`.</summary>
internal sealed class ApprovalScopeRow
{
    public long WorkflowId { get; set; }
    public string ModuleName { get; set; } = string.Empty;
    public long EntityId { get; set; }
}

/// <summary>
/// One campus's PUBLISHED timetable and the classroom it hangs off - see `ResolveScopeAsync`.
/// The timetable module's specs are keyed on a classroom, a grade and a section rather than on the
/// generic `ScopeVars.ClassroomId`/`AcademicGradeId`, because those two resolve through a chain that
/// points at another campus's academic year on this database.
/// </summary>
internal sealed class TimetableScopeRow
{
    public long ClassroomId { get; set; }
    public long TimetableId { get; set; }
    public long AcademicGradeId { get; set; }
    public long SectionId { get; set; }
}
