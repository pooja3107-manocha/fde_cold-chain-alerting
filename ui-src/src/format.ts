// All times in the data are UTC; show them as UTC so they match the alert messages and reports.

export const time = (iso: string | null | undefined) => (iso ? new Date(iso).toISOString().slice(11, 16) : "-");

export const date = (iso: string) => new Date(iso).toISOString().slice(0, 10);

export const temp = (c: number | null | undefined) => (c == null ? "-" : `${c.toFixed(1)} °C`);

export const range = (minC: number, maxC: number) => `${minC} to ${maxC} °C`;

export const minutes = (m: number | null) =>
  m == null ? "ongoing" : m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, "0")} min`;
