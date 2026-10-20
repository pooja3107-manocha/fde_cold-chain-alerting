// Mirrors the records in src/ColdChain.Dashboard/FleetData.cs.

export type TruckStatus = "Alert" | "Recovering" | "Resolved" | "Ok";
export type IncidentStatus = "Open" | "Recovering" | "Resolved";
export type Severity = "Info" | "Warning" | "Critical";

export interface Me {
  username: string;
  displayName: string;
  allTrucks: boolean;
  trucks: string[];
}

export interface ProfileInfo {
  name: string;
  description: string;
  minC: number;
  maxC: number;
  highToleranceMinutes: number;
  lowToleranceMinutes: number;
}

export interface TruckSummary {
  truckId: string;
  profile: ProfileInfo;
  status: TruckStatus;
  headline: string;
  latestC: number | null;
  latestAt: string | null;
  readings: number;
  alerts: number;
  naiveBreaches: number;
}

export interface AlertRow {
  incidentId: string;
  truckId: string;
  problem: string;
  status: IncidentStatus;
  startedAt: string;
  alertedAt: string;
  recoveredAt: string | null;
  resolvedAt: string | null;
  outOfRangeMinutes: number | null;
  peakC: number;
  why: string;
  notes: string[];
  missedReadings: string[];
}

export interface Fleet {
  source: string;
  dataStart: string;
  dataEnd: string;
  trucks: TruckSummary[];
  alerts: AlertRow[];
}

export interface Point {
  at: string;
  temperatureC: number | null;
  humidityPct: number | null;
}

export interface FileRow {
  row: number;
  at: string;
  temperatureC: number | null;
  humidityPct: number | null;
  outOfOrder: boolean;
  duplicate: boolean;
  fault: boolean;
}

export interface EngineEvent {
  at: string;
  type: string;
  label: string;
  severity: Severity;
  message: string;
  incidentId: string | null;
  temperatureC: number | null;
}

export interface TruckDetail {
  summary: TruckSummary;
  readings: Point[];
  faults: Point[];
  fileRows: FileRow[];
  missingSlots: string[];
  incidents: AlertRow[];
  events: EngineEvent[];
  silenceThresholdMinutes: number;
}
