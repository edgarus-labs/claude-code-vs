#!/usr/bin/env node
// Launcher for @agentclientprotocol/claude-agent-acp used by Claude Code for Visual Studio.
//
// It runs the stock adapter unchanged (same entry logic as its dist/index.js) and adds ONE
// JSON-RPC extension request the adapter does not expose: "_vs/remoteControl". That request is
// intercepted at the transport level (before the adapter's ACP parser sees it) and answered by
// calling the Agent SDK's `enableRemoteControl` control request on the session's query - the same
// call the official VS Code extension makes - so a session can be driven from claude.ai/code.
//
// Everything else on stdin/stdout passes through untouched.
import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { PassThrough } from "node:stream";
import { createInterface } from "node:readline";

const REMOTE_CONTROL_METHOD = "_vs/remoteControl";

const adapterDir = process.env.CLAUDE_ACP_ADAPTER_DIR;
if (!adapterDir) {
  console.error("CLAUDE_ACP_ADAPTER_DIR is not set; cannot locate @agentclientprotocol/claude-agent-acp.");
  process.exit(1);
}

const sdkUrl = pathToFileURL(join(adapterDir, "node_modules", "@anthropic-ai", "claude-agent-sdk", "sdk.mjs")).href;
const agentUrl = pathToFileURL(join(adapterDir, "dist", "acp-agent.js")).href;

const { resolveSettings } = await import(sdkUrl);
const { runAcp } = await import(agentUrl);

// --- identical to the adapter's own entry point ---------------------------------------------
const policy = await resolveSettings({ settingSources: [] });
for (const [key, value] of Object.entries(policy.effective.env ?? {})) {
  process.env[key] = value;
}

// stdout carries ACP messages; everything else goes to stderr.
console.log = console.error;
console.info = console.error;
console.warn = console.error;
console.debug = console.error;
process.on("unhandledRejection", (reason, promise) => {
  console.error("Unhandled Rejection at:", promise, "reason:", reason);
});

const logDirectory = process.env.CLAUDE_AGENT_LOGS;
const logger = logDirectory
  ? (() => {
      mkdirSync(logDirectory, { recursive: true });
      const logFile = join(logDirectory, "agent.log");
      const writeLog = (...args) => {
        const rendered = args
          .map((arg) => (arg instanceof Error ? (arg.stack ?? arg.message) : String(arg)))
          .join(" ");
        appendFileSync(logFile, `${new Date().toISOString()} pid=${process.pid} ${rendered}\n`);
      };
      return {
        log: writeLog,
        error: (...args) => {
          console.error(...args);
          writeLog(...args);
        },
      };
    })()
  : undefined;

// --- transport-level interception ---------------------------------------------------------------
// The adapter reads `process.stdin`; hand it a PassThrough fed with every line except ours.
const realStdin = process.stdin;
const filteredStdin = new PassThrough();
Object.defineProperty(process, "stdin", { value: filteredStdin, configurable: true, writable: true });

let agent;

function respond(id, payload) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, ...payload }) + "\n");
}

async function handleRemoteControl(message) {
  const params = message.params ?? {};
  try {
    const session = agent?.sessions?.[params.sessionId];
    const query = session?.query;
    if (!query || typeof query.enableRemoteControl !== "function") {
      throw new Error("Remote Control is not available for this session (unknown session or unsupported SDK).");
    }

    const enabled = params.enabled === true;
    const name = enabled && typeof params.name === "string" && params.name.trim().length > 0 ? params.name.trim() : undefined;
    const result = await query.enableRemoteControl(enabled, name);
    logger?.log(`[vs] remote control ${enabled ? "enabled" : "disabled"} for ${params.sessionId}`);
    respond(message.id, {
      result: {
        enabled,
        sessionUrl: typeof result?.session_url === "string" ? result.session_url : null,
        connectUrl: typeof result?.connect_url === "string" ? result.connect_url : null,
        bridgeSessionId: typeof result?.bridge_session_id === "string" ? result.bridge_session_id : null,
      },
    });
  } catch (error) {
    const text = error instanceof Error ? error.message : String(error);
    logger?.error(`[vs] remote control failed: ${text}`);
    respond(message.id, { error: { code: -32000, message: text } });
  }
}

const lines = createInterface({ input: realStdin, crlfDelay: Infinity });
lines.on("line", (line) => {
  if (line.length === 0) {
    return;
  }

  let message = null;
  try {
    message = JSON.parse(line);
  } catch {
    // Not JSON: let the adapter report the framing error.
  }

  if (message && message.method === REMOTE_CONTROL_METHOD && message.id !== undefined) {
    void handleRemoteControl(message);
    return;
  }

  filteredStdin.write(line + "\n");
});
lines.on("close", () => filteredStdin.end());

// --- run the adapter -----------------------------------------------------------------------------
logger?.log("Claude ACP (Visual Studio launcher) started");
const started = runAcp(logger);
agent = started.agent;

async function shutdown() {
  await agent.dispose().catch((err) => {
    console.error("Error during cleanup:", err);
  });
  process.exit(0);
}

started.connection.closed.then(shutdown);
process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);
realStdin.resume();
