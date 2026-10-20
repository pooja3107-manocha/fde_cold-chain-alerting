import { useMemo, useRef, useState, type MouseEvent } from "react";
import type { TruckDetail } from "../types";
import { temp, time } from "../format";

const W = 960, H = 260, L = 48, R = 56, T = 28, B = 30;
const PW = W - L - R, PH = H - T - B;
const PAGE_TYPES = new Set(["ExcursionAlert", "ExcursionReopened", "SensorSilentDuringExcursion"]);

/** Same visual language as out/seed/report.html, plus a hover readout for every reading. */
export default function TempChart({ detail }: { detail: TruckDetail }) {
  const { profile } = detail.summary;
  const svgRef = useRef<SVGSVGElement>(null);
  const [hover, setHover] = useState<number | null>(null);

  const m = useMemo(() => {
    const pts = detail.readings
      .filter((r) => r.temperatureC != null)
      .map((r) => ({ t: Date.parse(r.at), c: r.temperatureC as number, h: r.humidityPct }));
    const times = [
      ...pts.map((p) => p.t),
      ...detail.missingSlots.map(Date.parse),
      ...detail.events.filter((e) => e.type !== "ReadingRejected").map((e) => Date.parse(e.at)),
    ];
    if (pts.length === 0 || times.length === 0) return null;
    const t0 = Math.min(...times);
    const t1 = Math.max(Math.max(...times), t0 + 3_600_000 / 4);
    const temps = pts.map((p) => p.c);
    const lo = Math.min(...temps, profile.minC) - 1;
    const hi = Math.max(...temps, profile.maxC) + 1;
    const x = (t: number) => L + ((t - t0) / (t1 - t0)) * PW;
    const y = (c: number) => T + ((hi - c) / (hi - lo)) * PH;
    const silenceMs = detail.silenceThresholdMinutes * 60_000;
    const path = pts.map((p, i) => `${i === 0 || p.t - pts[i - 1].t > silenceMs ? "M" : "L"}${x(p.t).toFixed(1)},${y(p.c).toFixed(1)}`).join(" ");
    const step = hi - lo <= 6 ? 1 : hi - lo <= 15 ? 2 : 5;
    const yTicks: number[] = [];
    for (let v = Math.ceil(lo / step) * step; v <= hi; v += step) yTicks.push(v);
    const tickMs = t1 - t0 <= 2 * 3_600_000 ? 15 * 60_000 : t1 - t0 <= 5 * 3_600_000 ? 30 * 60_000 : 2 * 3_600_000;
    const xTicks: number[] = [];
    for (let t = Math.ceil(t0 / tickMs) * tickMs; t <= t1; t += tickMs) xTicks.push(t);
    return { pts, t1, x, y, path, yTicks, xTicks };
  }, [detail, profile]);

  if (!m) return <p className="muted">No valid readings to chart.</p>;

  const onMove = (e: MouseEvent<SVGSVGElement>) => {
    const box = svgRef.current!.getBoundingClientRect();
    const sx = ((e.clientX - box.left) / box.width) * W;
    let best = 0;
    m.pts.forEach((p, i) => {
      if (Math.abs(m.x(p.t) - sx) < Math.abs(m.x(m.pts[best].t) - sx)) best = i;
    });
    setHover(best);
  };

  const hp = hover != null ? m.pts[hover] : null;
  const top = Math.max(T, Math.min(m.y(profile.maxC), T + PH));
  const bottom = Math.max(T, Math.min(m.y(profile.minC), T + PH));

  return (
    <div className="chart">
      <svg
        ref={svgRef}
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label={`Temperature for ${detail.summary.truckId}, safe range ${profile.minC} to ${profile.maxC} °C`}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
      >
        <rect className="band" x={L} y={top} width={PW} height={bottom - top} />
        {m.yTicks.map((v) => (
          <g key={v}>
            <line className="grid" x1={L} x2={L + PW} y1={m.y(v)} y2={m.y(v)} />
            <text className="axis" x={L - 8} y={m.y(v) + 4} textAnchor="end">
              {v}
            </text>
          </g>
        ))}
        {[
          [profile.maxC, "max"],
          [profile.minC, "min"],
        ].map(([v, n]) => (
          <g key={n}>
            <line className="limit" x1={L} x2={L + PW} y1={m.y(v as number)} y2={m.y(v as number)} />
            <text className="limit-label" x={L + PW + 6} y={m.y(v as number) + 4}>
              {n} {v}°
            </text>
          </g>
        ))}

        {detail.incidents.map((i) => {
          const s = Date.parse(i.startedAt);
          const e = Date.parse(i.recoveredAt ?? i.resolvedAt ?? new Date(m.t1).toISOString());
          return (
            <rect key={i.incidentId} className={i.problem === "Too warm" ? "span-warm" : "span-cold"}
              x={m.x(s)} y={T} width={Math.max(2, m.x(e) - m.x(s))} height={PH}>
              <title>{`${i.incidentId}: ${time(i.startedAt)} to ${time(new Date(e).toISOString())}, peak ${temp(i.peakC)}`}</title>
            </rect>
          );
        })}

        {detail.missingSlots.map((s) => (
          <g key={s}>
            <line className="missed" x1={m.x(Date.parse(s))} x2={m.x(Date.parse(s))} y1={T} y2={T + PH} />
            <text className="missed-label" x={m.x(Date.parse(s))} y={T + PH - 6} textAnchor="middle">
              no reading
            </text>
          </g>
        ))}

        <path className="temp" d={m.path} />
        {m.pts.map((p) => {
          const out = p.c > profile.maxC ? "pt-warm" : p.c < profile.minC ? "pt-cold" : "pt-ok";
          return <circle key={p.t} className={out} cx={m.x(p.t)} cy={m.y(p.c)} r={out === "pt-ok" ? 2 : 3} />;
        })}

        {detail.events
          .filter((e) => PAGE_TYPES.has(e.type))
          .map((e) => {
            const x = m.x(Date.parse(e.at));
            return (
              <g key={`${e.at}-${e.type}`}>
                <line className="page-line" x1={x} x2={x} y1={T} y2={T + PH} />
                <path className="m-page" d={`M${x},${T - 6} L${x - 6},${T - 18} L${x + 6},${T - 18} Z`}>
                  <title>{`${time(e.at)} ${e.label}: ${e.message}`}</title>
                </path>
              </g>
            );
          })}

        {m.xTicks.map((t) => (
          <text key={t} className="axis" x={m.x(t)} y={T + PH + 20} textAnchor="middle">
            {time(new Date(t).toISOString())}
          </text>
        ))}
        <text className="axis" x={L - 8} y={T - 14} textAnchor="end">
          °C
        </text>

        {hp && (
          <g className="hover" pointerEvents="none">
            <line x1={m.x(hp.t)} x2={m.x(hp.t)} y1={T} y2={T + PH} />
            <circle cx={m.x(hp.t)} cy={m.y(hp.c)} r={4.5} />
          </g>
        )}
      </svg>
      <p className="readout" aria-live="polite">
        {hp
          ? `${time(new Date(hp.t).toISOString())} UTC: ${temp(hp.c)}${hp.h != null ? `, humidity ${hp.h}%` : ""}${
              hp.c > profile.maxC ? ", too warm" : hp.c < profile.minC ? ", too cold" : ", in range"
            }`
          : "Hover over the chart to read any reading."}
      </p>
    </div>
  );
}
