import { useState, type FormEvent } from "react";
import { api, Unauthorized } from "../api";
import type { Me } from "../types";

export default function Login({ onSignedIn }: { onSignedIn: (me: Me) => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.login(username, password));
    } catch (err) {
      setError(err instanceof Unauthorized ? "Wrong username or password." : "Could not reach the server.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login-page">
      <form className="login" onSubmit={submit}>
        <h1>
          <span className="brand-mark" aria-hidden="true" />
          Cold Chain Ops
        </h1>
        <p className="muted">Sign in to see alerts for your trucks.</p>
        <label>
          Username
          <input autoFocus autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} />
        </label>
        <label>
          Password
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <button type="submit" disabled={busy || !username || !password}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
        <p className="hint">
          Demo users: <code>ops</code> (all trucks) and <code>north</code> (T-01 to T-03), password <code>coldchain</code>.
        </p>
      </form>
    </div>
  );
}
