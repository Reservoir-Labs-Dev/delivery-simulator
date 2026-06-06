// Thin fetch wrapper for dashboard-api's /chaos/* endpoints.
//
// Resolution order for the base URL:
//   1. REACT_APP_API_URL build-time env var (overrideable per environment)
//   2. Derived from REACT_APP_HUB_URL by stripping `/hubs/...` (so a single
//      env var configures both the SignalR hub and REST)
//   3. http://localhost:5000 (docker-compose default for dashboard-api)
//
// We deliberately keep this layer ultra-thin: callers do their own state
// management. The dashboard owns no shared HTTP cache.

function deriveApiBase() {
  if (process.env.REACT_APP_API_URL) return process.env.REACT_APP_API_URL;
  const hub = process.env.REACT_APP_HUB_URL;
  if (hub) {
    try {
      const u = new URL(hub);
      return `${u.protocol}//${u.host}`;
    } catch {
      /* fall through */
    }
  }
  return 'http://localhost:5000';
}

export const API_BASE = deriveApiBase();

/**
 * Lists every chaos scenario row currently stored in chaos.chaos_config.
 * Returned rows have shape: { name, enabled, params (string), updatedAt }.
 */
export async function listChaos(signal) {
  const res = await fetch(`${API_BASE}/chaos/list`, { signal });
  if (!res.ok) throw new Error(`GET /chaos/list -> ${res.status}`);
  return res.json();
}

/**
 * Upserts a chaos scenario row. `params` is a plain JS object; it is sent
 * to the API as a JSON object (the server stores it verbatim into jsonb).
 */
export async function setChaos(name, enabled, params) {
  const body = JSON.stringify({ name, enabled, params: params ?? {} });
  const res = await fetch(`${API_BASE}/chaos/set`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body,
  });
  if (!res.ok) {
    const detail = await res.text().catch(() => '');
    throw new Error(`POST /chaos/set -> ${res.status} ${detail}`);
  }
  return res.json();
}
