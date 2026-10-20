import type { TruckStatus } from "../types";

const LABEL: Record<TruckStatus, string> = {
  Alert: "Alert",
  Recovering: "Recovering",
  Resolved: "Resolved",
  Ok: "OK",
};

export default function StatusPill({ status }: { status: TruckStatus }) {
  return <span className={`pill pill-${status.toLowerCase()}`}>{LABEL[status]}</span>;
}
