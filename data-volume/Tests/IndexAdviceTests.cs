using SchoolPerformance.QueryShapes;
using Xunit;

namespace SchoolPerformance.Tests;

/// <summary>
/// The rules that decide WHICH indexes a run is allowed to propose, and under what name.
///
/// ⚠️ These deserve tests because both failure modes are SILENT:
///   * proposing an index that another one already covers adds write cost and no read benefit -
///     nothing breaks, the table just gets slower to write;
///   * proposing an index under a name a migration already declares makes that migration a
///     no-op that still records as applied.
/// Neither shows up as a failure anywhere, so neither can be caught by running the tool once.
/// </summary>
public class IndexAdviceTests
{
    private static IndexCandidate Candidate(string table, string name, string columns) =>
        new(table, name, columns, "test rationale");

    [Fact]
    public void Reduce_collapses_one_index_proposed_by_two_queries()
    {
        // The student grid asks for the same index from both halves of its load - the page and
        // the count. That is ONE index, not two.
        var reduced = IndexAdvice.Reduce(new[]
        {
            Candidate("student", "ix_student_tenantschoolcampus_isactive", "tenantid, schoolid, campusid, isactive"),
            Candidate("student", "ix_student_tenantschoolcampus_isactive", "tenantid, schoolid, campusid, isactive"),
        });

        var kept = Assert.Single(reduced);
        Assert.Equal("ix_student_tenantschoolcampus_isactive", kept.Name);
    }

    [Fact]
    public void Reduce_drops_an_index_a_longer_one_already_covers()
    {
        // PostgreSQL serves (tenantid, schoolid, campusid) from an index on
        // (tenantid, schoolid, campusid, isactive), so shipping both is write cost for no read
        // benefit. This is the dashboard's index versus the student grid's.
        var reduced = IndexAdvice.Reduce(new[]
        {
            Candidate("student", "ix_student_tenantschoolcampus", "tenantid, schoolid, campusid"),
            Candidate("student", "ix_student_tenantschoolcampus_isactive", "tenantid, schoolid, campusid, isactive"),
        });

        var kept = Assert.Single(reduced);
        Assert.Equal("ix_student_tenantschoolcampus_isactive", kept.Name);
    }

    [Fact]
    public void Reduce_keeps_indexes_that_do_not_cover_each_other()
    {
        // (tenantid, campusid) is covered by both of the others, so it goes. The two that remain
        // are genuinely different indexes: the register reads ONE classroom on ONE day, while the
        // grid reads the campus newest-first.
        var reduced = IndexAdvice.Reduce(new[]
        {
            Candidate("attendance", "idx_attendance_scope", "tenantid, campusid"),
            Candidate("attendance", "idx_attendance_scope_date", "tenantid, campusid, attendancedate DESC"),
            Candidate("attendance", "idx_attendance_classroom_date", "tenantid, campusid, classroomid, attendancedate"),
        });

        Assert.Equal(2, reduced.Count);
        Assert.DoesNotContain(reduced, candidate => candidate.Name == "idx_attendance_scope");
        Assert.Contains(reduced, candidate => candidate.Name == "idx_attendance_scope_date");
        Assert.Contains(reduced, candidate => candidate.Name == "idx_attendance_classroom_date");
    }

    [Fact]
    public void Reduce_never_merges_across_tables()
    {
        // Same columns, different tables: two indexes, on two tables.
        var reduced = IndexAdvice.Reduce(new[]
        {
            Candidate("student", "ix_student_scope", "tenantid, campusid"),
            Candidate("attendance", "idx_attendance_scope", "tenantid, campusid"),
        });

        Assert.Equal(2, reduced.Count);
    }

    [Fact]
    public void Reduce_treats_a_sort_direction_as_part_of_the_index()
    {
        // (a, b) does NOT cover (a, b DESC) - the planner cannot use one for the other, so
        // merging them would silently change a query's plan rather than save an index.
        var reduced = IndexAdvice.Reduce(new[]
        {
            Candidate("t", "ix_t_ab", "tenantid, campusid"),
            Candidate("t", "ix_t_ab_desc", "tenantid, campusid DESC"),
        });

        Assert.Equal(2, reduced.Count);
    }

    [Fact]
    public void The_probe_name_is_disposable_and_a_legal_identifier()
    {
        var candidate = Candidate("student", "ix_student_tenantschoolcampus_isactive",
            "tenantid, schoolid, campusid, isactive");

        var probe = IndexAdvice.ProbeName(candidate);

        Assert.StartsWith(IndexAdvice.ProbePrefix, probe);
        // A leaked probe is only safe to drop by hand if it cannot be mistaken for a real index.
        Assert.NotEqual(candidate.Name, probe);
        Assert.Matches("^[a-z_][a-z0-9_]*$", probe);
        Assert.True(probe.Length <= IndexAdvice.MaxIdentifierLength);
    }

    [Fact]
    public void The_probe_name_stays_within_the_identifier_limit()
    {
        var candidate = Candidate("attendance", "idx_x",
            "tenantid, schoolid, campusid, classroomid, studentenrollmentid, attendancedate, attendancestatusid");

        Assert.True(IndexAdvice.ProbeName(candidate).Length <= IndexAdvice.MaxIdentifierLength);
    }

    [Fact]
    public void The_deployed_ddl_is_idempotent_and_schema_qualified()
    {
        var ddl = IndexAdvice.CreateDdl(Candidate("student", "ix_student_scope", "tenantid, campusid"));

        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS ix_student_scope ON public.student USING btree (tenantid, campusid)",
            ddl);
    }

    [Fact]
    public void The_probe_ddl_never_uses_the_deployed_name()
    {
        var candidate = Candidate("student", "ix_student_scope", "tenantid, campusid");

        var probe = IndexAdvice.ProbeDdl(candidate);

        Assert.DoesNotContain(candidate.Name, probe);
        Assert.Contains(IndexAdvice.ProbeName(candidate), probe);
    }
}
