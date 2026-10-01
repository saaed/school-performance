# SCube Performance Testing

Performance testing tools for the SCube School ERP system.

## Start here

```bash
# Measure the real queries against the data you already have, and prove the fix.
dotnet run --project school-performance/db-report -- --advise
```

That is the only tool here that runs today without installing anything. It is safe on a
database you care about: everything it does is a `SELECT`, and the index it tries is created
inside a **rolled-back transaction**. See `db-report/README.md`.

### ⚠️ Two known blockers in the rest of this folder

1. **`load-testing/` cannot run at all: k6 is not installed**, and the binary the paths below
   reference (`D:\SCube\k6-v0.50.0-windows-amd64\k6.exe`) does not exist. The scenarios also
   have defects that would make them lie or overload the server — see the notes in that
   folder before trusting a run.
2. **`pg_stat_statements` is installed as an extension but is NOT loaded**, because
   `shared_preload_libraries` is empty. Querying its view fails with *"pg_stat_statements
   must be loaded via shared_preload_libraries"*, so `Queries/QueryProfiler.FindSlowQueriesAsync`
   can never return rows. Enabling it needs a `postgresql.conf` edit **and a PostgreSQL restart**.
   The `db-report` tool deliberately does not depend on it — it reads plans from
   `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` instead.

## Structure

Both projects live in **one solution** so the IDE shows them together:

```bash
dotnet build school-performance/SchoolPerformance.slnx
```

```
school-performance/
├── SchoolPerformance.slnx     # QueryShapes + db-report + data-volume, together
├── QueryShapes/               # the ONE source of truth for the app's query shapes
│   ├── SchoolPerformance.QueryShapes.csproj
│   └── QueryCatalog.cs        # each spec cites the repository method it came from
├── db-report/                 # measure the real queries + prove the index  <-- start here
│   ├── SchoolPerformance.DbReport.csproj
│   ├── Benchmark.cs           # timing, EXPLAIN parsing, rolled-back index probe
│   ├── Program.cs             # CLI + report
│   └── README.md
├── data-volume/               # does a query's cost scale with row count?
│   ├── SchoolDataVolume.csproj
│   ├── appsettings.json       # the ONE place the perf password lives (db-report links it)
│   ├── Queries/
│   ├── Seeders/               # incl. PerfDatasetSeeder - the multi-campus fixture
│   └── Tests/
├── load-testing/              # k6 scripts for API load testing (k6 NOT installed)
│   ├── scenarios/
│   ├── config/
│   └── utils/
└── README.md
```

### Why `db-report` and `data-volume` are not one project

They answer different questions and have **opposite contracts**, so sharing an entry point would
be wrong: `data-volume` *creates* data by seeding it, while `db-report` is read-only and safe to
point at a database you care about. `data-volume` is also an xunit test project, whose entry point
and output plumbing belong to the test host — a report you read in a terminal wants `dotnet run`
and an exit code. Full reasoning: `db-report/README.md`.

What they **do** share is `QueryShapes/QueryCatalog.cs` — the query shapes they both measure.
⚠️ Edit the shapes there and nowhere else. They used to hold private copies, and the copies drifted
from the repositories until the measurements were of queries the application never sends.

## Prerequisites

### Data Volume Testing (.NET)
- .NET 8 SDK
- PostgreSQL database

