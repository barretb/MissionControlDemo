# Mission Control — OpenTelemetry with .NET Aspire

A beginner-friendly sample for a conference talk on OpenTelemetry in .NET.
You launch Star Trek starship missions from a web dashboard; every launch produces a
distributed **trace**, a custom **metric**, structured **logs**, and propagated **baggage** —
all visible in the **.NET Aspire dashboard**.

Stack: **.NET 10** + **Aspire 13.5** + **EF Core (in-memory SQLite)** + **OpenTelemetry**.

---

## What you'll see

A launch flows **Web → API → database** as a single trace and demonstrates:

- **Traces / spans** — automatic ASP.NET Core + HttpClient + EF Core spans, plus a manual
  `LaunchMission` span and a reusable `db.launch.insert` span.
- **Metrics** — a custom counter `missions_launched`, tagged by ship name (outcome is counted
  separately by `mission.completed`, once it is actually known).
- **Structured logs** — `ILogger` messages that automatically carry `TraceId` / `SpanId`.
- **Baggage** — `mission.commander` and `mission.priority` set in the Web tier and propagated
  to the API over the W3C `baggage` header, with zero manual header plumbing. The commander is
  read back out of ambient baggage on the background flight thread and tagged onto every
  `mission.tick` span — it is never passed as a method parameter.
- **Errors** — the "Force failure" checkbox biases a flight toward a fast shield collapse, which
  produces an error span (with a recorded exception event) plus a matching Error-level log,
  both carrying the same `TraceId`.
