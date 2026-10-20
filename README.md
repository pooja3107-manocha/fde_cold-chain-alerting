# Nimbus Freight: cold-chain alerting engine

Alerts ops only when a specific truck has a genuinely sustained temperature problem, and says why. Brief blips, repeated alerts for the same problem and bad sensor data don't page anyone; a sensor going quiet while cargo is at risk does.

On the provided data (5 trucks, 08:00 to 08:50), 21 readings are above 8 °C. The engine raises **3 alerts**: T-01 at 08:30, T-05 at 08:30 and T-04 at 08:45. T-02 (one blip) and T-03 (never above 8 °C) stay quiet.

Design, assumptions and trade-offs: [SOLUTION_DESIGN.md](SOLUTION_DESIGN.md). Production options: [PRODUCTION_ARCHITECTURE.md](PRODUCTION_ARCHITECTURE.md).

## Three ways to see the results

| | What it is | Best for |
|---|---|---|
| **Console + `out/alerts.md`** | Colour-coded timeline per truck, the alert list with reasons, the tolerance table | The written answer; first thing to show in a demo |
| **`out/seed/report.html`** | A self-contained page: one chart per truck, incidents, every engine decision, every raw reading | Walking a reviewer through the data offline |
| **Ops dashboard** | A sign-in web app: filter the fleet by truck or status, drill into a truck | What an ops person would use when an alert arrives |

## Prerequisites

