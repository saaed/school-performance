using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="InventoryModuleSeeder"/>.</summary>
public sealed class InventorySeedOptions
{
    /// <summary>Catalogue items on the campus. A 2,000-student school stocks a few hundred.</summary>
    public int ItemsPerCampus { get; set; } = 200;

    /// <summary>Stock movements per item - the module's VOLUME table.</summary>
    public int MovementsPerItem { get; set; } = 30;

    public int SuppliersPerCampus { get; set; } = 20;
    public int PurchaseOrdersPerCampus { get; set; } = 40;
    public int GrnsPerCampus { get; set; } = 30;
    public int AssetsPerCampus { get; set; } = 60;

    /// <summary>Depreciation rows per asset (one per month of useful life recorded so far).</summary>
    public int DepreciationMonthsPerAsset { get; set; } = 12;

    public int StockRequestsPerCampus { get; set; } = 30;
    public int AdjustmentsPerCampus { get; set; } = 10;
    public int ReservationsPerCampus { get; set; } = 20;

    /// <summary>Re-seed even when the campus already holds inventory rows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's inventory seed produced.</summary>
public sealed class InventorySeedResult
{
    public bool Skipped { get; set; }
    public int Categories { get; set; }
    public int Uoms { get; set; }
    public int Locations { get; set; }
    public int Suppliers { get; set; }
    public int Items { get; set; }
    public int ItemUoms { get; set; }
    public int StockRows { get; set; }
    public int ItemCosts { get; set; }
    public int Movements { get; set; }
    public int PurchaseOrders { get; set; }
    public int PurchaseOrderLines { get; set; }
    public int Grns { get; set; }
    public int GrnLines { get; set; }
    public int Assets { get; set; }
    public int DepreciationRows { get; set; }
    public int StockRequests { get; set; }
    public int Adjustments { get; set; }
    public int AdjustmentLines { get; set; }
    public int Reservations { get; set; }
}

/// <summary>
/// Seeds the INVENTORY / PROCUREMENT module's volume tables for one campus.
///
/// WHY THIS EXISTS
/// ---------------
/// Every table this seeder writes held ZERO rows in `ayra_perf`, so all 28 `Inv*` repositories -
/// and the 16 `inv.*` screens that drive them - were unmeasurable: every spec reported SKIP, and a
/// SKIP is honest but useless. Coverage of a module is gated on its DATA, not on how many specs
/// exist (the same rule the HR and fee spine seeders exist for).
///
/// WHICH TABLES, AND WHY
/// ---------------------
/// The module has a reference tier and a volume tier, and only the second one is a performance
/// question:
///
///   * REFERENCE (tens of rows): invcategory, invuom, invlocation, invsupplier. Created here because
///     `invitem.categoryid` and `invitemcost.locationid` are NOT NULL - a volume table that cannot be
///     written without them.
///   * VOLUME (thousands): invmovement (one row per stock movement, the ledger the `inv.movement`
///     grid pages over), invdepreciation (one row per asset per period), invgrnline / invpoline
///     (lines per document), invstock / invitemcost (one per item per location).
///   * DOCUMENTS (tens each): invpurchaseorder, invgrn, invstockadjustment, invstockrequest,
///     invasset. Real schools hold tens of these; they are seeded because their GRIDS are what we
///     measure, not because they grow.
///
/// ⚠️ THE CODES ARE `PERF-*` AND DETERMINISTIC, so a re-run collides by design - that is what makes
/// `Force` a clear-then-write rather than an append. `ClearTableAsync` tolerates an FK refusal, so
/// the clear order below is children-first.
///
/// ⚠️ THE MODULE'S SCOPE INDEXES ALREADY EXIST (`idx_inv<Table>_campus (tenantid, schoolid,
/// campusid)` on invasset/invgrn/invpurchaseorder/invreservation/invstockadjustment/
/// invstockrequest, and `idx_invmovement_campus_date (tenantid, schoolid, campusid, movementdate)`),
/// plus the unique `ux_invstock_campus_item` / `ux_invitemcost_campus_location_item` triples. That is
/// why the index advice for this module is expected to be thin - and why measuring it is worth doing
/// either way: "the index exists" and "the index is used" are different claims.
/// </summary>
public sealed class InventoryModuleSeeder : BaseSeeder
{
    public InventoryModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk inventory load invalidates, so the planner sees the new row counts.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "invcategory", "invuom", "invlocation", "invsupplier", "invitem", "invitemuom",
        "invstock", "invitemcost", "invmovement", "invpurchaseorder", "invpoline",
        "invgrn", "invgrnline", "invasset", "invdepreciation", "invstockrequest",
        "invstockadjustment", "invstockadjustmentline", "invreservation"
    };

    private static readonly string[] CategoryNames =
    {
        "Stationery", "Furniture", "Laboratory", "IT Equipment", "Sports", "Cleaning", "Textbooks"
    };

    private static readonly string[] UomNames =
    {
        "Piece", "Box", "Ream", "Litre", "Kilogram", "Pack"
    };

