using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="LibraryModuleSeeder"/>.</summary>
public sealed class LibrarySeedOptions
{
    /// <summary>Titles in the catalogue.</summary>
    public int BooksPerCampus { get; set; } = 300;

    /// <summary>Physical copies per title. The circulation ledger is per COPY, so this is the axis.</summary>
    public int CopiesPerBook { get; set; } = 2;

    /// <summary>Members registered from the campus's own students.</summary>
    public int MembersPerCampus { get; set; } = 500;

    /// <summary>
    /// Issues per member. The circulation desk is the module's volume table - one row per
    /// loan, and the `library.issue` grid pages it ordered by IssuedDate DESC.
    /// </summary>
    public int IssuesPerMember { get; set; } = 4;

    public int ReservationsPerCampus { get; set; } = 100;
    public int ReadingListsPerCampus { get; set; } = 10;
    public int AcquisitionsPerCampus { get; set; } = 30;
    public int InventoryAuditsPerCampus { get; set; } = 200;

    /// <summary>Re-seed even when the campus already holds library rows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's library seed produced.</summary>
public sealed class LibrarySeedResult
{
    public bool Skipped { get; set; }
    public int Categories { get; set; }
    public int Authors { get; set; }
    public int Publishers { get; set; }
    public int Vendors { get; set; }
    public int Tiers { get; set; }
    public int Books { get; set; }
    public int BookAuthors { get; set; }
    public int Copies { get; set; }
    public int Members { get; set; }
    public int Issues { get; set; }
    public int Fines { get; set; }
    public int Reservations { get; set; }
    public int ReadingLists { get; set; }
    public int ReadingListItems { get; set; }
    public int Acquisitions { get; set; }
    public int InventoryAudits { get; set; }
}

/// <summary>
/// Seeds the LIBRARY module's volume tables for one campus.
///
/// WHY THIS EXISTS
/// ---------------
/// All 17 `library*` tables held ZERO rows in `ayra_perf`, so every library spec reported SKIP and
/// the 14 `Library*` repositories were unmeasurable. Coverage of a module is gated on VOLUME.
///
/// WHICH TABLES, AND WHY
/// ---------------------
///   * REFERENCE: librarycategory, libraryauthor, librarypublisher, libraryvendor,
///     librarymembershiptier. The tier matters beyond FK completeness: `finerateperday` is what the
///     circulation service computes an overdue fine from, so a campus with no tier charges nothing.
///   * CATALOGUE: librarybook (+ librarybookauthor) and librarybookcopy. The COPY is the unit the
///     whole module moves - a loan is per copy, and the copy's `status` (Available / Issued / Lost)
///     is what the desk reads.
///   * VOLUME: libraryissue (one row per loan) and libraryfine (one per overdue loan). These are the
///     two tables the circulation and fines grids page over.
///   * DOCUMENTS: libraryreservation, libraryreadinglist(+item), libraryacquisition,
///     libraryinventoryaudit.
///
/// ⚠️ MEMBERS ARE DRAWN FROM THE CAMPUS'S OWN STUDENTS, not fabricated. `librarymember.studentid`
/// would otherwise point at nothing, and every issue's member name is resolved through it by the
/// circulation query (`GetActiveIssuesPaged` joins `Student st ON st.Id = lm.StudentId`) - a member
/// with a dangling student id makes that join's output look empty.
///
/// ⚠️ `librarybookcopy.barcode` is GLOBALLY unique (not per campus), so the codes below carry the
/// campus id. `librarymember` is unique per (tenant, school, campus, membernumber).
///
/// ⚠️ THE ISSUES SPREAD ACROSS STATES ON PURPOSE: `Issued` rows carry a due date in the future,
/// `Issued` rows past due give the desk something to charge a fine for, and `Returned` rows give the
/// recent-returns grid rows. A ledger where every row is one state measures one branch of the screen.
/// </summary>
public sealed class LibraryModuleSeeder : BaseSeeder
{
    public LibraryModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk library load invalidates.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "librarycategory", "libraryauthor", "librarypublisher", "libraryvendor",
        "librarymembershiptier", "librarybook", "librarybookauthor", "librarybookcopy",
        "librarymember", "libraryissue", "libraryfine", "libraryreservation",
        "libraryreadinglist", "libraryreadinglistitem", "libraryacquisition",
        "libraryinventoryaudit"
    };

    private static readonly string[] CategoryNames =
    {
        "Fiction", "Non-Fiction", "Reference", "Science", "Mathematics",
        "History", "Children", "Periodicals"
    };

    private static readonly string[] TierNames = { "Student", "Staff", "Premium" };

    private static readonly string[] TitleWords =
    {
        "Foundations of", "Introduction to", "Advanced", "Practical", "Essential",
        "Modern", "Complete Guide to", "Principles of", "Applied", "Elements of"
    };

