# Database performance report

Measures how the application's **real** queries behave as the data grows, and tells you
which ones are too slow, why, and which index would fix them.

It answers a different question from `../data-volume/`:

| | Question |
|---|---|
| `../data-volume/` | *Does a query's cost scale with row count?* — seeds 1k → 200k rows and asserts a threshold. |
| **`db-report/` (this)** | *Is the query slow RIGHT NOW, on the data I actually have, and what is the fix?* — measures, explains, and proves the index. |

## Why this is a separate project from `../data-volume/`

Both live in one solution (`../SchoolPerformance.slnx`) so the IDE shows them together. They are
separate projects because they have opposite contracts:

* **Different inputs.** `data-volume` *creates* its data (it seeds 1k → 200k students and then
  asserts). This tool *reads the data you already have*. Merging them would put a read-only
diagnostic behind a project that writes hundreds of thousands of rows.
* **Different safety contract.** This tool is safe against a database you care about — every
  statement is a `SELECT`, and the index probe is rolled back. `data-volume` mutates (its
  `BaseSeeder` even attempts deletes). Those should not share an entry point.
* **Different output and exit code.** A report wants `dotnet run` and an exit code of *how many
  queries failed* (a CI gate). `data-volume` is an xunit test project (`IsTestProject=true`), whose
  entry point and output plumbing belong to the test host — reading a report through
  `ITestOutputHelper` and `dotnet test --logger` is a test that prints, not a report.

They share `../QueryShapes/QueryCatalog.cs` — the query shapes both of them measure, which is why
there is one catalogue rather than two copies that drift. They also share
`../data-volume/appsettings.json`, which this project **links** rather than copies, so there is one
place holding the perf-database password.

`SchoolPerformance.slnx` is the only solution — build either project on its own by pointing at its
`.csproj` (`dotnet test school-performance/data-volume/SchoolDataVolume.csproj`).

## Running it

```bash
# both performance projects are in one solution:
dotnet build school-performance/SchoolPerformance.slnx

# then run the report (default scope = the MEDIAN campus by student count)
dotnet run --project school-performance/db-report
dotnet run --project school-performance/db-report -- --advise
dotnet run --project school-performance/db-report -- --advise --busiest
dotnet run --project school-performance/db-report -- --advise --write-migrations
dotnet run --project school-performance/db-report -- --only attendance --runs 10
```

| Option | Meaning |
|---|---|
| `--advise` | For each **failed** query, create its suggested index inside a **rolled-back transaction**, re-measure, then undo. |
| `--busiest` | Measure the campus holding the most rows (worst case) instead of the default median campus. On the busiest campus a scope-column index matches ~every row, so it measures ~1.0x however good it is — see below. |
| `--write-migrations` | Write each **proven** index out as a migration under `school-db/migrations`, with the measurement that justified it in the header (implies `--advise`). Nothing is applied to a database — it only writes the file. See “From measurement to migration”. |
| `--migrations-dir <path>` | Write the migration there instead of the default `school-db/migrations`. |
| `--runs <n>` | Timed iterations per query (default 5). One extra warm-up run is always discarded first. |
| `--only <term>` | Measure only queries whose key or title contains the term. |
| `--timeout <ms>` | Statement timeout (default 120000). |
| `--exit-zero` | Always exit 0. |
| `-h`, `--help` | Usage. |

**Exit code = the number of failing queries**, so this can be a CI gate.

Connection string: `SCUBE_PERF_DB`, otherwise `ConnectionStrings:PerfTestDatabase` from
`../data-volume/appsettings.json` (linked, not copied, so the password lives in one place).
The password is always redacted in the output.

## It cannot change your database

* Every measurement is a `SELECT`.
* The `--advise` probe wraps `CREATE INDEX` **and** its `ANALYZE` in a transaction that is
  always rolled back. PostgreSQL DDL is transactional, so the index exists for the planner
  during the probe and is gone afterwards — **even if this process is killed.** A killed
  process closes its connection, which rolls the transaction back.

This is what makes it safe to point at a database you care about.

## From measurement to migration

A `FAIL` whose probe then comes back inside its budget is **PROVEN**, and the tool can write it out
as a migration rather than leaving you to copy a DDL string out of the report:

```bash
dotnet run --project school-performance/db-report -- --advise --write-migrations
```

That writes one file per run — `school-db/migrations/V<n>__Perf_Index_Advice.sql` — carrying the
measurement that justified it: the scope, how much of the table it held, the before/after p95 and
the plan's heaviest node. The evidence travels with the DDL instead of living in a chat log.

