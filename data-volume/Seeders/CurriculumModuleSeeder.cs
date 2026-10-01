using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>
/// Options for <see cref="CurriculumModuleSeeder"/>. The defaults build a school-sized curriculum:
/// ten grades, seven subjects each, fifteen topics per subject, two plans per topic.
/// </summary>
public sealed class CurriculumSeedOptions
{
    /// <summary>Grades the published curriculum version carries (KG..Grade 10 is ten).</summary>
    public int GradesPerVersion { get; set; } = 10;

    /// <summary>Subjects taught in every grade.</summary>
    public int SubjectsPerGrade { get; set; } = 7;

    /// <summary>Topics a subject covers in a year. This is the axis that makes the report a read.</summary>
    public int TopicsPerSubject { get; set; } = 15;

    /// <summary>Term plans behind each topic (the view's LATERAL counts them).</summary>
    public int PlansPerTopic { get; set; } = 2;

    /// <summary>
    /// Re-seed the topics and plans this seeder owns. Only rows named with its own prefix are
    /// removed - a curriculum is shared with the grades classrooms already point at.
    /// </summary>
    public bool Force { get; set; }
}

/// <summary>What the curriculum seed produced.</summary>
public sealed class CurriculumSeedResult
{
    public bool Skipped { get; set; }
    public long CurriculumVersionId { get; set; }
    public int Subjects { get; set; }
    public int CurriculumGrades { get; set; }
    public int CurriculumGradeSubjects { get; set; }
    public int Topics { get; set; }
    public int TopicPlans { get; set; }
}

