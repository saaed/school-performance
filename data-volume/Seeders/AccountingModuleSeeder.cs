using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="AccountingModuleSeeder"/>.</summary>
public sealed class AccountingSeedOptions
{
    /// <summary>Monthly periods generated for the campus's fiscal year.</summary>
    public int FiscalPeriods { get; set; } = 12;

    /// <summary>
    /// Accounts in the chart. Enough to group postings, small enough that the ledger drill-down
    /// still reads like a real chart.
    /// </summary>
    public int AccountsPerCampus { get; set; } = 30;

    /// <summary>
    /// Posted journal entries. THE volume table - `accounting.journal` pages it, and the
    /// trial balance / P&amp;L / balance sheet aggregate over it.
    /// </summary>
    public int JournalEntriesPerCampus { get; set; } = 1500;

    /// <summary>Posting-monitor rows, generated for a subset of the journal entries.</summary>
    public int FinancialPostingsPerCampus { get; set; } = 500;

    /// <summary>Re-seed even when the campus already holds accounting rows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's accounting seed produced.</summary>
public sealed class AccountingSeedResult
{
    public bool Skipped { get; set; }
    public int FiscalYears { get; set; }
    public int FiscalPeriods { get; set; }
    public int AccountGroups { get; set; }
    public int Accounts { get; set; }
    public int JournalEntries { get; set; }
    public int JournalLines { get; set; }
    public int FinancialPostings { get; set; }
}

/// <summary>
/// Seeds the ACCOUNTING module's tables for one campus.
///
/// WHY THIS EXISTS
/// ---------------
/// All seven accounting tables held ZERO rows in `ayra_perf`, so the journal grid, the posting
/// monitor and all three financial statements were unmeasurable - every spec SKIPped. This is the
/// module where that gap costs the most, because its statements are AGGREGATES over the journal:
/// a trial balance that reads four rows teaches nothing about one that reads 1.1M.
///
/// WHICH TABLES, AND WHY
/// ---------------------
///   * fiscalyear + fiscalperiod: `journalentry.fiscalperiodid` is NOT NULL, and the period is what
///     the "entry dated into a closed period" rule is about.
///   * accountgroup + account: `account.accountgroupid` is NOT NULL. The chart is seeded with a
///     spread of types, because the balance sheet's shape (assets = liabilities + equity) and the
///     P&amp;L section both depend on which types exist - a chart of only Asset rows makes both
///     statements measure half a document.
///   * journalentry + journalentryline: THE volume. One entry per line pair.
///   * financialposting: the posting monitor's grid (a JSONB `payload` per row).
///
/// ⚠️ `chk_debit_credit` requires **debit xor credit** on every line (at least one positive, never
/// both). So each entry is written as a balanced PAIR - one debit line, one credit line on a
/// different account - which satisfies the constraint per line AND keeps the statements balanced.
/// A seeder that wrote debit = credit = amount on one row would be rejected by the database.
///
/// ⚠️ `chk_journal_status` / `chk_posting_status` / `chk_account_type` / `chk_normal_balance` are
/// enumerations; the values below are the ones the check constraints allow.
///
/// ⚠️ `ux_financialposting_source` is a PARTIAL unique index on
/// (tenantid, schoolid, campusid, sourcetype, sourceid, postingtype) - excluding
/// 'LibraryFinePayment'. The postings below key on their own journal entry's id, so every row is
/// distinct by construction and a re-seed cannot collide.
///
/// ⚠️ THE SCOPE INDEXES ALREADY EXIST (`idx_journalentry_scope`, `idx_journalentryline_entry`,
/// `idx_financialposting_scope`, `idx_fiscalperiod_dates`, ...). Measuring this module is therefore
/// as much about proving those are USED as about finding a missing one.
/// </summary>
public sealed class AccountingModuleSeeder : BaseSeeder
{
    public AccountingModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk accounting load invalidates.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "fiscalyear", "fiscalperiod", "accountgroup", "account",
        "journalentry", "journalentryline", "financialposting"
    };

    private static readonly string[] GroupNames =
    {
        "Current Assets", "Fixed Assets", "Current Liabilities", "Equity", "Revenue", "Expenses"
    };

    private static readonly string[] GroupTypes =
    {
        "Asset", "Asset", "Liability", "Equity", "Income", "Expense"
    };