Four things it deliberately does **not** do:

* **It never writes an unproven index.** Only a probe that came back inside the budget is evidence;
  a suggestion that did not clear its budget is reported and left alone.
* **It never duplicates one.** An index that any existing migration already declares is skipped,
  and index names are unique per *schema*, so a name match is exact evidence the index is already
  shipped. Run it twice and the second run writes nothing.
* **It never proposes two indexes where one covers the other.** An index that is a leading prefix
  of another on the same table is dropped, because PostgreSQL serves `(a, b)` from `(a, b, c)`.
* **It never touches a database.** It writes a file; applying it stays a deliberate step.

It is flag-gated rather than always-on because a measurement tool that silently adds DDL to the
product's migration path is a surprise in someone else's working tree. Run `--advise` **without**
it and the report still tells you which proven indexes are not yet recorded, and how to record them.

⚠️ The probe and the migration use **different names**, on purpose. The probe creates
`zz_perf_probe_<table>_<columns>`: it really does create an index (inside the rolled-back
transaction), and if one ever leaked — a killed session, a dropped connection — a disposable name
makes it greppable and obviously safe to drop. The migration creates the deployed `IndexName`, which
is what the dedupe check looks for.

## Reading the output

| Verdict | Meaning |
|---|---|
| `OK` | p95 is inside the budget. |
| `FAIL` | p95 is over the budget **and the samples agree with each other**. The detail block names the heaviest node and a candidate index. |
| `SKIP` | The table holds too few rows here for the timing to mean anything — the number is real, the *conclusion* is not. |
| `UNSTABLE` | The samples disagree by **3x or more** and the slow end misses the budget. Not a pass and not a FAIL: the tool **cannot decide**. Counted in the exit code. |
| `ERROR` | The query did not run (schema drift). The message is printed and the rest of the run continues. |

⚠️ **`p95` NEEDS 21+ SAMPLES TO BE A PERCENTILE AT ALL.** `Stats` computes
`sorted[ceil(0.95 * (L-1))]`, so for any run count below 21 it is literally the **max** — and the
documented headline command uses `--runs 3`. That is why a bimodal sample set gets its own verdict
above rather than being collapsed into a FAIL: with few runs, one outlier *is* the verdict. Raise
`--runs` before drawing a conclusion from a number that moved.

✅ **A `UNSTABLE` row used to be mis-detected, and the fix matters.** `IsUnstable` required
`Min >= 50 ms`, so a query that ran at **45 ms five times and 160 ms five times** was reported as a
plain FAIL — the shape the detector exists for, excluded because its *fast* branch was 5 ms under the
floor. The floor now gates the **slow** end, which is what "1 ms vs 4.5 ms is noise" was always
saying. Gating on the MIN also made the flag depend on how lucky the fast branch was, so the same
distribution flipped in and out of detection as the machine warmed.

⚠️ **THE DETAIL BLOCK NO LONGER ASSERTS A CAUSE IT HAS NOT VERIFIED.** It used to print *"the planner
is choosing different plans"*, and on `rpt-student-attendance` that claim is **false**: `auto_explain`
at `log_min_duration = 0` captured an 85 ms and a 187 ms execution of the same statement with
**byte-identical plans** (same costs, same rows, same join order) and every node proportionally
slower. Timings alone cannot tell a changed plan from a changed machine, so it now says so and names
the check (`auto_explain.log_min_duration = 0`, diff a fast and a slow sample) instead of naming a
culprit.

Columns: `min`/`p50`/`p95`/`max` are wall-clock around the whole round trip; **`p95 (s)`** is the
same p95 in **seconds**, which is the unit a duration is normally quoted in; **`plan exec`** is the
planner's own `Execution Time` from `EXPLAIN (ANALYZE)`, which is the number to trust when
comparing two versions of a query.

Every duration is printed in **ms and seconds** wherever the number matters (the detail blocks and
the index-impact comparison), so a reading of `421 ms` and `0.421 s` can never be confused for two
different measurements. Budgets stay in ms, because that is what `QuerySpec.P95BudgetMs` declares.

## The planned query shapes

Each spec cites the repository method it came from, so it can be re-verified after a
refactor. The catalogue is `QueryCatalog.cs` — the one file to edit.

