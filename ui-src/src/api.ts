import type { AlertRow, Fleet, Me, TruckDetail } from "./types";

export class Unauthorized extends Error {}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    credentials: "same-origin",
    headers: { "Content-Type": "application/json" },
    ...init,
  });
  if (res.status === 401) throw new Unauthorized();
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.status === 204 ? (undefined as T) : res.json();
}

export const api = {
  me: () => call<Me>("/api/me"),
  login: (username: string, password: string) =>
    call<Me>("/api/login", { method: "POST", body: JSON.stringify({ username, password }) }),
  logout: () => call<void>("/api/logout", { method: "POST" }),
  fleet: () => call<Fleet>("/api/fleet"),
  alerts: (truck?: string) => call<AlertRow[]>(`/api/alerts${truck ? `?truck=${encodeURIComponent(truck)}` : ""}`),
  truck: (id: string) => call<TruckDetail>(`/api/trucks/${encodeURIComponent(id)}`),
};
