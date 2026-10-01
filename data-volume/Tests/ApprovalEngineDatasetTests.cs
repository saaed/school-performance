using Dapper;
using Npgsql;
using SchoolPerformance.Seeders;
using Xunit;
using Xunit.Abstractions;

namespace SchoolPerformance.Tests;

/// <summary>
/// Seeds the APPROVAL ENGINE (`approvaltemplatestep`, `approvalworkflow`, `approvalworkflowstep`)
/// and `rolepermission`, then asserts the joins and rules that make the module's two paged grids
/// measure instead of returning an empty page.
///
/// ⚠️ WHY THE ASSERTIONS ARE JOINS AND NOT COUNTS. `approvalworkflow` held zero rows and so did its
/// steps, but `approvaltemplate` already held its eight rows - so the module LOOKED configured and
/// routed nothing. A count of the rows this seeder wrote cannot see that: the inbox is a five-way
/// join whose WHERE clause decides whether a row is reachable at all, and a cycle whose current step
/// is closed, missing, or numbered differently from `w.CurrentStep` is INVISIBLE while every table
/// looks populated. The five contracts below are therefore the query's own predicates:
///
///   * every cycle the INBOX can show must have a step with `StepNo = CurrentStep` and
///     `IsCompleted = false`, and that step must name an approver or a role somebody holds;
///   * `w.CurrentStep` must be a step number the cycle actually HAS, or the join is unsatisfiable;
///   * every step's `RoleId` must resolve in `roles`, because the grid renders the role NAME through
///     a LEFT JOIN - an invented role id is a blank approver column on a healthy row;
///   * `myworkflows` must return rows for the same identity the inbox is opened as, so both grids of
///     the module are measurable from one `ScopeVars.UserId`;
///   * and NO timestamp may be `-infinity`. This is `ApprovalTemplateStep`'s original defect: an
///     unassigned `DateTime.MinValue` reaches PostgreSQL as `-infinity`, sorts BEFORE every real
///     timestamp, and ties every row in `ORDER BY RequestedDate DESC` - so "the newest approval" is
///     whatever the engine happens to return. The seeder writes its stamps explicitly; this holds it.
///
/// ⚠️ IT DEPENDS ON `PerfDatasetSeeder` (a campus with students) AND on `approvaltemplate` already
/// holding the eight module templates - those are CONFIGURATION, not facts, and inventing them here
/// would make this seeder responsible for a screen (`hr.approval.template`) it is not measuring.
///
/// Opt in with the same flag the other dataset seeders use:
///
///     SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 \
///       dotnet test data-volume/SchoolDataVolume.csproj --no-build \
///       --filter "FullyQualifiedName~ApprovalEngineDataset"
/// </summary>
public sealed class ApprovalEngineDatasetTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString = SeedCampuses.ConnectionString;

    public ApprovalEngineDatasetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Approval_engine_dataset_fills_the_inbox_and_my_requests_grids()
    {
        if (!SeedCampuses.DatasetEnabled)
        {
            _output.WriteLine("SKIPPED: set SCUBE_PERF_DATASET=1 to build the approval-engine perf dataset.");
            return;
        }

        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var campusIds = await SeedCampuses.CampusesAsync(conn);
        Assert.True(campusIds.Count > 0,
            "the student table holds no campus, so there is no scope to seed approvals into - " +
            "run PerfDatasetTests first");

        var options = new ApprovalEngineOptions
        {
            WorkflowsPerModule = SeedCampuses.EnvInt("SCUBE_PERF_APPR_WORKFLOWS_PER_MODULE", 40),
            StepsPerTemplate = SeedCampuses.EnvInt("SCUBE_PERF_APPR_STEPS", 2),
            Force = SeedCampuses.Force,
        };

        _output.WriteLine($"Seeding APPROVAL ENGINE for {campusIds.Count} campus(es) " +
                          $"[{string.Join(", ", campusIds)}]: " +
                          $"{options.WorkflowsPerModule} workflows/module, {options.StepsPerTemplate} steps");
        _output.WriteLine("");

        var seeder = new ApprovalEngineSeeder(_connectionString);
        var totalWorkflows = 0;
        var seededCampusIds = new List<long>();

        foreach (var campusId in campusIds)
        {
            var result = await seeder.SeedAsync(
                SeedCampuses.TenantId, SeedCampuses.SchoolId, campusId, options, verbose: false);

            totalWorkflows += result.Workflows;

            _output.WriteLine(
                $"  campus {campusId,-5} {result.TemplateSteps,3} template steps " +
                $"{result.Workflows,4} workflows ({result.PendingWorkflows,4} open) " +
                $"{result.WorkflowSteps,5} steps {result.RolePermissions,3} role perms " +
                $"approver={result.ApproverUserId} requester={result.RequestedByUserId}" +
                $"{(result.Skipped ? "  [already had data - skipped]" : "")}");

            if (result.Skipped) continue;

            seededCampusIds.Add(campusId);
        }

        _output.WriteLine("");
        _output.WriteLine($"total: {totalWorkflows:N0} approval workflows");

        Assert.True(totalWorkflows > 0,
            "no approval workflow was seeded, so the inbox and my-requests grids over " +
            "`approvalworkflow` would still measure an empty result");

        if (seededCampusIds.Count > 0)
        {
            await AssertInboxJoinIsSatisfiableAsync(conn, seededCampusIds);
            await AssertEveryRoleResolvesAsync(conn, seededCampusIds);
            await AssertBothGridsReturnRowsForTheMeasuredUserAsync(conn, seededCampusIds);
            await AssertNoSentinelTimestampsAsync(conn, seededCampusIds);
        }
    }

    /// <summary>
    /// The inbox's WHERE clause, held together. For every OPEN cycle there must be a step whose
    /// `StepNo` equals `CurrentStep` and whose `IsCompleted` is false - otherwise the five-way join
    /// matches nothing and the workflow is a row no screen can list.
    ///
    /// ⚠️ `CurrentStep` must also be a number the cycle HAS. A cycle left at step 3 of a two-step
    /// chain satisfies the equality but joins to nothing, which reads identically from the table.
    /// </summary>
    private static async Task AssertInboxJoinIsSatisfiableAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var unreachable = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM approvalworkflow w
                   WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                     AND w.workflowstatus IN ('Pending', 'InProgress')
                     AND NOT EXISTS (
                           SELECT 1 FROM approvalworkflowstep s
                            WHERE s.approvalworkflowid = w.id
                              AND s.stepno = w.currentstep
                              AND s.iscompleted = false)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(unreachable == 0,
                $"campus {campusId} holds {unreachable} OPEN workflow(s) whose join to " +
                "`approvalworkflowstep` cannot match - the inbox would list none of them while " +
                "`approvalworkflow` looks populated");

            var outOfRange = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM approvalworkflow w
                   WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                     AND w.currentstep > (SELECT COALESCE(MAX(s.stepno), 0)
                                            FROM approvalworkflowstep s
                                           WHERE s.approvalworkflowid = w.id)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(outOfRange == 0,
                $"campus {campusId} holds {outOfRange} workflow(s) whose `CurrentStep` is a step " +
                "number the cycle does not have - a satisfiable-looking WHERE clause that joins to " +
                "nothing");
        }
    }

    /// <summary>
    /// Both grids render the step's role NAME through `LEFT JOIN Roles`, so a role id that does not
    /// resolve is a blank approver column on a row the screen happily shows. The templates and the
    /// workflows are both checked, because they are written from the same resolved role list.
    /// </summary>
    private static async Task AssertEveryRoleResolvesAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var orphanWorkflowSteps = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM approvalworkflowstep s
                    JOIN approvalworkflow w ON w.id = s.approvalworkflowid
                   WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = s.roleid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphanWorkflowSteps == 0,
                $"campus {campusId} holds {orphanWorkflowSteps} workflow step(s) whose `roleid` " +
                "resolves to no `roles` row - the inbox would render a blank approver");

            var orphanTemplateSteps = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM approvaltemplatestep ts
                    JOIN approvaltemplate t ON t.id = ts.approvaltemplateid
                   WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                     AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = ts.roleid)",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(orphanTemplateSteps == 0,
                $"campus {campusId} holds {orphanTemplateSteps} template step(s) whose `roleid` " +
                "resolves to no `roles` row");
        }
    }

    /// <summary>
    /// ⚠️ THE POINT OF THE WHOLE SEED: both paged grids must return rows for the SAME identity, and
    /// that identity is resolved by ONE rule (the campus's lowest-numbered user) so the fixture and
    /// `db-report` cannot disagree about who the inbox belongs to.
    ///
    /// The two grids match on different columns - the inbox on `s.ApproverUserId`, "my requests" on
    /// `w.RequestedBy` - so a seed that satisfies one can leave the other empty, which is a spec that
    /// reports SKIP for a screen full of rows.
    /// </summary>
    private static async Task AssertBothGridsReturnRowsForTheMeasuredUserAsync(
        NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var userId = await conn.ExecuteScalarAsync<long?>(
                @"SELECT id FROM users
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                   ORDER BY id LIMIT 1",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(userId is > 0,
                $"campus {campusId} holds no user, so neither grid can be opened by anybody");

            var inbox = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM approvalworkflow w
                   INNER JOIN approvalworkflowstep s ON s.approvalworkflowid = w.id
                        AND s.stepno = w.currentstep AND s.iscompleted = false
                   WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                     AND w.workflowstatus IN ('Pending', 'InProgress')
                     AND (s.approveruserid = @userId
                          OR EXISTS (SELECT 1 FROM userrole ur
                                      WHERE ur.roleid = s.roleid AND ur.userid = @userId))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId, userId });

            Assert.True(inbox > 0,
                $"campus {campusId}'s inbox grid returns 0 rows for user {userId} - the spec over " +
                "`GetPendingApprovals` would report SKIP for a screen a real approver sees populated");

            var mine = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM approvalworkflow w
                   WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                     AND w.requestedby = @userId",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId, userId });

            Assert.True(mine > 0,
                $"campus {campusId}'s my-requests grid returns 0 rows for user {userId} - the spec " +
                "over `GetWorkflowHistory` would report SKIP");
        }
    }

    /// <summary>
    /// ⚠️ THE SENTINEL GUARD. `approvaltemplatestep` is one of the two entities where the
    /// `-infinity` defect was originally found: `GenericRepository.GenerateInsertQuery` names EVERY
    /// column, so the column's `DEFAULT now()` can never apply and an unassigned `DateTime.MinValue`
    /// is written as `-infinity`. That value sorts BEFORE every real timestamp, so
    /// `ORDER BY RequestedDate DESC` ties across every affected row and "the newest approval"
    /// becomes arbitrary - which is how it reached production looking like a working history.
    ///
    /// The seeder writes raw INSERTs with an explicit stamp, so the sentinel cannot appear; this
    /// holds that promise against the three tables it writes and the column the grids sort by.
    /// </summary>
    private static async Task AssertNoSentinelTimestampsAsync(NpgsqlConnection conn, List<long> campusIds)
    {
        foreach (var campusId in campusIds)
        {
            var bad = await conn.ExecuteScalarAsync<long>(
                @"SELECT
                    (SELECT COUNT(*) FROM approvalworkflow
                      WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                        AND (requesteddate = '-infinity'::timestamp
                             OR createdon = '-infinity'::timestamp
                             OR modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM approvalworkflowstep s
                       JOIN approvalworkflow w ON w.id = s.approvalworkflowid
                      WHERE w.tenantid = @tenantId AND w.schoolid = @schoolId AND w.campusid = @campusId
                        AND (s.createdon = '-infinity'::timestamp
                             OR s.modifiedon = '-infinity'::timestamp))
                  + (SELECT COUNT(*) FROM approvaltemplatestep ts
                       JOIN approvaltemplate t ON t.id = ts.approvaltemplateid
                      WHERE t.tenantid = @tenantId AND t.schoolid = @schoolId AND t.campusid = @campusId
                        AND (ts.createdon = '-infinity'::timestamp
                             OR ts.modifiedon = '-infinity'::timestamp))",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(bad == 0,
                $"campus {campusId} holds {bad} row(s) stamped `-infinity` - the sentinel that ties " +
                "`ORDER BY RequestedDate DESC` and makes the approval history order arbitrary");

            var ties = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM (
                      SELECT requesteddate FROM approvalworkflow
                       WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                    GROUP BY requesteddate HAVING COUNT(*) > 1) t",
                new { tenantId = SeedCampuses.TenantId, schoolId = SeedCampuses.SchoolId, campusId });

            Assert.True(ties == 0,
                $"campus {campusId} holds {ties} `requesteddate` value(s) shared by more than one " +
                "workflow - `ORDER BY w.RequestedDate DESC` then has no defined winner, which is " +
                "exactly the state a sentinel timestamp produces");
        }
    }
}
