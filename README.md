# Mission Control — OpenTelemetry with .NET Aspire

A beginner-friendly sample for the **BeerCity Code** talk on OpenTelemetry in .NET.
You launch Star Trek starship missions from a web dashboard; every launch produces a
distributed **trace**, a custom **metric**, structured **logs**, and propagated **baggage** —
all visible in the **.NET Aspire dashboard**.

Stack: **.NET 10** + **Aspire 13** + **PostgreSQL** + **OpenTelemetry**.

---

## What you'll see

A launch flows **Web → API → PostgreSQL** as a single trace and demonstrates:

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
- **Docker** (or Podman) running — Aspire starts PostgreSQL and pgAdmin as containers

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
2. Pick a ship, set a commander + priority, and click **Launch**.
3. Toggle **Force failure** to generate an error trace/log.

---

## Where to look in the Aspire dashboard

| Dashboard tab   | What to point at                                                                 |
|-----------------|----------------------------------------------------------------------------------|
| **Traces**      | A launch trace spanning `missioncontrol-web` → `missioncontrol-api` → Postgres. Expand to see `LaunchMission`, `db.launch.insert` (with `db.entity.*` tags), and the baggage/tags. Failed launches show a red error span with an exception event. |
| **Metrics**     | `missions_launched` under the `missioncontrol-api` resource. Filter/group by the `mission.name` and `mission.success` tags. |
| **Structured logs** | The "Mission launch requested…" / "Mission launch FAILED…" entries, each linked to its trace via TraceId/SpanId. |
| **Resources**   | The Postgres + pgAdmin containers and both projects, with health status.         |

---

## Where each OpenTelemetry concept lives in the code (slide map)

| Talk slide / concept        | File | What to show |
|-----------------------------|------|--------------|
| **The OTel hub** (register everything once) | `MissionControl.ServiceDefaults/Extensions.cs` → `ConfigureOpenTelemetry` | `WithTracing(... AddSource("MissionControl.Telemetry"))`, `WithMetrics(... AddMeter("MissionControl.Telemetry"))`, log pipeline, and OTLP export gated on `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| **Shared ActivitySource + Meter** | `MissionControl.ServiceDefaults/MissionTelemetry.cs` | Well-known name `MissionControl.Telemetry` used by both signals. |
| **Reusable span factory** (advanced) | `MissionControl.ServiceDefaults/MissionTelemetry.cs` → `StartDatabaseSpan<T>` | Starts an internal span and auto-tags entity properties as `db.entity.*` via safe reflection. |
| **Custom metric counter** (advanced) | `MissionControl.ServiceDefaults/MissionTelemetry.cs` → `MissionMetrics` + `AddMissionMetrics()` | `Counter<long>` named `missions_launched` created via `IMeterFactory`, wrapped in a DI singleton. |
| **Manual span + tags** | `MissionControl.Api/Program.cs` → launch endpoint | `ActivitySource.StartActivity("LaunchMission", ...)` and `SetTag(...)`. |
| **Baggage — set** | `MissionControl.Web/Program.cs` → launch proxy | `Baggage.SetBaggage("mission.commander", ...)` before the HttpClient call. |
| **Baggage — read + propagation** | `MissionControl.Api/Program.cs` + `GET /api/baggage` | `Baggage.GetBaggage("mission.commander")` — arrives via the `baggage` header automatically. |
| **Structured logging w/ trace correlation** | `MissionControl.Api/Program.cs` | `logger.LogInformation("Mission launch requested: {MissionName}, commander {Commander}, status {Status}", ...)`. |
| **Error spans + error logs** | `MissionControl.Api/Program.cs` (force-failure path) | `activity.SetStatus(ActivityStatusCode.Error, ...)`, `activity.AddException(ex)`, `logger.LogError(...)`. |
| **Orchestration + wiring** | `MissionControl.AppHost/Program.cs` | `AddPostgres().AddDatabase()`, `AddProject<Projects.MissionControl_Api>()`, references + service discovery. |

---

## Project layout

```
MissionControlDemo/
├─ MissionControlDemo.sln
├─ Directory.Packages.props        # central package versions (.NET 10 / Aspire 13)
├─ MissionControl.AppHost/         # Aspire orchestrator: Postgres + API + Web
├─ MissionControl.ServiceDefaults/ # OTel config + custom telemetry (the talk's core)
├─ MissionControl.Api/             # Minimal API + EF Core/Npgsql + seeded roster
└─ MissionControl.Web/             # Static dashboard + baggage-setting proxy
```

## API endpoints

- `GET  /api/missions` — the roster
- `POST /api/missions/{id}/launch` — body `{ "commander", "priority", "forceFailure" }`
- `GET  /api/baggage` — echoes current baggage (propagation demo)

---

## Notes

- The database schema is created with `EnsureCreated()` and seeded on startup — no migrations needed
  to run the demo.
- OTLP export turns on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is present; Aspire injects it, so signals
  reach the dashboard automatically with no extra config.
