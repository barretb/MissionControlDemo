using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MissionControl.Api.Data;
using MissionControl.Api.Models;
using MissionControl.ServiceDefaults;

namespace MissionControl.Api.Simulation;

/// <summary>
/// A point-in-time view of an in-flight (or just-finished) mission, returned to the UI so it can
/// render live warp / shields / torpedoes every couple of seconds.
/// </summary>
public sealed record MissionSnapshot(
    int MissionId,
    string Name,
    string Status,
    bool Active,
    double WarpSpeed,
    int ShieldStrength,
    int PhotonTorpedoes,
    int ElapsedSeconds,
    int DurationSeconds,
    string LastEvent,
    string Commander,
    string Priority);

/// <summary>
/// TALK HIGHLIGHT - continuous telemetry.
///
/// Runs a background "flight" for each launched mission. Every 2 seconds a random event fires and
/// the ship's warp speed, shield strength, and photon-torpedo count change. Each tick is recorded as:
///   * a TRACE span ("mission.tick") parented to a long-lived "Mission: {ship}" root span, and
///   * METRICS (gauges for warp/shields/torpedoes, counters for events and outcomes).
///
/// End conditions:
///   * shields reach 0            -> Failure
///   * photon torpedoes reach 0   -> Retreat
///   * otherwise after 20-40s     -> Success
///
/// State is held in memory so the UI can poll <see cref="GetSnapshot"/> for live values. Simulation
/// runs on a background task and touches the database only at start/finish via a DI scope.
/// </summary>
public sealed class MissionSimulator : IDisposable
{
    private const int TickMs = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MissionSimulator> _logger;

    private readonly ConcurrentDictionary<int, Run> _runs = new();
    private readonly ConcurrentDictionary<int, MissionSnapshot> _last = new();

    // Metrics use the shared meter name, so they flow through the AddMeter registration in ServiceDefaults.
    private readonly Gauge<double> _warp;
    private readonly Gauge<int> _shields;
    private readonly Gauge<int> _torpedoes;
    private readonly Counter<long> _events;
    private readonly Counter<long> _completed;

    public MissionSimulator(
        IMeterFactory meterFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<MissionSimulator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var meter = meterFactory.Create(MissionTelemetry.MeterName);
        _warp = meter.CreateGauge<double>("mission.warp_speed", unit: "warp", description: "Current warp factor of an in-flight mission.");
        _shields = meter.CreateGauge<int>("mission.shield_strength", unit: "%", description: "Current shield strength of an in-flight mission.");
        _torpedoes = meter.CreateGauge<int>("mission.photon_torpedoes", unit: "{torpedo}", description: "Remaining photon torpedoes.");
        _events = meter.CreateCounter<long>("mission.events", unit: "{event}", description: "In-flight mission events by type.");
        _completed = meter.CreateCounter<long>("mission.completed", unit: "{mission}", description: "Completed missions by outcome.");
    }

    /// <summary>True while a mission is actively flying (used to block re-launch).</summary>
    public bool IsActive(int missionId) => _runs.ContainsKey(missionId);

    /// <summary>Live snapshot if flying; otherwise the final snapshot of the last flight, or null.</summary>
    public MissionSnapshot? GetSnapshot(int missionId) =>
        _runs.TryGetValue(missionId, out var run) ? run.Snapshot()
        : _last.TryGetValue(missionId, out var last) ? last
        : null;

    /// <summary>Starts a background flight. Returns false if the ship is already on a mission.</summary>
    public bool TryStart(Mission mission, string commander, string priority, bool forceFailure)
    {
        var run = new Run(mission.Id, mission.Name, commander, priority, forceFailure);
        if (!_runs.TryAdd(mission.Id, run))
        {
            return false;
        }

        // Fire-and-forget the flight loop.
        _ = Task.Run(() => RunAsync(run));
        return true;
    }

    /// <summary>Cancels every active flight (used by "Reset All").</summary>
    public void CancelAll()
    {
        foreach (var run in _runs.Values)
        {
            run.Cancellation.Cancel();
        }
    }

