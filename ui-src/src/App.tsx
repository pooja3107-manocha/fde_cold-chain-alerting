import { useCallback, useEffect, useState } from "react";
import { api, Unauthorized } from "./api";
import type { Fleet, Me } from "./types";
import { useRoute } from "./route";
import { date, time } from "./format";
import Login from "./components/Login";
import FleetView from "./components/FleetView";
import TruckView from "./components/TruckView";

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined); // undefined = still checking the session
  const [fleet, setFleet] = useState<Fleet | null>(null);
  const [error, setError] = useState<string | null>(null);
  const route = useRoute();

  const handle = useCallback((e: unknown) => {
    if (e instanceof Unauthorized) setMe(null);
    else setError(e instanceof Error ? e.message : String(e));
  }, []);

  useEffect(() => {
    api.me().then(setMe).catch(handle);
  }, [handle]);

  useEffect(() => {
    if (me) api.fleet().then(setFleet).catch(handle);
    else setFleet(null);
  }, [me, handle]);

  if (me === undefined) return <div className="center muted">Loading…</div>;
  if (me === null) return <Login onSignedIn={setMe} />;

  const signOut = async () => {
    await api.logout().catch(() => undefined);
    setMe(null);
  };

  return (
    <div className="shell">
      <header className="topbar">
        <a className="brand" href="#/">
          <span className="brand-mark" aria-hidden="true" />
          Cold Chain Ops
        </a>
        {fleet && (
          <span className="asof" title={fleet.source}>
            Data {date(fleet.dataStart)}, {time(fleet.dataStart)} to {time(fleet.dataEnd)} UTC
          </span>
        )}
        <span className="spacer" />
        <span className="user">
          {me.displayName}
          <span className="muted">{me.allTrucks ? " · all trucks" : ` · ${me.trucks.join(", ")}`}</span>
        </span>
        <button className="ghost" onClick={signOut}>
          Sign out
        </button>
      </header>

      <main className="content">
        {error && (
          <p className="error" role="alert">
            Could not load data: {error}
          </p>
        )}
        {!fleet && !error && <p className="muted">Loading fleet…</p>}
        {fleet && route.page === "fleet" && <FleetView fleet={fleet} filter={route} />}
        {fleet && route.page === "truck" && <TruckView truckId={route.truckId} onError={handle} />}
      </main>
    </div>
  );
}
