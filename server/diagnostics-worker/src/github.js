import { RequestError, issueContent } from "./report.js";

const API = "https://api.github.com";
const REPOSITORY = "YangYingX/Perivolt";
const encoder = new TextEncoder();

function base64url(bytes) {
  let text = "";
  for (const byte of bytes) text += String.fromCharCode(byte);
  return btoa(text).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}
function der(tag, bytes) {
  let length = bytes.length;
  const size = [];
  do { size.unshift(length & 255); length >>>= 8; } while (length);
  return new Uint8Array([tag, ...(bytes.length < 128 ? [bytes.length] : [128 | size.length, ...size]), ...bytes]);
}
function privateKeyBytes(pem) {
  const rsa = pem.includes("-----BEGIN RSA PRIVATE KEY-----");
  const encoded = pem.replace(/-----[A-Z ]+-----/g, "").replace(/\s/g, "");
  const bytes = Uint8Array.from(atob(encoded), value => value.charCodeAt(0));
  if (!rsa) return bytes;
  // GitHub downloads PKCS#1; Web Crypto imports PKCS#8. Wrap the RSA DER without a dependency.
  return der(0x30, new Uint8Array([
    0x02, 0x01, 0x00, 0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48,
    0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00, ...der(0x04, bytes),
  ]));
}

export function configured(env) {
  return /^[1-9]\d{0,18}$/.test(env.GITHUB_APP_ID ?? "") &&
    /^[1-9]\d{0,18}$/.test(env.GITHUB_INSTALLATION_ID ?? "") &&
    typeof env.GITHUB_PRIVATE_KEY === "string" && env.GITHUB_PRIVATE_KEY.length <= 16384 &&
    /^-----BEGIN (RSA )?PRIVATE KEY-----[\s\S]+-----END (RSA )?PRIVATE KEY-----\s*$/.test(env.GITHUB_PRIVATE_KEY) &&
    typeof env.IP_HASH_SALT === "string" && env.IP_HASH_SALT.length >= 32 && env.IP_HASH_SALT.length <= 256;
}

async function jwt(env) {
  const now = Math.floor(Date.now() / 1000);
  const header = base64url(encoder.encode(JSON.stringify({ alg: "RS256", typ: "JWT" })));
  const payload = base64url(encoder.encode(JSON.stringify({ iat: now - 60, exp: now + 540, iss: env.GITHUB_APP_ID })));
  const key = await crypto.subtle.importKey("pkcs8", privateKeyBytes(env.GITHUB_PRIVATE_KEY),
    { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, encoder.encode(`${header}.${payload}`));
  return `${header}.${payload}.${base64url(new Uint8Array(signature))}`;
}

async function githubRequest(path, authorization, body) {
  // No redirects: an upstream redirect must never receive the installation credential.
  return fetch(API + path, {
    method: "POST", redirect: "error", signal: AbortSignal.timeout(10000),
    headers: {
      Accept: "application/vnd.github+json", Authorization: `Bearer ${authorization}`,
      "Content-Type": "application/json", "User-Agent": "Perivolt-Diagnostics",
      "X-GitHub-Api-Version": "2026-03-10",
    },
    body: JSON.stringify(body),
  });
}

async function responseJson(response, limit) {
  const reader = response.body?.getReader();
  if (!reader) throw new RequestError(502, "github_unavailable");
  let bytes = 0;
  const chunks = [];
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      bytes += value.byteLength;
      if (bytes > limit) throw new RequestError(502, "github_unavailable");
      chunks.push(value);
    }
    const buffer = new Uint8Array(bytes);
    let offset = 0;
    for (const chunk of chunks) { buffer.set(chunk, offset); offset += chunk.byteLength; }
    return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(buffer));
  } finally { await reader.cancel().catch(() => {}); }
}

export class GitHubIssues {
  constructor(env) {
    this.env = env;
    this.token = undefined;
    this.tokenExpires = 0;
  }

  async installationToken() {
    if (this.token && Date.now() < this.tokenExpires) return this.token;
    try {
      const response = await githubRequest(`/app/installations/${this.env.GITHUB_INSTALLATION_ID}/access_tokens`,
        await jwt(this.env), { repositories: ["Perivolt"], permissions: { issues: "write" } });
      if (!response.ok) {
        await response.body?.cancel();
        throw new RequestError(502, "github_unavailable");
      }
      const data = await responseJson(response, 16384);
      const expires = Date.parse(data.expires_at);
      if (typeof data.token !== "string" || !/^[A-Za-z0-9_]{20,512}$/.test(data.token) ||
          !Number.isFinite(expires) || expires < Date.now() + 60000) throw new RequestError(502, "github_unavailable");
      this.token = data.token;
      this.tokenExpires = expires - 60000;
      return this.token;
    } catch {
      this.token = undefined;
      this.tokenExpires = 0;
      throw new RequestError(502, "github_unavailable");
    }
  }

  async create(report, hash) {
    try {
      const token = await this.installationToken();
      const response = await githubRequest(`/repos/${REPOSITORY}/issues`, token, issueContent(report, hash));
      if (response.status !== 201) {
        if (response.status === 401 || response.status === 403) {
          this.token = undefined;
          this.tokenExpires = 0;
        }
        await response.body?.cancel();
        throw new RequestError(502, "github_unavailable");
      }
      const issue = await responseJson(response, 131072);
      if (!Number.isSafeInteger(issue.number) || issue.number < 1) throw new RequestError(502, "github_unavailable");
      return `https://github.com/${REPOSITORY}/issues/${issue.number}`;
    } catch {
      throw new RequestError(502, "github_unavailable");
    }
  }
}
