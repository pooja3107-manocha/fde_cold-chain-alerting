# Solution design: Nimbus Freight cold-chain alerting

## The problem, restated

Nimbus's reefer trucks report cargo temperature every 5 minutes. Today ops are alerted on every reading over the limit, so they ignore alerts, and real failures get lost. Priya wants an alert only when a specific truck has a genuinely sustained problem: not for brief blips, and not repeatedly for the same ongoing problem.

Two more requirements are implied. Data from moving trucks arrives with gaps and out of order, so the system must give the right answer anyway. And every alert must say why, in terms an ops person can act on.

## What the seed data shows

Each of the five trucks tests something different. The provided starter utility alerts on T-01 only.

| Truck | What happens | Right answer | Starter utility | This engine |
|---|---|---|---|---|
| T-01 | Above 8 °C from 08:10 to 08:40, peak 9.6 °C | Alert | Alert | Alert at 08:30 |
| T-02 | One reading at 8.9 °C (08:10) | No alert: a blip | No alert | Blip logged, no alert |
| T-03 | Warms to 7.0 °C, never crosses 8 °C | No alert | No alert | No alert |
| T-04 | Above 8 °C at 08:05–08:10, dips to 7.0–7.5 °C, above again 08:25–08:45; **no reading at 08:35** | Alert: second excursion in 40 minutes, 20 minutes long | **Missed**: counts readings, so 20 min becomes 15 | Alert at 08:45, naming the missing reading and the earlier excursion |
| T-05 | **T-01's exact data, shuffled in the file** | Same as T-01 | **Missed**: assumes sorted input, so the episode is split into fragments | Identical to T-01, alert at 08:30 |

Out of 21 readings above 8 °C, the engine raises 3 alerts. The full list with reasons is generated in `out/alerts.md`.

## Assumptions (all in `config/cargo_profiles.json`)

| Question | Assumption | Reasoning |
|---|---|---|
| Too warm | Above 8 °C (pharma 2–8 °C). Too cold: below 2 °C | The data has no cargo type, so every truck gets the strictest profile. Freezing destroys many vaccines, so cold alerts too. |
| Too long | 20 minutes above, 10 below | 4 consecutive readings. The longest blip in the data is 10 minutes; a door opening is typically 1–2 readings. |
| Excursion over | 0.5 °C back inside the range (≤ 7.5 °C), held 15 min to resolve | Hysteresis prevents a reading at 7.9 °C from "ending" an excursion. |
| Same problem or new | Relapse within 60 min of resolving reopens the same incident; reminders hourly | One alert per problem; a failing unit stays one story. |
| Missing reading | Time between readings counts as out of range if the truck was out of range before the gap | Unknown is treated as unsafe. Durations come from timestamps, never from counting readings. |
| Out of order | The seed file has no arrival times, so it is evaluated in timestamp order. Live, a 10-min buffer reorders late readings; later ones are logged for review, not applied | Deterministic results without rewriting history. |
| Silence | No reading for 15 min while out of range, or while an incident is still recovering, pages immediately; otherwise a warning | A quiet sensor during an excursion is the most dangerous case, and an unconfirmed recovery is not yet safe. |
| Timestamps | No timezone in the data; read as UTC | To confirm with Nimbus. |
| Humidity | Context in the summary only; not used to decide | Rising humidity with temperature suggests warm air or a failing unit, but one hour of data can't validate a rule. |

The tolerance is the main business decision, so the engine reports how the answer changes (`sensitivity` command). At 10–20 minutes, T-01, T-04 and T-05 alert. At 25–30 minutes, T-04 no longer alerts. At 35 minutes, nothing alerts, because T-01's excursion is back under 7.5 °C before then. I chose 20 minutes because it catches T-04's repeat while ignoring every blip. Priya should confirm it per shipper.

## Approach

A deterministic state machine per truck, with no AI in the decision. Alerts must be explainable to a driver, a shipper and an auditor, and the same data must always produce the same answer.

```
readings (any order, gaps) -> reorder buffer -> per-truck state machine -> events -> alert list, audit trail, report
                                                Normal -> Pending -> Alerting -> Recovering -> resolved
```

**Pending** means out of range but within tolerance. It ends quietly as a logged blip, or it becomes **one alert**, which names any missing readings and any earlier short excursions in the last hour. **Alerting** sends hourly reminders, never a second alert. **Recovering** waits for 15 observed minutes back in range before resolving; a data gap restarts that clock, and going out of range again continues the same incident. Separately, each truck is watched for silence, implausible values (such as -127 °C, a common probe fault code) and readings with bad device clocks. Trucks never share state.

## Trade-offs

| Decision | Benefit | Cost |
|---|---|---|
| Gap time counts as out of range | Never under-reports an excursion | May over-report if the sensor was merely offline |
| Earlier blips mentioned, not summed | Alert explains a recurring pattern; rule stays simple | Two short excursions never trigger an alert on their own (see "more time") |
| 10-min reorder buffer when streaming | Shuffled data gives identical decisions (`chaos` check) | Decisions arrive up to 10 min after the reading |
| Late data logged, not applied | History is never silently rewritten | A person reviews late out-of-range readings |
| No dependencies, JSON output | Runs with only the .NET SDK | No restart recovery yet |

## Optional AI summary (bonus)

Each alert gets a plain-English summary for ops. By default it comes from a template. With `LLM_ENDPOINT` and `LLM_API_KEY` set, any OpenAI-compatible model rephrases the same facts. The model only sees facts the rules computed, may only name causes as "possible" from a fixed list, and its answer is rejected if it drops the truck id or runs long. Any failure falls back to the template, so alerting never depends on the model.

## What I'd do with more time, data or budget

1. **Cumulative exposure budget** per shipment: T-04's two excursions total about 35 minutes. Pharma typically measures this as mean kinetic temperature; that needs shipment boundaries, which the data lacks.
2. **Persist engine state** so a restart mid-excursion neither re-alerts nor forgets.
3. **Cargo type per shipment** instead of assuming pharma for all, with per-shipper thresholds.
4. **Rules on the truck** (IoT Edge) to warn drivers without signal; arrival timestamps from the gateway to make lateness measurable.
5. **Door-open and unit-status signals** to separate door openings from failing units, and, with months of history, predict failing units early.
6. **Delivery and acknowledgement**: SMS or Teams pages, escalation if unacknowledged, fleet-level alerts when many trucks go quiet at once.

## Ops dashboard

A small sign-in web app shows the fleet, each truck's chart, its alerts with reasons and its raw file rows (including T-04's missing reading and T-05's out-of-order rows). It reads the engine's results through an API and makes no decisions of its own. Sign-in uses demo users with per-user truck access; production would use company sign-in.

Production deployment, storage and cost options by fleet size are in [PRODUCTION_ARCHITECTURE.md](PRODUCTION_ARCHITECTURE.md).