    private static readonly string[] SubjectWords =
    {
        "Algebra", "Physics", "Chemistry", "World History", "Grammar", "Geography",
        "Biology", "Computer Science", "Literature", "Economics", "Statistics", "Art"
    };

    public async Task<LibrarySeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, LibrarySeedOptions options, bool verbose = true)
    {
        var result = new LibrarySeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM librarybook
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.Books = (int)existing;
            if (verbose)
                Console.WriteLine($"  Library: campus {campusId} already holds {existing:N0} books - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // Children first: libraryissue points at both the copy and the member, libraryfine at the
        // issue, and librarybookcopy at the book - so the parents cannot be deleted first.
        if (options.Force && existing > 0)
        {
            // ⚠️ `libraryreadinglistitem` and `librarybookauthor` carry NO scope columns - only
            // their parent's id - so they are cleared THROUGH the parent, and before it. The
            // generic helper would answer 42703 there, which is not the FK refusal it tolerates.
            await ClearTableByParentAsync(conn, "libraryreadinglistitem", "readinglistid",
                "libraryreadinglist", tenantId, schoolId, campusId);
            await ClearTableByParentAsync(conn, "librarybookauthor", "bookid",
                "librarybook", tenantId, schoolId, campusId);

            foreach (var table in new[]
                     {
                         // ⚠️ `librarymember` MUST be in this list. It was missing first time round,
                         // so a forced re-seed appended a SECOND set of members under the same
                         // `uq_librarymember_numberpercampus` and died with 23505 - and the same
                         // number is what the member grid shows, so the duplicate would have been
                         // invisible if the constraint had not been there.
                         "libraryfine", "libraryissue", "libraryreservation", "librarymember",
                         "libraryinventoryaudit", "libraryacquisition",
                         "libraryreadinglist", "librarybookcopy", "librarybook",
                         "librarymembershiptier", "libraryvendor", "librarypublisher",
                         "libraryauthor", "librarycategory"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
                Console.WriteLine($"  Library: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. Reference tier.
        // ------------------------------------------------------------------
        var categoryIds = (await conn.QueryAsync<long>(
            @"INSERT INTO librarycategory
                  (tenantid, schoolid, campusid, name, code, description, displayorder, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Codes), unnest(@Descs), unnest(@Orders), true,
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, CategoryNames.Length),
                SchoolIds = Repeat(schoolId, CategoryNames.Length),
                CampusIds = Repeat(campusId, CategoryNames.Length),
                Names = CategoryNames,
                Codes = System.Linq.Enumerable.Range(0, CategoryNames.Length)
                    .Select(i => $"PERF-LCAT-{campusId}-{i + 1:D2}").ToArray(),
                Descs = CategoryNames.Select(n => $"PERF library category {n}").ToArray(),
                Orders = System.Linq.Enumerable.Range(0, CategoryNames.Length).Select(i => i + 1).ToArray(),
                now
            })).ToList();
        result.Categories = categoryIds.Count;

        var authorIds = (await conn.QueryAsync<long>(
            @"INSERT INTO libraryauthor
                  (tenantid, schoolid, campusid, name, bio, nationality, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Bios), unnest(@Nationalities), true,
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, 40),
                SchoolIds = Repeat(schoolId, 40),
                CampusIds = Repeat(campusId, 40),
                Names = System.Linq.Enumerable.Range(0, 40)
                    .Select(i => $"PERF Author {i + 1:D2}").ToArray(),
                Bios = System.Linq.Enumerable.Range(0, 40)
                    .Select(i => $"PERF author biography {i + 1}").ToArray(),
                Nationalities = System.Linq.Enumerable.Range(0, 40)
                    .Select(i => i % 3 == 0 ? "UAE" : "UK").ToArray(),
                now
            })).ToList();
        result.Authors = authorIds.Count;

        var publisherIds = (await conn.QueryAsync<long>(
            @"INSERT INTO librarypublisher
                  (tenantid, schoolid, campusid, name, address, contactemail, phone, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Addrs), unnest(@Emails), unnest(@Phones), true,
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, 10),
                SchoolIds = Repeat(schoolId, 10),
                CampusIds = Repeat(campusId, 10),
                Names = System.Linq.Enumerable.Range(0, 10)
                    .Select(i => $"PERF Publisher {i + 1:D2}").ToArray(),
                Addrs = System.Linq.Enumerable.Range(0, 10)
                    .Select(i => $"PERF publisher address {i + 1}").ToArray(),
                Emails = System.Linq.Enumerable.Range(0, 10)
                    .Select(i => $"perf.publisher{campusId}.{i + 1}@perf.test").ToArray(),
                Phones = System.Linq.Enumerable.Range(0, 10)
                    .Select(i => $"04{campusId:D2}{i:D5}").ToArray(),
                now
            })).ToList();
        result.Publishers = publisherIds.Count;

        var vendorIds = (await conn.QueryAsync<long>(
            @"INSERT INTO libraryvendor
                  (tenantid, schoolid, campusid, name, address, contactperson, email, phone,
                   paymentterms, isactive, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Addrs), unnest(@Contacts), unnest(@Emails),
                     unnest(@Phones), 'Net 30', true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, 8),
                SchoolIds = Repeat(schoolId, 8),
                CampusIds = Repeat(campusId, 8),
                Names = System.Linq.Enumerable.Range(0, 8)
                    .Select(i => $"PERF Library Vendor {i + 1:D2}").ToArray(),
                Addrs = System.Linq.Enumerable.Range(0, 8)
                    .Select(i => $"PERF vendor address {i + 1}").ToArray(),
                Contacts = System.Linq.Enumerable.Range(0, 8)
                    .Select(i => $"Contact {i + 1}").ToArray(),
                Emails = System.Linq.Enumerable.Range(0, 8)
                    .Select(i => $"perf.lvendor{campusId}.{i + 1}@perf.test").ToArray(),
                Phones = System.Linq.Enumerable.Range(0, 8)
                    .Select(i => $"04{campusId:D2}{i:D5}").ToArray(),
                now
            })).ToList();
        result.Vendors = vendorIds.Count;

        var tierIds = (await conn.QueryAsync<long>(
            @"INSERT INTO librarymembershiptier
                  (tenantid, schoolid, campusid, name, borrowinglimit, reservationlimit,
                   finediscountpercent, displayorder, isactive, finerateperday,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Limits), unnest(@ResLimits),
                     unnest(@Discounts), unnest(@Orders), true, unnest(@FineRates),
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, TierNames.Length),
                SchoolIds = Repeat(schoolId, TierNames.Length),
                CampusIds = Repeat(campusId, TierNames.Length),
                Names = TierNames,
                Limits = new[] { 2, 5, 10 },
                ResLimits = new[] { 1, 3, 5 },
                Discounts = new[] { 0m, 25m, 50m },
                Orders = new[] { 1, 2, 3 },
                FineRates = new[] { 1m, 0.5m, 0m },
                now
            })).ToList();
        result.Tiers = tierIds.Count;

        // ------------------------------------------------------------------
        // 2. Catalogue: books, their authors, and the physical copies.
        // ------------------------------------------------------------------
        var bookIds = await SeedBooksAsync(
            conn, tenantId, schoolId, campusId, options.BooksPerCampus,
            categoryIds, publisherIds, vendorIds, now, verbose);
        result.Books = bookIds.Count;

        if (bookIds.Count == 0)
            throw new InvalidOperationException(
                $"Library seeding wrote no books for campus {campusId}, so copies and issues " +
                "cannot be seeded either.");

        result.BookAuthors = await conn.ExecuteAsync(
            @"INSERT INTO librarybookauthor (bookid, authorid)
              SELECT unnest(@BookIds), unnest(@AuthorIds)",
            new
            {
                BookIds = bookIds.ToArray(),
                AuthorIds = bookIds.Select((_, i) => authorIds[i % authorIds.Count]).ToArray()
            });

        var copyIds = await SeedCopiesAsync(
            conn, tenantId, schoolId, campusId, bookIds, options.CopiesPerBook, now, verbose);
        result.Copies = copyIds.Count;

        // ------------------------------------------------------------------
        // 3. Members - drawn from the campus's own students, so `studentid` resolves.
        // ------------------------------------------------------------------
        var memberIds = await SeedMembersAsync(
            conn, tenantId, schoolId, campusId, options.MembersPerCampus,
            tierIds, now, verbose);
        result.Members = memberIds.Count;

        if (memberIds.Count == 0)
            throw new InvalidOperationException(
                $"Library seeding wrote no members for campus {campusId} - the campus holds no " +
                "students, so there is nobody to issue a book to. Seed students first " +
                "(PerfDatasetSeeder does this in step 3).");

        // ------------------------------------------------------------------
        // 4. Circulation - THE volume table.
        // ------------------------------------------------------------------
        var circulation = await SeedIssuesAsync(
            conn, tenantId, schoolId, campusId, memberIds, copyIds,
            options.IssuesPerMember, now, verbose);
        result.Issues = circulation.Issues;
        result.Fines = circulation.Fines;

        // ------------------------------------------------------------------
        // 5. Reservations, reading lists, acquisitions, inventory audits.
        // ------------------------------------------------------------------
        result.Reservations = await SeedReservationsAsync(
            conn, tenantId, schoolId, campusId, memberIds, bookIds,
            options.ReservationsPerCampus, now);

        var lists = await SeedReadingListsAsync(
            conn, tenantId, schoolId, campusId, bookIds, options.ReadingListsPerCampus, now);
        result.ReadingLists = lists.Lists;
        result.ReadingListItems = lists.Items;

        result.Acquisitions = await SeedAcquisitionsAsync(
            conn, tenantId, schoolId, campusId, vendorIds, options.AcquisitionsPerCampus, now);

        result.InventoryAudits = await SeedInventoryAuditsAsync(
            conn, tenantId, schoolId, campusId, copyIds, options.InventoryAuditsPerCampus, now);

        if (verbose)
        {
            Console.WriteLine(
                $"  Library: campus {campusId} -> {result.Books:N0} books, {result.Copies:N0} copies, " +
                $"{result.Members:N0} members, {result.Issues:N0} issues, {result.Fines:N0} fines, " +
                $"{result.Reservations} reservations, {result.ReadingLists} lists " +
                "(+{result.ReadingListItems} items), " +
                $"{result.Acquisitions} acquisitions, {result.InventoryAudits} inventory audits");
        }

        return result;
    }

    private static long[] Repeat(long value, int count)
        => System.Linq.Enumerable.Repeat(value, count).ToArray();

    private async Task<List<long>> SeedBooksAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        int count, List<long> categoryIds, List<long> publisherIds, List<long> vendorIds,
        DateTime now, bool verbose)
    {
        var ids = new List<long>(count);

        for (var start = 0; start < count; start += 500)
        {
            var take = Math.Min(500, count - start);
            var range = System.Linq.Enumerable.Range(start, take).ToArray();

            var batch = await conn.QueryAsync<long>(
                @"INSERT INTO librarybook
                      (tenantid, schoolid, campusid, title, subtitle, isbn, language, edition,
                       publishedyear, summary, pagecount, price, categoryid, publisherid, vendorid,
                       isactive, createdby, modifiedby, createdon, modifiedon)
                  SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                         unnest(@Titles), unnest(@Subtitles), unnest(@Isbns), 'English',
                         unnest(@Editions), unnest(@Years), unnest(@Summaries), unnest(@Pages),
                         unnest(@Prices), unnest(@CategoryIds), unnest(@PublisherIds),
                         unnest(@VendorIds), true, 1, 1, @now, @now
                  RETURNING id",
                new
                {
                    TenantIds = Repeat(tenantId, take),
                    SchoolIds = Repeat(schoolId, take),
                    CampusIds = Repeat(campusId, take),
                    Titles = range.Select(i =>
                        $"{TitleWords[i % TitleWords.Length]} {SubjectWords[i % SubjectWords.Length]} " +
                        $"{i + 1:D4}").ToArray(),
                    Subtitles = range.Select(i => $"PERF title {i + 1}").ToArray(),
                    // ⚠️ Stamped with the campus id: a receipted acquisition matches a book by
                    // ISBN, and a re-run that reused an ISBN would find this run's book instead
                    // of creating one.
                    Isbns = range.Select(i => $"PERF-{campusId:D2}-{i + 1:D6}").ToArray(),
                    Editions = range.Select(i => $"{1 + i % 3} ed.").ToArray(),
                    Years = range.Select(i => 2005 + (i % 20)).ToArray(),
                    Summaries = range.Select(i => $"PERF summary for title {i + 1}").ToArray(),
                    Pages = range.Select(i => 100 + (i % 400)).ToArray(),
                    Prices = range.Select(i => 45m + (i % 30) * 5m).ToArray(),
                    CategoryIds = range.Select(i => categoryIds[i % categoryIds.Count]).ToArray(),
                    PublisherIds = range.Select(i => publisherIds[i % publisherIds.Count]).ToArray(),
                    VendorIds = range.Select(i => vendorIds[i % vendorIds.Count]).ToArray(),
                    now
                });

            ids.AddRange(batch);
        }

        if (verbose) LogProgress($"  Library books (campus {campusId})", ids.Count, count);
        return ids;
    }

    private async Task<List<long>> SeedCopiesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> bookIds, int copiesPerBook, DateTime now, bool verbose)
    {
        var ids = new List<long>(bookIds.Count * copiesPerBook);
        var perBatch = Math.Max(1, 800 / copiesPerBook);

        for (var start = 0; start < bookIds.Count; start += perBatch)
        {
            var take = Math.Min(perBatch, bookIds.Count - start);
            var rows = new List<(long BookId, int CopyIndex)>(take * copiesPerBook);

            for (var b = 0; b < take; b++)
            {
                for (var c = 0; c < copiesPerBook; c++)
                    rows.Add((bookIds[start + b], c));
            }

            var batch = await conn.QueryAsync<long>(
                @"INSERT INTO librarybookcopy
                      (tenantid, schoolid, campusid, bookid, barcode, rfid, shelflocation,
                       condition, acquireddate, status, createdby, modifiedby, createdon, modifiedon)
                  SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                         unnest(@BookIds), unnest(@Barcodes), unnest(@Rfids), unnest(@Shelves),
                         'Good', unnest(@Acquired)::date, unnest(@Statuses), 1, 1, @now, @now
                  RETURNING id",
                new
                {
                    TenantIds = Repeat(tenantId, rows.Count),
                    SchoolIds = Repeat(schoolId, rows.Count),
                    CampusIds = Repeat(campusId, rows.Count),
                    BookIds = rows.Select(r => r.BookId).ToArray(),
                    // ⚠️ Globally unique, so the campus id has to be in the barcode.
                    Barcodes = rows.Select(r => $"PERF-COPY-{campusId}-{r.BookId}-{r.CopyIndex + 1}").ToArray(),
                    Rfids = rows.Select(r => $"PERF-RFID-{campusId}-{r.BookId}-{r.CopyIndex + 1}").ToArray(),
                    Shelves = rows.Select((r, i) => $"Shelf {1 + i % 20}").ToArray(),
                    // A handful of copies are not on the shelf, so the copies grid's status
                    // filter has more than one branch.
                    Statuses = rows.Select((r, i) => i % 17 == 16 ? "Lost" : "Available").ToArray(),
                    Acquired = rows.Select((r, i) => DateTime.Today.AddDays(-500 + i % 400).Date).ToArray(),
                    now
                });

            ids.AddRange(batch);
        }

        if (verbose) LogProgress($"  Library copies (campus {campusId})", ids.Count, ids.Count);
        return ids;
    }

    private async Task<List<long>> SeedMembersAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        int count, List<long> tierIds, DateTime now, bool verbose)
    {
        var studentIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM student
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT @count",
            new { tenantId, schoolId, campusId, count })).ToList();

        if (studentIds.Count == 0) return new List<long>();

        var ids = new List<long>(studentIds.Count);

        for (var start = 0; start < studentIds.Count; start += 500)
        {
            var take = Math.Min(500, studentIds.Count - start);
            var range = System.Linq.Enumerable.Range(start, take).ToArray();

            var batch = await conn.QueryAsync<long>(
                @"INSERT INTO librarymember
                      (tenantid, schoolid, campusid, membertype, studentid, employeeid,
                       membernumber, borrowinglimit, tierid, isactive, blacklisted,
                       blacklistreason, createdby, modifiedby, createdon, modifiedon)
                  SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                         'Student', unnest(@StudentIds), NULL,
                         unnest(@Numbers), unnest(@Limits), unnest(@TierIds), true, false,
                         NULL, 1, 1, @now, @now
                  RETURNING id",
                new
                {
                    TenantIds = Repeat(tenantId, take),
                    SchoolIds = Repeat(schoolId, take),
                    CampusIds = Repeat(campusId, take),
                    StudentIds = studentIds.Skip(start).Take(take).ToArray(),
                    Numbers = range.Select(i => $"PERF-MBR-{campusId}-{i + 1:D5}").ToArray(),
                    Limits = range.Select(i => 2 + (i % 3) * 3).ToArray(),
                    TierIds = range.Select(i => tierIds[i % tierIds.Count]).ToArray(),
                    now
                });

            ids.AddRange(batch);
        }

        if (verbose) LogProgress($"  Library members (campus {campusId})", ids.Count, studentIds.Count);
        return ids;
    }

    private async Task<(int Issues, int Fines)> SeedIssuesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> memberIds, List<long> copyIds, int issuesPerMember,
        DateTime now, bool verbose)
    {
        if (copyIds.Count == 0) return (0, 0);

        var total = memberIds.Count * issuesPerMember;
        var inserted = 0;
        var fineRows = 0;

        // ⚠️ A copy can only hold ONE active loan - `GetActiveByCopy` reads by copy and
        // status, and the circulation service asserts it. So the ledger is generated in PASSES
        // over the copy list: the first pass gives each copy one loan in a spread of states, and
        // every later pass is RETURNED-ONLY, so no copy ever carries two simultaneous loans.
        // (Deriving the state from `i % 4` alone did read correctly until the second pass, when
        // 600 copies and a period of 4 made the state constant per copy and stacked three active
        // loans on the same shelf copy - realistic-looking rows that violate the desk's own rule.)
        var batch = new List<IssueRow>(1000);
        var fines = new List<FineRow>(500);

        var today = DateTime.Today;

        for (var i = 0; i < total; i++)
        {
            var memberId = memberIds[i % memberIds.Count];
            var pass = i / copyIds.Count;
            var copyId = copyIds[i % copyIds.Count];

            // Four states, spread so every branch of the desk has rows:
            //   0 -> Issued, due in the future
            //   1 -> Issued, PAST DUE  (what the overdue counter and the fine rule are about)
            //   2 -> Returned on time
            //   3 -> Returned LATE (and therefore fined)
            // A later pass is always 2 or 3 (returned) - see the note above.
            var state = pass == 0 ? i % 4 : 2 + (i % 2);
            // Each pass is further back in time, so a reused copy's second loan is genuinely
            // older than its first and the desk's ordering by IssuedDate DESC reads correctly.
            var issuedDate = today.AddDays(-30 + (i % 25) - pass * 60);
            var returned = state >= 2;

            var dueDate = state == 1
                ? today.AddDays(-1 - (i % 10))          // past due, still out
                : issuedDate.AddDays(14);

            var returnDate = state == 2
                ? issuedDate.AddDays(10)
                : issuedDate.AddDays(14 + 3 + (i % 5)); // late

            var fineDays = returned && state == 3 ? 3 + (i % 5) : 0;
            var fineAmount = fineDays * 1m;

            batch.Add(new IssueRow
            {
                CopyId = copyId,
                MemberId = memberId,
                IssuedDate = issuedDate.AddHours(10).AddMinutes(i % 60),
                DueDate = dueDate.Date,
                ReturnDate = returned ? returnDate.AddHours(11).AddMinutes(i % 60) : (DateTime?)null,
                FineDays = fineDays,
                FineAmount = fineAmount,
                Status = returned ? "Returned" : "Issued",
                RenewedCount = i % 5 == 4 ? 1 : 0,
            });

            if (batch.Count >= 1000)
            {
                var (wrote, ids) = await InsertIssuesAsync(
                    conn, tenantId, schoolId, campusId, batch, now);
                inserted += wrote;

                // Fines are written straight after their own issues, keyed on the returned ids.
                foreach (var (row, issueId) in batch.Zip(ids))
                {
                    if (row.FineAmount > 0)
                    {
                        // Half of the late returns are settled, so the fines grid has both a
                        // Paid and an Unpaid branch - and the desk's "Member has unpaid fines"
                        // rule has rows on both sides. The parity is taken from the ROW, not from
                        // the running total, so a batch boundary cannot flip it.
                        var settled = issueId % 2 == 0;
                        fines.Add(new FineRow
                        {
                            IssueId = issueId,
                            MemberId = row.MemberId,
                            Amount = row.FineAmount,
                            PaidAmount = settled ? row.FineAmount : 0m,
                            PaymentStatus = settled ? "Paid" : "Unpaid",
                            PaymentReference = settled ? $"PERF-REF-{campusId}-{issueId}" : null,
                        });
                    }
                }

                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            var (wrote, ids) = await InsertIssuesAsync(conn, tenantId, schoolId, campusId, batch, now);
            inserted += wrote;
            foreach (var (row, issueId) in batch.Zip(ids))
            {
                if (row.FineAmount > 0)
                {
                    fines.Add(new FineRow
                    {
                        IssueId = issueId,
                        MemberId = row.MemberId,
                        Amount = row.FineAmount,
                        PaidAmount = 0m,
                        PaymentStatus = "Unpaid",
                        PaymentReference = null,
                    });
                }
            }
        }

        // The copy's own status follows the loan it carries (Available only when returned).
        await conn.ExecuteAsync(
            @"UPDATE librarybookcopy c
                 SET status = CASE WHEN EXISTS (
                        SELECT 1 FROM libraryissue i
                         WHERE i.bookcopyid = c.id AND i.status = 'Issued')
                     THEN 'Issued' ELSE 'Available' END
               WHERE c.tenantid = @tenantId AND c.schoolid = @schoolId AND c.campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (fines.Count > 0)
        {
            for (var start = 0; start < fines.Count; start += 1000)
            {
                var slice = fines.Skip(start).Take(1000).ToList();
                fineRows += await conn.ExecuteAsync(
                    @"INSERT INTO libraryfine
                          (tenantid, schoolid, campusid, issueid, memberid, amount, paidamount,
                           paymentstatus, remarks, createdby, modifiedby, createdon, modifiedon,
                           paymentmethodid, paymentreference, receiptnumber, receivedby, paiddate)
                      SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                             unnest(@IssueIds), unnest(@MemberIds), unnest(@Amounts), unnest(@Paid),
                             unnest(@Statuses), unnest(@Remarks), 1, 1, @now, @now,
                             NULL, unnest(@Refs), unnest(@Receipts), 1, unnest(@PaidDates)",
                    new
                    {
                        TenantIds = Repeat(tenantId, slice.Count),
                        SchoolIds = Repeat(schoolId, slice.Count),
                        CampusIds = Repeat(campusId, slice.Count),
                        IssueIds = slice.Select(f => f.IssueId).ToArray(),
                        MemberIds = slice.Select(f => f.MemberId).ToArray(),
                        Amounts = slice.Select(f => f.Amount).ToArray(),
                        Paid = slice.Select(f => f.PaidAmount).ToArray(),
                        Statuses = slice.Select(f => f.PaymentStatus).ToArray(),
                        Remarks = slice.Select(f => "PERF overdue fine").ToArray(),
                        Refs = slice.Select(f => f.PaymentReference).ToArray(),
                        Receipts = slice.Select(f => f.PaymentReference == null
                            ? null : $"PERF-RCPT-{campusId}-{f.IssueId}").ToArray(),
                        PaidDates = slice.Select(f => f.PaymentReference == null
                            ? (DateTime?)null : today.Date).ToArray(),
                        now
                    });
            }
        }

        if (verbose)
        {
            LogProgress($"  Library issues (campus {campusId})", inserted, total);
            Console.WriteLine($"  Library fines (campus {campusId}): {fineRows:N0}");
        }

        return (inserted, fineRows);
    }

    private sealed class IssueRow
    {
        public long CopyId { get; set; }
        public long MemberId { get; set; }
        public DateTime IssuedDate { get; set; }
        public DateTime DueDate { get; set; }
        public DateTime? ReturnDate { get; set; }
        public int FineDays { get; set; }
        public decimal FineAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public int RenewedCount { get; set; }
    }

    private sealed class FineRow
    {
        public long IssueId { get; set; }
        public long MemberId { get; set; }
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public string PaymentStatus { get; set; } = string.Empty;
        public string? PaymentReference { get; set; }
    }

    private static async Task<(int Count, List<long> Ids)> InsertIssuesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<IssueRow> batch, DateTime now)
    {
        // ⚠️ `duedate` is a `date` (not a timestamp like `issueddate`/`returndate`), so it is
        // cast on the way in - unnest of a DateTime[] binds timestamp and PostgreSQL refuses the
        // narrower column.
        var ids = (await conn.QueryAsync<long>(
            @"INSERT INTO libraryissue
                  (tenantid, schoolid, campusid, bookcopyid, memberid, issueddate, duedate,
                   renewedcount, maxrenewals, returndate, finedays, fineamount, status,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@CopyIds), unnest(@MemberIds), unnest(@Issued), unnest(@Dues)::date,
                     unnest(@Renewed), 2, unnest(@Returns), unnest(@FineDays), unnest(@FineAmounts),
                     unnest(@Statuses), 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, batch.Count),
                SchoolIds = Repeat(schoolId, batch.Count),
                CampusIds = Repeat(campusId, batch.Count),
                CopyIds = batch.Select(b => b.CopyId).ToArray(),
                MemberIds = batch.Select(b => b.MemberId).ToArray(),
                Issued = batch.Select(b => b.IssuedDate).ToArray(),
                Dues = batch.Select(b => b.DueDate.Date).ToArray(),
                Renewed = batch.Select(b => b.RenewedCount).ToArray(),
                Returns = batch.Select(b => b.ReturnDate).ToArray(),
                FineDays = batch.Select(b => b.FineDays).ToArray(),
                FineAmounts = batch.Select(b => b.FineAmount).ToArray(),
                Statuses = batch.Select(b => b.Status).ToArray(),
                now
            })).ToList();

        return (ids.Count, ids);
    }

    private static async Task<int> SeedReservationsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> memberIds, List<long> bookIds, int count, DateTime now)
    {
        return await conn.ExecuteAsync(
            @"INSERT INTO libraryreservation
                  (tenantid, schoolid, campusid, bookid, memberid, reservationdate, expirydate,
                   status, fulfilledissueid, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@BookIds), unnest(@MemberIds), unnest(@ReservationDates),
                     unnest(@Expiry)::date, unnest(@Statuses), NULL, 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                BookIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => bookIds[i % bookIds.Count]).ToArray(),
                MemberIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => memberIds[i % memberIds.Count]).ToArray(),
                ReservationDates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-20 + i % 20).AddHours(9)).ToArray(),
                Expiry = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(3 + i % 10).Date).ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => (i % 3) switch { 0 => "Pending", 1 => "Fulfilled", _ => "Cancelled" })
                    .ToArray(),
                now
            });
    }

    private static async Task<(int Lists, int Items)> SeedReadingListsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> bookIds, int count, DateTime now)
    {
        var listIds = (await conn.QueryAsync<long>(
            @"INSERT INTO libraryreadinglist
                  (tenantid, schoolid, campusid, name, description, createdbyemployeeid,
                   isactive, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Descs), 0, true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Names = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Reading List {i + 1:D2}").ToArray(),
                Descs = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF reading list {i + 1} for the campus").ToArray(),
                now
            })).ToList();

        var perList = 8;
        var itemCount = listIds.Count * perList;
        await conn.ExecuteAsync(
            @"INSERT INTO libraryreadinglistitem
                  (readinglistid, bookid, displayorder, isrequired, notes)
              SELECT unnest(@ListIds), unnest(@BookIds), unnest(@Orders), unnest(@Required), unnest(@Notes)",
            new
            {
                ListIds = listIds.SelectMany(id => System.Linq.Enumerable.Repeat(id, perList).ToList()).ToArray(),
                BookIds = System.Linq.Enumerable.Range(0, itemCount)
                    .Select(i => bookIds[i % bookIds.Count]).ToArray(),
                Orders = System.Linq.Enumerable.Range(0, itemCount)
                    .Select(i => (i % perList) + 1).ToArray(),
                Required = System.Linq.Enumerable.Range(0, itemCount)
                    .Select(i => i % 3 != 2).ToArray(),
                Notes = System.Linq.Enumerable.Range(0, itemCount)
                    .Select(i => $"PERF list item {i + 1}").ToArray()
            });

        return (listIds.Count, itemCount);
    }

    private static async Task<int> SeedAcquisitionsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> vendorIds, int count, DateTime now)
    {
        return await conn.ExecuteAsync(
            @"INSERT INTO libraryacquisition
                  (tenantid, schoolid, campusid, title, isbn, author, publisher, quantity,
                   unitprice, totalamount, requestedby, vendorid, academicyearid, status,
                   workflowid, remarks, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Titles), unnest(@Isbns), unnest(@Authors), unnest(@Publishers),
                     unnest(@Qtys), unnest(@Prices), unnest(@Totals), 0, unnest(@VendorIds),
                     NULL, unnest(@Statuses), NULL, unnest(@Remarks), 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Titles = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Acquisition Title {i + 1:D3}").ToArray(),
                // Stamped, so a re-run cannot collide with the book an earlier run's receipt created.
                Isbns = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-ACQ-{campusId}-{i + 1:D5}").ToArray(),
                Authors = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Acq Author {i + 1}").ToArray(),
                Publishers = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Acq Publisher {i + 1}").ToArray(),
                Qtys = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 5 + (i % 20)).ToArray(),
                Prices = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 40m + (i % 20) * 5m).ToArray(),
                Totals = System.Linq.Enumerable.Range(0, count)
                    .Select(i => (5m + (i % 20)) * (40m + (i % 20) * 5m)).ToArray(),
                VendorIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => vendorIds[i % vendorIds.Count]).ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => (i % 4) switch
                    {
                        0 => "Requested",
                        1 => "Approved",
                        2 => "Received",
                        _ => "Cancelled"
                    }).ToArray(),
                Remarks = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF acquisition {i + 1}").ToArray(),
                now
            });
    }

    private static async Task<int> SeedInventoryAuditsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> copyIds, int count, DateTime now)
    {
        if (copyIds.Count == 0) return 0;
        var take = Math.Min(count, copyIds.Count);

        // One audit session, `count` scanned copies - the shape the physical-inventory screen
        // produces (the screen mints one session id and files every scan under it).
        var sessionId = $"PERF-AUDIT-{campusId}-{Guid.NewGuid():N}".Substring(0, 32);

        return await conn.ExecuteAsync(
            @"INSERT INTO libraryinventoryaudit
                  (tenantid, schoolid, campusid, bookcopyid, auditdate, auditsessionid, status,
                   auditedby, remarks, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@CopyIds), unnest(@Dates), @sessionId, unnest(@Statuses),
                     1, unnest(@Remarks), 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, take),
                SchoolIds = Repeat(schoolId, take),
                CampusIds = Repeat(campusId, take),
                CopyIds = copyIds.Take(take).ToArray(),
                Dates = System.Linq.Enumerable.Range(0, take)
                    .Select(i => DateTime.Today.AddDays(-7 + i % 7).AddHours(10)).ToArray(),
                // Three of the four outcomes the screen offers; the fourth (Damaged) would need
                // a copy this seeder deliberately did not damage.
                Statuses = System.Linq.Enumerable.Range(0, take)
                    .Select(i => (i % 3) switch { 0 => "Matched", 1 => "Missing", _ => "Misplaced" })
                    .ToArray(),
                Remarks = System.Linq.Enumerable.Range(0, take)
                    .Select(i => $"PERF inventory scan {i + 1}").ToArray(),
                sessionId,
                now
            });
    }
}
