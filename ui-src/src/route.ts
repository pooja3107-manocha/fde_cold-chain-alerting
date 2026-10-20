import { useEffect, useState } from "react";

// Hash routes keep deep links working without server-side routing, e.g. an alert notification can link
// straight to  /#/trucks/T-04  and the fleet view keeps its filters in  /#/?truck=T-0&status=Alert.

export type Route =
  | { page: "fleet"; truck: string; status: string }
  | { page: "truck"; truckId: string };

export function parse(hash: string): Route {
  const [path, query = ""] = hash.replace(/^#/, "").split("?");
  const truckMatch = path.match(/^\/trucks\/(.+)$/);
  if (truckMatch) return { page: "truck", truckId: decodeURIComponent(truckMatch[1]) };
  const q = new URLSearchParams(query);
  return { page: "fleet", truck: q.get("truck") ?? "", status: q.get("status") ?? "" };
}

export function href(route: Route): string {
  if (route.page === "truck") return `#/trucks/${encodeURIComponent(route.truckId)}`;
  const q = new URLSearchParams();
  if (route.truck) q.set("truck", route.truck);
  if (route.status) q.set("status", route.status);
  const s = q.toString();
  return s ? `#/?${s}` : "#/";
}

export function navigate(route: Route, replace = false) {
  const next = href(route);
  if (replace) history.replaceState(null, "", next);
  else location.hash = next;
  if (replace) window.dispatchEvent(new HashChangeEvent("hashchange"));
}

export function useRoute(): Route {
  const [route, setRoute] = useState(() => parse(location.hash));
  useEffect(() => {
    const onChange = () => setRoute(parse(location.hash));
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return route;
}