/// <summary>
/// Seeds the curriculum TREE behind `vw_learning_outcome_performance` - grades, subjects, topics and
/// their term plans - for one tenant + school.
///
/// WHY THIS EXISTS
/// ---------------
/// `LEARNING_OUTCOME_PERFORMANCE` reported SKIP because `vw_learning_outcome_performance` returned
/// ONE row (`curriculumgradesubjecttopic` held a single topic and `curriculumtopicplan` none). The
/// view is a six-table INNER JOIN over the curriculum structure:
///
///     curriculumgradesubjecttopic
///       -> curriculumgradesubject -> curriculumgrade -> curriculumversion -> curriculum
///       -> subject
///       -> LATERAL (curriculumtopicplan: count + sum of durationminutes)
///
/// so a topic whose grade or subject does not resolve is INVISIBLE - the same trap that made the
/// attendance report return nothing while the table held 14.2M rows.
///
/// ⚠️ THE VIEW IS **SCHOOL-SCOPED** (tenant + school, NO campus), which is why this seeder takes no
/// campus: the report counts every topic the school's published curriculum carries. That also makes
/// the volume a SCHOOL-level number, and a real school's curriculum is properly large - roughly
/// grades x subjects x topics. The defaults here land on ~1,050 topics for one version, which is the
/// shape (and the order of magnitude) the report is meant to page over.
///
/// ⚠️ `subject`, `curriculumgradesubjecttopic` AND `curriculumtopicplan` ARE
/// `GENERATED ALWAYS AS IDENTITY`, so their ids are assigned by the DATABASE and supplying one is a
/// **428C9** ("cannot insert a non-DEFAULT value into column id") - the same trap the PDF plan
/// recorded on `reporttemplate`/`documentsettings`. Every insert here either omits the id or reads
/// it back with `RETURNING id`.
///
/// ⚠️ This seeder never invents a curriculum or a version, either: it attaches to the school's
/// PUBLISHED version (`curriculumstatus = 5`, the filter the view applies). Inventing a second
/// version would give the report a tree the application never serves.
///
/// ADDITIVE BY DESIGN, EXCEPT FOR ITS OWN ROWS. The tree it builds is shared with the `academicgrade`
/// rows classrooms already point at, so it never deletes a grade, a version or a subject. `Force`
/// removes only the topics and plans carrying its own `PERF-` prefix.
/// </summary>
public sealed class CurriculumModuleSeeder : BaseSeeder
{
    public CurriculumModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk curriculum load invalidates.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "subject", "curriculumgrade", "curriculumgradesubject",
        "curriculumgradesubjecttopic", "curriculumtopicplan"
    };

    /// <summary>The marker every row this seeder creates carries, so Force can find its own work.</summary>
    private const string TopicPrefix = "PERF-TOPIC-";
    private const string SubjectPrefix = "PERF-SUBJ-";

    private static readonly string[] SubjectNames =
    {
        "Mathematics", "English", "Science", "Arabic", "Islamic Studies",
        "Social Studies", "Computer Science", "Physical Education", "Art", "Music"
    };

    public async Task<CurriculumSeedResult> SeedAsync(
        long tenantId, long schoolId, CurriculumSeedOptions options, bool verbose = true)
    {
        var result = new CurriculumSeedResult();
        using var conn = await OpenConnectionAsync();
        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // 1. The PUBLISHED version - the only one the view reads. Attaching to a Draft would seed
        //    ~1,050 rows that no report can ever see.
        // ------------------------------------------------------------------
        var version = await conn.QueryFirstOrDefaultAsync<VersionRow>(
            @"SELECT cv.id AS VersionId, c.id AS CurriculumId
                FROM curriculumversion cv
                JOIN curriculum c ON c.id = cv.curriculumid
               WHERE cv.curriculumstatus = 5
                 AND c.tenantid = @tenantId AND c.schoolid = @schoolId
               ORDER BY cv.id LIMIT 1",
            new { tenantId, schoolId });

        if (version is null || version.VersionId == 0)
        {
            throw new InvalidOperationException(
                $"tenant {tenantId} / school {schoolId} holds no PUBLISHED curriculum version " +
                "(curriculumstatus = 5), which is the only tree " +
                "vw_learning_outcome_performance reads - publish one first.");
        }
        result.CurriculumVersionId = version.VersionId;

        var existingTopics = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM curriculumgradesubjecttopic tp
                JOIN curriculumgradesubject cgs ON cgs.id = tp.curriculumgradesubjectid
                JOIN curriculumgrade cg ON cg.id = cgs.curriculumgradeid
               WHERE cg.curriculumversionid = @versionId",
            new { versionId = version.VersionId });

        // The target is derived from the options rather than written as a literal, so raising the
        // knobs on a later run tops the tree up instead of silently skipping work it did not do.
        var targetTopics = (long)options.GradesPerVersion * options.SubjectsPerGrade * options.TopicsPerSubject;

        if (!options.Force && existingTopics >= targetTopics)
        {
            result.Skipped = true;
            result.Topics = (int)existingTopics;
            if (verbose)
            {
                Console.WriteLine(
                    $"  Curriculum: version {version.VersionId} already holds {existingTopics:N0} topics " +
                    $"(target {targetTopics:N0}) - skipped");
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 2. Subjects - a tenant + school level CATALOG (no campus, no sequence). Read first, then
        //    top up, so a school that already names its subjects is not given duplicates.
        // ------------------------------------------------------------------
        var subjectIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM subject
               WHERE tenantid = @tenantId AND schoolid = @schoolId
               ORDER BY id",
            new { tenantId, schoolId })).ToList();

        if (subjectIds.Count < options.SubjectsPerGrade)
        {
            for (var i = subjectIds.Count; i < options.SubjectsPerGrade; i++)
            {
                // ⚠️ No `id` in the column list: `subject.id` is GENERATED ALWAYS AS IDENTITY, so
                // supplying one is a **428C9** ("cannot insert a non-DEFAULT value into column id").
                // The list is re-read below, so the id does not have to come back here.
                await conn.ExecuteAsync(
                    @"INSERT INTO subject
                          (tenantid, schoolid, name, code, description, category,
                           isactive, defaultcredits, createdby, modifiedby, createdon, modifiedon)
                      VALUES (@tenantId, @schoolId, @name, @code, @description, 1,
                              true, 3, 1, 1, @now, @now)",
                    new
                    {
                        tenantId, schoolId,
                        name = $"{SubjectPrefix}{i + 1} {SubjectNames[i % SubjectNames.Length]}",
                        code = $"PERF-SUBJ-{schoolId}-{i + 1}",
                        description = "Seeded subject for the learning-outcome report dataset",
                        now
                    });
            }

            subjectIds = (await conn.QueryAsync<long>(
                @"SELECT id FROM subject
                   WHERE tenantid = @tenantId AND schoolid = @schoolId
                   ORDER BY id",
                new { tenantId, schoolId })).ToList();
        }
        result.Subjects = subjectIds.Count;

        // ------------------------------------------------------------------
        // 3. Grades on the version (sequence-backed, so no id plumbing needed).
        // ------------------------------------------------------------------
        var gradeIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM curriculumgrade
               WHERE curriculumversionid = @versionId ORDER BY id",
            new { versionId = version.VersionId })).ToList();

        for (var i = gradeIds.Count; i < options.GradesPerVersion; i++)
        {
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO curriculumgrade
                      (curriculumversionid, name, code, minimumage, maximumage, description,
                       createdby, modifiedby, createdon, modifiedon)
                  VALUES (@versionId, @name, @code, @minAge, @maxAge, @description,
                          1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    versionId = version.VersionId,
                    name = $"PERF-GRADE-{i + 1}",
                    code = $"PG{i + 1}",
                    minAge = 4 + i,
                    maxAge = 5 + i,
                    description = "Seeded grade for the learning-outcome report dataset",
                    now
                });
            gradeIds.Add(id);
        }
        result.CurriculumGrades = gradeIds.Count;

        // ------------------------------------------------------------------
        // 4. The (grade, subject) mapping, then the topics and their plans.
        // ------------------------------------------------------------------
        var mappingIds = new List<long>();
        var termId = await ResolveTermIdAsync(conn, tenantId, schoolId);
        var topics = 0;
        var plans = 0;

        foreach (var gradeId in gradeIds)
        {
            foreach (var subjectId in subjectIds.Take(options.SubjectsPerGrade))
            {
                var mappingId = await conn.ExecuteScalarAsync<long?>(
                    @"SELECT id FROM curriculumgradesubject
                       WHERE curriculumgradeid = @gradeId AND subjectid = @subjectId",
                    new { gradeId, subjectId });

                if (mappingId is null || mappingId == 0)
                {
                    mappingId = await conn.ExecuteScalarAsync<long>(
                        @"INSERT INTO curriculumgradesubject
                              (curriculumgradeid, subjectid, displayorder, weeklyperiods,
                               isoptional, passingmarks, totalmarks,
                               createdby, modifiedby, createdon, modifiedon)
                          VALUES (@gradeId, @subjectId, @displayOrder, 5,
                                  false, 40, 100, 1, 1, @now, @now)
                          RETURNING id",
                        new
                        {
                            gradeId, subjectId,
                            displayOrder = subjectIds.IndexOf(subjectId) + 1,
                            now
                        });
                }

                var mapping = mappingId.Value;
                mappingIds.Add(mapping);

                var existingForMapping = await conn.ExecuteScalarAsync<int>(
                    @"SELECT COUNT(*) FROM curriculumgradesubjecttopic
                       WHERE curriculumgradesubjectid = @mapping AND topicname LIKE @prefix",
                    new { mapping, prefix = TopicPrefix + "%" });

                if (options.Force && existingForMapping > 0)
                {
                    // Only this seeder's own topics, and their plans first (the plan carries the FK).
                    await conn.ExecuteAsync(
                        @"DELETE FROM curriculumtopicplan
                           WHERE curriculumtopicid IN (
                                 SELECT id FROM curriculumgradesubjecttopic
                                  WHERE curriculumgradesubjectid = @mapping AND topicname LIKE @prefix)",
                        new { mapping, prefix = TopicPrefix + "%" });

                    await conn.ExecuteAsync(
                        @"DELETE FROM curriculumgradesubjecttopic
                           WHERE curriculumgradesubjectid = @mapping AND topicname LIKE @prefix",
                        new { mapping, prefix = TopicPrefix + "%" });

                    existingForMapping = 0;
                }

                for (var t = existingForMapping; t < options.TopicsPerSubject; t++)
                {
                    // ⚠️ `curriculumgradesubjecttopic.id` is GENERATED ALWAYS AS IDENTITY, so it is
                    // read back rather than assigned - the plan below needs it as its foreign key.
                    var topicId = await conn.ExecuteScalarAsync<long>(
                        @"INSERT INTO curriculumgradesubjecttopic
                              (curriculumgradesubjectid, topicname, learningobjective,
                               estimatedhours, sequencenumber,
                               createdby, modifiedby, createdon, modifiedon)
                          VALUES (@mapping, @topicName, @objective, @hours, @sequence,
                                  1, 1, @now, @now)
                          RETURNING id",
                        new
                        {
                            mapping,
                            topicName = $"{TopicPrefix}{mapping}-{t + 1}",
                            objective = "By the end of this topic the student can " +
                                        "apply, analyse and explain the concept in unfamiliar contexts.",
                            hours = 6f + (t % 5),
                            sequence = t + 1,
                            now
                        });

                    topics++;

                    for (var p = 0; p < options.PlansPerTopic; p++)
                    {
                        // Also identity-backed - no id supplied.
                        await conn.ExecuteAsync(
                            @"INSERT INTO curriculumtopicplan
                                  (curriculumtopicid, termid, title, planneddate,
                                   durationminutes, curriculumtopicplanstatus,
                                   createdby, modifiedby, createdon, modifiedon)
                              VALUES (@topicId, @termId, @title, @plannedDate,
                                      @minutes, 1, 1, 1, @now, @now)",
                            new
                            {
                                topicId,
                                termId,
                                title = $"PERF-PLAN-{topicId}-{p + 1}",
                                plannedDate = now.AddDays(p * 7).Date,
                                minutes = (short)(45 + p * 15),
                                now
                            });

                        plans++;
                    }
                }
            }
        }

        result.CurriculumGradeSubjects = mappingIds.Count;
        result.Topics = topics;
        result.TopicPlans = plans;

        if (verbose)
        {
            Console.WriteLine(
                $"  Curriculum: version {result.CurriculumVersionId} -> {result.Subjects} subjects, " +
                $"{result.CurriculumGrades} grades, {result.CurriculumGradeSubjects} grade-subjects, " +
                $"{result.Topics:N0} new topics, {result.TopicPlans:N0} new plans");
        }

        return result;
    }

    /// <summary>
    /// `curriculumtopicplan.termid` is a NOT NULL FK into `terms`, and `terms` hangs off an academic
    /// year - so the term is resolved through the school's own years rather than assumed. The
    /// fallback exists because the plan's term is not what the report measures; it only has to
    /// resolve.
    /// </summary>
    private static async Task<long> ResolveTermIdAsync(NpgsqlConnection conn, long tenantId, long schoolId)
    {
        var id = await conn.ExecuteScalarAsync<long?>(
            @"SELECT t.id FROM terms t
                JOIN academicyear ay ON ay.id = t.academicyearid
               WHERE ay.tenantid = @tenantId AND ay.schoolid = @schoolId
               ORDER BY t.id LIMIT 1",
            new { tenantId, schoolId });

        if (id is null || id == 0)
        {
            id = await conn.ExecuteScalarAsync<long?>("SELECT MIN(id) FROM terms");
        }

        if (id is null || id == 0)
        {
            throw new InvalidOperationException(
                "no term exists, and curriculumtopicplan.termid is a NOT NULL foreign key into it.");
        }

        return id.Value;
    }

    private sealed class VersionRow
    {
        public long VersionId { get; set; }
        public long CurriculumId { get; set; }
    }
}