    private static readonly string[] AccountNames =
    {
        "Cash in Hand", "Bank - Operating", "Fees Receivable", "Stationery Expense",
        "Salaries Expense", "Utilities Expense", "Tuition Income", "Transport Income",
        "Furniture & Fixtures", "Accumulated Depreciation", "Accounts Payable", "Retained Earnings",
        "Maintenance Expense", "Event Income", "Prepaid Expenses", "Petty Cash"
    };

    /// <summary>(accountType, normalBalance) per account name, so the chart is coherent.</summary>
    private static readonly (string Type, string Normal)[] AccountKinds =
    {
        ("Asset", "Debit"), ("Asset", "Debit"), ("Asset", "Debit"), ("Expense", "Debit"),
        ("Expense", "Debit"), ("Expense", "Debit"), ("Income", "Credit"), ("Income", "Credit"),
        ("Asset", "Debit"), ("Asset", "Credit"), ("Liability", "Credit"), ("Equity", "Credit"),
        ("Expense", "Debit"), ("Income", "Credit"), ("Asset", "Debit"), ("Asset", "Debit")
    };

    public async Task<AccountingSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, AccountingSeedOptions options, bool verbose = true)
    {
        var result = new AccountingSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM fiscalyear
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                 AND name LIKE @prefix",
            new { tenantId, schoolId, campusId, prefix = $"PERF-FY-{campusId}-%" });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.FiscalYears = (int)existing;
            if (verbose)
                Console.WriteLine($"  Accounting: campus {campusId} already holds {existing} fiscal years - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // Children first: journalentryline -> journalentry -> account -> accountgroup, and
        // journalentry -> fiscalperiod -> fiscalyear. `financialposting.journalentryid` is nullable
        // but the monitor's join reads it, so it is cleared alongside.
        if (options.Force && existing > 0)
        {
            // ⚠️ THE MODULE'S OWN IMMUTABILITY RULE HAS TO BE RESPECTED, NOT SIDESTEPPED.
            // `trg_journal_entry_line_immutable` is a BEFORE DELETE trigger on `journalentryline`
            // that refuses to delete a line whose entry is `Posted`:
            //     P0001: Cannot delete lines of a posted journal entry (line 1)
            // A posted journal IS immutable - that is the point of the trigger - so a rebuild moves
            // the campus's own entries back to `Draft` first, which is the state in which they may
            // be rewritten. Disabling the trigger would be the wrong fix: the next seed would then
            // silently write data the application considers impossible.
            await conn.ExecuteAsync(
                @"UPDATE journalentry SET status = 'Draft', modifiedon = @now
                   WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
                     AND status <> 'Draft'",
                new { tenantId, schoolId, campusId, now });

            // ⚠️ `journalentryline` has NO scope columns (only `journalentryid`), so it is cleared
            // through its parent - and BEFORE that parent, or the delete has nothing to select from.
            await ClearTableByParentAsync(
                conn, "journalentryline", "journalentryid", "journalentry",
                tenantId, schoolId, campusId);

            foreach (var table in new[]
                     {
                         "financialposting", "journalentry",
                         "account", "accountgroup", "fiscalperiod", "fiscalyear"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
                Console.WriteLine($"  Accounting: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. The fiscal year and its monthly periods.
        //    ⚠️ `isclosed = true` on every period EXCEPT the one containing today: the close
        //    branch is a real rule ("an entry dated into a closed period is refused"), and a
        //    calendar where nothing is closed cannot exercise it. Today's period stays open so
        //    the entries below are legitimately postable.
        // ------------------------------------------------------------------
        var yearStart = new DateTime(DateTime.Today.Year, 1, 1);
        var yearEnd = new DateTime(DateTime.Today.Year, 12, 31);

        var fiscalYearId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO fiscalyear
                  (tenantid, schoolid, campusid, name, startdate, enddate, periodnumber,
                   isclosed, isactive, createdby, modifiedby, createdon, modifiedon)
              VALUES (@tenantId, @schoolId, @campusId, @name, @start::date, @end::date, @periods,
                      false, true, 1, 1, @now, @now)
              RETURNING id",
            new
            {
                tenantId, schoolId, campusId,
                name = $"PERF-FY-{campusId}-{yearStart.Year}",
                start = yearStart.Date,
                end = yearEnd.Date,
                periods = options.FiscalPeriods,
                now
            });
        result.FiscalYears = 1;

        var periodStartMonths = System.Linq.Enumerable.Range(0, options.FiscalPeriods)
            .Select(i => new DateTime(DateTime.Today.Year, 1, 1).AddMonths(i))
            .ToList();

        var periodIds = new List<long>();
        for (var i = 0; i < options.FiscalPeriods; i++)
        {
            var start = periodStartMonths[i];
            var end = start.AddMonths(1).AddDays(-1);
            var containsToday = start <= DateTime.Today && DateTime.Today <= end;

            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO fiscalperiod
                      (tenantid, schoolid, campusid, fiscalyearid, name, startdate, enddate,
                       isclosed, isactive, createdby, modifiedby, createdon, modifiedon)
                  VALUES (@tenantId, @schoolId, @campusId, @fiscalYearId, @name, @start::date, @end::date,
                          @closed, true, 1, 1, @now, @now)
                  RETURNING id",
                new
                {
                    tenantId, schoolId, campusId, fiscalYearId,
                    name = start.ToString("MMM yyyy"),
                    start = start.Date,
                    end = end.Date,
                    closed = !containsToday,
                    now
                });

            periodIds.Add(id);
        }
        result.FiscalPeriods = periodIds.Count;

        if (periodIds.Count == 0)
            throw new InvalidOperationException(
                $"Accounting seeding wrote no fiscal periods for campus {campusId}, so its journal " +
                "cannot be seeded (journalentry.fiscalperiodid is NOT NULL).");

        var openPeriodId = periodIds[^1];

        // ------------------------------------------------------------------
        // 2. The chart of accounts.
        // ------------------------------------------------------------------
        var groupIds = (await conn.QueryAsync<long>(
            @"INSERT INTO accountgroup
                  (tenantid, schoolid, campusid, name, code, parentgroupid, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Codes), NULL, true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, GroupNames.Length),
                SchoolIds = Repeat(schoolId, GroupNames.Length),
                CampusIds = Repeat(campusId, GroupNames.Length),
                Names = GroupNames,
                Codes = System.Linq.Enumerable.Range(0, GroupNames.Length)
                    .Select(i => $"PERF-GRP-{campusId}-{(i + 1) * 1000:D5}").ToArray(),
                now
            })).ToList();
        result.AccountGroups = groupIds.Count;

        var accountCount = options.AccountsPerCampus;
        var accounts = await conn.QueryAsync<long>(
            @"INSERT INTO account
                  (tenantid, schoolid, campusid, accountgroupid, parentaccountid, code, name,
                   accounttype, normalbalance, issystem, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@GroupIds), NULL, unnest(@Codes), unnest(@Names),
                     unnest(@Types), unnest(@Normals), false, true,
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, accountCount),
                SchoolIds = Repeat(schoolId, accountCount),
                CampusIds = Repeat(campusId, accountCount),
                GroupIds = System.Linq.Enumerable.Range(0, accountCount)
                    .Select(i => groupIds[i % groupIds.Count]).ToArray(),
                Codes = System.Linq.Enumerable.Range(0, accountCount)
                    .Select(i => $"PERF-ACC-{campusId}-{(i + 1) * 10:D6}").ToArray(),
                Names = System.Linq.Enumerable.Range(0, accountCount)
                    .Select(i => $"{AccountNames[i % AccountNames.Length]} {i / AccountNames.Length + 1}")
                    .ToArray(),
                Types = System.Linq.Enumerable.Range(0, accountCount)
                    .Select(i => AccountKinds[i % AccountKinds.Length].Type).ToArray(),
                Normals = System.Linq.Enumerable.Range(0, accountCount)
                    .Select(i => AccountKinds[i % AccountKinds.Length].Normal).ToArray(),
                now
            });
        var accountIds = accounts.ToList();
        result.Accounts = accountIds.Count;

        if (accountIds.Count < 2)
            throw new InvalidOperationException(
                $"Accounting seeding wrote {accountIds.Count} accounts for campus {campusId}; a balanced " +
                "journal entry needs at least two.");

        // ------------------------------------------------------------------
        // 3. The journal - THE volume table.
        // ------------------------------------------------------------------
        var journal = await SeedJournalAsync(
            conn, tenantId, schoolId, campusId, accountIds, openPeriodId, periodIds,
            options.JournalEntriesPerCampus, now, verbose);
        result.JournalEntries = journal.Entries;
        result.JournalLines = journal.Lines;

        // ------------------------------------------------------------------
        // 4. The posting monitor.
        // ------------------------------------------------------------------
        result.FinancialPostings = await SeedFinancialPostingsAsync(
            conn, tenantId, schoolId, campusId, journal.EntryIds,
            options.FinancialPostingsPerCampus, now);

        if (verbose)
        {
            Console.WriteLine(
                $"  Accounting: campus {campusId} -> 1 fiscal year (+{result.FiscalPeriods} periods), " +
                $"{result.AccountGroups} groups, {result.Accounts} accounts, " +
                $"{result.JournalEntries:N0} journal entries (+{result.JournalLines:N0} lines), " +
                $"{result.FinancialPostings:N0} postings");
        }

        return result;
    }

    private static long[] Repeat(long value, int count)
        => System.Linq.Enumerable.Repeat(value, count).ToArray();

    private async Task<(int Entries, int Lines, List<long> EntryIds)> SeedJournalAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> accountIds, long openPeriodId, List<long> periodIds,
        int count, DateTime now, bool verbose)
    {
        var entryIds = new List<long>(count);
        var insertedLines = 0;
        const int batchSize = 1000;

        for (var start = 0; start < count; start += batchSize)
        {
            var take = Math.Min(batchSize, count - start);
            var range = System.Linq.Enumerable.Range(start, take).ToArray();

            // Most entries land in the OPEN period (they have to be postable); every fifth lands in
            // an earlier period so the closed-period branch has data behind it.
            var periods = range.Select(i => i % 5 == 4
                ? periodIds[i % Math.Max(1, periodIds.Count - 1)]
                : openPeriodId).ToArray();

            var amounts = range.Select(i => 100m + (i % 200) * 25m).ToArray();

            var ids = (await conn.QueryAsync<long>(
                @"INSERT INTO journalentry
                      (tenantid, schoolid, campusid, fiscalperiodid, journalnumber, entrydate,
                       reference, description, sourcetype, sourceid, status, postedon,
                       createdby, modifiedby, createdon, modifiedon)
                  SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                         unnest(@PeriodIds), unnest(@Numbers), unnest(@Dates)::date,
                         unnest(@References), unnest(@Descs), 'ManualJournal', unnest(@SourceIds),
                         unnest(@Statuses), unnest(@PostedOn), 1, 1, @now, @now
                  RETURNING id",
                new
                {
                    TenantIds = Repeat(tenantId, take),
                    SchoolIds = Repeat(schoolId, take),
                    CampusIds = Repeat(campusId, take),
                    PeriodIds = periods,
                    Numbers = range.Select(i => $"PERF-JV-{campusId:D2}-{i + 1:D7}").ToArray(),
                    Dates = range.Select(i => DateTime.Today.AddDays(-(i % 300)).Date).ToArray(),
                    References = range.Select(i => $"PERF-REF-{campusId}-{i + 1:D7}").ToArray(),
                    Descs = range.Select(i => $"PERF journal entry {i + 1:D6}").ToArray(),
                    // `sourceid` mirrors the entry's own ordinal IN THE BATCH - it is only required
                    // to be non-null and distinct per posting row, which the financial postings below
                    // key on.
                    SourceIds = range.Select(i => (long)(i + 1)).ToArray(),
                    // Most entries are Posted (the ledger and the statements read posted entries);
                    // the rest are Draft/Reversed so the grid's status filter has branches.
                    Statuses = range.Select(i => (i % 9) switch
                    {
                        7 => "Draft",
                        8 => "Reversed",
                        _ => "Posted"
                    }).ToArray(),
                    PostedOn = range.Select(i => DateTime.Today.AddDays(-(i % 300))).ToArray(),
                    now
                })).ToList();

            entryIds.AddRange(ids);

            // Two lines per entry: one debit, one credit, on different accounts and for the SAME
            // amount - balanced, and each line satisfies `chk_debit_credit` (debit xor credit).
            var lineCount = ids.Count * 2;
            var debitAccounts = System.Linq.Enumerable.Range(0, ids.Count)
                .Select(i => accountIds[(start + i) % accountIds.Count]).ToArray();
            var creditAccounts = System.Linq.Enumerable.Range(0, ids.Count)
                .Select(i => accountIds[((start + i) + 1 + accountIds.Count / 2) % accountIds.Count])
                .ToArray();

            var entryIdCol = ids.SelectMany(id => new[] { id, id }).ToArray();
            var accountCol = debitAccounts.Concat(creditAccounts).ToArray();
            // Interleave so index 2k is the debit and 2k+1 the credit of entry k.
            var debits = new decimal[lineCount];
            var credits = new decimal[lineCount];
            for (var i = 0; i < ids.Count; i++)
            {
                debits[i * 2] = amounts[i];
                credits[i * 2 + 1] = amounts[i];
            }

            insertedLines += await conn.ExecuteAsync(
                @"INSERT INTO journalentryline
                      (journalentryid, accountid, debit, credit, description, lineorder,
                       reversedjournalentryid, createdby, modifiedby, createdon, modifiedon)
                  SELECT unnest(@EntryIds), unnest(@AccountIds), unnest(@Debits), unnest(@Credits),
                         unnest(@Descs), unnest(@Orders), NULL, 1, 1, @now, @now",
                new
                {
                    EntryIds = entryIdCol,
                    AccountIds = accountCol,
                    Debits = debits,
                    Credits = credits,
                    Descs = System.Linq.Enumerable.Range(0, lineCount)
                        .Select(i => $"PERF line {i + 1}").ToArray(),
                    Orders = System.Linq.Enumerable.Range(0, lineCount)
                        .Select(i => (i % 2) + 1).ToArray(),
                    now
                });

            if (verbose) LogProgress($"  Accounting journal (campus {campusId})",
                entryIds.Count, count);
        }

        if (verbose)
        {
            LogProgress($"  Accounting journal (campus {campusId})", entryIds.Count, count);
            Console.WriteLine($"  Accounting journal lines (campus {campusId}): {insertedLines:N0}");
        }

        return (entryIds.Count, insertedLines, entryIds);
    }

    private static async Task<int> SeedFinancialPostingsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> entryIds, int count, DateTime now)
    {
        if (entryIds.Count == 0) return 0;

        // One posting per entry, so `ux_financialposting_source` cannot collide: the key is
        // (tenant, school, campus, sourcetype, sourceid, postingtype) and `sourceid` is the
        // entry's own id.
        var take = Math.Min(count, entryIds.Count);
        var slice = entryIds.Take(take).ToArray();

        return await conn.ExecuteAsync(
            @"INSERT INTO financialposting
                  (tenantid, schoolid, campusid, sourcetype, sourceid, postingtype,
                   journalentryid, status, payload, errormessage, retrycount, maxretries,
                   requestedon, processedon, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     'ManualJournal', unnest(@SourceIds), 'Create',
                     unnest(@EntryIds), unnest(@Statuses), unnest(@Payloads)::jsonb,
                     unnest(@Errors), unnest(@Retries), 3,
                     unnest(@Requested), unnest(@Processed), 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, take),
                SchoolIds = Repeat(schoolId, take),
                CampusIds = Repeat(campusId, take),
                SourceIds = slice,
                EntryIds = slice.Select(id => (long?)id).ToArray(),
                // The monitor's grid filters by status, so all four states are represented -
                // and a Failed row carries the message the screen renders.
                Statuses = System.Linq.Enumerable.Range(0, take)
                    .Select(i => (i % 5) switch
                    {
                        0 => "Pending",
                        1 => "Failed",
                        2 => "Reversed",
                        _ => "Posted"
                    }).ToArray(),
                Payloads = System.Linq.Enumerable.Range(0, take)
                    .Select(i => $"{{\"description\":\"PERF posting {i + 1}\",\"source\":\"ManualJournal\",\"amount\":{100 + i % 50 * 25}}}")
                    .ToArray(),
                Errors = System.Linq.Enumerable.Range(0, take)
                    .Select(i => i % 5 == 1
                        ? "PERF simulated posting failure: destination account is inactive."
                        : null).ToArray(),
                Retries = System.Linq.Enumerable.Range(0, take)
                    .Select(i => i % 5 == 1 ? 1 : 0).ToArray(),
                Requested = System.Linq.Enumerable.Range(0, take)
                    .Select(i => DateTime.Today.AddDays(-(i % 120)).AddHours(9)).ToArray(),
                Processed = System.Linq.Enumerable.Range(0, take)
                    .Select(i => i % 5 == 0
                        ? (DateTime?)null
                        : DateTime.Today.AddDays(-(i % 120)).AddHours(10)).ToArray(),
                now
            });
    }
}