- **GenAI telemetry** — "Request Debrief" asks a model to narrate the finished flight, producing a
  `mission.debrief` span with a `chat {model}` child carrying the OpenTelemetry `gen_ai.*`
  semantic-convention attributes, token-usage metrics, and a correlated log. **No API key is
  needed** — a simulated `IChatClient` ships in the box; see [AI debriefs](#ai-debriefs) below.

---

## Prerequisites

- **.NET 10 SDK**
- **.NET Aspire 13.5** — the Aspire CLI and/or workload (`aspire --version` should report 13.5 or later)

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
   - **Shields reach 0 → Failure** (the mission span is marked as an error, with a matching
     Error-level log).
   - **Torpedoes reach 0 → Retreat.**
   - **Otherwise, after a random 20–40s → Success.**
5. Toggle **Force failure** to bias a launch toward a fast shield collapse.
6. Use **Generate Load** to launch every docked ship at once (fills Traces & Metrics for ~20–40s),
   **Reset All** to abort flights and re-dock, and **Inspect Baggage** to see the key-value context
   the API received via header propagation.

### AI debriefs

Select a ship that has already flown and click **Request Debrief**. The API builds a prompt from
that flight's telemetry, sends it through an `IChatClient`, and returns a three-sentence debrief
along with its token counts.

**It works with no API key.** The default inner client is `SimulatedChatClient`, which writes the
debrief locally, takes a realistic 400–1200 ms, and reports `UsageDetails` so the token metrics are
real numbers rather than zeros. The point of the module is that the OpenTelemetry wrapper
instruments the **`IChatClient` interface**, not any particular provider — so the spans and metrics
are identical whether the model is simulated, OpenAI, or Azure OpenAI.

To use a real model instead, nothing changes but configuration:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project MissionControl.Api
```

Optionally set `OpenAI:Model` (default `gpt-4o-mini`). `OpenAI:CaptureContent` (default `false`)
turns on prompt/completion capture — leave it off unless you know the content is safe to store.

What to point at in the dashboard:

| Signal | What appears |
|--------|--------------|
| **Trace** | `mission.debrief` (why the model was called, which ship) with a `chat {model}` child carrying `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.response.finish_reasons`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`. |
| **Metrics** | `gen_ai.client.token.usage` and `gen_ai.client.operation.duration` from the wrapper, plus `mission.debrief.tokens` (by ship and `gen_ai.token.type`) and `mission.debrief.requests` (by outcome). |
| **Logs** | "Mission debrief generated for {Ship}…" with model and token counts — and deliberately **without** the prompt or the completion. |

### Presenting this live

A run order that avoids the two things that make the demo look flat:

1. Launch one ship (**Enterprise**), then immediately hit **Generate Load**. A single launch puts
   one point on the `missions_launched` chart, which reads as a broken dashboard; five in-flight
   ships give the Metrics tab real shape for the next 20–40 seconds.
2. Go to **Structured Logs** and find `Mission launch requested: Enterprise…`. Point at the
   `TraceId` on the record — that is the slide-7 payoff.
3. Click through to the trace. **Give it ~30 seconds first.** Spans export when they *end*:
   `mission.tick` children appear every 2s, but the `Mission: {ship}` parent bar does not render
   until the flight finishes. Click too early and the waterfall looks half-built.
4. In the waterfall, the flight is nested under the launch request — `POST` → `HTTP POST` →
   `POST` → `LaunchMission` → `Mission: {ship}` → `mission.tick` ×N. Same `TraceId` throughout.
   Say "nested under the launch," not "at the top."
5. For baggage, open a `mission.tick` span and show the `mission.commander` tag, then hit
   **Inspect Baggage** to show the raw key-value pairs the API received over the header. The tag
   is read from ambient baggage on a background thread — never passed as a parameter.
6. Metrics tab: `missions_launched` grouped by `mission.name`, then the live gauges
   (`mission.shield_strength` is the most legible) and `mission.completed` by `outcome`.
7. Optional error beat: launch with **Force failure** checked and come back to a red error span
   plus its matching Error-level log, both on the same `TraceId`.
8. GenAI beat: select a ship that has finished flying and click **Request Debrief**. In the trace,
   `mission.debrief` wraps a `chat {model}` child whose attributes are the `gen_ai.*` semantic
   conventions; in Metrics, `gen_ai.client.token.usage` and `mission.debrief.tokens` now have data.
   Note out loud that the log line carries the token counts but **not** the prompt.

---

## Where to look in the Aspire dashboard

| Dashboard tab   | What to point at                                                                 |
|-----------------|----------------------------------------------------------------------------------|
| **Traces**      | The launch trace (`missioncontrol-web` → `missioncontrol-api` → SQLite) containing the manual `LaunchMission` span, and **nested under it** a long-lived `Mission: {ship}` span with a `mission.tick` child every 2s carrying warp/shields/torpedoes tags. The flight is part of the launch's trace, not a separate root — same `TraceId` for all 20–40s, which is what makes "click the launch log line, land in the waterfall" work. A mission that ends in Failure shows a red error span with an exception event. **Note:** spans export when they *end*, so ticks appear every 2s but the `Mission: {ship}` bar only fills in once the flight finishes. |
| **Metrics**     | `missions_launched` (counter), the live gauges `mission.warp_speed`, `mission.shield_strength`, `mission.photon_torpedoes`, and the counters `mission.events` (by `event`) and `mission.completed` (by `outcome`). Group any of them by the `mission.name` tag. |
| **Structured logs** | The per-tick "Mission {Ship}: {Event}…" lines and the "Mission {Ship} ended: {Outcome}…" entries, each linked to its trace via TraceId/SpanId. |
| **Resources**   | Both projects, with health status and endpoints.                                 |

---

## Where each OpenTelemetry concept lives in the code (slide map)

| Talk slide / concept        | File | What to show |
|-----------------------------|------|--------------|
| **The OTel hub** (register everything once) | `MissionControl.ServiceDefaults/Extensions.cs` → `ConfigureOpenTelemetry` | `WithTracing(... AddSource("MissionControl.Telemetry"))`, `WithMetrics(... AddMeter("MissionControl.Telemetry"))`, log pipeline, and OTLP export gated on `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| **Shared ActivitySource + Meter** | `MissionControl.ServiceDefaults/MissionTelemetry.cs` | Well-known name `MissionControl.Telemetry` used by both signals. |
| **Reusable span factory** (advanced) | Defined: `MissionControl.ServiceDefaults/MissionTelemetry.cs` → `StartDatabaseSpan<T>`<br>Used: `MissionControl.Api/Simulation/MissionSimulator.cs` → `PersistResultAsync` | Starts an internal span and auto-tags entity properties as `db.entity.*` via safe reflection. Call site is one line; look for the `db.launch.insert` span at the end of each flight, tagged `db.entity.commander`, `db.entity.success`, etc. |
| **Custom metric counter** (advanced) | `MissionControl.ServiceDefaults/MissionTelemetry.cs` → `MissionMetrics` + `AddMissionMetrics()` | `Counter<long>` named `missions_launched` created via `IMeterFactory`, wrapped in a DI singleton. |
| **Manual span + tags** | `MissionControl.Api/Program.cs` → launch endpoint | `ActivitySource.StartActivity("LaunchMission", ...)` and `SetTag(...)`. |
| **Continuous telemetry** (2s ticks) | `MissionControl.Api/Simulation/MissionSimulator.cs` | Long-lived `Mission: {ship}` span (nested in the launch trace) + per-tick child spans, live gauges (`mission.warp_speed`/`shield_strength`/`photon_torpedoes`), event/outcome counters, and per-tick structured logs at Information — plus a Warning when shields drop below 25%, so severity filtering in the dashboard has something to find. End conditions set Success / Failure / Retreat. |
| **GenAI: instrument the interface** | `MissionControl.Api/Program.cs` → `IChatClient` registration | `new ChatClientBuilder(inner).UseOpenTelemetry(sourceName: MissionTelemetry.ChatSourceName, ...)`. Wraps the interface, so simulated and real providers emit identical telemetry. |
| **GenAI: the privacy switch** | same registration → `EnableSensitiveData` | Off by default. Prompts and completions are the likeliest place for PII to reach your observability vendor. |
| **GenAI: business span + cost metric** | `MissionControl.Api/Ai/MissionDebriefService.cs` | `mission.debrief` parent span (why the call happened) and the `mission.debrief.tokens` counter (what it cost), alongside the wrapper's automatic `gen_ai.*` signals. |
| **GenAI: no key required** | `MissionControl.Api/Ai/SimulatedChatClient.cs` | Reports `UsageDetails` and `ChatClientMetadata` so token counts, provider name, and model land on the span exactly as a real client's would. |
| **Baggage — set** | `MissionControl.Web/Program.cs` → launch proxy | `Baggage.SetBaggage("mission.commander", ...)` before the HttpClient call. |
| **Baggage — read + propagation** | `MissionControl.Api/Program.cs` + `GET /api/baggage` | `Baggage.GetBaggage("mission.commander")` — arrives via the `baggage` header automatically. The **Inspect Baggage** button in the UI shows exactly what the API received; that is the visible proof, since OTel does not copy baggage onto span attributes. |
| **Baggage — survives onto a background thread** | `MissionControl.Api/Simulation/MissionSimulator.cs` → `RunAsync` | `Baggage.GetBaggage("mission.commander")` read on the flight thread, then tagged onto every `mission.tick`. `Task.Run` captures the ExecutionContext, so baggage set in the Web tier is still ambient here long after the HTTP request ended. |
| **Structured logging w/ trace correlation** | `MissionControl.Api/Program.cs` → launch endpoint | `logger.LogInformation("Mission launch requested: {MissionName}, commander {Commander}, priority {Priority}, status {Status}", ...)` — this is the record shown on the "Anatomy of an OTel Log Record" slide. |
| **Error spans + error logs** | `MissionControl.Api/Simulation/MissionSimulator.cs` → end-of-flight `Failure` branch | `root.SetStatus(ActivityStatusCode.Error, ...)`, `root.AddException(breach)`, and the matching `_logger.LogError(breach, ...)`. Reached fastest via the **Force failure** checkbox. |
| **Orchestration + wiring** | `MissionControl.AppHost/Program.cs` | `AddProject<Projects.MissionControl_Api>()`, references + service discovery. |

---

## Project layout

```
MissionControlDemo/
├─ MissionControlDemo.sln
├─ Directory.Packages.props        # central package versions (.NET 10 / Aspire 13.5)
├─ MissionControl.AppHost/         # Aspire orchestrator: API + Web
├─ MissionControl.ServiceDefaults/ # OTel config + custom telemetry (the talk's core)
├─ MissionControl.Api/             # Minimal API + EF Core (in-memory SQLite) + seeded roster
│  └─ Ai/                        # IChatClient debrief service + simulated client (GenAI telemetry)
└─ MissionControl.Web/             # Static dashboard + baggage-setting proxy
```

## API endpoints

- `GET  /api/missions` — the roster
- `GET  /api/missions/{id}/telemetry` — **live flight snapshot** (warp / shields / torpedoes / status), polled every 2s by the Live Telemetry panel
- `GET  /api/missions/{id}/launches` — launch history + aggregate stats for one mission
- `POST /api/missions/{id}/launch` — body `{ "commander", "priority", "forceFailure" }`; starts a background flight. Returns `409` if the ship is already on a mission.
- `POST /api/missions/reset` — abort any flights and return every ship to Docked (Reset All)
- `POST /api/missions/{id}/debrief` — AI mission debrief for the last flight; returns the narrative plus token counts
- `GET  /api/baggage` — echoes current baggage (propagation demo / Baggage Inspector)

---

## Notes

- The database schema is created with `EnsureCreated()` and seeded on startup — no migrations needed
  to run the demo.
- OTLP export turns on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is present; Aspire injects it, so signals
  reach the dashboard automatically with no extra config.
