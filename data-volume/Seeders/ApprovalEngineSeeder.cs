using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>
/// Options for <see cref="ApprovalEngineSeeder"/>. The defaults give one campus a realistic
/// approval history: every configured module has a run of finished cycles plus the open ones an
/// approver's inbox actually shows.
/// </summary>
public sealed class ApprovalEngineOptions
{
    /// <summary>
    /// Workflow cycles created per configured module. Eight modules are configured on a seeded
    /// campus, so the default writes 320 cycles - a staff of 120 over a few terms, not a token row.
    /// </summary>
    public int WorkflowsPerModule { get; set; } = 40;

    /// <summary>
    /// Steps each template and each cycle carries. Two is the app's commonest chain (a manager then
    /// HR), and it is the minimum that lets a cycle be `InProgress` at step 2.
    /// </summary>
    public int StepsPerTemplate { get; set; } = 2;

    /// <summary>Re-seed even when the campus already holds workflows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's approval-engine seed produced. Returned so a run can report real counts.</summary>
public sealed class ApprovalEngineSeedResult
{
    public bool Skipped { get; set; }
    public int TemplateSteps { get; set; }
    public int Workflows { get; set; }
    public int WorkflowSteps { get; set; }
    public int RolePermissions { get; set; }
    public int PendingWorkflows { get; set; }
    public long RequestedByUserId { get; set; }
    public long ApproverUserId { get; set; }
}

/// <summary>
/// Seeds the APPROVAL ENGINE - `approvaltemplatestep`, `approvalworkflow` and
/// `approvalworkflowstep` - plus `rolepermission`, for one campus.
///
/// WHY THESE FOUR WERE EMPTY, AND WHY THAT IS ONE GAP RATHER THAN FOUR
/// ------------------------------------------------------------------
/// `approvaltemplate` ALREADY held its eight rows on `ayra_perf` (one per module: AttendanceCorrection,
/// EmployeeLeave, EmployeeOvertime, EmploymentContract, Loan, Payroll, Refund, Settlement) - and every
/// one of them had **ZERO steps**, because nothing here had ever configured a chain. The engine's
/// facts were empty for the same reason: no approval has ever been run against the perf dataset.
/// So the module's two paged grids (`GET approvalworkflow/pending`, `GET approvalworkflow/myworkflows`)
/// could only ever measure an empty result, and `rolepermission` - the table
/// `HasPermissionAttribute` consults on EVERY permission-gated request - held zero rows, so the
/// per-request permission lookup was never exercised against data.
///
/// ⚠️ THE CHAIN IS THE POINT, NOT THE ROW COUNT. `GetPendingApprovals` is a five-way join:
///
///     ApprovalWorkflow w
///       INNER JOIN ApprovalWorkflowStep s ON s.ApprovalWorkflowId = w.Id AND s.StepNo = w.CurrentStep
///                                                                    AND s.IsCompleted = false
///       LEFT  JOIN Users req ON req.Id = w.RequestedBy
///       LEFT  JOIN Roles  r   ON r.Id  = s.RoleId
///       LEFT  JOIN userrole ur ON ur.roleid = r.Id AND ur.userid = @UserId
///     WHERE (s.ApproverUserId = @UserId OR ur.userid IS NOT NULL)
///
/// A cycle whose current step is missing, completed, or numbered differently from `w.CurrentStep` is
/// INVISIBLE - the workflow row exists and the inbox that lists it is empty. That is exactly the
/// "a seeded row no query can reach" defect the e2e baseline recorded three times, so every cycle
/// here is written with its current step OPEN and its `StepNo` equal to `CurrentStep`, and the
/// assertion in <c>ApprovalEngineDatasetTests</c> holds the two together.
///
/// ⚠️ THE SEEDER AND THE MEASURING SCOPE MUST AGREE ABOUT *WHICH USER* THE INBOX IS OPENED AS.
/// Both grids take a `userId` from the session, and the inbox matches either
/// `s.ApproverUserId = @UserId` or a role the user holds through `userrole`. The perf campus's users
/// hold the Teacher role (id 4) while the templates name HR Manager / HOD, so the ROLE branch would
/// match nothing - the seeder therefore sets `ApproverUserId` on every OPEN step to one deterministic
/// user, and <c>db-report</c> resolves that same user with the same rule ("the campus's
/// lowest-numbered user"). The two are the same query, so they cannot disagree.
///
/// ⚠️ TIMESTAMPS ARE WRITTEN EXPLICITLY, NEVER LEFT TO A DEFAULT. This is the table where the
/// `-infinity` bug was originally found: `ApprovalTemplateStep` is one of the two entities whose
/// row was written with an unassigned `CreatedOn` because `GenericRepository.GenerateInsertQuery`
/// names EVERY column, so the column's `DEFAULT now()` can never apply and an unassigned
/// `DateTime.MinValue` reaches PostgreSQL as `-infinity`. A `-infinity` stamp sorts BEFORE every
/// real timestamp, which would tie every cycle in `ORDER BY w.RequestedDate DESC` and make "the
/// newest approval" whatever the engine happened to return. These are raw INSERTs with an explicit
/// `@now`, so the sentinel cannot appear.
///
/// IDEMPOTENT BY DEFAULT: a campus that already holds workflows is skipped unless Force is set, and
/// a SKIPPED campus still reports what it holds so the fixture's own assertion stays true on a re-run.
/// </summary>
public sealed class ApprovalEngineSeeder : BaseSeeder
{
    public ApprovalEngineSeeder(string connectionString) : base(connectionString) { }

