# Mission Control — OpenTelemetry with .NET Aspire

A beginner-friendly sample for a conference talk on OpenTelemetry in .NET.
You launch Star Trek starship missions from a web dashboard; every launch produces a
distributed **trace**, a custom **metric**, structured **logs**, and propagated **baggage** —
all visible in the **.NET Aspire dashboard**.

Stack: **.NET 10** + **Aspire 13** + **EF Core (in-memory SQLite)** + **OpenTelemetry**.

---

## What you'll see

A launch flows **Web → API → database** as a single trace and demonstrates:

- **Traces / spans** — automatic ASP.NET Core + HttpClient + EF Core spans, plus a manual
  `LaunchMission` span and a reusable `db.launch.insert` span.
- **Metrics** — a custom counter `missions_launched`, tagged by ship name and success.
- **Structured logs** — `ILogger` messages that automatically carry `TraceId` / `SpanId`.
- **Baggage** — `mission.commander` and `mission.priority` set in the Web tier and propagated
  to the API over the W3C `baggage` header, with zero manual header plumbing.
- **Errors** — the "Force failure" checkbox produces an error span (with a recorded exception)
  and an error-level log.

---

## Prerequisites

- **.NET 10 SDK**
- **.NET Aspire 13** — the Aspire CLI and/or workload (`aspire --version` should report 13.x)

No Docker needed: the API uses an in-memory SQLite database, so nothing persists between runs —
every start begins with a clean roster.

---

## Run it

From the solution root:

```bash
# Preferred (Aspire CLI):
aspire run

# Or with the SDK directly:
dotnet run --project MissionControl.AppHost
```

The Aspire dashboard opens automatically. From the dashboard:

1. Open the **missioncontrol-web** endpoint — the Mission Control dashboard.
2. Pick a ship, set a commander + priority, and click **Launch**. The ship enters **In Flight**
   and cannot launch again until the mission ends.
3. Watch the **Live Telemetry** panel: every 2 seconds a random event changes the ship's
   **warp speed**, **shield strength**, and **photon torpedoes** — each tick recorded as a trace
   span and metric sample.
4. Missions end on their own:
   - **Shields reach 0 → Failure** (the mission's root span is marked as an error).
   - **Torpedoes reach 0 → Retreat.**
   - **Otherwise, after a random 20–40s → Success.**
5. Toggle **Force failure** to bias a launch toward a fast shield collapse.
6. Use **Generate Load** to launch every docked ship at once (fills Traces & Metrics for ~20–40s),
   **Reset All** to abort flights and re-dock, and **Inspect Baggage** to see the key-value context
   the API received via header propagation.

---

## Where to look in the Aspire dashboard

| Dashboard tab   | What to point at                                                                 |
|-----------------|----------------------------------------------------------------------------------|
| **Traces**      | The `LaunchMission` request trace (`missioncontrol-web` → `missioncontrol-api` → SQLite) **plus** a long-lived `Mission: {ship}` root span with a `mission.tick` child span every 2s carrying warp/shields/torpedoes tags. A mission that ends in Failure shows a red error span with an exception event. |
| **Metrics**     | `missions_launched` (counter), the live gauges `mission.warp_speed`, `mission.shield_strength`, `mission.photon_torpedoes`, and the counters `mission.events` (by `event`) and `mission.completed` (by `outcome`). Group any of them by the `mission.name` tag. |
| **Structured logs** | The per-tick "Mission {Ship}: {Event}…" lines and the "Mission {Ship} ended: {Outcome}…" entries, each linked to its trace via TraceId/SpanId. |
| **Resources**   | Both projects, with health status and endpoints.                                 |

---

## Where each OpenTelemetry concept lives in the code (slide map)

| Talk slide / concept        | File | What to show |
|-----------------------------|------|--------------|
| **The OTel hub** (register everything once) | `MissionControl.ServiceDefaults/Extensions.cs` → `ConfigureOpenTelemetry` | `WithTracing(... AddSource("MissionControl.Telemetry"))`, `WithMetrics(... AddMeter("MissionControl.Telemetry"))`, log pipeline, and OTLP export gated on `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| **Shared ActivitySource + Meter** | `MissionControl.ServiceDefaults/MissionTelemetry.cs` | Well-known name `MissionControl.Telemetry` used by both signals. |
| **Reusable span factory** (advanced) | `MissionControl.ServiceDefaults/MissionTelemetry.cs` → `StartDatabaseSpan<T>` | Starts an internal span and auto-tags entity properties as `db.entity.*` via safe reflection. |
| **Custom metric counter** (advanced) | `MissionControl.ServiceDefaults/MissionTelemetry.cs` → `MissionMetrics` + `AddMissionMetrics()` | `Counter<long>` named `missions_launched` created via `IMeterFactory`, wrapped in a DI singleton. |
| **Manual span + tags** | `MissionControl.Api/Program.cs` → launch endpoint | `ActivitySource.StartActivity("LaunchMission", ...)` and `SetTag(...)`. |
| **Continuous telemetry** (2s ticks) | `MissionControl.Api/Simulation/MissionSimulator.cs` | Long-lived root span + per-tick child spans, live gauges (`mission.warp_speed`/`shield_strength`/`photon_torpedoes`), event/outcome counters, and per-tick structured logs. End conditions set Success / Failure / Retreat. |
| **Baggage — set** | `MissionControl.Web/Program.cs` → launch proxy | `Baggage.SetBaggage("mission.commander", ...)` before the HttpClient call. |
| **Baggage — read + propagation** | `MissionControl.Api/Program.cs` + `GET /api/baggage` | `Baggage.GetBaggage("mission.commander")` — arrives via the `baggage` header automatically. |
| **Structured logging w/ trace correlation** | `MissionControl.Api/Program.cs` | `logger.LogInformation("Mission launch requested: {MissionName}, commander {Commander}, status {Status}", ...)`. |
| **Error spans + error logs** | `MissionControl.Api/Program.cs` (force-failure path) | `activity.SetStatus(ActivityStatusCode.Error, ...)`, `activity.AddException(ex)`, `logger.LogError(...)`. |
| **Orchestration + wiring** | `MissionControl.AppHost/Program.cs` | `AddProject<Projects.MissionControl_Api>()`, references + service discovery. |

---

## Project layout

```
MissionControlDemo/
├─ MissionControlDemo.sln
├─ Directory.Packages.props        # central package versions (.NET 10 / Aspire 13)
├─ MissionControl.AppHost/         # Aspire orchestrator: API + Web
├─ MissionControl.ServiceDefaults/ # OTel config + custom telemetry (the talk's core)
├─ MissionControl.Api/             # Minimal API + EF Core (in-memory SQLite) + seeded roster
└─ MissionControl.Web/             # Static dashboard + baggage-setting proxy
```

## API endpoints

- `GET  /api/missions` — the roster
- `GET  /api/missions/{id}/telemetry` — **live flight snapshot** (warp / shields / torpedoes / status), polled every 2s by the Live Telemetry panel
- `GET  /api/missions/{id}/launches` — launch history + aggregate stats for one mission
- `POST /api/missions/{id}/launch` — body `{ "commander", "priority", "forceFailure" }`; starts a background flight. Returns `409` if the ship is already on a mission.
- `POST /api/missions/reset` — abort any flights and return every ship to Docked (Reset All)
- `GET  /api/baggage` — echoes current baggage (propagation demo / Baggage Inspector)

---

## Notes

- The database schema is created with `EnsureCreated()` and seeded on startup — no migrations needed
  to run the demo.
- OTLP export turns on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is present; Aspire injects it, so signals
  reach the dashboard automatically with no extra config.