| Needed for | Tool |
|---|---|
| Engine, reports, dashboard API | [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version`) |
| Dashboard UI (build once) | [Node.js](https://nodejs.org) 20 or later, with npm |
| `dotnet test` only | Access to nuget.org for the xUnit packages; see [Troubleshooting](#troubleshooting) |
| Or, instead of all of the above | Docker; see [With Docker only](#with-docker-only) |

The engine, runner and dashboard API use no NuGet packages. On the .NET 8 SDK, change `net10.0` to `net8.0` in `Directory.Build.props`; nothing else changes.

## 1. Run the engine

```bash
dotnet run --project src/ColdChain.Runner
```

This runs the scenario checks and the determinism check, then evaluates `data/sensor_readings.json` and prints:

1. **Data issues** found in the input (T-04's missing reading, T-05's out-of-order rows)
2. **A timeline per truck** of every decision: red for alerts, yellow for warnings, grey for ignored blips
3. **What I would alert on, and why**, plus the reason each other truck did not alert
4. **A sensitivity table**: which trucks alert at warm tolerances from 10 to 35 minutes

It also writes:

| File | Contents |
|---|---|
| `out/alerts.md` | The alert list with reasons: the main output (in VS Code: right-click, Open Preview) |
| `out/seed/report.html` | The HTML report for the seed data (see below) |
| `out/scenarios/report.html` | A chart per edge-case scenario |
| `out/seed/incidents.json`, `out/seed/events.jsonl` | Incidents and the full audit trail |

### Other commands

```bash
dotnet run --project src/ColdChain.Runner -- alerts           # only the seed data evaluation
dotnet run --project src/ColdChain.Runner -- sensitivity      # only the tolerance table
dotnet run --project src/ColdChain.Runner -- scenarios        # every scenario with its timeline
dotnet run --project src/ColdChain.Runner -- scenarios 14     # one scenario, by number or name
dotnet run --project src/ColdChain.Runner -- verify           # scenarios only; exit code 1 on failure
dotnet run --project src/ColdChain.Runner -- chaos            # seed data shuffled + resent: decisions must not change
dotnet test                                                   # xUnit: all scenarios + engine properties
```

### With Docker only

The image builds everything, including the dashboard UI, so you need neither the .NET SDK nor Node.js locally:

```bash
docker build -t nimbus-coldchain .
docker run --rm -v "$PWD/out:/app/out" nimbus-coldchain                # everything (same as above); outputs land in ./out
docker run --rm -v "$PWD/out:/app/out" nimbus-coldchain scenarios      # any runner command works the same way
docker run --rm -p 5080:5080 nimbus-coldchain dashboard                # the ops dashboard at http://localhost:5080
```

### Optional AI summaries

Each alert includes a short summary for ops, written from a template by default. To have a model rephrase it, point the engine at any OpenAI-compatible chat completions endpoint:

```bash
export LLM_ENDPOINT=https://api.openai.com/v1/chat/completions   # or an Azure OpenAI deployment URL
export LLM_API_KEY=...
export LLM_MODEL=gpt-4o-mini                                      # optional
```

The model only receives facts the rules computed and never decides whether to alert. If it fails, is unreachable, or returns something that fails the checks, the template is used and the output says so.

## 2. The HTML report

Open `out/seed/report.html` in any browser by double-clicking it; it needs no server. For each truck:

- **A one-line verdict**: the plain-English alert message, or why the truck did not alert
- **A temperature chart**: the safe range shaded, every reading on the line, out-of-range readings in red, each incident shaded, a red triangle where the alert fired, and a dashed "no reading" line where a reading is missing (T-04 at 08:35). Hover a marker for its exact time and value.
- **An incident table**: started, alerted, back in range, time out of range, peak, notes
- **What the engine decided** (click to expand): every decision with its reason
- **Raw readings** (click to expand): every row from the file in time order, with its position in the file, temperature, humidity and a status (in range, too warm, too cold, sensor fault, missing). Use it to walk through T-04's gap and T-05's shuffled rows row by row.

`out/scenarios/report.html` has the same chart for each of the 16 scenarios.

## 3. The ops dashboard

A web app for the person who receives an alert. It replays the seed data through the same engine at startup.

```bash
cd ui-src && npm install && npm run build && cd ..   # once: builds the React UI into the dashboard's wwwroot
dotnet run --project src/ColdChain.Dashboard         # then open http://localhost:5080
```

Or with Docker: `docker run --rm -p 5080:5080 nimbus-coldchain dashboard` (see [With Docker only](#with-docker-only)).

Sign in with a demo user (configured in `src/ColdChain.Dashboard/appsettings.json`; demo only):

| User | Password | Sees |
|---|---|---|
| `ops` | `coldchain` | All trucks |
| `north` | `coldchain` | T-01 to T-03 only (T-04 and T-05 are refused) |

What's on screen:

- **Fleet view**: status tiles (active alert, recovering, resolved, no alert) that filter when clicked; a truck-number search and a status filter; a card per truck with its latest temperature and a one-line explanation; the alert table (click a row to open the truck)
- **Truck view**: headline, cargo profile and tolerance; a temperature chart where hovering shows any reading's time, temperature and humidity; incidents; the engine's decisions; the raw readings
- **Deep links**: `http://localhost:5080/#/trucks/T-04` opens a truck directly, so an alert notification can link straight to it. Fleet filters are kept in the URL too (`#/?truck=T-0&status=Alert`).

API (everything except login requires sign-in):

| Endpoint | Returns |
|---|---|
| `POST /api/login`, `POST /api/logout`, `GET /api/me` | Session, display name and assigned trucks |
| `GET /api/fleet` | Data window, truck summaries and alerts for the user's trucks |
| `GET /api/alerts?truck=&status=` | Filterable alert feed, for other consumers (mobile app, ticketing) |
| `GET /api/trucks/{id}` | Readings, faults, missing slots, file rows, incidents and events for one truck |

**UI development:** keep the dashboard running, run `npm run dev` in `ui-src` and open http://localhost:5173 (hot reload, `/api` forwarded to :5080).

**Built to extend:**

- The UI depends only on the API records in `src/ColdChain.Dashboard/FleetData.cs`, not on engine internals.
- Data comes through `IFleetDataSource`. Replace the seed replay with an implementation over the live incident store and telemetry database.
- Access is a list of `truck` claims per user. Replace the demo users with Entra ID (OIDC) and map groups to trucks or depots; nothing else changes.
- New pages (acknowledging an alert, shift handover notes, per-customer views) are new routes in `ui-src/src/route.ts` and new endpoints beside the existing ones.

## Changing the rules

Every threshold is in `config/cargo_profiles.json`, with the reasoning in comments. Truck-to-cargo mapping is in `data/trucks.csv`: the data has no cargo type, so all five trucks use the strictest profile (pharma, 2 to 8 °C). Change a number and rerun (or restart the dashboard); no code changes needed.

## Scenarios

Each file in `scenarios/` is a small story about one truck plus the exact events the engine must raise, and each is an xUnit test. Readings are `[minute, °C]` or `[minute, °C, arrival delay in minutes]`; a missing minute is a gap. To test a "what if", copy a file, edit it and run `-- scenarios <name>`.

| # | Scenario | Expected behaviour |
|---|---|---|
| 01 | Door opening, 10 min over 8 °C | Blip logged, no alert |
| 02 | Sustained excursion, then recovery | One alert; resolved with duration and peak |
| 03 | Three hours too warm | One alert, then hourly reminders; never re-alerts |
| 04 | Frozen load hovering at -15 °C | Hysteresis: one alert rather than flapping |
| 05 | Relapse 25 min after recovery | Same incident reopened, not a new alert |
| 06 | Sensor silent while cargo is warming | Paged as soon as the silence is detected |
| 07 | Dead zone with cargo in range | Device offline warning, no page |
| 08 | Scenario 02 delivered shuffled | Identical decisions |
| 09 | Gateway resends readings | Duplicates dropped |
| 10 | Out-of-range reading 40 min late | Flagged for review; history not rewritten |
| 11 | Vaccines drop below 2 °C | Cold alert with a shorter tolerance |
| 12 | Probe reports 85 °C, then -127 °C | Sensor faults and device offline; never "cargo frozen" |
| 13 | Reading stamped 3 h in the future | Rejected as a device clock problem |
| 14 | T-04's pattern: repeat excursion with a missing reading | Alert at 20 min, naming the earlier excursion |
| 15 | T-03's pattern: close to 8 °C, never over | Nothing raised |
| 16 | Sensor goes quiet while an incident is recovering | Paged; recovery clock restarts when data returns, so unseen minutes never count as stable |

## Project layout

```
config/cargo_profiles.json      every threshold, with reasoning
data/                           provided seed data + assumed truck-to-cargo mapping
src/ColdChain.Core/
  Engine/TruckMonitor.cs        the per-truck state machine (start here)
  Engine/AlertEngine.cs         routes readings to trucks; trucks never share state
  Simulation/                   data loading, data-quality checks, replay, fault injection
  Scenarios/                    scenario format and checker
  Storage/                      incident store interface; in-memory and JSON implementations
src/ColdChain.Runner/           alert list, sensitivity, console timeline, HTML report, AI summary
src/ColdChain.Dashboard/        ops dashboard API: sign-in, per-user truck access, fleet/alerts/truck endpoints
  FleetData.cs                  API records and the IFleetDataSource seam (seed replay today)
  Users.cs                      demo users and truck-access claims
ui-src/                         React + TypeScript dashboard UI (Vite; builds into the dashboard's wwwroot)
  src/components/               Login, FleetView, TruckView, TempChart
tests/ColdChain.Tests/          xUnit tests
scenarios/                      16 edge-case scenarios
starter_kit/                    the provided starter kit, unchanged
tools/starter_baseline.py       runs the starter utility on the same data
SOLUTION_DESIGN.md              design, assumptions and trade-offs
PRODUCTION_ARCHITECTURE.md      deployment, storage and cost options by fleet size
```

## Troubleshooting

- **`dotnet build` fails with `403 (Forbidden)` downloading xunit or Microsoft.NET.Test.Sdk.** A corporate proxy is blocking NuGet package downloads. Only the test project needs packages, so the engine, runner and dashboard still work; build them directly (`dotnet build src/ColdChain.Runner`, `dotnet build src/ColdChain.Dashboard`). To run the tests, add your company's internal NuGet feed (`dotnet nuget add source <url> -n internal`), or restore once on a network that allows nuget.org; the packages are then cached.
- **The dashboard says "The dashboard UI has not been built yet".** Run `npm install && npm run build` in `ui-src`, then restart the dashboard.
- **Port 5080 is in use.** Change `Urls` in `src/ColdChain.Dashboard/appsettings.json` and the proxy target in `ui-src/vite.config.ts`. With Docker, map a different host port instead: `-p 8080:5080`.
- **`docker build` fails at `npm ci` or `dotnet build` behind a corporate proxy.** The build downloads the Node and .NET base images and the npm packages. Pass your proxy as build args (`--build-arg HTTPS_PROXY=...`), or build on a network that allows Docker Hub, mcr.microsoft.com and registry.npmjs.org.

## Known limitations

- **Thresholds are assumptions**, especially the 20-minute tolerance and treating every truck as pharma. The sensitivity table shows what changes.
- **The seed file has no arrival times**, so it is evaluated in timestamp order. If its row order did reflect arrival order, 7 of T-05's 11 readings would be 5 to 40 minutes late, and 5 of them too late for the 10-minute reorder buffer. Live, those would be logged for review rather than applied, and T-05's alert would be missed. Gateway arrival timestamps would settle this.
- **No restart recovery.** Engine state is in memory.
- **Short excursions are mentioned, not summed.** Two excursions just under the tolerance never alert on their own; a cumulative exposure budget is designed but not built.
- **Gap time counts as out of range** during an excursion, which is deliberately pessimistic.
- **Up to 10 minutes' decision delay** when streaming, the price of reordering late data.
- **Alerts aren't pushed anywhere.** They go to the console, `out/alerts.md`, the audit trail and the dashboard; there is no SMS, email or Teams notification yet.
- **The dashboard is a snapshot, not live.** It replays the seed file once at startup and does not refresh, and alerts can't be acknowledged or assigned yet.
- **Dashboard sign-in is demo-grade**: plain-text users in a config file, served over HTTP. Production needs company sign-in (Entra ID) and HTTPS.
- **One hour of data** is too little to validate humidity-based or predictive rules.