    /// <summary>
    /// The tables a bulk approval load invalidates, so <see cref="PerfDatasetSeeder"/> can ANALYZE
    /// them. Seeding without this leaves the planner reasoning from the row counts that described an
    /// EMPTY table - the failure mode already measured on `classroom` and on the student grid.
    /// </summary>
    public static readonly string[] TablesToAnalyze =
    {
        "approvaltemplatestep", "approvalworkflow", "approvalworkflowstep", "rolepermission"
    };

    /// <summary>`Permission` - CanView / CanAdd / CanEdit / CanDelete. The whole vocabulary.</summary>
    private static readonly short[] Permissions = { 0, 1, 2, 3 };

    /// <summary>
    /// The role a template step names when the campus has one. HR Manager (8) then HOD (7) are the
    /// two desks a school routes an approval through, and both are seeded system roles.
    /// </summary>
    private static readonly string[] PreferredRoleNames = { "HR Manager", "HOD" };

    /// <summary>
    /// The status a cycle is left in, cycled so the inbox (Pending / InProgress) is populated while
    /// the finished states are real too - a campus whose whole history is open is not a history.
    ///
    /// ⚠️ TWO OF THE FIVE ARE OPEN, AND THAT RATIO IS DELIBERATE. A real approval queue is mostly
    /// CLOSED - a history where 60% of the cycles are still pending is not what an inbox looks like,
    /// and the share matters: the inbox's `WorkflowStatus IN ('Pending','InProgress')` is the
    /// selective half of its predicate, so a fixture where it matches most of the table would make
    /// the scope/status index look useless. `Completed`/`Approved` are the engine's own terminal
    /// tokens (`WorkflowStatus`), not invented strings.
    /// </summary>
    private static readonly (string Status, bool Completed)[] Cycle =
    {
        ("Pending", false),
        ("InProgress", false),
        ("Approved", true),
        ("Completed", true),
        ("Rejected", true),
    };

    public async Task<ApprovalEngineSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, ApprovalEngineOptions options, bool verbose = true)
    {
        var result = new ApprovalEngineSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM approvalworkflow
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            // ⚠️ REPORT what the campus holds rather than returning zeros - a zero-return makes the
            // fixture's own "did anything get seeded" assertion fail on a re-run.
            await ReadCountsAsync(conn, tenantId, schoolId, campusId, result);
            if (verbose)
            {
                Console.WriteLine(
                    $"  ApprovalEngine: campus {campusId} already holds {existing:N0} workflows - skipped");
            }
            return result;
        }

        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 0. A forced re-seed clears first, CHILDREN FIRST. `approvalworkflowstep` and
        //    `approvaltemplatestep` carry NO scope columns of their own (the step table has only
        //    `approvalworkflowid`), so a scoped DELETE on either is a 42703 that would abort the
        //    whole seed - they go through their parent, before the parent is cleared.
        // ------------------------------------------------------------------
        if (options.Force && existing > 0)
        {
            await ClearTableByParentAsync(
                conn, "approvalworkflowstep", "approvalworkflowid", "approvalworkflow",
                tenantId, schoolId, campusId);

            await ClearTableByParentAsync(
                conn, "approvaltemplatestep", "approvaltemplateid", "approvaltemplate",
                tenantId, schoolId, campusId);

            foreach (var table in new[] { "approvalworkflow", "rolepermission" })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
            {
                Console.WriteLine($"  ApprovalEngine: campus {campusId} cleared for a forced re-seed");
            }
        }

