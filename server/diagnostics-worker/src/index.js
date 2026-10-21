import { MAX_REPORT_BYTES, RequestError, fingerprint, sanitizeReport, sha256 } from "./report.js";
import { GitHubIssues, configured } from "./github.js";

const HOUR = 60 * 60 * 1000;
const DAY = 24 * HOUR;
const DEDUPLICATION_TTL = 7 * DAY;

function json(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), {
    status, headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", ...headers },
  });
}
function errorResponse(error) {
  const expected = error instanceof RequestError;
  return json({ error: expected ? error.message : "service_unavailable" }, expected ? error.status : 503,
    expected && error.retryAfter ? { "Retry-After": String(error.retryAfter) } : {});
}

async function readJson(request, limit) {
  const size = request.headers.get("Content-Length");
  if (size && (!/^\d+$/.test(size) || Number(size) > limit)) throw new RequestError(413, "report_too_large");
  if (request.headers.get("Content-Encoding") && request.headers.get("Content-Encoding") !== "identity")
    throw new RequestError(415, "unsupported_encoding");
  const reader = request.body?.getReader();
  if (!reader) throw new RequestError(400, "invalid_json");
  const chunks = [];
  let length = 0;
  let timeout;
  try {
    const deadline = new Promise((_, reject) => { timeout = setTimeout(() => reject(new RequestError(408, "request_timeout")), 10000); });
    while (true) {
      const { value, done } = await Promise.race([reader.read(), deadline]);
      if (done) break;
      length += value.byteLength;
      if (length > limit) throw new RequestError(413, "report_too_large");
      chunks.push(value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    try { return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)); }
    catch { throw new RequestError(400, "invalid_json"); }
  } finally {
    clearTimeout(timeout);
    // Cancellation is best effort; do not await an untrusted stream's cancellation.
    reader.cancel().catch(() => {});
  }
}

export default {
  async fetch(request, env) {
    try {
      const path = new URL(request.url).pathname;
      if (path === "/health" && request.method === "GET")
        return json({ configured: configured(env) && Boolean(env.REPORT_COORDINATOR) });
      if (path !== "/v1/reports") return json({ error: "not_found" }, 404);
      if (request.method !== "POST") return json({ error: "method_not_allowed" }, 405, { Allow: "POST" });
      if (!configured(env) || !env.REPORT_COORDINATOR) throw new RequestError(503, "not_configured");
      if (request.headers.get("Content-Type")?.split(";", 1)[0].trim().toLowerCase() !== "application/json")
        throw new RequestError(415, "json_required");
      const ip = request.headers.get("CF-Connecting-IP");
      if (!ip || ip.length > 64 || !/^[0-9a-fA-F:.]+$/.test(ip)) throw new RequestError(400, "client_ip_unavailable");
      const report = sanitizeReport(await readJson(request, MAX_REPORT_BYTES));
      const ipHash = await sha256(`${env.IP_HASH_SALT}\n${ip}`);
      const id = env.REPORT_COORDINATOR.idFromName("global");
      return await env.REPORT_COORDINATOR.get(id).fetch(new Request("https://diagnostics.internal/v1/reports", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ report, ipHash }),
      }));
    } catch (error) { return errorResponse(error); }
  },
};

// The single named SQLite-backed object serializes the complete read/create/write
// sequence, including GitHub awaits, so concurrent matching reports create one issue.
export class ReportCoordinator {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.sql = ctx.storage.sql;
    this.github = new GitHubIssues(env);
    this.sql.exec("CREATE TABLE IF NOT EXISTS reports (fingerprint TEXT PRIMARY KEY, issue_url TEXT NOT NULL, expires INTEGER NOT NULL)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS counters (key TEXT PRIMARY KEY, count INTEGER NOT NULL, expires INTEGER NOT NULL)");
  }

  async fetch(request) {
    try {
      if (!configured(this.env)) throw new RequestError(503, "not_configured");
      const payload = await readJson(request, MAX_REPORT_BYTES + 256);
      if (!payload || typeof payload !== "object" || Array.isArray(payload) ||
          Object.keys(payload).some(key => !["report", "ipHash"].includes(key)) ||
          typeof payload.ipHash !== "string" || !/^[a-f0-9]{64}$/.test(payload.ipHash))
        throw new RequestError(400, "invalid_report");
      const report = sanitizeReport(payload.report);
      const hash = await fingerprint(report);
      return await this.ctx.blockConcurrencyWhile(async () => {
        try {
          const now = Date.now();
          this.cleanup(now);
          this.consumeRateLimit(payload.ipHash, now);
          await this.scheduleCleanup();
          const previous = this.sql.exec("SELECT issue_url FROM reports WHERE fingerprint = ?", hash).toArray()[0];
          if (previous) return json({ issueUrl: previous.issue_url, duplicate: true });
          const issueUrl = await this.github.create(report, hash);
          this.sql.exec("INSERT INTO reports (fingerprint, issue_url, expires) VALUES (?, ?, ?)", hash, issueUrl, now + DEDUPLICATION_TTL);
          await this.scheduleCleanup();
          return json({ issueUrl, duplicate: false }, 201);
        } catch (error) { return errorResponse(error); }
      });
    } catch (error) { return errorResponse(error); }
  }

  consumeRateLimit(ipHash, now) {
    const hour = Math.floor(now / HOUR);
    const day = Math.floor(now / DAY);
    // Check global limits first; rejected random IPs cannot grow persistent storage.
    const buckets = [
      { key: `global-hour:${hour}`, limit: 30, expires: (hour + 1) * HOUR },
      { key: `global-day:${day}`, limit: 100, expires: (day + 1) * DAY },
      { key: `ip:${hour}:${ipHash}`, limit: 5, expires: (hour + 1) * HOUR },
    ];
    for (const bucket of buckets) {
      const current = this.sql.exec("SELECT count FROM counters WHERE key = ?", bucket.key).toArray()[0]?.count ?? 0;
      if (current >= bucket.limit)
        throw new RequestError(429, "rate_limited", Math.max(1, Math.ceil((bucket.expires - now) / 1000)));
    }
    for (const bucket of buckets)
      this.sql.exec("INSERT INTO counters (key, count, expires) VALUES (?, 1, ?) ON CONFLICT(key) DO UPDATE SET count = count + 1", bucket.key, bucket.expires);
  }

  cleanup(now) {
    this.sql.exec("DELETE FROM reports WHERE expires <= ?", now);
    this.sql.exec("DELETE FROM counters WHERE expires <= ?", now);
  }

  async scheduleCleanup() {
    const next = this.sql.exec("SELECT MIN(expires) AS expires FROM (SELECT expires FROM reports UNION ALL SELECT expires FROM counters)").toArray()[0]?.expires;
    const current = await this.ctx.storage.getAlarm();
    if (next && (current === null || next < current)) await this.ctx.storage.setAlarm(next);
  }

  async alarm() {
    return this.ctx.blockConcurrencyWhile(async () => {
      this.cleanup(Date.now());
      await this.scheduleCleanup();
    });
  }
}
