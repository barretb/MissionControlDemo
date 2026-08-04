using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MissionControl.Api.Data;
using MissionControl.Api.Models;
using MissionControl.Api.Simulation;
using MissionControl.ServiceDefaults;
using OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults: OpenTelemetry (logs/metrics/traces), health checks, service discovery.
builder.AddServiceDefaults();

// In-memory SQLite: real SQL (so EF Core spans still show up in traces) with zero persistence —
// the database exists only while the app runs, so every run starts fresh. A named shared-cache
// in-memory database lives as long as at least one connection stays open; this keep-alive
// connection is registered as a singleton so DI holds it (and the data) for the app's lifetime.
const string inMemoryConnectionString = "Data Source=missionsdb;Mode=Memory;Cache=Shared";
var keepAliveConnection = new SqliteConnection(inMemoryConnectionString);
keepAliveConnection.Open();
builder.Services.AddSingleton(keepAliveConnection);
builder.Services.AddDbContext<MissionDbContext>(options => options.UseSqlite(inMemoryConnectionString));

// Register the custom missions_launched counter wrapper (see MissionMetrics).
builder.Services.AddMissionMetrics();

// Background flight simulator: emits live traces + metrics every 2s per active mission.
builder.Services.AddSingleton<MissionSimulator>();

builder.Services.AddProblemDetails();

var app = builder.Build();

// Maps /health and /alive in development.
app.MapDefaultEndpoints();

// Create the schema and seed the starship roster on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MissionDbContext>();
    db.Database.EnsureCreated();
    MissionSeeder.Seed(db);
}

// -------------------------------------------------------------------------------------------------
// GET /api/missions - list the roster.
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/missions", async (MissionDbContext db) =>
    await db.Missions.OrderBy(m => m.Id).ToListAsync());

// -------------------------------------------------------------------------------------------------
// GET /api/baggage - returns the W3C Baggage currently in scope.
// Because the Web front-end sets Baggage before calling us, and HttpClient instrumentation
// propagates it over the "baggage" header, this endpoint demonstrates automatic propagation.
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/baggage", () =>
{
    var items = Baggage.Current.GetBaggage(); // IReadOnlyDictionary<string, string>
    return Results.Ok(items);
});

// -------------------------------------------------------------------------------------------------
// GET /api/missions/{id}/launches - launch history + aggregate telemetry for one mission.
// Powers the "Live Telemetry" panel in the UI.
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/missions/{id:int}/launches", async (int id, MissionDbContext db) =>
{
    var mission = await db.Missions.FindAsync(id);
    if (mission is null)
    {
        return Results.NotFound(new { message = $"No mission with id {id}." });
    }

    var launches = await db.Launches
        .Where(l => l.MissionId == id)
        .OrderByDescending(l => l.LaunchedAtUtc)
        .Take(10)
        .ToListAsync();

    var total = await db.Launches.CountAsync(l => l.MissionId == id);
    var successes = await db.Launches.CountAsync(l => l.MissionId == id && l.Success);
    var last = launches.FirstOrDefault();

    return Results.Ok(new
    {
        mission.Id,
        mission.Name,
        mission.Status,
        total,
        successes,
        failures = total - successes,
        lastCommander = last?.Commander,
        lastPriority = last?.Priority,
        lastLaunchedAtUtc = last?.LaunchedAtUtc,
        recent = launches.Select(l => new { l.Commander, l.Priority, l.Success, l.LaunchedAtUtc })
    });
});

// -------------------------------------------------------------------------------------------------
// POST /api/missions/reset - return every ship to "Docked".
// Wrapped in a manual span and logged, so "Reset All" also shows up as telemetry.
// -------------------------------------------------------------------------------------------------
app.MapPost("/api/missions/reset", async (MissionDbContext db, MissionSimulator simulator, ILogger<Program> logger) =>
{
    using var activity = MissionTelemetry.ActivitySource.StartActivity("ResetRoster", ActivityKind.Internal);

    // Abort any in-flight missions first, then dock everything.
    simulator.CancelAll();

    var missions = await db.Missions.ToListAsync();
    foreach (var m in missions)
    {
        m.Status = "Docked";
    }
    await db.SaveChangesAsync();

    activity?.SetTag("missions.reset", missions.Count);
    logger.LogInformation("Roster reset: {Count} missions returned to Docked.", missions.Count);

    return Results.Ok(new { reset = missions.Count });
});

// -------------------------------------------------------------------------------------------------
// GET /api/missions/{id}/telemetry - live flight snapshot (warp / shields / torpedoes / status).
// Polled by the UI every ~2s so the Live Telemetry panel shows real-time values.
// -------------------------------------------------------------------------------------------------
app.MapGet("/api/missions/{id:int}/telemetry", (int id, MissionSimulator simulator) =>
{
    var snapshot = simulator.GetSnapshot(id);
    return snapshot is not null
        ? Results.Ok(snapshot)
        : Results.Ok(new { missionId = id, active = false });
});

// -------------------------------------------------------------------------------------------------
// POST /api/missions/{id}/launch - the demo centerpiece.
// Shows: reading Baggage, a manual span, the custom counter, and structured logging. The launch
// kicks off a background flight (MissionSimulator) that emits live traces + metrics every 2s until
// it ends in Success, Failure (shields hit 0), or Retreat (torpedoes hit 0).
// -------------------------------------------------------------------------------------------------
app.MapPost("/api/missions/{id:int}/launch", async (
    int id,
    LaunchRequest request,
    MissionDbContext db,
    MissionMetrics metrics,
    MissionSimulator simulator,
    ILogger<Program> logger) =>
{
    var mission = await db.Missions.FindAsync(id);
    if (mission is null)
    {
        return Results.NotFound(new { message = $"No mission with id {id}." });
    }

    // LAUNCH LOCK: a ship already on a mission cannot launch again until the current mission ends.
    if (simulator.IsActive(id))
    {
        return Results.Conflict(new { message = $"{mission.Name} is already on a mission." });
    }

    // BAGGAGE: read commander/priority propagated from the Web tier via the W3C "baggage" header.
    // Fall back to the request body if baggage was not set.
    var commander = Baggage.GetBaggage("mission.commander") ?? request.Commander ?? "Unknown";
    var priority = Baggage.GetBaggage("mission.priority") ?? request.Priority ?? "Routine";

    // MANUAL SPAN: an internal span for the launch action, nested under the ASP.NET Core server span.
    using var activity = MissionTelemetry.ActivitySource.StartActivity("LaunchMission", ActivityKind.Internal);
    activity?.SetTag("mission.id", mission.Id);
    activity?.SetTag("mission.name", mission.Name);
    activity?.SetTag("mission.registry", mission.Registry);
    activity?.SetTag("mission.commander", commander);
    activity?.SetTag("mission.priority", priority);
    activity?.SetTag("mission.force_failure", request.ForceFailure);

    // Mark the ship as flying and count the launch.
    mission.Status = "In Flight";
    await db.SaveChangesAsync();
    metrics.MissionLaunched(mission.Name, success: true);

    // Begin the continuous-telemetry flight in the background.
    simulator.TryStart(mission, commander, priority, request.ForceFailure);

    logger.LogInformation(
        "Mission launch initiated: {MissionName}, commander {Commander}, priority {Priority}",
        mission.Name, commander, priority);

    // 202 Accepted: the mission is now under way; poll /telemetry for live values.
    return Results.Accepted(
        $"/api/missions/{id}/telemetry",
        simulator.GetSnapshot(id) ?? (object)new { mission.Id, mission.Name, status = "In Flight" });
});

app.Run();
