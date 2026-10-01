using Dapper;
using Npgsql;

namespace SchoolPerformance.Seeders;

/// <summary>Options for <see cref="TransportModuleSeeder"/>.</summary>
public sealed class TransportSeedOptions
{
    public int VehiclesPerCampus { get; set; } = 15;
    public int DriversPerCampus { get; set; } = 20;
    public int AttendantsPerCampus { get; set; } = 15;
    public int RoutesPerCampus { get; set; } = 12;

    /// <summary>Stops on each route, numbered in sequence. The stop grid is per route.</summary>
    public int StopsPerRoute { get; set; } = 8;

    /// <summary>
    /// Students riding the bus. `transportstudentassignment` is the module's volume table,
    /// and it is keyed on an ENROLMENT - not a student - so this is capped by the campus's
    /// own enrolments.
    /// </summary>
    public int RidersPerCampus { get; set; } = 500;

    /// <summary>Re-seed even when the campus already holds transport rows.</summary>
    public bool Force { get; set; }
}

/// <summary>What one campus's transport seed produced.</summary>
public sealed class TransportSeedResult
{
    public bool Skipped { get; set; }
    public int Vehicles { get; set; }
    public int Drivers { get; set; }
    public int Attendants { get; set; }
    public int Routes { get; set; }
    public int RouteStops { get; set; }
    public int VehicleAssignments { get; set; }
    public int StudentAssignments { get; set; }
}

/// <summary>
/// Seeds the TRANSPORT module's tables for one campus.
///
/// WHY THIS EXISTS
/// ---------------
/// All seven `transport*` tables held ZERO rows in `ayra_perf`, so every transport grid reported
/// SKIP. The module is SMALL by nature - a campus runs a dozen buses, not a million - so the point
/// is not volume for its own sake. It is that a grid over an EMPTY table cannot be measured at all,
/// and several of this module's rules only exist as ROWS (which vehicle is currently assigned,
/// which students ride which route), so a seeder is what makes those assertions measurable.
///
/// ⚠️ `ux_transportvehicleassignment_activevehicle` is a PARTIAL unique index on the ACTIVE row -
/// **one active assignment per vehicle** - so this seeder writes exactly one active row per vehicle
/// and leaves the rest of the fleet unassigned. That is the same rule the vehicle-assignment screen
/// enforces before it posts, and a second active row here would be a 23505.
///
/// ⚠️ RIDERS ARE ENROLMENTS, NOT STUDENTS. `transportstudentassignment.studentenrollmentid` is NOT
/// NULL and the transport student list joins through it, so the riders are drawn from the campus's
/// own `studentenrollment` rows. A campus with no enrolments gets no riders - and the seeder says so
/// rather than writing rows that point at nothing.
///
/// ⚠️ A STUDENT MAY HOLD ONLY ONE ACTIVE ASSIGNMENT (the service refuses a duplicate), so each
/// enrolment appears at most once and the rider count is capped by the enrolment count.
/// </summary>
public sealed class TransportModuleSeeder : BaseSeeder
{
    public TransportModuleSeeder(string connectionString) : base(connectionString) { }

    /// <summary>Tables a bulk transport load invalidates.</summary>
    public static readonly string[] TablesToAnalyze =
    {
        "transportvehicle", "transportdriver", "transportattendant", "transportroute",
        "transportroutestop", "transportvehicleassignment", "transportstudentassignment"
    };

    private static readonly string[] VehicleTypes = { "Bus", "Van", "Mini Bus" };
    private static readonly string[] VehicleStatuses = { "Active", "Active", "Active", "Maintenance" };
    private static readonly string[] DriverStatuses = { "Active", "Active", "OnLeave" };
    private static readonly string[] StopNames =
    {
        "Main Gate", "Clock Tower", "Central Market", "Corniche", "Al Bahia",
        "Marina Mall", "Airport Road", "City Centre", "Park Avenue", "Harbour Point"
    };

    public async Task<TransportSeedResult> SeedAsync(
        long tenantId, long schoolId, long campusId, TransportSeedOptions options, bool verbose = true)
    {
        var result = new TransportSeedResult();
        using var conn = await OpenConnectionAsync();

        var existing = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM transportvehicle
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId",
            new { tenantId, schoolId, campusId });

