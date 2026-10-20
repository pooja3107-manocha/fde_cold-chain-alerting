import { useEffect, useState } from "react";
import { api } from "../api";
import type { TruckDetail } from "../types";
import { minutes, range, temp, time } from "../format";
import StatusPill from "./StatusPill";
import TempChart from "./TempChart";

export default function TruckView({ truckId, onError }: { truckId: string; onError: (e: unknown) => void }) {
  const [detail, setDetail] = useState<TruckDetail | null>(null);
  const [notFound, setNotFound] = useState(false);

  useEffect(() => {
    setDetail(null);
    setNotFound(false);
    api
      .truck(truckId)
      .then(setDetail)
      .catch((e) => (e instanceof Error && /^(403|404)/.test(e.message) ? setNotFound(true) : onError(e)));
  }, [truckId, onError]);

  if (notFound)
    return (
      <>
        <a className="back" href="#/">← All trucks</a>
        <p className="error">Truck {truckId} does not exist or is not assigned to you.</p>
      </>
    );
  if (!detail) return <p className="muted">Loading {truckId}…</p>;

  const { summary: s } = detail;
  const rows = [
    ...detail.fileRows.map((r) => ({ ...r, missing: false })),
    ...detail.missingSlots.map((at) => ({ row: 0, at, temperatureC: null, humidityPct: null, outOfOrder: false, duplicate: false, fault: false, missing: true })),
  ].sort((a, b) => Date.parse(a.at) - Date.parse(b.at) || a.row - b.row);

  return (
    <>
      <a className="back" href="#/">← All trucks</a>

      <section className="truck-head">
        <h1>
          {s.truckId} <StatusPill status={s.status} />
        </h1>
        <p className="headline">{s.headline}</p>
        <dl className="facts">
          <div><dt>Latest</dt><dd>{temp(s.latestC)} at {time(s.latestAt)}</dd></div>
          <div><dt>Cargo profile</dt><dd>{s.profile.name}</dd></div>
          <div><dt>Safe range</dt><dd>{range(s.profile.minC, s.profile.maxC)}</dd></div>
          <div><dt>Tolerance</dt><dd>{s.profile.highToleranceMinutes} min warm, {s.profile.lowToleranceMinutes} min cold</dd></div>
          <div><dt>Readings</dt><dd>{s.readings}</dd></div>
          <div><dt>Alerts sent</dt><dd>{s.alerts} <span className="muted">(a per-reading rule: {s.naiveBreaches})</span></dd></div>
        </dl>
      </section>

      <TempChart detail={detail} />

      <section>
        <h2>Incidents ({detail.incidents.length})</h2>
        {detail.incidents.length === 0 ? (
          <p className="muted">No incidents for this truck.</p>
        ) : (
          <div className="scroll">
            <table>
              <thead>
                <tr><th>Incident</th><th>Problem</th><th>Status</th><th>Started</th><th>Alerted</th><th>Back in range</th><th>Out of range</th><th>Peak</th><th>Notes</th></tr>
              </thead>
              <tbody>
                {detail.incidents.map((i) => (
                  <tr key={i.incidentId}>
                    <td>{i.incidentId}</td>
                    <td className={i.problem === "Too warm" ? "warm" : "cold"}>{i.problem}</td>
                    <td>{i.status}</td>
                    <td className="num">{time(i.startedAt)}</td>
                    <td className="num">{time(i.alertedAt)}</td>
                    <td className="num">{i.recoveredAt ? time(i.recoveredAt) : "not yet"}</td>
                    <td className="num">{minutes(i.outOfRangeMinutes)}</td>
                    <td className="num">{temp(i.peakC)}</td>
                    <td>{i.notes.join(", ")}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <details open={detail.incidents.length > 0}>
        <summary>What the engine decided ({detail.events.length} events)</summary>
        <div className="scroll">
          <table>
            <tbody>
              {detail.events.map((e, k) => (
                <tr key={k} className={`sev-${e.severity.toLowerCase()}`}>
                  <td className="num">{time(e.at)}</td>
                  <td className="ev">{e.label}</td>
                  <td>{e.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>

      <details>
        <summary>
          Raw readings ({detail.fileRows.length} rows in the file{detail.missingSlots.length ? `, ${detail.missingSlots.length} missing` : ""})
        </summary>
        <div className="scroll">
          <table>
            <thead>
              <tr><th>Time</th><th>File row</th><th>Temperature</th><th>Humidity</th><th>Status</th><th>Notes</th></tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const [cls, status] = r.missing ? ["missing", "Missing"] : r.fault ? ["fault", "Sensor fault, ignored"]
                  : r.temperatureC! > s.profile.maxC ? ["warm", "Too warm"]
                  : r.temperatureC! < s.profile.minC ? ["cold", "Too cold"] : ["ok", "In range"];
                const notes = [
                  r.missing && "expected reading never arrived",
                  r.duplicate && "duplicate row, counted once",
                  r.outOfOrder && "out of order in the file",
                ].filter(Boolean).join("; ");
                return (
                  <tr key={`${r.at}-${r.row}`} className={`raw-${cls}`}>
                    <td className="num">{time(r.at)}</td>
                    <td className="num">{r.missing ? "-" : r.row}</td>
                    <td className="num">{r.missing ? "-" : temp(r.temperatureC)}</td>
                    <td className="num">{r.humidityPct == null ? "-" : `${r.humidityPct} %`}</td>
                    <td className="st">{status}</td>
                    <td>{notes}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </details>
    </>
  );
}