### Load Testing (k6)
- k6 installed (https://k6.io/docs/getting-started/installation/)
- Running API server

## Setup

### 1. Create Performance Test Database

```sql
CREATE DATABASE perf_test;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
```

### 2. Configure Connection String

Edit `data-volume/appsettings.json` with your PostgreSQL credentials.

### 3. Apply Database Schema

```bash
pg_dump -s your_current_database > schema.sql
psql -d perf_test -f schema.sql
```

## Running Tests

### Data Volume Testing

⚠️ **Read this before running it.** Each test method calls `SeedAsync(<count>)`, which
**appends** to the same campus — so running the suite seeds 1k + 10k + 50k + 200k students
on top of whatever is already there (that is why `ayra_perf` now holds 841,614 of them).
Its pass threshold is a loose `< 3000 ms`, so it passes while telling you very little about
which query is the problem. Prefer `db-report/` for a diagnosis, and this project to prove
that a query's cost grows with volume.

```bash
cd data-volume
dotnet test
dotnet test --filter "StudentList_ByCampus_ScalesWithVolume"
dotnet test --logger "console;verbosity=detailed"
```

#### The multi-campus fixture (run this before trusting `db-report`'s index advice)

Every seeder above fills **one** campus, which is why `ayra_perf` reached 842,614 students all in
campus 1. Against that shape an index on the scope columns matches the whole table and looks
useless. `PerfDatasetSeeder` builds the shape a real deployment has — many campuses, each holding
a realistic student count — on top of the existing volume:

```bash
SCUBE_PERF_DATASET=1 SCUBE_PERF_CAMPUSES=20 SCUBE_PERF_STUDENTS_PER_CAMPUS=2000 \
  dotnet test school-performance/data-volume/SchoolDataVolume.csproj \
  --filter "FullyQualifiedName~PerfDataset" --logger "console;verbosity=detailed"
```

It is opt-in (`SCUBE_PERF_DATASET=1`), idempotent (an already-populated campus is skipped unless
`SCUBE_PERF_FORCE=1`), and it `ANALYZE`s the seeded tables at the end, because a bulk load without
it leaves the planner reasoning from pre-seed statistics.

### Load Testing

```bash
cd load-testing
k6 run scenarios/auth.js
k6 run --vus 100 --duration 5m scenarios/auth.js
k6 run -e BASE_URL=http://localhost:8086 -e API_URL=http://localhost:8087 scenarios/auth.js
```

## Test Scenarios

### Data Volume Tests

| Test | Data Volume | Threshold |
|------|-------------|-----------|
| StudentList | 1K-200K students | 500ms |
| StudentSearch | 10K-200K students | 1000ms |
| AttendanceList | 10K-10M records | 500ms |
| AttendanceSummary | 100K-10M records | 5000ms |

### Load Tests

| Scenario | VUs | Duration |
|----------|-----|----------|
| auth.js | 50-200 | 15min |
| enrollment.js | 20-50 | 13min |
| attendance.js | 30-100 | 13min |
| fees.js | 20-50 | 13min |

## Measured index findings

From `db-report --advise`, against `ayra_perf` (878,714 students, 4,549,500 attendance rows)
with the scope set to the **median campus** — 2,000 students, i.e. 0.2% of the table.

| Query | Today | With the index | Finding |
|---|---|---|---|
| `student-count` (the grid's total) | 373-389 ms | **31.9-39.6 ms — 9.4-12.2x** | `student` carries **no scope index** (only `tenantid`, `isactive`, `name`, `admissionnumber`), while every sibling scope table carries one. Shipped as **`V130__Student_Scope_Index.sql`**. |
| `attendance-list-page` / `-count` / `-classroom-date` | 3-6 ms | — | **No new index needed.** `idx_attendance_campusid` already ships in `V000__Full_Schema_AyraDB.sql`. |

### The migration is generated from the measurement, not hand-written

```bash
dotnet run --project school-performance/db-report -- --advise --write-migrations
```

A `FAIL` whose probe comes back inside its budget is **proven**, and that command writes it out as
`school-db/migrations/V<n>__Perf_Index_Advice.sql`, with the measurement that justified it in the
file header — so the evidence travels with the DDL instead of living in a chat log or a commit
message. It skips any index an existing migration already declares (re-running writes nothing),
drops an index that is a leading prefix of another on the same table (PostgreSQL serves `(a, b)`
from `(a, b, c)`, so shipping both is write cost for no read benefit), and never applies anything
to a database. Full rules and the four things it deliberately will not do: `db-report/README.md`.

⚠️ **Do not act on the recommendations this section used to carry.** `idx_attendance_student_date`
and `idx_invoice_scope_status` were guesses; neither was measured, and the attendance one is
unnecessary. A row is evidence only once `--advise` has measured it.

## The generic reporting engine — the module the catalogue did not cover (Sept 2026)

The 17 seeded `reportdefinition` rows run over **14 `vw_*` views**, and until they were transcribed
into `QueryCatalog` the tool measured **none** of them: it reported a healthy database while two of
those reports took ~200 ms to return a single page on a 2,000-student campus. They are now specs
keyed `rpt-*` (run them with `--only rpt-`), and the three findings below are what they measured.

Three defects, in the order they were proved. **None of them is an index problem** — that is why
those rows print `suggested index: (none - this query is slow for a reason an index will not fix)`
and why `--advise` correctly probes nothing.

| # | Defect | Where | Status |
|---|---|---|---|
| 1 | `countmode='window'` defeats paging | every `reportdefinition` | **PARTIALLY CLOSED** — `V133` + `V135` flip the **4** reports that measured faster under `separate`; for the rest the count mode is not the cost |
| 2 | the scope `current_setting()` predicate makes the planner pick an early-exiting MERGE JOIN | `vw_student_attendance` and the other view reads | **FIXED** — engine sets `enable_mergejoin = off`; **85,517 ms → 441 ms (194x)** |
| 3 | a view with no scope predicate at all | `vw_assessment_performance` | **FIXED** — `V131` adds it |

### 1. `COUNT(*) OVER()` is not free (a `countmode` decision, not an index — now measured per report)

The engine's default count mode is `window`, so `ReportQueryBuilder.Build` emits
`SELECT <cols>, COUNT(*) OVER() AS __total_rows … LIMIT 50 OFFSET 0`. A window aggregate is computed
over the **whole partition before the first row can be returned**, so the `LIMIT` cannot
short-circuit: page 1 materialises the entire filtered set. Read out of the real plans:

```
WITH window count : Limit -> WindowAgg -> Nested Loop (rows=8000) -> Parallel Seq Scan on invoices (48,133 x3)
WITHOUT           : Limit -> Nested Loop (rows=50) -> Index Scan student -> Index Scan invoices   (stops at 50)
```

The second plan reads **50 rows**; the first reads **144,399**. This is not a "the count is a bit
slow" issue — it is the difference between paging and not paging, and it applies to **every** seeded
definition. The engine also supports `countmode='separate'` (a second `COUNT(*)` query), which lets
the page early-exit — and that lever has now been **measured per report and applied selectively
(`V133`, then `V135`)**, see the re-measured section below. It is NOT a blanket fix: for the
attendance/absentee reports the separate count costs about what the whole window query did, so only
the FOUR reports where dropping the window term makes the page nearly free were flipped.

### 2. ⚠️ THE ACTUAL CAUSE IS A PLANNER COST-MODEL PATHOLOGY, AND NOTHING BUT A PLANNER FLAG FIXES IT

⚠️ **READ THE TABLE BELOW AS HISTORY — every row in it was measured with the merge-join pathology
ACTIVE** (its own baseline row is 20,000-85,517 ms). It was taken as evidence against reshaping the
view, and *for that state it was right*. Once `enable_mergejoin = off` landed the reshape became a
WIN — `V134` is that change, and it took `rpt-student-absentee` from 302 ms to 44 ms. **A verdict
recorded against a broken plan is a verdict about the plan, not about the shape.**

| attempt | `COUNT(*) FROM vw_student_attendance` |
|---|---|
| as shipped | 202 ms *(STALE stats — a fluke, see below)* |
| as shipped, statistics freshly `ANALYZE`d | **20,000 - 85,517 ms** |
| scope pushed onto `attendance` | 27,900 ms |
| + composite `(tenantid, schoolid, campusid)` index | 29,000 - 36,000 ms (five consecutive runs) |
| + covering `(campusid) INCLUDE (studentid, …)` index | 27,665 ms |
| + extended statistics over the scope triple | 35,400 - 37,800 ms |
| with LITERAL scope values in place of `current_setting()` | 6,199 ms *(control — still bad)* |
| **engine sets `enable_mergejoin = off`** | **441 ms** |

⚠️ **The stale-statistics row is the trap that hid this.** The bulk seeder never ran `ANALYZE`, so the
planner had no MCV list for `campusid` and costed the index generously — the view looked FINE at 202 ms.
Running `ANALYZE` (which production does, and which any maintenance window does) made it 20-85 s.
**A performance reading taken against un-analyzed statistics is not a reading.**

**What is actually happening.** With the scope predicate present, PostgreSQL estimates the post-filter
row count very low and its **merge-join cost model assumes the join can therefore STOP EARLY** — so the
plan is costed at ~2,900 while the index scan beneath it is costed at 1,562,000. The parent aggregate
needs the whole set, so the join walks all 14.28M index entries in `studentenrollmentid` order, and the
`Filter: (tenantid = … AND schoolid = … AND campusid = …)` rejects all but ~10,000 of them. The tell is
in the plan itself:

```
Merge Join  (cost=819.02..2861.02 rows=9)          <- costed as if it stops after 9 rows
  -> Index Scan using ix_attendance_studentenrollment  (cost=0.43..1562233.94 rows=11438)
        Filter: (tenantid = … AND schoolid = … AND campusid = …)
        Rows Removed by Filter: 14276500
```

⚠️ **The row estimate is ACCURATE (11,438 vs 10,000 actual).** That is why every index and statistic
above failed: they improve an estimate that was never the problem. The COST MODEL is what picks the
plan, and only a planner directive moves it. Measured across every view with data, the directive is
uniformly equal-or-better — no regression anywhere:

| view | default planner | `enable_mergejoin = off` |
|---|---|---|
| `vw_student_attendance` | **85,517 ms** | **441 ms** |
| `vw_student_subject_results` | 109 ms | 3 ms |
| `vw_fee_collection` | 130 ms | 7 ms |
| `vw_outstanding_fees` | 615 ms | 418 ms |
| `vw_student_academic_risk` | 125 ms | 123 ms |
| `vw_student_demographics` | 44 ms | 30 ms |

The fix lives in `ReportQueryExecutor.SetScopeAsync` (a `SET LOCAL`, alongside the `set_config` calls
it already made), so it is scoped to the report's own transaction and cannot leak into anything else
on the pooled connection. This tool mirrors it in `Benchmark.SetReportScopeAsync` — **a harness that
measured the default strategy would be measuring a query the application never issues.**

### 2b. The attendance view, reshaped — `V134` (and the second `ANALYZE` trap)

With the planner flag in place the reshape became worthwhile, so `vw_student_attendance`'s scope
predicate moved from the JOINED `student` onto `attendance` itself. **The premise, verified in
`ayra_perf`, `ayra_e2e` and `AyraDB`: 0 attendance rows carry a scope that differs from their
student's** — which is why the two shapes are equivalent on real data (the view still EXPOSES
`st.tenantid/schoolid/campusid`; only the WHERE moved).

| report | scope on `student` (shipped) | scope on `attendance` (`V134`) |
|---|---|---|
| `rpt-student-absentee` | 302 ms **FAIL** | **44 ms OK** |
| `rpt-student-attendance` | 258 ms **FAIL** | 155 ms FAIL (fast branch 40-52 ms) |
| window query, buffer accesses | 120,870 | **40,875** |

The absentee report is the unambiguous win: with the predicate on the fact table its
`ispresent = false` filter prunes on `idx_attendance_campusid` instead of after the join.

⚠️ **`both` predicates is the obvious-looking compromise and it is WORSE — measured, not reasoned
about: 348-657 ms and 447,559 buffer accesses.** A redundant predicate is a second access path for
the planner to weigh, not a safety net. Do not add the `st.` half back "for safety".

⚠️ **THE SECOND `ANALYZE` TRAP.** The first readings of this reshape were taken against stale
statistics: the perf seeder analyzed `student`, `attendance`, `parent` and each module's tables, but
**`classroom` — which `PerfDatasetSeeder` itself inserts — was in no list**, so the planner nested-looped
`classroom_pkey` once per row (10,000 times) instead of hash-joining a 22-row table. Refreshing the
academic-structure tables changed the plan from **five per-row nested loops to two** and cut the
attendance report's buffer accesses 3x. `PerfDatasetSeeder` now analyzes
`classroom`/`academicgrade`/`curriculumgrade`/`academicyear`/`section` unconditionally.

#### `rpt-student-attendance` is a BOUNDARY case, and the reshape is not the remaining lever

It is the one spec `V134` improved (~40%) without clearing. What it actually costs:

* **Its server-side `plan exec` is 100 ms** (that column is `EXPLAIN (ANALYZE)`'s own `Execution Time`),
  and **49-65 ms in a warmed `psql` session** — 20 consecutive executions, flat, in the named-statement
  form, the unnamed Parse/Bind/Execute form, and under all three `plan_cache_mode` settings.
* **Its wall-clock samples straddle the 150 ms budget and swing 3x run to run** — `43, 49, 52, 46, 110,
  97, 145, 160, 161, 151` in one run; `150, 158, 166, 161, 157, 158, 162, 150` in another.

⚠️ **THE PLANS ARE IDENTICAL — this was measured, not assumed.** `auto_explain` at
`log_min_duration = 0` (armed for `ayra_perf` only, then reset) captured an 85 ms and a 187 ms
execution of the same statement with **byte-identical plans**: same costs, same `rows=`, same join
order, same buffers — and every node proportionally slower (the `attendance` scan 9.9 → 27.1 ms, the
`studentenrollment` seq scan 9.95 → 16.1 ms). So this is **not** a plan flip, and the tool no longer
prints that it is (its detail block used to say "the planner is choosing different plans", which the
evidence contradicts). A faster plan does not exist either: forcing `enable_nestloop = off` to hash
join instead of the 10,000 per-row `student_pkey` lookups costs **284 ms against 49 ms**.

⚠️ **The 66x row underestimate has a NAME, and it is a FIXTURE artifact.** The plan estimates 152 rows
where the truth is 10,000, and the arithmetic is exact:
`11417 x (1 - null_frac(attendance.studentenrollmentid)) = 11417 x 0.0133 = 152`. That is because
**98.7% of `attendance` rows really do carry a NULL `studentenrollmentid`** — `AttendanceSeeder` used
to write them that way for the whole 14.2M-row table, and only the campuses it backfilled have them
populated. So the global statistic is right and the per-campus selectivity it implies is wrong: on
this dataset a campus's attendance rows are 100% enrolled while the table says 1.3% of them are.
`ANALYZE` cannot fix that (the value is genuinely 0.987), and it is not a production shape — a real
school's attendance rows all have an enrolment. **Measure this report on a campus the seeder reached,
or the estimate is a statement about the fixture.**

⚠️ **`countmode='separate'` was re-measured against the RESHAPED view and still loses — do not flip
it.** The V133 table above records separate as WORSE for both attendance reports (401 / 385 vs
227 / 224), but that reading predates `V134` — and this file already carries the lesson that a perf
decision taken before a view/index change must be re-measured after it. Re-measured in the tool
(`--runs 8-10`, campus 15, both legs as their own specs):

| shape | page | count | TOTAL |
|---|---|---|---|
| `STUDENT_ATTENDANCE` window (shipped) | 161 ms | — (in the page) | **161** |
| `STUDENT_ATTENDANCE` separate | 28 ms | 122 ms | **150** |

The separate shape is a **wash at best** and doubles the view evaluation (the count re-runs the whole
view), so `V133`'s verdict stands after the reshape — **and `V135` does not touch it**: the two
reports that pass below all sit on views whose count leg is cheap (`vw_learning_outcome_performance`
6-7 ms, `vw_assessment_performance` 81-93 ms), while this one's count re-evaluates the 441 ms view.
Note the page leg without the window term IS
flat and fast (28 ms) — the window aggregate is not the page's problem; the count is exactly what the
window already pays for.

**How the tool reports this now, and why it is allowed to say FAIL.**

| run | samples (ms) | spread | verdict |
|---|---|---|---|
| bimodal | 43, 49, 52, 46, 110, 97, 145, 160, 161, 151 | 3.7x | **`UNSTABLE`** — the tool cannot decide |
| uniformly slow | 150, 158, 166, 161, 157, 158, 162, 150 | 1.1x | **`FAIL`** — correct: even the best sample missed |
| uniformly slow | 96, 128, 150, 148, 155, 159, 137, 121 | 1.7x | **`FAIL`** — same |

`UNSTABLE` is its own verdict now: samples that disagree by 3x+ **and** whose slow end misses the
budget. It is neither a pass nor a FAIL, it is counted separately in the headline, and it stays in
the exit code. That is the honest reading — `Stats` computes p95 as `sorted[ceil(0.95*(L-1))]`, which
for any run count below 21 **is the MAX**, so collapsing a bimodal sample set into a single `FAIL`
reports a machine-level hiccup as a query defect.

⚠️ **But a UNIFORMLY slow run is a true FAIL and the tool still says so.** When every sample lands in
the slow regime there is no disagreement to point at: the query really did take 150-166 ms eight
times, and calling that "undecided" would be the tool lying to make a number look better. So
`rpt-student-attendance`'s verdict genuinely switches between `UNSTABLE` and `FAIL` depending on
which regime the machine was in — which is the honest description of a query that costs 50-180 ms
with an identical plan. **Do not tune the verdict logic to force this row green.** See
`db-report/README.md` for the detector fix (the floor now gates the SLOW end, which is what
"1 ms vs 4.5 ms is noise" was always saying — it previously required the FAST end to be over 50 ms,
which excluded this exact distribution).

### 3. `vw_assessment_performance` had no scope predicate whatsoever — FIXED by `V131`

Its only `WHERE` was inside a LATERAL over `studentassessmentitem`. Measured: with a nonsense scope
(`999/999/999`) it returned the same rows as with none. It was unobservable **only because
`studentassessment` is empty in every database here** — an unscoped view with no data looks exactly
like a correctly scoped one, which is why the report's numbers never moved. This was an **isolation
defect, not a performance one**: the report exposes student names, admission numbers and marks, so it
was a cross-tenant leak waiting for the first school that ran an assessment. `V131` adds the predicate
on the fact table (`sa`), matching every sibling view. `SchoolE2E` J12 phase 10 now asserts the
observable it produces — the report answers its own scope's rows and NOTHING for a scope that holds
none.

### The perf FIXTURE hid these, and is now fixed too

Two seeder defects made a broken report look like an empty campus:

* **`AttendanceSeeder` never wrote `attendance.studentenrollmentid`.** The column is nullable, so
  PostgreSQL accepted **all 14,286,500 rows with it NULL** and nothing complained — while
  `vw_student_attendance` joins on exactly that column, so the attendance report returned **0 rows on
  every campus while still scanning the table**. The seeder now resolves each student's enrolment and
  emits rows only for students that have one (reporting the rest rather than mis-seeding them).
* **`FeesModuleSeeder` never wrote `paymentallocation`.** `vw_fee_collection` reaches its fact rows
  through `payment p JOIN paymentallocation pa ON pa.transactionid = p.id`, so the Fee Collection
  report returned 0 rows while 2,000 Completed payments sat on the campus (3 stray allocation rows
  existed in the whole database). The seeder now allocates each payment to the invoice it settled.

Both were backfilled on `ayra_perf` so the numbers in this file are measured against real data.
**A report that returns nothing still pays for whatever it scanned** — which is why the `rpt-*` specs
gate their volume on the driving *table* rather than on the view's output rows.

⚠️ One limitation remains and is honest: the dataset holds 879,514 students but only ~36,103
enrolments, so most attendance rows have no enrolment to point at. `rpt-student-attendance` therefore
measures the campuses the fee spine reached, not all 20 — a seeder `ANALYZE`/volume issue, not an
engine one.

### The sixth pass — the master-data seeder, and the FK index the data exposed (Sept 2026)

**What the pass set out to do.** Cover the remaining GRID queries: walk the controllers that carry a
scope grid, spec the ones whose driving table already holds rows, and SEED the ones whose table is
empty. It added **33 `ScopeGrid` specs** (the generic `GenericRepository.GetAllAsync(page, predicate)`
shape — see the block comment in `QueryCatalog.cs`) and one seeder, taking the catalogue from 74 to
**107 specs** and the SKIP count from 11 to **0**.

#### `MasterDataSeeder` — ten tables, because a SKIP over a shipped grid is not coverage

The eleven SKIPs were ten master-data tables that held ZERO rows in every database here, so their
generic grid specs reported SKIP *honestly and uselessly*: a grid the application ships, sitting
unmeasured. They are `section`, `room`, `holiday`, `discount`, `taxcode`, `approvaltemplate`,
`roles` (custom only), `campussubject`, `timetable` and `schoolevent`.

⚠️ **THIS IS A PLAUSIBILITY FIXTURE, NOT A PERFORMANCE ONE.** Every table here holds TENS of rows on a
real campus, and a few dozen rows will never be slow. What the specs then prove is that the grid is
REACHABLE and its query is well formed - which is why those specs carry `MinVolume = 1`. Do not pad the
counts to make a number look impressive; a school with 400 rooms is not a school.

Four constraints the seeder had to respect, all measured against `ayra_perf`:

* **`GENERATED ALWAYS AS IDENTITY` refuses a supplied id (`428C9`).** `section`, `room`, `holiday`,
  `discount`, `taxcode`, `roles`, `timetable` and `schoolevent` all have `is_identity = ALWAYS` - and
  `information_schema.column_default` is **NULL** for them, which reads like "no default, so supply
  one". It is not: a null default on an identity column is exactly how `ALWAYS` presents. The first
  version derived ids from `MAX(id)+1` and died on the first INSERT. Every id is now omitted.
* `ux_discount_tenantcode` / `ux_taxcode_tenantcode` are UNIQUE on **(tenantid, code)**, so codes are
  campus-stamped (`PERF-D1-15`); two campuses seeding one tenant must not collide.
* `ux_approvaltemplate_one_active_per_module` is a PARTIAL unique on
  `(tenantid, schoolid, campusid, lower(modulename)) WHERE isactive`, so the seeder writes the eight
  canonical modules and no more.
* **`terms` HAS NO SCOPE COLUMNS** - it hangs off `academicyear`, so a campus's terms are the terms of
  ITS years. `WHERE terms.campusid = @campusId` is a `42703`, and filtering it is how the first version
  of both the seeder AND its assertion failed. The event's `(year, term)` pair is taken from ONE term
  row, because a term belongs to exactly one year and pairing them by index writes a pair that does not
  exist.

#### `studentfinalresult` + `studentreportcard` — the last SKIP needed a downstream row

`student-report-card-page` was the ONE skip left after the master-data seed, and it is not master data:
`studentreportcard.studentfinalresultid` is NOT NULL, and `studentfinalresult` was **empty in every
database here** (no seeder had ever written it). Both are now derived from the subject results the
campus already holds (summed per enrollment), so the numbers are the campus's own. The two writes are
`NOT EXISTS`-guarded and run **even on the skip path**, because the campuses that carry subject results
are exactly the ones missing them - which is what lets a fixture run backfill without `SCUBE_PERF_FORCE=1`
and a full 6,000-assessment rebuild.

#### ⚠️ THE NEW DATA EXPOSED A MISSING FK INDEX — `studentfinalresult(studentenrollmentid)`

Populating that table turned `rpt-student-academic-risk-count` from ~145 ms into a failure. The cause
is structural, not a data accident: `vw_student_academic_risk` reaches each student's final result
through a per-enrollment LATERAL (`WHERE fr_1.studentenrollmentid = enr.id ORDER BY fr_1.id DESC LIMIT 1`),
and `studentfinalresult` carries `ix_studentfinalresult_year`, `ix_studentfinalresult_academicyearid`,
`ix_studentfinalresult_tsc` and its PK - **but nothing on `studentenrollmentid`**. So the LATERAL
seq-scans the table once per enrollment: measured `Seq Scan on studentfinalresult fr_1 (loops=2000)`,
**78,000 of the query's 646,280 buffer accesses**.

| count leg (`SELECT COUNT(*) FROM vw_student_academic_risk`) | buffers | execution |
|---|---|---|
| as shipped | **646,280** | 167 ms |
| + `studentfinalresult(studentenrollmentid)` (rolled-back probe) | ~10,000 | **19 ms** |

**Every sibling already has it**, which is what makes this a defect rather than a convention:
`studentsubjectresult` -> `ix_studentsubjectresult_enrollment_term`, `attendance` ->
`ix_attendance_studentenrollment`, `studentexam` -> `ix_studentexam_enrollment`, `studentassessment` ->
`ix_studentassessment_exam`, `studentreportcard` -> `ix_studentreportcard_final`. `studentfinalresult`
is the one table in that family that was missed - the same class as `V132` and `V130`.

⚠️ **THE VERDICT IS CONTEXT-DEPENDENT AND THE PLAN IS NOT.** Run alone the count reads 164-173 ms
(12% over the budget); inside a whole-catalogue sweep it reads **~1.03 s**, with an **identical plan,
identical buffers and the same indexes used**. That is the documented plan-identical machine effect
(see the attendance view), amplified by a query whose buffer traffic is already ~5 GB. It straddles the
budget in both contexts today, and the index takes it out of contention in both - which is why the
number is worth acting on even though only one of the two contexts shows a `FAIL`.

#### ✅ SHIPPED — `V136__Student_Final_Result_And_Picker_Indexes.sql`

Both candidates are now a migration: `ix_studentfinalresult_enrollment` on
`studentfinalresult (studentenrollmentid)` and `ix_student_tenantschoolcampus_isactive_name` on
`student (tenantid, schoolid, campusid, isactive, name)`. Applied to `AyraDB` and `ayra_perf`, and
carried into the rebuilt template (`ok V136`, **902 indexes**, `17 migrations recorded, up to V136`);
`ayra_e2e` was then reset from it and both indexes verified present.

| spec | before | after |
|---|---|---|
| `student-available-page` | 8,232-8,448 ms FAIL | **258-265 ms OK** (\~31x; `--advise` reads 9.02 s -> 18.1 ms, 497.6x, `PROVEN`) |
| `rpt-student-academic-risk-count` | 998 ms in-sweep / 168 ms alone | **151 ms in-sweep / 20.5 ms alone** (\~8x) |
| `rpt-student-academic-risk` (page) | 39-44 ms | **3-4 ms** |

⚠️ **NORTH OF THE FIX, TWO OF THE THREE REMAINING FAILURES ARE NOW BOUNDARY CASES AT 151 ms AGAINST A
150 ms BUDGET** (`rpt-student-attendance` and `rpt-student-academic-risk-count`), i.e. the same verdict
class the tool already models as `UNSTABLE` - and `rpt-student-academic-risk-count` reads **20.5 ms when
run alone**. The whole-catalogue headline stays **104 OK / 3 FAIL / 0 SKIP**, but the COMPOSITION of the
three changed from "one multi-second pathology plus two view costs" to "three boundary/view costs".
Re-run `--only rpt-student-academic-risk --runs 5` before quoting the count leg; in a sweep it is a
machine-regime artifact, alone it is 8x better than it was.

⚠️ **A plain performance index refuses nothing, so it owes no negative `SchoolE2E` phase — but it IS a
schema change, so the whole-suite rung of the verification ladder applies.** 
**✅ TAKEN (Sept 2026) - and this is the rung for `V136` + `V137` + `V138` together.** On a PRISTINE
baseline (stop API -> `reset-test-database.sh` -> start the stack with `SCUBE_MainDb` -> whole suite):
**334 cases - 332 passed / 1 failed / 1 skipped, 1 h 18 m**, and the single failure is J7's
phase-independent **ribbon race on the registration workspace** (**13/14** in a filtered whole-J7 run,
and the failing phase passes ALONE in 45 s) - nothing to do with a schema change, and no `rpt-*` spec
regressed. See `../AGENTS.md` for that run's full narrative, including the two launch mistakes it cost
(`SCUBE_MainDb` must be exported into the TEST process as well as the stack, or
`Api_connection_string_points_at_the_test_database` reports a FALSE mismatch against `appsettings.json`).

## The coverage census — what this tool does NOT reach (Sept 2026, re-counted)

**A green run is not a coverage statement.** The catalogue is green against **92** of the controllers
that carry a grid, and the gap has two independent halves:

| dimension | the application has | the catalogue reaches |
|---|---|---|
| grid list endpoints | **113** `WrapSearch(` call sites across **92** `Controllers/*.cs` (**121** `GetPageInfo(`) | **243** tabular specs |
| reporting views | 17 shipped reports over 14 views | **21** report specs (page + count legs) |
| tables it can measure | 246 in the schema | **243** carry any data — **3** are EMPTY |
| of the nonempty tables | **43** hold more than 1,000 rows | see the volume-table gap below |
| **grid-driving tables that are EMPTY** | **3**, and all three are DELIBERATE (no reader exists — see the TWELFTH pass) | the list is the EXACT empty set below — the derived number is deliberately not quoted |

⚠️ **THE EMPTY COUNT WAS WRONG TWICE, IN THE DIRECTION THAT MAKES THE GAP LOOK BIGGER, AND
`pg_stat_user_tables.n_live_tup` IS WHY.** The catalogue's own habit of reading `n_live_tup` is fine for
"is there ANY data" at a glance, but it is an ESTIMATE maintained by ANALYZE/autovacuum, and on this
database it reported **145 empty where 95 are empty** - it under-counted 50 nonempty tables, including
ones the catalogue demonstrably measures (`reportdefinition`, `users`, `menuitem`). Use an EXACT count:

```sql
select relname,
       (xpath('/row/c/text()',
              query_to_xml(format('select count(*) as c from public.%I', relname),
                           false, true, '')))[1]::text::bigint as n
  from pg_stat_user_tables
 where n = 0;   -- the window is only for readability; filter on the value
```

**The `51` is the number that matters, and it is derived, not hand-counted.** A table is a grid target
when it is EMPTY and EITHER (a) some hand-written repository's paged method reads it
(`DataTablePageInfo`/`PagingInfo` in the same file as a `FROM <table>`), OR (b) it is a
`GenericRepository` table (`RepositoryTable(TableName.X)`) - because the tool's `ScopeGrid` specs ARE
that generic shape. The derivation first yielded **81** (36 hand-written + 78 generic, overlapping); the
seventh pass SEEDED 30 of them (HR workflow, the asset lifecycle, HR money/lifecycle, the student
profile) and the intersection with the current EMPTY set is **51**. That is the list to seed from, in
module order, and it is why "145 empty tables" was never the right target: most of the remaining ones
are child/join/detail tables no grid pages.

⚠️ **THE REMAINING 51 ARE THE HARD HALF, and the reason is a PREREQUISITE CHAIN, not the seeding.**
Two blocks dominate: the **communication workspace** (`homework` + `homeworkattachment` +
`homeworkstudent` + `homeworksubmission` + its attachment/comment children, `moment` + its three,
`schooleventread`/`response`/`audience`/`attachment`, `hrmeeting`) needs `teacher` rows (EMPTY at the
seeded campus) and `attachmentfile` rows; and the **approval engine** (`approvalworkflow` +
`approvalworkflowstep` + `approvaltemplatestep`, `rolepermission`) is a chain of parents rather than
independent tables - the TENTH pass seeded it, and the chain was two deep: the eight `approvaltemplate`
rows already existed and every one of them held **zero** steps, so the module was configured and could
route nothing. Seed the PREREQUISITE first and the batch becomes ordinary - which is the same lesson
`HrWorkflowSeeder` recorded for `HrModuleSeeder`.

⚠️ **THE DERIVED "31 REMAINING" FIGURE IS NOT QUOTED ANY MORE, AND THAT IS THE POINT OF THIS PARAGRAPH.**
`51 - 16 - 4 = 31` is ARITHMETIC over a derivation that was already one pass old, and this file's own
rule is that a hand-carried census drifts in both directions. The derived test (a table is a grid target
when it is EMPTY and either a hand-written paged repository reads it or it is a `RepositoryTable` target)
was re-run by hand for the current 36 and it is NOT reproducible as a single number: 33 of the 36 appear
in `Utilities/TableName.cs` (the GenericRepository catalogue) and 8 are read by a paged method in a
hand-written repository (`adhoccharge`, `assessmenttool`, `assessmenttoolitem`, `eventparticipant`,
`refund`, `rubriccriterionlevel`, `taxexemption`, `usertwofactor`), but most of those are CHILD tables of
a parent grid (`employeepayrolldetail`, `employeesalarystructuredetail`, `studentadhoccharge`,
`discountinvoices`, …) whose parent is the thing a controller pages. **So the honest worklist is the EXACT 36, named — not a derived count.** The last three
passes filled **28** previously-empty tables (`16` NINTH + `4` TENTH + `8` ELEVENTH), which is a tally
over the SWEEP rather than over the original derivation - and that is the only kind this file accepts.

**`265` specs in total = 244 tabular + 21 report.** The tabular half grew from 53 in the sixth pass,
which added a spec for every master-data and reference grid whose table the perf dataset left EMPTY
(see *The sixth pass* below) - that is what closed the last `SKIP` - then from 86 to 125 in the
seventh pass, which SEEDED the empty tables behind the remaining specs (see *The seventh pass*), to
**139** in the NINTH pass, which seeded the sixteen tables of the communication workspace (`teacher`,
`attachmentfile`, `homework` + its five children, `moment` + its three, the four `schoolevent*` tables,
`hrmeeting`) behind the same ONE missing prerequisite), to **147** in the TENTH pass, which seeded
the approval engine and wrote eight specs over it, and to **168** in the ELEVENTH pass, which seeded the
TIMETABLE / TEACHER-OPS module (its own academic spine included) and wrote twenty-one specs over it, and
to **215** in the TWELFTH pass, which seeded the LAST 33 empty grid-driving tables (exam tooling, the
event money chain, the HR money/structure details and the desk/administrative tables) and wrote the
remaining specs over them, and to **243** in the THIRTEENTH pass, which specced the remaining grid
endpoints whose tables ALREADY held rows (accounting master data, the curriculum tree, the inventory
desks, the fee-structure grid, and the HR / transport / library / exam desks), and to **244** in the
FOURTEENTH pass, which FIXED the vehicle-assignment desk's paging and replaced its one unpaged
`ScopeList` with the page + count pair the repository now actually sends - see *The ELEVENTH pass*,
*The TWELFTH pass*, *The THIRTEENTH pass* and *The FOURTEENTH pass* below, and `../AGENTS.md` for that
narrative and the NINTH's and TENTH's.

⚠️ **RECOUNTED September 2026, seven times, because this table drifts in BOTH directions.** Earlier
revisions read 96/234/15/22, 115/93/37/71, 113/92/89 and 101 nonempty tables against a truth of 151.
The current row of numbers comes from `grep -rhoE 'WrapSearch\(' Controllers/*.cs | wc -l` (113), the
harness's OWN total from a `--runs 3` run (**265** specs, split into 21 `--only rpt`) and an EXACT
`count(*)` sweep (**243** nonempty / 3 empty / 43 above 1,000 rows) - **regenerate them, never edit
them, and never source the table count from `n_live_tup`** (see the warning above; it is the reason two
of those revisions were wrong at once).
⚠️ **The TWELFTH pass's deltas, for the same CHECK**: non-empty 210 -> **243** and empty 36 -> **3**,
i.e. **+33 and -33 - exactly the thirty-three tables the pass filled**, with above-1,000-rows moving
42 -> **43** (the one volume table it pushed past that line, `employeepayrolldetail` at 1,600 rows - one
payslip line per employee per period, which is exactly the shape the table exists for). The three that
survive are not a gap - they have no reader at all
(see *The TWELFTH pass*).
⚠️ **The ELEVENTH pass's deltas, and the reason they are a CHECK**: non-empty 202 -> **210** and empty
44 -> **36**, i.e. **+8 and -8 - exactly the eight tables it filled** (`timetablesetup`,
`timetablesetupdetail`, `timetablesetupoffday`, `timetableentry`, `classroomsubjectteacher`,
`teacherabsence`, `timetablerelief`, `teachermoduleauditlog`). Above-1,000-rows is unchanged at **42**
(210 timetable entries and 84 assignments are real, and neither crosses that line).
⚠️ **SWEEP THE CENSUS AFTER THE PASS, NOT DURING IT.** An earlier read of this same pass returned
**209/37** - an unexplained off-by-one that was simply `timetablesetupoffday` not yet written at the
time. The clean +8/-8 above is what the check is for, and it only appears when the pass is finished.
⚠️ **The TENTH pass's deltas are the useful part, and they are worth recording as a CHECK rather than a
tally**: non-empty 198 -> **202** and empty 48 -> **44** (the four tables it filled, exactly), with
above-1,000-rows unchanged at **42** (320 workflows and 640 steps are real, and neither crosses that
line - which is the honest answer for a table whose real size is "a few terms of approvals").
**The last three passes filled 28 previously-empty tables** (16 in the NINTH, 4 in the TENTH, 8 in the
ELEVENTH), which is what kept the worklist honest - and the surviving set is taken from the sweep above,
not from a subtraction (see the paragraph on why the derived count is no longer quoted).
⚠️ **The NINTH pass's deltas, for the same check**: non-empty 182 -> 198 and empty 64 -> 48, and
above-1,000-rows 37 -> **42** (the five it pushed past that line).
Note the first row counts `WrapSearch(` **call sites** deliberately: counting the token
`WrapSearch|GetPageInfo` gives ~238 because every endpoint pairs the two, which reads as a coverage
statistic twice its real size.

⚠️ **An earlier revision of this table said "96 controllers / 234 call sites / 15 tabular specs / 22
nonempty tables", and every one of those four numbers was wrong** — the first three were copied from
the catalogue's earlier state, and the fourth from a database the module seeders had not yet filled.
It under-reported the tool's own reach by about 2x and the data by 3x. Both are recounted here from
SOURCE (`grep` over `Controllers/` and `QueryCatalog.cs`) and from `pg_stat_user_tables`, which is the
only way these numbers should ever be produced. A hand-maintained census drifts on both sides at once,
and drifts in the direction that makes the tool look thinner than it is.

**Why more specs is not the fix.** A spec over an empty table reports `SKIP` — honestly. So coverage is
`specs x data`, and the data side is the larger cost. Seeding a module is the unlock; writing its specs
first just produces skips. Many of the uncovered grids are also not a perf risk *yet*: their driving
table is empty (`FROM latefeecharges`, `FROM studentfeeassignment`), so they return nothing quickly. The
risk concentrates where a VOLUME table drives the query.

### `V132` — the four biggest fact tables had no composite scope index

182 tables carry the `(tenantid, schoolid, campusid)` triple and 104 followed the convention. The four
outliers were the LARGEST fact tables in the schema, each with only a single-column `ix_<table>_tenantid`
that cannot prune a campus — so a campus's first page seq-scanned the whole table. `V130` had already
fixed this on `student`; these are the same defect, one tier larger.

| query | as shipped | after `V132` |
|---|---|---|
| `invoices` campus 1, first page | 202.8 ms (144,399 rows discarded) | **0.091 ms** |
| `invoices` campus 8 | 269.8 ms | **0.091 ms** |
| `attendance` campus 1, first page | 1,432 ms | **0.361 ms** |
| `attendance` campus 8 | 3.839 ms | **0.153 ms** |

⚠️ **The shape is the triple PLUS the grid's sort column, and the triple alone is not enough.** The
bare `_tsc` convention was measured first: on `invoices` it gives 1.404 ms (vs 0.060 ms with the sort
column), and on `attendance` campus 1 — which holds **98.8%** of the table — it gives **1,233 ms and the
planner still chooses a seq scan**, while triple+`attendancedate DESC` gives 1.543 ms. The triple is a
LEFTMOST PREFIX of the longer index, so 4 columns serve every query the triple would and never regress.
`ANALYZE` before and after any such reading; stale statistics make a broken situation look acceptable
and a fixed one look useless.

### The HR module needed data, not specs

`ayra_perf` held 14.2M attendance rows and **zero employees**, so every HR spec SKIPPED.
`data-volume/Seeders/HrModuleSeeder.cs` now fills the module's VOLUME tables — `employee`,
`employeeattendance`, `employeepayroll` — plus the reference rows their NOT NULL foreign keys need
(`department`, `designation`, `payrollperiod`, `attendancestatus`). It deliberately does NOT seed the
workflow tables (leave, loans, overtime, contracts, reviews, settlements): a real school holds TENS of
those, so they are not a performance question.

Measured after it: four specs (`hr-employee-page`, `hr-employee-count`, `hr-attendance-daily`,
`hr-payroll-periods-page`) move **SKIP -> OK**, all sub-2 ms, and the measurable table set grows from
15 to 22. Three traps the seeder's own test now guards:

* **Seed a TYPICAL campus, not the busiest.** Selecting by student count DESC picked the artifacts
  (campus 1 holds 843,414 of 879,514 students) while `db-report` measures the campus at the MEDIAN
  student count (2,000) — so 1,440 seeded employees still read SKIP. The rule now excludes any campus
  holding >10% of the table.
* **`attendancestatus` must exist at the campus's OWN scope** — the nine canonical rows ship at
  (1,1,1) and the repository matches the scope exactly, so any other campus sees an empty list.
* **The attendance window must END today**, not yesterday, or the day the screen opens on is empty.

### Inventory / Library / Transport / Accounting: unmeasurable, not slow

The same gap HR had, four times over. `data-volume/Seeders/` gained `InventoryModuleSeeder`,
`LibraryModuleSeeder`, `TransportModuleSeeder` and `AccountingModuleSeeder`, and the catalogue gained
ten specs (`inv-item-page` / `inv-item-count` / `inv-movement-page` / `inv-stock-page`,
`lib-book-page` / `lib-issue-page`, `tr-assignment-page` / `tr-route-page`,
`acct-journal-page` / `acct-posting-page`).

**Every one of them measures OK, and no index came out of the batch — which is the correct outcome.**
This tool only PROPOSES an index by probing a FAILING query, so a module that passes owes no DDL.
Measured on campus 15 (the median campus the tool reads by default):

| spec | p95 | rows | what it proves |
|---|---|---|---|
| `inv-item-page` / `inv-item-count` | 4.3 / 2.0 ms | 50 / 200 | the item grid and its separate count round trip |
| `inv-movement-page` | 2.8 ms | 50 | the ledger, 6,000 rows/campus, sorted by `MovementDate DESC` |
| `inv-stock-page` | 4.4 ms | 50 | stock grid joining `invitem` |
| `lib-book-page` | 33.0 ms | 50 | the catalogue, with a correlated `string_agg` over authors PER ROW |
| `lib-issue-page` | 17.7 ms | 50 | the circulation desk (`Status = 'Issued'`) |
| `tr-assignment-page` | 37.2 ms | 50 | the rider list, now genuinely **paged** — 37.2 ms buys a page of 50, where the pre-fix spec measured the WHOLE campus list for 31.4 ms |
| `tr-assignment-count` | 1.4 ms | 500 | that page's COUNT round trip, which the fix made a real query instead of `list.Count` |
| `tr-route-page` | 1.5 ms | 12 | the module's low-volume control |
| `acct-journal-page` | 12.7 ms | 50 | the journal, with FOUR correlated sub-selects per row |
| `acct-posting-page` | 6.3 ms | 50 | the posting monitor + `Payload ->> 'description'` |

Two structural notes, recorded in each spec's `IndexRationale`:

* **`invitem` is the one inventory table without the scope convention** — `idx_invitem_tenant_school`
  is tenant+school only and the partial unique is `(tenantid, schoolid, code) WHERE isactive`, so
  neither serves the grid's campus-scoped read. It is fast today because 200 items is 200 items; a
  campus with 10,000 would want the triple. That is a candidate, not a fix — it has to be PROVEN by a
  failing probe.
* **`transportstudentassignment` carries `(tenantid, campusid)` with no `schoolid`**, while
  `transportroute` and `transportvehicle` both carry the full triple.
* ✅ **`TransportStudentAssignmentRepository.GetAll(page, …)` used to take a `page` and IGNORE it** — no
  `LIMIT`, no `ORDER BY`, no count query; it loaded the whole campus rider list and reported
  `Count = list.Count`, so "page 1, size 1" was 500 rows and the total grew with the list. **FIXED** as a
  paging-CONTRACT fix (no index was owed, and none was added): the two specs above now measure a page of 50
  (37.2 ms) plus a real count (1.4 ms) instead of one 500-row read. Verified live through the running host as
  well as in-process — `length=1 -> 1 row`, `start=1 -> the next id`, `start=10000 -> 0 rows with the total
  unmoved`, `length=-1 -> 500 rows`, a no-match search `-> 0` (was 500), and a no-match search on the JOINED
  `r.RouteName -> 0`. Carried into `SchoolE2E` as J10 phase 8, because the route has NO screen and the journey
  is therefore the only thing that holds its contract.

Seed it with:

```bash
cd school-performance/data-volume && SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 \
    SCUBE_PERF_MODULE_CAMPUS_LIST=15 dotnet test SchoolDataVolume.csproj \
    --no-build --filter "FullyQualifiedName~ModuleDataset"
```

`SCUBE_PERF_MODULE_CAMPUS_LIST` must name the campus `db-report` measures (the MEDIAN by student
count — 15 here). `ModuleDatasetTests` asserts each module's volume is not owned by one scope.

### The volume-table gap: three tables >1,000 rows that NO spec and NO view named (Sept 2026)

A census of `pg_stat_user_tables` against the table identifiers the catalogue's SQL actually names
found **seven tables holding >1,000 rows that no spec reaches directly**. Four of those turned out to
be fine to leave, and three were a genuine blind spot:

| rows (dataset) | table | why it is not a gap |
|---|---|---|
| 144,395 | `invoiceline` | reached INDIRECTLY — it is inside `vw_fee_collection` and `vw_outstanding_fees` |
| 36,103 | `paymentallocation` | reached INDIRECTLY — inside `vw_fee_collection`, and named directly by 4 specs |
| 9,360 | `employeepayroll` | indexed for both of its access paths; see *Still owed* |
| 4,320 | `invdepreciation` | read per ASSET (`AssetId = @AssetId`), not per campus |
| 1,400 | `invitemuom` | read per ITEM (`InvItemId = @ItemId`), not per campus |
| 1,120 | `invpoline` | join-only — driven by the GRN / posting queries |

**The three that were blind** — and the five specs now covering them. All three ALREADY carry the
scope convention, so unlike `invitem` these were a measurement gap rather than a defect: the batch
produced **no DDL**, which is the correct outcome for a module that passes.

| spec | p95 | rows | the query it mirrors |
|---|---|---|---|
| `lib-fine-page` | 10.8 ms | 50 | `LibraryFineRepository.GetAllPaged`, data half — a scope filter over **six** left joins |
| `lib-fine-count` | 3.0 ms | 850 | the same round trip's count half (`QueryMultipleAsync` runs both) |
| `inv-itemcost-page` | 4.8 ms | 200 | `InvItemCostRepository.GetAll` — **no `LIMIT` on purpose** |
| `lib-inventory-audit-page` | 3.0 ms | 50 | `LibraryInventoryAuditRepository.GetAllPaged` over three left joins |
| `lib-inventory-audit-count` | 0.8 ms | 200 | the same round trip's count half |

Two of those carry a lesson worth naming:

* **`invitemcost` is loaded IN FULL, and that is the finding.** `inv.itemCost.html` is a CLIENT-side
grid: the repository returns every cost row for the campus and DataTables pages it in the browser. So
its spec has no `LIMIT` — the absence of one is the thing being measured, and it is why a campus whose
cost rows grow hits a page-LOAD problem rather than a paging one. 200 rows is 4.8 ms; the shape is
what matters, not today's number.
* **A count half is worth its own spec when it runs in the same round trip.** `libraryfine` and
`libraryinventoryaudit` both issue `countSql + ";\n" + dataSql` through one `QueryMultipleAsync`, so
the user waits for BOTH — and the count re-runs the whole join set (the repository does that
 deliberately, so DataTables search columns resolve in both queries). Measuring only the page would
have reported half the cost.

### `countmode=window` vs `separate` — RE-MEASURED PER REPORT, THEN APPLIED SELECTIVELY (Sept 2026, `V133` then `V135`)

The five `rpt-*` failures were the last open question: every seeded definition uses
`countmode='window'`, where `COUNT(*) OVER()` forces the whole filtered set to be materialised before
the first row can be returned, so `LIMIT 50` cannot short-circuit. The engine implements
`countmode='separate'` (`ReportQueryBuilder` emits `query.CountSql = SELECT COUNT(*) FROM <from>
<where>` and `ReportQueryExecutor.ExecuteCountAsync` runs it as a second round trip), so flipping the
column looked like the fix.

⚠️ **AN EARLIER REVISION OF THIS SECTION REJECTED SEPARATE WHOLESALE, AND ONE OF ITS NUMBERS HAD GONE
STALE.** It reported `vw_fee_collection` as a "3.7x REGRESSION" because the page query nested-looped a
`Seq Scan on paymentallocation` (36,103 rows). `V132` added
`ix_paymentallocation_tenantschoolcampus_invoice`, that plan no longer exists, and the very same page
query now measures **26 ms**. A perf decision recorded before an index landed must be re-measured
after it — so the A/B was re-run **inside the tool** (same session, binding and statistics as the
control; `--only rpt --runs 5`, campus 15, p95, budget 150 ms) and per report:

| report | window | sep page | sep count | sep TOTAL | effect |
|---|---|---|---|---|---|
| `STUDENT_ACADEMIC_RISK` | 176 | 19 | 72 | **91** | −48% — **WIN, flipped by `V133`** |
| `FEE_COLLECTION` | 159 | 25 | 59 | **84** | −47% — **WIN, flipped by `V133`** |
| `OUTSTANDING_FEES` | 248 | 11 | 224 | **235** | −5%, still over — left on `window` |
| `STUDENT_ATTENDANCE` | 227 | 205 | 196 | **401** | **+77% WORSE** — left on `window` |
| `STUDENT_ABSENTEE_LIST` | 224 | 197 | 188 | **385** | **+72% WORSE** — left on `window` |
| `ASSESSMENT_PERFORMANCE` | 240 | 64 | 87 | **150** | −40% — **WIN, flipped by `V135`** (lands ON the 150 ms line) |
| `LEARNING_OUTCOME_PERFORMANCE` | 430 | 26 | 7 | **33** | **~15x** — **WIN, flipped by `V135`** |

The last two were **not measurable when `V133` ran** — their fact tables (`studentassessment`,
`curriculumtopicplan`) held no rows on this campus, so both specs reported `SKIP` and could not
produce a window number at all. They only acquired one after the sixth-module seeding pass (below),
which is why the flip they deserve is a second migration rather than a change to the first one.

* **The separate `COUNT(*)` is not cheap** — the count RE-RUNS the whole view, so where it costs about
  what the window did the cost is not removed, only moved into a second round trip. That is exactly
  what the attendance/absentee rows show (count 196/188 vs window 227/224): separate pays the view
  evaluation twice and loses.
* **Separate wins where dropping the window term makes the PAGE nearly free** —
  `vw_student_academic_risk` 19 ms and `vw_fee_collection` 25 ms against 176/159 under the window
  count. There the window aggregate (and the per-row LATERAL evaluation it forced) was the entire
  cost of page 1.
* **`OUTSTANDING_FEES` is deliberately left on `window`.** Its page drops to 11 ms under separate but
  its count is 224 ms, so separate moves the cost rather than leaving it (248 → 235 is noise, and the
  second view evaluation buys nothing).
* **The remaining over-budget reports are not a count-mode problem.** `(none - this query is slow for
  a reason an index will not fix)`: their cost is the VIEWS themselves (the anchor-only scope
  predicate and the per-row LATERALs). No `countmode` value brings them under 150 ms; the real fix is
  reshaping the views, which is its own task.
* **`V133` is the change of record** (`school-db/migrations/V133__Report_CountMode_Selective.sql`):
  it sets `countmode='separate'` on exactly `STUDENT_ACADEMIC_RISK` and `FEE_COLLECTION` and nothing
  else.
* **`V135` is the SECOND change of record** (`school-db/migrations/V135__Report_CountMode_Selective_II.sql`):
  it sets `countmode='separate'` on exactly `ASSESSMENT_PERFORMANCE` and `LEARNING_OUTCOME_PERFORMANCE`
  and nothing else. It is a separate migration because those two reports did not exist as measureable
  specs when `V133` was written — they were `SKIP`, and a `SKIP` is not a verdict.
* **The column default deliberately stays `'window'`**, and all FOUR flipped definitions carry their own
  page **and** count spec in `QueryCatalog` (`rpt-<code>` + `rpt-<code>-count`), because the catalogue
  has to reproduce what the app actually runs — including the separate mode's second round trip.
* **⚠️ A MIGRATION CANNOT SET A VALUE ON ROWS THE SEED HAS NOT INSERTED YET, so `V133`/`V135` each land
  in TWO places**: the migration (for databases where the rows already exist) **and**
  `school-db/seed/02_Config_Seed_Data.sql` (what a fresh install and the e2e template are built from).
  The seed is GENERATED from `AyraDB`, and `e2e/03_Baseline_Reference.sql` is generated from the seed,
  so the recipe is: apply the migration to `AyraDB` → regenerate the seed → regenerate the e2e
  baseline. Verify on the REBUILT database, never on the one you hand-patched — that is how the
  V133-as-a-no-op (all 17 rows still `window` on a fresh template) was caught.

### The second census: the grids whose tables ALREADY hold data (Sept 2026)

⚠️ **THE UNLOCK FOR THE SIX `rpt-*` SKIPs IS A SEEDER, NOT A SPEC** — so this pass deliberately took
the OTHER half of the gap: the grids whose driving table already has rows, where a spec measures
something today instead of reporting SKIP. (Those six were then closed by seeders in the pass below,
*The third census*.) Sixteen specs were added (`lib-copy-page`,
`lib-copy-count`, `lib-member-page`, `lib-member-count`, `lib-returns-page`, `lib-returns-count`,
`lib-overdue-list`, `lib-overdue-count`, `lib-popular-books`, `lib-reservation-page`,
`lib-dashboard-copy-counts`, `hr-attendance-grid-page`, `hr-attendance-grid-count`,
`hr-attendance-monthly`, `student-available-page`, `invoice-overdue-eligible`), taking the catalogue
from **56 to 72 specs** — **53 tabular + 19 report**.

⚠️ **THE PER-CAMPUS NUMBER IS THE ONE THAT DECIDES, AND IT IS MUCH SMALLER THAN THE TABLE TOTAL.**
This is why the pass covers a module (library) and a handful of others rather than the ~70 uncovered
grids the census counts:

| table | total | per campus (the scope measured) | verdict |
|---|---|---|---|
| `libraryissue` | 10,200 | 2,000 | specced — returns, overdue, popular books |
| `invoices` | 144,402 | 8,000 | specced — the late-fee engine's sweep |
| `employeeattendance` | 65,520 | 5,040 | specced — grid + monthly summary |
| `student` | 879,514 | 2,000 | specced — the enrolment picker |
| `librarybookcopy` | 4,200 | 600 | specced — grid + the dashboard's N+1 |
| `librarymember` | 2,600 | 500 | specced |
| `libraryreservation` | 600 | 100 | specced |
| `transportroutestop` | 672 | 96 | **not** specced — read PER ROUTE (~8 rows) |
| `invasset` / `invpurchaseorder` / `invgrn` / `invstockrequest` / `libraryacquisition` / `transport*` / `invstockadjustment` / `libraryreadinglist` | 30-360 | **15-60** | **not** specced — tens of rows |

The tens-of-rows group is left out by the same rule that keeps the leave / loan / overtime / contract
tables out: a campus owns 15-60 assets, POs, GRNs, suppliers, drivers or routes, so a spec over one
times noise while reporting `OK`.

#### ⚠️ ONE REAL DEFECT, AND IT IS THE WORST NUMBER THE CATALOGUE HAS MEASURED

`student-available-page` — the ENROLMENT PICKER (`StudentRepository.GetAllAvailableStudents`) —
**8.70 s**, budget 300 ms.

| | |
|---|---|
| plan | `Limit on ? (~0 rows, 9,043 ms)`, driving **`ix_student_name`** |
| buffers | 3.28M hit / 1.14M read |
| rows returned | **0** |
| probe | **8.70 s → 0.289-0.306 s (28-30x)** with `ix_student_tenantschoolcampus_isactive_name (tenantid, schoolid, campusid, isactive, name)` |

**The shape is an `ORDER BY s.Name LIMIT 50` over a filter that can reject EVERY row.** `IsActive` is
not in `ix_student_name`, so the campus predicate is a query-level Filter and the planner walks
students in NAME ORDER across the whole 879,514-row table, testing the scope and a per-row
`NOT EXISTS` against `studentenrollment` — and on a campus where all 2,000 students are already
enrolled for the target year it qualifies ZERO, so the LIMIT never short-circuits and the walk runs
to the end of the index. **The end-of-year state is the worst case**, which is exactly when a user
opens the picker.

Two things the reader should carry away, and the second is the reason this row is not simply
"ship the index":

* **The deployed `ix_student_tenantschoolcampus_isactive` (V130) cannot help** — it does not carry the
  sort column, which is why the plan falls back to `ix_student_name`. The candidate is the
  **scope prefix AND the sort column together**, the same rule `V130`/`V132` already recorded: the
  triple is what a small campus slice needs, and the sort column is what bounds the read.
* ⚠️ **IT IS A BOUNDARY CASE AND THE TOOL FLIP-FLOPS ON IT.** One run's probe read **289 ms** and
  printed `PROVEN`; the next read **306 ms** against the same 300 ms budget and printed
  `NOT proven, not written`. So the honest statement is **"28-30x faster and still at the budget
  line"** — not "fixed". The durable fix is the query's shape, which is its own change: exclude the
  enrolled set once (a `LEFT JOIN ... IS NULL` the planner can drive from `studentenrollment`) instead
  of a per-row `NOT EXISTS` under an ordered `LIMIT`.

#### ✅ FIXED — the shape was the lever, not the index (Sept 2026)

`StudentRepository.GetAllAvailableStudents` now expresses the same rule as an **anti-join** —
`LEFT JOIN StudentEnrollment se ON ... AND se.StudentStatus NOT IN (4,5,6,7)` plus `AND se.Id IS NULL` —
replacing the correlated `NOT EXISTS` under the ordered `LIMIT`. The `LIMIT` could not short-circuit a
shape that probed `studentenrollment` once per candidate, so on this campus (all 2,000 students already
enrolled for the year) it paid that probe for the whole campus and still returned zero rows.

| | old `NOT EXISTS` | anti-join |
|---|---|---|
| p95 | 336 ms | **1.9-6.5 ms** |
| plan exec | 640 ms | **0.9-3.3 ms** |
| buffers | 10,060 | **106** |
| heaviest node | `Limit on ? (~0 rows, 640 ms)` — the LIMIT itself | `Hash Left Join` over the campus's own index scan |

The plan is now `Limit -> Sort(s.name) -> Hash Left Join (s.id = se.studentid), Filter: (se.id IS NULL),
Rows Removed by Filter: 2000 -> Index Scan using ix_student_tenantschoolcampus_isactive -> Hash
(studentenrollment)`: the campus's students are read once and the campus's live enrollments hashed once,
so the number of per-row probes is **1, not 2,000**.

⚠️ **THE PICKER NO LONGER NEEDS THE SORT COLUMN.** `V136`'s `ix_student_tenantschoolcampus_isactive_name`
is what bounded the OLD shape's name-ordered walk to the campus, and the probe landed ON the budget line
(289 ms `PROVEN` one run, 306 ms `NOT proven` the next). With the anti-join the read is the campus's own
2,000 rows, and dropping that index inside a rolled-back transaction leaves the query at **~1.0 ms** on
`V130`'s triple. `V136` stays (the student grid's own top-N read uses the sort column), but this spec now
declares the index the plan actually takes.