⚠️ **Keep them faithful.** This tool measures what the *application* sends, including its
**parameterized** statements. Rewriting a spec into a literal-valued query would measure the
optimistic case only: PostgreSQL may pick a generic plan for parameters and a better custom
plan for literals, which we measured as a 0.3s vs 2.1s difference on the same count.

### Adding a query

```csharp
new QuerySpec
{
    Key    = "my-screen-list-page",
    Title  = "My screen - first page",
    Source = "MyRepository.GetAll (line 42)",
    P95BudgetMs = 200,
    VolumeSql   = "SELECT COUNT(*) FROM mytable WHERE tenantid = @tenantId",
    IndexTable  = "mytable",
    IndexName    = "idx_mytable_scope",     // the name a migration would DEPLOY
    IndexColumns = "tenantid, campusid",    // exactly as measured - a DESC matters
    IndexRationale = "the WHERE filters on exactly those columns.",
    Sql = "SELECT * FROM mytable WHERE TenantId = @tenantId ORDER BY CreatedOn DESC LIMIT 50",
    Params = new { v.TenantId },
},
```

`IndexName`/`IndexColumns` are the **deployed** index; the probe derives its own throwaway name from
them. Do not put a `zz_perf_probe_` name here — that prefix is a leak marker, not a name to ship.

### ⚠️ Scope columns: a spec and a probe follow OPPOSITE rules

Every statement in this project filters on a scope, and it is easy to "tidy" one into disagreeing
with the thing it is meant to measure. There are two kinds, and unifying them breaks one:

| kind | rule | why |
|---|---|---|
| a spec's `Sql` **and its `VolumeSql`** | reproduce the application EXACTLY, including columns it forgets | a spec that filters *more* than the app does stops measuring the app. `AttendanceRepository.GetAll(page, tenantId, campusId)` passes `c.TenantId == tenantId && c.CampusId == campusId` — **no `SchoolId`** — so the attendance specs filter two columns, and must keep doing that. |
| a **probe** query (resolving the scope, computing the scope's share) | always the full `tenant + school + campus` triple | a probe describes *this scope*. If it counts a different set than the specs filter on, the "scope size" percentage the whole index argument rests on is describing something else. |

The probes in `Program.cs` used to filter `tenant + campus` only. That reads as harmless — `campusid`
is a unique PK and no campus spans two schools — and on `ayra_perf` every row carries `schoolid = 1`,
so tightening it changed **no number**. It was still wrong: a row stored with `schoolid = 0` (which
this schema does contain — several write paths store 0 rather than NULL) is counted by one rule and
not the other. Verified as a no-op: scope, share and all 15 verdicts were identical before and after.

`VolumeSql` also has to match its own spec, and that one bites in the **dangerous** direction: a
volume probe LOOSER than the query passes the `MinVolume` gate for a scope the query answers nothing
for, and the timing then gets judged anyway. Three of them counted the campus without the school
while their specs filtered both.

### Measuring a REPORT (`vw_*`) — the scope is a SESSION SETTING, not a parameter

The generic reporting engine's 17 seeded definitions run over 14 `vw_*` views, and **every one of
them carries a `ReportScope`** in the catalogue. Those views do not take their scope from bind
parameters: they read `app.tenant_id` / `app.school_id` / `app.campus_id` out of the session, which
`ReportQueryExecutor.SetScopeAsync` sets. A view spec therefore declares
`ReportScope = new ReportScope(tenantId, schoolId, campusId)` and the runner issues the same
`set_config` calls before measuring (`BenchmarkRunner.SetReportScopeAsync`).

⚠️ **Without it a view fails closed and answers zero rows**, because it reads the settings with
`current_setting(name, missing_ok => true)`. The tool would then report a 200 ms report as a 0.4 ms
one — "the report is fine" for a report that returns nothing. The setting NAMES are a contract with
`ReportQueryExecutor`; a rename there must be a rename here.

⚠️ **AND IT ALSO SETS `SET enable_mergejoin = off`, BECAUSE THE ENGINE DOES.** This is not cosmetic and
must not be dropped when the scope code is touched: against these views the `current_setting()`
predicate makes PostgreSQL pick a merge join whose **cost model assumes it can stop early**, so the
plan is costed at ~2,900 while the index scan beneath it costs 1,562,000. The join then walks all 14.28M
entries and the parent aggregate consumes every one. Measured on
`COUNT(*) FROM vw_student_attendance`: **85,517 ms** with merge joins allowed, **441 ms** without. A
harness that measured the default planner strategy would be measuring a query the application never
issues.

⚠️ **AND `ANALYZE` THE TABLES FIRST.** The bulk seeder did not, and the resulting stale statistics
made this view look *healthy* at 202 ms — running `ANALYZE` turned the same query into 20-85 s. A
performance reading taken against un-analyzed statistics is not a reading.

Two more things a view spec must get right, and neither is obvious:

* **Reproduce the definition's OWN count mode — do not assume `window`.** The column DEFAULT is
  `'window'` (13 of the 17 seeded rows), so the engine emits `COUNT(*) OVER() AS __total_rows` — and
  that aggregate is computed over the whole partition before the first row is returned, which means
  the `LIMIT` cannot short-circuit and page 1 reads the entire filtered set. Dropping the term from a
  spec would measure a query the engine never sends. (Measured: 0.4 ms without it vs 199 ms with it,
  on `vw_outstanding_fees`.) ⚠️ **But FOUR rows are `countmode='separate'`** — `V133` flipped
  `STUDENT_ACADEMIC_RISK` + `FEE_COLLECTION`, `V135` flipped `ASSESSMENT_PERFORMANCE` +
  `LEARNING_OUTCOME_PERFORMANCE` — and for those the engine sends the page query WITHOUT the window
  term and a SECOND round trip (`ReportQueryExecutor.ExecuteCountAsync`). Each carries TWO specs in
  `QueryCatalog` (`rpt-<code>` and `rpt-<code>-count`, the latter `scalar: true`) for exactly this
  reason: a spec has to reproduce what the app RUNS.
* **Gate the volume on the DRIVING TABLE, not on the view's output rows.** A view whose scope never
  reaches its fact table scans that table and can still return **zero** rows —
  `vw_student_attendance` did exactly that (190 ms, `rows 0`) while `attendance.studentenrollmentid` was
  unpopulated. A `MinVolume` gate on output rows would SKIP the very query whose cost is the finding, so
  the `rpt-*` specs count the driving table for the scope instead. That is the literal reading of
  `VolumeSql` ("rows this query would have to look at").

Run them with `--only rpt-`. The findings, the fixes and the measured numbers are in `../README.md`.

⚠️ **`--advise` PROBES NOTHING FOR AN `rpt-*` SPEC — AND THAT IS NOT EVIDENCE AN INDEX WOULD NOT
HELP.** A report spec declares no `IndexTable`/`IndexName`, so `QueryCatalog.IndexFor` returns null and
the probe is skipped entirely: the `suggested index: (none …)` line is printed because no candidate
was DECLARED, never because one was measured and rejected. Read it as "nothing proposed here", not
"nothing to propose".

⚠️ **AND FOR A VIEW THE COST SOMETIMES IS AN INDEX AFTER ALL — measured September 2026.**
`vw_learning_outcome_performance`'s `LEFT JOIN LATERAL` joins `curriculumtopicplan` on
`curriculumtopicid`, and that table carried **only its primary key** — so the LATERAL seq-scanned it
**once per topic**: 1,051 loops, `Rows Removed by Filter: 2098` on each, **32,581 buffers**. A
rolled-back probe index on that one column took the report from **34,734 buffers / 134 ms to 3,146
buffers / 4.2 ms**. So when a report's plan shows a `loops=` in the hundreds or thousands over a seq
scan, check the JOINED table's indexes before accepting "the cost is the view".

The earlier finding this note rests on still stands **for the view it was taken on**: on
`vw_student_attendance` a composite `(tenantid, schoolid, campusid)` index, a covering
`(campusid) INCLUDE (…)` index and extended statistics over the scope triple were each measured and
each left the query at 27-38 s, because the row estimate there was already accurate (11,438 vs 10,000
actual) — the COST MODEL is what chooses that plan.

### Giving another module coverage

The catalogue covering a module needs TWO things, and the second is the one that gets skipped:

1. **Specs** — the module's volume-sensitive grids, transcribed from the repository SQL, with a
   budget and a `Source`.
2. **Volume.** When this tool was first pointed at `ayra_perf` it held rows in **5 tables of 246**
   (student, attendance, parent, classroom, campus) — and it now holds **89** (recount with
   `pg_stat_user_tables`, never from prose). A spec for any other module reports
   `SKIP (this scope holds too few rows)` — a catalogue that looks like module coverage and measures
   nothing.

`data-volume/Seeders/FeesModuleSeeder.cs` is the worked example: one academic year, term, fee type,
fee structure, then an enrolment per student, N invoices, their lines and a payment each.
`PerfDatasetSeeder` calls it for every campus — **including the campuses it skipped**, because a
campus seeded before the fee seeder existed still has no fee volume.

⚠️ **Check the seeding cost before choosing a module** — it varies far more than the spec work does.
Count the NOT NULL columns with no default:

```sql
SELECT string_agg(column_name, ', ' ORDER BY ordinal_position)
  FROM information_schema.columns
 WHERE table_schema = 'public' AND table_name = '<table>'
   AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO';
```

Measured: `journalentryline` **2**, `libraryissue` **3**, `invmovement` **6**, `studentenrollment`
**11**, `invoices` **18**, `payment` **19**. A table whose required set is small *and* which does not
hang off a student is the cheapest module to make measurable.

## Which campus is measured, and why it decides the advice

The report measures **one scope at a time**, and that choice is what makes the index advice
meaningful:

* **default — the MEDIAN campus** by student count. The question a real deployment asks: a
  typical campus, inside a table this large.
* **`--busiest`** — the campus holding the most rows (worst case).

Both are useful, and they answer different questions. On the busiest campus a scope-column
index matches ~every row and so measures ~1.0x *however good it really is*; that is why the
median is the default and the busiest is opt-in.

The report states the choice and its size, so a reading is never ambiguous:

```
scope     : tenant 1 / school 1 / campus 15 (a TYPICAL campus - the median by student count)
scope size: this campus holds 0.2% of students (2,000/878,714), 0.2% of attendance (10,000/4,549,500)
```

**`scope size` is the number that decides whether a scope index can be selective.** 100% means
it cannot (and a `FAIL` there is not evidence against the index); 0.2% means it is exactly what
the index is for.

### The dataset has to be multi-campus for any of this to work (measured)

`ayra_perf` was originally seeded **entirely into campus 1** — 842,614 students and every
attendance row. Against that shape an index on `(tenantid, campusid)` matches the whole table,
so the tool reported *"this index will not help"* for an index that helps enormously in a real
deployment.

`../data-volume/`'s `PerfDatasetSeeder` now builds the shape a real deployment has — many
campuses each holding a realistic student count, on top of the existing volume:

```bash
SCUBE_PERF_DATASET=1 SCUBE_PERF_CAMPUSES=20 SCUBE_PERF_STUDENTS_PER_CAMPUS=2000 \
  dotnet test school-performance/data-volume/SchoolDataVolume.csproj \
  --filter "FullyQualifiedName~PerfDataset" --logger "console;verbosity=detailed"
```

It is idempotent (a campus that already holds students is skipped unless `SCUBE_PERF_FORCE=1`)
and it runs `ANALYZE student / attendance / parent` at the end — without that the planner
reasons from pre-seed statistics and the next report is nonsense (measured: after adding 36,000
rows without `ANALYZE`, the student grid planned a parallel sort over the **whole** table).
`PerfDatasetTests` asserts the property the advice depends on: many campuses hold students, each
holds attendance, and **the smallest campus holds under 10% of all students**.

## ⚠️ What this database can and cannot tell you

* A query whose scope filter **matches ~100% of the rows** cannot be helped by an index on those
  scope columns — the planner is right to prefer a sequential scan. On a single-campus dataset
  that is every query, which is why `--busiest` is not the default.
* A query that benefits from an index because of its **`ORDER BY` + `LIMIT`** (a top-N read) is
  measured correctly either way, because that win does not depend on selectivity.
* A **`COUNT(*)` over a whole filtered set** genuinely costs what it costs at volume — but that
  is not the same as "no index can help". Measured on a 2,000-student campus inside 878,714
  rows, `student-count` is 373-389 ms and the suggested scope index takes it to **31.9-39.6 ms
  (9.4-12.2x)**, now shipped as `school-db/migrations/V130__Student_Scope_Index.sql`.
* **Check the indexes that already exist before trusting a suggestion, and never infer one
  database's indexes from another's.** The attendance grid runs in 3-6 ms with **no new index at
  all**, because `idx_attendance_campusid` is already in the shipped schema
  (`school-db/migrations/V000__Full_Schema_AyraDB.sql`). It is present on `ayra_perf` and
  `ayra_e2e` but **not** on the dev `AyraDB` — the three databases do not hold the same indexes
  (895 vs 859, 36 differing), so a suggestion must be checked against the database being
  measured.
