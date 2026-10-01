namespace SchoolPerformance.QueryShapes;

/// <summary>One query to measure, together with the budget it must meet.</summary>
public sealed class QuerySpec
{
    public required string Key { get; init; }

    /// <summary>What the user is doing, in their words.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Where this SQL came from — the repository method AND where it is generated, so the
    /// shape can be re-verified after a refactor. Types it rather than \"the repo\" when it
    /// comes from shared plumbing (e.g. GenericRepository.FindAsync), because that is where
    /// the shape actually comes from.
    /// </summary>
    public required string Source { get; init; }

    public required string Sql { get; init; }

    /// <summary>
    /// A mutable parameter bag rather than an anonymous object, so a consumer can override
    /// a value (a test needs a different page size, or a different search term) without
    /// re-typing the SQL. Copy it before mutating: <c>new Dictionary&lt;string, object&gt;(spec.Params!)</c>.
    /// </summary>
    public Dictionary<string, object>? Params { get; init; }

    /// <summary>True for a COUNT-style query: the "result" is one number, not rows.</summary>
    public bool IsScalar { get; init; }

    /// <summary>p95 wall-clock budget in ms.</summary>
    public required double P95BudgetMs { get; init; }

    /// <summary>
    /// Rows this query would have to look at. Below <see cref="MinVolume"/> the timing means nothing.
    ///
    /// ⚠️ IT MUST FILTER ON EXACTLY WHAT <see cref="Sql"/> FILTERS ON. This is the gate that decides
    /// whether a measurement is worth judging, so a gate that counts a DIFFERENT set than the query
    /// is measuring is worse than no gate: too loose and a scope passes the check while the query
    /// returns nothing, too tight and a measurable scope is skipped as empty.
    ///
    /// That is why the attendance specs below filter tenant + campus while every other scope here
    /// filters tenant + school + campus. It is not an oversight: AttendanceRepository.GetAll(
    /// page, tenantId, campusId) passes the predicate `c.TenantId == tenantId && c.CampusId ==
    /// campusId`, so the application genuinely omits SchoolId on that grid, and a volume probe that
    /// added it would be describing a different set from the query it gates.
    /// </summary>
    public string? VolumeSql { get; init; }
    /// <summary>
    /// Rows this query's SCOPE must hold before its timing is worth judging.
    ///
    /// ⚠️ Keep this low on a distributed dataset. It used to be 5,000, which was fine
    /// when one campus held 842,614 students - but a REAL campus holds a couple of
    /// thousand, inside a table of hundreds of thousands. The cost of these queries is
    /// driven by the TOTAL table size (the planner walks an index looking for the
    /// scope), not by how many rows the scope matches, so skipping a 2,000-student
    /// campus inside an 878k-row table would skip exactly the case worth measuring.
    /// The output prints what share of the table the scope holds, which is the context
    /// the old threshold was standing in for.
    /// </summary>
    public long MinVolume { get; init; } = 1000;

    /// <summary>Table to ANALYZE before an index probe.</summary>
    public string? IndexTable { get; init; }

    /// <summary>
    /// The name the candidate index would be DEPLOYED under, following this repo's naming for
    /// the table. It is what a migration must create, and what the writer matches against the
    /// migrations already on disk so a re-run cannot duplicate an index that is already shipped.
    ///
    /// ⚠️ It is NOT the name the probe uses - see <see cref="IndexAdvice.ProbeName"/>. The probe
    /// builds its own disposable name precisely so a leaked probe cannot masquerade as this one.
    /// </summary>
    public string? IndexName { get; init; }

    /// <summary>The column list, exactly as measured - a `DESC` here changes the plan, so keep it.</summary>
    public string? IndexColumns { get; init; }

    /// <summary>Why that index should help - so the suggestion can be argued with.</summary>
    public string? IndexRationale { get; init; }

    /// <summary>
    /// Set when <see cref="Sql"/> reads a REPORTING VIEW (a `vw_*` object).
    ///
    /// ⚠️ Those views do not take their scope from bind parameters - they read it from the
    /// session settings `app.tenant_id` / `app.school_id` / `app.campus_id`, which the
    /// reporting engine sets with `set_config(..., is_local => true)` in
    /// `ReportQueryExecutor.SetScopeAsync`. A spec therefore has to carry the scope values
    /// for the RUNNER to set them, exactly as the engine does.
    ///
    /// Why this matters: the settings are read with `current_setting(name, missing_ok => true)`,
    /// so outside the engine a view FAILS CLOSED and answers zero rows. Measuring one without
    /// setting the scope would report a 200 ms query as a 0.4 ms one - "the report is fine" for
    /// a report that returns nothing.
    /// </summary>
    public ReportScope? ReportScope { get; init; }
}

/// <summary>
/// The scope a REPORTING VIEW reads from the session, rather than from bind parameters.
/// Mirrors `ReportQueryExecutor.SetScopeAsync` - the setting NAMES are a contract with that
/// method, so a rename there must be a rename here.
/// </summary>
public sealed record ReportScope(long TenantId, long SchoolId, long CampusId);

/// <summary>The scope every query is filtered by.</summary>
public sealed class ScopeVars
{
    public long TenantId { get; set; } = 1;
    public long SchoolId { get; set; } = 1;
    public long CampusId { get; set; } = 1;
    public long ClassroomId { get; set; } = 1;

    /// <summary>The campus's academic year - the enrolment, invoice and payment grids all filter on it.</summary>
    public long AcademicYearId { get; set; } = 1;

    /// <summary>The grade of the campus's classroom - the enrolment grids reach it through Classroom.</summary>
    public long AcademicGradeId { get; set; } = 1;

    /// <summary>
    /// One enrolment of the measured campus. The grids page over a whole campus, but the
    /// outstanding-balance guard is a PER-ENROLMENT query, so it needs one to point at.
    /// </summary>
    public long EnrollmentId { get; set; } = 1;

    /// <summary>
    /// The campus's CURRENT ACTIVE TERM, resolved the way the application resolves it.
    ///
    /// ⚠️ THIS IS NOT DECORATION, AND IT IS NOT THE YEAR. The homework and moment grids do not filter
    /// on `AcademicYearId` alone - `GetAllHomeworkByFilters` / `GetAllMomentsByFilters` take
    /// `(classroomId, academicYearId, termId)` and the controller fills the last two from
    /// `TermRepository.GetCurrentActiveTerm(tenantId, schoolId, campusId)`, i.e. the term whose
    /// `startdate..enddate` contains TODAY under the ACTIVE + PUBLISHED year. A spec that filtered on
    /// the year without the term would describe a different set from the one the screen asks for, and
    /// a campus whose current term is not the one the fixture seeded would report SKIP for a screen
    /// that is full of rows. A campus with no current term resolves this to 0 and those specs report
    /// SKIP, which is honest - the controller itself throws `SetupAcademicYear` in that state.
    /// </summary>
    public long TermId { get; set; } = 1;

    /// <summary>
    /// The measured campus's school event that holds the MOST responses. The event detail screen pages
    /// its responses by the event the user opened (`SchoolEventResponseRepository.GetByEventIdPaged`),
    /// which takes the id rather than filtering by scope - so the spec has to be handed one, and the
    /// busiest event is the worst case rather than the first row. A campus with no event resolves this
    /// to 0 and those specs report SKIP, which is honest.
    /// </summary>
    public long SchoolEventId { get; set; } = 1;

    /// <summary>
    /// The measured campus's event carrying the MOST charges, for the EVENT FINANCE reads.
    ///
    /// ⚠️ THIS IS A DIFFERENT EVENT FROM <see cref="SchoolEventId"/>, AND THAT IS THE POINT. The
    /// response/read screens are pointed at the campus's busiest event BY RESPONSES, while the
    /// finance screens page an event's charges - and on a real campus those are not the same event
    /// (the perf dataset leaves twelve events with responses and no charges, plus one paid event
    /// that holds every charge). Reusing one variable would have pointed the finance specs at an
    /// event with nothing to page while both looked configured. Resolves to 0 on a campus with no
    /// charges, and those specs then report SKIP, which is honest.
    /// </summary>
    public long EventFinanceEventId { get; set; }

    /// <summary>
    /// The measured campus's payroll period that HOLDS the most payroll records. The period
    /// grid itself filters by scope, but the RECORDS grid
    /// (`PayrollRepository.GetPayrollByPeriodPage`) filters `PayrollPeriodId = @PayrollPeriodId`
    /// - a literal the page supplies - so it needs one to point at. A campus with no payroll
    /// seeded resolves this to 0 and its spec then reports SKIP, which is honest: a scope with
    /// no records genuinely has nothing to time.
    /// </summary>
    public long PayrollPeriodId { get; set; } = 1;

    /// <summary>
    /// The measured campus's book that holds the MOST copies. The library dashboard's copy
    /// counts are a PER-BOOK query (`LibraryBookCopyRepository.TotalCountByBook` /
    /// `CountAvailableByBook`), run twice for every book the campus owns - so the spec needs one
    /// book to point at, and the one holding the most copies is the worst case for it.
    /// A campus with no copies resolves this to 0 and the spec reports SKIP, which is honest.
    /// </summary>
    public long LibraryBookId { get; set; } = 1;

    /// <summary>
    /// The measured campus's loan holding the MOST installments. The loan grid pages by scope, but
    /// the loan DETAIL modal reads its schedule by the loan id the user clicked
    /// (`LoanRepository.GetInstallments(loanId)`), so the spec needs one to point at - and the loan
    /// with the longest schedule is the one whose read walks the most index entries. A campus with
    /// no loans resolves this to 0 and the spec reports SKIP, which is honest.
    /// </summary>
    public long LoanId { get; set; } = 1;

    /// <summary>
    /// The measured campus's performance scale holding the MOST levels. The scale grid pages by
    /// scope, but its editor reads the levels by the scale id
    /// (`PerformanceScaleLevelRepository.GetByScale`), so the spec needs one to point at. A campus
    /// with no scale resolves this to 0 and the spec reports SKIP, which is honest.
    /// </summary>
    public long PerformanceScaleId { get; set; } = 1;

    /// <summary>
    /// The measured campus's ASSET holding the MOST maintenance rows. The asset grids page by scope,
    /// but an asset's detail modal reads its maintenance log by the asset id
    /// (`InvMaintenanceRepository.GetByAssetId`), so the spec needs one to point at - and the asset
    /// with the longest log is the one whose read walks the most rows. A campus with no maintenance
    /// resolves this to 0 and the spec reports SKIP, which is honest.
    /// </summary>
    public long InvAssetId { get; set; } = 1;

    /// <summary>
    /// The measured campus's employee holding the MOST tax records. Three desks read a person rather
    /// than a page - the salary-structure panel (`GetCurrentByEmployee`), the leave desk's balance
    /// rings (`GetEmployeeBalance`) and the payroll tax history (`GetByEmployee`) - and all three
    /// take the employee id the user picked rather than filtering by scope. The employee with the
    /// most tax records is the one whose reads walk the most rows. A campus with no tax records
    /// resolves this to 0 and those specs report SKIP, which is honest.
    /// </summary>
    public long EmployeeId { get; set; } = 1;

    /// <summary>
    /// The measured campus's separation that has a settlement. The exit desk opens the settlement
    /// from its separation (`SeparationRepository.GetBySeparation(separationId)`), which takes the
    /// id rather than filtering by scope. A campus with no settlement resolves this to 0 and the
    /// spec reports SKIP, which is honest.
    /// </summary>
    public long SeparationId { get; set; } = 1;

    /// <summary>
    /// The measured campus's INCOME-TAX config - the progressive one, chosen because it holds the
    /// slabs. Its editor reads them by the config id (`TaxConfigRepository.GetByTaxConfig`), which
    /// takes the id rather than filtering by scope. A campus with no tax config resolves this to 0
    /// and the spec reports SKIP, which is honest.
    /// </summary>
    public long TaxConfigId { get; set; } = 1;

    /// <summary>
    /// The measured campus's student that HAS a profile. Every student-profile read is keyed on the
    /// person the tab is showing (`studentGuardian/{studentId}`, `studentContact/{studentId}`,
    /// `studentHealth/{studentId}`, `studentImmunization/{studentId}`) rather than filtering by scope,
    /// so the spec has to be handed one - and one that actually holds contact and health rows, or the
    /// read would measure an empty result and look fast for the wrong reason. A campus with no profile
    /// resolves this to 0 and those specs report SKIP, which is honest.
    /// </summary>
    public long StudentId { get; set; } = 1;

    /// <summary>
    /// The measured campus's assessment tool holding the MOST criteria. `AssessmentToolItemRepository
    /// .GetAll(assessmentToolId)` takes the tool the user opened rather than filtering by scope, so the
    /// spec has to be handed one - and the tool with the longest criterion list is the one whose read
    /// walks the most rows. A campus with no assessment tool resolves this to 0 and the spec reports
    /// SKIP, which is honest: nothing has configured a tool, so nothing can list its criteria.
    /// </summary>
    public long AssessmentToolId { get; set; } = 1;

    /// <summary>
    /// The measured campus's rubric criterion holding the MOST levels. The rubric evidence screen reads
    /// its level set by the criterion (`RubricCriterionLevelRepository.GetAllByItemId(itemId)`), which
    /// takes the id rather than filtering by scope - and the criterion with the fullest level set is the
    /// worst case. A campus with no rubric levels resolves this to 0 and the spec reports SKIP.
    /// </summary>
    public long AssessmentToolItemId { get; set; } = 1;

    /// <summary>
    /// The measured campus's salary structure carrying the MOST component lines. The structure editor
    /// reads its line set BY STRUCTURE (`WHERE essd.SalaryStructureId = @StructureId`) rather than
    /// filtering by scope, and the fullest structure is the worst case for a read whose cost is its own
    /// line count. A campus with no structure resolves it to 0 and the spec reports SKIP, which is
    /// honest - the same rule every per-parent resolver here follows.
    /// </summary>
    public long SalaryStructureId { get; set; } = 1;

    /// <summary>
    /// The measured campus's performance review holding the MOST KPI score lines. Both the review
    /// dialog's score table and its recommendation list are read BY REVIEW, and the review with the
    /// fullest score table is the worst case. A campus with no review resolves it to 0 and those two
    /// specs report SKIP.
    /// </summary>
    public long PerformanceReviewId { get; set; } = 1;

    /// <summary>
    /// The measured campus's payroll record holding the MOST detail lines.
    ///
    /// ⚠️ RESOLVED BY THE TABLE THAT IS ACTUALLY UNDER TEST, NOT BY THE ONE THAT IS EASIER TO QUERY.
    /// `employeepayrolldetail` grows with (employees x components x periods) and is this batch's only
    /// real volume table, so the spec has to be handed a record that carries lines; "the newest payroll
    /// period's first record" would hand it one the seed never broke down and read an empty result as a
    /// fast query. A campus with no payroll record resolves it to 0 and both the detail and adjustment
    /// specs report SKIP.
    /// </summary>
    public long EmployeePayrollId { get; set; } = 1;

    /// <summary>
    /// The report definition carrying the MOST run-log rows, for the reporting desk's own history and
    /// saved views - both are read per definition (`ReportRunLogRepository.GetPage(tenantId,
    /// reportDefinitionId, page)` and `ReportViewRepository.GetByDefinition(...)`), not by scope alone.
    /// The definition with the longest history is the worst case for a paged read. A campus with no run
    /// history resolves this to 0 and both specs report SKIP, which is honest.
    /// </summary>
    public long ReportDefinitionId { get; set; } = 1;

    /// <summary>
    /// A background job that OWNS a completed report export. `ReportExportRepository.GetByJobId` is the
    /// only read of `reportexport` and the download is reached THROUGH the job, so the spec has to be
    /// handed an id that really owns one - a fabricated job id would measure a row the app can never
    /// fetch. A campus with no export resolves this to 0 and the spec reports SKIP.
    /// </summary>
    public long ReportJobId { get; set; } = 1;

    /// <summary>
    /// The meeting carrying the MOST audience rows, for `HrMeetingRepository.GetAudience(meetingId)` -
    /// that read takes the meeting the user opened rather than filtering by scope, so the spec has to be
    /// handed one. The fullest invite list is the worst case rather than the first meeting. A campus
    /// with no audience row resolves this to 0 and the spec reports SKIP.
    /// </summary>
    public long HrMeetingId { get; set; } = 1;

    /// <summary>
    /// The measured campus's NEWEST background job, for `JobsController.GetJob` (`GET api/jobs/{id}`,
    /// the UI's status poll) - that read takes the id the caller is polling rather than filtering by
    /// scope, so the spec has to be handed a real row.
    ///
    /// ⚠️ IT IS RESOLVED PER CAMPUS EVEN THOUGH THE TABLE'S OWN ROUTE HAS NO SCOPE. `api/jobs` carries
    /// no tenant/school/campus segments and the CLAIM is global (`SELECT ... LIMIT 1` with no scope
    /// filter); the job specs are therefore the one family in this catalogue that is deliberately NOT
    /// scoped. This id only has to be a row that exists. A campus with no job resolves it to 0 and the
    /// status spec reports SKIP, which is honest.
    /// </summary>
    public long JobId { get; set; } = 1;

    /// <summary>
    /// The measured SCHOOL's curriculum carrying the most versions. The curriculum grid's own list is
    /// a scope grid, but the VERSION grid is routed as `curriculum/{curriculumId}/curriculumVersion`
    /// and `CurriculumVersionRepository.GetAll(page, curriculumId)` filters `c.Id = @CurriculumId`
    /// rather than by scope - so the spec has to be handed one. A school with no curriculum resolves
    /// this to 0 and the spec reports SKIP, which is honest.
    /// </summary>
    public long CurriculumId { get; set; } = 1;

    /// <summary>
    /// The measured school's VERSION carrying the most grades, for `CurriculumGradeRepository
    /// .GetAll(page, curriculumVersionId)` - it filters `CurriculumVersionId = @id` and `curriculumgrade`
    /// carries NO scope columns (it hangs off its version), so the spec must be handed one. The version
    /// with the fullest grade list is the worst case. A school with no version resolves this to 0 and
    /// the spec reports SKIP.
    /// </summary>
    public long CurriculumVersionId { get; set; } = 1;

    /// <summary>
    /// The measured school's curriculum GRADE carrying the most subjects, for
    /// `CurriculumGradeSubjectRepository.GetAll(page, curriculumGradeId)` - keyed on the grade the user
    /// opened, and `curriculumgradesubject` carries no scope columns either. The fullest grade is the
    /// worst case. A school with no graded subject resolves this to 0 and the spec reports SKIP.
    /// </summary>
    public long CurriculumGradeId { get; set; } = 1;

    /// <summary>
    /// The measured campus's route carrying the MOST stops, for
    /// `TransportRouteStopRepository.GetAllByRoute(page, t, s, c, routeId)` - the stop grid is routed
    /// as `transport/route/{routeId}/stop` and the repository filters `RouteId == routeId` on top of
    /// the scope, so the spec has to be handed one. The fullest route is the worst case rather than
    /// the first route. A campus with no stop resolves this to 0 and the spec reports SKIP.
    /// </summary>
    public long TransportRouteId { get; set; } = 1;

    /// <summary>
    /// The identity the APPROVAL ENGINE's two grids are opened as - the campus's lowest-numbered
    /// `users` row.
    ///
    /// ⚠️ THIS ONE VALUE HAS TO SATISFY TWO DIFFERENT PREDICATES, WHICH IS WHY IT IS RESOLVED BY A
    /// RULE RATHER THAN PINNED. The inbox (`GetPendingApprovals`) matches `s.ApproverUserId = @UserId`
    /// OR a role the user holds through `userrole`; "my requests" (`GetWorkflowHistory`) matches
    /// `w.RequestedBy = @UserId`. A perf campus's users hold the Teacher role while the approval
    /// templates name HR Manager / HOD, so the role branch matches nothing and the seed sets
    /// `ApproverUserId` on every OPEN step to this same user - the fixture and this resolution are
    /// the same query ("lowest by id"), so they cannot disagree. A campus with no user resolves it
    /// to 0 and those two specs report SKIP, which is honest.
    /// </summary>
    public long UserId { get; set; } = 1;

    /// <summary>
    /// The role ids <see cref="UserId"/> holds, in the order `userrole` returns them.
    ///
    /// ⚠️ THIS EXISTS BECAUSE THE PERMISSION LOOKUP BINDS AN ARRAY, NOT AN IN-LIST.
    /// `HasPermissionAttribute` resolves the caller's roles first and then calls
    /// `roleIds.Contains(x.RoleId)`, which `SqlBuilder` renders as `RoleId = ANY(@p0)` - so the spec
    /// has to be handed the same array the filter builds, not a hand-written `IN (...)`. An empty
    /// array is a real outcome (a user with no role), and `= ANY('{}')` matches nothing, which is
    /// exactly what the filter's `!roleIds.Any()` guard then refuses out loud.
    /// </summary>
    public long[] RoleIds { get; set; } = System.Array.Empty<long>();

    /// <summary>
    /// The measured campus's approval cycle holding the MOST steps. The engine's detail reads are
    /// keyed on the cycle the user opened (`GetWorkflowSteps(workflowId)`,
    /// `GetWorkflowsByEntity(module, entityId, scope)`) rather than filtering by scope, so each spec
    /// needs one to point at - and the cycle with the longest chain is the worst case. A campus with
    /// no approvals resolves this to 0 and those specs report SKIP, which is honest.
    /// </summary>
    public long ApprovalWorkflowId { get; set; } = 1;

    /// <summary>
    /// The module of <see cref="ApprovalWorkflowId"/>, passed to `GetWorkflowsByEntity` verbatim.
    /// The repository takes the module NAME, not its id, and filters `w.ModuleName = @ModuleName`.
    /// </summary>
    public string ApprovalModuleName { get; set; } = string.Empty;

    /// <summary>
    /// The entity of <see cref="ApprovalWorkflowId"/>, passed to `GetWorkflowsByEntity` verbatim.
    /// The pair (module, entity) is what identifies one submission's history - a module alone would
    /// return every cycle of that module on the campus.
    /// </summary>
    public long ApprovalEntityId { get; set; }

    /// <summary>
    /// The measured campus's approval TEMPLATE holding the most steps. The template editor reads its
    /// chain by the template id (`ApprovalTemplateStepRepository.GetSteps(templateId)`), which takes
    /// the id rather than filtering by scope. A campus with no template step resolves this to 0 and
    /// the spec reports SKIP, which is honest.
    /// </summary>
    public long ApprovalTemplateId { get; set; } = 1;

    public DateTime AttendanceDate { get; set; } = DateTime.Today;

    /// <summary>
    /// The measured campus's classroom that carries a PUBLISHED timetable.
    ///
    /// ⚠️ THIS IS NOT <see cref="ClassroomId"/>, AND THE DIFFERENCE IS A REAL MEASUREMENT TRAP.
    /// `ClassroomId` is resolved from `attendance` (the register's own classroom) and
    /// `AcademicGradeId` from the campus's first `classroom` row - which on `ayra_perf` points at
    /// `academicgrade` **1**, a grade belonging to CAMPUS 1. Every timetable read joins
    /// `Classroom -> AcademicGrade(Subject)` and filters `Timetable.Status = 'Published'`, so a spec
    /// placed through those two variables would filter on a grade no entry names and report an empty
    /// page while `timetableentry` held 210 rows. `TimetableModuleSeeder` writes the campus's own
    /// spine and this is resolved from IT.
    /// </summary>
    public long TimetableClassroomId { get; set; }

    /// <summary>
    /// The PUBLISHED timetable with the most entries, for `TimetableEntryRepository.GetByTimetableId`
    /// - which takes the timetable id the user opened rather than filtering by scope. A Draft is
    /// never resolved (every entry read filters `Status = 'Published'`), so a campus with no published
    /// timetable resolves it to 0 and the spec reports SKIP, which is honest.
    /// </summary>
    public long TimetableId { get; set; }

    /// <summary>
    /// The grade of <see cref="TimetableClassroomId"/>. `GetClassroomEntries` filters on the classroom's
    /// SECTION and GRADE directly, so the pair has to be the one the seeded classrooms actually use.
    /// </summary>
    public long TimetableGradeId { get; set; }

    /// <summary>The section of <see cref="TimetableClassroomId"/> - see `GetClassroomEntries`.</summary>
    public long TimetableSectionId { get; set; }

    /// <summary>
    /// The campus's period template with the most slots. `TimeTableSetupRepository.GetSetupResponse`
    /// reads the header, its periods/breaks and its off days through the scope rather than by id, but
    /// the PeriodIndex correlated subquery every entry spec runs walks this table once per row - so
    /// the template with the most slots is the worst case rather than the first row.
    /// </summary>
    public long TimetableSetupId { get; set; }

    /// <summary>
    /// The teacher carrying the MOST published periods. The four workload reads
    /// (`GetTeacherEntries`, `GetTeacherWorkloadSubjectStats`, `GetTeacherWorkloadWeekdayStats`,
    /// `CountPublishedByTeacher`) and the teacher assignment panel all take a teacher id rather than
    /// filtering by scope, so each spec has to be handed one - and the busiest teacher is the worst
    /// case.
    /// </summary>
    public long TimetableTeacherId { get; set; }

    /// <summary>
    /// The weekday the campus actually teaches on, passed to
    /// `TimeTableReliefRepository.GetCoverageCells` verbatim. The substitute desk's grid is keyed on a
    /// weekday NAME (a cell is `(classroom, period)` for one day), so a spec that passed the wrong one
    /// would measure the empty side of the join while the desk renders a full day.
    /// </summary>
    public string TimetableWeekDay { get; set; } = string.Empty;

    /// <summary>
    /// A date the campus has substitute coverage ON. The coverage read joins `timetablerelief` on the
    /// DATE and the scheduled cell on the WEEKDAY, so a date with no relief row measures the LEFT
    /// JOIN's empty half - the date is resolved from the relief table itself for that reason.
    /// </summary>
    public DateTime TimetableDate { get; set; } = DateTime.Today;
}

/// <summary>
/// THE SINGLE SOURCE OF TRUTH for the application's query shapes.
///
/// ⚠️ READ THIS BEFORE ADDING A QUERY.
///
/// Both consumers read these shapes:
///   * `db-report/`         - measures them against a live database and diagnoses the plan.
///   * `data-volume/Tests/` - asserts that their cost scales with row count.
///
/// They must NOT hold private copies. They used to, and the copies drifted from the
/// repositories in ways that made the measurements meaningless:
///   * an attendance "list" copied with `INNER JOIN Student` and a projected column list,
///     while the real path is `GenericRepository.GetAllAsync` emitting
///     `SELECT * FROM attendance WHERE TenantId=... AND CampusId=... ORDER BY ... LIMIT ...`
///     with no joins at all - so the test measured a query the app never sends;
///   * a student list copied with an explicit column list that had lost the
///     `COALESCE(sc.StudentStatus, 0) AS EnrollmentStatus` the repository later added.
///
/// So: if you change a repository's SQL, change it HERE, and both consumers move with it.
/// </summary>
public static class QueryCatalog
{
    /// <summary>The page size every grid asks for by default.</summary>
    public const int DefaultPageSize = 50;
    public const int DefaultOffset = 0;

    /// <summary>
    /// How many recent hashes the password-reuse check compares against.
    ///
    /// ⚠️ IT MIRRORS `UserPasswordHistoryRepository.IsReuseAsync`'s `lookback` DEFAULT (5) RATHER THAN
    /// BEING CHOSEN HERE. A spec that scanned a different window would be timing a different query, and
    /// the value is also what decides how many rows the fixture must spread over time.
    /// </summary>
    public const int PasswordHistoryLookback = 5;

    /// <summary>
    /// The budget for a REPORT page's first page.
    ///
    /// ⚠️ This is deliberately NOT the number the application declares. Every seeded
    /// `reportdefinition` carries `maxexecsec = 30`, and `appsettings.json` sets
    /// `PerformanceThresholds.ReportQueryMs = 5000` - i.e. the product's own budget for a report is
    /// "thirty seconds", which cannot fail. A report page is an interactive navigation, so the
    /// number that means something is the one a user would notice: 150 ms for 50 rows.
    ///
    /// It is a single constant so it can be argued with in one place. Measured on the median
    /// campus of `ayra_perf` (2,000 students inside a 14.3M-row attendance table): the healthy
    /// reporting views come back in 6-15 ms and the two broken ones in ~200 ms, so this budget
    /// separates them without sitting on either side's boundary.
    /// </summary>
    public const double ReportPageBudgetMs = 150;

    /// <summary>The active-year subquery StudentRepository.GetAllStudentInfo injects.</summary>
    private const string ActiveYearJoin =
        "(SELECT ay.Id FROM AcademicYear ay " +
        "WHERE ay.TenantId = s.TenantId AND ay.SchoolId = s.SchoolId " +
        "AND ay.CampusId = s.CampusId AND ay.IsActive = TRUE LIMIT 1)";

    /// <summary>The search clause BaseController.SearchClause builds (cast to text before lower()).</summary>
    private const string StudentSearchClause =
        "(lower(s.Name::text) like @search " +
        "OR lower(s.AdmissionNumber::text) like @search " +
        "OR lower(u.Email::text) like @search)";

    /// <summary>Look a shape up by key, failing loudly rather than silently measuring nothing.</summary>
    public static QuerySpec Require(IEnumerable<QuerySpec> specs, string key)
        => specs.FirstOrDefault(s => s.Key == key)
           ?? throw new KeyNotFoundException(
               $"No query shape named '{key}' in QueryCatalog. Known keys: " +
               string.Join(", ", specs.Select(s => s.Key)));

    /// <summary>
    /// The index this query needs, or null when an index is not the answer - which is a real
    /// outcome, not a gap: the invoice list is unbounded, and no index fixes an unbounded read.
    /// </summary>
    public static IndexCandidate? IndexFor(QuerySpec spec) =>
        spec.IndexTable != null && spec.IndexName != null && spec.IndexColumns != null
            ? new IndexCandidate(spec.IndexTable, spec.IndexName, spec.IndexColumns, spec.IndexRationale ?? string.Empty)
            : null;

    /// <summary>Copy a spec's parameters so the caller can override values without mutating the catalogue.</summary>
    public static Dictionary<string, object> ParametersOf(QuerySpec spec, long tenantId, long schoolId, long campusId)
    {
        var bag = spec.Params == null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(spec.Params);
        bag["tenantId"] = tenantId;
        bag["schoolId"] = schoolId;
        bag["campusId"] = campusId;
        return bag;
    }

    public static List<QuerySpec> Build(ScopeVars v) => new()
    {
        // =====================================================================
        // STUDENT LIST - StudentRepository.GetAllStudentInfo
        // =====================================================================
        new QuerySpec
        {
            Key = "student-list-page",
            Title = "Student grid - first page",
            Source = "StudentRepository.GetAllStudentInfo",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 500,
            IndexTable = "student",
            IndexName = "ix_student_tenantschoolcampus_isactive",
            IndexColumns = "tenantid, schoolid, campusid, isactive",
            IndexRationale = "the WHERE filters on exactly those four columns; today only ix_student_tenantid exists, and the ORDER BY s.Name is what the planner actually uses.",
            Sql = $@"
                SELECT s.*, u.Email, c.classroomname, sc.Id AS StudentEnrollmentId,
                       COALESCE(sc.StudentStatus, 0) AS EnrollmentStatus
                FROM Student s
                INNER JOIN Parent t ON s.ParentId = t.Id
                INNER JOIN Users u ON t.UserId = u.Id
                LEFT JOIN StudentEnrollment sc ON sc.StudentId = s.Id
                    AND sc.TenantId = s.TenantId AND sc.SchoolId = s.SchoolId AND sc.CampusId = s.CampusId
                    AND sc.AcademicYearId = {ActiveYearJoin}
                LEFT JOIN Classroom c ON c.Id = sc.ClassroomId
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId
                  AND s.IsActive = true
                ORDER BY s.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // STUDENT COUNT - the countQuery half of the same method.
        // =====================================================================
        new QuerySpec
        {
            Key = "student-count",
            Title = "Student grid - total count",
            Source = "StudentRepository.GetAllStudentInfo (countQuery)",
            P95BudgetMs = 300,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 500,
            IndexTable = "student",
            IndexName = "ix_student_tenantschoolcampus_isactive",
            IndexColumns = "tenantid, schoolid, campusid, isactive",
            IndexRationale = "turns the count into an index scan over only the matching rows, instead of reading every student row and then filtering.",
            Sql = $@"
                SELECT COUNT(*)
                FROM Student s
                INNER JOIN Parent t ON s.ParentId = t.Id
                INNER JOIN Users u ON t.UserId = u.Id
                LEFT JOIN StudentEnrollment sc ON sc.StudentId = s.Id
                    AND sc.TenantId = s.TenantId AND sc.SchoolId = s.SchoolId AND sc.CampusId = s.CampusId
                    AND sc.AcademicYearId = {ActiveYearJoin}
                LEFT JOIN Classroom c ON c.Id = sc.ClassroomId
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId
                  AND s.IsActive = true",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // STUDENT COUNT (SEARCHED) - the same countQuery with the search clause
        // appended, which is a DIFFERENT shape from "student-count" and must be
        // measured separately: the filter is applied to every joined row.
        // =====================================================================
        new QuerySpec
        {
            Key = "student-count-search",
            Title = "Student grid - total count, filtered by a search",
            Source = "StudentRepository.GetAllStudentInfo (countQuery + BaseController.SearchClause)",
            P95BudgetMs = 500,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            Sql = $@"
                SELECT COUNT(*)
                FROM Student s
                INNER JOIN Parent t ON s.ParentId = t.Id
                INNER JOIN Users u ON t.UserId = u.Id
                LEFT JOIN StudentEnrollment sc ON sc.StudentId = s.Id
                    AND sc.TenantId = s.TenantId AND sc.SchoolId = s.SchoolId AND sc.CampusId = s.CampusId
                    AND sc.AcademicYearId = {ActiveYearJoin}
                LEFT JOIN Classroom c ON c.Id = sc.ClassroomId
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId
                  AND s.IsActive = true
                  AND {StudentSearchClause}",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["search"] = "%a%",
            },
        },

        // =====================================================================
        // STUDENT SEARCH - what happens when a user types in the grid's search box.
        // =====================================================================
        new QuerySpec
        {
            Key = "student-search",
            Title = "Student grid - search by a name",
            Source = "BaseController.SearchClause over StudentRepository.GetAllStudentInfo",
            P95BudgetMs = 500,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            Sql = $@"
                SELECT s.Id, s.Name, s.AdmissionNumber
                FROM Student s
                INNER JOIN Parent t ON s.ParentId = t.Id
                INNER JOIN Users u ON t.UserId = u.Id
                LEFT JOIN StudentEnrollment sc ON sc.StudentId = s.Id
                    AND sc.TenantId = s.TenantId AND sc.SchoolId = s.SchoolId AND sc.CampusId = s.CampusId
                    AND sc.AcademicYearId = {ActiveYearJoin}
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId
                  AND s.IsActive = true
                  AND {StudentSearchClause}
                ORDER BY s.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["search"] = "%a%",
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // ATTENDANCE LIST - GenericRepository.GetAllAsync, which is what
        // AttendanceRepository.GetAll(page, tenantId, campusId) calls. Note the
        // shape: `SELECT * ... WHERE TenantId/CampusId ORDER BY <client column>
        // LIMIT n OFFSET m` with NO index on the scope columns and NO joins.
        // =====================================================================
        new QuerySpec
        {
            Key = "attendance-list-page",
            Title = "Attendance grid - first page",
            Source = "AttendanceRepository.GetAll -> GenericRepository.GetAllAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM attendance WHERE tenantid = @tenantId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "attendance",
            IndexName = "idx_attendance_scope_date",
            IndexColumns = "tenantid, campusid, attendancedate DESC",
            IndexRationale = "the filter and the ORDER BY are both in the index, so the LIMIT can stop after 50 index entries instead of sorting every matching row.",
            Sql = @"
                SELECT * FROM attendance
                WHERE TenantId = @tenantId AND CampusId = @campusId
                ORDER BY AttendanceDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // ATTENDANCE COUNT - the other half of every attendance grid page load.
        // =====================================================================
        new QuerySpec
        {
            Key = "attendance-count",
            Title = "Attendance grid - total count",
            Source = "AttendanceRepository.GetAll -> GenericRepository.GetAllAsync (countQuery)",
            P95BudgetMs = 300,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM attendance WHERE tenantid = @tenantId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "attendance",
            IndexName = "idx_attendance_scope",
            IndexColumns = "tenantid, campusid",
            IndexRationale = "a narrow index-only scan of the scope columns, instead of reading every column of every row.",
            Sql = @"
                SELECT COUNT(0) FROM attendance
                WHERE TenantId = @tenantId AND CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // ATTENDANCE BY CLASSROOM + DATE - the register screen. AttendanceDate is a
        // timestamp, so the app compares `AttendanceDate::date = @date`, which
        // CANNOT use a plain index on attendancedate.
        // =====================================================================
        new QuerySpec
        {
            Key = "attendance-classroom-date",
            Title = "Attendance register - one classroom, one day",
            Source = "DashboardRepository.GetClassroomAttendance",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM attendance WHERE tenantid = @tenantId AND campusid = @campusId AND classroomid = @classroomId",
            MinVolume = 500,
            IndexTable = "attendance",
            IndexName = "idx_attendance_classroom_date",
            IndexColumns = "tenantid, campusid, classroomid, attendancedate",
            IndexRationale = "the classroom narrows the set first; the date column then filters within it. A `::date` cast on the column still needs the date in the index to help.",
            Sql = @"
                SELECT a.Id, a.StudentId, a.SubjectId, a.AttendanceDate, a.IsPresent, a.Remarks
                FROM Attendance a
                WHERE a.TenantId = @tenantId AND a.SchoolId = @schoolId AND a.CampusId = @campusId
                  AND a.ClassroomId = @classroomId
                  AND a.AttendanceDate::date = @date",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["date"] = v.AttendanceDate,
            },
        },

        // =====================================================================
        // INVOICE LIST - ⚠️ UNBOUNDED, and that is the shape, not an oversight.
        //
        // InvoiceRepository.GetAll(t, s, c) delegates to GenericRepository.FindAsync,
        // which emits `SELECT * FROM <table> WHERE <clause>` - NO ORDER BY, NO LIMIT.
        // So this "list" materialises EVERY matching invoice. An earlier version of
        // this entry claimed an ORDER BY/LIMIT paged query, which the code does not
        // send; it is recorded honestly here instead.
        // =====================================================================
        new QuerySpec
        {
            Key = "invoice-list-page",
            Title = "Invoice list - EVERY invoice for the campus (unbounded)",
            Source = "InvoiceRepository.GetAll -> GenericRepository.FindAsync",
            P95BudgetMs = 500,
            VolumeSql = "SELECT COUNT(*) FROM invoices WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1000,
            IndexTable = "invoices",
            IndexName = "idx_invoices_scope",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the only filter is the scope triple, and no current index carries all three; but note an index cannot fix an unbounded read - only a LIMIT can.",
            Sql = @"
                SELECT * FROM Invoices
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // DASHBOARD - the per-campus counters. No campus filter at all: it groups
        // every student in the school, so it scales with the whole school.
        // =====================================================================
        new QuerySpec
        {
            Key = "dashboard-campus-counts",
            Title = "Dashboard - students per campus",
            Source = "DashboardRepository (student count by campus)",
            P95BudgetMs = 1000,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId",
            MinVolume = 500,
            IndexTable = "student",
            IndexName = "ix_student_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the group-by is on CampusId, so an index carrying it lets the planner group without a full sort of every student row.",
            Sql = @"
                SELECT COUNT(0) AS TotalCount, b.Name
                FROM Student s
                INNER JOIN Campus b ON s.CampusId = b.Id
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId
                GROUP BY b.Name
                ORDER BY b.Name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
            },
        },

        // =====================================================================
        // FEE / ENROLMENT MODULE
        //
        // ⚠️ THESE ARE THE ONLY SPECS WHOSE SQL IS NOT PARAMETERIZED, and keeping
        // them that way is FAITHFUL, not sloppy.
        //
        // StudentEnrollmentRepository builds its SQL by string interpolation
        // (`where st.campusId = {campusId}`), so PostgreSQL receives LITERALS and can
        // plan against the actual values. That is a measurable difference from the
        // parameterized queries the rest of this catalogue models, where the planner
        // may fall back to a generic plan that cannot use the value's selectivity.
        // Rewriting these into bind parameters here would measure a statement the
        // server never sends - the one thing this catalogue exists to prevent.
        //
        // It is also a finding in its own right: interpolated scope values are one
        // step from an injection, and the only reason this is not one is that every
        // value comes from a route or a typed query parameter.
        // =====================================================================
        new QuerySpec
        {
            Key = "enrollment-grade-count",
            Title = "Enrolment / exemption picker - count of a grade's students",
            Source = "StudentEnrollmentRepository.GetAllGradeStudents (countQuery, interpolated literals)",
            P95BudgetMs = 300,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "studentenrollment",
            IndexName = "ix_studentenrollment_scope_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale = "the grid filters on all four columns; the shipped ix_studentenrollment_tenantschoolcampus stops at campusid, so the year filter and all four joins are applied to every enrolment of the campus.",
            Sql = $@"
                SELECT COUNT(*) FROM StudentEnrollment st
                inner join Student s on st.StudentId = s.Id and s.IsActive = true
                inner join Parent p on s.ParentId = p.Id
                inner join Users u on p.UserId = u.Id
                inner join Classroom c on c.Id = st.ClassroomId
                where st.campusId = {v.CampusId} and st.TenantId = {v.TenantId} and st.SchoolId = {v.SchoolId}
                  and c.AcademicGradeId = {v.AcademicGradeId} and st.AcademicYearId = {v.AcademicYearId}",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "enrollment-grade-page",
            Title = "Enrolment / exemption picker - first page of a grade's students",
            Source = "StudentEnrollmentRepository.GetAllGradeStudents (searchQuery, interpolated literals)",
            P95BudgetMs = 300,
            VolumeSql = "SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "studentenrollment",
            IndexName = "ix_studentenrollment_scope_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale = "same four-column filter as the count, and it is the page that pays for the four joins once the index has narrowed the set.",
            Sql = $@"
                select st.*,s.Name,s.AdmissionNumber,s.Age,s.Dob,s.Gender,
                       u.FirstName || ' ' || u.LastName AS Parent,u.Mobile,u.Email,p.Id as ParentId,s.Photo,
                       u.ConfirmEmail,u.ConfirmMobile,c.ClassroomName
                from StudentEnrollment st
                inner join Student s on st.StudentId = s.Id and s.IsActive = true
                inner join Parent p on s.ParentId = p.Id
                inner join Users u on p.UserId = u.Id
                inner join Classroom c on c.Id = st.ClassroomId
                where st.campusId = {v.CampusId} and st.TenantId = {v.TenantId} and st.SchoolId = {v.SchoolId}
                  and c.AcademicGradeId = {v.AcademicGradeId} and st.AcademicYearId = {v.AcademicYearId}
                ORDER BY s.Name
                LIMIT {DefaultPageSize} OFFSET {DefaultOffset}",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "enrollment-classroom-page",
            Title = "Fee assignment wizard - first page of a classroom's enrolled students",
            Source = "StudentEnrollmentRepository.GetEnrolledStudentsByYearPaged (interpolated literals)",
            P95BudgetMs = 400,
            VolumeSql = "SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "studentenrollment",
            IndexName = "ix_studentenrollment_classroom_year",
            IndexColumns = "tenantid, schoolid, campusid, classroomid, academicyearid",
            IndexRationale = "this one filters on classroom AND year, and the shipped ix_studentenrollment_classroomid carries only the classroom - so the year is applied to every enrolment that classroom has ever had, which grows every year.",
            Sql = $@"
                SELECT st.*, s.Name, s.Age, s.Dob, s.Gender, s.AdmissionNumber,
                       c.ClassroomName, u.FirstName || ' ' || u.LastName AS Parent,
                       u.Mobile, u.Email, p.Id AS ParentId, s.Photo,
                       u.ConfirmEmail, u.ConfirmMobile,
                       sfa.Id AS FeeAssignmentId, sfa.Status AS FeeAssignmentStatus,
                       CASE sfa.Status
                           WHEN 0 THEN 'Draft'
                           WHEN 1 THEN 'Active'
                           WHEN 2 THEN 'Suspended'
                           WHEN 3 THEN 'Cancelled'
                           ELSE ''
                       END AS FeeAssignmentStatusName,
                       fs.Name AS FeeStructureName
                   FROM StudentEnrollment st
                   INNER JOIN Student s ON st.StudentId = s.Id AND s.IsActive = true
                   INNER JOIN Parent p ON s.ParentId = p.Id
                   INNER JOIN Users u ON p.UserId = u.Id
                   INNER JOIN Classroom c ON c.Id = st.ClassroomId
                   LEFT JOIN StudentFeeAssignment sfa ON sfa.StudentEnrollmentId = st.Id
                                                     AND sfa.TenantId = st.TenantId
                                                     AND sfa.SchoolId = st.SchoolId
                                                     AND sfa.CampusId = st.CampusId
                                                     AND sfa.Status = 1
                   LEFT JOIN FeeStructure fs ON fs.Id = sfa.FeeStructureId
                  WHERE st.TenantId = {v.TenantId} AND st.SchoolId = {v.SchoolId} AND st.CampusId = {v.CampusId}
                    AND st.ClassroomId = {v.ClassroomId} AND st.AcademicYearId = {v.AcademicYearId}
                    AND st.StudentStatus NOT IN (4, 5, 6, 7)
                  ORDER BY s.Name
                  LIMIT {DefaultPageSize} OFFSET {DefaultOffset}",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "payment-transactions-count",
            Title = "Transactions screen - count of the year's payments",
            Source = "PaymentRepository.GetAllTransactions (countQuery)",
            P95BudgetMs = 300,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM payment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "payment",
            IndexName = "ix_payment_scope_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale = "the count filters on the scope triple AND the year; payment ships only single-column indexes (tenantid, academicyearid, studentid), so no index carries the whole filter and the joins run over a broader set than they need to.",
            Sql = @"
                SELECT COUNT(st.id)
                    FROM payment st
                    INNER JOIN student s ON st.studentid = s.id
                    LEFT JOIN invoices i ON st.invoiceid = i.id
                    LEFT JOIN schoolevent e ON st.eventid = e.id
                    LEFT JOIN paymentmethod p ON st.paymentmethodid = p.id
                    WHERE st.tenantid = @TenantId
                      AND st.schoolid = @schoolId
                      AND st.campusid = @campusId
                      AND st.academicyearid = @AcademicYearId
                      AND st.amountpaid > 0",
            Params = new Dictionary<string, object>
            {
                ["TenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["AcademicYearId"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "payment-transactions-page",
            Title = "Transactions screen - first page (5-table join)",
            Source = "PaymentRepository.GetAllTransactions (searchQuery)",
            P95BudgetMs = 500,
            VolumeSql = "SELECT COUNT(*) FROM payment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "payment",
            IndexName = "ix_payment_scope_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale = "the page reads four joined tables per row, so narrowing the driving set before the joins is what the index buys.",
            Sql = @"
                SELECT
                    st.id, st.studentid, st.invoiceid, st.paymentmethodid, st.amountpaid,
                    st.transactiondate, st.transactionstatus, st.receiptnumber, st.notes,
                    st.tenantid, st.campusid, st.createdon, st.modifiedon,
                    st.transactionreferencenumber, st.chequenumber, st.receivedby, st.voidedby, st.refundedby,
                    st.receivedfrom, st.eventid, st.academicyearid, st.termid,
                    s.id AS stdId, s.id as Id, s.name, s.age, s.dob, s.gender, s.photo, s.parentid,
                    s.isactive, s.admissionnumber, s.enrollmentdate, s.status, s.tenantid AS TenantId,
                    s.campusid AS campusId, s.createdon AS CreatedOn, s.modifiedon AS ModifiedOn,
                    i.id AS invId, i.id as Id, i.feeid, i.invoicenumber, i.studentid AS StudentId, i.totalamount,
                    i.invoicedate, i.baseamount, i.amountpaid AS AmountPaid, i.totaldiscount,
                    i.adhocchargeamount, i.latefeeamount, i.totaltaxamount,
                    i.duedate, i.status AS Status, i.academicyearid, i.termid, i.tenantid AS TenantId,
                    i.campusid AS campusId, i.createdon AS CreatedOn, i.modifiedon AS ModifiedOn,
                    e.id AS evtId, e.id as Id, e.title AS Name, e.description AS Description,
                    e.startdate, e.enddate, e.starttime, e.endtime, e.amount, e.duedate,
                    p.id AS methodId, p.id as Id, p.name AS Name, p.description AS Description,
                    p.tenantid AS TenantId, p.campusid AS campusId, p.createdon AS CreatedOn,
                    p.modifiedon AS ModifiedOn
                FROM payment st
                INNER JOIN student s ON st.studentid = s.id
                LEFT JOIN invoices i ON st.invoiceid = i.id
                LEFT JOIN schoolevent e ON st.eventid = e.id
                LEFT JOIN paymentmethod p ON st.paymentmethodid = p.id
                WHERE st.tenantid = @TenantId
                  AND st.schoolid = @schoolId
                  AND st.campusid = @campusId
                  AND st.academicyearid = @AcademicYearId
                  AND st.amountpaid > 0
                ORDER BY st.transactiondate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["TenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["AcademicYearId"] = v.AcademicYearId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // OUTSTANDING BALANCE - the guard the PROMOTION and TRANSFER screens run.
        //
        // It is one query per student, so what matters is not its own latency but
        // that it must not be (and today is not) called in the row of a grid. It is
        // measured because it is the only aggregate over invoices that a user action
        // depends on, and because an index on invoices(scope, enrollment) is what it
        // asks for.
        // =====================================================================
        new QuerySpec
        {
            Key = "outstanding-balance-by-enrollment",
            Title = "Promotion / transfer guard - outstanding balance for one enrolment",
            Source = "InvoiceRepository.GetOutstandingBalanceByEnrollment",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM invoices WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1000,
            IndexTable = "invoices",
            IndexName = "ix_invoices_scope_enrollment",
            IndexColumns = "tenantid, schoolid, campusid, studentenrollmentid",
            IndexRationale = "the filter is the scope triple plus the enrolment; invoices ships ix_invoices_studentenrollmentid alone, so the scope is filtered after the lookup rather than before it.",
            Sql = @"
                SELECT COALESCE(SUM(i.TotalAmount - i.AmountPaid), 0)
                  FROM Invoices i
                 WHERE i.TenantId = @TenantId AND i.SchoolId = @schoolId AND i.CampusId = @campusId
                   AND i.StudentEnrollmentId = @EnrollmentId
                   AND i.TotalAmount - i.AmountPaid > 0",
            Params = new Dictionary<string, object>
            {
                ["TenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["EnrollmentId"] = v.EnrollmentId,
            },
        },

        // =====================================================================
        // THE GENERIC REPORTING ENGINE - every shipped reportdefinition.
        //
        // ⚠️ WHY THESE WERE MISSING, AND WHAT IT COST. All 17 seeded `reportdefinition` rows run
        // over 14 `vw_*` views, and until they were transcribed here the catalogue measured NONE
        // of them - so the tool reported a healthy database while two of those reports took ~200 ms
        // to return a single page on a 2,000-student campus. A catalogue that covers the grids but
        // not the reports is the "looks like module coverage and measures nothing" trap the README
        // warns about, one module over.
        //
        // The SQL below is the engine's own output shape, verbatim
        // (`ReportQueryBuilder.Build` with no filters, pageSize 50, pageNumber 1 and
        // includeTotalCount = true), because a spec exists to reproduce the application:
        //
        //   SELECT <columns, in Sequence order>, COUNT(*) OVER() AS __total_rows
        //     FROM <databaseobject>
        //    [WHERE <the definition's DEFAULT filter values>]
        //    LIMIT @__limit OFFSET @__offset
        //
        // Three things about that shape are load-bearing, and each one is a finding on its own:
        //
        //   1. `COUNT(*) OVER()` is not free. A window aggregate is computed over the WHOLE
        //      partition before the first row can be returned, so the LIMIT cannot short-circuit:
        //      page 1 reads the entire filtered set. Measured on the median campus, that is the
        //      difference between 0.4 ms and 199 ms on `vw_outstanding_fees` (470x) and between
        //      0.8 ms and 14.6 ms on `vw_student_academic_risk` (17x). Every seeded definition used
        //      countmode='window' until V133 flipped the two that measured faster under 'separate'
        //      (see point 4) - the other 15 still pay it. Models the real request deliberately -
        //      do NOT drop the window term here to make the numbers look better.
        //   2. The scope is a SESSION SETTING, not a bind parameter - hence ReportScope.
        //   3. Most views filter the scope on their ANCHOR table only (usually `student`), so the
        //      fact table they join is never pruned by the scope. `vw_student_attendance` reaches
        //      `attendance` through `ix_attendance_studentid` once per student; `vw_outstanding_fees`
        //      never sees the scope on `invoices` at all. See each spec's VolumeSql note.
        //
        //   4. `countmode='separate'` is a PER-REPORT lever, not a blanket fix, and it was re-measured
        //      after V132 changed the plans that made the old version of this note STALE. It removes the
        //      `COUNT(*) OVER()` term from the page query but replaces it with a second
        //      `SELECT COUNT(*) FROM <view>` that re-runs the whole view, so the user pays page + count
        //      (the engine awaits `ExecuteAsync` then `ExecuteCountAsync`). MEASURED on campus 15,
        //      `--only rpt --runs 5`, p95 ms, budget 150:
        //
        //        report                  window   sep page   sep count   sep TOTAL   effect
        //        STUDENT_ACADEMIC_RISK      176         19          72          91   -48%  WIN
        //        FEE_COLLECTION             159         25          59          84   -47%  WIN
        //        OUTSTANDING_FEES           248         11         224         235    -5%  still over
        //        STUDENT_ATTENDANCE         227        205         196         401   +77%  WORSE
        //        STUDENT_ABSENTEE_LIST      224        197         188         385   +72%  WORSE
        //
        //      Two findings, and V133 acted on exactly one of them (it flips ONLY the two winners):
        //        * separate WINS where dropping the window term makes the page nearly free - the
        //          whole-view/LATERAL evaluation the window count forced is what the page no longer does.
        //          On `vw_student_academic_risk` that is 176 -> 91 and on `vw_fee_collection` 159 -> 84;
        //        * separate LOSES where the count alone costs about what the whole window query costs
        //          (attendance 196 vs 227; absentee 188 vs 224). There the cost is the VIEW EVALUATION,
        //          not the count mode, and no count mode removes it.
        //
        //      ⚠️ THE OLD NOTE'S `vw_fee_collection` NUMBER IS RETIRED, and its lesson is the point of
        //      this block. It read "3.7x REGRESSION (57 ms -> 185 + 24 ms)" because the page query used
        //      to nested-loop a `Seq Scan on paymentallocation` (36,103 rows). V132 added
        //      `ix_paymentallocation_tenantschoolcampus_invoice`, that plan is gone, and the very same
        //      page query now measures 25 ms. A perf decision recorded before an index lands must be
        //      re-measured after it - which is why the A/B was run again rather than trusted.
        //
        //      `OUTSTANDING_FEES` stays in window mode on purpose: its page drops to 11 ms under
        //      separate but its count is 224 ms, so separate would move the cost, not leave it
        //      (248 -> 235 is noise, and the second view evaluation buys nothing).
        //
        // ⚠️ NO INDEX IS SUGGESTED FOR THESE, AND THAT IS THE HONEST ANSWER rather than an
        // omission. Neither defect is an index problem: one is a window function defeating the
        // LIMIT, the other is a predicate that never reaches the table it needs to prune. An index
        // on a table the view does not filter is never chosen by the planner - which is exactly
        // what the tool prints when QueryCatalog.IndexFor returns null.
        //
        // ⚠️ VolumeSql counts the DRIVING TABLE's rows for the scope, not the view's OUTPUT rows.
        // That is the literal reading of the field ("rows this query would have to look at") and it
        // is what keeps the gate honest here: a view whose scope filter never reaches its fact table
        // scans the fact table and can return ZERO rows - `vw_student_attendance` does exactly that
        // on the perf dataset, spending 190 ms to print nothing. A gate on output rows would SKIP
        // the very query whose cost is the finding.
        // =====================================================================

        ReportSpec(v, "rpt-student-enrollment", "Report: Student Enrollment - first page",
            "STUDENT_ENROLLMENT",
            @"SELECT enrollmentid, studentname, admissionnumber, rollnumber, gender, gradename,
                     classroomname, academicyearname, enrollmentdate, studentstatus,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_student_enrollment
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-student-attendance", "Report: Student Attendance - first page",
            "STUDENT_ATTENDANCE",
            @"SELECT attendanceid, attendancedate, studentname, admissionnumber, classroomname,
                     gradename, academicyearname, ispresent, COUNT(*) OVER() AS __total_rows
                FROM vw_student_attendance
               LIMIT @__limit OFFSET @__offset",
            // The view joins `attendance` with the scope applied to `student` only, so this counts
            // the fact table the query must actually traverse. On ayra_perf it is 10,000 rows while
            // the report returns 0 (the seeded attendance references no enrollment row).
            "SELECT COUNT(*) FROM attendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-student-absentee", "Report: Student Absentee List - first page (default filter applied)",
            "STUDENT_ABSENTEE_LIST",
            @"SELECT attendanceid, attendancedate, studentname, admissionnumber, classroomname,
                     gradename, ispresent, COUNT(*) OVER() AS __total_rows
                FROM vw_student_attendance
               WHERE ispresent = @ispresent
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM attendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            // The definition ships `defaultvalue = 'false'` on `ispresent`, and the validator
            // applies a default even when the client sends no filters - so this is the shape of a
            // report opened with nothing filled in, not a filtered variant of the spec above.
            new Dictionary<string, object> { ["ispresent"] = false }),



        ReportSpec(v, "rpt-employee-headcount", "Report: Employee Headcount - first page",
            "EMPLOYEE_HEADCOUNT",
            @"SELECT employeeid, employeecode, employeename, gender, departmentname, designationname,
                     employmentstatus, joiningdate, isactive, COUNT(*) OVER() AS __total_rows
                FROM vw_employee_headcount
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM employee WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            minVolume: 50),

        ReportSpec(v, "rpt-outstanding-fees", "Report: Outstanding Fees - first page",
            "OUTSTANDING_FEES",
            @"SELECT invoiceid, invoicenumber, studentname, admissionnumber, gradename, classroomname,
                     academicyearname, feecategoryname, invoicedate, duedate, totalamount, amountpaid,
                     outstanding, COUNT(*) OVER() AS __total_rows
                FROM vw_outstanding_fees
               LIMIT @__limit OFFSET @__offset",
            // The view's WHERE is on `student`; `invoices` carries no scope predicate, so the planner
            // is free to seq-scan the whole table and hash-join the campus's students onto it.
            "SELECT COUNT(*) FROM invoices WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-student-demographics", "Report: Student Demographics - first page",
            "STUDENT_DEMOGRAPHICS",
            @"SELECT enrollmentid, studentname, admissionnumber, gender, age, gradename, classroomname,
                     academicyearname, studentstatus, COUNT(*) OVER() AS __total_rows
                FROM vw_student_demographics
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-grade-performance", "Report: Grade Performance - first page",
            "GRADE_PERFORMANCE",
            @"SELECT resultid, studentname, gradename, classroomname, subjectname, termname,
                     academicyearname, obtainedmarks, totalmarks, percentage, grade, ispass, isfail,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_student_subject_results
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM studentsubjectresult WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        // ⚠️ V135 FLIPPED THIS definition to countmode='separate' - the IN-TOOL A/B measured
        // 240-249 ms under the window count against 62-66 ms page + 81-93 ms count = 143-159 ms
        // (a ~40% cut that lands ON the 150 ms line). So this spec is the engine's SEPARATE shape
        // (no window term in the page query) and `rpt-assessment-performance-count` below is the
        // second round trip `ReportQueryExecutor.ExecuteCountAsync` awaits.
        ReportSpec(v, "rpt-assessment-performance", "Report: Assessment Performance - first page (V135: separate)",
            "ASSESSMENT_PERFORMANCE",
            @"SELECT assessmentid, studentname, assessmentcomponentname, subjectname, gradename,
                     classroomname, termname, academicyearname, maxmarks, obtainedmarks, percentage,
                     ispass, isabsent
                FROM vw_assessment_performance
               LIMIT @__limit OFFSET @__offset",
            // ⚠️ THIS VIEW ONCE HAD NO SCOPE PREDICATE AT ALL - its only WHERE lived inside a
            // LATERAL - and `V131__Reporting_View_Scope_Isolation.sql` fixed that on the FACT
            // table (`sa.*`). So the volume probe can be scoped exactly like every sibling
            // report's, and the earlier "no scope to count, keep it unscoped" note here was
            // superseded by that migration. (An unscoped probe is the dangerous direction:
            // it passes the gate for a scope the query answers nothing for.)
            "SELECT COUNT(*) FROM studentassessment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            countMode: "separate"),
        ReportSpec(v, "rpt-assessment-performance-count", "Report: Assessment Performance - count (V135: separate)",
            "ASSESSMENT_PERFORMANCE", "SELECT COUNT(*) FROM vw_assessment_performance",
            "SELECT COUNT(*) FROM studentassessment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            countMode: "separate", scalar: true),

        // ⚠️ V135 FLIPPED THIS definition to countmode='separate' - the IN-TOOL A/B measured
        // 430-487 ms under the window count against 23-30 ms page + 6-7 ms count = 30-35 ms,
        // i.e. about 15x. The count leg is CHEAP here because the planner drops the view's
        // LATERAL when the projection does not ask for `plancount`/`plannedminutes`, so paying
        // for the view twice costs almost nothing while removing the whole-set materialisation.
        ReportSpec(v, "rpt-learning-outcome", "Report: Learning Outcome Performance - first page (V135: separate)",
            "LEARNING_OUTCOME_PERFORMANCE",
            @"SELECT topicid, topicname, learningobjective, gradename, subjectname, curriculumname,
                     curriculumversion, estimatedhours, plancount, plannedminutes
                FROM vw_learning_outcome_performance
               LIMIT @__limit OFFSET @__offset",
            // Curriculum tables are SCHOOL-scoped (the view has no campus predicate), so this one
            // counts the view's own rows - the only volume statement that matches its scope.
            "SELECT COUNT(*) FROM vw_learning_outcome_performance",
            countMode: "separate"),
        ReportSpec(v, "rpt-learning-outcome-count", "Report: Learning Outcome Performance - count (V135: separate)",
            "LEARNING_OUTCOME_PERFORMANCE", "SELECT COUNT(*) FROM vw_learning_outcome_performance",
            "SELECT COUNT(*) FROM vw_learning_outcome_performance",
            countMode: "separate", scalar: true),

        // ⚠️ V133 flipped THIS definition to countmode='separate' - it measured 176 ms under the
        // window count against 19 ms page + 72 ms count. So this spec is the engine's SEPARATE shape
        // (no window term in the page query) and `rpt-student-academic-risk-count` below is the
        // second round trip `ReportQueryExecutor.ExecuteCountAsync` awaits. Both are under budget;
        // the window shape was not.
        ReportSpec(v, "rpt-student-academic-risk", "Report: Student Academic Risk - first page (V133: separate)",
            "STUDENT_ACADEMIC_RISK",
            @"SELECT studentid, studentname, admissionnumber, gradename, classroomname, academicyearname,
                     overallgrade, percentage, failedsubjectcount, lowgradecount, attendancepercent,
                     missingassessments, riskscore, risklevel, riskreasons
                FROM vw_student_academic_risk
               LIMIT @__limit OFFSET @__offset",
            // Anchored on `student` with four LATERAL subqueries per student, so the student table
            // is what the query walks - and it is the window count that forced all of them to be
            // evaluated before page 1 could return.
            "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            countMode: "separate"),
        ReportSpec(v, "rpt-student-academic-risk-count", "Report: Student Academic Risk - count (V133: separate)",
            "STUDENT_ACADEMIC_RISK", "SELECT COUNT(*) FROM vw_student_academic_risk",
            "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            countMode: "separate", scalar: true),

        ReportSpec(v, "rpt-teacher-attendance", "Report: Teacher Attendance - first page",
            "TEACHER_ATTENDANCE",
            @"SELECT attendanceid, employeename, employeecode, departmentname, designationname,
                     attendancedate, attendancestatusname, countsaspresent, countsasabsent, lateminutes,
                     workinghours, COUNT(*) OVER() AS __total_rows
                FROM vw_teacher_attendance
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM employeeattendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-leave-summary", "Report: Leave Summary - first page",
            "LEAVE_SUMMARY",
            @"SELECT requestid, employeename, employeecode, departmentname, leavetypename, fromdate,
                     todate, requesteddays, approveddays, pendingdays, rejecteddays, status,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_leave_summary
               LIMIT @__limit OFFSET @__offset",
            // ⚠️ A REAL SCHOOL EMPLOYS TENS OF PEOPLE, NOT THOUSANDS. The honest volume here is
            // ~480 on the median campus (120 staff x 4 requests per employee), well under the
            // 1,000 default - so the gate has to be stated per-spec or this report sits at
            // "not measured yet" forever. Same reasoning as `rpt-employee-headcount`'s 50.
            "SELECT COUNT(*) FROM employeeleaverequest WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            minVolume: 400),

        // ⚠️ V133 flipped THIS definition to countmode='separate' - 159 ms under the window count
        // against 25 ms page + 59 ms count. The old "leave the window mode" note here quoted a 3.7x
        // regression from a `Seq Scan on paymentallocation` plan that V132's
        // `ix_paymentallocation_tenantschoolcampus_invoice` removed; re-measured after the index,
        // separate is the faster shape.
        ReportSpec(v, "rpt-fee-collection", "Report: Fee Collection - first page (V133: separate)",
            "FEE_COLLECTION",
            @"SELECT paymentid, receiptnumber, transactiondate, paymentmethodname, invoicenumber,
                     studentname, admissionnumber, gradename, classroomname, academicyearname,
                     feecategoryname, billedamount, discountamount, taxamount, netcollected
                FROM vw_fee_collection
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM payment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            countMode: "separate"),
        ReportSpec(v, "rpt-fee-collection-count", "Report: Fee Collection - count (V133: separate)",
            "FEE_COLLECTION", "SELECT COUNT(*) FROM vw_fee_collection",
            "SELECT COUNT(*) FROM payment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            countMode: "separate", scalar: true),

        ReportSpec(v, "rpt-collections-by-method", "Report: Collections by Method - first page",
            "COLLECTIONS_BY_METHOD",
            @"SELECT paymentmethodname, paymentid, netcollected, latefeeamount, transactiondate,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_fee_collection
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM payment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-student-promotion", "Report: Student Promotion - first page",
            "STUDENT_PROMOTION",
            @"SELECT enrollmentid, studentname, admissionnumber, fromgradename, fromclassroomname,
                     togradename, toclassroomname, promoted, promotiondate, toacademicyearname,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_student_promotion
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM studentenrollment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ReportSpec(v, "rpt-student-transfer", "Report: Student Transfer - first page",
            "STUDENT_TRANSFER",
            @"SELECT transferid, studentname, admissionnumber, transfertype, effectivedate,
                     fromcampusname, tocampusname, fromclassroomname, toclassroomname, reason,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_student_transfer
               LIMIT @__limit OFFSET @__offset",
            // A transfer is a RARE event: a campus of ~2,000 students genuinely holds tens of
            // them, not thousands. The floor reflects the table's real size rather than a wish
            // for it to be bigger.
            "SELECT COUNT(*) FROM enrollmenttransferhistory WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            minVolume: 30),

        ReportSpec(v, "rpt-subject-performance", "Report: Subject Performance - first page",
            "SUBJECT_PERFORMANCE",
            @"SELECT resultid, subjectname, gradename, classroomname, termname, studentname,
                     obtainedmarks, totalmarks, percentage, grade, ispass, isfail,
                     COUNT(*) OVER() AS __total_rows
                FROM vw_student_subject_results
               LIMIT @__limit OFFSET @__offset",
            "SELECT COUNT(*) FROM studentsubjectresult WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        // =====================================================================
        // THE HR MODULE.
        //
        // ⚠️ These three tables held ZERO rows until `data-volume`'s HrModuleSeeder existed,
        // so every one of these specs reported SKIP - the catalogue was honest about a module
        // it could not measure at all. They are transcribed from the repositories verbatim;
        // if a repository's SQL changes, it changes here (the rule at the top of this file).
        // =====================================================================
        new QuerySpec
        {
            Key = "hr-employee-page",
            Title = "HR employee grid - first page",
            Source = "EmployeeRepository.GetAll (searchQuery half)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employee WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "employee",
            IndexName = "ix_employee_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the WHERE filters on exactly those three columns. Already deployed - the HR tables follow the scope convention (unlike attendance/invoices/payment, see V132).",
            Sql = @"
                SELECT e.*, dept.Name AS DepartmentName, desig.Name AS DesignationName
                FROM employee e
                LEFT JOIN department dept ON e.DepartmentId = dept.Id
                LEFT JOIN designation desig ON e.DesignationId = desig.Id
                WHERE e.TenantId = @tenantId AND e.SchoolId = @schoolId AND e.CampusId = @campusId
                ORDER BY e.FirstName
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-employee-count",
            Title = "HR employee grid - total count",
            Source = "EmployeeRepository.GetAll (countQuery half)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM employee WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "employee",
            IndexName = "ix_employee_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the count is a pure scope filter, so the composite turns it into an index-only walk of the matching rows.",
            Sql = @"
                SELECT COUNT(*)
                FROM employee e
                LEFT JOIN department dept ON e.DepartmentId = dept.Id
                LEFT JOIN designation desig ON e.DesignationId = desig.Id
                WHERE e.TenantId = @tenantId AND e.SchoolId = @schoolId AND e.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // The HR attendance GRID - `hr.employee.attendance.html`'s daily view, which the page
        // loads from `employeeAttendance/date?date=`. This is the module's real volume query:
        // one row per employee per day.
        //
        // ⚠️ `desig` joins on `desig.Id`, NOT `dept.Id`. The repository joined it on the
        // DEPARTMENT's id until it was fixed, which resolved a designation whose id merely
        // equalled the employee's department id - measured on ayra_perf, an employee whose real
        // designation is "Teacher" was rendered as "Librarian", on 3,780 of one campus's rows.
        // The shape below is the FIXED one, and J5 phase 10 asserts the name it returns.
        // =====================================================================
        new QuerySpec
        {
            Key = "hr-attendance-daily",
            Title = "HR attendance - one day for the campus",
            Source = "EmployeeAttendanceRepository.GetAttendanceByDate",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM employeeattendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "employeeattendance",
            IndexName = "ix_employeeattendance_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a campus-wide day read is a scope filter plus a date, and the scope composite is what the planner can walk. Already deployed.",
            Sql = @"
                SELECT ea.*, emp.FirstName || ' ' || emp.LastName AS EmployeeName, emp.EmployeeCode,
                       dept.Name AS DepartmentName, desig.Name AS DesignationName,
                       ast.Name AS StatusName, ast.Code AS StatusCode
                FROM employeeattendance ea
                LEFT JOIN employee emp ON ea.EmployeeId = emp.Id
                LEFT JOIN department dept ON emp.DepartmentId = dept.Id
                LEFT JOIN designation desig ON emp.DesignationId = desig.Id
                LEFT JOIN attendancestatus ast ON ea.AttendanceStatusId = ast.Id
                WHERE ea.AttendanceDate = @attendanceDate
                  AND ea.TenantId = @tenantId AND ea.SchoolId = @schoolId AND ea.CampusId = @campusId
                ORDER BY emp.FirstName",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["attendanceDate"] = v.AttendanceDate.Date,
            },
        },

        new QuerySpec
        {
            Key = "hr-payroll-periods-page",
            Title = "Payroll periods grid - first page",
            Source = "PayrollRepository.GetPayrollPeriodsPage",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM payrollperiod WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 3,
            IndexTable = "payrollperiod",
            IndexName = "ix_payrollperiod_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the grid is a pure scope filter ordered by StartDate DESC. Already deployed.",
            Sql = @"
                SELECT *
                FROM payrollperiod
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                ORDER BY StartDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // The payroll RECORDS grid - `hr.payroll.process.html`'s per-employee rows for one
        // period, i.e. the module's real volume table (one row per employee per period).
        //
        // ⚠️ It is NOT scope-filtered: the repository takes the `payrollPeriodId` the PAGE
        // supplies (`PayrollRepository.GetPayrollByPeriodPage`), and the scope arrives through
        // the period, which is itself campus-scoped. That is why this table needs no
        // `(tenantid, schoolid, campusid)` index - measured, no query of it filters by scope
        // alone. The spec is handed `v.PayrollPeriodId` so it points at the same period a user
        // would have opened.
        //
        // Note the page and the count are TWO separate round trips here (`ExecuteScalarAsync`
        // then `QueryAsync`), not one `QueryMultipleAsync` - so the user waits for both and each
        // gets its own spec.
        // =====================================================================
        new QuerySpec
        {
            Key = "hr-payroll-records-page",
            Title = "Payroll records grid - first page of a period",
            Source = "PayrollRepository.GetPayrollByPeriodPage",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeepayroll WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND payrollperiodid = @payrollPeriodId",
            MinVolume = 50,
            IndexTable = "employeepayroll",
            IndexName = "ix_ep_payrollperiodid",
            IndexColumns = "payrollperiodid",
            IndexRationale = "driven by the period, NOT the scope triple: the grid pages one period's records and the period is already campus-scoped. `ix_ep_payrollperiodid` and `ix_employeepayroll_periodstatus` (payrollperiodid, status) both serve it. A scope composite would be unused - see the README's employeepayroll note.",
            Sql = @"
                SELECT ep.*, pp.Name AS PeriodName,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName, emp.EmployeeCode,
                       dept.Name AS DepartmentName,
                       appr.FirstName || ' ' || appr.LastName AS ApprovedByName
                FROM employeepayroll ep
                LEFT JOIN payrollperiod pp ON ep.PayrollPeriodId = pp.Id
                LEFT JOIN employee emp ON ep.EmployeeId = emp.Id
                LEFT JOIN department dept ON emp.DepartmentId = dept.Id
                LEFT JOIN employee appr ON ep.ApprovedBy = appr.UserId
                WHERE ep.PayrollPeriodId = @payrollPeriodId
                ORDER BY emp.FirstName
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["payrollPeriodId"] = v.PayrollPeriodId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-payroll-records-count",
            Title = "Payroll records grid - total count of a period",
            Source = "PayrollRepository.GetPayrollByPeriodPage (count half, its own round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM employeepayroll WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND payrollperiodid = @payrollPeriodId",
            MinVolume = 50,
            IndexTable = "employeepayroll",
            IndexName = "ix_ep_payrollperiodid",
            IndexColumns = "payrollperiodid",
            IndexRationale = "a per-period count with the same two left joins as the page (the repository repeats them so DataTables search columns resolve in both) - served index-only by `uix_ep_periodemployee` or `ix_ep_payrollperiodid`.",
            Sql = @"
                SELECT COUNT(*)
                FROM employeepayroll ep
                LEFT JOIN employee emp ON ep.EmployeeId = emp.Id
                LEFT JOIN department dept ON emp.DepartmentId = dept.Id
                WHERE ep.PayrollPeriodId = @payrollPeriodId",
            Params = new Dictionary<string, object>
            {
                ["payrollPeriodId"] = v.PayrollPeriodId,
            },
        },

        // The payroll HISTORY grid (`PayrollRepository.GetPayrollHistoryPage`, filters
        // EmployeeId + the scope triple) is deliberately NOT specced: it is inherently ~6 rows on
        // this dataset (1,560 employees share 9,360 records) and a real school holds one record
        // per employee per PERIOD, so it is a tens-of-rows query forever.
        //
        // ⚠️ THIS USED TO CLAIM THE SAME RULE "keeps the leave / loan / overtime / contract tables
        // out of the catalogue". IT DOES NOT ANY MORE - they are specced (see THE HR WORKFLOW DESKS
        // block, which states why the census reversed that decision: those tables held ZERO rows, so
        // their SKIPs could not be told apart from an unmeasured grid). The rule that remains is
        // narrower and about SHAPE, not row count: a query whose driving table holds tens of rows
        // AND whose verdict cannot be the finding is still not worth a spec. This one is not a
        // candidate to add on the strength of the HR block.

        // =====================================================================
        // INVENTORY / PROCUREMENT.
        //
        // These tables held ZERO rows before `data-volume`'s InventoryModuleSeeder, so the
        // module was unmeasurable rather than fast. Each spec is transcribed from the
        // repository that serves the screen that owns it.
        // =====================================================================
        new QuerySpec
        {
            Key = "inv-item-page",
            Title = "Inventory item grid - first page",
            Source = "InvItemRepository.GetAll(page, ...)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM invitem WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE",
            MinVolume = 50,
            IndexTable = "invitem",
            IndexName = "ix_invitem_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the grid filters the scope triple plus IsActive and orders by Name. `invitem` carries idx_invitem_tenant_school (tenant+school only) and a partial unique (tenant, school, code) WHERE isactive - NEITHER can serve a campus-scoped read, so this is the one inventory table missing the convention its siblings follow.",
            Sql = @"
                SELECT i.Id, i.TenantId, i.SchoolId, i.CampusId, i.CategoryId, i.Code, i.Name,
                       i.Description, i.ReorderLevel, i.MinLevel, i.MaxLevel, i.IsActive,
                       c.Name AS CategoryName
                FROM InvItem i
                LEFT JOIN InvCategory c ON c.Id = i.CategoryId
                WHERE i.TenantId = @tenantId AND i.SchoolId = @schoolId AND i.CampusId = @campusId
                  AND i.IsActive = TRUE
                ORDER BY i.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-item-count",
            Title = "Inventory item grid - total count",
            Source = "InvItemRepository.GetAll(page, ...) (count half, run as its own round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM invitem WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE",
            MinVolume = 50,
            IndexTable = "invitem",
            IndexName = "ix_invitem_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a pure scope count - the composite turns it into an index-only walk of the campus's own rows.",
            Sql = @"
                SELECT COUNT(*)
                FROM InvItem i
                LEFT JOIN InvCategory c ON c.Id = i.CategoryId
                WHERE i.TenantId = @tenantId AND i.SchoolId = @schoolId AND i.CampusId = @campusId
                  AND i.IsActive = TRUE",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // The inventory MOVEMENT ledger - the module's real volume: one row per stock
        // movement, N per item, forever. `idx_invmovement_campus_date` already leads with
        // the scope triple, which is why this one is expected to pass - it is here so the
        // catalogue can show the contrast with a table that does NOT carry the convention.
        // =====================================================================
        new QuerySpec
        {
            Key = "inv-movement-page",
            Title = "Inventory movement ledger - first page",
            Source = "InvMovementRepository.GetAll(page, ...)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invmovement WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 500,
            IndexTable = "invmovement",
            IndexName = "idx_invmovement_campus_date",
            IndexColumns = "tenantid, schoolid, campusid, movementdate",
            IndexRationale = "the grid is a scope filter ordered by MovementDate DESC - the index is already exactly that, in that order. Already deployed.",
            Sql = @"
                SELECT m.Id, m.TenantId, m.SchoolId, m.CampusId, m.InvItemId,
                       i.Name AS ItemName, i.Code AS ItemCode,
                       m.MovementType, m.Quantity, m.UnitCost, m.ReferenceType, m.ReferenceId,
                       m.Notes, m.MovementDate
                FROM InvMovement m
                JOIN InvItem i ON i.Id = m.InvItemId
                WHERE m.TenantId = @tenantId AND m.SchoolId = @schoolId AND m.CampusId = @campusId
                ORDER BY m.MovementDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-stock-page",
            Title = "Inventory stock grid - first page",
            Source = "InvStockRepository.GetAll(page, ...)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM invstock WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "invstock",
            IndexName = "ix_invstock_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid, invitemid",
            IndexRationale = "the grid joins InvItem and filters the receipt's own scope. The deployed unique (tenantid, schoolid, campusid, invitemid) answers both the filter and the join.",
            Sql = @"
                SELECT s.Id, s.TenantId, s.SchoolId, s.CampusId, s.InvItemId,
                       i.Name AS ItemName, i.Code AS ItemCode, c.Name AS CategoryName,
                       s.QtyOnHand, s.QtyReserved
                FROM InvStock s
                JOIN InvItem i ON i.Id = s.InvItemId
                LEFT JOIN InvCategory c ON c.Id = i.CategoryId
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId
                  AND i.IsActive = TRUE
                ORDER BY i.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // LIBRARY.
        // =====================================================================
        new QuerySpec
        {
            Key = "lib-book-page",
            Title = "Library book catalogue - first page",
            Source = "LibraryBookRepository.SearchBooksPaged",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM librarybook WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 50,
            IndexTable = "librarybook",
            IndexName = "ix_librarybook_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the catalogue grid filters the scope triple plus IsActive. Note the data query ALSO carries a correlated `string_agg` sub-select over librarybookauthor - that per-row work is what the budget is really about, and no index on librarybook removes it.",
            Sql = @"
                SELECT b.*, lc.Name AS CategoryName, lp.Name AS PublisherName,
                    CASE WHEN ags.Id IS NULL THEN NULL
                         ELSE cg.Name || ' - ' || COALESCE(NULLIF(cs.CustomName, ''), s2.Name, s.Name)
                    END AS AcademicGradeSubjectName,
                    (SELECT string_agg(la.Name, ', ') FROM LibraryBookAuthor lba
                     JOIN LibraryAuthor la ON la.Id = lba.AuthorId WHERE lba.BookId = b.Id) AS AuthorNames
                FROM LibraryBook b
                LEFT JOIN LibraryCategory lc ON lc.Id = b.CategoryId
                LEFT JOIN LibraryPublisher lp ON lp.Id = b.PublisherId
                LEFT JOIN AcademicGradeSubject ags ON ags.Id = b.AcademicGradeSubjectId
                LEFT JOIN AcademicGrade ag ON ag.Id = ags.AcademicGradeId
                LEFT JOIN curriculumgrade cg ON cg.id = ag.curriculumgradeid
                LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                LEFT JOIN Subject s ON s.Id = cgs.SubjectId
                LEFT JOIN Subject s2 ON s2.Id = cs.SubjectId
                WHERE b.TenantId = @tenantId AND b.SchoolId = @schoolId AND b.CampusId = @campusId
                  AND b.IsActive = true
                ORDER BY b.Title
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "lib-issue-page",
            Title = "Library circulation - books on loan",
            Source = "LibraryIssueRepository.GetActiveIssuesPaged",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryissue WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND status = 'Issued'",
            MinVolume = 50,
            IndexTable = "libraryissue",
            IndexName = "ix_libraryissue_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, status",
            IndexRationale = "the circulation grid is the scope triple PLUS `Status = 'Issued'` - a fixed, always-present fourth predicate. The grid also filters the RETURNED list by the same triple plus Status = 'Returned', so the extended index serves both halves of the desk. Deployed index is (tenantid, schoolid, campusid) only.",
            Sql = @"
                SELECT li.*, li.FineAmount AS Fine, GREATEST(0, (CURRENT_DATE - li.DueDate::date)) AS DaysOverdue,
                    lb.Title AS BookTitle, lbc.Barcode,
                    lm.MemberNumber AS MemberCode,
                    COALESCE(
                        CASE WHEN lm.MemberType = 'Student' THEN st.Name END,
                        CASE WHEN lm.MemberType = 'Employee' THEN emp.FirstName || ' ' || emp.LastName END,
                        ''
                    ) AS MemberName
                FROM LibraryIssue li
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = li.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                LEFT JOIN LibraryMember lm ON lm.Id = li.MemberId
                LEFT JOIN Student st ON st.Id = lm.StudentId
                LEFT JOIN Employee emp ON emp.Id = lm.EmployeeId
                WHERE li.TenantId = @tenantId AND li.SchoolId = @schoolId AND li.CampusId = @campusId
                  AND li.Status = 'Issued'
                ORDER BY li.IssuedDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // THE VOLUME TABLES THE FIRST CENSUS MISSED.
        //
        // Each of these carries MORE THAN 1,000 ROWS across the dataset while being named
        // by NO spec and by NO view - so the first catalogue was blind to them. Two of them
        // (libraryfine 4,300, libraryinventoryaudit 1,200) are paged scope lists on a
        // library desk; the third (invitemcost 1,400) is loaded IN FULL by a client-side
        // grid, which is why its spec has no LIMIT - that IS the query the screen runs.
        //
        // Unlike `invitem`, all three ALREADY carry the scope convention, so these specs are
        // expected to PASS: their value is as a regression guard, and as the record that the
        // convention was checked rather than assumed. A FAIL here would mean the index is
        // present but unused, which is a different (and more interesting) finding.
        // =====================================================================
        new QuerySpec
        {
            Key = "lib-fine-page",
            Title = "Library fines desk - first page",
            Source = "LibraryFineRepository.GetAllPaged (data half of the count+data round trip)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryfine WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "libraryfine",
            IndexName = "ix_libraryfine_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the fines grid is a scope filter over SIX left joins (member, student, employee, issue, copy, book), ordered by lf.Id DESC. The scope composite is DEPLOYED - this spec exists so the desk is measured at all, not to propose it.",
            Sql = @"
                SELECT lf.*, (lf.Amount - lf.PaidAmount) AS OutstandingAmount,
                    lm.MemberNumber, lm.MemberNumber AS MemberCode,
                    COALESCE(
                        CASE WHEN lm.MemberType = 'Student' THEN st.Name END,
                        CASE WHEN lm.MemberType = 'Employee' THEN emp.FirstName || ' ' || emp.LastName END,
                        ''
                    ) AS MemberName,
                    li.BookCopyId, lbc.Barcode, lb.Title AS BookTitle
                FROM LibraryFine lf
                LEFT JOIN LibraryMember lm ON lm.Id = lf.MemberId
                LEFT JOIN Student st ON st.Id = lm.StudentId
                LEFT JOIN Employee emp ON emp.Id = lm.EmployeeId
                LEFT JOIN LibraryIssue li ON li.Id = lf.IssueId
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = li.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                WHERE lf.TenantId = @tenantId AND lf.SchoolId = @schoolId AND lf.CampusId = @campusId
                ORDER BY lf.Id DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The count half runs in the SAME round trip as the page above (QueryMultipleAsync),
        // so the user waits for both. It carries the same six joins on purpose - the
        // repository does that so DataTables search columns resolve in both queries.
        new QuerySpec
        {
            Key = "lib-fine-count",
            Title = "Library fines desk - total count",
            Source = "LibraryFineRepository.GetAllPaged (count half, measured on its own)",
            P95BudgetMs = 250,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM libraryfine WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "libraryfine",
            IndexName = "ix_libraryfine_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope count still paying for six left joins - if the planner drives from the scope composite this stays an index walk; the joins are the cost to watch.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryFine lf
                LEFT JOIN LibraryMember lm ON lm.Id = lf.MemberId
                LEFT JOIN Student st ON st.Id = lm.StudentId
                LEFT JOIN Employee emp ON emp.Id = lm.EmployeeId
                LEFT JOIN LibraryIssue li ON li.Id = lf.IssueId
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = li.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                WHERE lf.TenantId = @tenantId AND lf.SchoolId = @schoolId AND lf.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // `inv.itemCost.html` is a CLIENT-side grid: the repository returns EVERY cost row
        // for the campus and DataTables pages it in the browser. So there is deliberately no
        // LIMIT here - the absence of one is the thing being measured, and it is why a
        // campus whose cost rows grow is a page-load problem rather than a paging one.
        new QuerySpec
        {
            Key = "inv-itemcost-page",
            Title = "Inventory item cost list - all rows (client-side grid)",
            Source = "InvItemCostRepository.GetAll (unpaged by design - the grid pages in the browser)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invitemcost WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "invitemcost",
            IndexName = "ux_invitemcost_campus_location_item",
            IndexColumns = "tenantid, schoolid, campusid, locationid, invitemid",
            IndexRationale = "the scope triple is the leading edge of the deployed unique (tenant, school, campus, location, item), so a campus-scoped read is index-served. NO LIMIT: the whole campus list is the payload.",
            Sql = @"
                SELECT ic.Id, ic.TenantId, ic.SchoolId, ic.CampusId, ic.LocationId,
                       l.Name AS LocationName, ic.InvItemId, i.Code AS ItemCode, i.Name AS ItemName,
                       ic.CostMethod, ic.CurrentCost, ic.LastCost, ic.UpdatedOn
                FROM InvItemCost ic
                JOIN InvItem i ON i.Id = ic.InvItemId
                JOIN InvLocation l ON l.Id = ic.LocationId
                WHERE ic.TenantId = @tenantId AND ic.SchoolId = @schoolId AND ic.CampusId = @campusId
                ORDER BY i.Name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // INVENTORY, PART TWO - the five desk grids the inventory tier's first pass left
        // out (only item / stock / movement / cost were specced).
        //
        // ⚠️ THE WHOLE INVENTORY FAMILY IS THE `invitem` CLASS: these tables carry a
        // SINGLE-COLUMN `idx_*_campus` (campusid) and no `(tenant, school, campus)` composite,
        // so a campus-scoped read is a campus-index scan plus a heap recheck. That is
        // deliberate and recorded, not fixed: a campus owns tens of these rows, and per
        // AGENTS's own rule a missing index is not a defect until a query would use it - the
        // composite must be PROVEN by a failing probe (`--advise` on a failing spec) before
        // it is written, and none of these is close to the budget.
        // =====================================================================
        new QuerySpec
        {
            Key = "inv-asset-page",
            Title = "Inventory asset register - first page",
            Source = "InvAssetRepository.GetAll(page, tenantId, schoolId, campusId, status)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invasset WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invasset",
            IndexName = "idx_invasset_campus",
            IndexColumns = "campusid",
            IndexRationale =
                "⚠️ `status` is NOT reproduced - the screen sends it only when a status tab is chosen, and the " +
                "default load is the unfiltered scope read. The table carries `idx_invasset_campus` (campusid ONLY, " +
                "no tenant/school prefix) and the LEFT JOIN to InvItem is a PK lookup. The sort is " +
                "`a.CreatedOn DESC` with no supporting index - at a campus's tens of assets that is a small sort, " +
                "and this is the `invitem` case AGENTS records: a scope composite is a candidate, not a defect, " +
                "until a probe proves it.",
            Sql = @"
                SELECT a.Id, a.TenantId, a.SchoolId, a.CampusId, a.InvItemId,
                       i.Name AS ItemName, a.AssetCode, a.Name, a.Description, a.PurchaseDate,
                       a.PurchaseCost, a.UsefulLifeYears, a.SalvageValue, a.DepreciationMethod,
                       a.Status, a.Location, a.SerialNumber, a.WarrantyExpiry
                FROM InvAsset a
                LEFT JOIN InvItem i ON i.Id = a.InvItemId
                WHERE a.TenantId = @tenantId AND a.SchoolId = @schoolId AND a.CampusId = @campusId
                ORDER BY a.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-grn-page",
            Title = "Goods-receipt grid - first page",
            Source = "InvGRNRepository.GetAll(page, tenantId, schoolId, campusId)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invgrn WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invgrn",
            IndexName = "idx_invgrn_campus",
            IndexColumns = "campusid",
            IndexRationale =
                "Scope read over an INNER JOIN to InvPurchaseOrder (PK lookup for the PO number), ordered by " +
                "`g.ReceivedDate DESC`. ⚠️ The INNER JOIN is load-bearing: a receipt whose PO does not resolve is " +
                "INVISIBLE while `invgrn` looks populated - the same \"a row no query can reach\" shape as the " +
                "attendance enrolment column. `idx_invgrn_campus` is campusid-only (no scope composite); a campus " +
                "owns tens of receipts, so that is the constant, not a defect.",
            Sql = @"
                SELECT g.Id, g.TenantId, g.SchoolId, g.CampusId, g.POId,
                       po.PONumber, g.GRNNumber, g.ReceivedDate, g.ReceivedBy, g.Status, g.Notes
                FROM InvGRN g
                JOIN InvPurchaseOrder po ON po.Id = g.POId
                WHERE g.TenantId = @tenantId AND g.SchoolId = @schoolId AND g.CampusId = @campusId
                ORDER BY g.ReceivedDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-po-page",
            Title = "Purchase-order grid - first page",
            Source = "InvPurchaseOrderRepository.GetAll(page, tenantId, schoolId, campusId, status)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invpurchaseorder WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invpurchaseorder",
            IndexName = "idx_invpo_campus",
            IndexColumns = "campusid",
            IndexRationale =
                "Scope read over an INNER JOIN to InvSupplier (PK lookup for the supplier name), ordered by " +
                "`po.CreatedOn DESC`. ⚠️ `status` is not reproduced (the screen sends it only when a status tab is " +
                "chosen); when present it is an extra equality, which `idx_invstockrequest_status`-style indexes do " +
                "not exist for here. `idx_invpo_campus` is campusid-only; a campus owns tens of POs.",
            Sql = @"
                SELECT po.Id, po.TenantId, po.SchoolId, po.CampusId, po.SupplierId,
                       s.Name AS SupplierName, po.PONumber, po.Status, po.OrderDate,
                       po.ExpectedDate, po.TotalAmount, po.Notes, po.ApprovedBy, po.ApprovedDate, po.WorkflowId
                FROM InvPurchaseOrder po
                JOIN InvSupplier s ON s.Id = po.SupplierId
                WHERE po.TenantId = @tenantId AND po.SchoolId = @schoolId AND po.CampusId = @campusId
                ORDER BY po.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-stock-adjustment-page",
            Title = "Stock-adjustment grid - first page",
            Source = "InvStockAdjustmentRepository.GetAll(page, tenantId, schoolId, campusId, status)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invstockadjustment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invstockadjustment",
            IndexName = "idx_invstockadjustment_campus",
            IndexColumns = "campusid",
            IndexRationale =
                "A campus-scoped read with ONE CORRELATED SUB-SELECT PER ROW: " +
                "`(SELECT COUNT(*) FROM InvStockAdjustmentLine l WHERE l.AdjustmentId = a.Id) AS LineCount`. " +
                "⚠️ THAT SUB-SELECT IS THE DOMINANT COST, not the scope filter - it runs once per returned row and " +
                "needs an index on `invstockadjustmentline.adjustmentid` to stay cheap. Ordered by " +
                "`a.AdjustmentDate DESC`; `idx_invstockadjustment_campus` is campusid-only.",
            Sql = @"
                SELECT a.Id, a.TenantId, a.SchoolId, a.CampusId, a.AdjustmentNumber,
                       a.Reason, a.Status, a.AdjustmentDate, a.ApprovedBy, a.ApprovedOn, a.Notes,
                       (SELECT COUNT(*) FROM InvStockAdjustmentLine l WHERE l.AdjustmentId = a.Id) AS LineCount
                FROM InvStockAdjustment a
                WHERE a.TenantId = @tenantId AND a.SchoolId = @schoolId AND a.CampusId = @campusId
                ORDER BY a.AdjustmentDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-stock-request-page",
            Title = "Stock-request grid - first page",
            Source = "InvStockRequestRepository.GetAll(page, tenantId, schoolId, campusId, status)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM invstockrequest WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invstockrequest",
            IndexName = "idx_invstockrequest_campus",
            IndexColumns = "campusid",
            IndexRationale =
                "Scope read over an INNER JOIN to InvItem (PK lookup) and a LEFT JOIN to users for the requester " +
                "(`COALESCE(u.FirstName || ' ' || u.LastName, u.Email)`), ordered by `sr.CreatedOn DESC`. ⚠️ The " +
                "users join is LEFT on purpose - a request whose requester row is gone must still list, or a " +
                "deactivated account would hide its own history. `idx_invstockrequest_campus` is campusid-only; " +
                "`idx_invstockrequest_status` serves the optional status tab, which is not reproduced here.",
            Sql = @"
                SELECT sr.Id, sr.TenantId, sr.SchoolId, sr.CampusId, sr.InvItemId,
                       i.Name AS ItemName, i.Code AS ItemCode,
                       sr.QtyRequested, sr.QtyApproved, sr.Purpose, sr.Status,
                       sr.RequestNumber, sr.RequestedByUserId,
                       COALESCE(u.FirstName || ' ' || u.LastName, u.Email) AS RequestedByName,
                       sr.WorkflowId, sr.CreatedOn
                FROM InvStockRequest sr
                JOIN InvItem i ON i.Id = sr.InvItemId
                LEFT JOIN users u ON u.Id = sr.RequestedByUserId
                WHERE sr.TenantId = @tenantId AND sr.SchoolId = @schoolId AND sr.CampusId = @campusId
                ORDER BY sr.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The physical-inventory audit grid. One row per scanned copy per session, so it
        // grows with every stocktake - the desk that grows without anybody creating a page.
        new QuerySpec
        {
            Key = "lib-inventory-audit-page",
            Title = "Library physical inventory - first page",
            Source = "LibraryInventoryAuditRepository.GetAllPaged (data half)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryinventoryaudit WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "libraryinventoryaudit",
            IndexName = "ix_libraryinventoryaudit_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "scope filter over three left joins (copy, book, users), ordered by lia.AuditDate DESC. The scope composite is DEPLOYED; a session-scoped read has its own ix_libraryinventoryaudit_session.",
            Sql = @"
                SELECT lia.*, lb.Title AS BookTitle,
                    COALESCE(u.FirstName || ' ' || u.LastName, '') AS AuditedByName, lbc.Barcode
                FROM LibraryInventoryAudit lia
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = lia.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                LEFT JOIN Users u ON u.Id = lia.AuditedBy
                WHERE lia.TenantId = @tenantId AND lia.SchoolId = @schoolId AND lia.CampusId = @campusId
                ORDER BY lia.AuditDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "lib-inventory-audit-count",
            Title = "Library physical inventory - total count",
            Source = "LibraryInventoryAuditRepository.GetAllPaged (count half, same round trip)",
            P95BudgetMs = 250,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM libraryinventoryaudit WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 50,
            IndexTable = "libraryinventoryaudit",
            IndexName = "ix_libraryinventoryaudit_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope count over three left joins - the cheapest thing that can still be slow if the planner picks a nested loop on Users.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryInventoryAudit lia
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = lia.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                LEFT JOIN Users u ON u.Id = lia.AuditedBy
                WHERE lia.TenantId = @tenantId AND lia.SchoolId = @schoolId AND lia.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // TRANSPORT.
        // =====================================================================
        new QuerySpec
        {
            Key = "tr-assignment-page",
            Title = "Transport rider list",
            Source = "TransportStudentAssignmentRepository.GetAll(page, ...)",
            P95BudgetMs = 400,
            VolumeSql = "SELECT COUNT(*) FROM transportstudentassignment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 100,
            IndexTable = "transportstudentassignment",
            IndexName = "ix_transportstudentassignment_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the page's rider list is a scope filter over SIX left joins. The repository used to take a `page` argument and IGNORE it (no LIMIT, so it loaded the WHOLE campus list on every visit) - the shape below is the FIXED one, paged and ordered exactly as the repository now builds it. `transportstudentassignment` also carries (tenantid, campusid) with NO schoolid, while its siblings transportroute/transportvehicle carry the full triple.",
            Sql = @"
                SELECT sa.*, s.Name AS StudentName, r.RouteName, v.VehicleNumber,
                       ps.StopName AS PickupStopName, ds.StopName AS DropStopName
                FROM TransportStudentAssignment sa
                LEFT JOIN StudentEnrollment se ON sa.StudentEnrollmentId = se.Id
                LEFT JOIN Student s ON se.StudentId = s.Id
                LEFT JOIN TransportRoute r ON sa.RouteId = r.Id
                LEFT JOIN TransportVehicle v ON sa.VehicleId = v.Id
                LEFT JOIN TransportRouteStop ps ON sa.PickupStopId = ps.Id
                LEFT JOIN TransportRouteStop ds ON sa.DropStopId = ds.Id
                WHERE sa.TenantId = @tenantId AND sa.SchoolId = @schoolId AND sa.CampusId = @campusId
                ORDER BY sa.Id DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The count half of the same endpoint, which is its own round trip now that the repository
        // pages properly (`ExecuteScalarAsync` then `QueryAsync`). It carries the same six joins ON
        // PURPOSE - `page.WhereCondition` is built from the columns the CLIENT marked searchable, so a
        // clause naming `s.Name` or `r.RouteName` has to resolve in the count too.
        new QuerySpec
        {
            Key = "tr-assignment-count",
            Title = "Transport rider list - total count",
            Source = "TransportStudentAssignmentRepository.GetAll(page, ...) (count half)",
            P95BudgetMs = 400,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM transportstudentassignment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 100,
            IndexTable = "transportstudentassignment",
            IndexName = "ix_transportstudentassignment_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope count still paying for six left joins. The joins are the cost to watch here - if the planner drives from the scope it stays cheap; a nested loop over Student is what turns it into a per-row walk.",
            Sql = @"
                SELECT COUNT(*)
                FROM TransportStudentAssignment sa
                LEFT JOIN StudentEnrollment se ON sa.StudentEnrollmentId = se.Id
                LEFT JOIN Student s ON se.StudentId = s.Id
                LEFT JOIN TransportRoute r ON sa.RouteId = r.Id
                LEFT JOIN TransportVehicle v ON sa.VehicleId = v.Id
                LEFT JOIN TransportRouteStop ps ON sa.PickupStopId = ps.Id
                LEFT JOIN TransportRouteStop ds ON sa.DropStopId = ds.Id
                WHERE sa.TenantId = @tenantId AND sa.SchoolId = @schoolId AND sa.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "tr-route-page",
            Title = "Transport route grid - first page",
            Source = "TransportRouteRepository.GetAll(page, ...)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM transportroute WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 5,
            IndexTable = "transportroute",
            IndexName = "ix_transportroute_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a small table (a campus owns a dozen routes), included as the module's low-volume control: it shows the catalogue distinguishes \"fast because indexed\" from \"fast because tiny\".",
            Sql = @"
                SELECT *
                FROM TransportRoute
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                ORDER BY RouteName
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // ACCOUNTING.
        // =====================================================================
        new QuerySpec
        {
            Key = "acct-journal-page",
            Title = "Accounting journal grid - first page",
            Source = "JournalEntryRepository.GetAll(page, ...)",
            P95BudgetMs = 400,
            VolumeSql = "SELECT COUNT(*) FROM journalentry WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 200,
            IndexTable = "journalentry",
            IndexName = "idx_journalentry_scope_entrydate",
            IndexColumns = "tenantid, schoolid, campusid, entrydate DESC",
            IndexRationale = "the grid is a scope filter ordered `EntryDate DESC, Id DESC`. idx_journalentry_scope is (tenant, school, campus) only, so the sort is a separate step; extending it with EntryDate DESC lets the index supply the order. ⚠️ FOUR correlated sub-selects over journalentryline run PER ROW (TotalDebit/TotalCredit/IsReversed/IsReversal) - that is the dominant cost and no index on journalentry removes it.",
            Sql = @"
                SELECT je.*, fp.Name AS FiscalPeriodName,
                    COALESCE((SELECT SUM(jel.Debit) FROM JournalEntryLine jel WHERE jel.JournalEntryId = je.Id), 0) AS TotalDebit,
                    COALESCE((SELECT SUM(jel.Credit) FROM JournalEntryLine jel WHERE jel.JournalEntryId = je.Id), 0) AS TotalCredit,
                    EXISTS(SELECT 1 FROM JournalEntryLine rl
                           WHERE rl.JournalEntryId = je.Id AND rl.ReversedJournalEntryId IS NOT NULL) AS IsReversed,
                    (SELECT rev.JournalNumber FROM JournalEntryLine rl
                       JOIN JournalEntry rev ON rev.Id = rl.ReversedJournalEntryId
                      WHERE rl.JournalEntryId = je.Id AND rl.ReversedJournalEntryId IS NOT NULL
                      ORDER BY rev.Id LIMIT 1) AS ReversalJournalNumber,
                    EXISTS(SELECT 1 FROM JournalEntryLine ml
                           WHERE ml.ReversedJournalEntryId = je.Id) AS IsReversal
                FROM JournalEntry je
                LEFT JOIN FiscalPeriod fp ON je.FiscalPeriodId = fp.Id
                WHERE je.TenantId = @tenantId AND je.SchoolId = @schoolId AND je.CampusId = @campusId
                ORDER BY je.EntryDate DESC, je.Id DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "acct-posting-page",
            Title = "Accounting posting monitor - first page",
            Source = "FinancialPostingRepository.GetAll(page, ...)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM financialposting WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 100,
            IndexTable = "financialposting",
            IndexName = "idx_financialposting_scope_requestedon",
            IndexColumns = "tenantid, schoolid, campusid, requestedon DESC",
            IndexRationale = "the monitor is a scope filter ordered `RequestedOn DESC`, and it projects `Payload ->> 'description'` off the JSONB. idx_financialposting_scope is the triple only, so the sort is a separate step.",
            Sql = @"
                SELECT fp.*, je.JournalNumber,
                       COALESCE(fp.Payload ->> 'description', '') AS Description
                FROM FinancialPosting fp
                LEFT JOIN JournalEntry je ON fp.JournalEntryId = je.Id
                WHERE fp.TenantId = @tenantId AND fp.SchoolId = @schoolId AND fp.CampusId = @campusId
                ORDER BY fp.RequestedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // ACCOUNTING, PART TWO - the four master-data grids and the posting-rule table
        // the accounting tier's first pass left out (only the journal, the posting monitor
        // and the batch trail were specced). All five tables already hold rows on the
        // measured campus, so each spec measures something rather than SKIPping.
        // =====================================================================
        new QuerySpec
        {
            Key = "acct-fiscal-year-page",
            Title = "Accounting fiscal-year grid - first page",
            Source = "FiscalYearRepository.GetAll(page, tenantId, schoolId, campusId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM fiscalyear WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "fiscalyear",
            IndexName = "idx_fiscalyear_scope",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "A campus owns a HANDFUL of fiscal years (five rows here), so the cost is the constant, not the " +
                "plan - what the spec proves is that the paged read and its ORDER BY behave. `idx_fiscalyear_scope` " +
                "is (tenant, school, campus) only, so `ORDER BY fy.StartDate DESC` is a sort step the index cannot " +
                "supply; at five rows that is free, and extending the index for it would be the `employeepayroll` " +
                "mistake - an index no query would use. **The correlated sub-select IS load-bearing**: " +
                "`(SELECT COUNT(*) FROM FiscalPeriod fp WHERE fp.FiscalYearId = fy.Id) AS PeriodCount` runs once per " +
                "returned row and is served by `idx_fiscalperiod_year`.",
            Sql = @"
                SELECT fy.*,
                       (SELECT COUNT(*) FROM FiscalPeriod fp WHERE fp.FiscalYearId = fy.Id) AS PeriodCount
                FROM FiscalYear fy
                WHERE fy.TenantId = @tenantId AND fy.SchoolId = @schoolId AND fy.CampusId = @campusId
                ORDER BY fy.StartDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "acct-fiscal-period-page",
            Title = "Accounting fiscal-period grid - first page",
            Source = "FiscalPeriodRepository.GetAll(page, tenantId, schoolId, campusId, fiscalYearId = null)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM fiscalperiod WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "fiscalperiod",
            IndexName = "idx_fiscalperiod_scope",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "⚠️ REPRODUCED WITHOUT `fiscalYearId`, because that is what the screen sends on a normal load: the " +
                "repository appends `AND fp.FiscalYearId = @FiscalYearId` ONLY when the query string carries one, so " +
                "the unfiltered read is the measured shape. A year holds twelve months, so a campus has tens of " +
                "fiscal periods - the cost is the constant. `idx_fiscalperiod_scope` is the triple only, so " +
                "`ORDER BY fp.StartDate DESC` is a sort step (`idx_fiscalperiod_dates` carries the scope plus the " +
                "dates but is not the one the planner picks here); the join is a PK lookup on a tiny FiscalYear.",
            Sql = @"
                SELECT fp.*, fy.Name AS FiscalYearName
                FROM FiscalPeriod fp
                LEFT JOIN FiscalYear fy ON fp.FiscalYearId = fy.Id
                WHERE fp.TenantId = @tenantId AND fp.SchoolId = @schoolId AND fp.CampusId = @campusId
                ORDER BY fp.StartDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "acct-account-group-page",
            Title = "Accounting chart of accounts - account-group grid, first page",
            Source = "AccountGroupRepository.GetAll(page, tenantId, schoolId, campusId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM accountgroup WHERE (tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) OR (tenantid = 0 AND schoolid = 0 AND campusid = 0)",
            MinVolume = 1,
            IndexTable = "accountgroup",
            IndexName = "idx_accountgroup_scope",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "⚠️ THE SCOPE PREDICATE IS A UNION WITH THE 0/0/0 SEEDS AND THE PARENTHESES ARE LOAD-BEARING. The " +
                "repository parenthesises `((scoped) OR (tenantid=0 AND ...)) AND IsActive = TRUE`; the " +
                "un-parenthesised earlier form let the OR escape the appended search clause (the comment in the " +
                "repository records that defect). Indexes: `idx_accountgroup_scope` serves the scoped half and " +
                "`accountgroup_pkey` the seed half. **The correlated `(SELECT COUNT(*) FROM Account a WHERE " +
                "a.AccountGroupId = ag.Id) AS AccountCount` runs PER ROW** and is served by `idx_account_group` - " +
                "that sub-select, not the scope filter, is the dominant cost on a campus with a real chart.",
            Sql = @"
                SELECT ag.*,
                       pg.Name AS ParentGroupName,
                       (SELECT COUNT(*) FROM Account a WHERE a.AccountGroupId = ag.Id) AS AccountCount
                FROM AccountGroup ag
                LEFT JOIN AccountGroup pg ON ag.ParentGroupId = pg.Id
                WHERE ((ag.TenantId = @tenantId AND ag.SchoolId = @schoolId AND ag.CampusId = @campusId)
                       OR (ag.TenantId = 0 AND ag.SchoolId = 0 AND ag.CampusId = 0))
                  AND ag.IsActive = TRUE
                ORDER BY ag.Code, ag.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "acct-account-page",
            Title = "Accounting chart of accounts - account grid, first page",
            Source = "AccountRepository.GetAll(page, tenantId, schoolId, campusId, accountType = null)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM account WHERE ((tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) OR (tenantid = 0 AND schoolid = 0 AND campusid = 0)) AND isactive = TRUE",
            MinVolume = 1,
            IndexTable = "account",
            IndexName = "idx_account_scope_code",
            IndexColumns = "tenantid, schoolid, campusid, code",
            IndexRationale =
                "⚠️ `idx_account_scope_code` (tenant, school, campus, code) IS DEPLOYED AND IS THE RIGHT SHAPE for " +
                "this read: after the CASE expression ranks the campus's own rows first, the sort is `a.Code, a.Name`, " +
                "so the index supplies the order instead of a sort step. Two LEFT JOINs (AccountGroup by PK, self " +
                "join to the parent account by PK) are PK lookups on tiny tables. `accountType` is intentionally NOT " +
                "reproduced: the screen sends it only when a type filter is chosen, and a spec should measure the " +
                "default load. The 0/0/0 seeds and the CASE-prefixed ORDER BY are reproduced VERBATIM.",
            Sql = @"
                SELECT a.*,
                       ag.Name AS AccountGroupName,
                       pa.Name AS ParentAccountName
                FROM Account a
                LEFT JOIN AccountGroup ag ON a.AccountGroupId = ag.Id
                LEFT JOIN Account pa ON a.ParentAccountId = pa.Id
                WHERE ((a.TenantId = @tenantId AND a.SchoolId = @schoolId AND a.CampusId = @campusId)
                       OR (a.TenantId = 0 AND a.SchoolId = 0 AND a.CampusId = 0))
                  AND a.IsActive = TRUE
                ORDER BY CASE WHEN a.TenantId = @tenantId AND a.SchoolId = @schoolId AND a.CampusId = @campusId THEN 0 ELSE 1 END,
                         a.Code, a.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "acct-posting-rule-page",
            Title = "Accounting posting-rule grid - first page",
            Source = "PostingRuleRepository.GetAll(page, tenantId, schoolId, campusId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM postingrule WHERE (tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId) OR (tenantid = 0 AND schoolid = 0 AND campusid = 0)",
            MinVolume = 1,
            IndexTable = "postingrule",
            IndexName = "idx_postingrule_lookup",
            IndexColumns = "sourcetype, postingaction",
            IndexRationale =
                "⚠️ REPRODUCED VERBATIM, INCLUDING THE UN-PARENTHESISED OR. The repository builds " +
                "`(TenantId = @t AND SchoolId = @s AND CampusId = @c OR (TenantId = 0 AND SchoolId = 0 AND CampusId = 0))`; " +
                "because AND binds tighter than OR that is `(scoped) OR (all-zero)`, which is the intended meaning - " +
                "but the shape is recorded here so a future reader does not \"fix\" it into something else. The table " +
                "carries NO scope index and none is owed (a campus has tens of posting rules); what it DOES carry is " +
                "the PARTIAL `idx_postingrule_lookup (SourceType, PostingAction) WHERE IsActive`, which is exactly " +
                "the `ORDER BY SourceType, PostingAction` this read asks for.",
            Sql = @"
                SELECT *
                FROM PostingRule
                WHERE (TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                       OR (TenantId = 0 AND SchoolId = 0 AND CampusId = 0))
                ORDER BY SourceType, PostingAction
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // THE COVERAGE CENSUS, PART TWO - the grids the first census left out.
        //
        // ⚠️ WHY *THESE* GRIDS AND NOT THE OTHER ~70 UNCOVERED ONES. The tool's own rule is
        // that a spec over an empty table reports SKIP honestly, so writing specs first and
        // seeding later produces skips rather than measurements. The census below was taken
        // from `pg_stat_user_tables` on `ayra_perf` and then RE-COUNTED PER CAMPUS, because
        // that is the scope the tool measures - and the per-campus number is where a module's
        // tables stop being a performance question:
        //
        //   table                     total   per campus   verdict
        //   libraryissue             10,200    2,000      specced (returns + overdue + popular)
        //   invoices                144,402    8,000      specced (the late-fee engine's read)
        //   employeeattendance       65,520    5,040      specced (grid + monthly summary)
        //   student                 879,514    2,000      specced (the enrolment picker)
        //   librarybookcopy           4,200      600      specced (grid + the dashboard's N+1)
        //   librarymember             2,600      500      specced
        //   libraryreservation          600      100      specced
        //   transportroutestop          672       96      NOT specced - read PER ROUTE (~8)
        //   invasset / invpurchaseorder /          15-60  NOT specced - tens of rows
        //   invgrn / invstockrequest / libraryacquisition / transport* /
        //   invstockadjustment / libraryreadinglist
        //
        // ⚠️ THE TENS-OF-ROWS TABLES ARE DELIBERATELY LEFT OUT, by the same rule that keeps the
        // leave / loan / overtime / contract tables out of the catalogue: a campus owns 15-60
        // assets, purchase orders, GRNs, suppliers, drivers or routes, so a spec over one times
        // noise while reporting OK - and the busiest of them would hold ~60 rows.
        //
        // ⚠️ AND WHAT IS NOT HERE BECAUSE IT NEEDS *DATA*, NOT A SPEC: exams / results
        // (`studentsubjectresult`, `studentassessment`, `studentexam` are 0 rows), leave
        // (`employeeleaverequest` 0) and transfers (`enrollmenttransferhistory` 1). Those are the
        // six `rpt-*` SKIPs; the unlock there is a SEEDER (as it was for HR), not more specs.
        //
        // ⚠️ ON `LIMIT 50 OFFSET 0` WRITTEN AS LITERALS. Every repository below interpolates
        // `page.PageSize` / `page.OffSet` into its SQL, so PostgreSQL receives literals and can
        // plan against them - the same fidelity rule the enrolment specs above follow. (Five
        // older library/HR/inventory specs use `@pageSize` against repositories that interpolate;
        // that is a recorded inconsistency, not a licence to extend it.)
        // =====================================================================

        new QuerySpec
        {
            Key = "lib-copy-page",
            Title = "Library copies grid - first page",
            Source = "LibraryBookCopyRepository.GetByFilterPaged (data half of the count+data round trip)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM librarybookcopy WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 100,
            IndexTable = "librarybookcopy",
            IndexName = "ix_librarybookcopy_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the copy list is a scope filter ordered `lbc.Id DESC` over one left join to the book. The scope composite is DEPLOYED - this spec exists because the screen was never measured, not to propose it.",
            Sql = @"
                SELECT lbc.*, lb.Title AS BookTitle
                FROM LibraryBookCopy lbc
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                WHERE lbc.TenantId = @tenantId AND lbc.SchoolId = @schoolId AND lbc.CampusId = @campusId
                ORDER BY lbc.Id DESC
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The count half runs in the SAME round trip as the page (`QueryMultipleAsync`), so the
        // user waits for both - which is why `libraryfine` and `libraryinventoryaudit` each got
        // two specs. Same rule here.
        new QuerySpec
        {
            Key = "lib-copy-count",
            Title = "Library copies grid - total count",
            Source = "LibraryBookCopyRepository.GetByFilterPaged (count half, same round trip)",
            P95BudgetMs = 250,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM librarybookcopy WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 100,
            IndexTable = "librarybookcopy",
            IndexName = "ix_librarybookcopy_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope count that still carries the book join, deliberately: the repository repeats the join so DataTables search columns (lb.Title) resolve in both queries.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryBookCopy lbc
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                WHERE lbc.TenantId = @tenantId AND lbc.SchoolId = @schoolId AND lbc.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The members desk. ⚠️ THE TWO CORRELATED SUB-SELECTS ARE PER ROW (a member's active loans
        // and their unpaid fines), so 50 rows means 100 extra index lookups - the same shape as the
        // journal grid's four, and the reason this is not "just a scope filter over three joins".
        new QuerySpec
        {
            Key = "lib-member-page",
            Title = "Library members desk - first page",
            Source = "LibraryMemberRepository.GetAllPaged (data half of the count+data round trip)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM librarymember WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 100,
            IndexTable = "librarymember",
            IndexName = "ix_librarymember_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "scope triple plus IsActive, ordered by MemberNumber, over three left joins and TWO correlated counts per row (active issues, unpaid fines). The scope composite is deployed; the per-row sub-selects are the cost to watch, and no index on librarymember removes them.",
            Sql = @"
                SELECT lm.*, mt.Name AS TierName,
                    COALESCE(
                        CASE WHEN lm.MemberType = 'Student' THEN st.Name END,
                        CASE WHEN lm.MemberType = 'Employee' THEN emp.FirstName || ' ' || emp.LastName END,
                        ''
                    ) AS MemberName,
                    lm.MemberNumber AS MemberCode,
                    CASE WHEN lm.Blacklisted THEN 'Blacklisted' ELSE 'Active' END AS Status,
                    (SELECT COUNT(*) FROM LibraryIssue li WHERE li.MemberId = lm.Id AND li.Status = 'Issued') AS ActiveIssues,
                    (SELECT COUNT(*) FROM LibraryFine lf WHERE lf.MemberId = lm.Id AND lf.PaymentStatus != 'Paid') AS OutstandingFines
                FROM LibraryMember lm
                LEFT JOIN LibraryMembershipTier mt ON mt.Id = lm.TierId
                LEFT JOIN Student st ON st.Id = lm.StudentId
                LEFT JOIN Employee emp ON emp.Id = lm.EmployeeId
                WHERE lm.TenantId = @tenantId AND lm.SchoolId = @schoolId AND lm.CampusId = @campusId AND lm.IsActive = true
                ORDER BY lm.MemberNumber
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "lib-member-count",
            Title = "Library members desk - total count",
            Source = "LibraryMemberRepository.GetAllPaged (count half, same round trip)",
            P95BudgetMs = 250,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM librarymember WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 100,
            IndexTable = "librarymember",
            IndexName = "ix_librarymember_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a pure scope count with NO joins (unlike the copies/fines counts) - so it should be index-only, and a FAIL here would mean the planner is not using the composite at all.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryMember lm
                WHERE lm.TenantId = @tenantId AND lm.SchoolId = @schoolId AND lm.CampusId = @campusId AND lm.IsActive = true",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The returns desk - `library.circulation.html`'s second grid. It is the mirror of the
        // active-issues grid but on the LARGER half of the table: `libraryissue` holds one row per
        // loan forever, so a campus's 2,000 issues are 300 out on loan and 1,700 already returned.
        // The grid the catalogue measured (`lib-issue-page`) is the smaller, faster half.
        new QuerySpec
        {
            Key = "lib-returns-page",
            Title = "Library recent returns desk - first page",
            Source = "LibraryIssueRepository.GetRecentReturnsPaged (data half of the count+data round trip)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryissue WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND status = 'Returned'",
            MinVolume = 200,
            IndexTable = "libraryissue",
            IndexName = "ix_libraryissue_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, status",
            IndexRationale = "scope triple PLUS `Status = 'Returned'`, ordered by ReturnDate DESC, over four left joins. Same candidate index as the active-issues spec: one (tenant, school, campus, status) index serves both halves of the desk.",
            Sql = @"
                SELECT li.*, li.FineAmount AS Fine,
                    lb.Title AS BookTitle, lbc.Barcode,
                    lm.MemberNumber AS MemberCode,
                    COALESCE(
                        CASE WHEN lm.MemberType = 'Student' THEN st.Name END,
                        CASE WHEN lm.MemberType = 'Employee' THEN emp.FirstName || ' ' || emp.LastName END,
                        ''
                    ) AS MemberName
                FROM LibraryIssue li
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = li.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                LEFT JOIN LibraryMember lm ON lm.Id = li.MemberId
                LEFT JOIN Student st ON st.Id = lm.StudentId
                LEFT JOIN Employee emp ON emp.Id = lm.EmployeeId
                WHERE li.TenantId = @tenantId AND li.SchoolId = @schoolId AND li.CampusId = @campusId AND li.Status = 'Returned'
                ORDER BY li.ReturnDate DESC
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "lib-returns-count",
            Title = "Library recent returns desk - total count",
            Source = "LibraryIssueRepository.GetRecentReturnsPaged (count half, same round trip)",
            P95BudgetMs = 250,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM libraryissue WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND status = 'Returned'",
            MinVolume = 200,
            IndexTable = "libraryissue",
            IndexName = "ix_libraryissue_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, status",
            IndexRationale = "a scope+status count still carrying the copy and book joins the repository repeats on purpose.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryIssue li
                LEFT JOIN LibraryBookCopy lbc ON lbc.Id = li.BookCopyId
                LEFT JOIN LibraryBook lb ON lb.Id = lbc.BookId
                WHERE li.TenantId = @tenantId AND li.SchoolId = @schoolId AND li.CampusId = @campusId AND li.Status = 'Returned'",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ⚠️ THE OVERDUE DESK IS UNBOUNDED AND THAT IS THE FINDING, not an oversight: the repository
        // selects EVERY overdue loan for the campus with no LIMIT. A campus with 252 of them is fine;
        // one that never chases its loans materialises the whole set on every visit. Measured here so
        // the shape is on the record even while the number is small.
        new QuerySpec
        {
            Key = "lib-overdue-list",
            Title = "Library overdue desk - EVERY overdue loan for the campus (unbounded)",
            Source = "LibraryIssueRepository.GetOverdueIssues",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryissue WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND status = 'Issued' AND duedate < CURRENT_DATE",
            MinVolume = 50,
            IndexTable = "libraryissue",
            IndexName = "ix_libraryissue_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, status",
            IndexRationale = "scope + `Status = 'Issued'` + a DueDate range, ordered by DueDate. The scope+status composite narrows the set; `ix_libraryissue_duedate` can serve the ORDER BY but carries no scope. Note NO LIMIT: an index cannot fix an unbounded read - only a LIMIT can.",
            Sql = @"
                SELECT * FROM LibraryIssue
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                AND Status = 'Issued' AND DueDate < CURRENT_DATE
                ORDER BY DueDate",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "lib-overdue-count",
            Title = "Library dashboard - overdue loan count",
            Source = "LibraryIssueRepository.OverdueCount",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM libraryissue WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND status = 'Issued' AND duedate < CURRENT_DATE",
            MinVolume = 50,
            IndexTable = "libraryissue",
            IndexName = "ix_libraryissue_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, status",
            IndexRationale = "the same predicate as the overdue list, as a count - it runs on the dashboard's own tile strip, so it must stay cheap.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryIssue
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                AND Status = 'Issued' AND DueDate < CURRENT_DATE",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The dashboard's "popular books" widget: a GROUP BY over the campus's WHOLE issue history
        // with a correlated `string_agg` over the book's authors per output row. It is the one
        // library query that reads every loan to produce ten rows - and it runs on page load.
        new QuerySpec
        {
            Key = "lib-popular-books",
            Title = "Library dashboard - popular books (aggregate over the whole issue history)",
            Source = "LibraryIssueRepository.GetPopularBooks",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryissue WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 200,
            IndexTable = "libraryissue",
            IndexName = "ix_libraryissue_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, status",
            IndexRationale = "a GROUP BY over every issue of the campus joined to its copy and book, with a correlated author aggregation per GROUP. The scope composite prunes the input; the aggregate itself is the cost and is why the widget is measured separately from the grid.",
            Sql = @"
                SELECT lb.Id AS BookId, lb.Title,
                    COALESCE((SELECT string_agg(la.Name, ', ') FROM LibraryBookAuthor lba
                        JOIN LibraryAuthor la ON la.Id = lba.AuthorId WHERE lba.BookId = lb.Id), '') AS AuthorNames,
                    COUNT(li.Id) AS IssueCount
                FROM LibraryIssue li
                INNER JOIN LibraryBookCopy lbc ON lbc.Id = li.BookCopyId
                INNER JOIN LibraryBook lb ON lb.Id = lbc.BookId
                WHERE li.TenantId = @tenantId AND li.SchoolId = @schoolId AND li.CampusId = @campusId
                GROUP BY lb.Id, lb.Title
                ORDER BY IssueCount DESC
                LIMIT 10",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "lib-reservation-page",
            Title = "Library reservations desk - first page",
            Source = "LibraryReservationRepository.GetAllPaged (data half of the count+data round trip)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM libraryreservation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 25,
            IndexTable = "libraryreservation",
            IndexName = "ix_libraryreservation_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "scope filter ordered by ReservationDate DESC over three left joins (book, member, student or employee for the name). The scope composite is deployed; a campus holds tens of reservations, so this is a low-volume control for the module.",
            Sql = @"
                SELECT lr.*, lb.Title AS BookTitle, lm.MemberNumber,
                    COALESCE(
                        CASE WHEN lm.MemberType = 'Student' THEN st.Name END,
                        CASE WHEN lm.MemberType = 'Employee' THEN emp.FirstName || ' ' || emp.LastName END,
                        ''
                    ) AS MemberName
                FROM LibraryReservation lr
                LEFT JOIN LibraryBook lb ON lb.Id = lr.BookId
                LEFT JOIN LibraryMember lm ON lm.Id = lr.MemberId
                LEFT JOIN Student st ON st.Id = lm.StudentId
                LEFT JOIN Employee emp ON emp.Id = lm.EmployeeId
                WHERE lr.TenantId = @tenantId AND lr.SchoolId = @schoolId AND lr.CampusId = @campusId
                ORDER BY lr.ReservationDate DESC
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // ⚠️ A PER-BOOK QUERY THAT RUNS TWICE PER BOOK - the library dashboard's N+1.
        //
        // `LibraryReportController.GetDashboard` (and `GetCounts`) loads EVERY active book for the
        // campus and then loops:
        //
        //     foreach (var b in books) {
        //         totalCopies     += await copyRepo.TotalCountByBook(b.Id);
        //         availableCopies += await copyRepo.CountAvailableByBook(b.Id);
        //     }
        //
        // so one page load is `1 + 2 x books` round trips. A spec measures ONE statement, so what is
        // measured here is the per-book statement - and the finding is the MULTIPLIER, which is why
        // the number that matters is this query's cost times 2 x books (a 350-book campus = 700
        // round trips before the dashboard's own nine aggregate queries run).
        //
        // It is bounded on `BookId` (ix_librarybookcopy_book) and is NOT scope-filtered - the app
        // relies on the book already being one of the campus's. The volume gate is the campus's copy
        // count, which is the table the loop walks.
        // =====================================================================
        new QuerySpec
        {
            Key = "lib-dashboard-copy-counts",
            Title = "Library dashboard - copies of ONE book (run 2x per book on every page load)",
            Source = "LibraryBookCopyRepository.TotalCountByBook / CountAvailableByBook",
            P95BudgetMs = 50,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM librarybookcopy WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 100,
            IndexTable = "librarybookcopy",
            IndexName = "ix_librarybookcopy_book",
            IndexColumns = "bookid",
            IndexRationale = "`BookId = @BookId` is exactly ix_librarybookcopy_book. It needs no scope composite - the per-book lookup is already served - so the dashboard's cost is NOT an index problem. It is the N+1: the fix is two `GROUP BY BookId` aggregate queries for the whole campus, not an index.",
            Sql = @"
                SELECT COUNT(*) FROM LibraryBookCopy WHERE BookId = @libBookId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["libBookId"] = v.LibraryBookId,
            },
        },

        // =====================================================================
        // HR - the ATTENDANCE desk's own grid, which is not the daily register.
        //
        // `hr.employee.attendance.html` has two reads: `GetAttendanceByDate` (specced as
        // `hr-attendance-daily`) and this paged list. The list is the campus's whole attendance
        // history filtered by scope, so it is the one that grows: `employeeattendance` is the
        // module's volume table and every working day adds a row per employee.
        // =====================================================================
        new QuerySpec
        {
            Key = "hr-attendance-grid-page",
            Title = "HR attendance grid - first page",
            Source = "EmployeeAttendanceRepository.GetAll(page, ...) (data half of the count+data round trip)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM employeeattendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 200,
            IndexTable = "employeeattendance",
            IndexName = "ix_empatt_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "scope filter over four left joins (employee, department, designation, status), ordered `AttendanceDate DESC, emp.FirstName`. Two scope composites are already deployed (idx_employeeattendance_tenant and ix_empatt_tenantschoolcampus); the ORDER BY is what an index does not carry, so the sort is a separate step.",
            Sql = @"
                SELECT ea.*,
                     emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                     emp.EmployeeCode,
                     dept.Name AS DepartmentName,
                     desig.Name AS DesignationName,
                     ast.Name AS StatusName,
                     ast.Code AS StatusCode
                     FROM employeeattendance ea
                     LEFT JOIN Employee emp ON ea.EmployeeId = emp.Id
                     LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                     LEFT JOIN Designation desig ON emp.DesignationId = desig.Id
                     LEFT JOIN AttendanceStatus ast ON ea.AttendanceStatusId = ast.Id
                     WHERE ea.TenantId = @tenantId AND ea.SchoolId = @schoolId AND ea.CampusId = @campusId
                     ORDER BY ea.AttendanceDate DESC, emp.FirstName
                     LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hr-attendance-grid-count",
            Title = "HR attendance grid - total count",
            Source = "EmployeeAttendanceRepository.GetAll(page, ...) (count half, same round trip)",
            P95BudgetMs = 250,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM employeeattendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 200,
            IndexTable = "employeeattendance",
            IndexName = "ix_empatt_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope count over three left joins - the count the repository repeats the joins for so DataTables search columns resolve in both halves.",
            Sql = @"
                SELECT COUNT(*)
                                FROM employeeattendance ea
                                LEFT JOIN Employee emp ON ea.EmployeeId = emp.Id
                                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                                LEFT JOIN AttendanceStatus ast ON ea.AttendanceStatusId = ast.Id
                                WHERE ea.TenantId = @tenantId AND ea.SchoolId = @schoolId AND ea.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The monthly summary - ONE query behind the month view. It is anchored on the EMPLOYEE
        // roster (LEFT JOIN, so an employee with no records still appears) and aggregates the month's
        // attendance with seven FILTER clauses. The year/month come from the resolved attendance
        // date, so the spec measures the month the data is actually in rather than a hardcoded one.
        new QuerySpec
        {
            Key = "hr-attendance-monthly",
            Title = "HR attendance monthly summary - one month, whole campus",
            Source = "EmployeeAttendanceRepository.GetMonthlySummary",
            P95BudgetMs = 300,
            VolumeSql = "SELECT COUNT(*) FROM employeeattendance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND EXTRACT(YEAR FROM attendancedate) = @year AND EXTRACT(MONTH FROM attendancedate) = @month",
            MinVolume = 200,
            IndexTable = "employeeattendance",
            IndexName = "ix_empatt_employeedate",
            IndexColumns = "employeeid, attendancedate",
            IndexRationale = "the join is `ea.EmployeeId = emp.Id AND EXTRACT(YEAR/MONTH FROM ea.AttendanceDate)` - a FUNCTION on the fact column, so no plain index on attendancedate can serve the month filter and `ix_empatt_employeedate` (employeeid, attendancedate) is what the per-employee probe uses. The roster side is indexed by the scope composite. The seven COUNT(...) FILTER arms are the cost: each one re-reads the month's rows.",
            Sql = @"
                SELECT emp.Id AS EmployeeId,
                           emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                           emp.EmployeeCode,
                           dept.Name AS DepartmentName,
                           COUNT(ea.Id) FILTER (WHERE ast.CountsAsPresent = true AND ast.CountsAsAbsent = false) AS Present,
                           COUNT(ea.Id) FILTER (WHERE ast.CountsAsAbsent = true AND ast.CountsAsPresent = false) AS Absent,
                           COUNT(ea.Id) FILTER (WHERE ea.LateMinutes > 0) AS Late,
                           COUNT(ea.Id) FILTER (WHERE ast.Code = 'ON_LEAVE') AS OnLeave,
                           COUNT(ea.Id) FILTER (WHERE ast.Code = 'HALF_DAY') AS HalfDay,
                           COALESCE(SUM(ea.WorkingHours), 0) AS TotalWorkingHours,
                           COALESCE(SUM(ea.OvertimeMinutes), 0) AS TotalOvertimeMinutes,
                           COALESCE(SUM(ea.LateMinutes), 0) AS TotalLateMinutes
                           FROM employee emp
                           LEFT JOIN department dept ON emp.DepartmentId = dept.Id
                           LEFT JOIN employeeattendance ea ON ea.EmployeeId = emp.Id
                               AND EXTRACT(YEAR FROM ea.AttendanceDate) = @year
                               AND EXTRACT(MONTH FROM ea.AttendanceDate) = @month
                               AND ea.TenantId = @tenantId AND ea.SchoolId = @schoolId AND ea.CampusId = @campusId
                           LEFT JOIN attendancestatus ast ON ea.AttendanceStatusId = ast.Id
                           WHERE emp.TenantId = @tenantId AND emp.SchoolId = @schoolId AND emp.CampusId = @campusId
                           AND emp.IsActive = true
                           GROUP BY emp.Id, emp.FirstName, emp.LastName, emp.EmployeeCode, dept.Name ORDER BY emp.FirstName",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["year"] = v.AttendanceDate.Year,
                ["month"] = v.AttendanceDate.Month,
            },
        },

        // =====================================================================
        // THE ENROLMENT PICKER - a student query the catalogue did not have.
        //
        // The student grid is specced (`student-list-page`), but the picker that ADDS a student to
        // something is a different query: it excludes anyone already enrolled in the target year with
        // a live status - an anti-join against `studentenrollment`.
        // =====================================================================
        new QuerySpec
        {
            Key = "student-available-page",
            Title = "Enrolment picker - first page of students NOT already enrolled",
            Source = "StudentRepository.GetAllAvailableStudents (searchQuery half of the count+data round trip)",
            P95BudgetMs = 300,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 500,
            IndexTable = "student",
            // ⚠️ FIXED IN THE APPLICATION (Sept 2026) - the query SHAPE was the lever, not the index.
            //
            // The shape that failed was `NOT EXISTS (...) ORDER BY s.Name LIMIT 50`: a per-row probe
            // under an ordered LIMIT cannot short-circuit, so on a campus where every student is
            // already enrolled for the target year it paid that probe for the whole campus and then
            // returned ZERO rows. MEASURED on `ayra_perf` (campus 15, 2,000 students inside 879,514,
            // all already enrolled for year 14):
            //
            //   old `NOT EXISTS`  : 336 ms p95, plan exec 640 ms, 10,060 buffers, `Limit on ? (~0 rows,
            //                       640 ms)` - the heaviest node was the LIMIT itself.
            //   new anti-join     : **1.9 ms p95** (5.5 ms on a later run), plan exec **0.9-5.3 ms**,
            //                       **106 buffers**, 0 rows.
            //
            // `V136` had already added `ix_student_tenantschoolcampus_isactive_name`, and it is what
            // bounded the OLD shape's name-ordered walk to the campus - but the probe still landed ON
            // the 300 ms budget line (289 ms `PROVEN` one run, 306 ms `NOT proven` the next), which is
            // why the index was a mitigation and not the fix. The anti-join replaces 2,000 per-row
            // `studentenrollment` probes with ONE hash of the campus's live enrollments:
            //
            //   Limit -> Sort(s.name) -> Hash Left Join (s.id = se.studentid)
            //                              Filter: (se.id IS NULL), Rows Removed by Filter: 2000
            //                              -> Index Scan using ix_student_tenantschoolcampus_isactive
            //                              -> Hash (studentenrollment)
            //
            // ⚠️ THE PICKER NO LONGER NEEDS THE SORT COLUMN: with `..._isactive_name` dropped inside a
            // rolled-back transaction the same anti-join still runs in ~1.0 ms on V130's
            // `ix_student_tenantschoolcampus_isactive`, because the read is the campus's own 2,000 rows
            // (the sort is a 2,000-row quicksort, not a 879,514-row walk). V136 is left in place - it
            // is what the student grid's own top-N read uses - but this spec declares the index the
            // plan actually takes.
            //
            // ⚠️ EQUIVALENCE IS NOT AN ASSUMPTION. Both shapes were run side by side, in a rolled-back
            // transaction with half of campus 15's year-14 enrollments marked non-live AND one student
            // given a SECOND live enrollment in the same year (the duplicate-row risk): 1,001 rows
            // available / 999 excluded, `EXCEPT` in both directions 0, and the anti-join's row count
            // equal to its DISTINCT count. The year-less branch (`academicYearId = 0`) was checked the
            // same way. A student matching several live enrollments is still excluded exactly once.
            IndexName = "ix_student_tenantschoolcampus_isactive",
            IndexColumns = "tenantid, schoolid, campusid, isactive",
            IndexRationale = "the shape fix is the fix; this index only has to bound the read to the campus's own students so the anti-join hashes a campus-sized set. Adding `name` does not help any more - the anti-join reads the whole campus and sorts it (2,000 rows), it never walks the name order.",
            // ⚠️ REPRODUCES THE ANTI-JOIN SHAPE THE REPOSITORY NOW SENDS. A spec carrying the old
            // correlated `NOT EXISTS` would go on measuring a query the application no longer issues
            // - and would go on reporting it as a failure it had already been fixed out of.
            Sql = @"
                SELECT s.Id,s.AdmissionNumber,t.Nic,t.Address,u.Email,s.Name,s.Age,s.Gender,s.Dob,s.ParentId,
                                u.Password,u.Password AS ConfirmPassword,u.FirstName,u.LastName,u.Mobile,u.Id AS UserId,
                                s.CampusId,s.TenantId,s.SchoolId,s.Photo
                                FROM Student s
                                INNER JOIN Parent t ON s.ParentId = t.Id
                                INNER JOIN Users u ON t.UserId = u.Id
                                LEFT JOIN StudentEnrollment se ON se.StudentId = s.Id
                                    AND se.TenantId = s.TenantId AND se.SchoolId = s.SchoolId AND se.CampusId = s.CampusId
                                    AND se.AcademicYearId = @academicYearId
                                    AND se.StudentStatus NOT IN (4, 5, 6, 7)
                                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId AND s.IsActive = true
                                AND se.Id IS NULL
                                ORDER BY s.Name
                                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // The COUNT half of the SAME round trip - `GetAllAvailableStudents` issues
        // `countQuery;\n searchQuery` through one `QueryMultipleAsync`, so the user waits for both and
        // measuring the page alone would report half the cost (the `lib-fine-count` rule). It took the
        // same anti-join change as the page: a `COUNT(*)` over a correlated `NOT EXISTS` pays the same
        // per-row probe, just without a LIMIT to hide behind.
        new QuerySpec
        {
            Key = "student-available-count",
            Title = "Enrolment picker - the count round trip (same QueryMultipleAsync)",
            Source = "StudentRepository.GetAllAvailableStudents (countQuery half of the count+data round trip)",
            P95BudgetMs = 300,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM student WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            MinVolume = 500,
            IndexTable = "student",
            IndexName = "ix_student_tenantschoolcampus_isactive_name",
            IndexColumns = "tenantid, schoolid, campusid, isactive, name",
            IndexRationale = "a scope count that still has to visit the campus's students and anti-join the campus's live enrollments; the composite serves the scope read and the anti-join is a hash/anti join over the campus-sized set, not a scan of the whole table.",
            Sql = @"
                SELECT COUNT(*)
                FROM Student s
                INNER JOIN Parent t ON s.ParentId = t.Id
                INNER JOIN Users u ON t.UserId = u.Id
                LEFT JOIN StudentEnrollment se ON se.StudentId = s.Id
                    AND se.TenantId = s.TenantId AND se.SchoolId = s.SchoolId AND se.CampusId = s.CampusId
                    AND se.AcademicYearId = @academicYearId
                    AND se.StudentStatus NOT IN (4, 5, 6, 7)
                WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId AND s.IsActive = true
                AND se.Id IS NULL",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // =====================================================================
        // THE LATE-FEE ENGINE'S DRIVING READ.
        //
        // Every other spec over `invoices` is per-student (the outstanding-balance guard) or the
        // unbounded `GetAll`. This one is the campus-wide sweep the late-fee run performs before it
        // assesses anything: the whole academic year's open invoices, minus the ones that already
        // carry a late-fee charge.
        //
        // ⚠️ IT RETURNS ZERO ROWS ON THIS DATASET AND STILL MEASURES SOMETHING. The perf fixture's
        // `invoices.balanceamount` is NULL on all 8,000 rows, so `balanceamount > 0` prunes
        // everything - the query is cheap here for a fixture reason, not an indexing one. The
        // volume gate is therefore the table's own scope rows (8,000), which is what the statement
        // has to traverse, and the spec is kept because it is the shape a production campus runs.
        // =====================================================================
        new QuerySpec
        {
            Key = "invoice-overdue-eligible",
            Title = "Late-fee engine - every open invoice of the academic year",
            Source = "InvoiceRepository.GetOverdueEligibleInvoices",
            P95BudgetMs = 400,
            VolumeSql = "SELECT COUNT(*) FROM invoices WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND academicyearid = @academicYearId",
            MinVolume = 1000,
            IndexTable = "invoices",
            IndexName = "ix_invoices_tenantschoolcampus_date",
            IndexColumns = "tenantid, schoolid, campusid, invoicedate DESC",
            IndexRationale = "scope triple + AcademicYearId + a status set + `balanceamount > 0`, ordered by `duedate, id`, with a NOT EXISTS against latefeecharges. `ix_invoices_tenantschoolcampus_date` (V132) serves the scope+year read; `ix_invoices_academicyearid` alone cannot narrow a campus. The ORDER BY is not in any index and the sweep is unbounded - it returns every eligible invoice.",
            Sql = @"
                SELECT * FROM invoices i
                          WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId
                            AND i.academicyearid = @academicYearId
                            AND i.status IN (1, 2, 4)
                            AND i.balanceamount > 0
                            AND NOT EXISTS (
                                SELECT 1 FROM latefeecharges lc WHERE lc.invoiceid = i.id
                            )
                          ORDER BY i.duedate, i.id",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // =====================================================================
        // THE GENERIC SCOPE GRIDS - `GenericRepository.GetAllAsync(page, predicate)`.
        //
        // This is the app's commonest list shape and it is GENERATED, not hand-written: ONE
        // `QueryMultipleAsync` carrying `SELECT COUNT(0) FROM <table> Where <predicate>` AND
        // `SELECT * FROM <table> Where <predicate> [ORDER BY <page.OrderBy>] [LIMIT n OFFSET m]`.
        //
        // ⚠️ ONE SPEC PER GRID, MEASURING THE PAGE HALF. On a SINGLE-TABLE scope grid the count
        // is an index-only count over the same predicate, so it cannot be the finding - which is
        // why the join-heavy library grids DID get a separate count spec (there the count re-runs
        // six joins) and these do not. If one of these ever shows its count as the cost, split it,
        // and say so here.
        //
        // ⚠️ `ORDER BY` IS PRESENT ONLY WHERE THE PAGE ACTUALLY HAS ONE. `GetAllAsync` appends the
        // sort only when the client sends `order[0][column]` pointing at a column that HAS a
        // `name`; a page whose first column declares no name sends no sort at all (the documented
        // `OrderBy.Trim()`-on-NULL case). Inventing a column here would measure a statement the
        // application never issues. Where a sort IS passed it is the page's own column 0 `name`.
        //
        // ⚠️ `MinVolume = 1` IS DELIBERATE ON THESE. These tables hold TENS of rows on a campus
        // (the exception is `StudentExam`, which is why it carries a real floor), so an honest
        // realistic threshold would report every one of them as SKIP - "not measured yet" for a
        // grid that was measurable all along, which is the one outcome a coverage census must not
        // produce. The floor records the COVERAGE CLAIM, not a yardstick.
        //
        // ⚠️ THE SERVER WRAPS EACH CONJUNCT (`((a AND b) AND c)`); written flat here, which is
        // planner-identical. The `@p0/@p1` names differ too - the predicate is positional there and
        // named here, and a bind name is not part of the plan.
        // =====================================================================

        // --- master data / academic structure -----------------------------------
        ScopeGrid(v, "department-page", "Department grid - first page",
            "DepartmentRepository.GetAll(page, t, s, c)", "Department", Scope3,
            "SELECT COUNT(*) FROM Department WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "section-page", "Section grid - first page",
            "SectionRepository.GetAll(page, t, s, c)", "Section", Scope3,
            "SELECT COUNT(*) FROM Section WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "room-page", "Room grid - first page",
            "RoomRepository.GetAll(page, t, s, c)", "Room", Scope3,
            "SELECT COUNT(*) FROM Room WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "classroom-page", "Classroom list - first page",
            "ClassroomRepository.GetAll(page, t, s, c)", "Classroom", Scope3,
            "SELECT COUNT(*) FROM Classroom WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "ClassroomName"),

        ScopeGrid(v, "subject-page", "Subject grid - first page (school-scoped table)",
            "SubjectRepository.GetAll(page, t, s)", "Subject", Scope2,
            "SELECT COUNT(*) FROM Subject WHERE tenantid = @tenantId AND schoolid = @schoolId",
            5, orderBy: "Name"),

        ScopeGrid(v, "subject-campus-page", "Campus subject grid - first page",
            "CampusSubjectRepository.GetAll(page, t, s, c)", "CampusSubject", Scope3,
            "SELECT COUNT(*) FROM CampusSubject WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "CustomName"),

        ScopeGrid(v, "curriculum-page", "Curriculum grid - first page (school-scoped table)",
            "CurriculumRepository.GetAll(page, t, s)", "Curriculum", Scope2,
            "SELECT COUNT(*) FROM Curriculum WHERE tenantid = @tenantId AND schoolid = @schoolId",
            orderBy: "Name"),

        // ⚠️ `curriculumapproval` and `academicyear` have NO `name` column (a version is identified
        // by its curriculum, a year by StartYear/EndYear), so their grids send no sort - `orderBy`
        // is null on purpose and the spec carries no ORDER BY either.
        ScopeGrid(v, "curriculum-approval-page", "Curriculum approval grid - first page (school-scoped table)",
            "CurriculumApprovalRepository.GetAll(page, t, s)", "CurriculumApproval", Scope2,
            "SELECT COUNT(*) FROM CurriculumApproval WHERE tenantid = @tenantId AND schoolid = @schoolId"),

        // --- the curriculum TREE: three grids keyed on a PARENT the user opened, not on the scope ---
        // ⚠️ `curriculumgrade` and `curriculumgradesubject` carry NO scope columns at all - they hang
        // off `curriculumversion`, the only one of the three holding tenant/school. Each grid is
        // reached by the id in its route (`curriculum/{curriculumId}/curriculumVersion`,
        // `curriculumVersion/{id}/curriculumGrade`, `curriculumGrade/{id}/curriculumGradeSubject`), so
        // the spec is handed the parent the resolver chose. The ORDER BY is the page's OWN first
        // column, taken from the grid declarations in `school.curriculum.{version,grade,subject}.js`.
        new QuerySpec
        {
            Key = "curriculum-version-page",
            Title = "Curriculum version grid - first page (keyed on the curriculum, not the scope)",
            Source = "CurriculumVersionRepository.GetAll(page, curriculumId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM curriculumversion WHERE curriculumid = @curriculumId",
            MinVolume = 1,
            IndexTable = "curriculumversion",
            IndexName = "ux_curriculumversion_curriculumid_version",
            IndexColumns = "curriculumid, version",
            IndexRationale =
                "⚠️ THE QUERY IS NOT SCOPED AT ALL - it filters `c.Id = @CurriculumId`, the curriculum the " +
                "route names, so the driver is a PK lookup on Curriculum and everything else is a PK join " +
                "(CurriculumVersion by `curriculumid` via the unique `ux_curriculumversion_curriculumid_version`, " +
                "Country by PK, two users self-joins by PK). A school holds a handful of versions, so the cost " +
                "is the constant - what the spec proves is that the paged read, its joins and its ORDER BY " +
                "behave. The two `users` joins are LEFT on purpose (an unapproved draft has no approver).",
            Sql = @"
                SELECT c.Name as CurriculumName, c.Code as CurriculumCode, c.Description as CurriculumDescription,
                       ct.Id as CountryId, ct.shortname as CountryName, cv.Id, cv.curriculumid, cv.version,
                       cv.CurriculumStatus, cv.ApprovedBy, cv.CreatedBy,
                       u.firstname||' '||u.lastname as ApprovalName, u2.firstname||' '||u2.lastname as CreatedByName,
                       cv.tenantid, cv.schoolid
                FROM Curriculum c
                INNER JOIN CurriculumVersion cv on c.Id = cv.CurriculumId
                INNER JOIN Country ct on c.CountryId = ct.Id
                LEFT JOIN users u on cv.approvedby = u.id
                LEFT JOIN users u2 on cv.createdby = u2.id
                WHERE c.Id = @curriculumId
                ORDER BY c.Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["curriculumId"] = v.CurriculumId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "curriculum-grade-page",
            Title = "Curriculum grade grid - first page (keyed on the version)",
            Source = "CurriculumGradeRepository.GetAll(page, curriculumVersionId) -> GenericRepository.GetAllAsync(page, x => x.CurriculumVersionId == id)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM curriculumgrade WHERE curriculumversionid = @curriculumVersionId",
            MinVolume = 1,
            IndexTable = "curriculumgrade",
            IndexName = "ix_curriculumgrade_curriculumversionid",
            IndexColumns = "curriculumversionid",
            IndexRationale =
                "The grid is `GenericRepository.GetAllAsync(page, x => x.CurriculumVersionId == id)`, i.e. " +
                "`SELECT * FROM CurriculumGrade WHERE CurriculumVersionId = @p ORDER BY Name LIMIT n` - and " +
                "`curriculumgrade` carries NO scope columns, so the index that matters is the FK one. " +
                "`ix_curriculumgrade_curriculumversionid` is deployed and is exactly the predicate's column; " +
                "the sort is a small in-memory step over one version's grades (a curriculum version has tens).",
            Sql = @"
                SELECT *
                FROM CurriculumGrade
                WHERE CurriculumVersionId = @curriculumVersionId
                ORDER BY Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["curriculumVersionId"] = v.CurriculumVersionId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "curriculum-grade-subject-page",
            Title = "Curriculum grade-subject grid - first page (keyed on the grade)",
            Source = "CurriculumGradeSubjectRepository.GetAll(page, curriculumGradeId) - the count+page round trip",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM curriculumgradesubject WHERE curriculumgradeid = @curriculumGradeId",
            MinVolume = 1,
            IndexTable = "curriculumgradesubject",
            IndexName = "ix_curriculumgradesubject_curriculumgradeid",
            IndexColumns = "curriculumgradeid",
            IndexRationale =
                "⚠️ THE SHAPE IS A COUNT + PAGE PAIR ISSUED THROUGH ONE `QueryMultipleAsync` (`countQuery;searchQuery`), " +
                "so the user waits for BOTH - reproduced here as the page half, which is the part that returns rows. " +
                "The predicate is `cgs.CurriculumGradeId = @p` and `curriculumgradesubject` has no scope columns, " +
                "so `ix_curriculumgradesubject_curriculumgradeid` is the driver and the INNER JOIN to Subject is a " +
                "PK lookup. The ORDER BY is the page's own first column (`cgs.DisplayOrder`).",
            Sql = @"
                SELECT cgs.Id, cgs.CurriculumGradeId, cgs.SubjectId, s.Name AS SubjectName,
                       cgs.DisplayOrder, cgs.WeeklyPeriods, cgs.IsOptional, cgs.PassingMarks, cgs.TotalMarks
                FROM CurriculumGradeSubject cgs
                INNER JOIN Subject s ON cgs.SubjectId = s.Id
                WHERE cgs.CurriculumGradeId = @curriculumGradeId
                ORDER BY cgs.DisplayOrder
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["curriculumGradeId"] = v.CurriculumGradeId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        ScopeGrid(v, "academic-year-page", "Academic year grid - first page",
            "AcademicYearRepository.GetAll(page, t, s, c)", "AcademicYear", Scope3,
            "SELECT COUNT(*) FROM AcademicYear WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ScopeGrid(v, "holiday-page", "Holiday grid - first page",
            "HolidayRepository.GetAll(page, t, s, c)", "Holiday", Scope3,
            "SELECT COUNT(*) FROM Holiday WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        // --- the platform grids: these three are gated on IsActive ALONE ---------
        // `TenantRepository.GetAllTenantUserInfo`, `SchoolRepository.GetAllSchoolUserInfo` and
        // `CampusRepository.GetAllCampusUserInfo` all pass `c => c.IsActive == true` with NO scope
        // predicates - a tenant/school/campus list is what a SuperAdmin picks FROM, so there is no
        // scope to filter by at the row level. Their volume probe therefore counts the whole table.
        ScopeGrid(v, "tenant-page", "Tenant list - first page (every active tenant)",
            "TenantRepository.GetAllTenantUserInfo (predicate is IsActive only - no scope columns)",
            "Tenant", "IsActive = true",
            "SELECT COUNT(*) FROM Tenant WHERE IsActive = true", orderBy: "Name"),

        ScopeGrid(v, "school-page", "School list - first page (every active school)",
            "SchoolRepository.GetAllSchoolUserInfo (predicate is IsActive only - no scope columns)",
            "School", "IsActive = true",
            "SELECT COUNT(*) FROM School WHERE IsActive = true", orderBy: "Name"),

        ScopeGrid(v, "campus-page", "Campus list - first page (every active campus)",
            "CampusRepository.GetAllCampusUserInfo (predicate is IsActive only - no scope columns)",
            "Campus", "IsActive = true",
            "SELECT COUNT(*) FROM Campus WHERE IsActive = true", orderBy: "Name"),

        // --- fees / HR master data ---------------------------------------------
        ScopeGrid(v, "fee-type-page", "Fee type grid - first page",
            "FeeTypeRepository.GetAll(page, t, s, c)", "FeeType", Scope3,
            "SELECT COUNT(*) FROM FeeType WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "fee-discount-type-page", "Discount type grid - first page",
            "DiscountRepository.GetAll(page, t, s, c)", "Discount", Scope3,
            "SELECT COUNT(*) FROM Discount WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        // The fee-STRUCTURE grid (`fee.structure.html`) - the billing plan every enrolment's
        // auto-assign gate reads. ⚠️ THREE MORE FEE GRIDS ARE DELIBERATELY NOT SPECCED HERE, and the
        // reason is the measurement, not the code: `studentfeeassignment`, `studentfeediscount` and
        // `latefeecharges` hold ZERO rows on the measured (median) campus - their rows live on campus 1
        // and 3 - so a spec for each would report SKIP, which this tool's own rule says reads as
        // coverage while measuring nothing. They are owed a seeder (or a different measured scope),
        // not a spec.
        new QuerySpec
        {
            Key = "fee-structure-page",
            Title = "Fee structure grid - first page",
            Source = "FeeStructureRepository.GetAll(page, tenantId, schoolId, campusId) - the select half",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM feestructure WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "feestructure",
            IndexName = "ix_feestructure_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "The scope triple is served by `ix_feestructure_tenantschoolcampus`. Two INNER JOINs " +
                "(AcademicYear and AcademicGrade by PK) and one LEFT JOIN to CurriculumGrade provide the " +
                "display names; the correlated `(SELECT COUNT(0) FROM FeeStructureDetail fsd WHERE " +
                "fsd.feestructureid = fs.id) AS DetailCount` runs PER ROW and wants an index on " +
                "`feestructuredetail.feestructureid` (`ix_feestructuredetail_structure`). ⚠️ The repo issues " +
                "count + page through ONE `QueryMultipleAsync`, so this page spec measures the half that " +
                "returns rows; its count re-runs the same joins but on a table holding ONE row per campus, " +
                "where a second spec would time a constant.",
            Sql = @"
                SELECT
                    fs.id AS Id, fs.tenantid AS TenantId, fs.schoolid AS SchoolId, fs.campusid AS CampusId,
                    fs.academicyearid AS AcademicYearId, fs.academicgradeid AS AcademicGradeId,
                    fs.name AS Name, fs.effectivefrom AS EffectiveFrom, fs.effectiveto AS EffectiveTo,
                    fs.status AS Status, fs.createdby AS CreatedBy, fs.modifiedby AS ModifiedBy,
                    fs.createdon AS CreatedOn, fs.modifiedon AS ModifiedOn,
                    (ay.startyear || '-' || ay.endyear) AS AcademicYearName,
                    cg.name AS AcademicGradeName,
                    (SELECT COUNT(0) FROM feestructuredetail fsd WHERE fsd.feestructureid = fs.id) AS DetailCount
                FROM feestructure fs
                INNER JOIN academicyear ay ON ay.id = fs.academicyearid
                INNER JOIN academicgrade ag ON ag.id = fs.academicgradeid
                LEFT JOIN curriculumgrade cg ON cg.id = ag.curriculumgradeid
                WHERE fs.tenantid = @tenantId AND fs.schoolid = @schoolId AND fs.campusid = @campusId
                ORDER BY fs.name ASC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        ScopeGrid(v, "payment-method-page", "Payment method grid - first page",
            "PaymentMethodRepository.GetAll(page, t, s, c)", "PaymentMethod", Scope3,
            "SELECT COUNT(*) FROM PaymentMethod WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "tax-code-page", "Tax code grid - first page",
            "TaxCodeRepository.GetAll(page, t, s, c)", "TaxCode", Scope3,
            "SELECT COUNT(*) FROM TaxCode WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "hr-approval-template-page", "Approval template grid - first page",
            "ApprovalTemplateRepository.GetAll(page, t, s, c)", "ApprovalTemplate", Scope3,
            "SELECT COUNT(*) FROM ApprovalTemplate WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "ModuleName"),

        // ⚠️ The roles screen deliberately shows CUSTOM roles only (`IsSystemRole = false`), and
        // every seeded role IS a system role - so this grid is legitimately EMPTY on the e2e
        // baseline and must not be "fixed" to include them. (The definition wizard's ACCESS step
        // needed the OTHER set, which is why `RolesRepository.GetAllForPermissions` exists.)
        ScopeGrid(v, "roles-page", "Roles grid - first page (custom roles only)",
            "RolesRepository.GetAll(page, t, s, c) - IsSystemRole == false && IsActive == true",
            "Roles", Scope3 + " AND IsSystemRole = false AND IsActive = true",
            "SELECT COUNT(*) FROM Roles WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND issystemrole = false AND isactive = true",
            orderBy: "Name"),

        // ⚠️ `UserRoleRepository.GetAll(page)` passes NO predicate at all, so this is the one grid
        // in this family that is NOT scope-filtered - the tool measures the unbounded table.
        ScopeGrid(v, "user-role-page", "User role grid - first page (NO predicate at all)",
            "UserRoleRepository.GetAll(page) - no predicate, so no WHERE clause is emitted",
            "UserRole", null,
            "SELECT COUNT(*) FROM UserRole", orderBy: null),

        // --- exams: the one meaningfully sized grid in this family ---------------
        // 6,000 rows on the measured campus. `exam.student.js` declares no column `name`, so the
        // page sends no sort and the spec carries none either.
        ScopeGrid(v, "exam-student-sheet-page", "Marks-entry sheet list - first page",
            "StudentExamRepository.GetAll(page, t, s, c)", "StudentExam", Scope3,
            "SELECT COUNT(*) FROM StudentExam WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            1000),

        ScopeGrid(v, "subject-assessment-component-page", "Subject assessment component grid - first page",
            "SubjectAssessmentRepository.GetAll(page, t, s, c)", "SubjectAssessmentComponent", Scope3,
            "SELECT COUNT(*) FROM SubjectAssessmentComponent WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ScopeGrid(v, "exam-assessment-component-page", "Assessment component grid - first page",
            "AssessmentComponentRepository.GetAll(page, t, s, c)", "AssessmentComponent", Scope3,
            "SELECT COUNT(*) FROM AssessmentComponent WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeGrid(v, "student-report-card-page", "Report card grid - first page",
            "StudentReportCardRepository.GetAll(page, t, s, c)", "StudentReportCard", Scope3,
            "SELECT COUNT(*) FROM StudentReportCard WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        // --- inventory master data (all four gate on IsActive as well) -----------
        ScopeGrid(v, "inv-category-page", "Inventory category grid - first page",
            "InvCategoryRepository.GetAll(page, t, s, c)", "InvCategory", Scope3 + " AND IsActive = true",
            "SELECT COUNT(*) FROM InvCategory WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            orderBy: "Name"),

        ScopeGrid(v, "inv-location-page", "Inventory location grid - first page",
            "InvLocationRepository.GetAll(page, t, s, c)", "InvLocation", Scope3 + " AND IsActive = true",
            "SELECT COUNT(*) FROM InvLocation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            orderBy: "Name"),

        ScopeGrid(v, "inv-uom-page", "Inventory UOM grid - first page",
            "InvUOMRepository.GetAll(page, t, s, c)", "InvUOM", Scope3 + " AND IsActive = true",
            "SELECT COUNT(*) FROM InvUOM WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            orderBy: "Name"),

        ScopeGrid(v, "inv-supplier-page", "Supplier grid - first page",
            "InvSupplierRepository.GetAll(page, t, s, c)", "InvSupplier", Scope3 + " AND IsActive = true",
            "SELECT COUNT(*) FROM InvSupplier WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            10, orderBy: "Name"),

        // --- transport (the route grid already has `tr-route-page`) -------------
        ScopeGrid(v, "transport-vehicle-page", "Transport vehicle grid - first page",
            "TransportVehicleRepository.GetAll(page, t, s, c)", "TransportVehicle", Scope3,
            "SELECT COUNT(*) FROM TransportVehicle WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            5, orderBy: "VehicleNumber"),

        ScopeGrid(v, "transport-driver-page", "Transport driver grid - first page",
            "TransportDriverRepository.GetAll(page, t, s, c)", "TransportDriver", Scope3,
            "SELECT COUNT(*) FROM TransportDriver WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            5, orderBy: "Name"),

        ScopeGrid(v, "transport-attendant-page", "Transport attendant grid - first page",
            "TransportAttendantRepository.GetAll(page, t, s, c)", "TransportAttendant", Scope3,
            "SELECT COUNT(*) FROM TransportAttendant WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            5, orderBy: "Name"),

        // --- events, timetable --------------------------------------------------
        // `school.event.js` declares no column `name` either, so no sort is sent.
        ScopeGrid(v, "school-event-page", "School event grid - first page",
            "SchoolEventRepository.GetAll(page, t, s, c)", "SchoolEvent", Scope3,
            "SELECT COUNT(*) FROM SchoolEvent WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        ScopeGrid(v, "timetable-page", "Timetable grid - first page",
            "TimetableRepository.GetAll(page, t, s, c)", "Timetable", Scope3,
            "SELECT COUNT(*) FROM Timetable WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        // =====================================================================
        // THE HR WORKFLOW DESKS.
        //
        // ⚠️ THESE SUPERSEDE AN EARLIER RECORDED DECISION, DELIBERATELY. A note further up this file
        // used to say the leave / loan / overtime / contract tables were "deliberately NOT specced"
        // because "a table with tens of rows is not a performance question". The coverage census is
        // what reversed it: those tables were not merely small, they held ZERO rows in every
        // database here, so a SKIP could not be told apart from an unmeasured grid - and the whole
        // point of `specs x data` is that a shipped grid nobody measures is the gap. The tables are
        // now seeded (`data-volume/Seeders/HrWorkflowSeeder`) and each spec below carries
        // `MinVolume = 1` for the reason the GENERIC SCOPE GRIDS block states: the floor records the
        // COVERAGE CLAIM, not a yardstick. Do not raise a floor here to make a number look serious.
        //
        // ⚠️ FOUR SHAPES, AND EACH IS TRANSCRIBED FROM THE REPOSITORY THAT SERVES ITS SCREEN - none
        // is a guess:
        //   * a hand-written `QueryMultipleAsync` (count + paged search) -> ONE page spec, because a
        //     single-table count over the same predicate cannot be the finding;
        //   * `GetAllPage`, whose count and page are TWO round trips -> two specs (the
        //     `hr-payroll-records-*` precedent);
        //   * a plain `IEnumerable` list read by a CLIENT-side grid -> a `ScopeList` spec, which
        //     sends no LIMIT because the repository does not;
        //   * a modal's FK child read -> a spec pointed at the id `ScopeVars` resolves.
        // =====================================================================

        // --- the four list reads a client-side grid or a dialog dropdown is fed from ---
        ScopeList(v, "hr-loan-type-list", "Loan type admin grid - the whole campus list",
            "LoanTypeRepository.GetAll(t, s, c)", "EmployeeLoanType", Scope3,
            "SELECT COUNT(*) FROM EmployeeLoanType WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "DisplayOrder, Name"),

        ScopeList(v, "hr-overtime-policy-list", "Overtime policy grid - the whole campus list",
            "OvertimePolicyRepository.GetAll(t, s, c)",
            "OvertimePolicy op", Scope3E("op"),
            "SELECT COUNT(*) FROM overtimepolicy WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            select: @"SELECT op.*, dept.Name AS DepartmentName, desig.Name AS DesignationName",
            joins: @"LEFT JOIN Department dept ON op.DepartmentId = dept.Id
                      LEFT JOIN Designation desig ON op.DesignationId = desig.Id",
            orderBy: "op.NormalHours DESC"),

        ScopeList(v, "hr-document-type-list", "Document type admin grid - the whole campus list",
            "EmployeeDocumentTypeRepository.GetAll -> GenericRepository.FindAsync(scope, Name)",
            "EmployeeDocumentType", Scope3,
            "SELECT COUNT(*) FROM employeedocumenttype WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        ScopeList(v, "hr-contract-type-list", "Contract type admin grid - the whole campus list",
            "ContractTypeRepository.GetAll -> GenericRepository.FindAsync(scope, Name)",
            "ContractType", Scope3,
            "SELECT COUNT(*) FROM contracttype WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        // --- the loan desk: count and page are TWO round trips -------------------
        new QuerySpec
        {
            Key = "hr-loan-page",
            Title = "Employee loan grid - first page (with its outstanding-balance sub-selects)",
            Source = "LoanRepository.GetAllPage (page half)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeloan WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT el.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                       emp.EmployeeCode,
                       dept.Name AS DepartmentName,
                       lt.Name AS LoanType,
                       appr.FirstName || ' ' || appr.LastName AS ApprovedByName,
                       COALESCE((SELECT SUM(i.TotalAmount) FROM EmployeeLoanInstallment i
                                  WHERE i.EmployeeLoanId = el.Id AND i.Status = 'Pending'), 0) AS OutstandingAmount,
                       (SELECT COUNT(*) FROM EmployeeLoanInstallment i
                         WHERE i.EmployeeLoanId = el.Id AND i.Status = 'Pending') AS RemainingInstallments
                FROM EmployeeLoan el
                LEFT JOIN Employee emp ON el.EmployeeId = emp.Id
                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                LEFT JOIN EmployeeLoanType lt ON el.LoanTypeId = lt.Id
                LEFT JOIN Employee appr ON el.ApprovedBy = appr.UserId
                WHERE el.TenantId = @tenantId AND el.SchoolId = @schoolId AND el.CampusId = @campusId
                ORDER BY el.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-loan-count",
            Title = "Employee loan grid - total count (its own round trip)",
            Source = "LoanRepository.GetAllPage (count half, `ExecuteScalarAsync` then `QueryAsync`)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM employeeloan WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT COUNT(*)
                FROM EmployeeLoan el
                LEFT JOIN Employee emp ON el.EmployeeId = emp.Id
                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                LEFT JOIN EmployeeLoanType lt ON el.LoanTypeId = lt.Id
                WHERE el.TenantId = @tenantId AND el.SchoolId = @schoolId AND el.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // --- the loan DETAIL modal reads the schedule by the loan id, not the scope ---
        new QuerySpec
        {
            Key = "hr-loan-installments",
            Title = "Loan detail modal - the schedule of one loan",
            Source = "LoanRepository.GetInstallments(loanId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeloaninstallment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT * FROM EmployeeLoanInstallment
                WHERE EmployeeLoanId = @loanId
                ORDER BY InstallmentNo",
            Params = new Dictionary<string, object> { ["loanId"] = v.LoanId },
        },

        // --- overtime, documents, contracts, reporting, corrections: one round trip each ---
        new QuerySpec
        {
            Key = "hr-overtime-page",
            Title = "Overtime All Records grid - first page",
            Source = "EmployeeOvertimeRepository.GetAll(page, t, s, c) - no filters",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeovertime WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT eo.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                       emp.EmployeeCode,
                       dept.Name AS DepartmentName,
                       appr.FirstName || ' ' || appr.LastName AS ApproverName
                FROM EmployeeOvertime eo
                LEFT JOIN Employee emp ON eo.EmployeeId = emp.Id
                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                LEFT JOIN Employee appr ON eo.ApprovedBy = appr.UserId
                WHERE eo.TenantId = @tenantId AND eo.SchoolId = @schoolId AND eo.CampusId = @campusId
                ORDER BY eo.AttendanceDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-document-page",
            Title = "Employee document grid - first page",
            Source = "EmployeeDocumentRepository.GetAll(page, t, s, c) - no filters",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeedocument WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT ed.Id, ed.EmployeeId,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                       emp.EmployeeCode,
                       edt.Name AS DocumentTypeName,
                       ed.DocumentNumber, ed.IssueDate, ed.ExpiryDate,
                       ed.FileName, ed.FileExtension, ed.VerificationStatus,
                       edt.HasExpiry, ed.IsActive
                FROM EmployeeDocument ed
                LEFT JOIN Employee emp ON ed.EmployeeId = emp.Id
                LEFT JOIN EmployeeDocumentType edt ON ed.DocumentTypeId = edt.Id
                WHERE ed.TenantId = @tenantId AND ed.SchoolId = @schoolId AND ed.CampusId = @campusId
                ORDER BY edt.Name, ed.ExpiryDate
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-contract-page",
            Title = "Employment contract grid - first page",
            Source = "EmploymentContractRepository.GetAll(page, t, s, c) - no filters",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employmentcontract WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT ec.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                       emp.EmployeeCode,
                       d.Name AS DepartmentName,
                       dg.Name AS DesignationName,
                       ct.Name AS ContractTypeName,
                       pf.Name AS PaymentFrequencyName
                FROM EmploymentContract ec
                LEFT JOIN Employee emp ON ec.EmployeeId = emp.Id
                LEFT JOIN Department d ON emp.DepartmentId = d.Id
                LEFT JOIN Designation dg ON emp.DesignationId = dg.Id
                LEFT JOIN ContractType ct ON ec.ContractTypeId = ct.Id
                LEFT JOIN PaymentFrequency pf ON ec.PaymentFrequencyId = pf.Id
                WHERE ec.TenantId = @tenantId AND ec.SchoolId = @schoolId AND ec.CampusId = @campusId
                ORDER BY ec.StartDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // ⚠️ A DERIVED TABLE. The repository wraps its own SELECT in `FROM (…) t` so the DataTables
        // column aliases (EmployeeName / ManagerName) are valid in the WHERE clause. Written flat
        // here because there is no search, and the wrapper is plan-identical without one - but the
        // ORDER BY still names `t`, so the alias is kept.
        new QuerySpec
        {
            Key = "hr-reporting-page",
            Title = "Reporting structure grid - first page",
            Source = "EmployeeReportingRepository.GetAll(page, t, s, c) - no filters",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeereporting WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT * FROM (
                    SELECT er.*,
                           e.FirstName || ' ' || e.LastName AS EmployeeName, e.EmployeeCode AS EmployeeCode,
                           m.FirstName || ' ' || m.LastName AS ManagerName, m.EmployeeCode AS ManagerCode,
                           m.UserId AS ManagerUserId
                    FROM EmployeeReporting er
                    LEFT JOIN Employee e ON e.Id = er.EmployeeId
                    LEFT JOIN Employee m ON m.Id = er.ManagerEmployeeId
                    WHERE er.TenantId = @tenantId AND er.SchoolId = @schoolId AND er.CampusId = @campusId
                ) t
                ORDER BY t.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // ⚠️ The repository sends NO ORDER BY unless the page supplies one (there is no `else`
        // branch), so the spec carries none either - `exam.performanceScale.js` declares no column
        // `name`, which is the documented "the page sorted nothing" case.
        // ⚠️ ALSO NOTE: the method's Dapper parameter object binds `TenantId`/`SchoolId` and omits
        // `CampusId` while the SQL text references `@campusId`. The spec binds all three, which is
        // what the statement needs; the omission in the repository is a separate (pre-existing)
        // question and is not silently mirrored here.
        new QuerySpec
        {
            Key = "exam-performance-scale-page",
            Title = "Performance scale grid - first page (with its level count)",
            Source = "PerformanceScaleRepository.GetAll(page, t, s, c)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancescale WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT ps.*,
                       (SELECT COUNT(*) FROM PerformanceScaleLevel WHERE PerformanceScaleId = ps.Id) AS LevelCount
                FROM PerformanceScale ps
                WHERE ps.TenantId = @tenantId AND ps.SchoolId = @schoolId AND ps.CampusId = @campusId
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // --- the scale editor's level read, keyed on the scale rather than the scope -----
        new QuerySpec
        {
            Key = "exam-performance-scale-levels",
            Title = "Performance scale editor - the levels of one scale",
            Source = "PerformanceScaleLevelRepository (levels of one scale)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancescalelevel l JOIN performancescale s ON s.id = l.performancescaleid WHERE s.tenantid = @tenantId AND s.schoolid = @schoolId AND s.campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT * FROM PerformanceScaleLevel
                WHERE PerformanceScaleId = @performanceScaleId
                ORDER BY DisplayOrder",
            Params = new Dictionary<string, object> { ["performanceScaleId"] = v.PerformanceScaleId },
        },

        // =====================================================================
        // EXAM TOOLING - `assessmenttool` / `assessmenttoolitem` / `rubriccriterionlevel` /
        // `examinvigilator`.
        //
        // ⚠️ ALL FOUR TABLES HELD ZERO ROWS IN EVERY DATABASE HERE WHILE FOUR SHIPPED SCREENS READ
        // THEM, so a spec over any of them reported SKIP - "not measured yet" for a screen the
        // application really ships. `ExamToolingSeeder` now fills them for the measured campus, and
        // every read below is transcribed from the repository that serves it.
        //
        // ⚠️ THE SAME TABLE IS REACHED TWO WAYS, AND BOTH ARE HERE, because they are different
        // statements with different predicates:
        //   * `assessmenttool` is PAGED on its own screen and UNPAGED as the rubric editor's
        //     tool dropdown (`examination/assessmentTool/list`, called by `exam.rubricCriterionLevel.js`);
        //   * `rubricCriterionLevel` has a PAGED overload, but its screen does NOT use it - the GET
        //     route calls the UNPAGED 3-join `GetAll(t, s, c)` and groups client-side, so the paged
        //     statement is deliberately NOT modelled here (a spec must reproduce what the app RUNS).
        //
        // ⚠️ WHAT "REACHABLE" MEANS DIFFERS PER TABLE. Only `assessmenttool` and `examinvigilator`
        // carry the scope triple; `assessmenttoolitem` and `rubriccriterionlevel` carry NONE, so both
        // reach the scope THROUGH `assessmenttool` (an item or level whose tool sits elsewhere is a
        // row that exists and is invisible). That is why every volume probe below joins up to the tool.
        // =====================================================================

        // The tool grid: PAGED, with the per-row `ItemCount` sub-select and the scale name the page
        // prints. `exam.assessmentTool.js` declares column 0 as `name: "Name"`, so the page sends
        // `ORDER BY Name` - which is why this carries a sort the unpaged sibling does not.
        new QuerySpec
        {
            Key = "exam-assessment-tool-page",
            Title = "Assessment tool grid - first page (with its criterion count)",
            Source = "AssessmentToolRepository.GetAll(page, t, s, c)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM AssessmentTool WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "assessmenttool",
            IndexName = "ix_assessmenttool_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "⚠️ `assessmenttool` carries NO scope index at all - only its PK, `assessmentmethodid` and " +
                "`performancescaleid` - so the tool grid's own `WHERE` on the triple has nothing to prune " +
                "with. It is recorded rather than written because a campus holds a TOOL PER METHOD (six), so " +
                "a seq scan of a six-row table is already free: the `invitem` rule, an index is owed only " +
                "when a probe on a FAILING query proposes it.",
            Sql = @"
                SELECT at.Id, at.Name, at.Description, at.AssessmentMethodId, at.PerformanceScaleId,
                       at.TenantId, at.SchoolId, at.CampusId, at.CreatedBy, at.ModifiedBy,
                       at.CreatedOn, at.ModifiedOn,
                       COALESCE(ps.Name, '') AS PerformanceScaleName,
                       (SELECT COUNT(*) FROM AssessmentToolItem WHERE AssessmentToolId = at.Id) AS ItemCount
                FROM AssessmentTool at
                LEFT JOIN PerformanceScale ps ON ps.Id = at.PerformanceScaleId
                WHERE at.TenantId = @tenantId AND at.SchoolId = @schoolId AND at.CampusId = @campusId
                ORDER BY Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The rubric editor's TOOL dropdown - `examination/assessmentTool/list`, the UNPAGED overload
        // (`AssessmentToolRepository.GetAll(t, s, c)` `ORDER BY at.Name`). Same table, no paging, no
        // `ItemCount`, but it DOES project the scale name.
        new QuerySpec
        {
            Key = "exam-assessment-tool-list",
            Title = "Rubric editor - the campus's assessment tools (unpaged dropdown)",
            Source = "AssessmentToolRepository.GetAll(t, s, c) - unpaged, `examination/assessmentTool/list`",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM AssessmentTool WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "assessmenttool",
            IndexRationale =
                "the same missing scope index as the paged grid, on the same six-row table - recorded, not owed.",
            Sql = @"
                SELECT at.*, COALESCE(ps.Name, '') AS PerformanceScaleName
                FROM AssessmentTool at
                LEFT JOIN PerformanceScale ps ON ps.Id = at.PerformanceScaleId
                WHERE at.TenantId = @tenantId AND at.SchoolId = @schoolId AND at.CampusId = @campusId
                ORDER BY at.Name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ⚠️ THE SECOND STATEMENT OF THAT SAME CALL, AND IT IS MODELLED AS A SEMI-JOIN ON PURPOSE.
        // `AssessmentToolRepository.GetAll(t, s, c)` issues a follow-up read for the items of ALL the
        // tools it just listed - `WHERE AssessmentToolId = ANY(@toolIds)`. A single statement cannot
        // issue the client's second round trip, and a spec pinned to ONE tool would measure a different
        // selectivity from the one the screen causes, so this drives from the SAME tool set (the
        // campus's) - the documented shape for a repository's follow-up read. Same index, same row set.
        new QuerySpec
        {
            Key = "exam-tool-items-by-tools",
            Title = "Rubric editor - the criteria of the campus's tools (follow-up read)",
            Source = "AssessmentToolRepository.GetAll(t, s, c) - the items of the listed tools",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM AssessmentToolItem i JOIN AssessmentTool at ON at.Id = i.AssessmentToolId WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "assessmenttoolitem",
            IndexName = "ix_assessmenttoolitem_assessmenttoolid",
            IndexColumns = "assessmenttoolid",
            IndexRationale =
                "⚠️ `assessmenttoolitem` carries ONLY its primary key - there is no index on `assessmenttoolid` " +
                "at all, which is the column this read (and the rubric 3-join) drives from. Recorded, not owed: " +
                "a campus holds a handful of methods x four criteria each, so the seq scan is a page or two.",
            Sql = @"
                SELECT * FROM AssessmentToolItem
                WHERE AssessmentToolId IN (
                    SELECT at.Id FROM AssessmentTool at
                    WHERE at.TenantId = @tenantId AND at.SchoolId = @schoolId AND at.CampusId = @campusId)
                ORDER BY AssessmentToolId, DisplayOrder",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The tool editor's own criteria read - `AssessmentToolItemRepository.GetAll(assessmentToolId)`,
        // served by `examination/assessmentToolItem/tool/{assessmentToolId}`. That route has NO screen
        // caller (the marks sheet takes its items from the ribbon payload), so it is the endpoint-only
        // case the transport tier also recorded: still a statement the API runs, and the tool it is
        // handed is the one with the MOST criteria so the row count is the worst case.
        new QuerySpec
        {
            Key = "exam-tool-items",
            Title = "Assessment tool editor - the criteria of one tool",
            Source = "AssessmentToolItemRepository.GetAll(assessmentToolId) -> GenericRepository.FindAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM AssessmentToolItem WHERE assessmenttoolid = @assessmentToolId",
            MinVolume = 1,
            IndexTable = "assessmenttoolitem",
            IndexName = "ix_assessmenttoolitem_assessmenttoolid",
            IndexColumns = "assessmenttoolid",
            IndexRationale =
                "the same missing FK index as the follow-up read above, keyed on ONE tool instead of the " +
                "campus's set - recorded, not owed on a table that is tens of rows by construction.",
            Sql = @"
                SELECT * FROM AssessmentToolItem
                WHERE AssessmentToolId = @assessmentToolId
                ORDER BY DisplayOrder",
            Params = new Dictionary<string, object>
            {
                ["assessmentToolId"] = v.AssessmentToolId,
            },
        },

        // The rubric cards: the UNPAGED 3-join the screen actually calls. ⚠️ The two LEFT JOINs are
        // load-bearing for REACHABILITY, not for display: the scope lives on `AssessmentTool` only, so
        // an item or level whose tool is elsewhere cannot be reached from here. `exam.rubricCriterionLevel.js`
        // then groups these rows by `assessmentToolItemId` into a CLIENT-side table.
        new QuerySpec
        {
            Key = "exam-rubric-levels",
            Title = "Rubric criterion levels - the campus's full level set (unpaged 3-join)",
            Source = "RubricCriterionLevelRepository.GetAll(t, s, c) - the GET route the screen calls",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM RubricCriterionLevel rcl JOIN AssessmentToolItem ati ON ati.Id = rcl.AssessmentToolItemId JOIN AssessmentTool at ON at.Id = ati.AssessmentToolId WHERE at.tenantid = @tenantId AND at.schoolid = @schoolId AND at.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "rubriccriterionlevel",
            IndexName = "ix_rubriccriterionlevel_assessmenttoolitemid",
            IndexColumns = "assessmenttoolitemid",
            IndexRationale =
                "⚠️ `rubriccriterionlevel` has an index on `performancescalelevelid` and its PK, but NONE on " +
                "`assessmenttoolitemid` - the column BOTH of its reads join on. Recorded, not owed: criteria x " +
                "levels is dozens of rows per campus, so the planner's seq scan is a page.",
            Sql = @"
                SELECT rcl.*, ati.Name AS CriterionName, psl.Label AS LevelLabel, psl.Score AS LevelScore, psl.Color AS LevelColor
                FROM RubricCriterionLevel rcl
                LEFT JOIN AssessmentToolItem ati ON ati.Id = rcl.AssessmentToolItemId
                LEFT JOIN PerformanceScaleLevel psl ON psl.Id = rcl.PerformanceScaleLevelId
                LEFT JOIN AssessmentTool at ON at.Id = ati.AssessmentToolId
                WHERE at.TenantId = @tenantId AND at.SchoolId = @schoolId AND at.CampusId = @campusId
                ORDER BY rcl.AssessmentToolItemId, rcl.DisplayOrder",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The rubric editor's per-criterion read - `examination/rubricCriterionLevel/item/{itemId}`.
        // ⚠️ It joins ONLY `PerformanceScaleLevel` (no reachability join), because the caller already
        // holds a criterion id the campus's tool list gave it. The criterion is the one with the MOST
        // levels, so this is the fattest level set the screen can ask for.
        new QuerySpec
        {
            Key = "exam-rubric-levels-by-item",
            Title = "Rubric editor - the levels of one criterion",
            Source = "RubricCriterionLevelRepository.GetAllByItemId(itemId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM RubricCriterionLevel WHERE assessmenttoolitemid = @assessmentToolItemId",
            MinVolume = 1,
            IndexTable = "rubriccriterionlevel",
            IndexName = "ix_rubriccriterionlevel_assessmenttoolitemid",
            IndexColumns = "assessmenttoolitemid",
            IndexRationale =
                "the same missing FK index as the campus-wide read, keyed on ONE criterion - recorded, not owed.",
            Sql = @"
                SELECT rcl.*, psl.Label AS LevelLabel, psl.Score AS LevelScore, psl.Color AS LevelColor
                FROM RubricCriterionLevel rcl
                LEFT JOIN PerformanceScaleLevel psl ON psl.Id = rcl.PerformanceScaleLevelId
                WHERE rcl.AssessmentToolItemId = @assessmentToolItemId
                ORDER BY rcl.DisplayOrder",
            Params = new Dictionary<string, object>
            {
                ["assessmentToolItemId"] = v.AssessmentToolItemId,
            },
        },

        // The invigilator desk - `examination/examInvigilator/list?scheduleId=`, the read that fills the
        // sitting's roster. ⚠️ It takes the schedule by QUERY STRING but the replacement list also
        // re-reads it with NO schedule (`scheduleId = null`), so the property is OPTIONAL and the spec
        // models the FILTERED form (the one that has to find the rows). The `Users` join is what makes
        // this measurable at all: a roster row whose teacher has no login renders a blank name, so the
        // fixture asserts the join resolves rather than the row count.
        new QuerySpec
        {
            Key = "exam-invigilator-list",
            Title = "Exam sitting roster - the invigilators of one schedule",
            Source = "ExamInvigilatorRepository.GetAll(t, s, c, scheduleId) - unpaged, ORDER BY TeacherName",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM ExamInvigilator WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "examinvigilator",
            IndexRationale =
                "`examinvigilator` is the one table in this batch that is properly indexed for its reads - " +
                "`ix_examinvigilator_schedule` (examscheduleid) serves the filtered form and " +
                "`ix_examinvigilator_tsc` the scope triple. Nothing is owed; declared so `--advise` compares " +
                "against the index that already exists instead of proposing a second one.",
            Sql = @"
                SELECT ei.*, COALESCE(tu.FirstName || ' ' || tu.LastName, '') AS TeacherName
                FROM ExamInvigilator ei
                LEFT JOIN Teacher t ON t.Id = ei.TeacherId
                LEFT JOIN Users tu ON tu.Id = t.UserId
                WHERE ei.TenantId = @tenantId AND ei.SchoolId = @schoolId AND ei.CampusId = @campusId
                ORDER BY TeacherName",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // FEE MONEY EXTRAS - `adhoccharge` (+ its student/classroom rows), `discountinvoices`,
        // `taxexemption`, `refund`.
        //
        // ⚠️ THREE OF THESE ARE PAGED GRIDS ON SHIPPED SCREENS and every table in the group held ZERO
        // rows in every database here, so each spec reported SKIP. `FeeMoneyExtrasSeeder` fills them
        // for the measured campus.
        //
        // ⚠️ THE PER-PARENT READS ARE MODELLED AS SEMI-JOINS, WHICH IS THE DOCUMENTED SHAPE for a
        // repository's follow-up read: `GetWithDetailsByChargeId`, `GetByAdHocChargeId`,
        // `GetDiscountByInvoiceId` and `GetByPayment` all take an id the USER picked rather than
        // filtering by scope, so a single statement cannot issue the round trip that produced it. The
        // semi-join drives the SAME index over the SAME id list the screen's own row click would give
        // it, so the plan is the application's - and it needs no per-parent resolver.
        //
        // ⚠️ INDEXES ARE ALREADY RIGHT HERE, so this batch OWES NO DDL. `adhoccharge` carries
        // `ix_adhoccharge_tenantschoolcampus`, `studentadhoccharge` `ix_studentadhoccharge_charge`,
        // `classroomadhoccharge` `idx_classroomadhoccharge_adhocid`, `discountinvoices`
        // `ix_discountinvoices_invoice`, `refund` `ix_refund_payment (tenantid, campusid, paymentid)`
        // and `taxexemption` `ix_taxexemption_studentfeetypetaxcode` whose leftmost three columns ARE
        // the grid's scope predicate. Each spec still declares its IndexTable and the index that
        // serves it, so `--advise` compares against what exists instead of proposing a second one.
        // =====================================================================

        // The ad-hoc charge grid (`fee.adhocCharge.html`). ⚠️ ITS SORT IS COLUMN 2, not column 0 - the
        // page declares `order: [[2, 'asc']]` over a `createdOn` column, so the request carries an order
        // the app's own `GetAll(page, t, s, c, academicYearId)` overload applies. The year predicate is
        // part of the screen's own filter, so it is part of the statement.
        new QuerySpec
        {
            Key = "fee-adhoc-charge-page",
            Title = "Ad-hoc charge grid - first page, one academic year",
            Source = "AdHocChargeRepository.GetAll(page, t, s, c, academicYearId) -> GenericRepository.GetAllAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM AdHocCharge WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND academicyearid = @academicYearId",
            MinVolume = 1,
            IndexTable = "adhoccharge",
            IndexName = "ix_adhoccharge_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "the grid's predicate is the scope triple plus an academic year, and " +
                "`ix_adhoccharge_tenantschoolcampus` serves exactly that prefix (`ix_adhoccharge_academicyear` " +
                "covers the year alone). Nothing owed - declared so the advisor compares rather than proposes.",
            Sql = @"
                SELECT * FROM AdHocCharge
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                  AND AcademicYearId = @academicYearId
                ORDER BY CreatedOn
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The charge's student rows - the dialog its own row action opens. ⚠️ The scope reaches this
        // read through the CHARGE (`INNER JOIN adhoccharge ac ON ac.id = sa.adhocchargeid` and its
        // three columns in the WHERE), and the NAME comes from `INNER JOIN student`, so either join
        // dropping a row makes it invisible while the table looks populated.
        new QuerySpec
        {
            Key = "fee-adhoc-charge-students",
            Title = "Ad-hoc charge detail - the students the charge was raised against",
            Source = "StudentAdHocChargeRepository.GetWithDetailsByChargeId(t, s, c, chargeId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentadhoccharge sa JOIN adhoccharge ac ON ac.id = sa.adhocchargeid WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "studentadhoccharge",
            IndexName = "ix_studentadhoccharge_charge",
            IndexColumns = "adhocchargeid",
            IndexRationale =
                "the read is keyed on the charge (`sa.adhocchargeid`) and `ix_studentadhoccharge_charge` " +
                "serves it; the semi-join below drives the same index over the campus's charge ids, which is " +
                "the set the grid's own row click can produce.",
            Sql = @"
                SELECT sa.*, s.name AS studentname, s.admissionnumber, i.invoicenumber
                FROM studentadhoccharge sa
                INNER JOIN student s ON sa.studentid = s.id
                LEFT JOIN invoices i ON sa.invoiceid = i.id
                INNER JOIN adhoccharge ac ON ac.id = sa.adhocchargeid
                WHERE sa.adhocchargeid IN (
                    SELECT id FROM adhoccharge
                     WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
                ORDER BY sa.id",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The classroom-scoped variant of the same charge (a `FindAsync` over `AdHocChargeId`).
        new QuerySpec
        {
            Key = "fee-adhoc-charge-classrooms",
            Title = "Ad-hoc charge detail - the classrooms the charge applies to",
            Source = "ClassroomAdHocChargeRepository.GetByAdHocChargeId(chargeId) -> GenericRepository.FindAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM classroomadhoccharge ca JOIN adhoccharge ac ON ac.id = ca.adhocchargeid WHERE ac.tenantid = @tenantId AND ac.schoolid = @schoolId AND ac.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "classroomadhoccharge",
            IndexName = "idx_classroomadhoccharge_adhocid",
            IndexColumns = "adhocchargeid",
            IndexRationale =
                "keyed on the charge, and `idx_classroomadhoccharge_adhocid` already indexes it. Nothing owed.",
            Sql = @"
                SELECT * FROM ClassroomAdHocCharge
                WHERE AdHocChargeId IN (
                    SELECT id FROM AdHocCharge
                     WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The discounts resolved onto an invoice - read BY INVOICE from the invoice detail, with no
        // scope predicate of its own. ⚠️ `ix_discountinvoices_invoice` is the index that keeps this
        // inside one invoice's rows.
        new QuerySpec
        {
            Key = "fee-discount-invoices",
            Title = "Invoice detail - the discounts applied to the campus's invoices",
            Source = "DiscountInvoicesRepository.GetDiscountByInvoiceId(invoiceId) -> GenericRepository.FindAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM discountinvoices di JOIN invoices i ON i.id = di.invoiceid WHERE i.tenantid = @tenantId AND i.schoolid = @schoolId AND i.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "discountinvoices",
            IndexName = "ix_discountinvoices_invoice",
            IndexColumns = "invoiceid",
            IndexRationale =
                "the read has NO scope predicate - it is `WHERE invoiceid = @id` - so the only thing that " +
                "bounds it is that column's index, which `ix_discountinvoices_invoice` is.",
            Sql = @"
                SELECT * FROM DiscountInvoices
                WHERE InvoiceId IN (
                    SELECT id FROM Invoices
                     WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The tax-exemption grid (`student.tax.exemption.html`). ⚠️ THE MOST JOIN-DEPENDENT READ IN THE
        // BATCH: `INNER JOIN Student` and `INNER JOIN TaxCode` (both drop a row that does not resolve)
        // plus LEFT JOINs to `TaxTreatmentMaster` and `FeeType`. The page declares no `order`, so
        // DataTables defaults to its FIRST column - `s.AdmissionNumber` - which is what the repository's
        // `ORDER BY {page.OrderBy}` therefore receives.
        new QuerySpec
        {
            Key = "fee-tax-exemption-page",
            Title = "Tax exemption grid - first page",
            Source = "TaxExemptionRepository.GetAll(page, t, s, c) - the select half",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(DISTINCT te.Id) FROM TaxExemption te INNER JOIN Student s ON te.StudentId = s.Id INNER JOIN TaxCode t ON te.TaxCodeId = t.Id LEFT JOIN FeeType f ON te.FeeTypeId = f.Id WHERE te.TenantId = @tenantId AND te.SchoolId = @schoolId AND te.campusId = @campusId",
            MinVolume = 1,
            IndexTable = "taxexemption",
            IndexName = "ix_taxexemption_studentfeetypetaxcode",
            IndexColumns = "tenantid, schoolid, campusid, studentid, feetypeid, taxcodeid",
            IndexRationale =
                "the grid's predicate is the scope triple, which is the LEFTMOST PREFIX of " +
                "`ix_taxexemption_studentfeetypetaxcode` - so the composite index serves this read as well as " +
                "the per-student one. Nothing owed.",
            Sql = @"
                SELECT te.id, te.studentid, te.feetypeid, te.taxcodeid, te.taxexemptionvalue, te.reason,
                       te.startdate, te.enddate, te.tenantid, te.schoolid, te.campusid, te.createdon, te.modifiedon,
                       f.name AS FeeTypeName,
                       s.id AS StudentId, s.name, s.age, s.dob, s.gender, s.photo, s.parentid, s.isactive,
                       s.admissionnumber, s.enrollmentdate, s.status, s.tenantid, s.campusid, s.createdon, s.modifiedon,
                       t.id AS TaxCodeId, t.name, t.description, t.code, t.taxtreatment,
                       tmm.name AS TaxTreatmentName, tmm.istaxable AS IsTaxable, t.isactive,
                       t.tenantid, t.campusid, t.createdon, t.modifiedon
                FROM TaxExemption te
                INNER JOIN Student s ON te.StudentId = s.Id
                INNER JOIN TaxCode t ON te.TaxCodeId = t.Id
                LEFT JOIN TaxTreatmentMaster tmm ON tmm.countryid = t.countryid AND tmm.id = t.taxtreatment AND tmm.isactive = TRUE
                LEFT JOIN FeeType f ON te.FeeTypeId = f.Id
                WHERE te.tenantid = @tenantId AND te.schoolid = @schoolId AND te.campusid = @campusId
                ORDER BY s.AdmissionNumber
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // ⚠️ THE GRID'S COUNT IS ITS OWN ROUND TRIP AND DESERVES ITS OWN SPEC. `GetAll` issues
        // `QueryAsync(countQuery)` and `QueryAsync(searchQuery)` as two SEPARATE statements, so the user
        // waits for both - and the count re-runs the same double INNER JOIN with a `count(DISTINCT)`,
        // which is not an index-only count over one table.
        new QuerySpec
        {
            Key = "fee-tax-exemption-count",
            Title = "Tax exemption grid - its own paged count",
            Source = "TaxExemptionRepository.GetAll(page, t, s, c) - the count half (a second round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(DISTINCT te.Id) FROM TaxExemption te INNER JOIN Student s ON te.StudentId = s.Id INNER JOIN TaxCode t ON te.TaxCodeId = t.Id LEFT JOIN FeeType f ON te.FeeTypeId = f.Id WHERE te.TenantId = @tenantId AND te.SchoolId = @schoolId AND te.campusId = @campusId",
            MinVolume = 1,
            IndexTable = "taxexemption",
            IndexName = "ix_taxexemption_studentfeetypetaxcode",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "the same scope-prefix predicate as the page, with a `count(DISTINCT te.Id)` over the joined " +
                "set - the composite index's leftmost three columns serve it.",
            Sql = @"
                SELECT count(DISTINCT te.Id) FROM TaxExemption te
                INNER JOIN Student s ON te.StudentId = s.Id
                INNER JOIN TaxCode t ON te.TaxCodeId = t.Id
                LEFT JOIN FeeType f ON te.FeeTypeId = f.Id
                WHERE te.TenantId = @tenantId AND te.SchoolId = @schoolId AND te.campusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The per-student read the fee screens use (`GetTaxExemptionByStudentId`) - a `FindAsync` over
        // scope + student. ⚠️ The student is taken from the campus's OWN exemption rows, the way the
        // screen takes it from the grid row the user opened: a spec pinned to a resolver's arbitrary
        // student would measure a search that finds nothing and look fast for the wrong reason.
        new QuerySpec
        {
            Key = "fee-tax-exemption-by-student",
            Title = "Fee screens - one student's tax exemptions",
            Source = "TaxExemptionRepository.GetTaxExemptionByStudentId(t, s, c, studentId) -> FindAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM taxexemption WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "taxexemption",
            IndexName = "ix_taxexemption_studentfeetypetaxcode",
            IndexColumns = "tenantid, schoolid, campusid, studentid",
            IndexRationale =
                "scope + student is the first four columns of `ix_taxexemption_studentfeetypetaxcode`, so the " +
                "read is index-served end to end (`ix_taxexemption_studentid` covers the student alone).",
            Sql = @"
                SELECT * FROM TaxExemption
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                  AND StudentId = (
                      SELECT te2.StudentId FROM TaxExemption te2
                       WHERE te2.TenantId = @tenantId AND te2.SchoolId = @schoolId AND te2.CampusId = @campusId
                       ORDER BY te2.Id LIMIT 1)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The refund dashboard's paged list (`school.refund.html`). ⚠️ IT IS `countSql;\ndataSql` IN ONE
        // `QueryMultipleAsync`, so both halves are one user wait - and the `ListSelect` it is built from
        // is FOUR joins plus a `paymentmethod`/`invoices` pair, which is why the page is the expensive
        // half of this batch rather than a single-table grid.
        new QuerySpec
        {
            Key = "fee-refund-page",
            Title = "Refund dashboard - first page",
            Source = "RefundRepository.GetAllCampusRefunds(t, s, c, page, status) - the data half",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(0) FROM refund r INNER JOIN student s ON s.id = r.studentid INNER JOIN payment p ON p.id = r.paymentid WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "refund",
            IndexName = "ix_refund_student_status",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "⚠️ THE PAGED READ SORTS BY A COLUMN NO INDEX LEADS. Its WHERE is the scope triple - the " +
                "leftmost prefix of `ix_refund_student_status (tenantid, schoolid, campusid, studentid, " +
                "status)` - but its `ORDER BY r.requestedon DESC, r.id DESC` has no supporting index, so the " +
                "planner sorts the campus's refunds. RECORDED, NOT OWED: a refund is an exceptional event, so " +
                "a campus holds a handful and the sort is free (the `invitem` rule - an index is owed only " +
                "when a probe on a FAILING query proposes it).",
            Sql = @"
                SELECT r.id, r.refundnumber, r.studentid, s.name AS studentname, s.admissionnumber,
                       r.paymentid, p.receiptnumber AS paymentreceiptnumber, p.amountpaid AS paymentamount,
                       r.invoiceid, i.invoicenumber, r.requestreference, r.requestedamount, r.amount,
                       r.taxamount, r.totalamount, r.refundmethodid, pm.name AS refundmethodname,
                       r.reason, r.status, r.requestedby, r.requestedon, r.approvedby, r.approvedon,
                       r.rejectedby, r.rejectedon, r.processedby, r.processedon, r.completedon,
                       r.externalreference, r.failurereason, r.workflowid, r.academicyearid,
                       r.tenantid, r.schoolid, r.campusid, r.createdon
                FROM refund r
                INNER JOIN student s ON s.id = r.studentid
                INNER JOIN payment p ON p.id = r.paymentid
                LEFT JOIN paymentmethod pm ON pm.id = r.refundmethodid
                LEFT JOIN invoices i ON i.id = r.invoiceid
                WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                ORDER BY r.requestedon DESC, r.id DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The same round trip's count - two joins over the whole campus, not an index-only count.
        new QuerySpec
        {
            Key = "fee-refund-count",
            Title = "Refund dashboard - its own paged count",
            Source = "RefundRepository.GetAllCampusRefunds(t, s, c, page, status) - the count half",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(0) FROM refund r INNER JOIN student s ON s.id = r.studentid INNER JOIN payment p ON p.id = r.paymentid WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "refund",
            IndexName = "ix_refund_student_status",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "the count is the scope triple over two INNER JOINs, served by the leftmost prefix of " +
                "`ix_refund_student_status`. Nothing owed.",
            Sql = @"
                SELECT COUNT(0) FROM refund r
                INNER JOIN student s ON s.id = r.studentid
                INNER JOIN payment p ON p.id = r.paymentid
                WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The refund a payment's own row action opens - read BY PAYMENT, which is exactly what
        // `ix_refund_payment (tenantid, campusid, paymentid)` indexes. ⚠️ Note the index does NOT carry
        // `schoolid` while the read filters it - the campus is the selective part, which is why the
        // index was shaped that way.
        new QuerySpec
        {
            Key = "fee-refund-by-payment",
            Title = "Payment row action - the refunds raised against one payment",
            Source = "RefundRepository.GetByPayment(t, s, c, paymentId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM refund WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "refund",
            IndexName = "ix_refund_payment",
            IndexColumns = "tenantid, campusid, paymentid",
            IndexRationale =
                "`ix_refund_payment` is (tenantid, campusid, paymentid) - it deliberately omits `schoolid` " +
                "because the campus is the selective column for this read and the scope is already pinned.",
            Sql = @"
                SELECT r.id, r.refundnumber, r.studentid, s.name AS studentname, s.admissionnumber,
                       r.paymentid, p.receiptnumber AS paymentreceiptnumber, p.amountpaid AS paymentamount,
                       r.invoiceid, i.invoicenumber, r.requestreference, r.requestedamount, r.amount,
                       r.taxamount, r.totalamount, r.refundmethodid, pm.name AS refundmethodname,
                       r.reason, r.status, r.requestedby, r.requestedon, r.approvedby, r.approvedon,
                       r.rejectedby, r.rejectedon, r.processedby, r.processedon, r.completedon,
                       r.externalreference, r.failurereason, r.workflowid, r.academicyearid,
                       r.tenantid, r.schoolid, r.campusid, r.createdon
                FROM refund r
                INNER JOIN student s ON s.id = r.studentid
                INNER JOIN payment p ON p.id = r.paymentid
                LEFT JOIN paymentmethod pm ON pm.id = r.refundmethodid
                LEFT JOIN invoices i ON i.id = r.invoiceid
                WHERE r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                  AND r.paymentid IN (
                      SELECT id FROM payment
                       WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId)
                ORDER BY r.requestedon DESC, r.id DESC",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hr-correction-page",
            Title = "Attendance correction grid - first page",
            Source = "AttendanceCorrectionRepository.GetAll(page, t, s, c) - no status filter",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM attendancecorrectionrequest WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT acr.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName,
                       emp.EmployeeCode,
                       ost.Name AS OriginalStatusName,
                       rst.Name AS RequestedStatusName,
                       rev.FirstName || ' ' || rev.LastName AS ReviewerName
                FROM AttendanceCorrectionRequest acr
                LEFT JOIN Employee emp ON acr.EmployeeId = emp.Id
                LEFT JOIN AttendanceStatus ost ON acr.OriginalStatusId = ost.Id
                LEFT JOIN AttendanceStatus rst ON acr.RequestedStatusId = rst.Id
                LEFT JOIN Employee rev ON acr.ReviewedBy = rev.UserId
                WHERE acr.TenantId = @tenantId AND acr.SchoolId = @schoolId AND acr.CampusId = @campusId
                ORDER BY acr.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // =====================================================================
        // THE INVENTORY ASSET LIFECYCLE.
        //
        // Four tables an asset's own screens page over, all of which held ZERO rows until
        // `data-volume/Seeders/InvAssetWorkspaceSeeder` filled them. The disposal and revaluation
        // grids are the two genuine paged grids here (the assignment list is a plain `IEnumerable`
        // and the maintenance log is a per-asset read).
        //
        // ⚠️ THE SCOPE COMES OFF `InvAsset` IN THREE OF THE FOUR, NOT OFF THE ROW'S OWN COLUMNS.
        // `InvAssetAssignmentRepository.GetAll` filters `a.TenantId/...` (the ASSET's scope) rather
        // than `aa.`'s, even though the assignment table carries the triple - so a spec filtering on
        // `aa` would measure a different predicate from the one the screen runs. The predicate is
        // transcribed as written.
        // =====================================================================
        ScopeList(v, "inv-asset-assignment-list", "Asset assignment list - every assignment of the campus",
            "InvAssetAssignmentRepository.GetAll(t, s, c) - the scope predicate is the ASSET's, not the row's",
            "InvAssetAssignment aa", "a.TenantId = @tenantId AND a.SchoolId = @schoolId AND a.CampusId = @campusId",
            "SELECT COUNT(*) FROM invassetassignment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            select: @"SELECT aa.Id, aa.AssetId, a.AssetCode, a.Name AS AssetName,
                             aa.AssignedToUserId, u.UserName AS AssignedToUserName,
                             aa.AssignedDate, aa.ReturnedDate, aa.Notes",
            joins: @"JOIN InvAsset a ON a.Id = aa.AssetId
                      LEFT JOIN Users u ON u.Id = aa.AssignedToUserId",
            orderBy: "aa.AssignedDate DESC"),

        // The disposal grid's count and page are TWO round trips (`QuerySingleOrDefaultAsync` for the
        // count, then `QueryAsync` for the page), so the user waits for both and each gets its own
        // spec - the `hr-loan-*` precedent.
        new QuerySpec
        {
            Key = "inv-asset-disposal-page",
            Title = "Asset disposal grid - first page",
            Source = "InvAssetDisposalRepository.GetAll(page, t, s, c, status) - no status filter",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM invassetdisposal WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invassetdisposal",
            IndexName = "idx_invassetdisposal_campus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the scope triple plus `ORDER BY d.DisposalDate DESC LIMIT n`, with a JOIN to InvAsset for the code and name. `idx_invassetdisposal_campus` serves the triple; `idx_invassetdisposal_asset` is the other access path but this query does not use it. Tens of rows per campus, so the sort is free either way - the spec records the shape, not an index claim.",
            Sql = @"
                SELECT d.Id, d.TenantId, d.SchoolId, d.CampusId, d.AssetId,
                       a.AssetCode, a.Name AS AssetName,
                       d.DisposalNumber, d.DisposalType, d.DisposalDate, d.DisposalValue,
                       d.Reason, d.Status, d.ApprovedBy, d.ApprovedOn, d.Notes
                FROM InvAssetDisposal d
                JOIN InvAsset a ON a.Id = d.AssetId
                WHERE d.TenantId = @tenantId AND d.SchoolId = @schoolId AND d.CampusId = @campusId
                ORDER BY d.DisposalDate DESC
                LIMIT @limit OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["limit"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-asset-disposal-count",
            Title = "Asset disposal grid - total count (its own round trip)",
            Source = "InvAssetDisposalRepository.GetAll (count half, its own round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM invassetdisposal WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invassetdisposal",
            IndexName = "idx_invassetdisposal_campus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope-only count with no join - why the repository can run it before building the page.",
            Sql = @"
                SELECT COUNT(*) FROM InvAssetDisposal d
                WHERE d.TenantId = @tenantId AND d.SchoolId = @schoolId AND d.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "inv-asset-revaluation-page",
            Title = "Asset revaluation grid - first page",
            Source = "InvAssetRevaluationRepository.GetAll(page, t, s, c, status) - no status filter",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM invassetrevaluation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invassetrevaluation",
            IndexName = "idx_invassetrevaluation_campus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "same shape as the disposal grid: the scope triple, a JOIN to InvAsset, and `ORDER BY r.RevaluationDate DESC LIMIT n`. Tens of rows per campus.",
            Sql = @"
                SELECT r.Id, r.TenantId, r.SchoolId, r.CampusId, r.AssetId,
                       a.AssetCode, a.Name AS AssetName,
                       r.RevaluationNumber, r.RevaluationDate, r.OldValue, r.NewValue,
                       r.Reason, r.Status, r.ApprovedBy, r.ApprovedOn, r.Notes
                FROM InvAssetRevaluation r
                JOIN InvAsset a ON a.Id = r.AssetId
                WHERE r.TenantId = @tenantId AND r.SchoolId = @schoolId AND r.CampusId = @campusId
                ORDER BY r.RevaluationDate DESC
                LIMIT @limit OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["limit"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "inv-asset-revaluation-count",
            Title = "Asset revaluation grid - total count (its own round trip)",
            Source = "InvAssetRevaluationRepository.GetAll (count half, its own round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM invassetrevaluation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invassetrevaluation",
            IndexName = "idx_invassetrevaluation_campus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope-only count with no join.",
            Sql = @"
                SELECT COUNT(*) FROM InvAssetRevaluation r
                WHERE r.TenantId = @tenantId AND r.SchoolId = @schoolId AND r.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The asset detail modal's log, keyed on the asset rather than the scope.
        new QuerySpec
        {
            Key = "inv-maintenance-by-asset",
            Title = "Asset detail modal - the maintenance log of one asset",
            Source = "InvMaintenanceRepository.GetByAssetId(assetId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM invmaintenance WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "invmaintenance",
            IndexName = "idx_invmaintenance_asset",
            IndexColumns = "assetid",
            IndexRationale = "keyed on `assetid` alone, which `idx_invmaintenance_asset` serves - the scope columns are in the table but are NOT in the predicate, so the scope index would not be used even if one existed.",
            Sql = @"
                SELECT * FROM InvMaintenance
                WHERE AssetId = @assetId
                ORDER BY MaintenanceDate DESC",
            Params = new Dictionary<string, object> { ["assetId"] = v.InvAssetId },
        },

        // =====================================================================
        // HR MONEY / LIFECYCLE - the payroll, performance and exit desks.
        //
        // All nine tables here held ZERO rows in every database, so their specs could only report
        // SKIP; `HrMoneyWorkspaceSeeder` fills them. Four of the reads below are PER-EMPLOYEE or
        // PER-PARENT reads that take an id rather than filtering by scope - the same shape as
        // `hr-loan-installments` and `inv-maintenance-by-asset` - so they resolve their id from
        // `ScopeVars` and print SKIP when the campus has none, which is honest.
        //
        // ⚠️ TWO OF THESE RUN THEIR COUNT IN THE SAME ROUND TRIP AS THE PAGE (`QueryMultipleAsync`
        // with `countSql + ";\n" + dataSql`), so the user waits for both and each half gets its own
        // spec - the `lib-fine-*` / `lib-inventory-audit-*` precedent. The performance grids are the
        // opposite: two SEPARATE round trips, so they also get a count spec each.
        // =====================================================================
        ScopeList(v, "hr-tax-config-list", "Payroll tax config grid - the whole campus list",
            "TaxConfigRepository.GetAll(t, s, c) - ACTIVE only, ordered by DisplayOrder",
            "TaxConfig", "TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId AND IsActive = true",
            "SELECT COUNT(*) FROM taxconfig WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = true",
            orderBy: "DisplayOrder"),

        new QuerySpec
        {
            Key = "hr-tax-slabs",
            Title = "Tax config editor - the slabs of one config",
            Source = "TaxConfigRepository.GetByTaxConfig(taxConfigId) - keyed on the config, not the scope",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM taxslab WHERE taxconfigid = @taxConfigId",
            MinVolume = 1,
            IndexTable = "taxslab",
            IndexName = "ix_taxslab_taxconfigid",
            IndexColumns = "taxconfigid",
            IndexRationale = "keyed on `taxconfigid` alone, which `ix_taxslab_taxconfigid` serves. The table carries the scope triple but the predicate does NOT, so a scope index would never be used here - recording the single-column index is the honest form.",
            Sql = @"
                SELECT * FROM TaxSlab
                WHERE TaxConfigId = @taxConfigId
                ORDER BY DisplayOrder",
            Params = new Dictionary<string, object> { ["taxConfigId"] = v.TaxConfigId },
        },

        new QuerySpec
        {
            Key = "hr-salary-structure-current",
            Title = "Salary-structure panel - one employee's CURRENT structure",
            Source = "EmployeeSalaryStructureRepository.GetCurrentByEmployee(employeeId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeesalarystructure WHERE employeeid = @employeeId",
            MinVolume = 1,
            IndexTable = "employeesalarystructure",
            IndexName = "ix_ess_iscurrent",
            IndexColumns = "employeeid, iscurrent",
            IndexRationale = "`WHERE EmployeeId = @EmployeeId AND IsCurrent = true ORDER BY Id DESC LIMIT 1` - exactly the pair `ix_ess_iscurrent` covers, so the read is an index lookup rather than a scope scan over the campus.",
            Sql = @"
                SELECT * FROM EmployeeSalaryStructure
                WHERE EmployeeId = @employeeId AND IsCurrent = true
                ORDER BY Id DESC
                LIMIT 1",
            Params = new Dictionary<string, object> { ["employeeId"] = v.EmployeeId },
        },

        new QuerySpec
        {
            Key = "hr-leave-balances-by-employee",
            Title = "Leave desk - one employee's balance rings",
            Source = "LeaveRepository.GetEmployeeBalance(employeeId, t, s, c)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeleavebalance WHERE employeeid = @employeeId",
            MinVolume = 1,
            IndexTable = "employeeleavebalance",
            IndexName = "ix_elb_employeeid",
            IndexColumns = "employeeid",
            IndexRationale = "the predicate leads on `elb.EmployeeId`, which `ix_elb_employeeid` serves; the scope triple is in the same WHERE but adds nothing once the employee is pinned. Two LEFT JOINs onto `leavetype`/`employee` are both primary-key lookups.",
            Sql = @"
                SELECT elb.*, lt.Name AS LeaveTypeName, lt.Code AS LeaveTypeCode, lt.IsPaid,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName, emp.EmployeeCode
                FROM EmployeeLeaveBalance elb
                LEFT JOIN LeaveType lt ON elb.LeaveTypeId = lt.Id
                LEFT JOIN Employee emp ON elb.EmployeeId = emp.Id
                WHERE elb.EmployeeId = @employeeId
                  AND elb.TenantId = @tenantId AND elb.SchoolId = @schoolId AND elb.CampusId = @campusId
                ORDER BY lt.Name",
            Params = new Dictionary<string, object>
            {
                ["employeeId"] = v.EmployeeId,
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hr-tax-record-by-employee",
            Title = "Payroll tax history - one employee's tax records",
            Source = "TaxConfigRepository.GetByEmployee(employeeId, t, s, c)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeetaxrecord WHERE employeeid = @employeeId",
            MinVolume = 1,
            IndexTable = "employeetaxrecord",
            IndexName = "ix_employeetaxrecord_employeeid",
            IndexColumns = "employeeid",
            IndexRationale = "the predicate leads on `etr.EmployeeId`; the SCOPE is taken off the JOINED `payrollperiod` (`pp.TenantId`), not off this row's own columns - which is why `ix_employeetaxrecord_employeeid` is the index the read actually uses.",
            Sql = @"
                SELECT etr.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName, emp.EmployeeCode,
                       tc.Name AS TaxConfigName
                FROM EmployeeTaxRecord etr
                LEFT JOIN Employee emp ON etr.EmployeeId = emp.Id
                LEFT JOIN TaxConfig tc ON etr.TaxConfigId = tc.Id
                INNER JOIN EmployeePayroll ep ON etr.EmployeePayrollId = ep.Id
                INNER JOIN PayrollPeriod pp ON ep.PayrollPeriodId = pp.Id
                WHERE etr.EmployeeId = @employeeId
                  AND pp.TenantId = @tenantId AND pp.SchoolId = @schoolId AND pp.CampusId = @campusId
                ORDER BY pp.StartDate DESC",
            Params = new Dictionary<string, object>
            {
                ["employeeId"] = v.EmployeeId,
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The review-cycle grid runs its count and its page as TWO round trips, so each is measured -
        // the `hr-loan-page` / `hr-loan-count` precedent. Both statements are transcribed from
        // `GetReviewCyclesPaged`, whose parameters are named `@T`/`@S`/`@C`; the names are kept in the
        // catalogue's own form because they are plan-identical.
        new QuerySpec
        {
            Key = "hr-review-cycle-page",
            Title = "Performance review-cycle grid - first page",
            Source = "PerformanceRepository.GetReviewCyclesPaged(page, t, s, c) - no search",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancereviewcycle WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "performancereviewcycle",
            IndexName = "ix_prc_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a single-table scope read with `ORDER BY StartDate DESC LIMIT n`. The scope triple is the whole predicate; tens of cycles per campus, so the sort is free. The spec records the shape, not an index claim.",
            Sql = @"
                SELECT * FROM PerformanceReviewCycle
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                ORDER BY StartDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-review-cycle-count",
            Title = "Performance review-cycle grid - total count (its own round trip)",
            Source = "PerformanceRepository.GetReviewCyclesPaged (count half, its own round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM performancereviewcycle WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "performancereviewcycle",
            IndexName = "ix_prc_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "a scope-only count on one table - why the repository can run it before building the page.",
            Sql = @"
                SELECT COUNT(*) FROM PerformanceReviewCycle
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The pending-reviews grid is the performance desk's live queue: `ReviewStatus != 'Completed'`
        // plus three LEFT JOINs for the names each row prints. It also runs count and page as two
        // round trips, so each half is measured.
        new QuerySpec
        {
            Key = "hr-pending-review-page",
            Title = "Performance desk - pending review queue, first page",
            Source = "PerformanceRepository.GetPendingReviewsPaged(page, t, s, c) - no search",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeperformancereview WHERE reviewstatus <> 'Completed' AND tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeeperformancereview",
            IndexName = "idx_employeeperformancereview_employeeid",
            IndexColumns = "employeeid",
            IndexRationale = "the scope triple leads the predicate and the three names are LEFT JOINed primary-key lookups, so on a campus of tens-to-hundreds of reviews the scope read is what bounds it. `idx_employeeperformancereview_cycleid` serves the OTHER path (reviews of one cycle).",
            Sql = @"
                SELECT r.*, e.FirstName || ' ' || e.LastName AS EmployeeName, e.EmployeeCode,
                       d.Name AS DepartmentName, c.Name AS CycleName
                FROM EmployeePerformanceReview r
                LEFT JOIN Employee e ON r.EmployeeId = e.Id
                LEFT JOIN Department d ON e.DepartmentId = d.Id
                LEFT JOIN PerformanceReviewCycle c ON r.PerformanceReviewCycleId = c.Id
                WHERE r.ReviewStatus <> 'Completed'
                  AND r.TenantId = @tenantId AND r.SchoolId = @schoolId AND r.CampusId = @campusId
                ORDER BY r.CreatedOn ASC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-pending-review-count",
            Title = "Performance desk - pending review count (its own round trip)",
            Source = "PerformanceRepository.GetPendingReviewsPaged (count half, its own round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM employeeperformancereview WHERE reviewstatus <> 'Completed' AND tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeeperformancereview",
            IndexName = "idx_employeeperformancereview_employeeid",
            IndexColumns = "employeeid",
            IndexRationale = "a scope-only count with NO join - the repository's count statement names one table, unlike its page statement.",
            Sql = @"
                SELECT COUNT(*) FROM EmployeePerformanceReview r
                WHERE r.ReviewStatus <> 'Completed'
                  AND r.TenantId = @tenantId AND r.SchoolId = @schoolId AND r.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ⚠️ THE SEPARATION GRID'S COUNT AND PAGE ARE ONE `QueryMultipleAsync` ROUND TRIP
        // (`countSql + ";\n" + dataSql`), so the user waits for BOTH and each half is measured - the
        // `lib-fine-page` / `lib-fine-count` precedent. The count is not free either: it carries the
        // same two LEFT JOINs as the page.
        //
        // ⚠️ `ORDER BY es.approvedDate DESC, es.id DESC` puts the NULL `approvedDate` of every DRAFT
        // row FIRST (PostgreSQL's DESC defaults to NULLS FIRST), which is what the desk relies on to
        // surface unapproved exits at the top. Transcribed as written.
        new QuerySpec
        {
            Key = "hr-separation-page",
            Title = "Exit desk - separation grid, first page",
            Source = "SeparationRepository.GetAll(t, s, c, page, status) - no status filter",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeseparation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeeseparation",
            IndexName = "ix_employeeseparation_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the scope triple plus two LEFT JOINs for the employee/department names and a CORRELATED sub-select onto `employeesettlement` for the amount - which `ix_employeesettlement_separationid` serves, once per rendered row. Tens of separations per campus.",
            Sql = @"
                SELECT es.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName, emp.EmployeeCode,
                       dept.Name AS DepartmentName, emp.JoiningDate,
                       (SELECT s.NetSettlementAmount FROM EmployeeSettlement s
                         WHERE s.SeparationId = es.Id ORDER BY s.Id DESC LIMIT 1) AS SettlementAmount
                FROM EmployeeSeparation es
                LEFT JOIN Employee emp ON es.EmployeeId = emp.Id
                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                WHERE es.tenantid = @tenantId AND es.schoolid = @schoolId AND es.campusid = @campusId
                ORDER BY es.approvedDate DESC, es.id DESC
                LIMIT @limit OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["limit"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "hr-separation-count",
            Title = "Exit desk - separation count (same round trip as the page)",
            Source = "SeparationRepository.GetAll (count half, issued with the page in ONE QueryMultiple)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM employeeseparation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeeseparation",
            IndexName = "ix_employeeseparation_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "not a free count: it carries the same two LEFT JOINs as the page, which is why it is measured rather than assumed.",
            Sql = @"
                SELECT COUNT(0) FROM EmployeeSeparation es
                LEFT JOIN Employee emp ON es.EmployeeId = emp.Id
                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                WHERE es.tenantid = @tenantId AND es.schoolid = @schoolId AND es.campusid = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hr-settlement-by-separation",
            Title = "Exit desk - the settlement of one separation",
            Source = "SeparationRepository.GetBySeparation(separationId) - keyed on the separation, not the scope",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeesettlement WHERE separationid = @separationId",
            MinVolume = 1,
            IndexTable = "employeesettlement",
            IndexName = "ix_employeesettlement_separationid",
            IndexColumns = "separationid",
            IndexRationale = "keyed on `es.SeparationId` alone, which `ix_employeesettlement_separationid` serves. The scope columns are NOT in the predicate, so a scope index could not help this read.",
            Sql = @"
                SELECT es.*,
                       emp.FirstName || ' ' || emp.LastName AS EmployeeName, emp.EmployeeCode,
                       dept.Name AS DepartmentName, emp.JoiningDate
                FROM EmployeeSettlement es
                LEFT JOIN Employee emp ON es.EmployeeId = emp.Id
                LEFT JOIN Department dept ON emp.DepartmentId = dept.Id
                WHERE es.SeparationId = @separationId",
            Params = new Dictionary<string, object> { ["separationId"] = v.SeparationId },
        },

        // =====================================================================
        // STUDENT PROFILE - the tabs of `student.create.html` and the term-result / settlement grids.
        //
        // Four of these are PER-STUDENT reads: the route is `.../{studentId}` and the repository
        // keys on the person the tab is showing rather than filtering by scope, which is why they
        // take their id from `ScopeVars` and print SKIP when the campus has no profile at all.
        //
        // ⚠️ THE CONTACT / HEALTH / IMMUNIZATION READS DO NOT ORDER, and two of them are
        // `FindAsync` over the scope predicate - so the SQL below is the statement SqlBuilder emits
        // (`SELECT * FROM <table> WHERE <predicate>`), not a tidied-up version of it.
        // =====================================================================
        new QuerySpec
        {
            Key = "student-guardian-by-student",
            Title = "Guardian tab - the guardians of one student",
            Source = "GuardianRepository.GetGuardianByStudentId(t, s, c, studentId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentguardian sg WHERE sg.studentid = @studentId",
            MinVolume = 1,
            IndexTable = "studentguardian",
            IndexName = "ix_sg_studentid",
            IndexColumns = "studentid",
            IndexRationale = "the JOIN drives from `StudentGuardian` on `sg.StudentId`, so the index the read wants is on that column rather than on `guardian`'s scope. `guardian.Nationality -> country.Id` is a primary-key lookup for the one label the grid prints.",
            Sql = @"
                select g.*, c.ShortName as CountryName, sg.* from Guardian g
                inner join StudentGuardian sg on g.Id = sg.GuardianId
                inner join Country c on g.Nationality = c.Id
                where g.TenantId = @tenantId and g.campusId = @campusId and sg.StudentId = @studentId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["studentId"] = v.StudentId,
            },
        },

        new QuerySpec
        {
            Key = "student-contact-by-student",
            Title = "Contacts tab - one student's emergency/pickup contacts",
            Source = "StudentContactRepository.GetStudentContactByStudentId(tenantId, campusId, studentId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentcontact WHERE studentid = @studentId",
            MinVolume = 1,
            IndexTable = "studentcontact",
            IndexName = "ix_sc_studentid",
            IndexColumns = "studentid",
            IndexRationale = "a `FindAsync` over (TenantId, CampusId, StudentId) with NO order - so the statement is `SELECT *` and the only thing that can make it selective is the studentid index. Tens of rows per student at most.",
            Sql = @"
                SELECT * FROM StudentContact
                WHERE TenantId = @tenantId AND CampusId = @campusId AND StudentId = @studentId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["studentId"] = v.StudentId,
            },
        },

        new QuerySpec
        {
            Key = "student-health-by-student",
            Title = "Health tab - one student's health record",
            Source = "StudentHealthRepository.GetStudentHealthByStudentId(tenantId, campusId, studentId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studenthealth WHERE studentid = @studentId",
            MinVolume = 1,
            IndexTable = "studenthealth",
            IndexName = "ix_sh_studentid",
            IndexColumns = "studentid",
            IndexRationale = "a `FirstOrDefaultAsync` on (TenantId, CampusId, StudentId). The Health tab renders exactly one record, so this read is bounded by the studentid index rather than by the campus.",
            Sql = @"
                SELECT * FROM StudentHealth
                WHERE TenantId = @tenantId AND CampusId = @campusId AND StudentId = @studentId
                LIMIT 1",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["studentId"] = v.StudentId,
            },
        },

        new QuerySpec
        {
            Key = "student-immunization-by-student",
            Title = "Immunization tab - one student's vaccinations",
            Source = "StudentImmunizationRepository.GetImmunizationByStudentId(tenantId, campusId, studentId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentimmunization WHERE studentid = @studentId",
            MinVolume = 1,
            IndexTable = "studentimmunization",
            IndexName = "ix_si_studentid",
            IndexColumns = "studentid",
            IndexRationale = "the same `FindAsync` shape as the contacts read; the projection carries the `fileproof` bytea, so the row width - not the row count - is what makes this one worth measuring.",
            Sql = @"
                SELECT * FROM StudentImmunization
                WHERE TenantId = @tenantId AND CampusId = @campusId AND StudentId = @studentId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["studentId"] = v.StudentId,
            },
        },

        ScopeGrid(v, "student-term-result-page", "Term result grid - first page",
            "StudentTermResultRepository.GetAll(page, t, s, c) - the generic single-table page",
            "StudentTermResult", Scope3,
            "SELECT COUNT(*) FROM studenttermresult WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId"),

        // ⚠️ THE SETTLEMENT GRID'S COUNT IS A SECOND ROUND TRIP (`QuerySingleAsync` AFTER the page
        // has already been read), and it carries the SAME `INNER JOIN student` - so the user waits for
        // both and each gets its own spec.
        new QuerySpec
        {
            Key = "student-settlement-history-page",
            Title = "Student account history - settlement grid, first page",
            Source = "StudentSettlementRepository.GetSettlements(t, s, c, page) - no search",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentsettlement WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "studentsettlement",
            IndexName = "ix_ss_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "the scope triple plus an `INNER JOIN student` for the name and admission number, and `ORDER BY ss.settlementdate DESC LIMIT n`. The join is a primary-key lookup because `ss.studentid = s.id` is the join key.",
            Sql = @"
                SELECT ss.id, ss.settlementnumber, ss.studentid, ss.studentenrollmentid, ss.settlementdate,
                       ss.totalcharges, ss.totaldiscounts, ss.totaltax, ss.totalpayments, ss.totalcredits,
                       ss.totalrefunds, ss.totaladjustments, ss.outstandingamount, ss.creditamount,
                       ss.status, ss.remarks, ss.tenantid, ss.schoolid, ss.campusid,
                       ss.createdon, ss.modifiedon,
                       s.id AS stdId, s.id AS Id, s.name, s.admissionnumber
                FROM StudentSettlement ss
                INNER JOIN Student s ON ss.studentid = s.id
                WHERE ss.tenantid = @tenantId AND ss.schoolid = @schoolId AND ss.campusid = @campusId
                ORDER BY ss.settlementdate DESC
                LIMIT @limit OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["limit"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "student-settlement-history-count",
            Title = "Student account history - settlement count (its own round trip)",
            Source = "StudentSettlementRepository.GetSettlements (count half, a SECOND round trip)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM studentsettlement WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "studentsettlement",
            IndexName = "ix_ss_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale = "not a free count: it carries the same `INNER JOIN student` as the page, and the repository issues it AFTER the page - so the user waits for the page and then this.",
            Sql = @"
                SELECT COUNT(ss.id)
                FROM StudentSettlement ss
                INNER JOIN Student s ON ss.studentid = s.id
                WHERE ss.tenantid = @tenantId AND ss.schoolid = @schoolId AND ss.campusid = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // =====================================================================
        // THE CURRICULUM TOPIC PLAN — an `N`-LOOKUP shape, not a page.
        //
        // ⚠️ `curriculumtopicplan` HAS NO SCOPE COLUMNS AT ALL, so `ScopeGrid` cannot express it:
        // it hangs off `curriculumtopicid` and reaches a campus only through
        // `curriculumgradesubjecttopic -> curriculumgradesubject -> curriculumgrade`. That is also
        // why its `VolumeSql` counts the whole table — a scope-filtered count would be a
        // 42703.
        //
        // ⚠️ THE SPEC MODELS A CORRELATED SUBQUERY ON PURPOSE. `GetByTopic` is called ONCE PER
        // TOPIC, so a screen's total read is N single-row lookups. A single statement cannot issue
        // N round trips, but a correlated subquery gets the planner to the SAME place: one subplan
        // execution per outer row (`loops=1051`), which is exactly what the per-topic reads do.
        // Modelling it as one joined statement instead would be modelling a query the application
        // NEVER ISSUES — and that shape is not slow at all (see the rationale below).
        //
        // ⚠️ AND THE CATALOGUE'S OWN VERDICT WOULD **NOT** HAVE CAUGHT THIS — READ THIS BEFORE
        // TRUSTING THE ROW AS A GUARD. The as-shipped reading is 144.98 ms against this 150 ms
        // budget: a MARGINAL PASS, not a FAIL. The finding came from a direct rollback-scoped probe
        // of the two plan shapes, not from a red row.
        //
        // ⚠️ SO THIS SPEC'S GUARD IS **WEAK AND I WILL NOT PRETEND OTHERWISE.** If the index is ever
        // dropped it returns to ~145 ms, which still sits UNDER this budget on a quiet machine — the
        // row would go green again on an index-less database. (The budget is left at the repo's own
        // stated interactive bar rather than lowered to manufacture a red row; tuning a budget so a
        // finding appears is the same mistake as tuning one so it disappears.) The authoritative
        // record of this defect is `V137`'s measurement block, and the assertion that would actually
        // catch a regression is a plan assertion (`Index Cond: (curriculumtopicid = ...)`), not a
        // timing one. What the spec does give is that the shape is MEASURED at all, on real data,
        // in every catalogue run — which is the `specs x data` gap this pass exists to close.
        // =====================================================================
        new QuerySpec
        {
            Key = "curriculum-topic-plan-lookup",
            Title = "Curriculum topic plans - the per-topic lookup, issued once per topic",
            Source = "CurriculumTopicPlanRepository.GetByTopic (one call per topic - modelled as a correlated subquery so the planner sees the same N executions)",
            P95BudgetMs = 150,
            VolumeSql = "SELECT COUNT(*) FROM curriculumtopicplan",
            MinVolume = 1000,
            IndexTable = "curriculumtopicplan",
            // ⚠️ THE INDEX THIS SPEC ASKS FOR ALREADY EXISTS — it is `V137`, shipped from this very
            // measurement. `--advise` reporting "already present" is the expected answer here, not a
            // failure to probe; the spec remains the regression guard and the record of the shape.
            IndexName = "ix_curriculumtopicplan_curriculumtopicid",
            IndexColumns = "curriculumtopicid",
            IndexRationale =
                "a missing foreign-key index on a child table the app looks up once per parent row. " +
                "WITHOUT IT the planner walks `curriculumtopicplan_pkey` in `id` order and filters on " +
                "`curriculumtopicid` (`ORDER BY id LIMIT 1` is what pins it to PK order), discarding " +
                "~1050 rows PER LOOKUP - measured 144.98 ms and 20,922 buffers over 1,051 topics. " +
                "WITH IT each lookup is an `Index Cond` returning its own 2 rows: 4.553 ms, 3,153 " +
                "buffers - ~32x faster and 6.6x less buffer traffic. " +
                "⚠️ `termid` (the table's OTHER foreign key, also ON DELETE CASCADE) was probed and " +
                "REJECTED - its RI check reads 2.623 -> 2.151 ms, a wash, because `count(distinct " +
                "termid) = 1` on this data. Do not add it for symmetry.",
            Sql = @"
                SELECT t.id,
                       (SELECT p.id FROM curriculumtopicplan p
                         WHERE p.curriculumtopicid = t.id
                         ORDER BY p.id
                         LIMIT 1) AS planId
                FROM curriculumgradesubjecttopic t",
            Params = new Dictionary<string, object>(),
        },

        // =====================================================================
        // THE COMMUNICATION WORKSPACE - homework, moments, the event response tables, teacher and
        // meetings. Thirteen specs over sixteen tables that held ZERO ROWS in every database here.
        //
        // ⚠️ WHY THEY WERE ALL EMPTY AT ONCE: `homework.teacherid` and `moment.teacherid` are NOT
        // NULL against `teacher`, and `teacher` was empty on every perf campus DELIBERATELY (the HR
        // seeder writes designations with `canteach = false`, and says so). One missing prerequisite,
        // sixteen SKIP rows. `data-volume/Seeders/CommunicationWorkspaceSeeder` owns them now.
        //
        // ⚠️ THE CHILD TABLES CARRY NO SCOPE COLUMNS. `homeworkstudent`, `homeworksubmission`,
        // `homeworkattachment`, `homeworksubmissionattachment`, `homeworksubmissioncomment`,
        // `momentstudent`, `momentattachment`, `momentcomment`, `schooleventread`,
        // `schooleventresponse`, `schooleventaudience` and `schooleventattachment` have neither
        // tenant/school/campus nor any way to express one, so the scope-augmented `ScopeGrid` factory
        // can only ever be a 42703 here. Two shapes are used instead and neither is a compromise:
        //
        //   * the app reaches them through their PARENT after it has paged ("SELECT * FROM
        //     homeworkstudent WHERE HomeworkId = ANY(@HomeworkIds)"), which is modelled as the SAME
        //     work - a semi-join to the paged parent set - because a single statement cannot issue
        //     the N+1 round trips the page actually makes, and the semi-join drives the identical
        //     index over the identical id list;
        //   * the event tables are keyed on the EVENT the user opened, so `@schoolEventId` (resolved
        //     by `db-report` to the campus's busiest event) stands in for the click.
        // =====================================================================

        new QuerySpec
        {
            Key = "comm-teacher-list",
            Title = "Teacher list - the three-way join the teacher grid's WHERE clause is",
            Source = "TeacherRepository.GetAll(t, s, c) - unpaged, unbounded",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM teacher WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "teacher",
            IndexName = "(none - the read is a three-way join over a table that holds TENS of rows per campus)",
            IndexColumns = "",
            IndexRationale =
                "⚠️ `teacher` is a STAFF table: a real school employs tens of teachers, not thousands, so a " +
                "scope-column index cannot be the finding here and none is proposed. The spec exists because " +
                "the read is the app's THREE-WAY contract and a break in it is silent: `TeacherRepository.GetAll` " +
                "inner-joins `users` ON the SAME scope triple AND `userrole` ON the teacher role, so a teacher row " +
                "whose login carries a different scope, or whose login has no Teacher grant, exists and is " +
                "invisible to the grid that lists teachers. Measured 15 rows on campus 15; the shape is what is " +
                "being measured, and the dataset fixture asserts the join resolves.",
            Sql = @"
                SELECT t.Id, t.Nic, t.Address, t.MaritalStatus, t.Religion, t.Degree, t.Photo, t.Dob,
                       t.Qualification, t.TeachingLicense, t.YearsExperience, t.Specialization, t.Biography, t.Notes,
                       t.EmployeeId, e.EmployeeCode, e.JoiningDate, e.EmploymentStatus, e.IsActive,
                       dept.Name AS DepartmentName, desig.Name AS DesignationName,
                       u.Email, u.Password, u.Password AS ConfirmPassword,
                       u.FirstName, u.LastName, u.Mobile, u.Id AS UserId,
                       t.TenantId, t.SchoolId, t.CampusId
                FROM Teacher t
                INNER JOIN Users u ON t.UserId = u.Id AND t.TenantId = u.TenantId AND t.SchoolId = u.SchoolId AND t.CampusId = u.CampusId
                INNER JOIN UserRole ur ON u.Id = ur.UserId AND ur.RoleId = 4
                LEFT JOIN Employee e ON t.EmployeeId = e.Id
                LEFT JOIN Department dept ON e.DepartmentId = dept.Id
                LEFT JOIN Designation desig ON e.DesignationId = desig.Id
                WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                ORDER BY u.FirstName",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "comm-homework-page",
            Title = "Homework grid - the classroom/term page the teacher screen opens on",
            Source = "HomeworkRepository.GetAllHomeworkByFilters - GET homework/classroom/{id}",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM homework WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "homework",
            IndexName = "ix_homework_tenantschoolcampus_classroom",
            IndexColumns = "tenantid, schoolid, campusid, classroomid",
            IndexRationale =
                "the scope triple is a WEAK prefix here - a campus owns every one of its homework rows, so " +
                "`tenantid, schoolid, campusid` narrows to the whole set. The classroom is the selective " +
                "column, and the controller ALWAYS supplies it (the route is " +
                "`homework/classroom/{classroomId}`), so the composite must lead with the scope triple and " +
                "end with `classroomid`. The year+term filter follows from the classroom.",
            Sql = @"
                SELECT h.id, h.title, h.description, h.classroomid, h.teacherid,
                       u.firstname || ' ' || u.lastname AS teachername,
                       h.subjectid, s.name AS subjectname,
                       h.duedate, h.iswholeclassroom, h.isstudentaddattachment, h.tenantid, h.campusid,
                       h.createdon, h.modifiedon, h.termid, h.academicyearid
                FROM Homework h
                INNER JOIN teacher t ON h.teacherid = t.id
                INNER JOIN users u ON t.userid = u.id
                LEFT JOIN subject s ON h.subjectid = s.id
                WHERE h.TenantId = @tenantId
                  AND h.campusId = @campusId
                  AND h.ClassroomId = @classroomId
                  AND h.AcademicYearId = @academicYearId
                  AND h.TermId = @termId
                  AND (@teacherId = 0 OR h.TeacherId = @teacherId)
                  AND (@subjectId = 0 OR h.SubjectId = @subjectId)
                  AND (@fromDate::timestamp IS NULL OR h.CreatedOn >= @fromDate::timestamp)
                  AND (@toDate::timestamp IS NULL OR h.CreatedOn <= @toDate::timestamp)
                  -- ⚠️ `::boolean` IS THE CATALOGUE'S BINDING, NOT A CHANGE OF MEANING, and it is
                  -- REQUIRED here for the same reason `@fromDate::timestamp` is required above: the
                  -- runner binds an UNTYPED null, and `@isDuePassed IS NULL` gives PostgreSQL nothing to
                  -- infer from (`42P08: could not determine data type of parameter $10`, measured at
                  -- this exact statement). The application has no such problem because it passes a
                  -- `bool?` - the CLR type IS the type information. The cast supplies the same type the
                  -- app's parameter already carries, so the plan is the plan the screen asks for.
                  AND (@isDuePassed::boolean IS NULL
                       OR (@isDuePassed::boolean = true AND h.DueDate < NOW() AT TIME ZONE 'UTC')
                       OR (@isDuePassed::boolean = false AND h.DueDate >= NOW() AT TIME ZONE 'UTC'))
                ORDER BY h.DueDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["teacherId"] = 0L,
                ["subjectId"] = 0L,
                // ⚠️ `null` RATHER THAN `DBNull.Value`. The runner hands this bag to Dapper, and
                // Dapper rejects a dictionary whose VALUE is already `DBNull` ("The member fromDate of
                // type System.DBNull cannot be used as a parameter value", measured) - it converts a
                // plain `null` to `DBNull` itself. The casts inside the SQL (`@fromDate::timestamp`) are
                // what give PostgreSQL the type to infer, so a typeless null is safe here and is the
                // same value the controller passes when the screen sends no date filter.
                ["fromDate"] = null!,
                ["toDate"] = null!,
                ["isDuePassed"] = null!,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        //
        // ⚠️ THE FOLLOW-UP READS ARE MODELLED AS SEMI-JOINS TO THE PARENT PAGE, NOT AS ARRAY PARAMS.
        // The app pages 50 homeworks and then issues `WHERE HomeworkId = ANY(@HomeworkIds)` with those
        // 50 ids bound as a PostgreSQL array. A catalogue entry cannot have per-run ids, and binding
        // an array would change nothing the planner sees anyway - `IN (<subquery LIMIT 50>)` drives the
        // SAME index over the SAME id list, which is the whole of what this spec is measuring. The
        // shapes below are the application's own (projections, joins and ORDER BY verbatim); only the
        // id source is substituted, and it is substituted for the one thing that makes the ids real.
        //
        new QuerySpec
        {
            Key = "comm-homework-roll",
            Title = "Homework roll - the assigned students, fetched for the page just rendered",
            Source = "HomeworkRepository.GetAllHomeworkByFilters -> `SELECT * FROM HomeworkStudent WHERE HomeworkId = ANY(@HomeworkIds)`",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM homeworkstudent hs JOIN homework h ON h.id = hs.homeworkid WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "homeworkstudent",
            IndexName = "ix_homeworkstudent_homeworkid",
            IndexColumns = "homeworkid",
            IndexRationale =
                "`homeworkstudent` has NO scope columns and is never paged on its own - it is fetched by the " +
                "ids of the page that was just rendered, so the only column the query can use is `homeworkid`. " +
                "Without an index on it the fetch seq-scans the whole table once per page (2,400 rows on " +
                "campus 15, and every campus's rows intermixed, since the table is not scoped).",
            Sql = @"
                SELECT hs.*
                FROM homeworkstudent hs
                WHERE hs.homeworkid IN (
                        SELECT h.id FROM homework h
                         WHERE h.tenantid = @tenantId AND h.campusid = @campusId
                           AND h.classroomid = @classroomId AND h.academicyearid = @academicYearId
                           AND h.termid = @termId
                         ORDER BY h.id
                         LIMIT @pageSize)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["pageSize"] = DefaultPageSize,
            },
        },

        new QuerySpec
        {
            Key = "comm-homework-attachments",
            Title = "Homework attachments - fetched for the page just rendered, with both file joins",
            Source = "HomeworkRepository.GetAllHomeworkByFilters -> the two LEFT JOIN AttachmentFile reads",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM homeworkattachment ha JOIN homework h ON h.id = ha.homeworkid WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "homeworkattachment",
            IndexName = "ix_homeworkattachment_homeworkid",
            IndexColumns = "homeworkid",
            IndexRationale =
                "the same shape as the roll: the table has no scope columns and is reached by the ids of the " +
                "page just rendered, so `homeworkid` is the only usable column. The two LEFT JOINs onto " +
                "`attachmentfile` are primary-key lookups.",
            Sql = @"
                SELECT ha.*,
                       af1.id AS HomeworkAttachmentFileId, af1.filename AS HomeworkFileName,
                       af1.filepath AS HomeworkFilePath, af1.filetype AS HomeworkFileType,
                       af1.entitytype AS HomeworkFileEntityType,
                       af2.id AS VideoAttachmentFileId, af2.filename AS VideoFileName,
                       af2.filepath AS VideoFilePath, af2.filetype AS VideoFileType,
                       af2.entitytype AS VideoFileEntityType
                FROM HomeworkAttachment ha
                LEFT JOIN AttachmentFile af1 ON af1.id = ha.HomeworkAttachmentFileId
                LEFT JOIN AttachmentFile af2 ON af2.id = ha.VideoAttachmentFileId
                WHERE ha.HomeworkId IN (
                        SELECT h.id FROM homework h
                         WHERE h.tenantid = @tenantId AND h.campusid = @campusId
                           AND h.classroomid = @classroomId AND h.academicyearid = @academicYearId
                           AND h.termid = @termId
                         ORDER BY h.id
                         LIMIT @pageSize)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["pageSize"] = DefaultPageSize,
            },
        },

        new QuerySpec
        {
            Key = "comm-homework-submissions",
            Title = "Homework grading desk - the whole classroom joined to its submissions",
            Source = "HomeworkSubmissionRepository.GetStudentHomeworksByHomeworkId(homeworkId)",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM homeworksubmission hs JOIN homework h ON h.id = hs.homeworkid WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "homeworksubmissionattachment",
            IndexName = "ix_homeworksubmissionattachment_submissionid",
            IndexColumns = "homeworksubmissionid",
            IndexRationale =
                "⚠️ THE COST IS NOT THE JOIN - IT IS THE CORRELATED SUB-SELECTS, AND ONE OF THE THREE IS " +
                "THE WHOLE QUERY. `EXPLAIN (ANALYZE)` attributes it exactly: `SubPlan 2` is the " +
                "`AttachmentCount` COUNT over `homeworksubmissionattachment`, and it runs **once per " +
                "eligible student** (`loops=2000`), each loop a `Seq Scan on homeworksubmissionattachment` " +
                "- ~0.087 ms x 2000 = 174 ms of the 194 ms total. The two `EXISTS` over " +
                "`homeworksubmissioncomment` are NOT the problem: PostgreSQL hoists both into hashed " +
                "subplans that run ONCE (`loops=1`, 0.5 and 0.25 ms). So the index that matters is on the " +
                "ATTACHMENT table's submission id, which is what this spec declares; the comment table's " +
                "own index is declared by `comm-submission-comments`. " +
                "⚠️ TWO CANDIDATES WERE PROBED AND REJECTED FIRST, both because they were the obvious " +
                "answer rather than the measured one: `homeworksubmission(homeworkid, studentid)` for the " +
                "LEFT JOIN (562 -> 575 ms, 1.0x - the join is a hash join over 1,500 rows) and " +
                "`homeworksubmissioncomment(homeworksubmissionid)` (538 -> 572 ms, 0.9x - the planner " +
                "never used it, because those two sub-selects were already hoisted).",
            // ⚠️ `@homeworkId` IS THE APPLICATION'S OWN PARAMETER and the statement below names it
            // three times, exactly as the repository does. The catalogue cannot be handed a per-run
            // id, so the CTE `target` resolves the same thing the screen's click does - the campus's
            // first whole-classroom homework - and every use of the parameter becomes
            // `(SELECT id FROM target)`. The row source, the joins, the projections and the ORDER BY
            // are the repository's verbatim; only the SOURCE of one id is substituted.
            Sql = @"
                WITH target AS (
                    SELECT h.id
                      FROM homework h
                     WHERE h.tenantid = @tenantId AND h.campusid = @campusId
                       AND h.classroomid = @classroomId AND h.academicyearid = @academicYearId
                       AND h.termid = @termId AND h.iswholeclassroom = TRUE
                     ORDER BY h.id
                     LIMIT 1
                ),
                eligibleStudents AS (
                    SELECT s.id AS studentId, s.name, s.photo, h.id AS homeworkId
                      FROM student s
                      INNER JOIN StudentEnrollment cs ON cs.studentid = s.id
                      INNER JOIN homework h ON h.classroomid = cs.classroomid
                     WHERE h.id = (SELECT id FROM target) AND h.iswholeclassroom = TRUE
                    UNION
                    SELECT s.id AS studentId, s.name, s.photo, hst.homeworkid
                      FROM homeworkstudent hst
                      INNER JOIN student s ON s.id = hst.studentid
                     WHERE hst.homeworkid = (SELECT id FROM target)
                )
                SELECT es.studentId, es.name, es.photo, es.homeworkid, hs.id, hs.createdon,
                       COALESCE(hs.homeworkstatus, 0) AS homeworkstatus,
                       (SELECT COUNT(*) FROM homeworksubmissionattachment hsa
                         WHERE hsa.homeworksubmissionid = hs.id) AS AttachmentCount,
                       EXISTS(SELECT 1 FROM homeworksubmissioncomment hsc
                               WHERE hsc.homeworksubmissionid = hs.id AND hsc.comment NOT LIKE 'http%') AS HasTextFeedback,
                       EXISTS(SELECT 1 FROM homeworksubmissioncomment hsc
                               WHERE hsc.homeworksubmissionid = hs.id AND hsc.comment LIKE 'http%') AS HasVoiceFeedback
                  FROM eligibleStudents es
                  LEFT JOIN homeworksubmission hs ON hs.studentid = es.studentId AND hs.homeworkid = (SELECT id FROM target)
                 ORDER BY es.name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
            },
        },

        new QuerySpec
        {
            Key = "comm-submission-comments",
            Title = "Submission comments - the paged feedback thread of one submission",
            Source = "HomeworkSubmissionCommentRepository.GetAll(page, homeworkSubmissionId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM homeworksubmissioncomment c JOIN homeworksubmission hs ON hs.id = c.homeworksubmissionid JOIN homework h ON h.id = hs.homeworkid WHERE h.tenantid = @tenantId AND h.schoolid = @schoolId AND h.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "homeworksubmissioncomment",
            IndexName = "ix_homeworksubmissioncomment_submissionid",
            IndexColumns = "homeworksubmissionid",
            IndexRationale =
                "`GenericRepository.GetAllAsync(page, c => c.HomeworkSubmissionId == id)` on a table with no " +
                "scope columns, so the predicate is the submission id alone - and the comment thread is " +
                "opened from a row the previous page rendered. A missing index is a seq scan of the whole " +
                "table per thread.",
            Sql = @"
                SELECT c.*
                FROM homeworksubmissioncomment c
                WHERE c.homeworksubmissionid = (
                        SELECT hs.id
                          FROM homeworksubmission hs
                          JOIN homework h ON h.id = hs.homeworkid
                         WHERE h.tenantid = @tenantId AND h.campusid = @campusId
                           AND h.classroomid = @classroomId AND h.academicyearid = @academicYearId
                           AND h.termid = @termId
                         ORDER BY hs.id
                         LIMIT 1)
                ORDER BY c.id
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "comm-moment-page",
            Title = "Moments grid - the classroom/term page the teacher screen opens on",
            Source = "MomentRepository.GetAllMomentsByFilters",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM moment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "moment",
            IndexName = "ix_moment_tenantschoolcampus_classroom",
            IndexColumns = "tenantid, schoolid, campusid, classroomid",
            IndexRationale =
                "the same shape as the homework grid and for the same reason: the campus scope is not selective " +
                "(a campus owns all of its moments), while `classroomid` always comes from the route. The " +
                "moment grid additionally inner-joins `momenttype` and `teacher`+`users`, both primary-key " +
                "lookups.",
            Sql = @"
                SELECT mt.name AS MomemtType, m.id, m.payloadjson, m.status, m.classroomid, m.teacherid,
                       u.firstname || ' ' || u.lastname AS teachername,
                       m.subjectid, s.name AS subjectname,
                       m.tenantid, m.campusid, m.iswholeclassroom,
                       m.createdon, m.modifiedon, m.termid, m.academicyearid, m.momenttypeid, m.createdon AS CreatedAt
                FROM Moment m
                INNER JOIN momenttype mt ON m.momenttypeid = mt.id
                INNER JOIN teacher t ON m.teacherid = t.id
                INNER JOIN users u ON t.userid = u.id
                LEFT JOIN subject s ON m.subjectid = s.id
                WHERE m.TenantId = @tenantId
                  AND m.campusId = @campusId
                  AND m.ClassroomId = @classroomId
                  AND m.AcademicYearId = @academicYearId
                  AND m.TermId = @termId
                  AND (@teacherId = 0 OR m.TeacherId = @teacherId)
                  AND (@subjectId = 0 OR m.SubjectId = @subjectId)
                  AND (@fromDate::timestamp IS NULL OR m.CreatedOn >= @fromDate::timestamp)
                  AND (@toDate::timestamp IS NULL OR m.CreatedOn <= @toDate::timestamp)
                ORDER BY m.CreatedOn DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["teacherId"] = 0L,
                ["subjectId"] = 0L,
                ["fromDate"] = null!,
                ["toDate"] = null!,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "comm-moment-roll",
            Title = "Moment roll - the students a moment was posted to, fetched for the rendered page",
            Source = "MomentRepository.GetAllMomentsByFilters -> `SELECT * FROM MomentStudent WHERE MomentId = ANY(@MomentIds)`",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM momentstudent ms JOIN moment m ON m.id = ms.momentid WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "momentstudent",
            IndexName = "ix_momentstudent_momentid",
            IndexColumns = "momentid",
            IndexRationale =
                "the moment-side twin of `comm-homework-roll`: no scope columns, never paged on its own, " +
                "reached only by the ids of the page just rendered, so `momentid` is the only usable column.",
            Sql = @"
                SELECT ms.*
                FROM momentstudent ms
                WHERE ms.momentid IN (
                        SELECT m.id FROM moment m
                         WHERE m.tenantid = @tenantId AND m.campusid = @campusId
                           AND m.classroomid = @classroomId AND m.academicyearid = @academicYearId
                           AND m.termid = @termId
                         ORDER BY m.id
                         LIMIT @pageSize)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["pageSize"] = DefaultPageSize,
            },
        },

        new QuerySpec
        {
            Key = "comm-moment-attachments",
            Title = "Moment attachments - fetched for the rendered page, with both file joins",
            Source = "MomentRepository.GetAllMomentsByFilters -> the two LEFT JOIN AttachmentFile reads",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM momentattachment ma JOIN moment m ON m.id = ma.momentid WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "momentattachment",
            IndexName = "ix_momentattachment_momentid",
            IndexColumns = "momentid",
            IndexRationale =
                "same shape again: reached by the rendered page's moment ids, `momentid` is the only usable " +
                "column, and the two `attachmentfile` joins are primary-key lookups.",
            Sql = @"
                SELECT ma.*,
                       af1.id AS MomentAttachmentFileId, af1.filename AS MomentFileName,
                       af2.id AS MomentVideoThumbnailFileId, af2.filename AS ThumbnailFileName
                FROM MomentAttachment ma
                LEFT JOIN AttachmentFile af1 ON af1.id = ma.MomentAttachmentFileId
                LEFT JOIN AttachmentFile af2 ON af2.id = ma.MomentVideoThumbnailFileId
                WHERE ma.MomentId IN (
                        SELECT m.id FROM moment m
                         WHERE m.tenantid = @tenantId AND m.campusid = @campusId
                           AND m.classroomid = @classroomId AND m.academicyearid = @academicYearId
                           AND m.termid = @termId
                         ORDER BY m.id
                         LIMIT @pageSize)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["pageSize"] = DefaultPageSize,
            },
        },

        new QuerySpec
        {
            Key = "comm-moment-feedback",
            Title = "Moment feedback - the single earliest comment of the rendered page",
            Source = "MomentRepository.GetAllMomentsByFilters -> `SELECT * FROM MomentComment WHERE MomentId = ANY(@MomentIds) ORDER BY createdon ASC LIMIT 1`",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM momentcomment mc JOIN moment m ON m.id = mc.momentid WHERE m.tenantid = @tenantId AND m.schoolid = @schoolId AND m.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "momentcomment",
            IndexName = "ix_momentcomment_momentid",
            IndexColumns = "momentid",
            IndexRationale =
                "⚠️ THE STATEMENT IS REPRODUCED VERBATIM, INCLUDING ITS `LIMIT 1`, AND THAT LIMIT IS THE " +
                "APPLICATION'S OWN AWAITING-A-DECISION SHAPE - it is not the spec's invention. `ORDER BY " +
                "createdon ASC LIMIT 1` over `MomentId = ANY(@MomentIds)` returns ONE comment for the WHOLE " +
                "page of moments, not one per moment, and the caller then looks it up per row " +
                "(`commentLookup[c.MomentId]`), so every moment except one gets no feedback. Reproducing it " +
                "exactly is what makes the spec a measurement; changing it here would measure a query the " +
                "application never issues. The index the statement needs is `momentid` (for the id list) with " +
                "`createdon` to satisfy the ordering and the limit without a sort of the whole set.",
            Sql = @"
                SELECT * FROM MomentComment
                WHERE MomentId IN (
                        SELECT m.id FROM moment m
                         WHERE m.tenantid = @tenantId AND m.campusid = @campusId
                           AND m.classroomid = @classroomId AND m.academicyearid = @academicYearId
                           AND m.termid = @termId
                         ORDER BY m.id
                         LIMIT @pageSize)
                ORDER BY createdon ASC
                LIMIT 1",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["campusId"] = v.CampusId,
                ["classroomId"] = v.ClassroomId,
                ["academicYearId"] = v.AcademicYearId,
                ["termId"] = v.TermId,
                ["pageSize"] = DefaultPageSize,
            },
        },

        new QuerySpec
        {
            Key = "comm-event-responses-page",
            Title = "Event detail - the paged responses of one event",
            Source = "SchoolEventResponseRepository.GetByEventIdPaged - the event detail DataTable",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM schooleventresponse r JOIN schoolevent e ON e.id = r.eventid WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "schooleventresponse",
            IndexName = "ix_schooleventresponse_eventid",
            IndexColumns = "eventid",
            IndexRationale =
                "`schooleventresponse` has NO scope columns and is keyed on the event the user opened, so " +
                "`eventid` is the only usable column. Without it the page seq-scans the whole table - and the " +
                "table is GLOBAL (every campus's responses are in it), so the scan grows with the deployment, " +
                "not with the campus.",
            Sql = @"
                SELECT r.*, s.name AS studentname
                FROM SchoolEventResponse r
                INNER JOIN Student s ON s.Id = r.StudentId
                WHERE r.EventId = @schoolEventId
                ORDER BY r.ResponseDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.SchoolEventId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "comm-event-reads",
            Title = "Event read receipts - the readers of one event",
            Source = "SchoolEventReadRepository.GetByEventId (FindAsync, ORDER BY ReadOn)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM schooleventread r JOIN schoolevent e ON e.id = r.eventid WHERE e.tenantid = @tenantId AND e.schoolid = @schoolId AND e.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "schooleventread",
            IndexName = "ix_schooleventread_eventid",
            IndexColumns = "eventid",
            IndexRationale =
                "the read receipt list has no scope columns either - it is reached by `EventId` alone, twice " +
                "(`GetByEventId` for the list and `GetReadCount` for the tile next to it), so `eventid` is the " +
                "only usable column and the table is global.",
            Sql = @"
                SELECT * FROM SchoolEventRead
                WHERE EventId = @schoolEventId
                ORDER BY ReadOn DESC",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.SchoolEventId,
            },
        },

        // ------------------------------------------------------------------
        // EVENT FINANCE - the charges, their audience and the per-student charges raised from them.
        //
        // ⚠️ ALL THREE TABLES HELD ZERO ROWS IN EVERY DATABASE HERE while three shipped screens read
        // them, so these specs would have reported SKIP. `EventFinanceSeeder` / the event-finance
        // dataset fixture filled them on the campus the tool measures.
        //
        // ⚠️ EVERY ONE OF THESE READS IS KEYED ON THE EVENT THE USER OPENED (`@schoolEventId`), not on
        // the scope triple - that is the repository's own shape (`WHERE ec.eventid = @EventId`). An
        // event-scoped read has no scope columns in its predicate, so the event id is the only usable
        // key and the table is global.
        //
        // ⚠️ THE `@schoolEventId` BOUND HERE IS `ScopeVars.EventFinanceEventId`, NOT THE RESPONSE
        // SCREENS' `SchoolEventId` - see that property's own doc comment. The two families page
        // DIFFERENT events on purpose (the busiest by responses vs the one carrying the charges), and
        // on the perf dataset those are not the same row. The parameter keeps the repository's own
        // name because that is what the SQL says; only which event it resolves to differs.
        // ------------------------------------------------------------------
        new QuerySpec
        {
            Key = "evtfin-charge-list",
            Title = "Event finance - the charges of one event, in their display order",
            Source = "EventChargeRepository.GetByEventId - the charges grid",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM eventcharge WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "eventcharge",
            IndexName = "ix_eventcharge_eventid",
            IndexColumns = "eventid",
            IndexRationale =
                "the read is BY EVENT, so `eventid` is the only usable column - the scope triple never appears " +
                "in the predicate. `ix_eventcharge_eventid` is deployed and serves it.",
            Sql = @"
                SELECT ec.*, ft.name AS feetypename, tc.name AS taxcodename,
                       CASE ec.status
                         WHEN 1 THEN 'Draft' WHEN 2 THEN 'Open' WHEN 3 THEN 'Approved' WHEN 4 THEN 'Rejected'
                         ELSE ec.status::text
                       END AS statusname
                  FROM EventCharge ec
                  LEFT JOIN FeeType ft ON ec.feetypeid = ft.id
                  LEFT JOIN TaxCode tc ON ec.taxcodeid = tc.id
                 WHERE ec.eventid = @schoolEventId
                 ORDER BY ec.sortorder, ec.id",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-charge-invoicing",
            Title = "Event finance - the charges eligible for invoicing (approved, non-zero)",
            Source = "EventChargeRepository.GetForInvoicing - what the invoice generator consumes",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM eventcharge WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND amount > 0 AND status = 3",
            MinVolume = 1,
            IndexTable = "eventcharge",
            IndexName = "ix_eventcharge_eventid",
            IndexColumns = "eventid",
            IndexRationale =
                "same access path as the charges grid - `eventid` - with `amount > 0 AND status = Approved` " +
                "filtering the rows the query already reached. `ix_eventcharge_approvalstatus` and " +
                "`ix_eventcharge_status` are deployed too, but neither is selective enough to lead when the " +
                "read is bound to one event, and the planner has no reason to prefer them.",
            Sql = @"
                SELECT ec.*, ft.name AS feetypename, tc.name AS taxcodename
                  FROM EventCharge ec
                  LEFT JOIN FeeType ft ON ec.feetypeid = ft.id
                  LEFT JOIN TaxCode tc ON ec.taxcodeid = tc.id
                 WHERE ec.eventid = @schoolEventId AND ec.amount > 0
                   AND ec.status = @approved
                 ORDER BY ec.sortorder, ec.id",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
                ["approved"] = (short)3,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-charge-status-counts",
            Title = "Event finance - the four status tiles' counts for one event",
            Source = "EventChargeRepository.GetStatusCounts - the screen's status tiles and tabs",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM eventcharge WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "eventcharge",
            IndexName = "ix_eventcharge_eventid",
            IndexColumns = "eventid",
            IndexRationale =
                "five aggregates over ONE event's charges - the same `eventid` key as the grid, so the same " +
                "index. A count-only read cannot be served by an index-only scan on a table this small, and " +
                "adding a covering index for it would be a second btree for no measurable gain.",
            Sql = @"
                SELECT
                  COUNT(*) AS total,
                  COUNT(CASE WHEN status = 1 THEN 1 END) AS draft,
                  COUNT(CASE WHEN status = 2 THEN 1 END) AS open,
                  COUNT(CASE WHEN status = 3 THEN 1 END) AS approved,
                  COUNT(CASE WHEN status = 4 THEN 1 END) AS rejected
                FROM eventcharge
                WHERE eventid = @schoolEventId",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-participant-count",
            Title = "Event detail - the participant total behind the pager",
            Source = "EventParticipantRepository.GetByEventIdPaged - the count half of the round trip",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM eventparticipant WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IsScalar = true,
            IndexTable = "eventparticipant",
            IndexName = "ux_eventparticipant_eventenrollment",
            IndexColumns = "eventid, studentenrollmentid",
            IndexRationale =
                "⚠️ THE COUNT HALF IS ITS OWN ROUND TRIP (the repository issues `ExecuteScalarAsync(countQuery)` " +
                "and then `QueryAsync(searchQuery)` on the SAME connection, so the user waits for both), and it " +
                "is the ONE statement of the pair with NO joins - `SELECT COUNT(*) FROM EventParticipant ep " +
                "WHERE ep.EventId = @EventId`. The deployed `ux_eventparticipant_eventenrollment` is " +
                "(eventid, studentenrollmentid), so its eventid prefix serves it; without it the count " +
                "seq-scans a table that is GLOBAL (every campus's participants live in it)."
            ,
            Sql = @"
                SELECT COUNT(*) FROM EventParticipant ep
                 WHERE ep.EventId = @schoolEventId",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-participant-page",
            Title = "Event detail - the paged participant list",
            Source = "EventParticipantRepository.GetByEventIdPaged - the event detail DataTable",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM eventparticipant WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "eventparticipant",
            IndexName = "ux_eventparticipant_eventenrollment",
            IndexColumns = "eventid, studentenrollmentid",
            IndexRationale =
                "the page half reaches the scope by `ep.EventId` and then INNER JOINs StudentEnrollment (id) " +
                "and Student (id) plus a LEFT JOIN to Classroom, all of them primary-key lookups. " +
                "`ux_eventparticipant_eventenrollment`'s eventid prefix bounds the driving set; the " +
                "`ORDER BY ep.id` then sorts only that event's rows.",
            Sql = @"
                SELECT ep.*, se.studentid, s.name AS studentname, s.admissionnumber, se.rollnumber, c.classroomname
                  FROM EventParticipant ep
                  INNER JOIN StudentEnrollment se ON ep.studentenrollmentid = se.id
                  INNER JOIN Student s ON se.studentid = s.id
                  LEFT JOIN Classroom c ON se.classroomid = c.id
                 WHERE ep.EventId = @schoolEventId
                 ORDER BY ep.id
                 LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-participant-list",
            Title = "Event detail - the event's whole audience, unpaged",
            Source = "EventParticipantRepository.GetByEventId - the invoice fan-out's own read",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM eventparticipant WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "eventparticipant",
            IndexName = "ux_eventparticipant_eventenrollment",
            IndexColumns = "eventid, studentenrollmentid",
            IndexRationale =
                "⚠️ THIS IS THE READ THE INVOICE FAN-OUT CONSUMES, AND IT IS UNBOUNDED - the same joins as the " +
                "page but with NO `LIMIT`, so the whole audience is materialised in one statement. That is why " +
                "the spec carries its own budget and why the eventid prefix matters more here than anywhere " +
                "else in the module: the row count is the EVENT's attendance, not a page.",
            Sql = @"
                SELECT ep.*, se.studentid, s.name AS studentname, s.admissionnumber, se.rollnumber, c.classroomname
                  FROM EventParticipant ep
                  INNER JOIN StudentEnrollment se ON ep.studentenrollmentid = se.id
                  INNER JOIN Student s ON se.studentid = s.id
                  LEFT JOIN Classroom c ON se.classroomid = c.id
                 WHERE ep.eventid = @schoolEventId
                 ORDER BY ep.id",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-studentcharge-list",
            Title = "Event finance - the per-student charges of one event",
            Source = "EventStudentChargeRepository.GetByEventId - the per-student charge grid",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM eventstudentcharge WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "eventstudentcharge",
            IndexName = "ux_eventstudentcharge_chargeenrollment",
            IndexColumns = "eventchargeid, studentenrollmentid",
            IndexRationale =
                "⚠️ THE SCOPE COMES FROM THE CHARGE, NOT FROM THE ROW. The read drives from `EventCharge ec " +
                "WHERE ec.eventid = @EventId` and joins `esc.eventchargeid = ec.id`, so the usable key here is " +
                "`eventchargeid` - and the deployed `ux_eventstudentcharge_chargeenrollment` is " +
                "(eventchargeid, studentenrollmentid), whose prefix serves it. The alternative access path - " +
                "`ix_eventstudentcharge_studenttaxexemption` on (tenantid, schoolid, campusid, " +
                "studentenrollmentid) - cannot lead, because the predicate names no scope column.",
            Sql = @"
                SELECT esc.*, s.name AS studentname, s.admissionnumber, ec.description AS chargedescription,
                       i.invoicenumber
                  FROM EventStudentCharge esc
                  INNER JOIN EventCharge ec ON esc.eventchargeid = ec.id
                  INNER JOIN StudentEnrollment se ON esc.studentenrollmentid = se.id
                  INNER JOIN Student s ON se.studentid = s.id
                  LEFT JOIN Invoices i ON esc.invoiceid = i.id
                 WHERE ec.eventid = @schoolEventId
                 ORDER BY esc.id",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
            },
        },

        new QuerySpec
        {
            Key = "evtfin-studentcharge-pending",
            Title = "Event finance - the pending, un-invoiced per-student charges",
            Source = "EventStudentChargeRepository.GetPendingByEventId - the read the invoice generator consumes",
            P95BudgetMs = 250,
            VolumeSql = "SELECT COUNT(*) FROM eventstudentcharge WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND status = 1 AND invoiceid IS NULL",
            MinVolume = 1,
            IndexTable = "eventstudentcharge",
            IndexName = "ux_eventstudentcharge_chargeenrollment",
            IndexColumns = "eventchargeid, studentenrollmentid",
            IndexRationale =
                "the same `eventchargeid` path as the list, with `esc.status = Pending AND esc.invoiceid IS " +
                "NULL` filtering rows the query had already reached by its charge id. " +
                "`ux_eventstudentcharge_invoice` is a PARTIAL unique index on `invoiceid WHERE invoiceid IS " +
                "NOT NULL`, so it is deliberately useless to this query: the predicate asks for the rows where " +
                "`invoiceid` IS NULL, which that index excludes by construction.",
            Sql = @"
                SELECT esc.*, s.name AS studentname, s.admissionnumber, ec.description AS chargedescription,
                       i.invoicenumber
                  FROM EventStudentCharge esc
                  INNER JOIN EventCharge ec ON esc.eventchargeid = ec.id
                  INNER JOIN StudentEnrollment se ON esc.studentenrollmentid = se.id
                  INNER JOIN Student s ON se.studentid = s.id
                  LEFT JOIN Invoices i ON esc.invoiceid = i.id
                 WHERE ec.eventid = @schoolEventId
                   AND esc.status = @pending AND esc.invoiceid IS NULL
                 ORDER BY esc.studentenrollmentid, esc.id",
            Params = new Dictionary<string, object>
            {
                ["schoolEventId"] = v.EventFinanceEventId,
                ["pending"] = (short)1,
            },
        },

        // ------------------------------------------------------------------
        // HR MONEY / STRUCTURE DETAILS - the child tables of the payroll and performance workspaces.
        //
        // ⚠️ EVERY ONE OF THESE PARENTS WAS POPULATED AND EVERY ONE OF THESE CHILDREN WAS EMPTY, which
        // is worse than an unseeded module: the parent grid renders rows, its detail dialog opens, and
        // it is blank - while a spec over the empty child reports SKIP. `HrMoneyDetailSeeder` filled
        // them for the campus the tool measures.
        //
        // ⚠️ THE ONES KEYED ON A PARENT ID (`@salaryStructureId`, `@performanceReviewId`,
        // `@employeePayrollId`, `@loanId`, `@employeeId`) ARE THE MAJORITY, because that is how the
        // application reads them - the user opens ONE structure/review/payslip and the panel loads its
        // lines. The resolvers hand each the campus's BUSIEST such row, never the first, so the reading
        // is the worst case rather than an empty one.
        // ------------------------------------------------------------------
        new QuerySpec
        {
            Key = "hrsc-list",
            Title = "Salary components - the campus's whole catalogue in display order",
            Source = "SalaryRepository.GetSalaryComponents - the salary-component grid",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM salarycomponent WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "salarycomponent",
            IndexName = "ix_salarycomponent_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "the read is the scope triple plus `ORDER BY DisplayOrder, Name` with NO LIMIT - an unpaged " +
                "catalogue read from the structure editor's dropdown. `ix_salarycomponent_tenantschoolcampus` " +
                "is deployed and serves the predicate; the sort is over the campus's own tens of rows. Same " +
                "family `hrmeeting` recorded: a scope-only index is right where the table stays small, and a " +
                "sort-column index is only owed when it stops being small.",
            Sql = @"
                SELECT * FROM salarycomponent
                 WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 ORDER BY displayorder, name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hrsc-active",
            Title = "Salary components - the ACTIVE subset the structure dialog offers",
            Source = "SalaryRepository.GetActiveSalaryComponents - the structure editor's dropdown",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM salarycomponent WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE",
            MinVolume = 1,
            IndexTable = "salarycomponent",
            IndexName = "ix_salarycomponent_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "the list read with `AND IsActive = true` added. `ix_salarycomponent_type` is deployed on the " +
                "component TYPE (Earning/Deduction) and cannot serve this predicate, and the scope index still " +
                "leads - the active filter is applied to rows it already reached.",
            Sql = @"
                SELECT * FROM salarycomponent
                 WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE
                 ORDER BY displayorder, name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hrsc-structure-lines",
            Title = "Salary structure - the components one structure is built from",
            Source = "EmployeeSalaryStructureRepository.GetDetails / SalaryRepository.GetSalaryDetails (same shape)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeesalarystructuredetail WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeesalarystructuredetail",
            IndexName = "ix_essd_structureid",
            IndexColumns = "salarystructureid",
            IndexRationale =
                "the read is BY STRUCTURE (`WHERE essd.SalaryStructureId = @StructureId`) and joins the " +
                "catalogue on its primary key for the name and code it renders. `ix_essd_structureid` is " +
                "deployed and is the only usable key; `ix_essd_componentid` is the mirror (used by the " +
                "component's own \"which structures use me\" direction) and cannot serve this.",
            Sql = @"
                SELECT essd.*, sc.name AS componentname, sc.code AS componentcode,
                       sc.componenttype, sc.calculationtype, sc.istaxable
                  FROM employeesalarystructuredetail essd
                  LEFT JOIN salarycomponent sc ON essd.salarycomponentid = sc.id
                 WHERE essd.salarystructureid = @salaryStructureId
                 ORDER BY sc.displayorder",
            Params = new Dictionary<string, object>
            {
                ["salaryStructureId"] = v.SalaryStructureId,
            },
        },

        new QuerySpec
        {
            Key = "perf-kpi-list",
            Title = "Performance KPIs - the whole campus list (active and inactive)",
            Source = "PerformanceRepository.GetKpis - the KPI tab",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancekpi WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "performancekpi",
            IndexName = "performancekpi_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ `performancekpi` carries NO index on its scope triple - only its primary key - and this " +
                "is recorded rather than fixed: the table holds a handful of rows per campus (a school reviews " +
                "against five to ten KPIs, ever), so the planner seq-scans it and a second btree would buy " +
                "nothing measurable. The same rule `employeepayroll` and `invitem` recorded: a missing index " +
                "is not a defect until a query would use it, and a CONVENTION index is not self-justifying. " +
                "What would change the answer is a campus with hundreds of KPIs, which the review engine's " +
                "100%-weight rule makes impossible.",
            Sql = @"
                SELECT * FROM performancekpi
                 WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 ORDER BY name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "perf-kpi-active",
            Title = "Performance KPIs - the ACTIVE subset the weight rule is summed from",
            Source = "PerformanceRepository.GetActiveKpis - the feed behind StartReviewAsync's 95-105% guard",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancekpi WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE",
            MinVolume = 1,
            IndexTable = "performancekpi",
            IndexName = "performancekpi_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ THIS IS THE READ THE REVIEW ENGINE'S GUARD IS TAKEN FROM: `StartReviewAsync` sums these " +
                "rows' `defaultweight` and refuses outside 95-105%. Same shape as the list read and the same " +
                "answer on indexing - the table is tens of rows per campus, so it seq-scans and the cost is " +
                "in the aggregate, not in the access path.",
            Sql = @"
                SELECT * FROM performancekpi
                 WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE
                 ORDER BY name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "perf-goals",
            Title = "Employee goals - one person's goals, newest first",
            Source = "PerformanceRepository.GetEmployeeGoals - the employee's goal panel",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeegoal WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeegoal",
            IndexName = "idx_employeegoal_employeeid",
            IndexColumns = "employeeid",
            IndexRationale =
                "the read filters the EMPLOYEE and the scope together and sorts by `CreatedOn DESC`. " +
                "`idx_employeegoal_employeeid` is the selective half (one person's goals, not the campus's) " +
                "and `idx_employeegoal_cycleid` is the reporting direction. There is no index on the scope " +
                "triple and none is owed: the driving predicate is the employee.",
            Sql = @"
                SELECT g.*, e.firstname || ' ' || e.lastname AS employeename, e.employeecode, c.name AS cyclename
                  FROM employeegoal g
                  LEFT JOIN employee e ON g.employeeid = e.id
                  LEFT JOIN performancereviewcycle c ON g.performancereviewcycleid = c.id
                 WHERE g.employeeid = @employeeId
                   AND g.tenantid = @tenantId AND g.schoolid = @schoolId AND g.campusid = @campusId
                 ORDER BY g.createdon DESC",
            Params = new Dictionary<string, object>
            {
                ["employeeId"] = v.EmployeeId,
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "perf-review-details",
            Title = "Performance review - the KPI score lines of one review",
            Source = "PerformanceRepository.GetReviewDetails - the review dialog's score table",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeperformancedetail WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeeperformancedetail",
            IndexName = "idx_employeeperformancedetail_reviewid",
            IndexColumns = "employeeperformancereviewid",
            IndexRationale =
                "the read is BY REVIEW and LEFT JOINs the KPI catalogue for the name and weight it orders by. " +
                "`idx_employeeperformancedetail_reviewid` is deployed and is the only usable key here; " +
                "`idx_employeeperformancedetail_kpiid` is the reverse direction.",
            Sql = @"
                SELECT d.*, k.name AS kpiname, k.defaultweight, k.maximumscore
                  FROM employeeperformancedetail d
                  LEFT JOIN performancekpi k ON d.performancekpiid = k.id
                 WHERE d.employeeperformancereviewid = @performanceReviewId
                 ORDER BY k.defaultweight DESC",
            Params = new Dictionary<string, object>
            {
                ["performanceReviewId"] = v.PerformanceReviewId,
            },
        },

        new QuerySpec
        {
            Key = "perf-recommendations",
            Title = "Performance review - the recommendations raised on one review",
            Source = "PerformanceRepository.GetRecommendations - the review dialog's recommendation list",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancerecommendation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "performancerecommendation",
            IndexName = "idx_performancerecommendation_reviewid",
            IndexColumns = "employeeperformancereviewid",
            IndexRationale =
                "keyed on the REVIEW and LEFT JOINing `employee` on `UserId` (not `Id`) for the approver's " +
                "name - the join is the reason a recommendation with no approver still renders, and the " +
                "deployed `idx_performancerecommendation_reviewid` is what bounds the driving set.",
            Sql = @"
                SELECT r.*, e.firstname || ' ' || e.lastname AS approvedbyname
                  FROM performancerecommendation r
                  LEFT JOIN employee e ON r.approvedby = e.userid
                 WHERE r.employeeperformancereviewid = @performanceReviewId
                 ORDER BY r.createdon",
            Params = new Dictionary<string, object>
            {
                ["performanceReviewId"] = v.PerformanceReviewId,
            },
        },

        new QuerySpec
        {
            Key = "perf-employee-recommendations",
            Title = "Performance recommendations - one employee's whole card feed",
            Source = "PerformanceRepository.GetEmployeeRecommendations - the employee's recommendations card",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM performancerecommendation WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "performancerecommendation",
            IndexName = "idx_performancerecommendation_reviewid",
            IndexColumns = "employeeperformancereviewid",
            IndexRationale =
                "⚠️ THE EMPLOYEE IS REACHED THROUGH THE REVIEW, not stored on the recommendation: the query " +
                "INNER JOINs `employeeperformancereview` on `v.EmployeeId = @Emp` and then joins the cycle for " +
                "the feed's cycle name. So the access path is `employeeperformancereview` by employee first (its " +
                "own index) and this table by review second - which is exactly why " +
                "`idx_performancerecommendation_reviewid` is the candidate that matters, and why a scope-column " +
                "index on this table could not lead.",
            Sql = @"
                SELECT r.*, c.name AS reviewcyclename, e.firstname || ' ' || e.lastname AS approvedbyname
                  FROM performancerecommendation r
                  INNER JOIN employeeperformancereview v ON r.employeeperformancereviewid = v.id
                  LEFT JOIN performancereviewcycle c ON v.performancereviewcycleid = c.id
                  LEFT JOIN employee e ON r.approvedby = e.userid
                 WHERE v.employeeid = @employeeId
                   AND r.tenantid = @tenantId AND r.schoolid = @schoolId AND r.campusid = @campusId
                 ORDER BY r.createdon DESC",
            Params = new Dictionary<string, object>
            {
                ["employeeId"] = v.EmployeeId,
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "hr-payroll-details",
            Title = "Payslip - the earning and deduction lines of one payroll record",
            Source = "PayrollRepository.GetPayrollDetails - the payslip's breakdown",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeepayrolldetail WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeepayrolldetail",
            IndexName = "ix_epd_payrollid",
            IndexColumns = "employeepayrollid",
            IndexRationale =
                "⚠️ THIS IS THE MODULE'S REAL VOLUME QUERY: the seeder writes one line per salary component per " +
                "payroll record, so the table grows with (employees x components x periods) - orders of " +
                "magnitude past anything else in this batch. It is read BY PAYROLL RECORD and " +
                "`ix_epd_payrollid` is deployed, which is the difference between an index scan over a " +
                "handful of lines and a seq scan over the campus's whole payslip history.",
            Sql = @"
                SELECT * FROM employeepayrolldetail
                 WHERE employeepayrollid = @employeePayrollId
                 ORDER BY displayorder",
            Params = new Dictionary<string, object>
            {
                ["employeePayrollId"] = v.EmployeePayrollId,
            },
        },

        new QuerySpec
        {
            Key = "hr-payroll-adjustments",
            Title = "Payslip - the adjustments applied to one payroll record",
            Source = "PayrollRepository.GetAdjustments - the payslip's adjustment list",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM payrolladjustment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "payrolladjustment",
            IndexName = "ix_pa_payrollid",
            IndexColumns = "employeepayrollid",
            IndexRationale =
                "keyed on the payroll record and LEFT JOINing `employee` on `UserId` for the approver's name. " +
                "`ix_pa_payrollid` is deployed. ⚠️ This table is SPARSE BY DESIGN - an adjustment is an " +
                "exception, not a line every payslip carries - which is why the resolver hands the spec the " +
                "record that HAS one rather than the first record on the campus.",
            Sql = @"
                SELECT pa.*, emp.firstname || ' ' || emp.lastname AS approvername
                  FROM payrolladjustment pa
                  LEFT JOIN employee emp ON pa.approvedby = emp.userid
                 WHERE pa.employeepayrollid = @employeePayrollId
                 ORDER BY pa.createdon",
            Params = new Dictionary<string, object>
            {
                ["employeePayrollId"] = v.EmployeePayrollId,
            },
        },

        new QuerySpec
        {
            Key = "hr-loan-payments",
            Title = "Loan - the payments collected against one loan",
            Source = "LoanRepository.GetPayments - the loan detail modal's payment history",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM employeeloanpayment WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "employeeloanpayment",
            IndexName = "ix_employeeloanpayment_loanid",
            IndexColumns = "employeeloanid",
            IndexRationale =
                "the loan is the key - the repository takes the loan id the user opened, not the scope - so " +
                "`ix_employeeloanpayment_loanid` is the only usable column and it is deployed. It is also the " +
                "column the paired write uses: `MarkInstallmentsPaidAsync` records the payment and marks the " +
                "installment in one step, and the payroll collection reads the schedule by the same id.",
            Sql = @"
                SELECT * FROM employeeloanpayment
                 WHERE employeeloanid = @loanId
                 ORDER BY paymentdate, id",
            Params = new Dictionary<string, object>
            {
                ["loanId"] = v.LoanId,
            },
        },

        // ------------------------------------------------------------------
        // THE DESK / ADMINISTRATIVE TABLES - the reporting desk's own history, the accounting monitor's
        // batch trail, the year-end History panel, the tenant's subscription, the bank-file desk, a
        // meeting's invite list, and the two identity tables.
        //
        // ⚠️ THEY ARE NOT ONE MODULE. What they share is that each is a table with a REAL READ SURFACE
        // whose parents were already populated, so every one of these reads returned an empty result
        // from a fully configured campus. `DeskOperationsSeeder` filled them for the measured campus.
        //
        // ⚠️ THREE TABLES ON THE SAME WORKLIST ARE DELIBERATELY NOT SEEDED AND HAVE NO SPEC, because
        // seeding a row no query reads is the "a seeded row no query can reach" defect:
        //   `calendarreminderlog` (write-only outbox), `teacherparentaction` (no code reference at all)
        //   and `curriculumtopiclearningmaterial` (a repository with NO CALLER - its only other
        //   appearance is the attachment orphan sweep, which EXCLUDES its ids). See the seeder.
        // ------------------------------------------------------------------
        new QuerySpec
        {
            Key = "rpt-saved-view-list",
            Title = "Report runner - the saved views of one definition",
            Source = "ReportViewRepository.GetByDefinition(tenantId, schoolId, campusId, reportDefinitionId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM reportview WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "reportview",
            IndexName = "ux_reportview_scope_definition_name",
            IndexColumns = "tenantid, schoolid, campusid, reportdefinitionid",
            IndexRationale =
                "the read filters the SCOPE TRIPLE AND the definition together and sorts by name. " +
                "`ux_reportview_scope_definition_name` is deployed on (tenantid, schoolid, campusid, " +
                "reportdefinitionid, lower(name)) - the leading four columns are exactly this predicate, " +
                "so the index serves the filter AND returns the rows in the order the query sorts by.",
            Sql = @"
                SELECT id, reportdefinitionid, name, filters, sort, createdby, createdon
                  FROM reportview
                 WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                   AND reportdefinitionid = @reportDefinitionId
                 ORDER BY name",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["reportDefinitionId"] = v.ReportDefinitionId,
            },
        },

        new QuerySpec
        {
            Key = "rpt-run-log-page",
            Title = "Report run history - first page for one definition",
            Source = "ReportRunLogRepository.GetPage(tenantId, reportDefinitionId, page)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM reportrunlog WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "reportrunlog",
            IndexName = "ix_reportrunlog_definition",
            IndexColumns = "tenantid, reportdefinitionid",
            IndexRationale =
                "⚠️ THE PREDICATE IS THE TENANT **AND** THE DEFINITION, NOT THE SCOPE TRIPLE - the campus " +
                "columns are carried on the row but the repository does not filter by them, which is why " +
                "the candidate is (tenantid, reportdefinitionid). That is also the correct SHAPE: a run log " +
                "is per definition and the definition is already scope-resolved when the page is opened.",
            Sql = @"
                SELECT * FROM reportrunlog
                 WHERE tenantid = @tenantId AND reportdefinitionid = @reportDefinitionId
                 LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["reportDefinitionId"] = v.ReportDefinitionId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "rpt-export-by-job",
            Title = "Report export - the artifact belonging to one export job",
            Source = "ReportExportRepository.GetByJobId(jobId) - the export download's only read",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM reportexport",
            MinVolume = 1,
            IndexTable = "reportexport",
            IndexName = "reportexport_jobid_key",
            IndexColumns = "jobid",
            IndexRationale =
                "⚠️ THIS TABLE HAS NO SCOPE COLUMNS AT ALL. Its reachability IS the job: the export is " +
                "created by the export job and fetched by `GetByJobId`, and `jobid` carries a UNIQUE index " +
                "(`reportexport_jobid_key`) but NO foreign key - so a row with a fabricated job id is " +
                "unreachable and nothing in the database would complain. The unique index is the candidate " +
                "and it is deployed.",
            Sql = @"SELECT * FROM reportexport WHERE jobid = @jobId",
            Params = new Dictionary<string, object>
            {
                ["jobId"] = v.ReportJobId,
            },
        },

        new QuerySpec
        {
            Key = "acct-posting-batch-page",
            Title = "Posting monitor - first page of the batch audit trail",
            Source = "PostingBatchRepository.GetAll(page, tenantId, schoolId, campusId) - the batch grid",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM postingbatch WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "postingbatch",
            IndexName = "postingbatch_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ `postingbatch` CARRIES ONLY ITS PRIMARY KEY AND THE SCOPE TRIPLE IS NOT INDEXED - recorded " +
                "rather than fixed. The read is `WHERE scope ORDER BY StartedOn DESC LIMIT n`, and the table " +
                "grows ONE ROW PER POSTING RUN: two a day for a decade is under six thousand rows for a " +
                "campus. A scope index is the `invitem` case - right for a table with ten thousand rows, not " +
                "owed for one that will never have a thousand. What a spec PROVES here is that the paged read " +
                "and its ORDER BY behave; the constant is the cost.",
            Sql = @"
                SELECT * FROM postingbatch
                 WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                 ORDER BY StartedOn DESC
                 LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "rollover-audit-list",
            Title = "Year-end history - the campus's rollover records",
            Source = "RolloverAuditLogRepository.GetByCampus(tenantId, schoolId, campusId, limit: 50)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM rolloverauditlog WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "rolloverauditlog",
            IndexName = "rolloverauditlog_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ TWO FINDINGS, BOTH IN THE SHAPE RATHER THAN AN INDEX. (1) The table carries only its " +
                "primary key - the repo builds `FindAsync(predicate, x => x.Id, ascending: false)` and the " +
                "scope triple is unindexed, which is acceptable for a table that gains ONE ROW PER YEAR " +
                "PER CAMPUS. (2) **THE `limit: 50` IS APPLIED IN MEMORY, NOT IN SQL** - `GetByCampus` reads " +
                "every matching row and then `.Take(limit)`s in C#, so the statement has no LIMIT at all. " +
                "The spec reproduces exactly that (it is what the app RUNS), which is why it declares no " +
                "`LIMIT`: a spec that added one would measure a query the application never issues. This is " +
                "the same class as `TransportStudentAssignmentRepository.GetAll(page, ...)` ignoring its " +
                "own envelope - harmless at six rows, a paging gap the day a campus rolls over many years.",
            Sql = @"
                SELECT * FROM rolloverauditlog
                 WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                 ORDER BY Id DESC",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "tenant-subscription",
            Title = "Tenant subscription - the tenant's own billing record",
            Source = "TenantSubscriptionRepository.GetByTenant(tenantId) - the subscription screen's read",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM tenantsubscription",
            MinVolume = 1,
            IndexTable = "tenantsubscription",
            IndexName = "ux_tenantsubscription_tenant",
            IndexColumns = "tenantid",
            IndexRationale =
                "⚠️ THIS IS A TENANT-LEVEL READ, NOT A CAMPUS ONE: the table has NO school/campus columns at " +
                "all, and `ux_tenantsubscription_tenant` is a UNIQUE index on (tenantid) - one row per tenant, " +
                "ever. The screen is a form rather than a grid, so the index scan is the whole cost and there " +
                "is nothing to page.",
            Sql = @"SELECT * FROM tenantsubscription WHERE tenantid = @tenantId LIMIT 1",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
            },
        },

        new QuerySpec
        {
            Key = "bank-file-template-list",
            Title = "Bank-file desk - the campus's ACTIVE templates",
            Source = "BankFileTemplateRepository.GetAll(tenantId, schoolId, campusId) - the desk's template list",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM bankfiletemplate WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "bankfiletemplate",
            IndexName = "bankfiletemplate_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ A CATALOGUE TABLE WITH NO SCOPE INDEX, AND THAT IS CORRECT HERE: a campus configures ONE " +
                "template per bank it pays through, so the table holds single digits forever and the planner " +
                "seq-scans it. The read is `scope AND IsActive` with no LIMIT (`FindAsync`, then an in-memory " +
                "convert), which is the right shape for a catalogue that IS the dropdown.",
            Sql = @"
                SELECT * FROM bankfiletemplate
                 WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                   AND isactive = TRUE",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "bank-file-export-list",
            Title = "Bank-file desk - every file the campus has generated",
            Source = "BankFileExportRepository.GetAll(tenantId, schoolId, campusId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM bankfileexport WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "bankfileexport",
            IndexName = "bankfileexport_pkey",
            IndexColumns = "id",
            IndexRationale =
                "the read is the scope triple plus `ORDER BY bfe.CreatedOn DESC`, WITH two LEFT JOINs for " +
                "the period and template names it renders. No index covers the scope triple and none is owed: " +
                "**a bank file is generated once per payroll period**, so the campus accumulates a handful a " +
                "year - the joins are over a payroll-period catalogue and a template table that are both tiny. " +
                "`fk_bankfileexport_period` and `fk_bankfileexport_template` are real FKs, so the planner has " +
                "index entries for both sides of the joins.",
            Sql = @"
                SELECT bfe.*, pp.Name AS PeriodName, bft.Name AS TemplateName, bft.BankName
                  FROM bankfileexport bfe
                  LEFT JOIN payrollperiod pp ON bfe.PayrollPeriodId = pp.Id
                  LEFT JOIN bankfiletemplate bft ON bfe.BankFileTemplateId = bft.Id
                 WHERE bfe.TenantId = @tenantId AND bfe.SchoolId = @schoolId AND bfe.CampusId = @campusId
                 ORDER BY bfe.CreatedOn DESC",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "bank-file-export-by-period",
            Title = "Bank-file desk - the files raised for one payroll period",
            Source = "BankFileExportRepository.GetByPeriod(payrollPeriodId)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM bankfileexport WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "bankfileexport",
            IndexName = "fk_bankfileexport_period",
            IndexColumns = "payrollperiodid",
            IndexRationale =
                "keyed on the PERIOD rather than the scope - the payroll screen opens a period and lists what " +
                "was generated for it, so the FK's own index entry is the usable key (`fk_bankfileexport_period`). " +
                "It shares `@payrollPeriodId` with the payroll RECORDS specs, which is the same period the " +
                "campus is actually looking at.",
            Sql = @"
                SELECT bfe.*, pp.Name AS PeriodName, bft.Name AS TemplateName, bft.BankName
                  FROM bankfileexport bfe
                  LEFT JOIN payrollperiod pp ON bfe.PayrollPeriodId = pp.Id
                  LEFT JOIN bankfiletemplate bft ON bfe.BankFileTemplateId = bft.Id
                 WHERE bfe.PayrollPeriodId = @payrollPeriodId
                 ORDER BY bfe.CreatedOn DESC",
            Params = new Dictionary<string, object>
            {
                ["payrollPeriodId"] = v.PayrollPeriodId,
            },
        },

        new QuerySpec
        {
            Key = "hr-meeting-audience",
            Title = "Meeting - the invite list of one meeting",
            Source = "HrMeetingRepository.GetAudience(meetingId) - the audience panel",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM hrmeetingaudience WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "hrmeetingaudience",
            IndexName = "hrmeetingaudience_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ THE READ TAKES THE MEETING ID AND **DOES NOT FILTER BY SCOPE AT ALL**, and the table carries " +
                "only its primary key - so a campus's invite list is a seq scan. Recorded rather than fixed, and " +
                "the reason is the SHAPE: `meetingid` is the column this read leads on and there is no index on " +
                "it, while the scope triple it does NOT use is what the convention would index. The honest " +
                "candidate is `(meetingid)`, which belongs with a change to the repository's scope filter - " +
                "measuring it here first (this spec is well under budget at this volume) is what says whether " +
                "it is worth writing, and the same finding is why the FIXTURE asserts that every audience row's " +
                "meeting belongs to its own campus.",
            Sql = @"
                SELECT ClassroomId AS ClassroomId, TeacherId AS TeacherId,
                       StudentId AS StudentId, GradeId AS GradeId
                  FROM hrmeetingaudience
                 WHERE MeetingId = @meetingId",
            Params = new Dictionary<string, object>
            {
                ["meetingId"] = v.HrMeetingId,
            },
        },

        new QuerySpec
        {
            Key = "identity-user-page",
            Title = "Users grid - first page, including each row's 2FA state",
            Source = "UserRepository.GetAll(page, tenantId, schoolId, campusId) - the Users screen",
            P95BudgetMs = 300,
            VolumeSql = "SELECT COUNT(*) FROM users WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "users",
            IndexName = "ix_users_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "⚠️⚠️ A FINDING, MEASURED BEFORE THIS SPEC COULD RUN AT ALL: **the users grid renders NOBODY " +
                "for a campus whose users carry a NULL `status`.** The filter is `AND u.Status <> 'Disabled'`, " +
                "and in SQL `NULL <> 'Disabled'` evaluates to NULL - so the row is excluded. On `ayra_perf` all " +
                "15 users of the measured campus held NULL (the perf seeder omitted the column), so this spec " +
                "returned **0 rows** for a campus with 15 users - a green reading from an empty result, which " +
                "is exactly the failure a spec is supposed to prevent. The dataset now writes 'Active' and " +
                "backfilled the rows. **THE APP HALF IS STILL WORTH RECORDING: `AccountStatusPolicy.CanSignIn` " +
                "ADMITS an empty/NULL status (deliberately - 24 rows predate the column), so a NULL-status " +
                "account can sign in but is invisible on the screen that administers it**, and the same " +
                "three-valued-logic shape sits behind the auth server's own `Status == 'Active'` comparisons. " +
                "That is an app fix, not a perf one; what this catalogue does is prove the read it is tied to. " +
                "\n\n" +
                "THIS SPEC EXISTS FOR THE CORRELATED `usertwofactor` LOOKUP, which is the ONLY read of that " +
                "table in the resource server: the grid projects `(SELECT COALESCE(tf.isenabled, false) FROM " +
                "usertwofactor tf WHERE tf.userid = pu.id) AS istwofactorenabled` PER ROW, and the Reset 2FA " +
                "row action renders from it. `usertwofactor`'s primary key IS `userid`, so that subquery is an " +
                "index lookup per row - which is the property worth pinning, because a scan of that table per " +
                "user would grow the users grid by O(users x enrolments). ⚠️ `users` carries no index on its " +
                "scope triple here (it is a per-deployment table of dozens of rows), so the outer read is a " +
                "seq scan over a tiny table and the subquery's access path is the whole finding.",
            Sql = @"
                SELECT u.*,
                       (SELECT COALESCE(tf.isenabled, false)
                          FROM usertwofactor tf WHERE tf.userid = u.id) AS istwofactorenabled
                  FROM users u
                 WHERE u.TenantId = @tenantId AND u.campusId = @campusId AND u.SchoolId = @schoolId
                   AND u.Status <> 'Disabled'
                 ORDER BY u.Id
                 LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "identity-password-history-reuse",
            Title = "Password reuse check - the user's last five hashes",
            Source = "UserPasswordHistoryRepository.IsReuseAsync(userIds, plaintext, lookback: 5)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM userpasswordhistory h JOIN users u ON u.id = h.userid WHERE u.tenantid = @tenantId AND u.schoolid = @schoolId AND u.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "userpasswordhistory",
            IndexName = "userpasswordhistory_pkey",
            IndexColumns = "id",
            IndexRationale =
                "⚠️ THE BIND IS AN ARRAY, NOT AN IN-LIST, AND THAT IS THE POINT: `UserId = ANY(@UserIds)` is how " +
                "the app asks, because ONE PASSWORD PER PERSON means the reuse check spans every row of the " +
                "address (`GetAccountIdsByEmail`) - an implementation that scanned only the signed-in row would " +
                "let a user bypass reuse protection by switching scopes. The table carries only its primary key " +
                "with NO index on `userid` or `createdon`, which is acceptable for the shape this query has: " +
                "the array is a handful of ids and a person's history is trimmed to ten rows (`TrimAsync`), so " +
                "the scan is bounded by the array rather than by the table. That is also why the FIXTURE asserts " +
                "the rows' timestamps are DISTINCT - the `LIMIT @Lookback` sits on top of an ORDER BY that ties.",
            Sql = @"
                SELECT PasswordHash FROM UserPasswordHistory
                 WHERE UserId = ANY(@UserIds)
                 ORDER BY CreatedOn DESC, Id DESC
                 LIMIT @lookback",
            Params = new Dictionary<string, object>
            {
                // ⚠️ THE SAME ARRAY SHAPE THE REPOSITORY BINDS. Npgsql renders this as `= ANY('{...}')`;
                // a single long would measure a different plan than the one the app issues.
                ["UserIds"] = new[] { v.UserId },
                ["lookback"] = PasswordHistoryLookback,
            },
        },

        new QuerySpec
        {
            Key = "comm-meeting-list",
            Title = "Meetings - the campus's active meetings in start order",
            Source = "HrMeetingRepository.GetAll(t, s, c) - unpaged, the calendar page",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM hrmeeting WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId AND isactive = TRUE",
            MinVolume = 1,
            IndexTable = "hrmeeting",
            IndexName = "ix_hrmeeting_tenantschoolcampus_start",
            IndexColumns = "tenantid, schoolid, campusid, startdatetime",
            IndexRationale =
                "⚠️ `hrmeeting` is the ONE table in this batch whose read is scoped AND SORTED with no LIMIT, " +
                "so the sort is what bounds it - the same distinction `V132` recorded on `invoices` and " +
                "`attendance`. The scope triple prunes nothing (a campus owns all of its meetings) while " +
                "`ORDER BY m.StartDateTime` forces a full sort of the campus's rows unless the index carries " +
                "the sort column. In a calendar a meeting table stays small, so this is a convention index " +
                "rather than a measured defect - recorded as such, not claimed as a fix.",
            Sql = @"
                SELECT m.Id AS Id, m.Title AS Title, m.Agenda AS Agenda,
                       to_char(m.StartDateTime, 'YYYY-MM-DD HH24:MI') AS StartDateTime,
                       to_char(m.EndDateTime, 'YYYY-MM-DD HH24:MI') AS EndDateTime,
                       m.OrganizerEmployeeId AS OrganizerEmployeeId,
                       COALESCE(e.FirstName, '') || ' ' || COALESCE(e.LastName, '') AS OrganizerName,
                       m.Location AS Location, m.IsActive AS IsActive,
                       m.TenantId AS TenantId, m.SchoolId AS SchoolId, m.CampusId AS CampusId,
                       m.RecurrenceRule AS RecurrenceRule, m.RecurrenceEnd AS RecurrenceEnd
                FROM HrMeeting m
                LEFT JOIN Employee e ON e.Id = m.OrganizerEmployeeId
                WHERE m.TenantId = @tenantId AND m.SchoolId = @schoolId AND m.CampusId = @campusId
                  AND m.IsActive = TRUE
                ORDER BY m.StartDateTime",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ==================================================================
        // THE TIMETABLE / TEACHER-OPS MODULE (`tt-*`, `cst-*`)
        //
        // ⚠️ EVERY ENTRY READ IN THIS MODULE IS FILTERED `Timetable.Status = 'Published'`, AND THE
        // SCREEN'S OWN SAVE PATH WRITES `Draft`. So the module has a state that is INVISIBLE to all of
        // its grids, and a spec that forgot the filter would measure a set no screen asks for. Each
        // SQL below is transcribed from the repository that serves its screen, status filter included.
        //
        // ⚠️ `ClassroomSubjectTeacher` HAS NO tenant/school/campus COLUMNS - the campus scope comes
        // from the INNER JOINed `Classroom`. That is why the `cst-*` specs spell the scope on `c.`
        // rather than on `st.`, and why `AcademicYearId` (a real column) is the only filter this table
        // can carry by itself.
        //
        // ⚠️ TWO OF THESE ARE "THE READ THE GRID DOES", NOT "THE GRID". `GetByTimetableId` and
        // `GetClassroomSubjectsByClassroomId` take an id the user clicked; the workload reads take a
        // teacher id; the audit read takes a teacher id and is capped at 300 rows. None of them can be
        // expressed as a scoped `GetAllAsync`, which is why they are hand-written specs pointed at the
        // id `ScopeVars` resolves from the rows `TimetableModuleSeeder` wrote.
        // ==================================================================

        new QuerySpec
        {
            Key = "tt-manager-rows",
            Title = "Timetable manager - every campus timetable with its entry count",
            Source = "TimetableRepository.GetManagerRows",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM Timetable WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "timetable",
            IndexName = "ix_timetable_tenantschoolcampus_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale =
                "the manager grid filters the scope triple plus an OPTIONAL `AcademicYearId`, and the table " +
                "ship: `ix_timetable_classroomid`, `ix_timetable_status` and `ix_timetable_academicyearid` - " +
                "three single-column indexes, none of which can serve the scope. There is no scope composite " +
                "at all. The composite is the candidate: whether it is worth an index depends on how many " +
                "timetables a campus accumulates across years, and a real campus keeps one per classroom per " +
                "year. A census candidate - only written into a migration if `--advise` proves it on the " +
                "measured statement.",
            Sql = @"
                SELECT tt.Id AS Id, tt.ClassroomId AS ClassroomId, c.ClassroomName AS ClassroomName,
                       tt.AcademicYearId AS AcademicYearId,
                       CAST(ay.StartYear AS text) || ' - ' || CAST(ay.EndYear AS text) AS AcademicYearName,
                       tt.Status AS Status, tt.ModifiedOn AS ModifiedOn,
                       (SELECT COUNT(*) FROM TimetableEntry te WHERE te.TimetableId = tt.Id) AS EntryCount
                  FROM Timetable tt
                  LEFT JOIN Classroom c ON c.Id = tt.ClassroomId
                  LEFT JOIN AcademicYear ay ON ay.Id = tt.AcademicYearId
                 WHERE tt.TenantId = @tenantid AND tt.SchoolId = @schoolid AND tt.CampusId = @campusid
                   AND (@academicyearid = 0 OR tt.AcademicYearId = @academicyearid)
                 ORDER BY tt.AcademicYearId DESC, c.ClassroomName, tt.Id",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "tt-entry-by-timetable",
            Title = "Classroom timetable - one timetable's whole week (the preview every grid is built from)",
            Source = "TimetableEntryRepository.GetByTimetableId",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM TimetableEntry WHERE TimetableId = @timetableid",
            MinVolume = 1,
            IndexTable = "timetableentry",
            IndexName = "ix_timetableentry_timetableid",
            IndexColumns = "timetableid",
            IndexRationale =
                "the ONLY table filter is `te.TimetableId = @timetableid`, and the table already carries " +
                "exactly that index - so this spec declares the EXISTING index rather than proposing a " +
                "second one, and `--advise` compares against what is there. The read also runs a correlated " +
                "`COUNT(*)` over `TimeTableSetupDetail` per row (which is how PeriodIndex is derived and " +
                "what the UI keys its CELLS on) - that subquery is what `tt-template-slots` measures.",
            Sql = @"
                SELECT te.Id, te.TimetableId, te.WeekDay, te.TimeTableSetupDetailId,
                       (SELECT COUNT(*) FROM TimeTableSetupDetail d
                         WHERE d.TimeTableSetupId = tsd.TimeTableSetupId
                           AND d.IsBreak = FALSE
                           AND d.PeriodNumber <= tsd.PeriodNumber) AS PeriodIndex,
                       te.AcademicGradeSubjectId, s.Name AS SubjectName,
                       te.TeacherId, u.FirstName || ' ' || u.LastName AS TeacherName,
                       te.RoomId, r.Name AS RoomName, te.LessonPlanId,
                       c.Id AS ClassroomId, c.ClassroomName
                  FROM TimetableEntry te
                  LEFT JOIN TimeTableSetupDetail tsd ON tsd.Id = te.TimeTableSetupDetailId
                  LEFT JOIN AcademicGradeSubject ags ON ags.Id = te.AcademicGradeSubjectId
                  LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                  LEFT JOIN Subject s ON s.Id = cs.SubjectId
                  LEFT JOIN Timetable tt ON tt.Id = te.TimetableId
                  LEFT JOIN Classroom c ON c.Id = tt.ClassroomId
                  LEFT JOIN Teacher t ON t.Id = te.TeacherId
                  LEFT JOIN Users u ON u.Id = t.UserId
                  LEFT JOIN Room r ON r.Id = te.RoomId
                 WHERE te.TimetableId = @timetableid
                 ORDER BY te.WeekDay, PeriodIndex",
            Params = new Dictionary<string, object>
            {
                ["timetableid"] = v.TimetableId,
            },
        },

        new QuerySpec
        {
            Key = "tt-teacher-weekly",
            Title = "Teacher weekly timetable - PUBLISHED periods taught by one teacher in one year",
            Source = "TimetableEntryRepository.GetTeacherEntries",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(*) FROM TimetableEntry te
                            INNER JOIN Timetable t ON t.Id = te.TimetableId
                           WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                             AND t.Status = 'Published' AND te.TeacherId = @timetableTeacherId",
            MinVolume = 1,
            IndexTable = "timetableentry",
            IndexName = "ix_timetableentry_teacherid",
            IndexColumns = "teacherid",
            IndexRationale =
                "the driving predicate is `te.TeacherId = @teacherid` filtered by the JOINED timetable's " +
                "scope+year+PUBLISHED - and `timetableentry` carries indexes on `timetableid`, `weekday`, " +
                "`roomid` and `academicgradesubjectid` but NONE on `teacherid`. The teacher's own sheet, the " +
                "workload screens and the deletion guard all reach the table through this column, so it is " +
                "the single most-used access path on the table and has no index at all. A census candidate - " +
                "probed only if the measured statement fails.",
            Sql = @"
                SELECT te.Id, te.TimetableId, te.WeekDay, te.TimeTableSetupDetailId,
                       (SELECT COUNT(*) FROM TimeTableSetupDetail d
                         WHERE d.TimeTableSetupId = tsd.TimeTableSetupId
                           AND d.IsBreak = FALSE
                           AND d.PeriodNumber <= tsd.PeriodNumber) AS PeriodIndex,
                       te.AcademicGradeSubjectId,
                       COALESCE(NULLIF(cs.CustomName, ''), s.Name, s2.Name) AS SubjectName,
                       te.TeacherId, u.FirstName || ' ' || u.LastName AS TeacherName,
                       te.RoomId, r.Name AS RoomName, te.LessonPlanId,
                       c.Id AS ClassroomId, c.ClassroomName
                  FROM TimetableEntry te
                  INNER JOIN Timetable t ON t.Id = te.TimetableId
                      AND t.Status = 'Published'
                      AND t.TenantId = @tenantid
                      AND t.SchoolId = @schoolid
                      AND t.CampusId = @campusid
                      AND t.AcademicYearId = @academicyearid
                  INNER JOIN Classroom c ON c.Id = t.ClassroomId
                  LEFT JOIN TimeTableSetupDetail tsd ON tsd.Id = te.TimeTableSetupDetailId
                  LEFT JOIN AcademicGradeSubject ags ON ags.Id = te.AcademicGradeSubjectId
                  LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                  LEFT JOIN Subject s ON s.Id = cs.SubjectId
                  LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                  LEFT JOIN Subject s2 ON s2.Id = cgs.SubjectId
                  LEFT JOIN Teacher tch ON tch.Id = te.TeacherId
                  LEFT JOIN Users u ON u.Id = tch.UserId
                  LEFT JOIN Room r ON r.Id = te.RoomId
                 WHERE te.TeacherId = @timetableteacherid
                 ORDER BY te.WeekDay, PeriodIndex",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
                ["timetableteacherid"] = v.TimetableTeacherId,
            },
        },

        new QuerySpec
        {
            Key = "tt-classroom-entries",
            Title = "Classroom timetable by grade+section - PUBLISHED periods of one classroom",
            Source = "TimetableEntryRepository.GetClassroomEntries",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(*) FROM TimetableEntry te
                            INNER JOIN Timetable t ON t.Id = te.TimetableId
                            INNER JOIN Classroom c ON c.Id = t.ClassroomId
                           WHERE t.Status = 'Published'
                             AND c.SectionId = @timetableSectionId AND c.AcademicGradeId = @timetableGradeId",
            MinVolume = 1,
            IndexTable = "timetable",
            IndexName = "ix_timetable_tenantschoolcampus_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale =
                "`GetClassroomEntries` filters the joined T_I_M_E_T_A_B_L_E by the full scope triple plus the " +
                "academic year, and the table has no scope composite (see `tt-manager-rows`). This is the " +
                "second reader that would use the same candidate, which is what makes it worth measuring " +
                "rather than recording: two independent screens reach the table this way.",
            Sql = @"
                SELECT te.Id, te.TimetableId, te.WeekDay, te.TimeTableSetupDetailId,
                       te.AcademicGradeSubjectId, s.Name AS SubjectName,
                       te.TeacherId, u.FirstName || ' ' || u.LastName AS TeacherName,
                       te.RoomId, r.Name AS RoomName, te.LessonPlanId
                  FROM TimetableEntry te
                  INNER JOIN Timetable t ON t.Id = te.TimetableId AND t.Status = 'Published'
                  INNER JOIN Classroom c ON c.Id = t.ClassroomId
                  LEFT JOIN AcademicGradeSubject ags ON ags.Id = te.AcademicGradeSubjectId
                  LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                  LEFT JOIN Subject s ON s.Id = cs.SubjectId
                  LEFT JOIN Teacher tch ON tch.Id = te.TeacherId
                  LEFT JOIN Users u ON u.Id = tch.UserId
                  LEFT JOIN Room r ON r.Id = te.RoomId
                 WHERE c.SectionId = @timetablesectionid
                   AND c.AcademicGradeId = @timetablegradeid
                   AND t.AcademicYearId = @academicyearid
                   AND t.TenantId = @tenantid
                   AND t.SchoolId = @schoolid
                   AND t.CampusId = @campusid
                 ORDER BY te.WeekDay, te.TimeTableSetupDetailId",
            Params = new Dictionary<string, object>
            {
                ["timetablesectionid"] = v.TimetableSectionId,
                ["timetablegradeid"] = v.TimetableGradeId,
                ["academicyearid"] = v.AcademicYearId,
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "tt-workload-subject-stats",
            Title = "Teacher workload - published periods grouped by SUBJECT",
            Source = "TimetableEntryRepository.GetTeacherWorkloadSubjectStats",
            P95BudgetMs = 200,
            IsScalar = false,
            VolumeSql = @"SELECT COUNT(DISTINCT te.AcademicGradeSubjectId) FROM TimetableEntry te
                            INNER JOIN Timetable t ON t.Id = te.TimetableId
                           WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                             AND t.Status = 'Published' AND te.TeacherId = @timetableTeacherId",
            MinVolume = 1,
            IndexTable = "timetableentry",
            IndexName = "ix_timetableentry_teacherid",
            IndexColumns = "teacherid",
            IndexRationale =
                "the GROUP BY is on `AcademicGradeSubjectId`, but the filter that decides how many rows are " +
                "read and grouped is `te.TeacherId` - which has no index. The grouping itself is served by " +
                "`ix_timetableentry_academicgradesubjectid` in the sort step, so `teacherid` remains the " +
                "candidate. Recorded alongside `tt-teacher-weekly`, which reads the same column.",
            Sql = @"SELECT te.AcademicGradeSubjectId AS AcademicGradeSubjectId,
                               COALESCE(NULLIF(cs.CustomName, ''), s.Name, s2.Name) AS SubjectName,
                               COUNT(DISTINCT te.Id) AS Periods
                        FROM TimetableEntry te
                        INNER JOIN Timetable t ON t.Id = te.TimetableId
                            AND t.Status = 'Published'
                            AND t.TenantId = @tenantid AND t.SchoolId = @schoolid AND t.CampusId = @campusid
                            AND t.AcademicYearId = @academicyearid
                        LEFT JOIN AcademicGradeSubject ags ON ags.Id = te.AcademicGradeSubjectId
                        LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                        LEFT JOIN Subject s ON s.Id = cs.SubjectId
                        LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                        LEFT JOIN Subject s2 ON s2.Id = cgs.SubjectId
                        WHERE te.TeacherId = @timetableteacherid
                        GROUP BY te.AcademicGradeSubjectId, COALESCE(NULLIF(cs.CustomName, ''), s.Name, s2.Name)
                        ORDER BY SubjectName",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
                ["timetableteacherid"] = v.TimetableTeacherId,
            },
        },

        new QuerySpec
        {
            Key = "tt-workload-weekday",
            Title = "Teacher workload - published periods grouped by WEEKDAY",
            Source = "TimetableEntryRepository.GetTeacherWorkloadWeekdayStats",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(DISTINCT te.WeekDay) FROM TimetableEntry te
                            INNER JOIN Timetable t ON t.Id = te.TimetableId
                           WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                             AND t.Status = 'Published' AND te.TeacherId = @timetableTeacherId",
            MinVolume = 1,
            IndexTable = "timetableentry",
            IndexName = "ix_timetableentry_teacherid",
            IndexColumns = "teacherid",
            IndexRationale =
                "same access path as the subject grouping (the teacher has no index), grouped by `WeekDay` " +
                "instead - so it is a second reader of the same candidate rather than a second candidate. " +
                "`ix_timetableentry_weekday` exists but is useless here: the weekday is the GROUPING key, not " +
                "the filter.",
            Sql = @"SELECT te.WeekDay AS WeekDay, COUNT(DISTINCT te.Id) AS Periods
                        FROM TimetableEntry te
                        INNER JOIN Timetable t ON t.Id = te.TimetableId
                            AND t.Status = 'Published'
                            AND t.TenantId = @tenantid AND t.SchoolId = @schoolid AND t.CampusId = @campusid
                            AND t.AcademicYearId = @academicyearid
                        WHERE te.TeacherId = @timetableteacherid
                        GROUP BY te.WeekDay
                        ORDER BY te.WeekDay",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
                ["timetableteacherid"] = v.TimetableTeacherId,
            },
        },

        new QuerySpec
        {
            Key = "tt-workload-totals",
            Title = "Workload overview - published periods per teacher, campus-wide (UNPAGED)",
            Source = "TimetableEntryRepository.GetTeacherPublishedWorkloadTotals",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(DISTINCT te.TeacherId) FROM TimetableEntry te
                            INNER JOIN Timetable t ON t.Id = te.TimetableId
                           WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                             AND t.Status = 'Published' AND te.TeacherId > 0",
            MinVolume = 1,
            IndexTable = "timetableentry",
            IndexName = "ix_timetableentry_teacherid",
            IndexColumns = "teacherid",
            IndexRationale =
                "the campus-wide overview groups EVERY published entry by teacher with no teacher filter, so " +
                "the scope+year+status predicate is what drives it and `teacherid` is the grouping key. It is " +
                "the one read here that cannot be narrowed by a teacher id, which is why it is worth having " +
                "alongside the per-teacher specs: it measures the cost of the campus's WHOLE timetable.",
            Sql = @"SELECT te.TeacherId AS TeacherId, COUNT(DISTINCT te.Id) AS Periods
                        FROM TimetableEntry te
                        INNER JOIN Timetable t ON t.Id = te.TimetableId
                            AND t.Status = 'Published'
                            AND t.TenantId = @tenantid AND t.SchoolId = @schoolid AND t.CampusId = @campusid
                            AND t.AcademicYearId = @academicyearid
                        WHERE te.TeacherId > 0
                        GROUP BY te.TeacherId",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "tt-count-published-by-teacher",
            Title = "Teacher deactivation guard - COUNT of published entries (scalar)",
            Source = "TimetableEntryRepository.CountPublishedByTeacher",
            P95BudgetMs = 150,
            IsScalar = true,
            VolumeSql = @"SELECT COUNT(*) FROM TimetableEntry te
                            INNER JOIN Timetable t ON t.Id = te.TimetableId
                           WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                             AND t.Status = 'Published' AND te.TeacherId = @timetableTeacherId",
            MinVolume = 1,
            IndexTable = "timetableentry",
            IndexName = "ix_timetableentry_teacherid",
            IndexColumns = "teacherid",
            IndexRationale =
                "a scalar COUNT on the same `te.TeacherId` predicate as the two workload groupings - the third " +
                "reader of the same candidate. It is separate from them because it is the DELETE guard: it runs " +
                "on every attempt to deactivate a teacher, and it must be exact rather than fast.",
            Sql = @"SELECT COUNT(0)
                       FROM TimetableEntry te
                       INNER JOIN Timetable t ON t.Id = te.TimetableId
                           AND t.Status = 'Published'
                           AND t.TenantId = @tenantid AND t.SchoolId = @schoolid
                           AND t.CampusId = @campusid AND t.AcademicYearId = @academicyearid
                       WHERE te.TeacherId = @timetableteacherid",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
                ["timetableteacherid"] = v.TimetableTeacherId,
            },
        },

        new QuerySpec
        {
            Key = "tt-template-slots",
            Title = "Period template - its slots (the read the classroom wizard and the print layout draw)",
            Source = "TimeTableSetupRepository.GetSetupResponse (batch 2 of 3 - the details read, the widest of the three statements in the round trip)",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT COUNT(*) FROM TimeTableSetupDetail d
                            INNER JOIN TimeTableSetup s ON s.Id = d.TimeTableSetupId
                           WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "timetablesetupdetail",
            IndexName = "ix_timetablesetupdetail_setupid",
            IndexColumns = "timetablesetupid",
            IndexRationale =
                "`timetablesetupdetail` carries ONLY its primary key - there is no index on `tablesetupid` at " +
                "all, so this read plus the CORRELATED subquery inside every entry spec (`tt-entry-by-timetable`, " +
                "`tt-teacher-weekly`) all scan it once per row. The correlated case is the one that matters: it " +
                "runs once per returned entry, i.e. once per cell of the grid. At the seeded size (nine slots) " +
                "that cannot be slow; it is recorded because the pattern scales with the number of ROWS RETURNED, " +
                "not with the size of this table. Probed only if the measured statement fails.",
            Sql = @"SELECT Id, TimeTableSetupId, PeriodNumber, Name, StartTime, EndTime, IsBreak,
                               CreatedBy, ModifiedBy, CreatedOn, ModifiedOn
                        FROM TimeTableSetupDetail
                        WHERE TimeTableSetupId = (
                            SELECT Id FROM TimeTableSetup
                            WHERE TenantId = @tenantid AND SchoolId = @schoolid AND CampusId = @campusid
                            ORDER BY Id DESC LIMIT 1)
                        ORDER BY PeriodNumber",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "tt-template-off-days",
            Title = "Period template - the weekdays it does NOT teach on (the wizard's off-day list)",
            Source = "TimeTableSetupRepository.GetSetupResponse (batch 3 of 3 - the off-day read; batch 1 reads TimeTableSetup itself, which tt-setup-list already covers)",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT COUNT(*) FROM TimeTableSetupOffDay o
                            INNER JOIN TimeTableSetup s ON s.Id = o.TimeTableSetupId
                           WHERE s.TenantId = @tenantId AND s.SchoolId = @schoolId AND s.CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "timetablesetupoffday",
            IndexName = "ix_timetablesetupoffday_setupid",
            IndexColumns = "timetablesetupid",
            IndexRationale =
                "the third statement of the same round trip as `tt-template-slots` (header + slots + off-days, " +
                "one `QueryMultipleAsync`), and `timetablesetupoffday` carries ONLY its identity primary key - " +
                "no index on `timetablesetupid`, exactly like its sibling. It is a small table (two rows per " +
                "template) so the read is a seq scan by construction and cannot be slow; it is recorded so the " +
                "batch is covered STATEMENT BY STATEMENT rather than by its widest member, and so an unseeded " +
                "off-day table is a SKIP instead of a silently empty list the wizard renders as \"no off days\".",
            Sql = @"SELECT Id, TimeTableSetupId, WeekDay
                        FROM TimeTableSetupOffDay
                        WHERE TimeTableSetupId = (
                            SELECT Id FROM TimeTableSetup
                            WHERE TenantId = @tenantid AND SchoolId = @schoolid AND CampusId = @campusid
                            ORDER BY Id DESC LIMIT 1)",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
            },
        },

        ScopeGrid(v, "tt-setup-list", "Period template grid - first page",
            "TimeTableSetupRepository.GetAll(page, t, s, c)", "TimeTableSetup", Scope3,
            "SELECT COUNT(*) FROM TimeTableSetup WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            orderBy: "Name"),

        new QuerySpec
        {
            Key = "cst-teacher-assignments",
            Title = "Teacher assignment panel - one teacher's year assignments (7 joins)",
            Source = "ClassroomSubjectTeacherRepository.GetTeacherAssignments",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(*) FROM ClassroomSubjectTeacher st
                           WHERE st.TeacherId = @timetableTeacherId AND st.AcademicYearId = @academicYearId",
            MinVolume = 1,
            IndexTable = "classroomsubjectteacher",
            IndexName = "ix_classroomsubjectteacher_teacherid_year",
            IndexColumns = "teacherid, academicyearid",
            IndexRationale =
                "`ClassroomSubjectTeacher` carries ONE index - `ix_classroomsubjectteacher_classroomid` - and " +
                "this screen does not use it: it filters `TeacherId` AND `AcademicYearId`, both of which are " +
                "unindexed. The table has no scope columns at all (the campus comes from the JOINed Classroom), " +
                "so a scope composite is not even expressible here: `(teacherid, academicyearid)` is the only " +
                "candidate, and the assignment panel, the workload share read and the two transfer paths all " +
                "reach the table through it.",
            Sql = @"SELECT st.Id, st.TeacherId, st.AcademicGradeSubjectId, st.ClassroomId,
                                 st.AcademicYearId, st.IsPrimaryTeacher,
                                 COALESCE(NULLIF(cs.CustomName, ''), sc.Name, ss.Name) AS SubjectName,
                                 c.ClassroomName, sec.Name AS SectionName,
                                 ag.Id AS AcademicGradeId, cg.Name AS GradeName
                          FROM ClassroomSubjectTeacher st
                          INNER JOIN Classroom c ON st.ClassroomId = c.Id
                          INNER JOIN Section sec ON sec.Id = c.SectionId
                          INNER JOIN AcademicGrade ag ON ag.Id = c.AcademicGradeId
                          LEFT JOIN CurriculumGrade cg ON cg.Id = ag.CurriculumGradeId
                          INNER JOIN AcademicGradeSubject ags ON ags.Id = st.AcademicGradeSubjectId
                          LEFT JOIN CampusSubject cs ON ags.CampusSubjectId = cs.Id
                          LEFT JOIN Subject sc ON cs.SubjectId = sc.Id
                          LEFT JOIN CurriculumGradeSubject cgs ON ags.CurriculumGradeSubjectId = cgs.Id
                          LEFT JOIN Subject ss ON cgs.SubjectId = ss.Id
                          WHERE st.TeacherId = @timetableteacherid AND st.AcademicYearId = @academicyearid
                          ORDER BY cg.Name, c.ClassroomName, SubjectName",
            Params = new Dictionary<string, object>
            {
                ["timetableteacherid"] = v.TimetableTeacherId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "cst-classroom-list",
            Title = "Classroom subjects panel - one classroom's teacher/subject rows",
            Source = "ClassroomSubjectTeacherRepository.GetClassroomSubjectTeacherByClassroomId",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM ClassroomSubjectTeacher WHERE ClassroomId = @timetableClassroomId",
            MinVolume = 1,
            IndexTable = "classroomsubjectteacher",
            IndexName = "ix_classroomsubjectteacher_classroomid",
            IndexColumns = "classroomid",
            IndexRationale =
                "the ONLY filter is `st.ClassroomId`, and the table already has exactly that index - so this " +
                "spec declares the EXISTING index and `--advise` compares against it, rather than proposing a " +
                "second one for a filter that is already served.",
            Sql = @"SELECT
                        st.TeacherId, st.AcademicGradeSubjectId, st.ClassroomId,
                        u.FirstName || ' ' || u.LastName AS TeacherName,
                        COALESCE(cs.CustomName, sc.Name, ss.Name) AS SubjectName,
                        c.ClassroomName, st.IsPrimaryTeacher, sc.Name, ss.Name
                        FROM ClassroomSubjectTeacher st
                        INNER JOIN Classroom c ON st.ClassroomId = c.Id
                        INNER JOIN AcademicGradeSubject ags ON st.AcademicGradeSubjectId = ags.Id
                        LEFT JOIN CampusSubject cs ON ags.CampusSubjectId = cs.Id
                        LEFT JOIN Subject sc ON cs.SubjectId = sc.Id
                        LEFT JOIN CurriculumGradeSubject cgs ON ags.CurriculumGradeSubjectId = cgs.Id
                        LEFT JOIN Subject ss ON cgs.SubjectId = ss.Id
                        INNER JOIN Teacher t ON st.TeacherId = t.Id
                        INNER JOIN Users u ON t.UserId = u.Id
                        WHERE st.ClassroomId = @timetableclassroomid",
            Params = new Dictionary<string, object>
            {
                ["timetableclassroomid"] = v.TimetableClassroomId,
            },
        },

        new QuerySpec
        {
            Key = "cst-year-assignments",
            Title = "Workload overview - EVERY assignment of the campus in the year (unpaged)",
            Source = "ClassroomSubjectTeacherRepository.GetAllYearAssignments",
            P95BudgetMs = 250,
            VolumeSql = @"SELECT COUNT(*) FROM ClassroomSubjectTeacher cst
                            INNER JOIN Classroom c ON c.Id = cst.ClassroomId
                           WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId
                             AND cst.AcademicYearId = @academicYearId",
            MinVolume = 1,
            IndexTable = "classroomsubjectteacher",
            IndexName = "ix_classroomsubjectteacher_teacherid_year",
            IndexColumns = "teacherid, academicyearid",
            IndexRationale =
                "this is the campus-wide share read: it filters `AcademicYearId` alone (no teacher) and resolves " +
                "the campus through the JOINed Classroom, then the caller groups the whole set in memory. It is " +
                "the read that justifies the SECOND column of the `(teacherid, academicyearid)` candidate: an " +
                "`(academicyearid)`-led index would serve this statement, but the assignment panel needs the " +
                "teacher leading. Recorded so the two shapes are weighed together rather than separately.",
            Sql = @"SELECT st.TeacherId, st.ClassroomId, c.ClassroomName, st.AcademicGradeSubjectId,
                                 st.IsPrimaryTeacher, ags.WeeklyPeriods,
                                 COALESCE(NULLIF(cs.CustomName, ''), sc.Name, ss.Name) AS SubjectName
                          FROM ClassroomSubjectTeacher st
                          INNER JOIN Classroom c ON c.Id = st.ClassroomId
                              AND c.TenantId = @tenantid AND c.SchoolId = @schoolid AND c.CampusId = @campusid
                          INNER JOIN AcademicGradeSubject ags ON ags.Id = st.AcademicGradeSubjectId
                          LEFT JOIN CampusSubject cs ON ags.CampusSubjectId = cs.Id
                          LEFT JOIN Subject sc ON cs.SubjectId = sc.Id
                          LEFT JOIN CurriculumGradeSubject cgs ON ags.CurriculumGradeSubjectId = cgs.Id
                          LEFT JOIN Subject ss ON cgs.SubjectId = ss.Id
                          WHERE st.AcademicYearId = @academicyearid
                          ORDER BY st.TeacherId, st.ClassroomId, st.AcademicGradeSubjectId",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "cst-count-by-teacher-year",
            Title = "Teacher deactivation guard - COUNT of years assignments (scalar)",
            Source = "ClassroomSubjectTeacherRepository.CountByTeacherAndYear",
            P95BudgetMs = 150,
            IsScalar = true,
            VolumeSql = @"SELECT COUNT(*) FROM ClassroomSubjectTeacher cst
                            INNER JOIN Classroom c ON c.Id = cst.ClassroomId
                           WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId
                             AND cst.TeacherId = @timetableTeacherId AND cst.AcademicYearId = @academicYearId",
            MinVolume = 1,
            IndexTable = "classroomsubjectteacher",
            IndexName = "ix_classroomsubjectteacher_teacherid_year",
            IndexColumns = "teacherid, academicyearid",
            IndexRationale =
                "the scalar half of the deactivation guard, on the same `(TeacherId, AcademicYearId)` predicate " +
                "as the assignment panel - a second reader of the same candidate.",
            Sql = @"SELECT COUNT(0) FROM ClassroomSubjectTeacher cst
                       INNER JOIN Classroom c ON c.Id = cst.ClassroomId
                           AND c.TenantId = @tenantid AND c.SchoolId = @schoolid AND c.CampusId = @campusid
                       WHERE cst.TeacherId = @timetableteacherid AND cst.AcademicYearId = @academicyearid",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["timetableteacherid"] = v.TimetableTeacherId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "cst-workload-source",
            Title = "Workload share - assignments that share a (classroom, subject) with the teacher",
            Source = "ClassroomSubjectTeacherRepository.GetTeacherWorkloadSourceRows",
            P95BudgetMs = 250,
            VolumeSql = @"SELECT COUNT(*) FROM ClassroomSubjectTeacher st2
                            INNER JOIN Classroom c ON c.Id = st2.ClassroomId
                           WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId
                             AND st2.AcademicYearId = @academicYearId
                             AND EXISTS (SELECT 1 FROM ClassroomSubjectTeacher mine
                                          WHERE mine.TeacherId = @timetableTeacherId
                                            AND mine.AcademicYearId = st2.AcademicYearId
                                            AND mine.ClassroomId = st2.ClassroomId
                                            AND mine.AcademicGradeSubjectId = st2.AcademicGradeSubjectId)",
            MinVolume = 2,
            IndexTable = "classroomsubjectteacher",
            IndexName = "ix_classroomsubjectteacher_teacherid_year",
            IndexColumns = "teacherid, academicyearid",
            IndexRationale =
                "the EXISTS subquery is the interesting half: it is correlated on `(TeacherId, AcademicYearId, " +
                "ClassroomId, AcademicGradeSubjectId)` and runs once per candidate row, so it walks " +
                "`ClassroomSubjectTeacher` for a teacher that this table cannot seek by - the same missing index " +
                "as the panel, reached from the other side. `MinVolume = 2` because a set of one row is exactly " +
                "the case the EXISTS cannot be selective on.",
            Sql = @"SELECT st2.TeacherId, st2.ClassroomId, c.ClassroomName, st2.AcademicGradeSubjectId,
                                 st2.IsPrimaryTeacher, ags.WeeklyPeriods,
                                 COALESCE(NULLIF(cs.CustomName, ''), sc.Name, ss.Name) AS SubjectName
                          FROM ClassroomSubjectTeacher st2
                          INNER JOIN Classroom c ON c.Id = st2.ClassroomId
                              AND c.TenantId = @tenantid AND c.SchoolId = @schoolid AND c.CampusId = @campusid
                          INNER JOIN AcademicGradeSubject ags ON ags.Id = st2.AcademicGradeSubjectId
                          LEFT JOIN CampusSubject cs ON ags.CampusSubjectId = cs.Id
                          LEFT JOIN Subject sc ON cs.SubjectId = sc.Id
                          LEFT JOIN CurriculumGradeSubject cgs ON ags.CurriculumGradeSubjectId = cgs.Id
                          LEFT JOIN Subject ss ON cgs.SubjectId = ss.Id
                          WHERE st2.AcademicYearId = @academicyearid
                            AND EXISTS (
                                SELECT 1 FROM ClassroomSubjectTeacher mine
                                WHERE mine.TeacherId = @timetableteacherid
                                  AND mine.AcademicYearId = st2.AcademicYearId
                                  AND mine.ClassroomId = st2.ClassroomId
                                  AND mine.AcademicGradeSubjectId = st2.AcademicGradeSubjectId)
                          ORDER BY st2.TeacherId, st2.ClassroomId, st2.AcademicGradeSubjectId",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
                ["timetableteacherid"] = v.TimetableTeacherId,
            },
        },

        new QuerySpec
        {
            Key = "tt-absence-by-date",
            Title = "Substitute desk - the absences recorded on one date",
            Source = "TeacherAbsenceRepository.GetByDate",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT COUNT(*) FROM TeacherAbsence
                           WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                             AND AcademicYearId = @academicYearId AND AbsenceDate = @timetableDate",
            MinVolume = 1,
            IndexTable = "teacherabsence",
            IndexName = "ix_ta_teacher_date",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid, teacherid, absencedate",
            IndexRationale =
                "the campus already carries a composite that leads with the scope triple, the year and the " +
                "date - so this read is served and the spec declares the EXISTING index rather than proposing " +
                "one. It is included because an absence query that is not indexed is the substitute desk's " +
                "whole day, and the declaration is what makes `--advise` compare against the real index.",
            Sql = @"SELECT a.Id, a.TeacherId,
                                  COALESCE(u.FirstName || ' ' || u.LastName, '') AS TeacherName,
                                  a.AbsenceDate::text AS Date,
                                  COALESCE(a.Reason, '') AS Reason,
                                  (a.LeaveRequestId IS NOT NULL) AS FromLeave
                           FROM TeacherAbsence a
                           INNER JOIN Teacher t ON t.Id = a.TeacherId
                           INNER JOIN Users u ON u.Id = t.UserId
                           WHERE a.TenantId = @tenantid AND a.SchoolId = @schoolid
                             AND a.CampusId = @campusid AND a.AcademicYearId = @academicyearid
                             AND a.AbsenceDate = @timetabledate
                           ORDER BY TeacherName",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
                ["timetabledate"] = v.TimetableDate,
            },
        },

        new QuerySpec
        {
            Key = "tt-relief-coverage",
            Title = "Substitute desk - one week-day's coverage cells for the campus",
            Source = "TimeTableReliefRepository.GetCoverageCells",
            P95BudgetMs = 250,
            VolumeSql = @"SELECT COUNT(*) FROM Timetable t
                            INNER JOIN TimetableEntry te ON te.TimetableId = t.Id AND te.WeekDay = @timetableWeekDay
                           WHERE t.Status = 'Published'
                             AND t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                             AND t.AcademicYearId = @academicYearId",
            MinVolume = 1,
            IndexTable = "timetable",
            IndexName = "ix_timetable_tenantschoolcampus_year",
            IndexColumns = "tenantid, schoolid, campusid, academicyearid",
            IndexRationale =
                "the coverage grid is the module's widest read: it walks EVERY published entry of the campus for " +
                "one weekday, LEFT JOINs the relief row for the date, then resolves two subject names and two " +
                "teacher names per cell - and the driving side has no scope composite. It is the strongest " +
                "reader of the `timetable` candidate, and the one whose row count is the campus's whole " +
                "timetable rather than one classroom's.",
            Sql = @"SELECT c.Id AS ClassroomId, c.ClassroomName, COALESCE(s.Name,'') AS SectionName,
                                  te.TimeTableSetupDetailId, tsd.Name AS PeriodName,
                                  (SELECT COUNT(*) FROM TimeTableSetupDetail d
                                    WHERE d.TimeTableSetupId = tsd.TimeTableSetupId
                                      AND d.IsBreak = FALSE AND d.PeriodNumber <= tsd.PeriodNumber) AS PeriodIndex,
                                  COALESCE(tsd.StartTime::text,'') AS StartTime,
                                  COALESCE(tsd.EndTime::text,'') AS EndTime,
                                  te.AcademicGradeSubjectId,
                                  COALESCE(NULLIF(cs.CustomName,''), sub.Name, sub2.Name) AS SubjectName,
                                  te.TeacherId,
                                  COALESCE(u.FirstName || ' ' || u.LastName,'') AS TeacherName,
                                  r.Id AS ReliefId, r.SubstituteTeacherId,
                                  COALESCE(su.FirstName || ' ' || su.LastName,'') AS SubstituteTeacherName,
                                  r.Notes AS ReliefNotes
                           FROM Timetable t
                           INNER JOIN TimetableEntry te ON te.TimetableId = t.Id AND te.WeekDay = @timetableweekday
                           INNER JOIN Classroom c ON c.Id = t.ClassroomId
                           LEFT JOIN Section s ON s.Id = c.SectionId
                           LEFT JOIN TimeTableSetupDetail tsd ON tsd.Id = te.TimeTableSetupDetailId
                           LEFT JOIN AcademicGradeSubject ags ON ags.Id = te.AcademicGradeSubjectId
                           LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                           LEFT JOIN Subject sub ON sub.Id = cs.SubjectId
                           LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                           LEFT JOIN Subject sub2 ON sub2.Id = cgs.SubjectId
                           LEFT JOIN Teacher tch ON tch.Id = te.TeacherId
                           LEFT JOIN Users u ON u.Id = tch.UserId
                           LEFT JOIN TimeTableRelief r
                                ON r.TenantId = t.TenantId AND r.SchoolId = t.SchoolId AND r.CampusId = t.CampusId
                               AND r.ReliefDate = @timetabledate AND r.ClassroomId = t.ClassroomId
                               AND r.TimeTableSetupDetailId = te.TimeTableSetupDetailId
                           LEFT JOIN Teacher sct ON sct.Id = r.SubstituteTeacherId
                           LEFT JOIN Users su ON su.Id = sct.UserId
                           WHERE t.Status = 'Published'
                             AND t.TenantId = @tenantid AND t.SchoolId = @schoolid AND t.CampusId = @campusid
                             AND t.AcademicYearId = @academicyearid
                           ORDER BY c.ClassroomName, PeriodIndex",
            Params = new Dictionary<string, object>
            {
                ["timetableweekday"] = v.TimetableWeekDay,
                ["timetabledate"] = v.TimetableDate,
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        new QuerySpec
        {
            Key = "tt-relief-candidates",
            Title = "Substitute desk - the campus's teacher candidates (unpaged dropdown feed)",
            Source = "TimeTableReliefRepository.GetCandidateTeachers",
            P95BudgetMs = 150,
            VolumeSql = "SELECT COUNT(*) FROM Teacher WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "teacher",
            IndexName = "ix_teacher_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "the dropdown is fed by a plain scoped list of the campus's teachers - small, but it runs on " +
                "every open of the relief dialog. The candidate is the scope triple; whether `teacher` already " +
                "has it is what `--advise` compares, so this records the access path rather than a finding.",
            Sql = @"SELECT t.Id AS TeacherId,
                                  COALESCE(u.FirstName || ' ' || u.LastName,'') AS TeacherName
                           FROM Teacher t
                           INNER JOIN Users u ON u.Id = t.UserId
                           WHERE t.TenantId = @tenantid AND t.SchoolId = @schoolid AND t.CampusId = @campusid
                           ORDER BY TeacherName",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "tt-teacher-subject-map",
            Title = "Substitute suggest - every campus teacher and the subjects they are assigned to",
            Source = "TimeTableReliefRepository.GetTeacherSubjectMap",
            P95BudgetMs = 250,
            VolumeSql = @"SELECT COUNT(*) FROM ClassroomSubjectTeacher cst
                            INNER JOIN Classroom c ON c.Id = cst.ClassroomId
                           WHERE c.TenantId = @tenantId AND c.SchoolId = @schoolId AND c.CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "classroomsubjectteacher",
            IndexName = "ix_classroomsubjectteacher_teacherid_year",
            IndexColumns = "teacherid, academicyearid",
            IndexRationale =
                "the read the substitute SUGGESTION is built from: it walks EVERY assignment of the campus and " +
                "groups them by teacher and subject name to build an in-memory map, then ranks candidates. It has " +
                "no teacher filter at all, so `teacherid` is only the GROUPING key - the candidate recorded here " +
                "is the same one the panel needs, and the interesting fact is that this statement's cost is the " +
                "campus's whole assignment set. `IndexColumns` deliberately mirrors the candidate's real columns.",
            Sql = @"SELECT cst.TeacherId,
                                  COALESCE(NULLIF(cs.CustomName,''), sub.Name, sub2.Name) AS SubjectName
                           FROM ClassroomSubjectTeacher cst
                           INNER JOIN Classroom c ON c.Id = cst.ClassroomId
                               AND c.TenantId = @tenantid AND c.SchoolId = @schoolid AND c.CampusId = @campusid
                           INNER JOIN AcademicGradeSubject ags ON ags.Id = cst.AcademicGradeSubjectId
                           LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                           LEFT JOIN Subject sub ON sub.Id = cs.SubjectId
                           LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                           LEFT JOIN Subject sub2 ON sub2.Id = cgs.SubjectId
                           WHERE COALESCE(NULLIF(cs.CustomName,''), sub.Name, sub2.Name) IS NOT NULL
                           GROUP BY cst.TeacherId, COALESCE(NULLIF(cs.CustomName,''), sub.Name, sub2.Name)",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "tt-audit-by-teacher",
            Title = "Teacher history modal - one teacher's audit trail (capped at 300 by the repository)",
            Source = "TeacherModuleAuditLogRepository.GetByTeacher",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT COUNT(*) FROM TeacherModuleAuditLog
                           WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                             AND TeacherId = @timetableTeacherId",
            MinVolume = 1,
            IndexTable = "teachermoduleauditlog",
            IndexName = "ix_tmal_teacher",
            IndexColumns = "tenantid, schoolid, campusid, teacherid, createdon DESC",
            IndexRationale =
                "this table is APPEND-ONLY and grows with every handover, absence, relief and assignment edit, " +
                "so it is the one table in the module whose growth is unbounded by a school year. It already " +
                "carries exactly the index this predicate needs - the scope triple, the teacher, and the " +
                "`CreatedOn DESC` this read orders by - so the spec declares the EXISTING index. The repository " +
                "caps the read at 300 rows, which is what makes the sort column part of the contract.",
            Sql = @"SELECT l.Id, l.TeacherId, l.AcademicYearId, l.Entity, l.Action, l.Detail,
                                  l.CreatedOn,
                                  COALESCE(actor.FirstName || ' ' || actor.LastName, '') AS ActorName
                           FROM TeacherModuleAuditLog l
                           LEFT JOIN Users actor ON actor.Id = l.CreatedBy
                           WHERE l.TenantId = @tenantid AND l.SchoolId = @schoolid
                             AND l.CampusId = @campusid AND l.TeacherId = @timetableteacherid
                             AND (@academicyearid = 0 OR l.AcademicYearId IS NULL OR l.AcademicYearId = @academicyearid)
                           ORDER BY l.CreatedOn DESC, l.Id DESC
                           LIMIT 300",
            Params = new Dictionary<string, object>
            {
                ["tenantid"] = v.TenantId,
                ["schoolid"] = v.SchoolId,
                ["campusid"] = v.CampusId,
                ["timetableteacherid"] = v.TimetableTeacherId,
                ["academicyearid"] = v.AcademicYearId,
            },
        },

        // ==================================================================
        // THE APPROVAL ENGINE (`appr-*`)
        //
        // `ApprovalWorkflowRepository` is the module's hot spot, and it is NOT a
        // `GenericRepository.GetAllAsync` grid: every read here is hand-written SQL with joins the
        // generic path cannot express, and both grids run a COUNT and a PAGE through ONE
        // `QueryMultipleAsync` (`countQuery;searchQuery`). So each grid is TWO specs - measuring only
        // the page reports half the cost the user waits for, which is the `lib-fine-*` precedent.
        //
        // ⚠️ THE INBOX'S WHERE CLAUSE IS FIVE CONDITIONS AND THREE OF THEM ARE EASY TO GET WRONG.
        // `s.StepNo = w.CurrentStep AND s.IsCompleted = false` is what makes a cycle's CURRENT step
        // the joined row - a seeded cycle whose chain has advanced past `CurrentStep`, or whose step
        // is already completed, is a workflow row the inbox cannot see while every table looks
        // populated. And the access rule is `(s.ApproverUserId = @userId OR ur.userid IS NOT NULL)`,
        // i.e. either the step is assigned to you OR your role owns it - transcribed from the
        // repository verbatim, including the `userrole` (lower-case) table name.
        //
        // ⚠️ `GetWorkflowHistory` FILTERS ON `RequestedBy`, NOT ON THE APPROVER. The two grids of
        // the same module look almost identical and match on different columns, which is why the
        // dataset fixture asserts a non-empty result for BOTH against one `ScopeVars.UserId`.
        // ==================================================================

        new QuerySpec
        {
            Key = "appr-pending-count",
            Title = "Approval inbox - total pending count (count half of one round trip)",
            Source = "ApprovalWorkflowRepository.GetPendingApprovals (count statement)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = @"SELECT COUNT(*) FROM approvalworkflow
                           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                             AND workflowstatus IN ('Pending', 'InProgress')",
            MinVolume = 1,
            IndexTable = "approvalworkflow",
            IndexName = "ix_approvalworkflow_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, workflowstatus",
            IndexRationale =
                "the inbox's driving predicate is scope + `WorkflowStatus IN ('Pending','InProgress')`, and the " +
                "two existing indexes cover those separately (`ix_approvalworkflow_tenantschoolcampus` and " +
                "`ix_approvalworkflow_status`). The composite is the candidate: whether it is worth an index " +
                "depends on what share of a campus's history is OPEN, and an approval history is mostly " +
                "closed, so the status is the selective half. A census candidate - it is only written into a " +
                "migration if `--advise` proves it on the measured statement.",
            Sql = @"
                SELECT COUNT(0)
                FROM ApprovalWorkflow w
                INNER JOIN ApprovalWorkflowStep s ON s.ApprovalWorkflowId = w.Id
                LEFT JOIN Users req ON req.Id = w.RequestedBy
                LEFT JOIN Roles r ON r.Id = s.RoleId
                LEFT JOIN userrole ur ON ur.roleid = r.Id AND ur.userid = @userId
                WHERE w.TenantId = @tenantId AND w.SchoolId = @schoolId AND w.CampusId = @campusId
                  AND w.WorkflowStatus IN ('Pending', 'InProgress')
                  AND s.IsCompleted = false
                  AND s.StepNo = w.CurrentStep
                  AND (s.ApproverUserId = @userId OR ur.userid IS NOT NULL)",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["userId"] = v.UserId,
            },
        },

        new QuerySpec
        {
            Key = "appr-pending-page",
            Title = "Approval inbox - first page (page half of one round trip)",
            Source = "ApprovalWorkflowRepository.GetPendingApprovals (search statement)",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(*) FROM approvalworkflow
                           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                             AND workflowstatus IN ('Pending', 'InProgress')",
            MinVolume = 1,
            IndexTable = "approvalworkflow",
            IndexName = "ix_approvalworkflow_tenantschoolcampus_status",
            IndexColumns = "tenantid, schoolid, campusid, workflowstatus",
            IndexRationale =
                "the page half of the same round trip - same predicate, plus `ORDER BY w.RequestedDate DESC` " +
                "and a LIMIT, so the sort is a second thing the index could carry. See the count spec: the " +
                "composite is a candidate, not a claim.",
            Sql = @"
                SELECT w.Id AS WorkflowId, s.Id AS StepId, s.StepNo, w.ModuleName, w.EntityId,
                       w.Remarks,
                       req.FirstName || ' ' || req.LastName AS RequestedByName,
                       w.RequestedDate AS RequestedDate,
                       w.WorkflowStatus,
                       s.RoleId, r.name AS RoleName
                FROM ApprovalWorkflow w
                INNER JOIN ApprovalWorkflowStep s ON s.ApprovalWorkflowId = w.Id
                LEFT JOIN Users req ON req.Id = w.RequestedBy
                LEFT JOIN Roles r ON r.Id = s.RoleId
                LEFT JOIN userrole ur ON ur.roleid = r.Id AND ur.userid = @userId
                WHERE w.TenantId = @tenantId AND w.SchoolId = @schoolId AND w.CampusId = @campusId
                  AND w.WorkflowStatus IN ('Pending', 'InProgress')
                  AND s.IsCompleted = false
                  AND s.StepNo = w.CurrentStep
                  AND (s.ApproverUserId = @userId OR ur.userid IS NOT NULL)
                ORDER BY w.RequestedDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["userId"] = v.UserId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "appr-myworkflows-count",
            Title = "My approval requests - total count (count half of one round trip)",
            Source = "ApprovalWorkflowRepository.GetWorkflowHistory (count statement)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = @"SELECT COUNT(*) FROM approvalworkflow
                           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                             AND requestedby = @userId",
            MinVolume = 1,
            IndexTable = "approvalworkflow",
            IndexName = "ix_approvalworkflow_requestedby_requesteddate",
            IndexColumns = "requestedby, requesteddate DESC",
            IndexRationale =
                "this grid matches on `RequestedBy` and then sorts by `RequestedDate DESC`, and the existing " +
                "`ix_approvalworkflow_requestedby` carries only the first - so the sort is a full sort of the " +
                "user's own history. The same shape as `V132`'s finding on `invoices` and `attendance` (the " +
                "scope/sort column pair), one table smaller. A candidate until a probe proves it.",
            Sql = @"
                SELECT COUNT(0)
                FROM ApprovalWorkflow w
                LEFT JOIN Users req ON req.Id = w.RequestedBy
                WHERE w.TenantId = @tenantId AND w.SchoolId = @schoolId AND w.CampusId = @campusId
                  AND w.RequestedBy = @userId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["userId"] = v.UserId,
            },
        },

        new QuerySpec
        {
            Key = "appr-myworkflows-page",
            Title = "My approval requests - first page (page half of one round trip)",
            Source = "ApprovalWorkflowRepository.GetWorkflowHistory (search statement)",
            P95BudgetMs = 200,
            VolumeSql = @"SELECT COUNT(*) FROM approvalworkflow
                           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                             AND requestedby = @userId",
            MinVolume = 1,
            IndexTable = "approvalworkflow",
            IndexName = "ix_approvalworkflow_requestedby_requesteddate",
            IndexColumns = "requestedby, requesteddate DESC",
            IndexRationale =
                "the same predicate as the count half plus `ORDER BY w.RequestedDate DESC` and a LIMIT - the " +
                "index that would let the sort be an index walk rather than a sort of the requester's whole " +
                "history. See the count spec.",
            Sql = @"
                SELECT w.Id AS WorkflowId, w.ModuleName, w.EntityId, w.WorkflowStatus,
                       req.FirstName || ' ' || req.LastName AS RequestedByName,
                       w.RequestedDate AS RequestedDate,
                       w.CompletedDate AS CompletedDate,
                       w.Remarks
                FROM ApprovalWorkflow w
                LEFT JOIN Users req ON req.Id = w.RequestedBy
                WHERE w.TenantId = @tenantId AND w.SchoolId = @schoolId AND w.CampusId = @campusId
                  AND w.RequestedBy = @userId
                ORDER BY w.RequestedDate DESC
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["userId"] = v.UserId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        new QuerySpec
        {
            Key = "appr-workflow-steps",
            Title = "Approval detail - the whole step trail of one cycle",
            Source = "ApprovalWorkflowRepository.GetWorkflowSteps(workflowId)",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT (SELECT COUNT(*) FROM approvalworkflowstep s
                                    JOIN approvalworkflow w ON w.id = s.approvalworkflowid
                                   WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId
                                     AND w.campusid = @campusId)",
            MinVolume = 1,
            IndexTable = "approvalworkflowstep",
            IndexName = "ix_approvalworkflowstep_workflowid_stepno",
            IndexColumns = "approvalworkflowid, stepno",
            IndexRationale =
                "`approvalworkflowstep` is the ONE child table in this module with no scope columns of its " +
                "own - it is reached by `ApprovalWorkflowId` alone, and the read then `ORDER BY s.StepNo`. " +
                "`ix_approvalworkflowstep_workflowid` covers the lookup but not the sort, so the pair is the " +
                "candidate. The chain is tens of rows per cycle in reality, so this is a convention index " +
                "rather than a measured defect unless a probe says otherwise.",
            Sql = @"
                SELECT s.Id, s.ApprovalWorkflowId, s.StepNo, s.RoleId, r.name as RoleName,
                       s.ApproverUserId,
                       app.FirstName || ' ' || app.LastName AS ApproverName,
                       s.Action, s.Comments, s.ActionDate AS ActionDate, s.IsCompleted
                FROM ApprovalWorkflowStep s
                LEFT JOIN Roles r ON r.Id = s.RoleId
                LEFT JOIN Users app ON app.Id = s.ApproverUserId
                WHERE s.ApprovalWorkflowId = @approvalWorkflowId
                ORDER BY s.StepNo",
            Params = new Dictionary<string, object>
            {
                ["approvalWorkflowId"] = v.ApprovalWorkflowId,
            },
        },

        new QuerySpec
        {
            Key = "appr-workflow-by-entity",
            Title = "Workflow history modal - every cycle of one (module, entity)",
            Source = "ApprovalWorkflowRepository.GetWorkflowsByEntity(moduleName, entityId, t, s, c)",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT COUNT(*) FROM approvalworkflow
                           WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "approvalworkflow",
            IndexName = "ix_approvalworkflow_moduleentity",
            IndexColumns = "modulename, entityid",
            IndexRationale =
                "⚠️ THIS ONE IS ALREADY INDEXED AND THE INDEX IS NOT SCOPE-FIRST, WHICH IS CORRECT HERE. " +
                "`ix_approvalworkflow_moduleentity` is `(modulename, entityid)` and the query's own leading " +
                "predicates are exactly those two - a scope-first composite would be useless because the " +
                "entity id in the query is more selective than the scope by orders of magnitude. Declared so " +
                "the advisor has the real index to compare against rather than proposing a second one.",
            Sql = @"
                SELECT w.Id, w.TenantId, w.SchoolId, w.CampusId, w.ModuleName, w.EntityId,
                       w.WorkflowStatus, w.CurrentStep,
                       w.RequestedBy,
                       req.FirstName || ' ' || req.LastName AS RequestedByName,
                       w.RequestedDate AS RequestedDate,
                       w.CompletedDate AS CompletedDate,
                       w.Remarks, w.CreatedBy, w.ModifiedBy
                FROM ApprovalWorkflow w
                LEFT JOIN Users req ON req.Id = w.RequestedBy
                WHERE w.ModuleName = @moduleName AND w.EntityId = @entityId
                  AND w.TenantId = @tenantId AND w.SchoolId = @schoolId AND w.CampusId = @campusId
                ORDER BY w.Id DESC",
            Params = new Dictionary<string, object>
            {
                ["moduleName"] = v.ApprovalModuleName,
                ["entityId"] = v.ApprovalEntityId,
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        new QuerySpec
        {
            Key = "appr-template-steps",
            Title = "Approval template editor - the chain of one template",
            Source = "ApprovalTemplateStepRepository.GetSteps(templateId)",
            P95BudgetMs = 150,
            VolumeSql = @"SELECT COUNT(*) FROM approvaltemplatestep ts
                            JOIN approvaltemplate t ON t.id = ts.approvaltemplateid
                           WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "approvaltemplatestep",
            IndexName = "ix_approvaltemplatestep_templateid",
            IndexColumns = "approvaltemplateid",
            IndexRationale =
                "the chain is read by `ApprovalTemplateId` alone and then sorted by `StepNo` - and the existing " +
                "`ix_approvaltemplatestep_templateid` is the right shape (a template has a handful of steps, so " +
                "the sort is free). Declared so the advisor compares against the real index rather than " +
                "proposing a second one.",
            Sql = @"
                SELECT ts.*, r.name AS RoleName
                FROM ApprovalTemplateStep ts
                LEFT JOIN Roles r ON r.Id = ts.RoleId
                WHERE ts.ApprovalTemplateId = @approvalTemplateId
                ORDER BY ts.StepNo",
            Params = new Dictionary<string, object>
            {
                ["approvalTemplateId"] = v.ApprovalTemplateId,
            },
        },

        new QuerySpec
        {
            Key = "appr-permission-lookup",
            Title = "HasPermission filter - the caller's granted permissions",
            Source = "HasPermissionAttribute -> RolePermissionRepository.Find(roleIds.Contains(RoleId) && PermissionId == p)",
            P95BudgetMs = 150,
            VolumeSql = "SELECT COUNT(*) FROM rolepermission",
            MinVolume = 1,
            IndexTable = "rolepermission",
            IndexName = "ix_rolepermission_roleid_permissionid",
            IndexColumns = "roleid, permissionid",
            IndexRationale =
                "⚠️ THIS QUERY IS NOT SCOPED AT ALL, AND THAT IS THE FINDING, NOT AN OVERSIGHT IN THE SPEC. " +
                "`RolePermissionRepository.Find` builds `RoleId = ANY(@roleIds) AND PermissionId = @permissionId` " +
                "with no tenant/school/campus predicate - `SqlBuilder` renders a `Contains` as `= ANY(...)`, " +
                "which the spec reproduces verbatim - so the filter scans every tenant's grants on EVERY " +
                "permission-gated request. The only index on the table is `ix_rolepermission_tenantid`, which " +
                "this predicate cannot use. `(roleid, permissionid)` is the candidate. The table is small " +
                "(four permissions x the tenant's roles) so this may well measure fine; it is recorded because " +
                "it is the app's hottest per-request lookup and it has never been measured against data.",
            Sql = @"
                SELECT * FROM rolepermission
                WHERE roleid = ANY(@roleIds) AND permissionid = @permissionId",
            Params = new Dictionary<string, object>
            {
                ["roleIds"] = v.RoleIds,
                // `Permission.CanEdit` - the gate the module's write endpoints carry, and the one a user
                // is most likely to lack.
                ["permissionId"] = (short)2,
            },
        },

        // =====================================================================
        // PASS: HR MASTER DATA, TRANSPORT DESKS, LIBRARY DESKS, MOMENT TYPES
        // AND THE EXAM SCHEDULE - the remaining grid endpoints whose driving
        // table ALREADY holds rows on the measured campus (15).
        //
        // The rule this pass follows is the one the coverage census recorded: a
        // spec over an EMPTY table reports SKIP, which reads as coverage while
        // measuring nothing - so a table with no rows is seeded or recorded as
        // owed, never specced.
        //
        // DELIBERATELY NOT SPECCED HERE (recorded, not forgotten):
        //   * `parent` - the grid is `ParentRepository.GetAllParentUserInfo`, an
        //     INNER JOIN to `users` ON the SAME scope triple. The campus holds one
        //     `parent` row and ZERO users that resolve to it, so the grid is EMPTY
        //     on this campus and a spec would report SKIP. Owed a parent-login
        //     seeder (or a campus whose parents have logins), not a spec.
        //   * `gradingscheme` - `GradingSchemeController` carries a `WrapSearch`, but
        //     the campus holds 0 rows, so the same rule applies.
        //   * `momenttype` IS specced below even though `school-web` has no page that
        //     loads it (the `moment` screens resolve their types through the moment
        //     list itself). It is kept because it is a live `WrapSearch` endpoint and
        //     the route is reachable by any API client.
        // =====================================================================

        // The HR designation grid. `DesignationRepository.GetAll(page, t, s, c)` issues the
        // count and the data query through ONE `QueryMultipleAsync`, and BOTH carry an
        // INNER JOIN to Department - so a designation whose department does not resolve is
        // invisible on this screen (the same shape as `inv-grn-page`'s PO join).
        new QuerySpec
        {
            Key = "designation-page",
            Title = "Designations master grid",
            Source = "DesignationRepository.GetAll(page, t, s, c) - searchQuery + page.OrderBy",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM designation d INNER JOIN department dept ON d.DepartmentId = dept.Id " +
                        "WHERE d.TenantId = @tenantId AND d.SchoolId = @schoolId AND d.CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "designation",
            IndexName = "ix_designation_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "The grid's predicate is the scope triple, which `ix_designation_tenantschoolcampus` serves, and the " +
                "INNER JOIN to Department leads `ix_designation_departmentid`. A campus owns TENS of designations, so " +
                "this is a reachability spec rather than a volume one; recorded because the table had never been " +
                "measured at all.",
            Sql = @"
                SELECT d.*, dept.Name AS DepartmentName
                FROM designation d
                INNER JOIN Department dept ON d.DepartmentId = dept.Id
                WHERE d.TenantId = @tenantId AND d.SchoolId = @schoolId AND d.CampusId = @campusId
                ORDER BY Name
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The count half of the SAME round trip - it re-runs the Department join, so the user
        // waits for it too. Kept separate for the reason recorded on `lib-fine-count`.
        new QuerySpec
        {
            Key = "designation-count",
            Title = "Designations master grid - total count",
            Source = "DesignationRepository.GetAll(page, t, s, c) - countQuery",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM designation d INNER JOIN department dept ON d.DepartmentId = dept.Id " +
                        "WHERE d.TenantId = @tenantId AND d.SchoolId = @schoolId AND d.CampusId = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT COUNT(*)
                FROM designation d
                INNER JOIN Department dept ON d.DepartmentId = dept.Id
                WHERE d.TenantId = @tenantId AND d.SchoolId = @schoolId AND d.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ⚠️ THE VEHICLE-ASSIGNMENT DESK NOW PAGES AND COUNTS ITS FILTERED SET (fixed Sept 2026).
        // `TransportVehicleAssignmentRepository.GetAll(page, t, s, c)` used to take the
        // `DataTablePageInfo` and use NONE of it - no search clause, no `ORDER BY`, no
        // `LIMIT`/`OFFSET` - and reported `data.Count = list.Count`, i.e. the count of the rows it
        // had just loaded rather than the filtered total. So "page 1, size 50" was the campus's WHOLE
        // assignment list and the grid's search box did nothing. It is fixed to the contract the
        // route declares (`[FromQuery] PagingInfo` + `WrapSearch`), mirroring
        // `TransportStudentAssignmentRepository.GetAll(page, ...)`: ONE shared from/where fragment,
        // the four LEFT JOINs carried by BOTH statements (so a search on `v.VehicleNumber` or
        // `r.RouteName` resolves in the count too), the `page.Search && page.WhereCondition` guard
        // that stops an `AND ()` 500, a null-safe `page.OrderBy?.Trim()` falling back to
        // `ORDER BY va.Id DESC`, and `LIMIT`/`OFFSET` guarded by `PageSize > 0`. The two specs below
        // reproduce the two statements the method now sends; `Count` is the count query, not the page.
        // The UNPAGED overload is untouched and still serves `[HttpGet("list")]` - a DIFFERENT
        // contract with its own consumers, not a second copy of this page.
        new QuerySpec
        {
            Key = "transport-vehassignment-page",
            Title = "Vehicle assignments - the campus desk's first page",
            Source = "TransportVehicleAssignmentRepository.GetAll(page, t, s, c) - dataQuery",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM TransportVehicleAssignment WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "TransportVehicleAssignment",
            IndexName = "ix_transportvehicleassignment_tenantcampus",
            IndexColumns = "tenantid, campusid",
            IndexRationale =
                "⚠️ THE TABLE HAS NO SCOPE TRIPLE INDEX: the closest is `ix_transportvehicleassignment_tenantcampus` " +
                "(tenant + campus, NO school) while every sibling in the module carries the full " +
                "`(tenantid, schoolid, campusid)` (`ix_transportroute_tenantschoolcampus`, " +
                "`ix_transportvehicle_tenantschoolcampus`). So the page's predicate is served only as a leftmost " +
                "prefix, and the sort column (`va.Id DESC`, the grid's default) is in no index at all. Recorded, " +
                "not fixed: a campus owns TENS of assignments and the spec measures it, so an index has to be " +
                "EARNED by a failing probe rather than proposed for symmetry.",
            Sql = @"
                SELECT va.*, v.VehicleNumber, d.Name AS DriverName, a.Name AS AttendantName, r.RouteName
                FROM TransportVehicleAssignment va
                LEFT JOIN TransportVehicle v ON va.VehicleId = v.Id
                LEFT JOIN TransportDriver d ON va.DriverId = d.Id
                LEFT JOIN TransportAttendant a ON va.AttendantId = a.Id
                LEFT JOIN TransportRoute r ON va.RouteId = r.Id
                WHERE va.TenantId = @tenantId AND va.SchoolId = @schoolId AND va.CampusId = @campusId
                ORDER BY va.Id DESC
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The count half - it runs BEFORE the page renders and carries the same four LEFT JOINs on
        // purpose, so the search columns resolve here as well. `data.Count` is THIS number, which is
        // what `recordsTotal` reports - so it is the assertion that the desk counts its filtered set
        // rather than the rows it happens to be showing.
        new QuerySpec
        {
            Key = "transport-vehassignment-count",
            Title = "Vehicle assignments - the filtered total",
            Source = "TransportVehicleAssignmentRepository.GetAll(page, t, s, c) - countQuery",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM TransportVehicleAssignment WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT COUNT(*) FROM TransportVehicleAssignment va
                LEFT JOIN TransportVehicle v ON va.VehicleId = v.Id
                LEFT JOIN TransportDriver d ON va.DriverId = d.Id
                LEFT JOIN TransportAttendant a ON va.AttendantId = a.Id
                LEFT JOIN TransportRoute r ON va.RouteId = r.Id
                WHERE va.TenantId = @tenantId AND va.SchoolId = @schoolId AND va.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The route's stop list. `TransportRouteStopRepository.GetAllByRoute(page, t, s, c, routeId)`
        // goes through `GenericRepository.GetAllAsync` (so the SQL is the shared shape) and orders
        // the result IN MEMORY by `Sequence` - there is no ORDER BY in the statement. The route is a
        // route parameter, so the spec is handed the campus's fullest route rather than filtering by
        // scope alone.
        new QuerySpec
        {
            Key = "transport-route-stop-page",
            Title = "Route stops - the stop list of the route the user opened",
            Source = "TransportRouteStopRepository.GetAllByRoute(page, t, s, c, routeId) -> GenericRepository.GetAllAsync",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM transportroutestop WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId AND RouteId = @routeId",
            MinVolume = 1,
            IndexTable = "transportroutestop",
            IndexName = "ix_transportroutestop_tenantcampusroute",
            IndexColumns = "tenantid, campusid, routeid",
            IndexRationale =
                "The predicate is the scope triple AND `RouteId`, which `ix_transportroutestop_tenantcampusroute` " +
                "serves as a leftmost prefix. A route carries a DOZEN stops, so this is reachability rather than " +
                "volume; recorded because the table had never been measured.",
            Sql = @"
                SELECT * FROM transportroutestop
                WHERE TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId
                  AND RouteId = @routeId
                LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["routeId"] = v.TransportRouteId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        },

        // The acquisitions desk. `LibraryAcquisitionRepository.GetAllPaged` issues count + data in ONE
        // `QueryMultipleAsync` and BOTH carry the vendor LEFT JOIN - deliberately, so a DataTables
        // search on `VendorName` resolves in the count as well. The `status` filter is optional and
        // omitted here (the spec reproduces the unfiltered first page the page requests on load).
        // ⚠️ `LIMIT`/`OFFSET` ARE INTERPOLATED LITERALS in that method, so the spec spells them as
        // literals too - the parameter form would describe a statement the server never sends.
        new QuerySpec
        {
            Key = "lib-acquisition-page",
            Title = "Library acquisitions - the request list",
            Source = "LibraryAcquisitionRepository.GetAllPaged(page, t, s, c, status=null) - dataSql",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM libraryacquisition WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "libraryacquisition",
            IndexName = "ix_libraryacquisition_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "The predicate is the scope triple and the sort is `la.Id DESC` (the page's default), which " +
                "`ix_libraryacquisition_tenantschoolcampus` serves; the vendor join leads the vendor's own PK. A " +
                "campus raises TENS of acquisitions, so this is reachability rather than volume.",
            Sql = @"
                SELECT la.*, lv.Name AS VendorName
                FROM LibraryAcquisition la
                LEFT JOIN LibraryVendor lv ON lv.Id = la.VendorId
                WHERE la.TenantId = @tenantId AND la.SchoolId = @schoolId AND la.CampusId = @campusId
                ORDER BY la.Id DESC
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The count half of the SAME round trip. It carries the vendor join on purpose (so the search
        // column resolves), which is what makes it worth measuring separately.
        new QuerySpec
        {
            Key = "lib-acquisition-count",
            Title = "Library acquisitions - total count",
            Source = "LibraryAcquisitionRepository.GetAllPaged(page, t, s, c, status=null) - countSql",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM libraryacquisition WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT COUNT(*) FROM LibraryAcquisition la
                LEFT JOIN LibraryVendor lv ON lv.Id = la.VendorId
                WHERE la.TenantId = @tenantId AND la.SchoolId = @schoolId AND la.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The reading-list desk. `GetAllPaged` also issues count + data in ONE `QueryMultipleAsync`, but
        // the two halves are ASYMMETRIC: the count is a plain single-table count, while the data query
        // carries EIGHT LEFT JOINs plus a correlated item count and a concatenated grade-subject name.
        // Both are measured, because both run before the grid renders.
        new QuerySpec
        {
            Key = "lib-reading-list-page",
            Title = "Library reading lists - the list grid",
            Source = "LibraryReadingListRepository.GetAllPaged(page, t, s, c) - dataSql (8 LEFT JOINs + correlated ItemCount)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM libraryreadinglist WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId AND IsActive = true",
            MinVolume = 1,
            IndexTable = "libraryreadinglist",
            IndexName = "ix_libraryreadinglist_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "The predicate is the scope triple plus `IsActive`, and the sort is `lrl.Name` - " +
                "`ix_libraryreadinglist_tenantschoolcampus` is the only candidate and cannot order `Name`, so a " +
                "campus with many lists would sort all of them. Its sibling `ix_libraryreadinglist_academicgradesubjectid` " +
                "serves the grade-subject join. A campus owns TENS of lists, so no index is PROPOSED here - the spec is " +
                "recorded because the table had never been measured.",
            Sql = @"
                SELECT lrl.*,
                    CASE WHEN ags.Id IS NULL THEN NULL
                         ELSE cg.Name || ' - ' || COALESCE(NULLIF(cs.CustomName, ''), s2.Name, s.Name)
                    END AS AcademicGradeSubjectName,
                    (SELECT COUNT(*) FROM LibraryReadingListItem lrla WHERE lrla.ReadingListId = lrl.Id) AS ItemCount,
                    e.FirstName || ' ' || e.LastName AS CreatedByEmployeeName
                FROM LibraryReadingList lrl
                LEFT JOIN AcademicGradeSubject ags ON ags.Id = lrl.AcademicGradeSubjectId
                LEFT JOIN AcademicGrade ag ON ag.Id = ags.AcademicGradeId
                left join curriculumgrade cg on ag.curriculumgradeid = cg.id
                LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                LEFT JOIN Subject s ON s.Id = cgs.SubjectId
                LEFT JOIN Subject s2 ON s2.Id = cs.SubjectId
                LEFT JOIN Employee e ON e.Id = lrl.CreatedByEmployeeId
                WHERE lrl.TenantId = @tenantId AND lrl.SchoolId = @schoolId AND lrl.CampusId = @campusId
                  AND lrl.IsActive = true
                ORDER BY lrl.Name
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ⚠️ THE COUNT'S ASYMMETRY IS THE POINT: it is a plain single-table count with NO joins, so it
        // cannot use the same search columns the data query resolves. It is measured because it is the
        // other half of one round trip.
        new QuerySpec
        {
            Key = "lib-reading-list-count",
            Title = "Library reading lists - total count",
            Source = "LibraryReadingListRepository.GetAllPaged(page, t, s, c) - countSql",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM libraryreadinglist WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId AND IsActive = true",
            MinVolume = 1,
            Sql = @"
                SELECT COUNT(*) FROM LibraryReadingList lrl
                WHERE lrl.TenantId = @tenantId AND lrl.SchoolId = @schoolId AND lrl.CampusId = @campusId
                  AND lrl.IsActive = true",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The moment-type list. `MomentTypeRepository.GetAll(page, ...)` IGNORES paging - it calls the
        // UNPAGED overload and sets `Count = list.Count` - so this is an unpaged list even though the
        // route carries a `PagingInfo`. The statement is a CTE that de-duplicates by NAME and prefers
        // the most specific scope, INCLUDING the system-wide (0,0,0) defaults - which is why it cannot
        // use the `ScopeList` factory (that factory cannot express a CTE or the 0/0/0 branch).
        new QuerySpec
        {
            Key = "moment-type-list",
            Title = "Moment types - the campus list plus the system-wide defaults",
            Source = "MomentTypeRepository.GetAll(page, t, s, c) -> the unpaged GetAll (CTE, paging ignored)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(DISTINCT Name) FROM momenttype mt WHERE " +
                        "(mt.TenantId = @tenantId AND mt.SchoolId = @schoolId AND mt.CampusId = @campusId) " +
                        "OR (mt.TenantId = @tenantId AND mt.SchoolId = @schoolId AND mt.CampusId = 0) " +
                        "OR (mt.TenantId = @tenantId AND mt.CampusId = 0) " +
                        "OR (mt.TenantId = 0 AND mt.SchoolId = 0 AND mt.CampusId = 0)",
            MinVolume = 1,
            IndexTable = "momenttype",
            IndexName = "ix_momenttype_tenantid",
            IndexColumns = "tenantid",
            IndexRationale =
                "The WHERE is a four-branch OR over the scope columns, and the only index is the single-column " +
                "`ix_momenttype_tenantid` - the 0/0/0 branch cannot use it at all. The table holds SEVEN rows on " +
                "this campus, so no composite is proposed; recorded because the read is unpaged and its cost is the " +
                "whole table however small that is today.",
            Sql = @"
                WITH ranked_moment_types AS
                (
                    SELECT
                        mt.*,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY mt.Name
                            ORDER BY
                                CASE
                                    WHEN mt.TenantId = @tenantId
                                     AND mt.SchoolId = @schoolId
                                     AND mt.campusId = @campusId THEN 1
                                    WHEN mt.TenantId = @tenantId
                                     AND mt.SchoolId = @schoolId
                                     AND mt.campusId = 0 THEN 2
                                    WHEN mt.TenantId = @tenantId
                                     AND mt.campusId = 0 THEN 3
                                    ELSE 4
                                END
                        ) AS rn
                    FROM momenttype mt
                    WHERE
                        (
                            (mt.TenantId = @tenantId AND mt.SchoolId = @schoolId AND mt.campusId = @campusId)
                            OR
                            (mt.TenantId = @tenantId AND mt.SchoolId = @schoolId AND mt.campusId = 0)
                            OR
                            (mt.TenantId = @tenantId AND mt.campusId = 0)
                            OR
                            (mt.TenantId = 0 AND mt.SchoolId = 0 AND mt.campusId = 0)
                        )
                )
                SELECT *
                FROM ranked_moment_types
                WHERE rn = 1",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The exam-schedule grid. FOURTEEN LEFT JOINs on BOTH the count and the page, and the two are
        // SEPARATE round trips (`ExecuteScalarAsync` then `QueryAsync`) - so both are specced.
        // ⚠️ THE PAGING IS `OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY`, NOT `LIMIT`/`OFFSET`.
        // This is the grid J7 found the campus-drift defect on, so the route matters: it is a POST
        // with a JSON DataTables body (`[HttpPost]` + `[FromBody] PagingInfo`).
        new QuerySpec
        {
            Key = "exam-schedule-page",
            Title = "Exam schedules - the schedule grid",
            Source = "ExamScheduleRepository.GetAll(page, t, s, c) - selectSql (14 LEFT JOINs, FETCH NEXT)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM examschedule WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId",
            MinVolume = 1,
            IndexTable = "examschedule",
            IndexName = "ix_examschedule_tsc",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "The page predicate is the scope triple, which `ix_examschedule_tsc` serves, and every one of the " +
                "fourteen joins leads a primary key. The sort is `es.ExamDate, es.StartTime`, which the scope index " +
                "cannot provide - a campus with a term's worth of schedules would sort its whole set. Three rows here, " +
                "so no index is PROPOSED; the shape (14 joins x 2 round trips) is what is recorded.",
            Sql = @"
                SELECT es.*, ac.Name AS ComponentName, c.ClassroomName AS ClassroomName,
                    r.Name AS RoomName, COALESCE(tu.FirstName || ' ' || tu.LastName, '') AS SupervisorName,
                    sac.TermId AS TermId, tm.Name AS TermName,
                    sac.AcademicGradeSubjectId AS AcademicGradeSubjectId,
                    sac.AssessmentMethodId AS AssessmentMethodId,
                    COALESCE(NULLIF(cs.CustomName, ''),s.Name,s2.Name ) AS SubjectName, cg.Name AS GradeName,
                    tm.AcademicYearId AS AcademicYearId
                FROM ExamSchedule es
                LEFT JOIN SubjectAssessmentComponent sac ON sac.Id = es.SubjectAssessmentComponentId
                LEFT JOIN AssessmentComponent ac ON ac.Id = sac.AssessmentComponentId
                LEFT JOIN Classroom c ON c.Id = es.ClassroomId
                LEFT JOIN Room r ON r.Id = es.RoomId
                LEFT JOIN Teacher t ON t.Id = es.SupervisorTeacherId
                LEFT JOIN Users tu ON tu.Id = t.UserId
                LEFT JOIN AcademicGradeSubject ags ON ags.Id = sac.AcademicGradeSubjectId
                LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                LEFT JOIN Subject s ON s.Id = cs.SubjectId
                LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                LEFT JOIN Subject s2 ON s2.Id = cgs.SubjectId
                LEFT JOIN AcademicGrade ag ON ag.Id = ags.AcademicGradeId
                LEFT JOIN CurriculumGrade cg ON cg.Id = ag.CurriculumGradeId
                LEFT JOIN Terms tm ON tm.Id = sac.TermId
                WHERE es.TenantId = @tenantId AND es.SchoolId = @schoolId AND es.CampusId = @campusId
                ORDER BY es.ExamDate, es.StartTime
                OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["offset"] = DefaultOffset,
                ["limit"] = DefaultPageSize,
            },
        },

        // The count half: the SAME fourteen LEFT JOINs in a separate round trip. This is the half that
        // decides how many pages the grid believes it has, and it is the reason `exam-schedule-page`
        // alone would report half the wait.
        new QuerySpec
        {
            Key = "exam-schedule-count",
            Title = "Exam schedules - total count (the same 14 joins)",
            Source = "ExamScheduleRepository.GetAll(page, t, s, c) - countSql",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM examschedule WHERE TenantId = @tenantId " +
                        "AND SchoolId = @schoolId AND CampusId = @campusId",
            MinVolume = 1,
            Sql = @"
                SELECT COUNT(1)
                FROM ExamSchedule es
                LEFT JOIN SubjectAssessmentComponent sac ON sac.Id = es.SubjectAssessmentComponentId
                LEFT JOIN AssessmentComponent ac ON ac.Id = sac.AssessmentComponentId
                LEFT JOIN Classroom c ON c.Id = es.ClassroomId
                LEFT JOIN Teacher t ON t.Id = es.SupervisorTeacherId
                LEFT JOIN Users tu ON tu.Id = t.UserId
                LEFT JOIN AcademicGradeSubject ags ON ags.Id = sac.AcademicGradeSubjectId
                LEFT JOIN CampusSubject cs ON cs.Id = ags.CampusSubjectId
                LEFT JOIN Subject s ON s.Id = cs.SubjectId
                LEFT JOIN CurriculumGradeSubject cgs ON cgs.Id = ags.CurriculumGradeSubjectId
                LEFT JOIN Subject s2 ON s2.Id = cgs.SubjectId
                LEFT JOIN AcademicGrade ag ON ag.Id = ags.AcademicGradeId
                LEFT JOIN CurriculumGrade cg ON cg.Id = ag.CurriculumGradeId
                LEFT JOIN Terms tm ON tm.Id = sac.TermId
                WHERE es.TenantId = @tenantId AND es.SchoolId = @schoolId AND es.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // ==================================================================================================
        // THE FIVE GRIDS WHOSE TABLES WERE EMPTY ON THE MEASURED CAMPUS (seeded Sept 2026)
        //
        // `parent`, `gradingscheme`, `studentfeeassignment`, `studentfeediscount` and `latefeecharges`
        // held ZERO rows on campus 15 - so a spec over any of them reported SKIP, which reads as "not
        // measured yet" for a screen the application ships. They are seeded now
        // (`EmptyGridTablesSeeder`; campus 15 -> 3 parents/3 logins, 2 schemes x 2 rules, 6 fee
        // assignments, 6 discounts, 1 policy + 5 assessments + 5 charges) and specced below.
        //
        // ⚠️ AND THE SEEDING IS WHY THE FIRST OF THEM IS A REAL FIX RATHER THAN A FIXTURE.
        // `ParentRepository.GetAllParentUserInfo` selected `u.Role` - a column `users` DOES NOT HAVE
        // (`roles` lives behind `userrole`) - so PostgreSQL rejected the statement at PARSE time and the
        // endpoint was a 500 for EVERY caller on EVERY campus, empty or not. The role is now a
        // correlated subquery over `userrole` -> `roles`, which is why the page spec below carries a
        // subquery and why the fixture asserts the grant resolves. **Transcription is a review: this is
        // the second time a repository's own SQL has been found broken by writing its spec down.**
        // ==================================================================================================

        // The PARENT grid. Count and page are ONE `QueryMultipleAsync` (`countQuery;searchQuery`), and
        // both carry the same `INNER JOIN Users` on the SAME (tenant, school, campus) triple - which is
        // the reachability gate the seeder had to satisfy by creating a LOGIN per parent.
        // ⚠️ THE ROUTE HAS NO SCREEN (nothing in `school-web` GETs `api/{t}/{s}/{c}/parent`; the two
        // call sites POST to it from the student screens), so no client supplies a sort column and the
        // method emits NO `ORDER BY` at all - its clause is appended only when `page.OrderBy` survives
        // `SqlGuard`, and `DataTablePageInfo.OrderBy` is initialised to EMPTY rather than null precisely
        // so that case is survivable. The spec measures that statement, not an invented sort.
        new QuerySpec
        {
            Key = "parent-grid-count",
            Title = "Parents - the filtered total (the grid's count half)",
            Source = "ParentRepository.GetAllParentUserInfo - countQuery (one QueryMultipleAsync with the page)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM parent t INNER JOIN users u ON t.userid = u.id " +
                        "AND t.tenantid = u.tenantid AND t.schoolid = u.schoolid AND t.campusid = u.campusid " +
                        "WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "parent",
            IndexName = "ix_parent_userid",
            IndexColumns = "userid",
            IndexRationale =
                "The predicate is the scope triple PLUS the `users` join, and `parent` carries no scope composite " +
                "- only `ix_parent_tenantid` (tenantid alone) and `ix_parent_userid`, which is what the join " +
                "actually leads on (`t.userid = u.id`). A campus holds TENS of parents, so the statement is " +
                "reachability rather than volume and no index is proposed; the table had never been measured " +
                "because it was empty here.",
            Sql = @"
                SELECT COUNT(0) FROM Parent t INNER JOIN Users u on t.UserId = u.Id
                and t.TenantId = u.TenantId and t.SchoolId = u.SchoolId and t.CampusId = u.CampusId
                WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The page half - and the STATEMENT THAT USED TO BE A 500. `u.Role` is gone; the role is a
        // scalar subquery so it cannot multiply the row (a `LEFT JOIN userrole` would, because `userrole`
        // has no unique index on (userid, roleid) and the count does not join it - the two halves would
        // then disagree, which is the documented DataTables contract this repo keeps re-learning).
        new QuerySpec
        {
            Key = "parent-grid-page",
            Title = "Parents - the parent grid's first page (the statement that was a 500)",
            Source = "ParentRepository.GetAllParentUserInfo - searchQuery (role as a correlated subquery)",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM parent t INNER JOIN users u ON t.userid = u.id " +
                        "AND t.tenantid = u.tenantid AND t.schoolid = u.schoolid AND t.campusid = u.campusid " +
                        "WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "parent",
            IndexName = "ix_parent_userid",
            IndexColumns = "userid",
            IndexRationale =
                "⚠️ THIS SPEC IS THE REGRESSION GUARD, NOT A PERFORMANCE QUESTION. The statement it replaces " +
                "named `u.Role`, a column `users` does not have, so PostgreSQL rejected it at PARSE time and " +
                "the endpoint answered 500 for every caller on every campus - the reason the parent grid could " +
                "never render, and invisible to a data check because an empty table looks the same as a broken " +
                "query. The role is a correlated subquery over `userrole` -> `roles` now (`ix_userrole_userid` " +
                "serves it). Tens of rows: no index is proposed.",
            Sql = @"
                SELECT t.Id,t.Nic,t.Address,
                    (SELECT r.Name FROM UserRole ur INNER JOIN Roles r on ur.RoleId = r.Id
                      WHERE ur.UserId = u.Id AND ur.RoleId = 5 AND ur.IsActive = true
                      ORDER BY ur.Id LIMIT 1) as Role,
                    u.Email,u.Password,u.Password as ConfirmPassword,
                    u.FirstName,u.LastName,u.Mobile,u.Id as UserId,t.TenantId,t.SchoolId,t.CampusId
                FROM Parent t INNER JOIN Users u on t.UserId = u.Id
                and t.TenantId = u.TenantId and t.SchoolId = u.SchoolId and t.CampusId = u.CampusId
                WHERE t.TenantId = @tenantId AND t.SchoolId = @schoolId AND t.CampusId = @campusId
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The GRADING SCHEME grid. Count and page come back in one `QueryMultipleAsync`.
        // ⚠️ THE TWO HALVES READ THE SAME ROWS FROM DIFFERENT SHAPES, DELIBERATELY: the count joins
        // nothing (`COUNT(0) FROM gradingscheme g WHERE scope`) while the page `LEFT JOIN gradingrule`
        // and lets Dapper collapse the scheme x rule rows through a `Dictionary<long, GradingSchemeDto>`
        // keyed on the scheme id. So a scheme with NO rules is still REACHABLE (it renders with an empty
        // band list) and both halves report SCHEMES - which is exactly why the fixture asserts the band
        // vocabulary rather than reachability.
        new QuerySpec
        {
            Key = "grading-scheme-count",
            Title = "Grading schemes - the filtered total",
            Source = "GradingSchemeRepository.GetAll(page, t, s, c) - countQuery (no join at all)",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM gradingscheme WHERE tenantid = @tenantId " +
                        "AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "gradingscheme",
            IndexName = "ix_grading_tenantid",
            IndexColumns = "tenantid",
            IndexRationale =
                "`gradingscheme` carries `ix_grading_tenantid` (tenant only) and `ix_grading_name`; there is NO " +
                "scope composite, so the school/campus half of the predicate is a filter over the tenant's rows. " +
                "A campus holds a HANDFUL of schemes (the fixture seeds 2 of them + 2 rules each) - so this is " +
                "reachability, and an index would have to be EARNED by a failing probe rather than proposed " +
                "because the column list looks incomplete.",
            Sql = @"
                SELECT COUNT(0) FROM gradingscheme g
                WHERE g.tenantid = @tenantId and g.schoolid = @schoolId and g.campusid = @campusId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The page half, including the LEFT JOIN whose rows Dapper folds back into the schemes. The sort
        // is `g.Code` because that is the grid's FIRST column and the page declares no `order` - so
        // DataTables sorts by column 0, which carries `name: 'g.Code'`.
        new QuerySpec
        {
            Key = "grading-scheme-page",
            Title = "Grading schemes - the grid's first page (page LEFT JOINs the bands, count does not)",
            Source = "GradingSchemeRepository.GetAll(page, t, s, c) - searchQuery",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM gradingscheme WHERE tenantid = @tenantId " +
                        "AND schoolid = @schoolId AND campusid = @campusId",
            MinVolume = 1,
            IndexTable = "gradingscheme",
            IndexName = "ix_grading_name",
            IndexColumns = "name",
            IndexRationale =
                "The sort is `g.Code` (the grid's first column - the page declares no `order`) and the predicate is " +
                "the scope triple, which no index covers: `ix_grading_name` indexes the NAME, not the code, so the " +
                "statement sorts every graded scheme of the tenant and then filters the campus. Two rows here. " +
                "Recorded rather than fixed - a campus owns a handful of schemes and an index has to be earned.",
            Sql = @"
                SELECT g.Id, g.Code, g.Name, g.Description, g.CalculationMethod, g.RoundingMethod, g.PassingScore, g.TenantId, g.SchoolId, g.CampusId,
                    gr.Id as gradingRuleId, gr.Id, gr.GradingSchemeId, gr.MinimumScore, gr.MaximumScore, gr.Grade, gr.GradePoint, gr.Remark, gr.Color, gr.DisplayOrder
                FROM GradingScheme g
                LEFT JOIN GradingRule gr ON gr.GradingSchemeId = g.Id
                WHERE g.TenantId = @tenantId
                AND g.SchoolId = @schoolId
                AND g.CampusId = @campusId
                ORDER BY g.Code
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        },

        // The FEE ASSIGNMENT grid. Count and page in ONE `QueryMultipleAsync`, and BOTH carry the whole
        // join chain - six INNER JOINs (enrolment, student, CLASSROOM, year, grade, fee structure) plus a
        // LEFT JOIN to the curriculum grade. ⚠️ `studentenrollment.classroomid` is NULLABLE, so the
        // `INNER JOIN classroom` is a genuine reachability gate rather than a formality: a row whose
        // enrolment has no classroom is counted and cannot render.
        // The grid also posts the year it is showing (`academicYearId`), which becomes `AND se.academicyearid`.
        new QuerySpec
        {
            Key = "fee-assignment-count",
            Title = "Fee assignments - the filtered total for the year on screen",
            Source = "StudentFeeAssignmentRepository.GetAll(page, t, s, c, academicYearId) - countQuery",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM studentfeeassignment sfa " +
                        "INNER JOIN studentenrollment se ON se.id = sfa.studentenrollmentid " +
                        "WHERE sfa.tenantid = @tenantId AND sfa.schoolid = @schoolId " +
                        "AND sfa.campusid = @campusId AND se.academicyearid = @academicYearId",
            MinVolume = 1,
            IndexTable = "studentfeeassignment",
            IndexName = "ix_studentfeeassignment_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "`studentfeeassignment` DOES carry the scope composite " +
                "(`ix_studentfeeassignment_tenantschoolcampus`) plus `ix_studentfeeassignment_enrollment` for the " +
                "join, and `ux_studentfeeassignment_activeenrollment` is a PARTIAL unique index on " +
                "(tenantid, studentenrollmentid) WHERE status = 1 - i.e. ONE active assignment per enrolment, " +
                "which is what stops the invoice engine billing a student twice. The count leads on the scope " +
                "composite; the year filter is reached through the enrolment join.",
            Sql = @"
                SELECT COUNT(0)
                FROM studentfeeassignment sfa
                INNER JOIN studentenrollment se ON se.id = sfa.studentenrollmentid
                INNER JOIN student s ON s.id = se.studentid
                INNER JOIN classroom c ON c.id = se.classroomid
                INNER JOIN academicyear ay ON ay.id = se.academicyearid
                INNER JOIN academicgrade ag ON ag.id = c.academicgradeid
                LEFT JOIN curriculumgrade cg ON cg.id = ag.curriculumgradeid
                INNER JOIN feestructure fs ON fs.id = sfa.feestructureid
                WHERE sfa.tenantid = @tenantId AND sfa.schoolid = @schoolId AND sfa.campusid = @campusId
                AND se.academicyearid = @academicYearId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // The page half. The sort is the grid's first column (`s.Name` - the page declares no `order`, so
        // DataTables sorts column 0) and the projection carries every column the grid renders, which is
        // why `classroomname` / `academicYearName` / `feeStructureName` are all joined for.
        new QuerySpec
        {
            Key = "fee-assignment-page",
            Title = "Fee assignments - the grid's first page (6 INNER joins + 1 LEFT)",
            Source = "StudentFeeAssignmentRepository.GetAll(page, t, s, c, academicYearId) - searchQuery",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentfeeassignment sfa " +
                        "INNER JOIN studentenrollment se ON se.id = sfa.studentenrollmentid " +
                        "WHERE sfa.tenantid = @tenantId AND sfa.schoolid = @schoolId " +
                        "AND sfa.campusid = @campusId AND se.academicyearid = @academicYearId",
            MinVolume = 1,
            IndexTable = "studentfeeassignment",
            IndexName = "ix_studentfeeassignment_tenantschoolcampus",
            IndexColumns = "tenantid, schoolid, campusid",
            IndexRationale =
                "The page leads on the scope composite exactly as its count does, and every join leads a primary " +
                "key; the sort (`s.Name`) is served by the student table's own name index. The interesting half is " +
                "SHAPE, not cost: the app asks for a campus's assignments and then joins outward six times, so the " +
                "reachability of every FK is part of the contract - which is what the dataset fixture asserts.",
            Sql = @"
                SELECT
                    sfa.id AS Id, sfa.tenantid AS TenantId, sfa.schoolid AS SchoolId, sfa.campusid AS CampusId,
                    sfa.studentenrollmentid AS StudentEnrollmentId, sfa.feestructureid AS FeeStructureId,
                    sfa.startdate AS StartDate, sfa.enddate AS EndDate, sfa.status AS Status,
                    sfa.remarks AS Remarks,
                    s.name AS StudentName, s.admissionnumber AS AdmissionNumber, se.rollnumber AS RollNumber,
                    c.classroomname AS ClassroomName,
                    se.academicyearid AS AcademicYearId, se.classroomid AS ClassroomId,
                    (ay.startyear || '-' || ay.endyear) AS AcademicYearName,
                    cg.name AS AcademicGradeName,
                    fs.name AS FeeStructureName
                FROM studentfeeassignment sfa
                INNER JOIN studentenrollment se ON se.id = sfa.studentenrollmentid
                INNER JOIN student s ON s.id = se.studentid
                INNER JOIN classroom c ON c.id = se.classroomid
                INNER JOIN academicyear ay ON ay.id = se.academicyearid
                INNER JOIN academicgrade ag ON ag.id = c.academicgradeid
                LEFT JOIN curriculumgrade cg ON cg.id = ag.curriculumgradeid
                INNER JOIN feestructure fs ON fs.id = sfa.feestructureid
                WHERE sfa.tenantid = @tenantId AND sfa.schoolid = @schoolId AND sfa.campusid = @campusId
                AND se.academicyearid = @academicYearId
                ORDER BY s.Name
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // The STUDENT FEE DISCOUNT grid - and the two halves are TWO SEPARATE ROUND TRIPS here
        // (`QueryAsync` for the page, then `QueryAsync` for the count), unlike the three grids above.
        // ⚠️ THE BASE QUERY MIXES THE JOIN TYPES ON PURPOSE AND BOTH HALVES CARRY IT: `inner join Student`
        // and `inner join Discount` (the page projects both and `splitOn: "StudentId,DiscountId"`), while
        // the assignment -> structure -> enrolment -> year chain is LEFT joined. So the two INNER joins
        // are the reachability gate and the rest merely enrich.
        // The count is `count(DISTINCT sd.Id)` - not a plain COUNT - because the two INNER joins CAN fan
        // out on a duplicated student/discount row; the page would then repeat a discount row that the
        // count correctly reports once.
        new QuerySpec
        {
            Key = "fee-discount-count",
            Title = "Student fee discounts - the filtered total (a SECOND round trip)",
            Source = "StudentFeeDiscountRepository.GetAll(page, t, s, c, status, academicYearId) - countQuery",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM studentfeediscount sd " +
                        "INNER JOIN student s ON sd.studentid = s.id INNER JOIN discount d ON sd.discountid = d.id " +
                        "WHERE sd.tenantid = @tenantId AND sd.schoolid = @schoolId AND sd.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "studentfeediscount",
            IndexName = "ix_studentdiscount_tenantid",
            IndexColumns = "tenantid",
            IndexRationale =
                "⚠️ `studentfeediscount` HAS NO SCOPE COMPOSITE - the closest is `ix_studentdiscount_tenantid` " +
                "(tenantid alone), while it carries FIVE single-column indexes for the joins " +
                "(`ix_studentfeediscount_student`, `_discount`, `_assignment`, `_approval`, plus the legacy " +
                "`ix_studentdiscount_studentid`). So the campus predicate is a filter over the tenant's rows and " +
                "`count(DISTINCT sd.Id)` has to de-duplicate afterwards. Recorded, not fixed: a campus holds a " +
                "page or two of discounts, and this repo's rule is that an index is only PROPOSED by a failing probe.",
            Sql = @"
                SELECT count(DISTINCT sd.Id)
                FROM StudentFeeDiscount sd
                inner join Student s on sd.StudentId = s.Id
                inner join Discount d on sd.DiscountId = d.Id
                left join StudentFeeAssignment sfa on sd.StudentFeeAssignmentId = sfa.Id
                left join FeeStructure fs on sfa.FeeStructureId = fs.Id
                left join StudentEnrollment se on sfa.StudentEnrollmentId = se.Id
                left join AcademicYear ay on se.AcademicYearId = ay.Id
                where sd.TenantId = @tenantId and sd.SchoolId = @schoolId and sd.CampusId = @campusId
                AND se.AcademicYearId = @academicYearId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // The page half. The projection repeats column NAMES deliberately (`s.name`, `d.name`, `d.id`)
        // because Dapper maps it multi-to-one with `splitOn: "StudentId,DiscountId"` - the SELECT is what
        // makes the split work, so the spec reproduces it verbatim rather than tidying the aliases.
        new QuerySpec
        {
            Key = "fee-discount-page",
            Title = "Student fee discounts - the grid's first page (dual round trip)",
            Source = "StudentFeeDiscountRepository.GetAll(page, t, s, c, status, academicYearId) - searchQuery",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM studentfeediscount sd " +
                        "INNER JOIN student s ON sd.studentid = s.id INNER JOIN discount d ON sd.discountid = d.id " +
                        "WHERE sd.tenantid = @tenantId AND sd.schoolid = @schoolId AND sd.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "studentfeediscount",
            IndexName = "ix_studentdiscount_tenantid",
            IndexColumns = "tenantid",
            IndexRationale =
                "Same access path as the count half - the tenant-only index, then a filter - and the sort is the " +
                "grid's first column (`s.Name`; the page declares no `order`, so DataTables sorts column 0). The " +
                "shape worth recording is the DOUBLE ROUND TRIP: this repository issues the page and the count as " +
                "two statements, so the user waits for both and measuring only the page reports half the cost.",
            Sql = @"
                SELECT
                    sd.id, sd.studentid, sd.feestructuredetailid, sd.studentfeeassignmentid, sd.discountid, sd.percentage, sd.amount, sd.startdate, sd.enddate, sd.approvalstatus, sd.approvedby, sd.approvedon, sd.remarks, sd.tenantid, sd.schoolid, sd.campusid, sd.createdon, sd.modifiedon,
                    s.id as StudentId, s.name, s.admissionnumber, s.enrollmentdate, s.isactive, s.status,
                    d.id as DiscountId, d.name, d.code, d.description, d.discounttype, d.value, d.isactive, d.tenantid, d.schoolid, d.campusid, d.createdon, d.modifiedon,
                    sfa.id as StudentFeeAssignmentId, fs.name as FeeStructureName,
                    (ay.startyear || '-' || ay.endyear) AS AcademicYearName
                FROM StudentFeeDiscount sd
                inner join Student s on sd.StudentId = s.Id
                inner join Discount d on sd.DiscountId = d.Id
                left join StudentFeeAssignment sfa on sd.StudentFeeAssignmentId = sfa.Id
                left join FeeStructure fs on sfa.FeeStructureId = fs.Id
                left join StudentEnrollment se on sfa.StudentEnrollmentId = se.Id
                left join AcademicYear ay on se.AcademicYearId = ay.Id
                where sd.TenantId = @tenantId and sd.SchoolId = @schoolId and sd.CampusId = @campusId
                AND se.AcademicYearId = @academicYearId
                ORDER BY s.Name
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // The LATE FEE CHARGE grid (the fee-desk list, not the two waived/outstanding variants, which
        // share the same four joins). Two separate round trips, and the joins are the point:
        // `invoices` is joined TWICE - `ci` on the charge's own invoice and `oi` on the ORIGINAL one -
        // plus `student`, so THREE foreign keys must resolve before a row can render.
        // ⚠️ NOTHING ON THE TABLE REQUIRES `originalinvoiceid` TO BE REAL, which is why a fixture that
        // pointed it at 0 would look seeded and render nothing.
        new QuerySpec
        {
            Key = "late-fee-charge-count",
            Title = "Late-fee charges - the filtered total (a SECOND round trip)",
            Source = "LateFeeChargesRepository.GetAll(page, t, s, c, status, academicYearId) - countQuery",
            P95BudgetMs = 200,
            IsScalar = true,
            VolumeSql = "SELECT COUNT(*) FROM latefeecharges lc " +
                        "INNER JOIN invoices ci ON ci.id = lc.invoiceid " +
                        "INNER JOIN invoices oi ON oi.id = lc.originalinvoiceid " +
                        "INNER JOIN student s ON s.id = lc.studentid " +
                        "WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "latefeecharges",
            IndexName = "ix_latefeecharges_student_status",
            IndexColumns = "studentid, status",
            IndexRationale =
                "`latefeecharges` has NO scope composite either: it carries `ix_latefeecharges_student_status` " +
                "(which serves the per-student desk, not the campus grid), `ix_latefeecharges_originalinvoice`, " +
                "`ix_latefeecharges_waivedby` and the UNIQUE `ux_latefeecharges_assessment`. So the campus " +
                "predicate is a filter over the tenant's charges and the three joins lead primary keys. A campus " +
                "holds a term's worth (the fixture seeds 5), so no index is proposed - the PARTIAL/UNIQUE indexes " +
                "are the interesting part and they are asserted by the dataset fixture, not measured here.",
            Sql = @"
                SELECT count(DISTINCT lc.id)
                FROM latefeecharges lc
                INNER JOIN invoices ci ON ci.id = lc.invoiceid
                INNER JOIN invoices oi ON oi.id = lc.originalinvoiceid
                INNER JOIN student s ON s.id = lc.studentid
                WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                AND lc.academicyearid = @academicYearId",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // The page half. The sort is the grid's first column (`s.AdmissionNumber` - the page declares no
        // `order`), and the projection joins back for the two invoice numbers the grid displays
        // (`oi.invoicenumber` is the ORIGINAL invoice, `ci.invoicenumber` the charge's own).
        new QuerySpec
        {
            Key = "late-fee-charge-page",
            Title = "Late-fee charges - the grid's first page (invoices joined twice)",
            Source = "LateFeeChargesRepository.GetAll(page, t, s, c, status, academicYearId) - searchQuery",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM latefeecharges lc " +
                        "INNER JOIN invoices ci ON ci.id = lc.invoiceid " +
                        "INNER JOIN invoices oi ON oi.id = lc.originalinvoiceid " +
                        "INNER JOIN student s ON s.id = lc.studentid " +
                        "WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId",
            MinVolume = 1,
            IndexTable = "latefeecharges",
            IndexName = "ix_latefeecharges_originalinvoice",
            IndexColumns = "originalinvoiceid",
            IndexRationale =
                "The page leads on the same campus filter as its count. `ix_latefeecharges_originalinvoice` " +
                "exists precisely because `originalinvoiceid` is a join AND a filter column here, and the " +
                "`invoices` lookups lead the primary key. Five rows on the measured campus: reachability, not " +
                "volume.",
            Sql = @"
                SELECT lc.id, lc.assessmentid, lc.originalinvoiceid, lc.invoiceid, lc.studentid,
                    lc.academicyearid, lc.chargedate, lc.baseamount, lc.amount, lc.taxamount,
                    lc.totalamount, lc.status, lc.waivedby, lc.waivedon, lc.waiveremarks,
                    lc.tenantid, lc.schoolid, lc.campusid, lc.createdon, lc.modifiedon,
                    ci.invoicenumber AS InvoiceNumber, ci.balanceamount AS InvoiceBalanceAmount,
                    oi.invoicenumber AS OriginalInvoiceNumber,
                    s.name AS StudentName, s.admissionnumber AS AdmissionNumber
                FROM latefeecharges lc
                INNER JOIN invoices ci ON ci.id = lc.invoiceid
                INNER JOIN invoices oi ON oi.id = lc.originalinvoiceid
                INNER JOIN student s ON s.id = lc.studentid
                WHERE lc.tenantid = @tenantId AND lc.schoolid = @schoolId AND lc.campusid = @campusId
                AND lc.academicyearid = @academicYearId
                ORDER BY s.AdmissionNumber
                LIMIT 50 OFFSET 0",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["academicYearId"] = v.AcademicYearId,
            },
        },

        // ============================================================================================
        // THE BACKGROUND JOB QUEUE - `JobsController`, route `api/jobs`. This is the ONE family in
        // this catalogue with NO SCOPE FILTER ANYWHERE, and that is the finding rather than an
        // oversight:
        //   * the CLAIM (`BackgroundJobRepository.ClaimNextAsync`) is `SELECT ... LIMIT 1` with no
        //     scope predicate at all - which is what lets several workers race safely, and which also
        //     means the shipped `ix_backgroundjob_tenantschoolcampus` is NEVER used by the hot path;
        //   * the CAMPUS SWEEP (`/billing` and `/postings`) reads EVERY campus row, because that is
        //     what a scheduled sweep is;
        //   * the orphan sweep is global by nature - a file is referenced or it is not.
        // So no spec below filters by scope, and none of them has a scope-shaped index candidate. Their
        // volume gates are counts over the WHOLE table (or the whole queue) for the same reason: a gate
        // that counted the measured campus would SKIP a query the app issues globally.
        // ============================================================================================

        new QuerySpec
        {
            Key = "job-queue-claim",
            Title = "Job worker - the claim's candidate scan (the queue's hot path, drained every minute)",
            Source = "BackgroundJobRepository.ClaimNextAsync - the INNER subquery of the claim UPDATE",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM backgroundjob WHERE status = 1",
            MinVolume = 1,
            IndexTable = "backgroundjob",
            IndexName = "ix_backgroundjob_status_lease",
            IndexColumns = "status, leaseuntil",
            IndexRationale =
                "DECLARED AS THE ALREADY-DEPLOYED INDEX, so the advisor compares against what is there " +
                "instead of proposing a second one. The claim leads with `Status = 1` and constrains " +
                "`LeaseUntil`, which is exactly this index's column list, and the measured plan uses it. " +
                "⚠️ THE OBVIOUS CANDIDATE - `(status, runafter, createdon)`, to serve the lease/run-after " +
                "predicates AND the ORDER BY - WAS PROBED AND REJECTED: on a synthetic 320,207-row queue " +
                "(20,200 queued, 201 due) the shipped claim read **5.269 ms** while the same statement with " +
                "that index in place read **13.700 ms (2.6x WORSE)**, and the plan did not even choose it. " +
                "The reason is the shape of the predicate: `(LeaseUntil IS NULL OR LeaseUntil < @Now)` " +
                "cannot be satisfied by an index range on a nullable column, so the new index would be " +
                "scanned in full and then re-checked, on top of the sort. This is the `employeepayroll` " +
                "rule again - a missing index is not a defect until a MEASURED query would use it, and " +
                "here the measured query uses the one that ships.",
            Sql = @"
                SELECT Id FROM BackgroundJob
                WHERE Status = 1
                  AND (LeaseUntil IS NULL OR LeaseUntil < @now)
                  AND (RunAfter IS NULL OR RunAfter <= @now)
                ORDER BY CreatedOn ASC
                LIMIT 1",
            Params = new Dictionary<string, object>
            {
                // ⚠️ THE SPEC IS THE SUBQUERY, NOT THE SHIPPED `UPDATE`, AND THAT IS DELIBERATE - no
                // query spec in this catalogue is a write (grep it: zero INSERT/UPDATE/DELETE).
                // `BenchmarkRunner.MeasureAsync` opens a session with NO transaction, and `ExplainAsync`
                // runs `EXPLAIN (ANALYZE, ...)`, which EXECUTES its statement too - so a spec whose Sql
                // were the claim UPDATE would genuinely CLAIM jobs in the database being measured (twice
                // per run: the samples plus the plan) and leave a queue that the app then sees as "busy".
                // The write half costs one row modification by primary key, and the subquery is what the
                // plan's cost is dominated by - the measured numbers above are for the FULL UPDATE.
                //
                // ⚠️ Kind=Unspecified ON PURPOSE: `leaseuntil` / `runafter` / `createdon` are `timestamp
                // WITHOUT time zone`, and the VALUE the app writes is `DateTime.UtcNow`. Npgsql 8 (this
                // tool's package) REFUSES to write a Kind=Utc DateTime into a `timestamp` parameter
                // outright; the API never sees that because its host enables
                // `Npgsql.EnableLegacyTimestampBehavior` at startup. Stripping the Kind binds the same
                // INSTANT with the same digits the app sends, without changing this project's global
                // timestamp behaviour for every other spec.
                ["now"] = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            },
        },

        new QuerySpec
        {
            Key = "job-status",
            Title = "Job status poll - one job by primary key",
            Source = "BackgroundJobRepository.Get(id) - JobsController.GetJob (GET api/jobs/{id})",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM backgroundjob",
            MinVolume = 1,
            IndexTable = "backgroundjob",
            IndexName = "backgroundjob_pkey",
            IndexColumns = "id",
            IndexRationale =
                "A primary-key lookup, and the index it needs is the one every table already has. The " +
                "read carries no scope predicate at all - `WHERE Id = @Id` - so neither of the table's " +
                "secondary indexes can serve it and none is owed. What this spec proves is that the poll " +
                "stays a point read as the queue grows, which matters because `backgroundjob` is NEVER " +
                "PRUNED (see `job-orphan-*`'s note): the table is append-only for the life of the install.",
            Sql = @"SELECT * FROM BackgroundJob WHERE Id = @jobId",
            Params = new Dictionary<string, object>
            {
                ["jobId"] = v.JobId,
            },
        },

        new QuerySpec
        {
            Key = "job-campus-sweep",
            Title = "Scheduled sweep - every campus, ordered (shared by /jobs/billing and /jobs/postings)",
            Source = "JobsController.GenerateInvoices + ProcessPostings - the raw Dapper campus read",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM campus",
            MinVolume = 1,
            IndexRationale =
                "NO CANDIDATE, AND AN INDEX CANNOT HELP: this is `SELECT ... FROM Campus ORDER BY " +
                "TenantId, SchoolId, Id` with NO predicate at all. The sweep exists to visit every campus, " +
                "so it reads every row by definition and the sort is over a table whose size is the number " +
                "of schools the platform serves (23 here, and it grows with customers rather than with " +
                "activity). The measured cost is a single shared buffer hit and a 23-row sort. The spec is " +
                "kept because the shape is load-bearing: it is the FIRST statement of both sweeps, and a " +
                "future change that made it scope-aware or joined it back to a directory table would be " +
                "the moment this read stopped being free.",
            Sql = @"SELECT c.TenantId, c.SchoolId, c.Id FROM Campus c ORDER BY c.TenantId, c.SchoolId, c.Id",
        },

        // The orphan sweep is the ONE statement in this catalogue whose cost is linear in TEN other
        // tables, so the branches are written out verbatim below. They are also the thing to re-read
        // before adding a table that references a file:
        //     HomeworkAttachment.HomeworkAttachmentFileId / .VideoAttachmentFileId
        //     HomeworkSubmissionAttachment.{HomeworkSubmissionAttachmentFileId, VideoSubmissionAttachmentFileId}
        //     MomentAttachment.{MomentAttachmentFileId, MomentVideoThumbnailFileId}
        //     SchoolEventAttachment.EventAttachmentFileId
        //     CurriculumTopicLearningMaterial.{LearningAttachmentFileId, LearningVideoThumbnailFileId}
        //     DocumentSettings.LogoAttachmentFileId
        //
        // ⚠️ TEN BRANCHES, NOT ELEVEN. `documentsettings.customtemplateattachmentfileid` was DROPPED in
        // V129 with the DOCX desk, so a branch naming it would make this query - and therefore the whole
        // sweep - a `42703 undefined_column` error. Every branch also keeps its `IS NOT NULL` guard: one
        // NULL makes `af.Id NOT IN (...)` evaluate to NULL for every row, and the sweep then reports and
        // deletes NOTHING.
        //
        // ⚠️ NONE OF THE TEN CHILD COLUMNS IS INDEXED, AND THAT IS RECORDED RATHER THAN FIXED. The
        // catalog agrees they are unindexed (`pg_indexes` filtered on `fileid` over these ten tables
        // returns ZERO rows), but an index is the wrong answer: the predicate is `NOT IN (<all ten id
        // sets>)`, so the planner must build the COMPLETE union of referenced ids before it can judge a
        // single attachment row. A btree probe per child column would replace one sequential scan of a
        // table holding tens-to-hundreds of rows with hundreds of index probes. What would make this
        // query expensive is a child table large enough for a seq scan to matter, and the shape to watch
        // then is the UNION (a `NOT EXISTS` chain, or `attachmentfile` carrying a reference count) -
        // not an index. This is the `invoice-overdue-eligible` stance: measured, recorded, no DDL.
        new QuerySpec
        {
            Key = "job-orphan-sweep",
            Title = "Attachment cleanup - the orphan report (10-branch NOT IN over every referencing table)",
            Source = "AttachmentFileRepository.GetOrphanFiles - JobsController.CleanAttachments",
            P95BudgetMs = 200,
            VolumeSql = "SELECT COUNT(*) FROM attachmentfile",
            MinVolume = 1,
            Sql = @"
                SELECT * FROM AttachmentFile af WHERE af.Id NOT IN (
                                SELECT HomeworkAttachmentFileId FROM HomeworkAttachment WHERE HomeworkAttachmentFileId IS NOT NULL
                                UNION
                                SELECT VideoAttachmentFileId FROM HomeworkAttachment WHERE VideoAttachmentFileId IS NOT NULL
                                UNION
                                SELECT HomeworkSubmissionAttachmentFileId FROM HomeworkSubmissionAttachment WHERE HomeworkSubmissionAttachmentFileId IS NOT NULL
                                UNION
                                SELECT VideoSubmissionAttachmentFileId FROM HomeworkSubmissionAttachment WHERE VideoSubmissionAttachmentFileId IS NOT NULL
                                UNION
                                SELECT MomentAttachmentFileId FROM MomentAttachment WHERE MomentAttachmentFileId IS NOT NULL
                                UNION
                                SELECT MomentVideoThumbnailFileId FROM MomentAttachment WHERE MomentVideoThumbnailFileId IS NOT NULL
                                UNION
                                SELECT EventAttachmentFileId FROM SchoolEventAttachment WHERE EventAttachmentFileId IS NOT NULL
                                UNION
                                SELECT LearningAttachmentFileId FROM CurriculumTopicLearningMaterial WHERE LearningAttachmentFileId IS NOT NULL
                                UNION
                                SELECT LearningVideoThumbnailFileId FROM CurriculumTopicLearningMaterial WHERE LearningVideoThumbnailFileId IS NOT NULL
                                UNION
                                SELECT LogoAttachmentFileId FROM DocumentSettings WHERE LogoAttachmentFileId IS NOT NULL
                );",
        },
    };

    /// <summary>The app's commonest scope predicate, spelled the way `SqlBuilder` emits it.</summary>
    private const string Scope3 = "TenantId = @tenantId AND SchoolId = @schoolId AND CampusId = @campusId";

    /// <summary>`Subject`/`Curriculum`/`CurriculumApproval` are SCHOOL-scoped - no campus column.</summary>
    private const string Scope2 = "TenantId = @tenantId AND SchoolId = @schoolId";

    /// <summary>`Scope3` with a table ALIAS, for a list read that joins other tables.</summary>
    private static string Scope3E(string alias) =>
        $"{alias}.TenantId = @tenantId AND {alias}.SchoolId = @schoolId AND {alias}.CampusId = @campusId";

    /// <summary>
    /// A plain (UNPAGED) list read - an `IEnumerable` repository method behind a CLIENT-side grid,
    /// or a dialog dropdown.
    ///
    /// ⚠️ THERE IS NO `LIMIT`. `ScopeGrid` adds one because a `GetAllAsync` grid really pages; these
    /// methods materialise EVERY matching row, so adding a limit would measure a statement the
    /// application never issues. That is also why they are a separate factory rather than another
    /// argument on `ScopeGrid` - the difference is the SHAPE, not a tuning knob.
    ///
    /// ⚠️ `select`/`joins` ARE OPTIONAL AND DEFAULT TO THE BARE `SELECT *`. When the repository joins
    /// (the overtime-policy list resolves its department and designation names) the projection and
    /// the joins are given verbatim, so the spec cannot drift into a shape the screen does not run.
    /// </summary>
    private static QuerySpec ScopeList(ScopeVars v, string key, string title, string source,
        string table, string predicate, string volumeSql, string? select = null,
        string? joins = null, string? orderBy = null, long minVolume = 1, double budgetMs = 200)
    {
        var projection = string.IsNullOrWhiteSpace(select)
            ? "SELECT * FROM " + table
            : select + "\nFROM " + table;
        var join = string.IsNullOrWhiteSpace(joins) ? string.Empty : "\n" + joins;
        var sort = string.IsNullOrWhiteSpace(orderBy) ? string.Empty : "\nORDER BY " + orderBy;

        return new QuerySpec
        {
            Key = key,
            Title = title,
            Source = source,
            P95BudgetMs = budgetMs,
            VolumeSql = volumeSql,
            MinVolume = minVolume,
            Sql = projection + join + "\nWHERE " + predicate + sort,
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
            },
        };
    }

    /// <summary>
    /// One `GenericRepository.GetAllAsync(page, predicate)` grid - the app's commonest list shape.
    ///
    /// ⚠️ THE SQL IS ASSEMBLED, SO THE STEPS ARE THE APP'S OWN: `SELECT * FROM <table>` + the
    /// predicate verbatim + the page's own `ORDER BY` (only when the page declares one - see the
    /// block comment where this is used) + `LIMIT @pageSize OFFSET @offset`. `@pageSize` is a
    /// PARAMETER here because `GetAllAsync` interpolates `page.PageSize` - both are 50, and the
    /// parameter form is the one that cannot silently diverge from `DefaultPageSize`.
    ///
    /// The count half of this round trip is deliberately NOT a second spec: on a single table it
    /// is an index-only count over the same predicate, and a spec that cannot be the finding is
    /// noise. `lib-fine-count`/`lib-inventory-audit-count` exist for the opposite reason - their
    /// count re-runs six joins, so the user waits for it.
    /// </summary>
    private static QuerySpec ScopeGrid(ScopeVars v, string key, string title, string source,
        string table, string? predicate, string volumeSql, long minVolume = 1,
        double budgetMs = 200, string? orderBy = null)
    {
        var where = string.IsNullOrWhiteSpace(predicate) ? string.Empty : " WHERE " + predicate;
        var sort = string.IsNullOrWhiteSpace(orderBy) ? string.Empty : " ORDER BY " + orderBy;

        return new QuerySpec
        {
            Key = key,
            Title = title,
            Source = source,
            P95BudgetMs = budgetMs,
            VolumeSql = volumeSql,
            MinVolume = minVolume,
            Sql = "SELECT * FROM " + table + where + sort + " LIMIT @pageSize OFFSET @offset",
            Params = new Dictionary<string, object>
            {
                ["tenantId"] = v.TenantId,
                ["schoolId"] = v.SchoolId,
                ["campusId"] = v.CampusId,
                ["pageSize"] = DefaultPageSize,
                ["offset"] = DefaultOffset,
            },
        };
    }

    /// <summary>
    /// One reporting-view spec. Kept as a factory rather than 17 copies of the same initializer,
    /// because the three fields that make a view spec correct - ReportScope, the page parameters and
    /// the budget - are the ones that must never drift between them.
    /// </summary>
    private static QuerySpec ReportSpec(ScopeVars v, string key, string title, string definition,
        string sql, string volumeSql, Dictionary<string, object>? filterParams = null,
        long minVolume = 1000, string countMode = "window", bool scalar = false)
    {
        var parameters = new Dictionary<string, object>
        {
            ["__limit"] = DefaultPageSize,
            ["__offset"] = DefaultOffset,
            // Named only by each spec's VolumeSql - the view itself reads the scope from the
            // session, so these are filtered out of the measured statement by BindableFor.
            ["tenantId"] = v.TenantId,
            ["schoolId"] = v.SchoolId,
            ["campusId"] = v.CampusId,
        };
        if (filterParams != null)
            foreach (var pair in filterParams) parameters[pair.Key] = pair.Value;

        return new QuerySpec
        {
            Key = key,
            Title = title,
            Source = "reportdefinition " + definition + " -> ReportQueryBuilder.Build (countmode=" + countMode + ")",
            P95BudgetMs = ReportPageBudgetMs,
            IsScalar = scalar,
            ReportScope = new ReportScope(v.TenantId, v.SchoolId, v.CampusId),
            VolumeSql = volumeSql,
            // ⚠️ A GATE THAT CAN NEVER OPEN IS WORSE THAN NO GATE. `MinVolume` defaults to 1,000
            // because that is a realistic size for the student-side tables, but a real school
            // employs TENS of people, not thousands - the HR seeder's own default is 120 per
            // campus. `rpt-employee-headcount` therefore sat at SKIP forever, which reads as
            // "not measured yet" for a report that was measurable all along. Pass a per-spec
            // floor whenever the table's honest size is below the default.
            MinVolume = minVolume,
            Sql = sql,
            Params = parameters,
        };
    }
}