⚠️ **EQUIVALENCE WAS MEASURED, NOT ASSUMED.** Both shapes were run side by side in a rolled-back
transaction with half of campus 15's year-14 enrollments marked non-live AND one student given a SECOND
live enrollment in the same year (the duplicate-row risk): **1,001 available / 999 excluded**, `EXCEPT` in
both directions **0**, and the anti-join's row count equal to its `DISTINCT` count. The year-less branch
(`academicYearId = 0`) was checked the same way. A student matching several live enrollments is still
excluded exactly once, and a student matching none still produces exactly one row.

The catalogue carries both halves of that round trip:
`student-available-page` **6.5 ms** and `student-available-count` **6.1 ms** (the repository issues
`countQuery; searchQuery` through one `QueryMultipleAsync`, so the user waits for both).
**Verified through the running application**: J2 + J2b + J2c + J3 filtered = **19 passed / 0 failed
(8 m 59 s)**, including J3's roster write and its "the enrolled student is no longer offered" assertion.

#### ⚠️ AN N+1 THE CENSUS COULD NOT SEE: the library dashboard

`LibraryReportController.GetDashboard` (and its `counts` twin) loads every active book for the campus
and then loops `TotalCountByBook(b.Id)` + `CountAvailableByBook(b.Id)` — **`1 + 2 x books` round trips
on every page load**. A spec measures one statement, so `lib-dashboard-copy-counts` measures the
per-book one (**0.4 ms**) and records the multiplier: a 350-book campus is **700 round trips** before
the dashboard's own nine aggregate queries run. It needs no index (`ix_librarybookcopy_book` serves
it) — the fix is two `GROUP BY BookId` aggregates for the whole campus.