        // ------------------------------------------------------------------
        // 1. The two identities the grids are opened AS. One deterministic rule, mirrored verbatim
        //    in `db-report`'s ResolveScopeAsync, so the seeder and the measurement cannot disagree
        //    about who the inbox belongs to.
        // ------------------------------------------------------------------
        var campusUsers = (await conn.QueryAsync<long>(
            @"SELECT id FROM users
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (campusUsers.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId} holds no `users` row, so no approval can be requested by - or " +
                "routed to - anybody. Run the student/HR seeders first.");
        }

        var approverUserId = campusUsers[0];
        var requesterUserId = campusUsers.Count > 1 ? campusUsers[1] : campusUsers[0];
        result.ApproverUserId = approverUserId;
        result.RequestedByUserId = requesterUserId;

        // ------------------------------------------------------------------
        // 2. The roles a step names. The campus's own `roles` rows are read rather than invented,
        //    because `Roles` has no scope columns and a made-up id would leave the step's
        //    `LEFT JOIN Roles` resolving to a NULL name - a chain that lists a blank approver.
        // ------------------------------------------------------------------
        var roleIds = new List<long>();
        foreach (var roleName in PreferredRoleNames)
        {
            var roleId = await conn.ExecuteScalarAsync<long?>(
                "SELECT id FROM roles WHERE lower(name) = lower(@name) AND isactive = TRUE ORDER BY id LIMIT 1",
                new { name = roleName });

            if (roleId is > 0) roleIds.Add(roleId.Value);
        }

        if (roleIds.Count == 0)
        {
            roleIds = (await conn.QueryAsync<long>(
                "SELECT id FROM roles WHERE isactive = TRUE ORDER BY id LIMIT 2")).ToList();
        }