    private static readonly string[] UomAbbreviations = { "pc", "box", "ream", "L", "kg", "pk" };

    private static readonly string[] LocationNames =
    {
        "Main Store", "Science Lab", "Sports Room", "IT Store"
    };

    private static readonly string[] ItemWords =
    {
        "Notebook", "Whiteboard Marker", "A4 Paper", "Desk", "Chair", "Microscope",
        "Beaker", "Laptop", "Projector", "Football", "Cricket Bat", "Floor Cleaner",
        "Textbook", "Printer Toner", "Extension Lead", "First Aid Kit"
    };

    private static readonly string[] MovementTypes =
    {
        "Purchase", "Consumption", "Adjustment", "Transfer", "Return", "Issue"
    };

    private static readonly string[] SupplierNames =
    {
        "Gulf Office Supplies", "Emirates Stationery", "TechSource LLC", "LabWorks Trading",
        "Academic Books FZE", "CleanPro Services", "Sportline Equipment", "Furniture Mart"
    };

    public async Task<InventorySeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, InventorySeedOptions options, bool verbose = true)
    {
        var result = new InventorySeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM invitem
               WHERE tenantid = @tenantId AND schoolid = @schoolId
                 AND code LIKE @prefix",
            new { tenantId, schoolId, prefix = $"PERF-ITEM-{campusId}-%" });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.Items = (int)existing;
            if (verbose)
                Console.WriteLine($"  Inventory: campus {campusId} already holds {existing:N0} items - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // Children first: invmovement/invstock/invitemcost/invitemuom/invreservation point at
        // invitem, and invgrnline points at both invgrn and invpoline - so a plain DELETE of the
        // parents is a 23503.
        if (options.Force && existing > 0)
        {
            foreach (var table in new[]
                     {
                         "invdepreciation", "invassetassignment", "invassetdisposal",
                         "invassetrevaluation", "invmaintenance", "invasset",
                         "invgrnline", "invgrn", "invpoline", "invpurchaseorder",
                         "invstockadjustmentline", "invstockadjustment",
                         "invmovement", "invreservation", "invstockrequest",
                         "invitemcost", "invstock", "invitemuom", "invitem",
                         "invlocation", "invsupplier", "invcategory", "invuom"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
                Console.WriteLine($"  Inventory: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. Reference tier - the NOT NULL parents of the volume tables.
        // ------------------------------------------------------------------
        var categoryIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invcategory
                  (tenantid, schoolid, campusid, name, description, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Descs), true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, CategoryNames.Length),
                SchoolIds = Repeat(schoolId, CategoryNames.Length),
                CampusIds = Repeat(campusId, CategoryNames.Length),
                Names = CategoryNames,
                Descs = CategoryNames.Select(n => $"PERF category {n}").ToArray(),
                now
            })).ToList();
        result.Categories = categoryIds.Count;

        var uomIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invuom
                  (tenantid, schoolid, campusid, name, abbreviation, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Abbrs), true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, UomNames.Length),
                SchoolIds = Repeat(schoolId, UomNames.Length),
                CampusIds = Repeat(campusId, UomNames.Length),
                Names = UomNames,
                Abbrs = UomAbbreviations,
                now
            })).ToList();
        result.Uoms = uomIds.Count;

        var locationIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invlocation
                  (tenantid, schoolid, campusid, name, description, isactive,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Descs), true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, LocationNames.Length),
                SchoolIds = Repeat(schoolId, LocationNames.Length),
                CampusIds = Repeat(campusId, LocationNames.Length),
                Names = LocationNames,
                Descs = LocationNames.Select(n => $"PERF location {n}").ToArray(),
                now
            })).ToList();
        result.Locations = locationIds.Count;

        var supplierIds = await SeedSuppliersAsync(
            conn, tenantId, schoolId, campusId, options.SuppliersPerCampus, now);
        result.Suppliers = supplierIds.Count;

        // ------------------------------------------------------------------
        // 2. Items - the root every inventory grid joins.
        //    ⚠️ `reorderlevel > 0` on most items ON PURPOSE: the stock screen's low-stock
        //    branch is `QtyOnHand <= ReorderLevel AND ReorderLevel > 0`, so a catalogue
        //    where every reorder level is 0 makes that branch measure nothing.
        // ------------------------------------------------------------------
        var itemIds = await SeedItemsAsync(
            conn, tenantId, schoolId, campusId, options.ItemsPerCampus,
            categoryIds, uomIds, now, verbose);
        result.Items = itemIds.Count;

        if (itemIds.Count == 0)
            throw new InvalidOperationException(
                $"Inventory seeding wrote no items for campus {campusId}, so its stock, movements and " +
                "documents cannot be seeded either.");

        // item -> base UOM, one row per item.
        result.ItemUoms = await conn.ExecuteAsync(
            @"INSERT INTO invitemuom
                  (tenantid, schoolid, campusid, invitemid, invuomid, isbase, conversionfactor,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@ItemIds), unnest(@UomIds), true, 1, 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, itemIds.Count),
                SchoolIds = Repeat(schoolId, itemIds.Count),
                CampusIds = Repeat(campusId, itemIds.Count),
                ItemIds = itemIds.ToArray(),
                UomIds = itemIds.Select((_, i) => uomIds[i % uomIds.Count]).ToArray(),
                now
            });

        // ------------------------------------------------------------------
        // 3. Stock + item cost, one row per item (per location for the cost row).
        // ------------------------------------------------------------------
        var stockQtys = itemIds.Select((_, i) => 20m + (i % 80) * 5m).ToArray();

        result.StockRows = await conn.ExecuteAsync(
            @"INSERT INTO invstock
                  (tenantid, schoolid, campusid, invitemid, qtyonhand, qtyreserved,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@ItemIds), unnest(@Qtys), 0, 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, itemIds.Count),
                SchoolIds = Repeat(schoolId, itemIds.Count),
                CampusIds = Repeat(campusId, itemIds.Count),
                ItemIds = itemIds.ToArray(),
                Qtys = stockQtys,
                now
            });

        result.ItemCosts = await conn.ExecuteAsync(
            @"INSERT INTO invitemcost
                  (tenantid, schoolid, campusid, locationid, invitemid, costmethod,
                   currentcost, lastcost, updatedon, updatedby,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@LocationIds), unnest(@ItemIds), 'FIFO',
                     unnest(@Costs), unnest(@Costs), @now, 1, 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, itemIds.Count),
                SchoolIds = Repeat(schoolId, itemIds.Count),
                CampusIds = Repeat(campusId, itemIds.Count),
                LocationIds = itemIds.Select((_, i) => locationIds[i % locationIds.Count]).ToArray(),
                ItemIds = itemIds.ToArray(),
                Costs = itemIds.Select((_, i) => 10m + (i % 40) * 2.5m).ToArray(),
                now
            });

        // ------------------------------------------------------------------
        // 4. Movements - THE volume table (`inv.movement`'s ledger grid pages this, ordered
        //    by MovementDate DESC).
        // ------------------------------------------------------------------
        result.Movements = await SeedMovementsAsync(
            conn, tenantId, schoolId, campusId, itemIds, options.MovementsPerItem,
            now, verbose);

        // ------------------------------------------------------------------
        // 5. Procurement: PO -> lines -> GRN -> lines. `invgrnline.invitemid` is NOT NULL and
        //    the receipt grid reads it, so the line carries the item as well as the PO line.
        // ------------------------------------------------------------------
        var purchase = await SeedPurchaseOrdersAsync(
            conn, tenantId, schoolId, campusId, supplierIds, itemIds, uomIds,
            options.PurchaseOrdersPerCampus, now);
        result.PurchaseOrders = purchase.PoIds.Count;
        result.PurchaseOrderLines = purchase.Lines.Count;

        var grnResult = await SeedGrnsAsync(
            conn, tenantId, schoolId, campusId, purchase.PoIds, purchase.Lines,
            options.GrnsPerCampus, now);
        result.Grns = grnResult.Grns;
        result.GrnLines = grnResult.Lines;

        // ------------------------------------------------------------------
        // 6. Assets + depreciation (the fixed-asset register and its schedule).
        // ------------------------------------------------------------------
        var assetIds = await SeedAssetsAsync(
            conn, tenantId, schoolId, campusId, itemIds, options.AssetsPerCampus, now);
        result.Assets = assetIds.Count;

        result.DepreciationRows = await SeedDepreciationAsync(
            conn, tenantId, schoolId, campusId, assetIds,
            options.DepreciationMonthsPerAsset, now);

        // ------------------------------------------------------------------
        // 7. Requests, adjustments, reservations.
        // ------------------------------------------------------------------
        var requestIds = await SeedStockRequestsAsync(
            conn, tenantId, schoolId, campusId, itemIds, options.StockRequestsPerCampus, now);
        result.StockRequests = requestIds.Count;

        var adjResult = await SeedAdjustmentsAsync(
            conn, tenantId, schoolId, campusId, itemIds, options.AdjustmentsPerCampus, now);
        result.Adjustments = adjResult.Adjustments;
        result.AdjustmentLines = adjResult.Lines;

        result.Reservations = await SeedReservationsAsync(
            conn, tenantId, schoolId, campusId, itemIds, requestIds,
            options.ReservationsPerCampus, now);

        if (verbose)
        {
            Console.WriteLine(
                $"  Inventory: campus {campusId} -> {result.Items:N0} items, {result.StockRows:N0} stock rows, " +
                $"{result.Movements:N0} movements, {result.PurchaseOrders} POs (+{result.PurchaseOrderLines} lines), " +
                $"{result.Grns} GRNs (+{result.GrnLines} lines), {result.Assets} assets " +
                "(+{result.DepreciationRows:N0} depreciation rows), " +
                $"{result.StockRequests} requests, {result.Adjustments} adjustments, {result.Reservations} reservations");
        }

        return result;
    }

    private static long[] Repeat(long value, int count)
        => System.Linq.Enumerable.Repeat(value, count).ToArray();

    private static async Task<List<long>> SeedSuppliersAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int count, DateTime now)
    {
        var names = System.Linq.Enumerable.Range(0, count)
            .Select(i => $"{SupplierNames[i % SupplierNames.Length]} {i + 1:D3}")
            .ToArray();

        return (await conn.QueryAsync<long>(
            @"INSERT INTO invsupplier
                  (tenantid, schoolid, campusid, name, contactperson, phone, email, address,
                   paymentterms, isactive, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Contacts), unnest(@Phones), unnest(@Emails),
                     unnest(@Addrs), 'Net 30', true, 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Names = names,
                Contacts = names.Select((_, i) => $"Contact {i + 1}").ToArray(),
                Phones = names.Select((_, i) => $"04{campusId:D2}{i:D5}").ToArray(),
                Emails = names.Select((_, i) => $"perf.supplier{campusId}.{i + 1}@perf.test").ToArray(),
                Addrs = names.Select(n => $"PERF address for {n}").ToArray(),
                now
            })).ToList();
    }

    private async Task<List<long>> SeedItemsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        int count, List<long> categoryIds, List<long> uomIds, DateTime now, bool verbose)
    {
        var ids = new List<long>(count);
        var batch = new List<ItemRow>(500);

        for (var i = 0; i < count; i++)
        {
            var word = ItemWords[i % ItemWords.Length];
            batch.Add(new ItemRow
            {
                Code = $"PERF-ITEM-{campusId}-{i + 1:D5}",
                Name = $"{word} {i + 1:D4}",
                CategoryId = categoryIds[i % categoryIds.Count],
                UomId = uomIds[i % uomIds.Count],
                // Most items carry a reorder level (see the class comment); every fifth does not.
                ReorderLevel = i % 5 == 4 ? 0m : 10m,
                MinLevel = 5m,
                MaxLevel = 500m,
            });

            if (batch.Count >= 500)
            {
                ids.AddRange(await InsertItemsAsync(conn, tenantId, schoolId, campusId, batch, now));
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            ids.AddRange(await InsertItemsAsync(conn, tenantId, schoolId, campusId, batch, now));

        if (verbose) LogProgress($"  Inventory items (campus {campusId})", ids.Count, count);
        return ids;
    }

    private sealed class ItemRow
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long CategoryId { get; set; }
        public long UomId { get; set; }
        public decimal ReorderLevel { get; set; }
        public decimal MinLevel { get; set; }
        public decimal MaxLevel { get; set; }
    }

    private static async Task<List<long>> InsertItemsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<ItemRow> batch, DateTime now)
    {
        var ids = await conn.QueryAsync<long>(
            @"INSERT INTO invitem
                  (tenantid, schoolid, campusid, categoryid, code, name, description,
                   reorderlevel, isactive, createdby, modifiedby, createdon, modifiedon,
                   minlevel, maxlevel)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@CategoryIds), unnest(@Codes), unnest(@Names), unnest(@Descs),
                     unnest(@ReorderLevels), true, 1, 1, @now, @now,
                     unnest(@MinLevels), unnest(@MaxLevels)
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, batch.Count),
                SchoolIds = Repeat(schoolId, batch.Count),
                CampusIds = Repeat(campusId, batch.Count),
                CategoryIds = batch.Select(b => b.CategoryId).ToArray(),
                Codes = batch.Select(b => b.Code).ToArray(),
                Names = batch.Select(b => b.Name).ToArray(),
                Descs = batch.Select(b => $"PERF item {b.Name}").ToArray(),
                ReorderLevels = batch.Select(b => b.ReorderLevel).ToArray(),
                MinLevels = batch.Select(b => b.MinLevel).ToArray(),
                MaxLevels = batch.Select(b => b.MaxLevel).ToArray(),
                now
            });

        return ids.ToList();
    }

    private async Task<int> SeedMovementsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> itemIds, int perItem, DateTime now, bool verbose)
    {
        var inserted = 0;
        var total = itemIds.Count * perItem;
        var batch = new List<MovementRow>(5000);

        // ⚠️ The window ENDS TODAY. `inv.movement`'s grid is date-bounded
        // (fromDate/toDate) and the screen opens on the current period, so a window that
        // stopped yesterday would leave the default view empty - the exact off-by-one the
        // HR attendance seeder was fixed for.
        var startDay = DateTime.Today.AddDays(-perItem + 1);

        for (var day = 0; day < perItem; day++)
        {
            var date = startDay.AddDays(day);
            for (var i = 0; i < itemIds.Count; i++)
            {
                // A spread of directions so the ledger's quantity column is not all one sign,
                // and a spread of types so the screen's type filter has more than one branch.
                var inbound = (day + i) % 3 == 0;
                batch.Add(new MovementRow
                {
                    ItemId = itemIds[i],
                    MovementType = MovementTypes[(day + i) % MovementTypes.Length],
                    Quantity = inbound ? 10m + (i % 20) : -(2m + (i % 8)),
                    UnitCost = 10m + (i % 40) * 2.5m,
                    ReferenceType = inbound ? "PurchaseOrder" : "StockRequest",
                    Notes = $"PERF movement d{day} i{i}",
                    MovementDate = date.AddHours(9).AddMinutes(i % 60),
                });
            }

            if (batch.Count >= 5000)
            {
                inserted += await InsertMovementsAsync(conn, tenantId, schoolId, campusId, batch, now);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            inserted += await InsertMovementsAsync(conn, tenantId, schoolId, campusId, batch, now);

        if (verbose) LogProgress($"  Inventory movements (campus {campusId})", inserted, total);
        return inserted;
    }

    private sealed class MovementRow
    {
        public long ItemId { get; set; }
        public string MovementType { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public string ReferenceType { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime MovementDate { get; set; }
    }

    private static async Task<int> InsertMovementsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<MovementRow> batch, DateTime now)
    {
        var ids = await conn.QueryAsync<long>(
            @"INSERT INTO invmovement
                  (tenantid, schoolid, campusid, invitemid, movementtype, quantity, unitcost,
                   referencetype, referenceid, notes, movementdate,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@ItemIds), unnest(@Types), unnest(@Qty), unnest(@Costs),
                     unnest(@RefTypes), NULL, unnest(@Notes), unnest(@Dates),
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, batch.Count),
                SchoolIds = Repeat(schoolId, batch.Count),
                CampusIds = Repeat(campusId, batch.Count),
                ItemIds = batch.Select(b => b.ItemId).ToArray(),
                Types = batch.Select(b => b.MovementType).ToArray(),
                Qty = batch.Select(b => b.Quantity).ToArray(),
                Costs = batch.Select(b => b.UnitCost).ToArray(),
                RefTypes = batch.Select(b => b.ReferenceType).ToArray(),
                Notes = batch.Select(b => b.Notes).ToArray(),
                Dates = batch.Select(b => b.MovementDate).ToArray(),
                now
            });

        return ids.Count();
    }

    /// <summary>One PO line, carrying the ids a receipt line needs (its PO and its item).</summary>
    private sealed class PurchaseOrderLine
    {
        public long PoId { get; set; }
        public long LineId { get; set; }
        public long ItemId { get; set; }
    }

    private sealed class PurchaseOrderSeed
    {
        public List<long> PoIds { get; } = new();
        public List<PurchaseOrderLine> Lines { get; } = new();
    }

    private static async Task<PurchaseOrderSeed> SeedPurchaseOrdersAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> supplierIds, List<long> itemIds, List<long> uomIds, int count, DateTime now)
    {
        var linesPerPo = 4;
        var seed = new PurchaseOrderSeed();

        var poIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invpurchaseorder
                  (tenantid, schoolid, campusid, supplierid, ponumber, status, orderdate,
                   expecteddate, totalamount, notes, approvedby, approveddate,
                   createdby, modifiedby, createdon, modifiedon,
                   totaltaxamount, currencycode, exchangerate)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@SupplierIds), unnest(@Numbers), unnest(@Statuses), unnest(@OrderDates),
                     unnest(@ExpectedDates), unnest(@Totals), unnest(@Notes), 1, unnest(@ApprovedOn),
                     1, 1, @now, @now, 0, 'AED', 1
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                SupplierIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => supplierIds[i % supplierIds.Count]).ToArray(),
                Numbers = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-PO-{campusId}-{i + 1:D4}").ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 3 == 0 ? "Draft" : (i % 3 == 1 ? "Approved" : "Received")).ToArray(),
                OrderDates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-120 + i * 3)).ToArray(),
                ExpectedDates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-110 + i * 3)).ToArray(),
                Totals = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 1000m + i * 137m).ToArray(),
                Notes = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF purchase order {i + 1}").ToArray(),
                ApprovedOn = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-119 + i * 3)).ToArray(),
                now
            })).ToList();

        seed.PoIds.AddRange(poIds);

        // Lines: 4 per PO, each pointing at a DIFFERENT item, and `quantityreceived` is
        // left at 0 (lines are not received until a GRN says so).
        //
        // ⚠️ THE IDS ARE RETURNED, AND THE RECEIPT LINES BELOW USE THEM. The first version wrote a
        // placeholder `polineid = 1` and corrected it with a follow-up UPDATE - which worked on an
        // EMPTY database (identity started at 1) and then failed on every re-seed with
        //    23503: insert or update on table "invgrnline" violates foreign key constraint
        //           "invgrnline_polineid_fkey"
        // because line id 1 no longer existed. A placeholder that happens to be valid on the first
        // run is a latent failure, not a shortcut.
        var lineCount = poIds.Count * linesPerPo;
        var linePoIds = poIds.SelectMany(id =>
            System.Linq.Enumerable.Repeat(id, linesPerPo).ToList()).ToArray();
        var lineItemIds = System.Linq.Enumerable.Range(0, lineCount)
            .Select(i => itemIds[i % itemIds.Count]).ToArray();

        var lineIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invpoline
                  (tenantid, schoolid, poid, invitemid, invuomid, quantity, unitprice,
                   quantityreceived, createdby, modifiedby, createdon, modifiedon,
                   campusid, taxcode, taxrate, taxamount)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@PoIds),
                     unnest(@ItemIds), unnest(@UomIds), unnest(@Qtys), unnest(@Prices),
                     0, 1, 1, @now, @now,
                     unnest(@CampusIds), NULL, 0, 0
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, lineCount),
                SchoolIds = Repeat(schoolId, lineCount),
                PoIds = linePoIds,
                ItemIds = lineItemIds,
                UomIds = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => uomIds[i % uomIds.Count]).ToArray(),
                Qtys = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => 5m + (i % 25)).ToArray(),
                Prices = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => 25m + (i % 50) * 3m).ToArray(),
                CampusIds = Repeat(campusId, lineCount),
                now
            })).ToList();

        // `RETURNING id` comes back in INSERT order, which is the order the arrays were built in -
        // so line k belongs to PO linePoIds[k] and carries item lineItemIds[k].
        for (var k = 0; k < lineIds.Count; k++)
        {
            seed.Lines.Add(new PurchaseOrderLine
            {
                PoId = linePoIds[k],
                LineId = lineIds[k],
                ItemId = lineItemIds[k],
            });
        }

        return seed;
    }

    private static async Task<(int Grns, int Lines)> SeedGrnsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> poIds, List<PurchaseOrderLine> poLines, int count, DateTime now)
    {
        var take = Math.Min(count, poIds.Count);
        var linesPerGrn = 2;
        var usedPoIds = poIds.Take(take).ToList();

        // The receipt's lines come from the ORDER'S OWN lines (id + item), so `polineid` and
        // `invitemid` are both real and consistent from the INSERT itself - no placeholder, no
        // repair UPDATE.
        var linesByPo = poLines
            .GroupBy(l => l.PoId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var grnIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invgrn
                  (tenantid, schoolid, campusid, poid, grnnumber, receiveddate, receivedby,
                   status, notes, createdby, modifiedby, createdon, modifiedon,
                   approvedby, approveddate, totaltaxamount, currencycode, exchangerate)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@PoIds), unnest(@Numbers), unnest(@Dates), 1,
                     unnest(@Statuses), unnest(@Notes), 1, 1, @now, @now,
                     1, unnest(@Dates), 0, 'AED', 1
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, take),
                SchoolIds = Repeat(schoolId, take),
                CampusIds = Repeat(campusId, take),
                PoIds = usedPoIds.ToArray(),
                Numbers = System.Linq.Enumerable.Range(0, take)
                    .Select(i => $"PERF-GRN-{campusId}-{i + 1:D4}").ToArray(),
                Dates = System.Linq.Enumerable.Range(0, take)
                    .Select(i => DateTime.Today.AddDays(-90 + i * 2)).ToArray(),
                // Most receipts are Received; every fourth stays Draft so the screen's status
                // filter and its "can this be received?" branch both have rows.
                Statuses = System.Linq.Enumerable.Range(0, take)
                    .Select(i => i % 4 == 3 ? "Draft" : "Received").ToArray(),
                Notes = System.Linq.Enumerable.Range(0, take)
                    .Select(i => $"PERF goods receipt {i + 1}").ToArray(),
                now
            })).ToList();

        // ⚠️ `invgrnline.invitemid` is NOT NULL and the receipt's stock movement is built from it -
        // a receipt line with no item is exactly the defect J8 phase 2 asserts against. Both ids
        // come from the ORDER'S line, so the pair cannot drift.
        var grnLinePoLineIds = new List<long>();
        var grnLineItemIds = new List<long>();
        var grnLineQtys = new List<decimal>();
        var grnLineNotes = new List<string>();
        for (var i = 0; i < grnIds.Count; i++)
        {
            var grnId = grnIds[i];
            var source = linesByPo[usedPoIds[i]];
            for (var j = 0; j < linesPerGrn; j++)
            {
                var line = source[j % source.Count];
                grnLinePoLineIds.Add(line.LineId);
                grnLineItemIds.Add(line.ItemId);
                grnLineQtys.Add(5m + (j % 20));
                grnLineNotes.Add($"PERF receipt line {grnId}-{j + 1}");
            }
        }

        var lineCount = grnLinePoLineIds.Count;
        await conn.ExecuteAsync(
            @"INSERT INTO invgrnline
                  (tenantid, schoolid, grnid, polineid, invitemid,
                   quantityreceived, quantityaccepted, quantityrejected, notes,
                   createdby, modifiedby, createdon, modifiedon,
                   campusid, taxcode, taxrate, taxamount)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@GrnIds),
                     unnest(@PoLineIds), unnest(@ItemIds),
                     unnest(@Qtys), unnest(@Qtys), 0, unnest(@Notes),
                     1, 1, @now, @now,
                     unnest(@CampusIds), NULL, 0, 0",
            new
            {
                TenantIds = Repeat(tenantId, lineCount),
                SchoolIds = Repeat(schoolId, lineCount),
                GrnIds = grnIds.SelectMany(id => System.Linq.Enumerable.Repeat(id, linesPerGrn).ToList()).ToArray(),
                PoLineIds = grnLinePoLineIds.ToArray(),
                ItemIds = grnLineItemIds.ToArray(),
                Qtys = grnLineQtys.ToArray(),
                Notes = grnLineNotes.ToArray(),
                CampusIds = Repeat(campusId, lineCount),
                now
            });

        return (grnIds.Count, lineCount);
    }

    private static async Task<List<long>> SeedAssetsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> itemIds, int count, DateTime now)
    {
        var ids = (await conn.QueryAsync<long>(
            @"INSERT INTO invasset
                  (tenantid, schoolid, campusid, invitemid, assetcode, name, description,
                   purchasedate, purchasecost, usefullifeyears, salvagevalue,
                   depreciationmethod, status, location, serialnumber, warrantyexpiry,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@ItemIds), unnest(@Codes), unnest(@Names), unnest(@Descs),
                     unnest(@PurchaseDates), unnest(@Costs), 5, unnest(@Salvage),
                     'StraightLine', unnest(@Statuses), unnest(@Locations), unnest(@Serials),
                     unnest(@Warranty), 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                ItemIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => itemIds[i % itemIds.Count]).ToArray(),
                Codes = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-ASSET-{campusId}-{i + 1:D4}").ToArray(),
                Names = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Asset {i + 1:D4}").ToArray(),
                Descs = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF asset registered for campus {campusId}").ToArray(),
                PurchaseDates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-400 + i * 5)).ToArray(),
                Costs = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 5000m + i * 250m).ToArray(),
                Salvage = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 500m + i * 25m).ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 7 == 6 ? "Disposed" : "Active").ToArray(),
                Locations = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"Room {100 + i}").ToArray(),
                Serials = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"SN-{campusId:D2}-{i + 1:D5}").ToArray(),
                Warranty = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(300 + i * 2)).ToArray(),
                now
            })).ToList();

        return ids;
    }

    private static async Task<int> SeedDepreciationAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> assetIds, int months, DateTime now)
    {
        if (assetIds.Count == 0 || months <= 0) return 0;

        var total = assetIds.Count * months;
        var rows = new List<DepreciationRow>(total);

        var firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        foreach (var assetId in assetIds)
        {
            var monthly = 100m;
            for (var m = 0; m < months; m++)
            {
                var start = firstOfMonth.AddMonths(-(months - 1 - m));
                rows.Add(new DepreciationRow
                {
                    AssetId = assetId,
                    PeriodStart = start,
                    PeriodEnd = start.AddMonths(1).AddDays(-1),
                    Amount = monthly,
                    Accumulated = monthly * (m + 1),
                    BookValue = Math.Max(0m, 60000m - monthly * (m + 1)),
                });
            }
        }

        // ⚠️ `unnest(...)::date` on the period columns: they are `date`, and unnest of a
        // DateTime[] binds `timestamp` - PostgreSQL refuses the narrower column rather than
        // widening it (the same 42804 the HR seeder hit on attendancedate).
        var inserted = await conn.ExecuteAsync(
            @"INSERT INTO invdepreciation
                  (tenantid, schoolid, campusid, assetid, periodstart, periodend,
                   depreciationamount, accumulateddepreciation, bookvalue,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@AssetIds), unnest(@Starts)::date, unnest(@Ends)::date,
                     unnest(@Amounts), unnest(@Accumulated), unnest(@BookValues),
                     1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, rows.Count),
                SchoolIds = Repeat(schoolId, rows.Count),
                CampusIds = Repeat(campusId, rows.Count),
                AssetIds = rows.Select(r => r.AssetId).ToArray(),
                Starts = rows.Select(r => r.PeriodStart.Date).ToArray(),
                Ends = rows.Select(r => r.PeriodEnd.Date).ToArray(),
                Amounts = rows.Select(r => r.Amount).ToArray(),
                Accumulated = rows.Select(r => r.Accumulated).ToArray(),
                BookValues = rows.Select(r => r.BookValue).ToArray(),
                now
            });

        return inserted;
    }

    private sealed class DepreciationRow
    {
        public long AssetId { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public decimal Amount { get; set; }
        public decimal Accumulated { get; set; }
        public decimal BookValue { get; set; }
    }

    private static async Task<List<long>> SeedStockRequestsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> itemIds, int count, DateTime now)
    {
        // `requestedbyuserid` is NOT NULL and the grid joins Users for the requester's name.
        // A campus's own users are not guaranteed to exist here, so the SYSTEM user (1) is
        // used - the same choice the reference seeders make, and it keeps this seeder from
        // becoming an identity fixture.
        var ids = (await conn.QueryAsync<long>(
            @"INSERT INTO invstockrequest
                  (tenantid, schoolid, campusid, invitemid, qtyrequested, qtyapproved, purpose,
                   status, requestedbyuserid, workflowid, createdby, modifiedby, createdon, modifiedon,
                   requestnumber, requireddate, approvedby, approvedon, approvalremarks)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@ItemIds), unnest(@Qtys), unnest(@Approved), unnest(@Purposes),
                     unnest(@Statuses), 1, NULL, 1, 1, @now, @now,
                     unnest(@Numbers), unnest(@RequiredDates), NULL, NULL, NULL
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                ItemIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => itemIds[i % itemIds.Count]).ToArray(),
                Qtys = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 5m + (i % 30)).ToArray(),
                Approved = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 3 == 0 ? 5m + (i % 30) : 0m).ToArray(),
                Purposes = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF request {i + 1} for classroom supplies").ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => (i % 4) switch
                    {
                        0 => "PendingApproval",
                        1 => "Approved",
                        2 => "Fulfilled",
                        _ => "Rejected"
                    }).ToArray(),
                Numbers = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-SR-{campusId}-{i + 1:D4}").ToArray(),
                RequiredDates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(i).Date).ToArray(),
                now
            })).ToList();

        return ids;
    }

    private static async Task<(int Adjustments, int Lines)> SeedAdjustmentsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> itemIds, int count, DateTime now)
    {
        var linesPer = 2;
        var adjIds = (await conn.QueryAsync<long>(
            @"INSERT INTO invstockadjustment
                  (tenantid, schoolid, campusid, adjustmentnumber, reason, status, adjustmentdate,
                   approvedby, approvedon, notes, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Numbers), unnest(@Reasons), unnest(@Statuses), unnest(@Dates),
                     1, unnest(@Dates), unnest(@Notes), 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Numbers = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-ADJ-{campusId}-{i + 1:D4}").ToArray(),
                Reasons = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF stock count variance {i + 1}").ToArray(),
                // ⚠️ `unitcost` on the lines is load-bearing: posting an adjustment queues an
                // accounting entry worth |qty| x cost, and an all-zero posting is refused one
                // tier down. The statuses also spread so the grid's filter has branches.
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 3 == 0 ? "Draft" : "Posted").ToArray(),
                Dates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-30 + i * 3).Date).ToArray(),
                Notes = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF adjustment {i + 1}").ToArray(),
                now
            })).ToList();

        var lineCount = adjIds.Count * linesPer;
        await conn.ExecuteAsync(
            @"INSERT INTO invstockadjustmentline
                  (tenantid, schoolid, adjustmentid, invitemid, quantity, unitcost, reason, notes,
                   createdby, modifiedby, createdon, modifiedon, campusid)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@AdjIds),
                     unnest(@ItemIds), unnest(@Qtys), unnest(@Costs), unnest(@Reasons), unnest(@Notes),
                     1, 1, @now, @now, unnest(@CampusIds)",
            new
            {
                TenantIds = Repeat(tenantId, lineCount),
                SchoolIds = Repeat(schoolId, lineCount),
                AdjIds = adjIds.SelectMany(id => System.Linq.Enumerable.Repeat(id, linesPer).ToList()).ToArray(),
                ItemIds = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => itemIds[i % itemIds.Count]).ToArray(),
                Qtys = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => (i % 2 == 0 ? 1m : -1m) * (2m + (i % 9))).ToArray(),
                Costs = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => 15m + (i % 20) * 2m).ToArray(),
                Reasons = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => $"PERF line {i + 1}").ToArray(),
                Notes = System.Linq.Enumerable.Range(0, lineCount)
                    .Select(i => $"PERF adjustment line {i + 1}").ToArray(),
                CampusIds = Repeat(campusId, lineCount),
                now
            });

        return (adjIds.Count, lineCount);
    }

    private static async Task<int> SeedReservationsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> itemIds, List<long> requestIds, int count, DateTime now)
    {
        return await conn.ExecuteAsync(
            @"INSERT INTO invreservation
                  (tenantid, schoolid, campusid, invitemid, invstockrequestid, qtyreserved,
                   status, expireson, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@ItemIds), unnest(@RequestIds), unnest(@Qtys),
                     unnest(@Statuses), unnest(@Expires), 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                ItemIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => itemIds[i % itemIds.Count]).ToArray(),
                RequestIds = System.Linq.Enumerable.Range(0, count)
                    .Select(i => requestIds.Count > 0 ? requestIds[i % requestIds.Count] : (long?)null)
                    .ToArray(),
                Qtys = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 1m + (i % 10)).ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 3 == 0 ? "Pending" : "Fulfilled").ToArray(),
                Expires = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(7 + i).Date).ToArray(),
                now
            });
    }
}