⚠️ **THE TOOL COULD NOT HAVE FOUND THIS ON ITS OWN.** Every statement involved passes its budget, so
no `FAIL` is printed; the defect is the *number of statements*, which is a shape no single-query
benchmark can see. It was found by READING the controller while transcribing the spec — the same
argument the catalogue's own "if a repository's SQL changes, it changes HERE" rule makes.

#### What the sixteen measured

| spec | p95 | rows | note |
|---|---|---|---|
| `lib-copy-page` / `-count` | 1.5 / 0.9 ms | 50 / 600 | scope composite deployed |
| `lib-member-page` / `-count` | 4.1 / 0.7 ms | 50 / 500 | two correlated counts PER ROW |
| `lib-returns-page` / `-count` | 19.4 / 1.3 ms | 50 / 1,700 | the LARGER half of `libraryissue` |
| `lib-overdue-list` / `-count` | 3.5 / 1.6 ms | 252 | **unbounded** — no `LIMIT` in the repository |
| `lib-popular-books` | 8.6 ms | 10 | `GROUP BY` over the whole issue history + a correlated `string_agg` |
| `lib-reservation-page` | 2.4 ms | 50 | the module's low-volume control |
| `lib-dashboard-copy-counts` | 0.4 ms | 2 | the N+1 above |
| `hr-attendance-grid-page` / `-count` | 5.5 / 2.2 ms | 50 / 5,040 | the attendance desk's own grid |
| `hr-attendance-monthly` | 18.5 ms | 120 | seven `COUNT(...) FILTER` arms over the month |
| `invoice-overdue-eligible` | 3.2 ms | 0 | the late-fee sweep; **cheap for a FIXTURE reason** — `invoices.balanceamount` is NULL on all 8,000 rows, so `balanceamount > 0` prunes everything |
| `student-available-page` | **8,703 ms** | 0 | the defect above |

### The third census: SEEDING the six SKIPped reports' fact tables (Sept 2026)

⚠️ **A SPEC OVER AN EMPTY TABLE REPORTS `SKIP` HONESTLY — AND SIX SHIPPED REPORTS SAT THERE.** The
second census deliberately took the OTHER half of the gap (grids whose tables already had rows),
because the unlock for these six is DATA, not more specs: `studentsubjectresult` and
`studentassessment` held 0 rows in every database here, `employeeleaverequest` 0,
`enrollmenttransferhistory` 1 and `curriculumgradesubjecttopic` 1. So `rpt-grade-performance`,
`rpt-subject-performance`, `rpt-assessment-performance`, `rpt-leave-summary`, `rpt-student-transfer`
and `rpt-learning-outcome` could not be measured at all — which reads as "not measured yet" on six
reports the product ships.

**Four module seeders closed it**, under `data-volume/Seeders/` (each exposing its own
`TablesToAnalyze` so `PerfDatasetSeeder` ANALYZEs them):

| seeder | what it builds | why it is more than a row count |
|---|---|---|
| `ExamsModuleSeeder` | `assessmentcomponent` → `academicgradesubject` → `subjectassessmentcomponent` → a **Published** `examschedule` → `studentexam` → `studentassessment` → `studentassessmentitem` → `studentsubjectresult` | `vw_assessment_performance` is a 9-deep INNER JOIN chain, so a fact row whose parent does not resolve is INVISIBLE to the report. It also fills `assessmentcomponent`, which held **0 rows in every database here** and is NOT NULL on `subjectassessmentcomponent` |
| `CurriculumModuleSeeder` | the learning-outcome tree on the school's PUBLISHED version: `subject` → `curriculumgrade` → `curriculumgradesubject` → `curriculumgradesubjecttopic` → `curriculumtopicplan` | `vw_learning_outcome_performance` filters `curriculumversion.curriculumstatus = 5`, so a topic under an UNPUBLISHED version is invisible while the table looks populated |
| `HrLeaveSeeder` | per-campus `leavetype` + `employeeleaverequest` against the campus's real employees | `GetActiveLeaveTypes` matches tenant/school/campus **EXACTLY** while all eight canonical types sit at (1,1,1) — so on any other campus the Apply dialog is EMPTY and a request has nothing to point at. Same trap as `attendancestatus` in the HR seeder |
| `TransferModuleSeeder` | `enrollmenttransferhistory` | `TransferType` is 1 Section / 2 Campus / 3 School / 4 Withdrawal / 5 Graduation; a value outside that set renders a blank type in the report |

**And a shared fixture (`Tests/ReportingDatasetTests.cs`) that asserts the joins, not the row count** —
each module asserts the VIEW returns exactly what the TABLE holds at the same scope, which only holds
when every parent id resolves. That is the defect the attendance seeder once shipped
(`attendance.studentenrollmentid` NULL on all 14.2M rows while `vw_student_attendance` joins on that
column, so the report returned 0 rows and still scanned the table).

#### ⚠️ SEED THE SCOPE THE TOOL MEASURES, OR THE SEED IS DECORATION

The first run seeded six campuses and BOTH leave and transfer reports STILL reported
`SKIP (scope holds 0 rows)` — the data was real and in the wrong scope. `SeedCampuses.CampusesAsync`
picks campuses holding <= 10% of the students (so a scope-column index can be judged), which orders by
ASCENDING campus id and returned **[4,5,6,7,8,9]**, while `db-report` measures the **MEDIAN campus by
student count** = **campus 15**. The list now appends the tool's OWN resolution (mirrored, not
re-derived) so the two cannot disagree about which campus is typical.

#### The six, after seeding (campus 15)

| report | before | after |
|---|---|---|
| `rpt-grade-performance` | SKIP | **64 ms OK** (2,000 results) |
| `rpt-subject-performance` | SKIP | **65 ms OK** |
| `rpt-leave-summary` | SKIP | **6 ms OK** (480 requests) |
| `rpt-student-transfer` | SKIP | **5 ms OK** (40 transfers) |
| `rpt-assessment-performance` | SKIP | **238 ms FAIL** (6,000 assessments) |
| `rpt-learning-outcome` | SKIP | **123-412 ms FAIL** (1,051 topics) |