    private async Task RunAsync(Run run)
    {
        var rng = new Random();

        // One long-lived root span for the whole mission; each tick is a child span.
        using var root = MissionTelemetry.ActivitySource.StartActivity($"Mission: {run.Name}", ActivityKind.Internal);
        root?.SetTag("mission.name", run.Name);
        root?.SetTag("mission.commander", run.Commander);
        root?.SetTag("mission.priority", run.Priority);
        root?.SetTag("mission.duration_seconds", run.DurationSeconds);

        var start = DateTime.UtcNow;
        var outcome = "Success";

        try
        {
            while (!run.Cancellation.IsCancellationRequested)
            {
                await Task.Delay(TickMs, run.Cancellation.Token).ConfigureAwait(false);

                run.ElapsedSeconds = (int)(DateTime.UtcNow - start).TotalSeconds;
                var evt = ApplyEvent(run, rng);
                run.LastEvent = evt;

                // TRACE: a child span for this tick, parented to the mission root span.
                using (var tick = MissionTelemetry.ActivitySource.StartActivity(
                    "mission.tick", ActivityKind.Internal, root?.Context ?? default))
                {
                    tick?.SetTag("mission.name", run.Name);
                    tick?.SetTag("event", evt);
                    tick?.SetTag("warp_speed", run.WarpSpeed);
                    tick?.SetTag("shield_strength", run.ShieldStrength);
                    tick?.SetTag("photon_torpedoes", run.PhotonTorpedoes);
                    tick?.SetTag("elapsed_seconds", run.ElapsedSeconds);
                }

                // METRICS: record the current gauges + count the event, all tagged by ship.
                var shipTag = new KeyValuePair<string, object?>("mission.name", run.Name);
                _warp.Record(run.WarpSpeed, shipTag);
                _shields.Record(run.ShieldStrength, shipTag);
                _torpedoes.Record(run.PhotonTorpedoes, shipTag);
                _events.Add(1, shipTag, new KeyValuePair<string, object?>("event", evt));

                _logger.LogInformation(
                    "Mission {Ship}: {Event} | warp {Warp}, shields {Shields}%, torpedoes {Torpedoes}",
                    run.Name, evt, run.WarpSpeed, run.ShieldStrength, run.PhotonTorpedoes);

                // END CONDITIONS.
                if (run.ShieldStrength <= 0) { outcome = "Failure"; break; }
                if (run.PhotonTorpedoes <= 0) { outcome = "Retreat"; break; }
                if (run.ElapsedSeconds >= run.DurationSeconds) { outcome = "Success"; break; }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = "Docked"; // cancelled by Reset All
        }
        catch (Exception ex)
        {
            outcome = "Failure";
            _logger.LogError(ex, "Mission {Ship} simulation error.", run.Name);
        }

        run.Status = outcome;
        run.Active = false;

        if (root is not null && outcome == "Failure")
        {
            var breach = new InvalidOperationException($"Shields collapsed aboard {run.Name}.");
            root.SetStatus(ActivityStatusCode.Error, breach.Message);
            root.AddException(breach); // exception event on the mission span
        }

        _completed.Add(
            1,
            new KeyValuePair<string, object?>("mission.name", run.Name),
            new KeyValuePair<string, object?>("outcome", outcome));

        _logger.LogInformation(
            "Mission {Ship} ended: {Outcome} after {Elapsed}s.", run.Name, outcome, run.ElapsedSeconds);

        // Persist a result row only for real outcomes (not a Reset-triggered cancellation).
        if (outcome != "Docked")
        {
            await PersistResultAsync(run, start, outcome);
        }

        _last[run.MissionId] = run.Snapshot();
        _runs.TryRemove(run.MissionId, out _);
    }

    /// <summary>Applies one random in-flight event and mutates ship state accordingly.</summary>
    private static string ApplyEvent(Run run, Random rng)
    {
        // Warp factor always drifts a little.
        run.WarpSpeed = Math.Clamp(Math.Round(run.WarpSpeed + (rng.NextDouble() * 2 - 1), 1), 0, 9.9);

        // Forced-failure launches take heavy, repeated damage so they fail fast.
        if (run.ForceFailure)
        {
            var hit = rng.Next(25, 45);
            run.ShieldStrength = Math.Max(0, run.ShieldStrength - hit);
            return $"Critical hull breach (-{hit}% shields)";
        }

        var roll = rng.Next(100);
        if (roll < 30)
        {
            var dmg = rng.Next(5, 25);
            run.ShieldStrength = Math.Max(0, run.ShieldStrength - dmg);
            return $"Enemy fire (-{dmg}% shields)";
        }
        if (roll < 45)
        {
            run.PhotonTorpedoes = Math.Max(0, run.PhotonTorpedoes - 1);
            return "Fired photon torpedo";
        }
        if (roll < 60)
        {
            var regen = rng.Next(3, 12);
            run.ShieldStrength = Math.Min(100, run.ShieldStrength + regen);
            return $"Shields recharging (+{regen}%)";
        }
        if (roll < 75)
        {
            return $"Warp field adjusted to {run.WarpSpeed:0.0}";
        }
        if (roll < 85)
        {
            var dmg = rng.Next(10, 30);
            run.ShieldStrength = Math.Max(0, run.ShieldStrength - dmg);
            return $"Asteroid impact (-{dmg}% shields)";
        }
        return "All systems nominal";
    }

    private async Task PersistResultAsync(Run run, DateTime start, string outcome)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MissionDbContext>();

            var mission = await db.Missions.FindAsync(run.MissionId);
            if (mission is null)
            {
                return;
            }

            mission.Status = run.Status;
            db.Launches.Add(new Launch
            {
                MissionId = run.MissionId,
                Commander = run.Commander,
                Priority = run.Priority,
                LaunchedAtUtc = start,
                Success = outcome == "Success"
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist mission {Ship} result.", run.Name);
        }
    }

    public void Dispose() => CancelAll();

    /// <summary>Mutable in-memory state for one active flight.</summary>
    private sealed class Run
    {
        public Run(int missionId, string name, string commander, string priority, bool forceFailure)
        {
            MissionId = missionId;
            Name = name;
            Commander = commander;
            Priority = priority;
            ForceFailure = forceFailure;

            // Starting conditions, set synchronously so an immediate snapshot is already valid.
            var rng = new Random();
            WarpSpeed = Math.Round(1 + rng.NextDouble() * 4, 1);
            ShieldStrength = 100;
            PhotonTorpedoes = rng.Next(8, 13);
            DurationSeconds = rng.Next(20, 41);
            LastEvent = "Mission underway";
        }

        public int MissionId { get; }
        public string Name { get; }
        public string Commander { get; }
        public string Priority { get; }
        public bool ForceFailure { get; }
        public CancellationTokenSource Cancellation { get; } = new();

        // Simple fields updated on the flight thread and read by request threads. For a demo, the
        // occasional torn read of a gauge value is harmless.
        public double WarpSpeed;
        public int ShieldStrength;
        public int PhotonTorpedoes;
        public int ElapsedSeconds;
        public int DurationSeconds;
        public string LastEvent = string.Empty;
        public string Status = "In Flight";
        public bool Active = true;

        public MissionSnapshot Snapshot() => new(
            MissionId, Name, Status, Active, WarpSpeed, ShieldStrength, PhotonTorpedoes,
            ElapsedSeconds, DurationSeconds, LastEvent, Commander, Priority);
    }
}
