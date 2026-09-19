// Standalone helper spawned as a child process by ClaudeUsageService (never loaded in-process).
// Mirrors AcpAuthService's approach for auth status: credential access and any token handling stay
// inside this subprocess boundary; the extension host process only ever sees the JSON this prints.
//
// Reads the OAuth access token from the same credentials file the `claude` CLI itself uses, calls
// the CLI's own (undocumented) usage endpoint, and prints a trimmed { "limits": [...] } to stdout.
// Prints { "error": "<code>" } and exits 1 on any failure. Never logs the token.
"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const https = require("https");

function fail(code) {
  process.stdout.write(JSON.stringify({ error: code }) + "\n");
  process.exit(1);
}

function readAccessToken() {
  const configDir = process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), ".claude");
  const credentialsPath = path.join(configDir, ".credentials.json");
  let raw;
  try {
    raw = fs.readFileSync(credentialsPath, "utf8");
  } catch {
    return null;
  }

  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  const token = parsed && parsed.claudeAiOauth && parsed.claudeAiOauth.accessToken;
  return typeof token === "string" && token.length > 0 ? token : null;
}

function fetchUsage(token) {
  const req = https.request(
    "https://api.anthropic.com/api/oauth/usage",
    {
      method: "GET",
      timeout: 8000,
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
      },
    },
    (res) => {
      let body = "";
      res.on("data", (chunk) => {
        body += chunk;
        if (body.length > 1024 * 1024) {
          req.destroy();
        }
      });
      res.on("end", () => {
        if (res.statusCode !== 200) {
          fail(res.statusCode === 401 ? "unauthorized" : `http_${res.statusCode}`);
          return;
        }

        let parsed;
        try {
          parsed = JSON.parse(body);
        } catch {
          fail("bad_response");
          return;
        }

        const rawLimits = Array.isArray(parsed.limits) ? parsed.limits : [];
        const limits = rawLimits.map((limit) => ({
          kind: typeof limit.kind === "string" ? limit.kind : "",
          group: typeof limit.group === "string" ? limit.group : "",
          percent: typeof limit.percent === "number" ? limit.percent : 0,
          severity: typeof limit.severity === "string" ? limit.severity : "normal",
          resetsAt: typeof limit.resets_at === "string" ? limit.resets_at : null,
          scopeLabel: limit.scope && limit.scope.model && typeof limit.scope.model.display_name === "string"
            ? limit.scope.model.display_name
            : null,
          isActive: limit.is_active === true,
        }));

        process.stdout.write(JSON.stringify({ limits }) + "\n");
      });
    }
  );

  req.on("timeout", () => req.destroy());
  req.on("error", () => fail("network_error"));
  req.end();
}

const token = readAccessToken();
if (!token) {
  fail("no_credentials");
} else {
  fetchUsage(token);
}