**The catalogue is now `0 SKIP`.** Two of the six are over budget, and they are TWO DIFFERENT causes —
which is the reason this pass was worth running rather than declaring the module "covered".

#### ⚠️ `rpt-learning-outcome` IS A REAL MISSING INDEX (and the tool's `(none …)` here was vacuous)

`curriculumtopicplan` carries **only its primary key** — no index on `curriculumtopicid`, the column the
view's `LEFT JOIN LATERAL` joins on. The LATERAL therefore runs a **seq scan per topic**: measured
`Seq Scan on curriculumtopicplan tp2 (loops=1051)`, `Rows Removed by Filter: 2098` on each of those
1,051 loops, **32,581 buffers**. With a rolled-back probe index:

| | buffers | execution |
|---|---|---|
| as shipped | **34,734** | 134 ms |
| + `curriculumtopicplan(curriculumtopicid)` | **3,146** | **4.2 ms** |

⚠️ **The tool printed `suggested index: (none - this query is slow for a reason an index will not fix)`
for this, and that was NOT a measurement.** A report spec declares no `IndexTable`/`IndexName`, so
`QueryCatalog.IndexFor` returns null and `--advise` probes NOTHING. A `(none …)` on an `rpt-*` spec
means "no candidate was declared", never "an index was tried and rejected" — read it that way.

#### ✅ SHIPPED — `V137__Curriculum_Topic_Plan_Fk_Index.sql` (the SECOND shape, measured the same day)

`curriculumtopicplan` was **the only table in the curriculum family with no foreign-key index at all** —
its siblings carry one per FK (`ix_curriculumgradesubject_curriculumgradeid`,
`ix_curriculumgrade_curriculumversionid`, `ix_curriculumgradesubject_subjectid`). Naming follows them
(`ix_<table>_<full fk column>`), and one index covers a SECOND independently measured access path: the
repository's own `CurriculumTopicPlanRepository.GetByTopic`, called **once per topic** — N single-row
lookups, not a page.

| shape | as shipped | with the index |
|---|---|---|
| the VIEW's `LATERAL` (`tp2`, seq scan per loop) | 34,734 buffers / 134 ms | **3,146 buffers / 4.2 ms** |
| the REPOSITORY's per-topic lookup (`ORDER BY id LIMIT 1`) | **20,922 buffers / 144.98 ms**, `Index Scan using curriculumtopicplan_pkey` + `Rows Removed by Filter: 1050` per loop | **3,153 buffers / 4.553 ms** |

⚠️ **TWO DIFFERENT PLANS FOR TWO DIFFERENT SHAPES, AND THAT IS WHY BOTH WERE MEASURED.** The view's
LATERAL gets a **seq scan** (31 pages x 1,051 loops = 32,581); the repository's `ORDER BY id LIMIT 1`
pins the planner to the **PK index and filters**, walking a growing prefix of it (~19.9 pages x 1,051 =
20,922). Same defect, one index, but a migration justified on the view's numbers alone would have
missed the shape the screen actually issues.

⚠️ **THE JOINED SHAPE IS NOT A DEFECT — RECORDED SO NOBODY RE-QUOTES IT.** Reading every plan in ONE
statement (`LEFT JOIN curriculumtopicplan p ON p.curriculumtopicid = t.id`) is **already 1.431 ms as
shipped**, a `Hash Right Join` over a single 31-page seq scan. An earlier probe of that shape read
**248 ms with a `Materialize` + `Join Filter` removing 2,205,000 rows**, but that plan was FORCED by
`SET LOCAL enable_hashjoin = off; SET LOCAL enable_mergejoin = off` on the harness side. **A shape the
application does not issue, measured under a planner the application does not run, is not a defect.**