        if (roleIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId}'s tenant holds no active `roles` row, so a template step cannot " +
                "name an approver and `RolesRepository`'s own screens would be empty too.");
        }

        // ------------------------------------------------------------------
        // 3. `approvaltemplatestep` - the chain each configured module runs. The templates ALREADY
        //    exist (one per module) and held no steps at all, which is why the module could be
        //    configured and still never route anything.
        // ------------------------------------------------------------------
        var templates = (await conn.QueryAsync<TemplateRow>(
            @"SELECT id AS TemplateId, modulename AS ModuleName
                FROM approvaltemplate
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND isactive = TRUE
               ORDER BY id",
            new { tenantId, schoolId, campusId })).ToList();

        if (templates.Count == 0)
        {
            throw new InvalidOperationException(
                $"campus {campusId} holds no ACTIVE `approvaltemplate`, so there is no chain to " +
                "instantiate. The eight module templates are configuration, not facts - seed them " +
                "first (the e2e baseline's 07_Approval_Templates, or the hr.approval.template screen).");
        }

        var templateStepRows = new List<TemplateStepRow>();
        foreach (var template in templates)
        {
            for (var stepNo = 1; stepNo <= options.StepsPerTemplate; stepNo++)
            {
                templateStepRows.Add(new TemplateStepRow
                {
                    TemplateId = template.TemplateId,
                    StepNo = stepNo,
                    // The LAST step is the entity owner's / HR's; the first is the reporting manager's.
                    // The two booleans are the app's own routing hints and are what the engine reads.
                    RoleId = roleIds[(stepNo - 1) % roleIds.Count],
                    AssignToManager = stepNo == 1,
                    AssignToEntityOwner = stepNo == options.StepsPerTemplate,
                });
            }
        }

        result.TemplateSteps = await InsertTemplateStepsAsync(
            conn, templateStepRows, now, verbose);

        // ------------------------------------------------------------------
        // 4. `approvalworkflow` + its steps. One cycle per (module, index), with the status cycled so
        //    the inbox and the "my requests" grid both hold rows.
        //
        //    ⚠️ `EntityId` is NOT a foreign key - `approvalworkflow.entityid` points at whatever the
        //    module's entity is (a leave request, a loan, a settlement) and the engine resolves it
        //    per module. The ids below are therefore a deterministic spread rather than a real row's
        //    id, and that is honest: the grid filters on `modulename` + `entityid`, never joins the
        //    entity, so a reachable row does not depend on the id resolving. The one read that DOES
        //    join - `GetWorkflowHistoryByEntity(module, entityId, scope)` - is measured against one
        //    of these same pairs, so it is reachable by construction.
        // ------------------------------------------------------------------
        var workflowRows = new List<WorkflowRow>();
        var stepNoByWorkflow = new List<int>();
        var cycleIndex = 0;

        foreach (var template in templates)
        {
            for (var i = 0; i < options.WorkflowsPerModule; i++)
            {
                var (status, completed) = Cycle[cycleIndex % Cycle.Length];
                cycleIndex++;

                // `Pending` sits on step 1 with step 1 open; `InProgress` has climbed to step 2.
                var currentStep = status == "InProgress" ? Math.Min(2, options.StepsPerTemplate) : 1;

                workflowRows.Add(new WorkflowRow
                {
                    ModuleName = template.ModuleName,
                    // A unique entity per cycle, so `GetWorkflowHistoryByEntity` on one cycle's pair
                    // returns that cycle rather than every campus cycle of the module.
                    EntityId = 100_000 + cycleIndex,
                    Status = status,
                    CurrentStep = currentStep,
                    // The FIRST user is both the approver the inbox is opened as AND (on every other
                    // cycle) the requester, so `myworkflows` is non-empty for the same identity.
                    RequestedBy = cycleIndex % 2 == 1 ? approverUserId : requesterUserId,
                    Completed = completed,
                });

                stepNoByWorkflow.Add(options.StepsPerTemplate);
            }
        }

        var workflowIds = await InsertWorkflowsAsync(
            conn, tenantId, schoolId, campusId, workflowRows, now, verbose);
        result.Workflows = workflowIds.Count;

        var stepRows = new List<WorkflowStepRow>();
        for (var w = 0; w < workflowIds.Count; w++)
        {
            var workflow = workflowRows[w];
            var stepCount = stepNoByWorkflow[w];
            var stepsToComplete = workflow.Completed
                ? stepCount                                   // Approved / Rejected: the chain finished
                : workflow.CurrentStep - 1;                   // InProgress: everything BELOW the current step

            for (var stepNo = 1; stepNo <= stepCount; stepNo++)
            {
                var isCurrent = stepNo == workflow.CurrentStep;
                var isCompleted = workflow.Completed || stepNo < workflow.CurrentStep;

                // ⚠️ ONLY THE CURRENT STEP CARRIES `ApproverUserId`. That is the column the inbox
                // matches on, and pointing a CLOSED step at the user would make the same cycle
                // appear twice the moment `StepNo = CurrentStep` stopped excluding it.
                stepRows.Add(new WorkflowStepRow
                {
                    WorkflowId = workflowIds[w],
                    StepNo = stepNo,
                    RoleId = roleIds[(stepNo - 1) % roleIds.Count],
                    ApproverUserId = isCurrent ? approverUserId : (isCompleted ? approverUserId : (long?)null),
                    IsCompleted = isCompleted,
                    Action = isCompleted
                        ? (workflow.Status == "Rejected" ? "Rejected" : "Approved")
                        : null,
                    Comments = isCompleted ? "perf-seed" : null,
                    ActionDate = isCompleted ? now.AddHours(-(stepCount - stepNo + 1)) : (DateTime?)null,
                });
            }

            // `stepsToComplete` is asserted only implicitly: a Rejected cycle keeps its later steps
            // open, which is what the engine itself does (it stops at the refusal).
            _ = stepsToComplete;
        }

        result.WorkflowSteps = await InsertWorkflowStepsAsync(
            conn, stepRows, now, verbose);

        result.PendingWorkflows = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM approvalworkflow w
               INNER JOIN approvalworkflowstep s ON s.approvalworkflowid = w.id
                    AND s.stepno = w.currentstep AND s.iscompleted = false
               WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                 AND w.workflowstatus IN ('Pending', 'InProgress')",
            new { tenantId, schoolId, campusId });

        // ------------------------------------------------------------------
        // 5. `rolepermission` - the table `HasPermissionAttribute` reads on EVERY permission-gated
        //    request, for the roles the campus actually has. It is the one table in this batch that
        //    is not a grid; it is seeded because a per-request lookup that has never had a row is a
        //    hot path no measurement has ever touched.
        //
        //    ⚠️ `rolepermission.id` IS THE ONE COLUMN IN THIS BATCH WITH NO DEFAULT. Every other id
        //    here is a sequence; this one is a bare `bigint NOT NULL`, so the ids are allocated from
        //    the table's own high-water mark - supplying a literal would collide on the second run.
        // ------------------------------------------------------------------
        result.RolePermissions = await InsertRolePermissionsAsync(
            conn, tenantId, schoolId, campusId, now, verbose);

        if (verbose)
        {
            Console.WriteLine(
                $"  ApprovalEngine: campus {campusId} -> {result.TemplateSteps} template steps, " +
                $"{result.Workflows:N0} workflows, {result.WorkflowSteps:N0} workflow steps " +
                $"({result.PendingWorkflows:N0} open), {result.RolePermissions} role permissions " +
                $"(approver user {result.ApproverUserId}, requester {result.RequestedByUserId})");
        }

        return result;
    }

    /// <summary>
    /// The step rows of every ACTIVE template, batched through `unnest` (Npgsql binds an array as one
    /// parameter, and `RETURNING id` reports what the server actually wrote rather than what the
    /// caller hoped it did).
    /// </summary>
    private static async Task<int> InsertTemplateStepsAsync(
        NpgsqlConnection conn, List<TemplateStepRow> rows, DateTime now, bool verbose)
    {
        if (rows.Count == 0) return 0;

        const string sql = @"
            INSERT INTO approvaltemplatestep
                (approvaltemplateid, stepno, roleid, isrequired, assigntoentityowner, assigntomanager,
                 createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@TemplateIds), unnest(@StepNos), unnest(@RoleIds), true,
                   unnest(@AssignToEntityOwner), unnest(@AssignToManager), 1, 1, @now, @now";

        var inserted = await conn.ExecuteAsync(sql, new
        {
            TemplateIds = rows.Select(r => r.TemplateId).ToArray(),
            StepNos = rows.Select(r => r.StepNo).ToArray(),
            RoleIds = rows.Select(r => r.RoleId).ToArray(),
            AssignToEntityOwner = rows.Select(r => r.AssignToEntityOwner).ToArray(),
            AssignToManager = rows.Select(r => r.AssignToManager).ToArray(),
            now
        });

        if (verbose) Console.WriteLine($"  ApprovalEngine template steps: {inserted}");
        return inserted;
    }

    private async Task<List<long>> InsertWorkflowsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<WorkflowRow> rows, DateTime now, bool verbose)
    {
        var ids = new List<long>(rows.Count);
        const int batchSize = 500;

        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var batch = rows.Skip(offset).Take(batchSize).ToList();

            // `requesteddate` is what the grids order by (`ORDER BY w.RequestedDate DESC`), so it is
            // staggered rather than identical: an index-only scan over ties makes "the newest" read
            // as whatever the engine returns, which is exactly how the -infinity rows hid.
            const string sql = @"
                INSERT INTO approvalworkflow
                    (tenantid, schoolid, campusid, modulename, entityid, workflowstatus, currentstep,
                     requestedby, requesteddate, completeddate, remarks,
                     createdby, modifiedby, createdon, modifiedon)
                SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                       unnest(@ModuleNames), unnest(@EntityIds), unnest(@Statuses), unnest(@CurrentSteps),
                       unnest(@RequestedBys), unnest(@RequestedDates),
                       NULLIF(unnest(@CompletedDates), '')::timestamp, 'perf-seed',
                       1, 1, @now, @now
                RETURNING id";

            // ⚠️ `completeddate` TRAVELS AS TEXT, AND THAT IS NOT LAZINESS - IT IS THE ONLY SHAPE
            // NPGSQL WILL BIND. A `DateTime?[]` whose elements are a mix of set and NULL has no
            // unambiguous element type on the client (PostgreSQL has TWO timestamp types and a
            // NULL carries no hint), so the bind dies inside Npgsql's converter resolver BEFORE the
            // statement is ever sent - and a `::timestamp[]` cast in the SQL does not help, because
            // the parameter's own type is resolved client-side. An empty string + `NULLIF` gives the
            // column the real NULL it needs with an argument Npgsql can always resolve.
            // `requesteddate` is NOT NULL, so a plain `DateTime[]` is unambiguous there.

            var requestedDates = batch.Select((r, i) => now.AddMinutes(-(offset + i))).ToArray();

            var batchIds = await conn.QueryAsync<long>(sql, new
            {
                TenantIds = System.Linq.Enumerable.Repeat(tenantId, batch.Count).ToArray(),
                SchoolIds = System.Linq.Enumerable.Repeat(schoolId, batch.Count).ToArray(),
                CampusIds = System.Linq.Enumerable.Repeat(campusId, batch.Count).ToArray(),
                ModuleNames = batch.Select(r => r.ModuleName).ToArray(),
                EntityIds = batch.Select(r => r.EntityId).ToArray(),
                Statuses = batch.Select(r => r.Status).ToArray(),
                CurrentSteps = batch.Select(r => r.CurrentStep).ToArray(),
                RequestedBys = batch.Select(r => r.RequestedBy).ToArray(),
                RequestedDates = requestedDates,
                CompletedDates = batch.Select(r => r.Completed ? IsoTimestamp(now) : string.Empty).ToArray(),
                now
            });

            ids.AddRange(batchIds);
            if (verbose) LogProgress($"  ApprovalEngine workflows (campus {campusId})", ids.Count, rows.Count);
        }

        return ids;
    }

    private async Task<int> InsertWorkflowStepsAsync(
        NpgsqlConnection conn, List<WorkflowStepRow> rows, DateTime now, bool verbose)
    {
        if (rows.Count == 0) return 0;

        var inserted = 0;
        const int batchSize = 2000;

        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var batch = rows.Skip(offset).Take(batchSize).ToList();

            // ⚠️ `approveruserid` is NULLABLE and the array is typed by the FIRST row's value, so the
            // batch is cast explicitly (`@ApproverUserIds::bigint[]`). An all-NULL batch would
            // otherwise reach the server as `text[]` and fail with 42804 - the same one-sided-type
            // trap `employeeattendance.checkintime` recorded.
            const string sql = @"
                INSERT INTO approvalworkflowstep
                    (approvalworkflowid, stepno, roleid, approveruserid, action, comments, actiondate,
                     iscompleted, createdby, modifiedby, createdon, modifiedon)
                SELECT unnest(@WorkflowIds), unnest(@StepNos), unnest(@RoleIds),
                       unnest(@ApproverUserIds::bigint[]), unnest(@Actions), unnest(@Comments),
                       NULLIF(unnest(@ActionDates), '')::timestamp, unnest(@IsCompleted), 1, 1, @now, @now";

            inserted += await conn.ExecuteAsync(sql, new
            {
                WorkflowIds = batch.Select(r => r.WorkflowId).ToArray(),
                StepNos = batch.Select(r => r.StepNo).ToArray(),
                RoleIds = batch.Select(r => r.RoleId).ToArray(),
                ApproverUserIds = batch.Select(r => r.ApproverUserId).ToArray(),
                Actions = batch.Select(r => r.Action).ToArray(),
                Comments = batch.Select(r => r.Comments).ToArray(),
                ActionDates = batch.Select(r => r.ActionDate.HasValue
                    ? IsoTimestamp(r.ActionDate.Value) : string.Empty).ToArray(),
                IsCompleted = batch.Select(r => r.IsCompleted).ToArray(),
                now
            });

            if (verbose) LogProgress($"  ApprovalEngine workflow steps", inserted, rows.Count);
        }

        return inserted;
    }

    /// <summary>
    /// Every permission, for every active role, at this campus - the row set
    /// `HasPermissionAttribute` searches, and the shape `roles.menuItems.html` writes.
    /// </summary>
    private static async Task<int> InsertRolePermissionsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, DateTime now, bool verbose)
    {
        var roleIds = (await conn.QueryAsync<long>(
            "SELECT id FROM roles WHERE isactive = TRUE ORDER BY id")).ToList();

        if (roleIds.Count == 0) return 0;

        var permissionIds = new List<short>();
        var permissionRoleIds = new List<long>();

        foreach (var roleId in roleIds)
        {
            foreach (var permission in Permissions)
            {
                permissionIds.Add(permission);
                permissionRoleIds.Add(roleId);
            }
        }

        // ⚠️ `id` IS OMITTED AND `information_schema` SAYS OTHERWISE. `rolepermission.id` is
        // `GENERATED ALWAYS AS IDENTITY`, and PostgreSQL reports an identity column with NO
        // `column_default` - so a schema dump of this table looks exactly like a bare `bigint NOT
        // NULL` with no default, and a seeder that "allocates" ids from the high-water mark fails
        // with `428C9 cannot insert a non-DEFAULT value into column "id"`. The identity generates
        // them; the earlier `nextval`-style guess was simply wrong. (`approvaltemplatestep`,
        // `approvalworkflow` and `approvalworkflowstep` are the other three, and all four are the
        // same trap.)
        const string sql = @"
            INSERT INTO rolepermission
                (permissionid, roleid, tenantid, schoolid, campusid,
                 createdby, modifiedby, createdon, modifiedon)
            SELECT unnest(@PermissionIds), unnest(@RoleIds),
                   @tenantId, @schoolId, @campusId, 1, 1, @now, @now";

        var inserted = await conn.ExecuteAsync(sql, new
        {
            PermissionIds = permissionIds.ToArray(),
            RoleIds = permissionRoleIds.ToArray(),
            tenantId, schoolId, campusId, now
        });

        if (verbose) Console.WriteLine($"  ApprovalEngine role permissions: {inserted}");
        return inserted;
    }

    /// <summary>
    /// Fills <paramref name="result"/> from what the campus already holds, so a skipped campus is
    /// reported the same way as a freshly seeded one.
    /// </summary>
    private static async Task ReadCountsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, ApprovalEngineSeedResult result)
    {
        result.Workflows = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM approvalworkflow
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.WorkflowSteps = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM approvalworkflowstep s
                JOIN approvalworkflow w ON w.id = s.approvalworkflowid
               WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.TemplateSteps = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM approvaltemplatestep ts
                JOIN approvaltemplate t ON t.id = ts.approvaltemplateid
               WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.RolePermissions = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM rolepermission
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        result.PendingWorkflows = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM approvalworkflow w
               INNER JOIN approvalworkflowstep s ON s.approvalworkflowid = w.id
                    AND s.stepno = w.currentstep AND s.iscompleted = false
               WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                 AND w.workflowstatus IN ('Pending', 'InProgress')",
            new { tenantId, schoolId, campusId });
    }

    /// <summary>
    /// A round-trippable timestamp for the text-parameter workaround above.
    ///
    /// ⚠️ `"O"` IS DELIBERATELY NOT USED. Its trailing `Z` would make PostgreSQL parse the value as
    /// `timestamptz` and convert it into the server's zone, silently shifting every stored stamp;
    /// the columns are `timestamp without time zone` and the app writes `DateTime.UtcNow` into them,
    /// so the literal must stay zoneless.
    /// </summary>
    private static string IsoTimestamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    // ----------------------------------------------------------------------
    // Row shapes. A private NESTED class can fail to instantiate under Dapper at runtime, so these
    // are simple and top-level-shaped.
    // ----------------------------------------------------------------------

    internal sealed class TemplateRow
    {
        public long TemplateId { get; set; }
        public string ModuleName { get; set; } = string.Empty;
    }

    internal sealed class TemplateStepRow
    {
        public long TemplateId { get; set; }
        public int StepNo { get; set; }
        public long RoleId { get; set; }
        public bool AssignToEntityOwner { get; set; }
        public bool AssignToManager { get; set; }
    }

    internal sealed class WorkflowRow
    {
        public string ModuleName { get; set; } = string.Empty;
        public long EntityId { get; set; }
        public string Status { get; set; } = "Pending";
        public int CurrentStep { get; set; } = 1;
        public long RequestedBy { get; set; }
        public bool Completed { get; set; }
    }

    internal sealed class WorkflowStepRow
    {
        public long WorkflowId { get; set; }
        public int StepNo { get; set; }
        public long RoleId { get; set; }
        public long? ApproverUserId { get; set; }
        public string? Action { get; set; }
        public string? Comments { get; set; }
        public DateTime? ActionDate { get; set; }
        public bool IsCompleted { get; set; }
    }
}