        if (existing > 0 && !options.Force)
        {
            result.Skipped = true;
            result.Vehicles = (int)existing;
            if (verbose)
                Console.WriteLine($"  Transport: campus {campusId} already holds {existing} vehicles - skipped");
            return result;
        }

        var now = DateTime.UtcNow;

        // Children first: assignments point at vehicles/routes/stops, stops at routes.
        if (options.Force && existing > 0)
        {
            foreach (var table in new[]
                     {
                         "transportstudentassignment", "transportvehicleassignment",
                         "transportroutestop", "transportroute",
                         "transportvehicle", "transportdriver", "transportattendant"
                     })
            {
                await ClearTableAsync(conn, table, tenantId, schoolId, campusId);
            }

            if (verbose)
                Console.WriteLine($"  Transport: campus {campusId} cleared for a forced re-seed");
        }

        // ------------------------------------------------------------------
        // 1. Fleet and crew.
        // ------------------------------------------------------------------
        var vehicleIds = await SeedVehiclesAsync(
            conn, tenantId, schoolId, campusId, options.VehiclesPerCampus, now);
        result.Vehicles = vehicleIds.Count;

        var driverIds = await SeedDriversAsync(
            conn, tenantId, schoolId, campusId, options.DriversPerCampus, now);
        result.Drivers = driverIds.Count;

        var attendantIds = await SeedAttendantsAsync(
            conn, tenantId, schoolId, campusId, options.AttendantsPerCampus, now);
        result.Attendants = attendantIds.Count;

        if (vehicleIds.Count == 0)
            throw new InvalidOperationException(
                $"Transport seeding wrote no vehicles for campus {campusId}, so its routes and " +
                "assignments cannot be seeded either.");

        // ------------------------------------------------------------------
        // 2. Routes and their stops (numbered in sequence - the stop grid orders by it).
        // ------------------------------------------------------------------
        var routeIds = await SeedRoutesAsync(
            conn, tenantId, schoolId, campusId, options.RoutesPerCampus, now);
        result.Routes = routeIds.Count;

        result.RouteStops = await SeedRouteStopsAsync(
            conn, tenantId, schoolId, campusId, routeIds, options.StopsPerRoute, now);

        // ------------------------------------------------------------------
        // 3. The vehicle assignment desk. ONE ACTIVE row per vehicle - see the class comment.
        // ------------------------------------------------------------------
        result.VehicleAssignments = await SeedVehicleAssignmentsAsync(
            conn, tenantId, schoolId, campusId, vehicleIds, driverIds, attendantIds, routeIds, now);

        // ------------------------------------------------------------------
        // 4. Riders - from the campus's own enrolments.
        // ------------------------------------------------------------------
        result.StudentAssignments = await SeedStudentAssignmentsAsync(
            conn, tenantId, schoolId, campusId, routeIds, vehicleIds, options.RidersPerCampus,
            now, verbose);

        if (verbose)
        {
            Console.WriteLine(
                $"  Transport: campus {campusId} -> {result.Vehicles} vehicles, {result.Drivers} drivers, " +
                $"{result.Attendants} attendants, {result.Routes} routes (+{result.RouteStops} stops), " +
                $"{result.VehicleAssignments} active vehicle assignments, " +
                $"{result.StudentAssignments:N0} riders");
        }