⚠️ **`termid` (the table's OTHER FK, also `ON DELETE CASCADE`) WAS PROBED AND REJECTED — do not add it
"for symmetry".** Its RI check reads 2.623 ms -> 2.151 ms (2,131 -> 38 buffers): a wash, because the
31-page table is fully cached. The data settles it — **`count(distinct termid) = 1`**, so a `termid`
index would be ONE key holding all 2,100 rows. The composite `(curriculumtopicid, termid)` was measured
too: 3.338 ms vs 3.603 ms for the single column — no better, just bigger.

**Carried:** applied to `AyraDB` + `ayra_perf`; template rebuilt (`ok V137`, **903 indexes**, `18
migrations recorded, up to V137`); `ayra_e2e` reset from it and the index verified present. A plain
performance index refuses nothing, so it owes no negative phase — but it IS a schema change, so the
whole-suite rung of the ladder applies (**✅ taken alongside `V136`/`V138` - see the run above**).

**New spec:** `curriculum-topic-plan-lookup` (**147** total) — a correlated subquery over
`curriculumgradesubjecttopic`, because that is what gets the planner to the same **`loops=1051`** the N
round trips produce. Reads **6.2-7.3 ms / 1,051 rows / OK** on the shipped index. `--advise` reports
*"Nothing failed, so there was nothing to probe"* — correct, since it only probes FAILING specs.
⚠️ **Its guard is WEAK and the spec says so.** The as-shipped 144.98 ms sits UNDER its own 150 ms
budget, so the catalogue verdict alone would have read a MARGINAL PASS and **never would have found
this**; the finding came from the direct rollback-scoped probe. The budget was left at the repo's stated
interactive bar rather than lowered to manufacture a red row — **tuning a budget so a finding APPEARS is
the same mistake as tuning one so it DISAPPEARS.**

#### ✅ BOTH NEW FAILS WERE `countmode` COSTS — the in-tool A/B was run, and `V135` flipped them

A `psql` probe first suggested these two would pass under `separate` (window 59/145 ms; page 1.7/6.8;
count 32/1.6). ⚠️ **`psql` mimicry is NOT the instrument this repo accepts for a `countmode`
decision** — the `V133` record says so explicitly (a psql probe read `vw_student_academic_risk` at
71 ms against the harness's 176 ms **on the same plan**). The numbers above were therefore treated as
indicative only, and the A/B was re-run **inside the tool**, with each leg as its own spec
(`--only rpt --runs 5`, campus 15, p95, budget 150 ms):

| report | window | sep page | sep count | sep TOTAL | verdict |
|---|---|---|---|---|---|
| `rpt-assessment-performance` | 240-249 | 62-66 | 81-93 | **143-159** | −40%, ON the line — **flipped by `V135`** |
| `rpt-learning-outcome` | 430-487 | 23-30 | 6-7 | **30-35** | **~15x** — **flipped by `V135`** |

**Why learning-outcome's count leg is nearly free (6-7 ms) while assessment's is not (81-93 ms):**
the planner **ELIMINATES** `vw_learning_outcome_performance`'s `LEFT JOIN LATERAL` when the
projection does not ask for `plancount`/`plannedminutes`, and `SELECT COUNT(*)` asks for nothing else.
`vw_assessment_performance` has no such eliminable LATERAL, so its count re-does the real work.

`V135` sets `countmode='separate'` on exactly these two, in BOTH the migration and the seed (above).
Verified on the REBUILT e2e template: **17 total / 13 window / 4 separate**, the four being
`ASSESSMENT_PERFORMANCE`, `FEE_COLLECTION`, `LEARNING_OUTCOME_PERFORMANCE`, `STUDENT_ACADEMIC_RISK`.
`SchoolE2E` J12 phase 11 asserts that set is exactly those four and that a definition editor save
cannot revert it.

#### Two traps this pass paid for

* **⚠️ `GENERATED ALWAYS AS IDENTITY` REFUSES A SUPPLIED ID (`428C9`).** `subject`,
  `curriculumgradesubjecttopic`, `curriculumtopicplan`, `curriculumversion`, `curriculum` and
  `enrollmenttransferhistory` are identity columns — the INSERT must omit the id and read it back.
  Their siblings (`curriculumgrade`, `curriculumgradesubject`, `academicgradesubject`,
  `assessmentcomponent`, `leavetype`, `employeeleaverequest`, `studentsubjectresult`) are
  sequence-backed and DO take one.
* **A CHILD TABLE WITH NO SCOPE COLUMNS CANNOT BE CLEARED BY THE SCOPE HELPER.** `BaseSeeder`'s
  clear-sweep filters `(tenantid, schoolid, campusid)`; `curriculumtopicplan`-style children have none,
  so a scoped DELETE there is `42703`. `ClearTableByParentAsync(child, parentColumn, parentTable, …)`
  clears them by their PARENT's ids instead — the same shape the inventory/library seeders needed.

### The ELEVENTH pass — the timetable module, and the parameter spelling that silently unbound (Sept 2026)

**`TimetableModuleSeeder` + `Tests/TimetableDatasetTests` + twenty-one `tt-*`/`cst-*` specs.** The
module's reads are all `TimetableEntryRepository` / `ClassroomSubjectTeacherRepository` /
`TeacherAbsenceRepository` / `TimeTableReliefRepository` / `TeacherModuleAuditLogRepository`, and every
one of them filters `Timetable.Status = 'Published'` while a screen's own save writes `Draft` - so the
seeder writes the status EXPLICITLY and the fixture asserts the published half rather than the row count
(the same defect class as `attendance.studentenrollmentid`).

**The campus already had an academic spine that belonged to SOMEBODY ELSE.** Exact `count(*)` on the
measured campus read **0 `academicgrade`, 0 `academicgradesubject`, 1 `classroom`, 1 `timetable`**, and
the single classroom carries a NULL `academicyearid` - the two rows the module would have joined to are
another campus's. So the seeder OWNS the spine (find-or-create, never cleared), takes the academic year
from `studentenrollment` rather than from the classroom, and the tool grew eight dedicated
`ScopeVars` (`TimetableClassroomId`/`TimetableId`/`TimetableGradeId`/`TimetableSectionId`/
`TimetableSetupId`/`TimetableTeacherId`/`TimetableWeekDay`/`TimetableDate`) so the specs are handed ids
the seeder stamped instead of whatever `ORDER BY id LIMIT 1` happens to return.

#### ⚠️ `@Name` MATCHED BY EXACT TEXT UNBINDS A STATEMENT WITHOUT FAILING IT

A spec holds TWO statements - the `Sql` transcribed from the repository (lower-case placeholders,
`@tenantid`) and its own `VolumeSql` (camelCase, `@tenantId`) - and one parameter bag serves both.
`BindableFor` filtered on `sql.Contains("@" + key)` with `StringComparison.Ordinal`, so the statement
whose placeholder was spelled differently SILENTLY lost the binding. **PostgreSQL then reads `@tenantId`
as the `@` (absolute value) PREFIX OPERATOR applied to a COLUMN, and that PARSES whenever the query's row
sources expose a column of that name** - `t.tenantid`, `t.schoolid`, `t.campusid` all exist on
`Timetable`, so the scope parameters looked bound while the statement was quietly filtering on
comparisons between a column and itself. It failed with `42703` only for the parameter that has NO
matching column: **nine specs reported ERROR naming a parameter (`column "timetableteacherid" does not
exist`), which is the tell - an error naming a PARAMETER is never a missing table.**
`BindableFor` now resolves the placeholder's OWN spelling out of each statement (whole-placeholder,
case-insensitive) and hands Npgsql that, which is version-proof; the specs were not edited.

#### ⚠️ THE FIXTURE OWES AN `ANALYZE`, AND THE `ANALYZE` MOVED TWO UNRELATED REPORT SPECS

This module is seeded INTO a populated campus (unlike the dataset-wide seeders), so its six extra
`classroom` rows changed the academic-structure joins while the planner's statistics still described the
smaller tables. Before the fixture analyzed them, `rpt-student-attendance` read **156-161 ms**; after,
**21-23 ms** - and `rpt-student-academic-risk-count` went **154-168 ms -> 84-105 ms**. The fixture now
runs `ANALYZE` over the seeder's own `TablesToAnalyze`, which is what makes that list real rather than
descriptive. **Two failures were NOT statistics and stayed failing after every fact table they join was
analyzed explicitly** (`rpt-outstanding-fees`, `rpt-assessment-performance-count`) - which is how the
report closes: a stats artifact disappears under `ANALYZE`, a genuine view cost does not.

**Result: twenty-one specs, every one `OK` (0.2-3.1 ms), `--advise` -> "Nothing failed, so there was
nothing to probe", NO migration owed and no schema change.**

### The TWELFTH pass — the LAST 33 empty grid-driving tables, and the three that must stay empty (Sept 2026)

**The instruction was "seed the remaining grid-driving empty tables", and the pass closed the worklist:
36 empty -> 3, 210 non-empty -> 243.** Every batch follows the same ladder the module passes established
(seeder -> a fixture that asserts the READS' OWN JOINS, never a row count -> specs -> a filtered
`--only <prefix>` measurement), and each one is listed below with its spec prefix. **No batch produced a
migration, and that is the correct outcome**: `--advise` printed *"Nothing failed, so there was nothing to
probe"* for every new family, so no DDL was owed. One APP defect was found and is recorded (not fixed) -
the `users.status` NULL below, which is the most valuable reading of the whole pass.

| batch | seeder | specs | what it unlocked |
|---|---|---|---|
| exam tooling | *already covered* | 7 `exam-*` | the specs existed; the pass only MEASURED them (`--only exam` -> **11 OK / 0 FAIL / 0 SKIP**, 6 tools / 19 items / 11 levels / 6 invigilators) |
| fee money extras | `FeeMoneyExtrasSeeder` | 19 | reported complete before this pass |
| event finance | `EventFinanceSeeder` | 8 `evtfin-*` | 4 charges / 120 participants / 100 student charges |
| HR money + structure details | `HrMoneyDetailSeeder` | 12 (`hrsc-*`, `perf-*`, `hr-payroll-details`, `hr-payroll-adjustments`, `hr-loan-payments`) | `salarycomponent` 8, `employeesalarystructuredetail` 400, `performancekpi` 5, `employeegoal` 30, `employeeperformancedetail` 300, `performancerecommendation` 120, `employeepayrolldetail` 1600, `payrolladjustment` 20, `employeeloanpayment` 150 |
| desk / administrative | `DeskOperationsSeeder` | 12 (`rpt-saved-view-*`, `rpt-run-log-*`, `rpt-export-by-job`, `acct-posting-batch-page`, `rollover-audit-list`, `tenant-subscription`, `bank-file-*`, `hr-meeting-audience`, `identity-*`) | `reportview` 3, `reportrunlog` 60, `reportexport` 3, `postingbatch` 24, `rolloverauditlog` 6, `tenantsubscription` 1, `bankfiletemplate` 3, `bankfileexport` 6, `hrmeetingaudience` 120, `usertwofactor` 1, `userpasswordhistory` 6 |

**New `ScopeVars`, all resolved by the tool as "the BUSIEST row", never `ORDER BY id LIMIT 1`:**
`SalaryStructureId`, `PerformanceReviewId`, `EmployeePayrollId`, `ReportDefinitionId`, `ReportJobId`,
`HrMeetingId` (beside the pre-existing `AssessmentToolId`/`AssessmentToolItemId`/`PerformanceScaleId`/
`LoanId`/`EmployeeId`/`UserId`). Each resolver answers **0** on a campus with no data and the spec then
reports **SKIP**, which is the honest outcome - a spec must not invent a fixture to look green. A new
const `QueryCatalog.PasswordHistoryLookback = 5` mirrors the repository's own default window.

#### ⚠️ MAJOR APP FINDING — `users.Status` NULL MAKES THE USERS GRID RENDER NOBODY

Measured while seeding the identity tables: **all fifteen users of campus 15 carried a NULL `status`**
(the perf seeder omitted the column), and `identity-user-page` - the spec over `UserRepository.GetAll` -
answered **0 rows for a campus that holds fifteen of them**. The cause is three-valued logic:

```sql
AND u.Status <> 'Disabled'   -- NULL <> 'Disabled' is NULL, not TRUE -> the row is EXCLUDED
```

So an account whose status is NULL can **sign in** (`AccountStatusPolicy.CanSignIn` deliberately admits
empty/NULL - it is an allow-list) while being **invisible on the one screen that administers it**. That is
a green reading produced by an empty result, i.e. exactly the failure class this tool exists to catch.
Four things were done: `CommunicationWorkspaceSeeder` now writes `status = 'Active'` with a loud comment;
the fifteen existing rows were backfilled (`UPDATE users SET status='Active' WHERE status IS NULL AND
email LIKE 'perf.%.%@perf.test'` -> `UPDATE 15`); the fixture now asserts `users listed > 0` **under the
grid's own predicate** (not `count(*) > 0`, which would pass on the unfiltered table); and the finding is
written into the spec's `IndexRationale`.

**⚠️ THE APP HALF IS RECORDED, NOT FIXED.** The candidate fixes are `u.Status IS DISTINCT FROM 'Disabled'`
or a `COALESCE(u.status,'')` in the predicate - a change to a shared grid that serves every tenant, so it
is its own reviewed change with its own whole-suite run. Until then a NULL-status account is
administrable only by direct SQL, and the fixture is what will notice if that changes.

#### ⚠️ THREE TABLES ARE DELIBERATELY LEFT EMPTY, AND EACH HAS A MEASURED REASON

`calendarreminderlog`, `teacherparentaction`, `curriculumtopiclearningmaterial`. Seeding them would have
produced rows, a count, and no evidence - so they are **recorded in `DeskOperationsSeeder`'s class comment
and ASSERTED empty by its fixture**, which keeps the census check clean (`+33/-33`, no silent skip):

* **`calendarreminderlog` is a WRITE-ONLY outbox.** `CalendarReminderService.SendReminder` is its only
  writer (`Channel = 'Mock'`); there is no reader in either server, no endpoint and no screen.
* **`teacherparentaction` has NO code reference anywhere** in `school-api` or `school-web` - a dead table.
* **`curriculumtopiclearningmaterial` is a repository with NO CALLER.** `CurriculumTopicLearningMaterialRepository.GetByTopic`
  exists and nothing calls it (grep: the only other appearance is `AttachmentFileRepository`'s orphan
  sweep, which **excludes** its file ids). `CurriculumTopicPlanDto.LearningMaterials` is a property **no
  repository populates**, so the curriculum screen's "Learning Materials" panel is empty for an APP
  reason - and seeding rows there would have HIDDEN that. Recorded as an app gap.

#### Two shape findings the specs carry in their `IndexRationale`

* **`RolloverAuditLogRepository.GetByCampus` passes `limit: 50` and applies it IN MEMORY** (`.Take()` in
  C#), so the SQL has no `LIMIT` - the spec reproduces the statement exactly rather than adding one, which
  is the catalogue's rule (a spec reproduces what the app RUNS).
* **`HrMeetingRepository.GetAudience(meetingId)` takes the meeting id and does NOT filter by scope**, on a
  table that carries only its primary key. The honest index candidate is `(meetingid)` and the fixture
  asserts the reachability instead: every audience row's meeting belongs to its own campus.

**Result: 239 specs = 215 tabular + 24 report; `--runs 3` whole catalogue = 236 OK / 3 FAIL / 0 SKIP.**
The three failures are the long-documented report-view costs, unchanged by this pass and each printing
`suggested index: (none - this query is slow for a reason an index will not fix)`: `rpt-outstanding-fees`
(**357 ms**), `rpt-assessment-performance-count` (**274 ms**) and `rpt-student-attendance` (**188 ms**) -
the last is the standing boundary case (22 ms in one run, 346 ms in another, identical plan).

### The THIRTEENTH pass — the remaining grid endpoints whose tables ALREADY hold rows (Sept 2026)

**The instruction was "spec the remaining grid endpoints whose tables already hold rows", i.e. the
other half of the TWELFTH pass's rule.** That pass seeded the EMPTY grid-driving tables; this one walked
the remaining `WrapSearch(` call sites whose driving table already holds rows on the measured campus and
had no spec. **Five batches, 25 specs, and NOT ONE produced a migration** - every family measured inside
budget, so `--advise` had nothing to probe and no DDL was owed.

| batch | specs | what it covers | measured |
|---|---|---|---|
| A - accounting | 5 (`acct-fiscal-year-page`, `acct-fiscal-period-page`, `acct-account-group-page`, `acct-account-page`, `acct-posting-rule-page`) | the five master grids the accounting tier reads | `--only acct` -> **8 OK / 0 FAIL / 0 SKIP**, 0.4-0.9 ms |
| B - curriculum | 3 (`curriculum-version-page`, `curriculum-grade-page`, `curriculum-grade-subject-page`) | the school-scoped curriculum tree below the version | `--only curriculum` -> **6 OK / 0 FAIL / 0 SKIP** |
| C - inventory | 5 (`inv-asset-page`, `inv-grn-page`, `inv-po-page`, `inv-stock-adjustment-page`, `inv-stock-request-page`) | the procurement/lifecycle desks | `--only inv-` -> **20 OK / 0 FAIL / 0 SKIP** |
| D - fees | 1 (`fee-structure-page`) | the fee-structure grid | `--only fee-` -> **15 OK / 0 FAIL / 0 SKIP**, 0.7 ms |
| E - HR / transport / library / exam | 11 (`designation-page`, `designation-count`, `transport-vehassignment-list`, `transport-route-stop-page`, `lib-acquisition-page`, `lib-acquisition-count`, `lib-reading-list-page`, `lib-reading-list-count`, `moment-type-list`, `exam-schedule-page`, `exam-schedule-count`) | see below | `--only transport` **8 OK**, `--only lib-` **21 OK**, `--only moment` **5 OK**, `--only designation` **2 OK**, `--only exam-schedule` **2 OK** - all 0 FAIL / 0 SKIP |

**Result: 264 specs = 243 tabular + 21 report; `--runs 3` whole catalogue = 262 OK / 2 FAIL / 0 SKIP.**
The two failures are the SAME two report-view costs the TWELFTH pass recorded, on the same budgets and
both printing `suggested index: (none - this query is slow for a reason an index will not fix)`:
`rpt-outstanding-fees` (**337 ms**) and `rpt-assessment-performance-count` (**277 ms**, the
`countmode=separate` count leg `V135` introduced). `rpt-student-attendance` - the TWELFTH pass's third
failure and this file's standing boundary case - read **OK** on this run, which is exactly the behaviour
that earned it the `UNSTABLE` verdict class.

#### What Batch E found — one real shape defect and three shapes worth recording

* **⚠️ THE VEHICLE-ASSIGNMENT DESK IS NOT PAGED, AND ITS `Count` IS THE ROWS IT LOADED.**
  `TransportVehicleAssignmentRepository.GetAll(page, t, s, c)` takes the `DataTablePageInfo` and uses
  NONE of it - no search clause, no `ORDER BY`, no `LIMIT`/`OFFSET` - and sets `data.Count = list.Count`,
  so "page 1, size 50" is the campus's WHOLE assignment list and the grid's search box does nothing.
  The spec was modelled UNPAGED (a `ScopeList`, 15 rows, 0.8/1.0 ms) because that is what the server
  actually sent. **It is the same defect class the transport rider list carries**
  (`TransportStudentAssignmentRepository`, J10 phase 8) - the second instance in one module, and the
  rider list's half was fixed there while this one was recorded rather than fixed, because changing it
  is a paging-contract change on a screen. **✅ FIXED in the FOURTEENTH pass** - the spec is now
  `transport-vehassignment-page` / `transport-vehassignment-count` (see *The FOURTEENTH pass*).
* **The designation grid hides a designation whose Department does not resolve.**
  `DesignationRepository.GetAll(page, ...)` issues the count and the data query through ONE
  `QueryMultipleAsync` and BOTH carry an `INNER JOIN Department` - so a row whose department is missing
  is invisible on the screen while the table holds it. Both halves are specced (`designation-page` /
  `designation-count`) for the reason `lib-fine-count` records: the count re-runs the join, so the user
  waits for it too. The same family as `inv-grn-page`'s INNER JOIN to `InvPurchaseOrder`.
* **The moment-type list is a CTE that ignores paging, and it needs the 0/0/0 branch.**
  `MomentTypeRepository.GetAll(page, ...)` calls the UNPAGED overload and sets `Count = list.Count`, and
  the statement de-duplicates by NAME with a `ROW_NUMBER()` whose rank anticipates SYSTEM-WIDE (0,0,0)
  defaults - which the `WHERE` did not admit until the seventh pass repaired it. It cannot use the
  `ScopeList` factory (a CTE and the four-branch OR are not expressible there), so it is a hand-written
  `QuerySpec`. It is kept even though **no page in `school-web` loads `momenttype`**: it is a live
  `WrapSearch` endpoint any API client can reach.
* **The exam-schedule grid is FOURTEEN LEFT JOINs on BOTH halves and TWO round trips.**
  `selectSql` and `countSql` each carry the same join chain and are issued as separate
  `ExecuteScalarAsync` + `QueryAsync` calls, so both are specced. Two details the spec has to reproduce
  exactly: the paging is **`OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY`** (not `LIMIT`/`OFFSET`),
  and the route is a **POST with a JSON DataTables body**. This is the grid J7 found the campus-drift
  defect on.

**New `ScopeVars`, all resolved by the tool as "the row with the MOST children", never
`ORDER BY id LIMIT 1`:** `CurriculumId`, `CurriculumVersionId`, `CurriculumGradeId` (SCHOOL-scoped -
`curriculumgrade` and `curriculumgradesubject` carry no scope columns at all and hang off their parent)
and `TransportRouteId` (the route with the most stops). Each resolver answers **0** on a scope with no
data and the spec then reports **SKIP**, which is the honest outcome.

#### ⚠️ DELIBERATELY NOT SPECCED, WITH THE REASON (a spec over an empty grid is not coverage)

* **`parent`** - `ParentRepository.GetAllParentUserInfo` is an INNER JOIN to `users` ON THE SAME SCOPE
  TRIPLE. Campus 15 holds **one** `parent` row and **ZERO** users that resolve to it, so the grid is
  EMPTY there and a spec would report SKIP while looking like coverage. Owed a parent-login seeder.
* **`gradingscheme`** - `GradingSchemeController` carries a `WrapSearch`, but the campus holds **0**
  rows (the earlier note that it held 1 was measured on a broader scope, not on campus 15). Same rule.
* **`studentfeeassignment`, `studentfeediscount`, `latefeecharges`** (Batch D) - all three are **0 rows on
  campus 15**; the seeded rows live on campuses 1 and 3. Their repository SQL was read and transcribed
  into the batch's comment so the eventual seeder starts from the shape rather than from scratch.

#### The inventory family's index shape, recorded and NOT fixed

The whole `inv*` family carries only a single-column `idx_*_campus` and no
`(tenantid, schoolid, campusid)` composite - unlike their HR and library siblings, which all follow the
convention. **No index is proposed**, because the rule is that DDL is only earned by a FAILING probe and
every inventory spec measures 4.6 ms or less; it is recorded in the batch's block comment so the next
person reading `V132`'s convention does not have to re-derive why these tables were left alone.

### The FOURTEENTH pass — the vehicle-assignment desk now PAGES and COUNTS its filtered set (Sept 2026)

**The instruction was "fix the transport vehicle-assignment desk so it actually pages and counts its
filtered set", i.e. the shape defect Batch E recorded rather than fixed.** It is the FIRST spec change in
this catalogue that came from an APP fix: the repository moved, so its spec had to move with it ("a spec
reproduces what the app RUNS").

* **`TransportVehicleAssignmentRepository.GetAll(DataTablePageInfo, t, s, c)`** accepted the paging
  envelope and used NONE of it - no search clause, no `ORDER BY`, no `LIMIT`/`OFFSET` - and set
  `data.Count = list.Count`, i.e. the rows it had just loaded rather than the filtered total. So
  "page 1, size 50" was the campus's WHOLE assignment list, `recordsTotal` grew with the list, and the
  grid's search box did nothing.
* **It is fixed by mirroring its sibling** `TransportStudentAssignmentRepository.GetAll(page, ...)` - the
  identical defect J10 phase 8 closed one route over: ONE shared `from`/`where` fragment, the four LEFT
  JOINs carried by BOTH statements (so a search on `v.VehicleNumber` or `r.RouteName` resolves in the
  COUNT too, instead of a `42P01`), the `page.Search && page.WhereCondition` guard that stops an
  `AND ()` 500, a null-safe `page.OrderBy?.Trim()` falling back to `ORDER BY va.Id DESC` (the order the
  unpaged overload already used), `LIMIT`/`OFFSET` guarded by `PageSize > 0`, and `Count` from
  `ExecuteScalarAsync<int>`.
* **The UNPAGED `GetAll(t, s, c)` is untouched and still serves `[HttpGet("list")]`** - a DIFFERENT
  contract (a bare JSON array) with its own consumers. Deleting it is not a tidy-up; it is breaking a
  route the dashboard reads.
* **The spec changed from one unpaged `ScopeList` to a page + count pair** (`transport-vehassignment-page`
  / `transport-vehassignment-count`), reproducing the two statements the method now sends. Measured
  (`--only transport-vehassignment --runs 3 --advise`): **page 1.9 ms / count 1.1 ms, both OK, 15 rows**,
  and `--advise` printed *"Nothing failed, so there was nothing to probe."* - so **no migration was owed
  and no schema changed.**
* **The table's index shape is recorded, not fixed:** `transportvehicleassignment` has NO scope-triple
  index - the closest is `ix_transportvehicleassignment_tenantcampus` (tenant + campus, NO school) while
  its module siblings carry the full `(tenantid, schoolid, campusid)` - and the grid's default sort
  column (`va.Id`) is in no index at all. That is the `invitem` note again: **a missing index is not a
  defect until a failing probe earns it**, and a campus owns TENS of assignments.
* **Result: 265 specs = 244 tabular + 21 report; `--runs 3` whole catalogue = 263 OK / 2 FAIL / 0 SKIP.**
  The 2 failures are unchanged and are still the documented report-view costs, both printing
  `suggested index: (none - this query is slow for a reason an index will not fix)`:
  `rpt-outstanding-fees` (**435 ms** this run) and `rpt-assessment-performance-count` (**264 ms**).

**The HARD RULE was honoured in the same change:** the app fix is carried by `SchoolE2E` - J10 gained
**phase 11** (`Ui/Workflows/TransportWorkflow.VehicleAssignmentPageAsync` + the `VehicleAssignmentPage`
record), which asserts the window, the LIMIT contract, the OFFSET, the ORDER BY with its `va.Id DESC`
fallback, a search over a JOINED column narrowing the FILTERED total, a no-match search reporting 0, and
`recordsTotal` agreeing with the UNPAGED `/list` control. Measured: the affected phases **2/2 (54 s)**
and the whole J10 file **11/11 (1 m 48 s)**, against a rebuilt + restarted Core stack on `ayra_e2e`.

### The FIFTEENTH pass — seeding the five 0-row tables on the measured campus, and the 500 a transcription found (Sept 2026)

**The instruction was "seed the empty on 15 tables", narrowed to the five that are 0-row ON THE CAMPUS
THE TOOL MEASURES.** The catalogue resolves campus **15** (the median campus by student count, 2,000
students), and five of its grid-driving tables held nothing there, so their specs were unreachable even
though the tables existed elsewhere. Seeding them turned ten specs from unmeasurable into measured — and
**the transcription review found a real 500 in one of the five repositories.**

* **`EmptyGridTablesSeeder`** (`data-volume/Seeders/`) fills the five, and it deliberately does NOT
  sweep scope-wide: `CampusesAsync` returns campuses that already hold real data, so `Force` clears
  **stamp-scoped rows only**. It creates a `users` login per carer plus the **Parent role grant** in
  `userrole` (the same grant the app's own create path writes), `gradingscheme` rows with their rules,
  `studentfeeassignment` over DISTINCT enrolments, `studentfeediscount` with percentage XOR amount (the
  approver mirrors the status), and one `latefeesettings` + one `latefeeassessments` + its charge. Every
  assertion is stamp-scoped and asserts the READS' OWN JOINS: the carer grid's `INNER JOIN users` plus
  the role subquery resolves; the fee-assignment chain resolves; each discount's student and discount
  resolve with exactly one value kind; the late-fee invoices and student resolve and the partial unique
  indexes hold. Verified green:
  `SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 dotnet test SchoolDataVolume.csproj
  --no-build --filter "FullyQualifiedName~EmptyGridTablesDataset"` → **Passed 1/1**.

* **⚠️ THE TRANSCRIPTION FOUND A REAL DEFECT, AND IT IS THE ARGUMENT FOR THE RULE "transcription is a
  review".** `ParentRepository.GetAllParentUserInfo`'s page SELECT named **`u.Role`** — a column
  `users` does **NOT** have (roles live on `roles`, granted through `userrole`). PostgreSQL therefore
  rejected the statement at **PARSE time**, so `GET {scope}/parent` was a **500 for every caller, on
  every campus, empty or not**. It survived because the route has **no screen**: `school-web` POSTs to
  `/parent` (the create path) and reads carers back by email or student id, so nothing ever drove the
  DataTables route and an empty grid looks exactly like a 500 to a user.
  * **Fixed with a CORRELATED SUBQUERY** over `userrole`/`roles` (`RoleId = 5`, `IsActive`, `ORDER BY
    ur.Id LIMIT 1`), deliberately **not** a `LEFT JOIN`: `userrole` has no unique index on
    (userid, roleid), so a second active Parent grant would MULTIPLY the row and make the count
    disagree with the page. A scalar subquery cannot multiply, and it is the same role resolution
    `GetParentUserInfoByEmail` performs with its INNER JOINs.
  * **Verified on the running host** (rebuilt AND restarted, per the standing rule):
    `GET api/1/1/1/parent?draw=1&…length=50` answers **200** with `recordsTotal 3` and `role:"Parent"`
    resolved for every row, where the same request used to be a 500.

* **The five tables' index shapes are recorded, not fixed — none earned a migration:** `parent` carries
  only `ix_parent_tenantid` + `ix_parent_userid`; `gradingscheme` only `ix_grading_tenantid` +
  `ix_grading_name`; `studentfeediscount` only `ix_studentdiscount_tenantid` + five single-column join
  indexes; `latefeecharges` has **no scope composite at all**. Every spec measured **≤ 13 ms**, so
  `--advise` had nothing to probe — the `invitem` rule again: **a missing index is not a defect until a
  failing probe earns it.**

* **The ten new specs** (`QueryCatalog.Build`, one documented block): `parent-grid-count` /
  `parent-grid-page` (**2.4 / 7.2 ms**), `grading-scheme-count` / `-page` (**1.7 / 1.3 ms**),
  `fee-assignment-count` / `-page` (**13.3 / 6.4 ms** — the repository is a single `QueryMultipleAsync`
  of count + page, six INNER joins and one LEFT), `fee-discount-count` / `-page` (**10.2 / 6.3 ms** — two
  round trips, `count(DISTINCT sd.Id)`), `late-fee-charge-count` / `-page` (**3.4 / 2.8 ms** — the
  `invoices` table is joined twice).

* **Result: 275 specs = 254 tabular + 21 report; `--runs 3` whole catalogue = 272 OK / 3 FAIL / 0 SKIP.**
  The 3 failures are unchanged and are the documented report-view costs, all printing
  `suggested index: (none - this query is slow for a reason an index will not fix)`:
  `rpt-student-attendance` (154 ms), `rpt-outstanding-fees` (455 ms) and
  `rpt-assessment-performance-count` (324 ms) against the 150 ms report budget.

* ⚠️ **Spec counting is a trap — do not grep for one form.** The factories take the key POSITIONALLY
  (`ScopeGrid(v, "key", …)`, `ScopeList(v, "key", …)`, `ReportSpec(v, "key", …)`), so a `grep -c 'Key = "'`
  undercounts. The reliable total is the UNION of
  `ScopeGrid(v, "` + `ScopeList(v, "` + `ReportSpec(v, "` + `Key = "` → **275**.

**The HARD RULE was honoured in the same change:** the `ParentRepository` fix is an APP change, so it was
carried into `SchoolE2E` — **J3 gained phase 7**
(`Ui/Workflows/StudentLifecycleWorkflow.ParentPageAsync` + the `ParentPage` record), an endpoint-only phase
because the route has no screen. It asserts the endpoint ANSWERS (the regression guard for the parse-time
500), echoes `draw`, reports a `recordsTotal` that agrees with the UNPAGED `/parent/list` control, honours
the LIMIT contract exactly (`rows == min(length, total)`), clamps `length: -1` (DataTables' "All"),
answers NOTHING past the end while the total stays put, narrows the FILTERED total on a search of a JOINED
column and empties it on a no-match term, honours an explicit ORDER BY in both directions, and resolves
the role subquery that replaced `u.Role`. Measured: the phase filtered **1/1 (24 s)** and the whole J3
file (see the run note below), against a rebuilt + restarted Core stack on `ayra_e2e`.

### The SIXTEENTH pass — the JOB QUEUE, the one family with no scope at all (Sept 2026)

**The instruction was "check jobcontroller queries performance handle in school-performance".**
`JobsController` was the last endpoint group in the API with **no spec at all** — a grep for
`BackgroundJob` across `QueryCatalog.cs`, `db-report/Program.cs` and `data-volume/` found nothing — so
the statement the application executes **every sixty seconds for the life of the install** was
unmeasured. Four specs now cover it, and **no migration came out of it**: every shape measured under
budget, and the one candidate index was probed and REJECTED.

* **⚠️ `api/jobs` CARRIES NO SCOPE SEGMENTS, AND THAT IS THE FAMILY'S DEFINING PROPERTY.** The claim is
  `SELECT … LIMIT 1` with **no tenant/school/campus predicate at all** — which is what lets several
  workers race safely, and which is also why the shipped `ix_backgroundjob_tenantschoolcampus` is
  **never used by the hot path**. The campus sweep reads EVERY campus row, because that is what a
  scheduled sweep is. So none of these specs filters by scope, and **their volume gates count the whole
table** — a gate that counted the measured campus would SKIP a query the app issues globally.

* **THE FOUR SPECS** (`QueryCatalog.Build`, one documented block; `--only job --runs 5`):

  | spec | what the user/system is doing | p95 | rows | verdict |
  |---|---|---|---|---|
  | `job-queue-claim` | the worker's claim, drained every minute | **0.4 ms** | 1 | OK |
  | `job-status` | the UI's status poll (`GET api/jobs/{id}`) | **0.2 ms** | 1 | OK |
  | `job-campus-sweep` | the campus list `/billing` and `/postings` both start with | **0.1 ms** | 23 | OK |
  | `job-orphan-sweep` | attachment cleanup — 10-branch `NOT IN` over every referencing table | **0.9 ms** | 0 | OK |

* **⚠️ THE CLAIM'S SPEC IS THE **SUBQUERY**, NOT THE SHIPPED `UPDATE` — AND THE REASON IS MEASURABLE.**
  `BenchmarkRunner.MeasureAsync` opens a session with **no transaction**, and `ExplainAsync` runs
  `EXPLAIN (ANALYZE, …)`, which **executes** its statement too. A spec whose `Sql` were the claim UPDATE
  would therefore genuinely **CLAIM JOBS** in the database being measured — twice per run — and leave a
  queue the application then sees as busy. There is no write anywhere in this catalogue (grep it: **zero**
  `INSERT`/`UPDATE`/`DELETE`), and that is a property to keep rather than a coincidence. The write half
  costs one row modification by primary key; the subquery is what the plan's cost is dominated by, and
  the stress numbers below are for the full UPDATE. Verified rather than argued: the queue's status
  distribution is byte-identical before and after a measurement run (`485 queued / 2 processing / 407
  completed / 8 failed`, and **0** due rows claimed).

* **⚠️ THE OBVIOUS CANDIDATE INDEX WAS PROBED AND REJECTED — `(status, runafter, createdon)` is 2.6x
  WORSE.** The claim is `WHERE Status = 1 AND (LeaseUntil IS NULL OR LeaseUntil < @Now) AND (RunAfter IS
  NULL OR RunAfter <= @Now) ORDER BY CreatedOn ASC LIMIT 1`, so an index carrying the two predicates
  *and* the sort column looks like the answer. Measured in a rolled-back transaction against a synthetic
  **320,207-row** queue (20,200 queued, 201 due):

  | shape | p95 | plan |
  |---|---|---|
  | **as shipped** | **5.269 ms** | `Bitmap Index Scan on ix_backgroundjob_status_lease`, 19,999 rows removed by filter, top-N heapsort |
  | + `(status, runafter, createdon)` | **13.700 ms (2.6x WORSE)** | still `ix_backgroundjob_status_lease` — **the new index was not even chosen** |

  The reason is the shape of the predicate: `(LeaseUntil IS NULL OR LeaseUntil < @Now)` **cannot be
  satisfied by an index range on a nullable column**, so the new index would be scanned in full and
  re-checked on top of the sort. This is the `employeepayroll` rule again — **a missing index is not a
  defect until a MEASURED query would use it**, and here the measured query uses the one that ships.

* **⚠️ THE FINDING THAT MATTERS IS RETENTION, NOT INDEXING: `backgroundjob` IS NEVER PRUNED.** A grep for
  `delete from backgroundjob` over `SchoolResourceServer/` returns **nothing**, so the table is
  append-only for the life of the install. That alone is survivable (the claim measured 5.3 ms at 320k
  rows) — but it is worth knowing *which* set is unbounded, because they are not the same set:
  * **completed rows** grow without bound and cost the claim **nothing** (the index is keyed on
    `status`, so `Status = 1` never touches them);
  * **queued-but-not-yet-due rows** are the set the claim walks, and `CalendarController.EnqueueReminder`
    writes one with a **future `RunAfter`** per calendar item that has a reminder — so a school that
    fills its calendar for the year holds hundreds of standing rows, and a bulk reminder generator would
    put tens of thousands there. The cost is `~0.26 µs per queued row` (5.269 ms ÷ 20,200), i.e. even
    100,000 standing reminders is ~26 ms.
  Recorded rather than fixed: adding retention is an APPLICATION change (a new job, or a bound on
  `CalendarController`), not an index.

* **⚠️ THE SPECS SKIPPED UNTIL THE DATA EXISTED — the unlock was a SEEDER, not another spec.**
  `ayra_perf` held **seven** `backgroundjob` rows and **every one was `Status = 3` (Succeeded)**, so
  `job-queue-claim` — whose gate counts the QUEUED rows, because that is the set its scan walks —
  reported `SKIP (scope holds 0 rows < 1)`: "not measured yet" for the hottest statement in the system.
  **`JobQueueSeeder`** (`data-volume/Seeders/`) is the data pass that comes first, and it seeds the
  queue's three DIFFERENT reasons for its size rather than one padded number:
  * **400 completed** per campus ≈ three years of a campus's own work (one billing job a month, the
    late-fee run, plus the ad-hoc/event/export jobs the modules enqueue) — about **0.4 jobs a day**, not
    a busy system;
  * **480 queued-but-not-yet-due** — the calendar's reminders, one per reminder, written up to a year
    ahead;
  * **5 due** — small on purpose, because the worker drains it every minute;
  * plus **2 processing** (a live lease) and **8 failed** (retries exhausted, with `errormessage` set).

* **⚠️ AND ITS ASSERTIONS ARE THE CLAIM'S OWN PREDICATE, NOT COUNTS — WHICH IS WHAT CATCHES THE
  TIMESTAMP FRAME.** `createdon` is written by SQL (`NOW()`, the DATABASE'S LOCAL time) while
  `leaseuntil` / `startedon` / `completedon` are written from C# (`DateTime.UtcNow`) **in the same
  `timestamp without time zone` columns** — the application's own mixing, reproduced rather than
  invented. `ClaimNextAsync` compares `RunAfter <= @Now` where `@Now` is `DateTime.UtcNow`, so `runafter`
  **has to be written in the UTC frame**: from `now()` it would sit four hours further out on this host,
  every reminder would still be skipped, and a COUNT would pass while the fixture quietly stopped
  exercising the scan. `JobQueueDatasetTests` therefore asserts the predicate itself — the DUE rows are
  claimable and ARE the whole claimable stamped set; the FUTURE rows are **rejected** by it; a live lease
  fails the lease comparison while an expired one passes (stated with the status condition DROPPED, so it
  is an assertion about `LeaseUntil` and not about `status`); the ordered scan picks the OLDEST claimable
  row; and finished/failed rows carry their end state. Verified green:
  `SCUBE_PERF_DATASET=1 SCUBE_PERF_FORCE=1 SCUBE_PERF_MODULE_CAMPUS_LIST=15 dotnet test
  SchoolDataVolume.csproj --no-build --filter "FullyQualifiedName~JobQueueDataset"` → **Passed 1/1**
  (902 rows on campus 15: 485 queued / 5 claimable).
  * **⚠️ The clear is STAMPED, never scoped** (`payload LIKE 'PERF-JOB %'` AND the scope). A scoped
    `DELETE … WHERE campusid = @campusId` would destroy rows the seeder neither wrote nor owns — campus 15
    holds **three real `ReportExport` jobs of its own**.
  * **⚠️ No plan assertion, on purpose.** At ~900 rows the planner **sequential-scans** rather than using
    `ix_backgroundjob_status_lease` (right for that size); pinning a plan here would assert a plan the
    application does not run at this size. The index question was answered at 320k rows instead.

* **⚠️ THE ORPHAN SWEEP'S `ReferencedFileIds` HAS **TEN** BRANCHES, NOT ELEVEN — a note elsewhere in this
  repo said eleven.** `documentsettings.customtemplateattachmentfileid` was **DROPPED in `V129`** with the
  DOCX desk, so a branch naming it would make the query — and therefore the whole sweep — a
  `42703 undefined_column`. The ten are `HomeworkAttachment.{HomeworkAttachmentFileId,
  VideoAttachmentFileId}`, `HomeworkSubmissionAttachment.{HomeworkSubmissionAttachmentFileId,
  VideoSubmissionAttachmentFileId}`, `MomentAttachment.{MomentAttachmentFileId, MomentVideoThumbnailFileId}`,
  `SchoolEventAttachment.EventAttachmentFileId`, `CurriculumTopicLearningMaterial.{LearningAttachmentFileId,
  LearningVideoThumbnailFileId}` and `DocumentSettings.LogoAttachmentFileId`. The spec transcribes all ten
  verbatim (including the `IS NOT NULL` guard each one needs — one NULL makes `NOT IN (…)` evaluate to NULL
  for every row and the sweep then deletes **nothing**).
* **⚠️ NONE OF THE TEN CHILD COLUMNS IS INDEXED, AND AN INDEX IS THE WRONG ANSWER — recorded, not fixed.**
  The catalog agrees (`pg_indexes` filtered on `fileid` over those tables → **zero** rows), but the
  predicate is `NOT IN (<all ten id sets>)`, so the planner must build the **complete union** of
  referenced ids before it can judge a single attachment row: a btree probe per child column would replace
  one sequential scan of a tens-to-hundreds-row table with hundreds of index probes. The sweep measures
  **0.9 ms**. This is the `invoice-overdue-eligible` stance: measured, recorded, **no DDL**.

* **Result: 279 specs = 258 tabular + 21 report; `--runs 3` whole catalogue = 277 OK / 2 FAIL / 0 SKIP.**
  The two failures are the long-documented report-view costs (`rpt-outstanding-fees` 269 ms,
  `rpt-assessment-performance-count` 285 ms, against the 150 ms report budget), both printing
  `suggested index: (none - this query is slow for a reason an index will not fix)`.
  `--advise` on the job family printed **"Nothing failed, so there was nothing to probe."** — which is
  the correct outcome for a module that passes.
  * ⚠️ **`2 FAIL` WHERE THE FIFTEENTH PASS RECORDED `3` IS NOT A FIX — it is `rpt-student-attendance`,
    the documented BOUNDARY case**, which alternates between `OK`, `FAIL` and `UNSTABLE` run to run on the
    same plan (it reads 22-346 ms across runs; the machine effect with a byte-identical plan is recorded in
    *The second census* and `V134`'s notes). Nothing in this pass touched it, and the tool's own verdict
    logic is deliberately NOT tuned to make it green.

* **⚠️ Spec counting, again: `275 → 279` is the UNION** of `ScopeGrid(v, "` + `ScopeList(v, "` +
  `ReportSpec(v, "` + `Key = "`; a `grep -c 'Key = "'` alone reads **217**.

**The HARD RULE is not engaged by this pass:** it adds specs, a seeder and a fixture, and touches no
frontend, backend or schema. No migration was earned, so nothing is owed to `SchoolE2E` or to the test
template.

### Still owed

* ⚠️ **`backgroundjob` HAS NO RETENTION, AND THAT IS AN APPLICATION DECISION, NOT THIS TOOL'S** (recorded by
  the SIXTEENTH pass). Nothing prunes the table, so it is append-only for the life of the install. The
  claim still measures 5.3 ms at 320,207 rows, so this is not a performance defect TODAY — it is a
  question about which set grows without bound, and the answer is not the one that costs money:
  **completed rows** grow forever and cost the claim **nothing** (the index is keyed on `status`), while
  **queued-but-not-yet-due rows** — the calendar's standing reminders, one per reminder, written ahead by
  `CalendarController.EnqueueReminder` — are what the claim walks, at ~0.26 µs each. Adding retention (a
  prune job, or a bound on how far ahead a reminder may be enqueued) is a product/app change.
* ~~⚠️ **THE SIX REPORT SKIPs STILL NEED A SEEDER, NOT A SPEC.**~~ **DONE (Sept 2026) — see
  *The third census* above.** Four seeders (`ExamsModuleSeeder`, `CurriculumModuleSeeder`,
  `HrLeaveSeeder`, `TransferModuleSeeder`) filled the six fact tables and the catalogue now reports
  **0 SKIP**. Four of the six pass; two are over budget for two DIFFERENT reasons, one of which
  (`curriculumtopicplan`'s missing FK index) is a real, cheap fix.**
* ~~⚠️ **`rpt-learning-outcome`'s index is OWED, and it is a DATABASE change.**~~ **SHIPPED —
  `V137__Curriculum_Topic_Plan_Fk_Index.sql`** (`curriculumtopicplan(curriculumtopicid)` takes the
  view from 34,734 to 3,146 buffers, 134 -> 4.2 ms, by removing a seq scan that ran **1,051 times**).
  Applied to `AyraDB` + `ayra_perf`, carried into the rebuilt template (`ok V137`, 903 indexes); the
  index was verified present in all three databases.
* ~~⚠️ **`student-available-page`'s fix is owed in the APPLICATION** (the ordered-`LIMIT` shape), not only
  as an index.~~ **DONE (Sept 2026) — the anti-join landed and the query reads 1.9-6.5 ms.** See
  *FIXED — the shape was the lever, not the index* above (equivalence measured both ways; verified
  through the running app by J2 + J2b + J2c + J3, 19/19).
* ~~`employeepayroll` has **no scope composite** while its siblings all do — same class as `V132`.~~
  **CHECKED, AND NOT A DEFECT (Sept 2026).** That item was inherited rather than measured.
  `employeepayroll` (9,360 rows) really has no `(tenantid, schoolid, campusid)` index — but **every
  read of it is keyed by `Id`, `EmployeeId` or `PayrollPeriodId`, so no query would ever use one.**
  Enumerated (every `TableName.EmployeePayroll` use site): the records grid filters
  `PayrollPeriodId = @P` (`ix_ep_payrollperiodid` / `ix_employeepayroll_periodstatus`), the history grid
  `EmployeeId = @E AND TenantId/SchoolId/CampusId = …` (`ix_ep_employeeid`), `GetPayroll(id)` by PK,
  `GetEligibleEmployees` an anti-join on `(EmployeeId, PayrollPeriodId)`
  (`uix_ep_periodemployee`), the two `TaxConfigRepository` reads by `PayrollPeriodId` / by
  `etr.EmployeeId` with the scope taken off the JOINED `payrollperiod` (not off this table), and the
  rest are writes by PK. The dashboard does not count it either (`dashboard-campus-counts` counts
  `student` and `teacher` only), and `PayrollRepository` has no count-only method.

  **Measured, in a rolled-back transaction — the index cannot help any of them** (campus 15 holds 720
  of 9,360 rows, 7.7%):

  | query | as shipped | with a scope index |
  |---|---|---|
  | the records grid, period 13 — **what the app runs**, probe shape (3 of its 5 joins) | **1.57 ms**, `Index Scan using ix_ep_payrollperiodid` | unchanged |
  | the same query as a SPEC, all five joins (`hr-payroll-records-page`) | **4.4 ms** | — |
  | records-grid count for the same period | **0.05 ms**, `Index Only Scan using uix_ep_periodemployee` | unchanged |
  | *hypothetical* campus-wide count (no such query exists) | 3.35 ms, `Seq Scan` | 0.20 ms, `Index Only Scan` |
  | *hypothetical* campus-wide page `ORDER BY id DESC LIMIT 50` | 0.53 ms, `Index Scan Backward using pk_employeepayroll` | **0.53 ms — the planner kept the PK** |

  So the composite's whole measurable effect is ~3.1 ms on a count **that nothing issues**, and on the
  paged shape it does not even change the plan. It is owed the day a query filters by scope ALONE — a
  campus-wide payroll overview, which the module does not currently have. **Note the pattern: this is
  the second time in this module that a scope composite turned out to be a convention the table did not
  need yet** — `V132`'s four tables were real defects because a scope-first grid existed; this one is not.

  **What the module DID owe was measurement, and it now has it.** The payroll RECORDS grid — the
  module's real volume table, one row per employee per period — was unmeasured, and it is the query the
  argument was about. It has two specs now (`hr-payroll-records-page` / `-count`: **4.4 ms / 0.9 ms** on
  120 rows), taken against the campus's busiest period via a new `ScopeVars.PayrollPeriodId` — because
  the repository takes a `payrollPeriodId` from the PAGE rather than filtering by scope, which is the
  reason it needs no scope index in the first place. Its page and count are **two separate round
  trips** (`ExecuteScalarAsync` then `QueryAsync`), so each gets its own spec.

  The payroll HISTORY grid (`GetPayrollHistoryPage`, filters `EmployeeId` + the scope triple) is
  deliberately **not** specced: it is inherently ~6 rows (1,560 employees share 9,360 records), and a
  real school holds one record per employee per PERIOD, so it is a tens-of-rows query forever. That is
  the same rule that keeps the leave / loan / overtime / contract tables out of the catalogue.
* ~~⚠️ **A big block of tables still hold no rows, and that is the next unlock.**~~ **CLOSED by the
  TWELFTH pass (Sept 2026).** The empty set went 36 -> **3**, and all three survivors have a MEASURED
  reason to be empty rather than a missing seeder: `calendarreminderlog` (write-only outbox, no reader),
  `teacherparentaction` (no code reference anywhere) and `curriculumtopiclearningmaterial` (a repository
  with no caller - see *The TWELFTH pass*). **The worklist is therefore no longer "empty tables": it is
  coverage, i.e. the grid endpoints whose driving table holds rows but that no spec reaches yet.**
  Recount both figures exactly (never `n_live_tup`) before quoting either.
* ⚠️ **A NULL `users.status` hides a user from the screen that administers them** (found by the TWELFTH
  pass). The predicate is `AND u.Status <> 'Disabled'`, and `NULL <> 'Disabled'` is NULL, not TRUE - so
  the row is excluded. The FIXTURE half is done (the seeder writes `'Active'`, the fifteen pre-existing
  rows were backfilled, and the spec asserts `users listed > 0` under the grid's own predicate); **the
  APP half is owed** - a shared grid over `users`, so it is its own reviewed change with its own
  whole-suite run.
* The attendance view's cost **was** the view, and `V134` reshaped it — see *The attendance view* below.
  `STUDENT_ABSENTEE_LIST` is **fixed**: 302 ms → **38-77 ms (OK)**. `STUDENT_ATTENDANCE` improved about
  40% (258 → 96-166 ms) and is now a **boundary case, not a defect** — its server-side `plan exec` is
  100 ms and 49 ms in a warmed `psql` session, while its wall-clock samples straddle the 150 ms budget
  with an identical plan. The reshape is no longer the lever there, and the two alternatives were
  measured and rejected (above).
* Over budget, and **each for its own reason** — after the sixth pass the catalogue's three failures are
  `student-available-page` (8.3 s, a missing index the `--advise` names), `OUTSTANDING_FEES`
  (230-315 ms, the view's own cost) and **`STUDENT_ACADEMIC_RISK`'s count leg**, which the sixth pass
  turned from ~145 ms into a failure by POPULATING `studentfinalresult` — the missing-FK-index defect
  documented under *The sixth pass* above; the index probe reads 167 -> 19 ms. `STUDENT_ATTENDANCE` is
  the standing boundary case whose verdict alternates with the machine's regime (**22 ms** in the
  `--only rpt --runs 5` run below, **346 ms** in a whole-catalogue `--runs 3` run, identical plan), and
  the two the reporting seeding pass made measurable are **fixed**: `V135` flipped
  both to `separate` (`rpt-assessment-performance` 233-238 → **57.8 ms** + its count 77 ms;
  `rpt-learning-outcome` 379-412 → **22.5 ms** + its count 8 ms). **The attendance view problem is
  closed** (`V134`, above), and re-measuring the attendance pair under `separate` is what proved
  `V133`'s verdict still holds **after both `V134` and `V135`** — do not re-run a `countmode` A/B
  without re-measuring first.

### Verified

**LATEST — after the FOURTEENTH pass (Sept 2026): 263 OK / 2 FAIL / 0 SKIP across 265 specs**
(244 tabular + 21 report, `--runs 3`, campus 15). The pass FIXED the one shape defect the THIRTEENTH pass
had recorded: `TransportVehicleAssignmentRepository.GetAll(page, ...)` now pages, searches and counts its
filtered set instead of returning the whole campus list with `Count = list.Count`, and its spec moved from
one unpaged `ScopeList` to the page + count pair the repository now sends (`--only transport-vehassignment`
**2 OK**: page **1.9 ms** / count **1.1 ms**, 15 rows, and `--advise` printed *"Nothing failed, so there was
nothing to probe"*, so **no migration and no schema change**). The two failures are the documented
report-view costs, unchanged: `rpt-outstanding-fees` (**435 ms** this run) and
`rpt-assessment-performance-count` (**264 ms**), both against the 150 ms report budget and both printing
`suggested index: (none - this query is slow for a reason an index will not fix)`. The app half was carried
by `SchoolE2E` J10 phase 11 in the same change (whole J10 file **11/11**, 1 m 48 s) - see *The FOURTEENTH
pass*.

**Previous — after the THIRTEENTH pass (Sept 2026): 262 OK / 2 FAIL / 0 SKIP across 264 specs**
(243 tabular + 21 report, `--runs 3`, campus 15). The five batches measured **25 new specs and not one
FAIL**, so `--advise` had nothing to probe and **the pass wrote no migration and owes no schema change**.
The two failures are the documented report-view costs, unchanged: `rpt-outstanding-fees` (**337 ms**) and
`rpt-assessment-performance-count` (**277 ms**), both against the 150 ms report budget and both printing
`suggested index: (none - this query is slow for a reason an index will not fix)`. `rpt-student-attendance`
- the TWELFTH pass's third failure - read **OK** here, which is the boundary behaviour that earned it the
`UNSTABLE` verdict class. **The one shape defect the pass found is recorded, not fixed**: the transport
vehicle-assignment desk ignores its own paging (see *The THIRTEENTH pass*) - **that defect is FIXED by the
FOURTEENTH pass above**.

**Previous — after the TWELFTH pass (Sept 2026): 236 OK / 3 FAIL / 0 SKIP across 239 specs**
(215 tabular + 24 report, `--runs 3`, campus 15). The empty set is **3** (all three deliberate - see
*The TWELFTH pass*), so the SKIP count of 0 is now a property of the DATA rather than of the spec list.
**All three failures are the documented report-view costs and none is an index problem** -
`rpt-outstanding-fees` (**357 ms**), `rpt-assessment-performance-count` (**274 ms**) and
`rpt-student-attendance` (**188 ms**, the standing boundary case: 22 ms in one whole-catalogue run and
346 ms in another with an IDENTICAL plan) - each against the 150 ms report budget, each printing
`suggested index: (none - this query is slow for a reason an index will not fix)`. `--advise` printed
*"Nothing failed, so there was nothing to probe"* for every family this pass added, so **the pass wrote no
migration and owes no schema change.**

**Previous — after the enrolment-picker shape fix (Sept 2026): 188 OK / 2 FAIL / 0 SKIP across 190 specs**
(169 tabular + 21 report, `--runs 3`, campus 15). `student-available-page` reads **6.5 ms** and its count
half **6.1 ms** (was 336 ms p95 / 640 ms plan exec / 10,060 buffers — see *FIXED — the shape was the
lever* under the second census), so the picker that used to be the catalogue's worst number is now an
ordinary grid. **The two failures are the documented stable pair, neither an index problem**:
`rpt-outstanding-fees` (**264 ms**) and `rpt-assessment-performance-count` (**281 ms**) against the
150 ms report budget, each printing `suggested index: (none - this query is slow for a reason an index
will not fix)`. `rpt-assessment-performance` read `OK ~ UNSTABLE 4x` this run (2.8 -> 52.8 ms) — the
machine-regime class the verdict logic models, not a regression.

**Earlier — after the ELEVENTH pass, 189 specs** (`--runs 3`, campus 15, Sept 2026): **187 OK / 2 FAIL
/ 0 SKIP across 189 specs** (168 tabular + 21 report), and five consecutive whole-catalogue runs with the
SAME code read **187/2 four times and 186/3 once** — the third failure is the standing boundary case, not
a regression, so the pair is the figure to record and never a single FAIL count.
**The two stable failures are NOT index problems** — `rpt-outstanding-fees` (232-306 ms, a
`countmode=window` page over `vw_outstanding_fees`) and `rpt-assessment-performance-count`
(242-287 ms, the `countmode=separate` `SELECT COUNT(*)` over `vw_assessment_performance`, a 6,000-row
scope) — each against the 150 ms report budget, each printing
`suggested index: (none - this query is slow for a reason an index will not fix)`, and each STILL failing
after `ANALYZE` was run explicitly on every fact table they join (`invoices`, `invoiceline`,
`studentassessment`, `studentexam`, `examschedule`, …). **`student-available-page` (278-342 ms against
its own 300 ms budget) remains the boundary case** — the one that comes and goes.
**The twenty-one specs this pass added all read `OK`**, every one between **0.2 and 3.1 ms**
(`--only tt`, `--only cst`, `--runs 5`, plus `tt-template-off-days`), and `--advise` printed *"Nothing
failed, so there was nothing to probe."* — **the ELEVENTH pass therefore wrote NO migration and owes no
schema change.**
⚠️ **AND THE THREE REPORTING FAILURES THE TENTH PASS RECORDED AS STABLE ARE GONE, WHICH IS A FIXTURE
FINDING RATHER THAN A QUERY ONE.** `rpt-student-attendance` went **156-161 ms -> 21-23 ms** and
`rpt-student-academic-risk-count` **154-168 ms -> 84-105 ms** — measured BEFORE and AFTER the new
`TimetableDatasetTests` began running `ANALYZE` over its own `TablesToAnalyze`, which includes
`classroom` and `academicgrade`. The module is seeded INTO a populated campus, so its six extra
classrooms changed the academic-structure joins while the planner's statistics still described the
smaller tables — the *"a performance reading on un-analyzed statistics is not a reading"* rule, one pass
later, in the one seeder family that had no ANALYZE of its own. **Do not read that as "the reports were
fixed": nothing about the views changed.**
**For comparison the previous headline was `164-165 OK / 3-4 FAIL / 0 SKIP across 168 specs` (the TENTH
pass)** — the delta is twenty-one specs over a module that had no data to measure, every one `OK` on
arrival; the `159 OK / 1 FAIL / 0 SKIP across 160 specs` headline above it is two passes back.
**`student-available-page` is the standing boundary case, and `V136` is what made it one** — its
`(tenantid, schoolid, campusid, isactive, name)` index took it from 8.35 s to **258-313 ms** across the
runs recorded here (still straddling its own 300 ms budget, but no longer a `FAIL` for a missing access
path). The application-side ordered-`LIMIT` fix remains owed and is tracked under *Still owed* above.
**The headline that matters is `0 SKIP`**: every grid this tool can reach now has data to measure. The
sixth pass took the SKIP count from 11 to **0** by SEEDING the ten campus master-data tables rather
than by writing more specs over empty ones; the seventh pass continued that (36 more tables across
three module seeder batches — HR workflow, HR money/lifecycle, student profile) and grew the catalogue
by **39 specs**, every one of which measures real rows rather than an empty result that looks fast for
the wrong reason. The report family (`--only rpt --runs 5`) reads **19 OK / 2 FAIL / 0 SKIP across 21
specs**:

```
rpt-grade-performance               36.7    55.8    61.4    0.061    61.4    51.4     50   OK
rpt-assessment-performance          40.4    48.8    57.8    0.058    57.8     4.7     50   OK
rpt-assessment-performance-count    69.0    73.7    77.4    0.077    77.4    99.3  6,000   OK
rpt-learning-outcome                14.7    21.0    22.5    0.022    22.5    18.2     50   OK
rpt-learning-outcome-count           1.6     3.2     8.0    0.008     8.0     5.9  1,051   OK
rpt-outstanding-fees                96.1     203     229    0.229     229     201     50   FAIL (budget 150ms)
```

**Before `V135`** the same two rows read `233-238` and `379-412` ms (both `FAIL`), which is the
change's own before/after: the flip is what closed them.

⚠️ Two whole-catalogue runs in one sitting disagreed about the FAIL count (4, then 3) with the SAME
code and the same data — on this machine a ~150 ms verdict is not stable, which is why the verdict
logic has its own `UNSTABLE` class and why the frozen sample below matters more than the headline.

**Whole catalogue, 72 specs — the second-census close-out** (`--advise --runs 3`, campus 15, Sept 2026):
**64 OK / 2 FAIL / 6 SKIP across 72 specs** — **53 tabular + 19 report**. The sixteen specs added in the
second census all ran (`0 ERROR`): fifteen are `OK`, and the one `FAIL` is `student-available-page`
(8.70 s, above). The two `FAIL`s are that one plus the long-standing `rpt-outstanding-fees` (369 ms);
`rpt-student-academic-risk-count` read **OK** on this run and 160 ms on the previous one — the
documented boundary case, not a fix. Nothing else moved: the 56 specs that existed before read the same
verdicts as the pre-change run (48 OK / 2 FAIL / 6 SKIP), so the new ones are additive and no shared
shape drifted. Sample of the run, for the frozen record:

```
lib-copy-page        1.2    1.5    50   OK      hr-attendance-grid-page    4.5    5.5    50   OK
lib-member-page      3.4    4.1    50   OK      hr-attendance-monthly     17.5   18.5   120   OK
lib-returns-page    16.5   19.4    50   OK      lib-dashboard-copy-counts  0.3    0.4     2   OK
lib-popular-books    7.5    8.6    10   OK      invoice-overdue-eligible   2.9    3.2     0   OK
lib-overdue-list     1.2    3.5   252   OK      student-available-page  8562   8703     0   FAIL
```

**Whole catalogue, 56 specs** (`dotnet run --project school-performance/db-report -- --runs 8`, campus 15):
**47 OK / 3 FAIL / 6 SKIP across 56 specs** — up from 39 OK / 3 FAIL / 6 SKIP across 48, and from 46 OK
across 55 before the transport fix added `tr-assignment-count`, i.e. every new spec passes and nothing else
moved. The report subset (`--only rpt --runs 5`, re-measured in the same pass) is
**10 OK / 3 FAIL / 6 SKIP** across its 19 specs. For comparison,
the same catalogue's 17 window specs measured **6 OK / 5 FAIL / 6 SKIP** — so `V133` closes exactly the
two it claims to and moves nothing else. (This block predates the sixth-module seeding, when the other
four `SKIP`s had no rows; `V135` later closed two more among them once they became measurable.) Both flipped reports pass on BOTH legs of the round trip
(`rpt-student-academic-risk` 15.7 ms + its count 63.7 ms; `rpt-fee-collection` 35.2 ms + its count 66.5 ms).
The 3 remaining failures are `rpt-student-attendance` **159**, `rpt-outstanding-fees` **383** and
`rpt-student-academic-risk-count` **172** ms against a 150 ms budget — characterizable over-budget
reports, not a multi-second pathology, each printing `suggested index: (none …)`. **`rpt-student-absentee`
is no longer among them**: `V134` took it from **302 ms (FAIL) to 38-77 ms (OK)**, and `rpt-student-attendance`
went from 258 to 96-166 — a boundary case whose verdict alternates between `FAIL` and `UNSTABLE`
depending on which regime the machine is in (above), NOT a fix still owed. These numbers **drift
run to run** (the pre-`V133` run read 208 / 245 / 308 for three reports on the same campus, and
`rpt-student-attendance` samples 43-166 ms in a single run), which is why the verdict is "the cost is
the VIEW, not the count mode" rather than a fixed number. The 6
SKIPs are the reports whose fact tables hold no rows on this campus (HR, results, assessments,
transfers) — honest, not silent. No probe index leaked and no transaction was left open.

⚠️ **This is not an index-tuning exercise and nothing here should be run blindly on production.**
`V130` is a plain `CREATE INDEX`, which locks `student` against writes for the build — fine on a
small or quiet table, but on a large write-hot one build it with `CREATE INDEX CONCURRENTLY`
outside a transaction. The migration file has no `BEGIN`/`COMMIT` precisely so that switch needs
no other edit.


D:\SCube\school-performance\
├── README.md
├── data-volume/
│   ├── SchoolDataVolume.csproj
│   ├── appsettings.json
│   ├── Queries/
│   │   ├── QueryProfiler.cs
│   │   └── YourActualQueries.cs
│   ├── Seeders/
│   │   ├── BaseSeeder.cs
│   │   ├── StudentSeeder.cs
│   │   └── AttendanceSeeder.cs
│   └── Tests/
│       ├── StudentQueryTests.cs
│       └── AttendanceQueryTests.cs
├── load-testing/
│   ├── scenarios/
│   │   ├── auth.js
│   │   ├── enrollment.js
│   │   ├── attendance.js
│   │   └── fees.js
│   ├── config/
│   │   └── environments.json
│   └── utils/
│       └── helpers.js
└── k6-v0.50.0-windows-amd64/
Next Steps to Run
Data Volume Tests
# 1. Create perf_test database
CREATE DATABASE perf_test;

# 2. Update connection string in appsettings.json

# 3. Run tests
cd D:\SCube\school-performance\data-volume
dotnet test
Load Tests
# 1. Start API server (auth on :8086, resource on :8087)

# 2. Run load tests
cd D:\SCube\school-performance\load-testing
D:\SCube\k6-v0.50.0-windows-amd64\k6.exe run scenarios/auth.js