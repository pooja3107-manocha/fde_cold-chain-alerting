import type { Fleet, TruckStatus } from "../types";
import { href, navigate } from "../route";
import { minutes, range, temp, time } from "../format";
import StatusPill from "./StatusPill";

const STATUS_LABEL: Record<TruckStatus, string> = {
  Alert: "Active alert",
  Recovering: "Recovering",
  Resolved: "Alert resolved",
  Ok: "No alert",
};
const STATUS_ORDER: TruckStatus[] = ["Alert", "Recovering", "Resolved", "Ok"];

export default function FleetView({ fleet, filter }: { fleet: Fleet; filter: { truck: string; status: string } }) {
  const setFilter = (next: Partial<{ truck: string; status: string }>) =>
    navigate({ page: "fleet", ...filter, ...next }, true);

  const q = filter.truck.trim().toLowerCase();
  const trucks = fleet.trucks
    .filter((t) => !q || t.truckId.toLowerCase().includes(q))
    .filter((t) => !filter.status || t.status === filter.status)
    .sort((a, b) => STATUS_ORDER.indexOf(a.status) - STATUS_ORDER.indexOf(b.status) || a.truckId.localeCompare(b.truckId));
  const ids = new Set(trucks.map((t) => t.truckId));
  const alerts = fleet.alerts.filter((a) => ids.has(a.truckId));

  const counts = STATUS_ORDER.map((s) => ({ s, n: fleet.trucks.filter((t) => t.status === s).length }));

  return (
    <>
      <section className="kpis" aria-label="Fleet status">
        {counts.map(({ s, n }) => (
          <button
            key={s}
            className={`kpi kpi-${s.toLowerCase()} ${filter.status === s ? "selected" : ""}`}
            onClick={() => setFilter({ status: filter.status === s ? "" : s })}
            aria-pressed={filter.status === s}
          >
            <span className="kpi-n">{n}</span>
            <span className="kpi-l">{STATUS_LABEL[s]}</span>
          </button>
        ))}
      </section>

      <section className="filters" aria-label="Filters">
        <label>
          Truck
          <input
            list="truck-ids"
            placeholder="e.g. T-04"
            value={filter.truck}
            onChange={(e) => setFilter({ truck: e.target.value })}
          />
          <datalist id="truck-ids">
            {fleet.trucks.map((t) => (
              <option key={t.truckId} value={t.truckId} />
            ))}
          </datalist>
        </label>
        <label>
          Status
          <select value={filter.status} onChange={(e) => setFilter({ status: e.target.value })}>
            <option value="">All</option>
            {STATUS_ORDER.map((s) => (
              <option key={s} value={s}>
                {STATUS_LABEL[s]}
              </option>
            ))}
          </select>
        </label>
        {(filter.truck || filter.status) && (
          <button className="ghost" onClick={() => setFilter({ truck: "", status: "" })}>
            Clear filters
          </button>
        )}
        <span className="spacer" />
        <span className="muted">
          {trucks.length} of {fleet.trucks.length} trucks
        </span>
      </section>

      <section className="cards" aria-label="Trucks">
        {trucks.length === 0 && <p className="muted">No trucks match these filters.</p>}
        {trucks.map((t) => (
          <a key={t.truckId} className={`card st-${t.status.toLowerCase()}`} href={href({ page: "truck", truckId: t.truckId })}>
            <div className="card-head">
              <strong>{t.truckId}</strong>
              <StatusPill status={t.status} />
            </div>
            <div className="card-temp">
              <span className="big">{temp(t.latestC)}</span>
              <span className="muted"> at {time(t.latestAt)}</span>
            </div>
            <div className="muted small">
              {t.profile.name}, safe {range(t.profile.minC, t.profile.maxC)}
            </div>
            <p className="card-headline">{t.headline}</p>
          </a>
        ))}
      </section>

      <section>
        <h2>Alerts ({alerts.length})</h2>
        {alerts.length === 0 ? (
          <p className="muted">No alerts for the trucks shown.</p>
        ) : (
          <div className="scroll">
            <table>
              <thead>
                <tr>
                  <th>Truck</th>
                  <th>Problem</th>
                  <th>Status</th>
                  <th>Started</th>
                  <th>Alerted</th>
                  <th>Out of range</th>
                  <th>Peak</th>
                  <th>Why</th>
                </tr>
              </thead>
              <tbody>
                {alerts.map((a) => (
                  <tr key={a.incidentId} className="clickable" onClick={() => navigate({ page: "truck", truckId: a.truckId })}>
                    <td>
                      <a href={href({ page: "truck", truckId: a.truckId })}>{a.truckId}</a>
                    </td>
                    <td className={a.problem === "Too warm" ? "warm" : "cold"}>{a.problem}</td>
                    <td>{a.status}</td>
                    <td className="num">{time(a.startedAt)}</td>
                    <td className="num">{time(a.alertedAt)}</td>
                    <td className="num">{minutes(a.outOfRangeMinutes)}</td>
                    <td className="num">{temp(a.peakC)}</td>
                    <td className="why">{a.why}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  );
}