        return result;
    }

    private static long[] Repeat(long value, int count)
        => System.Linq.Enumerable.Repeat(value, count).ToArray();

    private static async Task<List<long>> SeedVehiclesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int count, DateTime now)
    {
        return (await conn.QueryAsync<long>(
            @"INSERT INTO transportvehicle
                  (tenantid, schoolid, campusid, vehiclenumber, vehiclename, registrationnumber,
                   vehicletype, capacity, gpsdeviceid, fueltype, manufacturer, model, year,
                   insuranceexpiry, registrationexpiry, fitnessexpiry, status, notes,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Numbers), unnest(@Names), unnest(@Registrations),
                     unnest(@Types), unnest(@Capacities), unnest(@Gps), 'Diesel',
                     'Toyota', unnest(@Models), unnest(@Years),
                     unnest(@Expiries), unnest(@Expiries), unnest(@Expiries),
                     unnest(@Statuses), unnest(@Notes), 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Numbers = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-BUS-{campusId:D2}-{i + 1:D3}").ToArray(),
                Names = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Vehicle {i + 1:D2}").ToArray(),
                Registrations = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-REG-{campusId:D2}-{i + 1:D4}").ToArray(),
                Types = System.Linq.Enumerable.Range(0, count)
                    .Select(i => VehicleTypes[i % VehicleTypes.Length]).ToArray(),
                Capacities = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 20 + (i % 3) * 10).ToArray(),
                Gps = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-GPS-{campusId:D2}-{i + 1:D4}").ToArray(),
                Models = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"Coaster {2000 + i}").ToArray(),
                Years = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 2015 + (i % 9)).ToArray(),
                Expiries = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(200 + i * 5)).ToArray(),
                // One in four is off the road, so the fleet grid's status filter has branches.
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => VehicleStatuses[i % VehicleStatuses.Length]).ToArray(),
                Notes = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF fleet vehicle {i + 1}").ToArray(),
                now
            })).ToList();
    }

    private static async Task<List<long>> SeedDriversAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int count, DateTime now)
    {
        return (await conn.QueryAsync<long>(
            @"INSERT INTO transportdriver
                  (tenantid, schoolid, campusid, name, mobile, cnic, licensenumber, licenseexpiry,
                   joiningdate, emergencycontact, status, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Mobiles), unnest(@Cnics), unnest(@Licenses),
                     unnest(@Expiries), unnest(@JoinDates), unnest(@Emergency), unnest(@Statuses),
                     1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Names = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Driver {i + 1:D2}").ToArray(),
                Mobiles = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"05{campusId:D2}{i:D6}").ToArray(),
                Cnics = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-CNIC-{campusId:D2}-{i + 1:D4}").ToArray(),
                Licenses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF-LIC-{campusId:D2}-{i + 1:D4}").ToArray(),
                Expiries = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(300 + i * 4)).ToArray(),
                JoinDates = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DateTime.Today.AddDays(-800 + i * 10)).ToArray(),
                Emergency = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Emergency {i + 1}").ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => DriverStatuses[i % DriverStatuses.Length]).ToArray(),
                now
            })).ToList();
    }

    private static async Task<List<long>> SeedAttendantsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int count, DateTime now)
    {
        return (await conn.QueryAsync<long>(
            @"INSERT INTO transportattendant
                  (tenantid, schoolid, campusid, name, phone, status,
                   createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Phones), unnest(@Statuses), 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Names = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Attendant {i + 1:D2}").ToArray(),
                Phones = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"05{campusId:D2}{i + 500:D6}").ToArray(),
                Statuses = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 5 == 4 ? "Inactive" : "Active").ToArray(),
                now
            })).ToList();
    }

    private static async Task<List<long>> SeedRoutesAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId, int count, DateTime now)
    {
        return (await conn.QueryAsync<long>(
            @"INSERT INTO transportroute
                  (tenantid, schoolid, campusid, routename, routetype, distance, estimatedduration,
                   starttime, endtime, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@Names), unnest(@Types), unnest(@Distances), unnest(@Durations),
                     unnest(@Starts), unnest(@Ends), 1, 1, @now, @now
              RETURNING id",
            new
            {
                TenantIds = Repeat(tenantId, count),
                SchoolIds = Repeat(schoolId, count),
                CampusIds = Repeat(campusId, count),
                Names = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"PERF Route {i + 1:D2}").ToArray(),
                Types = System.Linq.Enumerable.Range(0, count)
                    .Select(i => i % 2 == 0 ? "Morning" : "Both").ToArray(),
                Distances = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 5m + i * 1.5m).ToArray(),
                Durations = System.Linq.Enumerable.Range(0, count)
                    .Select(i => 20 + i * 3).ToArray(),
                Starts = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"06:{30 + i % 20:D2}").ToArray(),
                Ends = System.Linq.Enumerable.Range(0, count)
                    .Select(i => $"07:{30 + i % 20:D2}").ToArray(),
                now
            })).ToList();
    }

    private static async Task<int> SeedRouteStopsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> routeIds, int stopsPerRoute, DateTime now)
    {
        if (routeIds.Count == 0 || stopsPerRoute <= 0) return 0;

        var total = routeIds.Count * stopsPerRoute;
        return await conn.ExecuteAsync(
            @"INSERT INTO transportroutestop
                  (tenantid, schoolid, campusid, routeid, sequence, stopname, landmark,
                   latitude, longitude, pickuptime, droptime, createdby, modifiedby,
                   createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@RouteIds), unnest(@Sequences), unnest(@Names), unnest(@Landmarks),
                     unnest(@Lats), unnest(@Lngs), unnest(@Pickups), unnest(@Drops),
                     1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, total),
                SchoolIds = Repeat(schoolId, total),
                CampusIds = Repeat(campusId, total),
                RouteIds = routeIds.SelectMany(id =>
                    System.Linq.Enumerable.Repeat(id, stopsPerRoute).ToList()).ToArray(),
                Sequences = System.Linq.Enumerable.Range(0, total)
                    .Select(i => (i % stopsPerRoute) + 1).ToArray(),
                Names = System.Linq.Enumerable.Range(0, total)
                    .Select(i => $"{StopNames[i % StopNames.Length]} {i + 1:D3}").ToArray(),
                Landmarks = System.Linq.Enumerable.Range(0, total)
                    .Select(i => $"PERF landmark {i + 1}").ToArray(),
                Lats = System.Linq.Enumerable.Range(0, total)
                    .Select(i => 24.4m + i * 0.001m).ToArray(),
                Lngs = System.Linq.Enumerable.Range(0, total)
                    .Select(i => 54.4m + i * 0.001m).ToArray(),
                Pickups = System.Linq.Enumerable.Range(0, total)
                    .Select(i => $"06:{10 + i % 50:D2}").ToArray(),
                Drops = System.Linq.Enumerable.Range(0, total)
                    .Select(i => $"14:{10 + i % 50:D2}").ToArray(),
                now
            });
    }

    private static async Task<int> SeedVehicleAssignmentsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> vehicleIds, List<long> driverIds, List<long> attendantIds,
        List<long> routeIds, DateTime now)
    {
        // ⚠️ ONE ROW PER VEHICLE, and `isactive = true` on every one of them. The partial unique
        // index `ux_transportvehicleassignment_activevehicle` is on (vehicleid) WHERE isactive, so a
        // second active row for the same vehicle is a 23505 - the same rule the assignment screen
        // pre-checks before it posts.
        return await conn.ExecuteAsync(
            @"INSERT INTO transportvehicleassignment
                  (tenantid, schoolid, campusid, vehicleid, driverid, attendantid, routeid,
                   effectivedate, isactive, createdby, modifiedby, createdon, modifiedon)
              SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                     unnest(@VehicleIds), unnest(@DriverIds), unnest(@AttendantIds),
                     unnest(@RouteIds), unnest(@Effective)::date, true, 1, 1, @now, @now",
            new
            {
                TenantIds = Repeat(tenantId, vehicleIds.Count),
                SchoolIds = Repeat(schoolId, vehicleIds.Count),
                CampusIds = Repeat(campusId, vehicleIds.Count),
                VehicleIds = vehicleIds.ToArray(),
                DriverIds = vehicleIds.Select((_, i) =>
                    driverIds.Count > 0 ? driverIds[i % driverIds.Count] : (long?)null).ToArray(),
                AttendantIds = vehicleIds.Select((_, i) =>
                    attendantIds.Count > 0 ? attendantIds[i % attendantIds.Count] : (long?)null).ToArray(),
                RouteIds = vehicleIds.Select((_, i) =>
                    routeIds.Count > 0 ? routeIds[i % routeIds.Count] : (long?)null).ToArray(),
                Effective = System.Linq.Enumerable.Range(0, vehicleIds.Count)
                    .Select(i => DateTime.Today.AddDays(-200 + i).Date).ToArray(),
                now
            });
    }

    private async Task<int> SeedStudentAssignmentsAsync(
        NpgsqlConnection conn, long tenantId, long schoolId, long campusId,
        List<long> routeIds, List<long> vehicleIds, int requested, DateTime now, bool verbose)
    {
        // ⚠️ The rider is an ENROLMENT. Reading them from THIS campus is what keeps the row
        // resolvable by the transport student list.
        var enrolmentIds = (await conn.QueryAsync<long>(
            @"SELECT id FROM studentenrollment
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY id LIMIT @requested",
            new { tenantId, schoolId, campusId, requested })).ToList();

        if (enrolmentIds.Count == 0)
        {
            if (verbose)
                Console.WriteLine(
                    $"  Transport: campus {campusId} has no enrolments, so no riders were seeded " +
                    "(a rider row needs a real studentenrollmentid).");
            return 0;
        }

        // The stops are per route, so a rider's pickup/drop stop is picked from ITS OWN route's
        // stops - a stop belonging to another route is a data shape the service would reject.
        var routeStopIds = (await conn.QueryAsync<RouteStopRow>(
            @"SELECT routeid AS RouteId, id AS StopId FROM transportroutestop
               WHERE tenantid = @tenantId AND schoolid = @schoolId AND campusid = @campusId
               ORDER BY routeid, sequence",
            new { tenantId, schoolId, campusId })).ToList();

        var inserted = 0;
        const int batchSize = 500;

        for (var start = 0; start < enrolmentIds.Count; start += batchSize)
        {
            var take = Math.Min(batchSize, enrolmentIds.Count - start);
            var slice = enrolmentIds.Skip(start).Take(take).ToList();

            var pickups = new long?[take];
            var drops = new long?[take];
            var routes = new long?[take];
            var vehicles = new long?[take];

            for (var i = 0; i < take; i++)
            {
                var globalIndex = start + i;
                routes[i] = routeIds.Count > 0 ? routeIds[globalIndex % routeIds.Count] : (long?)null;
                vehicles[i] = vehicleIds.Count > 0 ? vehicleIds[globalIndex % vehicleIds.Count] : (long?)null;

                if (routes[i].HasValue && routeStopIds.Count > 0)
                {
                    var stops = routeStopIds.Where(s => s.RouteId == routes[i]!.Value).ToList();
                    if (stops.Count > 0)
                    {
                        pickups[i] = stops[globalIndex % stops.Count].StopId;
                        drops[i] = stops[(globalIndex + 1) % stops.Count].StopId;
                    }
                }
            }

            inserted += await conn.ExecuteAsync(
                @"INSERT INTO transportstudentassignment
                      (tenantid, schoolid, campusid, studentenrollmentid, routeid, vehicleid,
                       pickupstopid, dropstopid, startdate, enddate, monthlyfee, discount, status,
                       createdby, modifiedby, createdon, modifiedon)
                  SELECT unnest(@TenantIds), unnest(@SchoolIds), unnest(@CampusIds),
                         unnest(@EnrolmentIds), unnest(@RouteIds), unnest(@VehicleIds),
                         unnest(@PickupIds), unnest(@DropIds), unnest(@StartDates)::date,
                         NULL, unnest(@Fees), unnest(@Discounts), unnest(@Statuses),
                         1, 1, @now, @now",
                new
                {
                    TenantIds = Repeat(tenantId, take),
                    SchoolIds = Repeat(schoolId, take),
                    CampusIds = Repeat(campusId, take),
                    EnrolmentIds = slice.ToArray(),
                    RouteIds = routes,
                    VehicleIds = vehicles,
                    PickupIds = pickups,
                    DropIds = drops,
                    StartDates = System.Linq.Enumerable.Range(start, take)
                        .Select(i => DateTime.Today.AddDays(-120 + i % 30).Date).ToArray(),
                    Fees = System.Linq.Enumerable.Range(start, take)
                        .Select(i => 300m + (i % 5) * 50m).ToArray(),
                    Discounts = System.Linq.Enumerable.Range(start, take)
                        .Select(i => i % 7 == 6 ? 50m : 0m).ToArray(),
                    // Most riders are Active; a few have left, so the grid's status filter has
                    // more than one branch and the release path has rows to act on.
                    Statuses = System.Linq.Enumerable.Range(start, take)
                        .Select(i => i % 11 == 10 ? "Cancelled" : "Active").ToArray(),
                    now
                });
        }

        if (verbose)
            LogProgress($"  Transport riders (campus {campusId})", inserted, enrolmentIds.Count);

        return inserted;
    }

    private sealed class RouteStopRow
    {
        public long RouteId { get; set; }
        public long StopId { get; set; }
    }
}
