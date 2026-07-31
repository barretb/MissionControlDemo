using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MissionControl.Api.Data;
using MissionControl.Api.Models;
using MissionControl.ServiceDefaults;
using OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults: OpenTelemetry (logs/metrics/traces), health checks, service discovery.
builder.AddServiceDefaults();

// Aspire integration: reads the "missionsdb" connection string injected by the AppHost and
// wires EF Core + Npgsql, including built-in DB instrumentation (spans for queries).
builder.AddNpgsqlDbContext<MissionDbContext>("missionsdb");

// Register the custom missions_launched counter wrapper (see MissionMetrics).
builder.Services.AddMissionMetrics();

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
app.MapPost("/api/missions/reset", async (MissionDbContext db, ILogger<Program> logger) =>
{
    using var activity = MissionTelemetry.ActivitySource.StartActivity("ResetRoster", ActivityKind.Internal);

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
// POST /api/missions/{id}/launch - the demo centerpiece.
// Shows: reading Baggage, a manual span, the reusable DB-span factory, the custom counter,
// structured logging (auto-carries TraceId/SpanId), and the "force failure" error path.
// -------------------------------------------------------------------------------------------------
app.MapPost("/api/missions/{id:int}/launch", async (
    int id,
    LaunchRequest request,
    MissionDbContext db,
    MissionMetrics metrics,
    ILogger<Program> logger) =>
{
    var mission = await db.Missions.FindAsync(id);
    if (mission is null)
    {
        return Results.NotFound(new { message = $"No mission with id {id}." });
    }

    // BAGGAGE: read commander/priority propagated from the Web tier via the W3C "baggage" header.
    // Fall back to the request body if baggage was not set.
    var commander = Baggage.GetBaggage("mission.commander") ?? request.Commander ?? "Unknown";
    var priority = Baggage.GetBaggage("mission.priority") ?? request.Priority ?? "Routine";

    // MANUAL SPAN: an internal span for the whole launch operation, nested under the ASP.NET
    // Core server span. Created from the shared ActivitySource registered in ServiceDefaults.
    using var activity = MissionTelemetry.ActivitySource.StartActivity("LaunchMission", ActivityKind.Internal);
    activity?.SetTag("mission.id", mission.Id);
    activity?.SetTag("mission.name", mission.Name);
    activity?.SetTag("mission.registry", mission.Registry);
    activity?.SetTag("mission.commander", commander);
    activity?.SetTag("mission.priority", priority);

    var launch = new Launch
    {
        MissionId = mission.Id,
        Commander = commander,
        Priority = priority,
        LaunchedAtUtc = DateTime.UtcNow,
        Success = !request.ForceFailure
    };

    // FORCE FAILURE path: error log + error span + recorded exception + HTTP 500.
    if (request.ForceFailure)
    {
        mission.Status = "Launch Failure";

        // REUSABLE SPAN FACTORY: wrap the DB write; the span is auto-tagged with the Launch's
        // properties as db.entity.* attributes.
        using (MissionTelemetry.ActivitySource.StartDatabaseSpan("db.launch.insert", launch))
        {
            db.Launches.Add(launch);
            await db.SaveChangesAsync();
        }

        // Custom metric, tagged with the ship name and success=false.
        metrics.MissionLaunched(mission.Name, success: false);

        var failure = new InvalidOperationException($"Warp core breach during launch of {mission.Name}.");
        activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
        activity?.AddException(failure); // records an exception event on the span

        // Structured log. Because logs flow through OpenTelemetry, this record carries TraceId/SpanId.
        logger.LogError(failure,
            "Mission launch FAILED: {MissionName}, commander {Commander}, status {Status}",
            mission.Name, commander, mission.Status);

        return Results.Problem(
            title: "Launch failure",
            detail: failure.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }

    // SUCCESS path.
    mission.Status = "Launched";

    using (MissionTelemetry.ActivitySource.StartDatabaseSpan("db.launch.insert", launch))
    {
        db.Launches.Add(launch);
        await db.SaveChangesAsync();
    }

    metrics.MissionLaunched(mission.Name, success: true);

    logger.LogInformation(
        "Mission launch requested: {MissionName}, commander {Commander}, status {Status}",
        mission.Name, commander, mission.Status);

    return Results.Ok(new
    {
        mission.Id,
        mission.Name,
        mission.Registry,
        mission.Status,
        launch.Commander,
        launch.Priority,
        launch.Success,
        launch.LaunchedAtUtc
    });
});

app.Run();
