#!/usr/bin/env node
// A small stand-in for an MCP Apps host, to look at the dbdatabuild app in a browser without Claude Desktop or VS Code.
//   node scripts/mcp-app-host.mjs <project dir> [--allow-apply] [--port 8799] [--exe path/to/dbdatabuild] [--inspectable]
// --inspectable lets the host page reach into the frame (sandbox gains allow-same-origin) so a script can drive the app; a real host never does.
// It starts `dbdatabuild ui mcp` for the project, serves a page on 127.0.0.1 that embeds the app (ui://dbdatabuild/app) in a sandboxed iframe, answers the app's ui/initialize, forwards its tools/call to the
// server (and refuses a tool whose _meta.ui.visibility lacks "app", as a host must), and has buttons that call a tool the way a model would and hand the result to the app.
// It checks nothing about a real host's behavior: it is the specification's message flow, written once, so the app can be driven and looked at.
import { spawn } from "node:child_process";
import http from "node:http";
import readline from "node:readline";

const args = process.argv.slice(2);
const project = args.find((a) => !a.startsWith("--"));
if (!project) { console.error("usage: mcp-app-host.mjs <project dir> [--allow-apply] [--port N] [--exe path]"); process.exit(2); }
const opt = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const port = Number(opt("--port", 8799));
const exe = opt("--exe", null);
const serverArgs = ["ui", "mcp", "--project", project, ...(args.includes("--allow-apply") ? ["--allow-apply"] : [])];
const child = exe ? spawn(exe, serverArgs, { stdio: ["pipe", "pipe", "inherit"] }) : spawn("dotnet", ["run", "--no-build", "--project", new URL("../src/DbDataBuild.Cli", import.meta.url).pathname, "--", ...serverArgs], { stdio: ["pipe", "pipe", "inherit"] });

let nextId = 1;
const waiting = new Map();
readline.createInterface({ input: child.stdout }).on("line", (line) => { try { const m = JSON.parse(line); if (m.id !== undefined && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); } } catch { /* not a reply */ } });
const rpc = (method, params) => new Promise((resolve) => { const id = nextId++; waiting.set(id, resolve); child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n"); });

const init = await rpc("initialize", { protocolVersion: "2025-06-18", clientInfo: { name: "mcp-app-host", version: "0" }, capabilities: { extensions: { "io.modelcontextprotocol/ui": { mimeTypes: ["text/html;profile=mcp-app"] } } } });
child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
const tools = (await rpc("tools/list", {})).result.tools;
const ui = (await rpc("resources/read", { uri: "ui://dbdatabuild/app" })).result.contents[0];
console.log(`server ${init.result.serverInfo.name}; ${tools.length} tools; app ${ui.mimeType}, ${ui.text.length} characters`);

// JSON inside a <script> block must not contain a literal "</script>"
const safe = (v) => JSON.stringify(v).replace(/</g, "\\u003c");
const hostPage = `<!doctype html><meta charset="utf-8"><title>MCP app host (stand-in)</title>
<style>body{font:14px system-ui;margin:0;background:#eee}header{padding:8px 12px;background:#333;color:#fff;display:flex;gap:8px;flex-wrap:wrap;align-items:center}button{font:inherit}iframe{width:100%;border:0;display:block;background:#fff}#log{font:11px monospace;max-height:120px;overflow:auto;padding:4px 12px;background:#fff}</style>
<header><b>stand-in host</b><button data-tool="show" data-args='{"screen":"plans"}'>model calls show(plans)</button><button data-tool="review" data-args='{}'>model calls review</button><button data-tool="graph" data-args='{}'>model calls graph</button><button id="theme">theme</button></header>
<iframe id="app" sandbox="${args.includes("--inspectable") ? "allow-scripts allow-same-origin" : "allow-scripts"}" style="height:700px"></iframe><div id="log"></div>
<script>
const tools = ${safe(tools)};
const appHtml = ${safe(ui.text)};
const frame = document.getElementById("app"); const log = (t) => { const d = document.createElement("div"); d.textContent = t; document.getElementById("log").prepend(d); };
let theme = "light";
const rpc = async (method, params) => (await (await fetch("/rpc", { method: "POST", body: JSON.stringify({ method, params }) })).json());
const reply = (id, result, error) => frame.contentWindow.postMessage(error ? { jsonrpc: "2.0", id, error } : { jsonrpc: "2.0", id, result }, "*");
addEventListener("message", async (ev) => {
  if (ev.source !== frame.contentWindow) return;
  const m = ev.data; if (!m || m.jsonrpc !== "2.0") return;
  if (m.method === "ui/initialize") { log("app: ui/initialize"); reply(m.id, { protocolVersion: "2026-01-26", hostInfo: { name: "stand-in", version: "0" }, hostCapabilities: { serverTools: {} }, hostContext: { theme, displayMode: "inline" } }); }
  else if (m.method === "tools/call") {
    const t = tools.find((x) => x.name === m.params.name);
    const visibility = t?._meta?.ui?.visibility ?? ["model", "app"];
    if (!t || !visibility.includes("app")) { log("app: tools/call " + m.params.name + " REFUSED (not visible to the app)"); return reply(m.id, null, { code: -32602, message: "Tool not available to the app" }); }
    log("app: tools/call " + m.params.name); const r = await rpc("tools/call", m.params); reply(m.id, r.result, r.error);
  }
  else if (m.method === "ui/notifications/size-changed") frame.style.height = Math.max(300, m.params.height + 4) + "px";
  else if (m.method === "ui/notifications/initialized") log("app: initialized");
  else log("app: " + m.method);
});
frame.srcdoc = appHtml;
document.querySelectorAll("button[data-tool]").forEach((b) => b.onclick = async () => {
  const name = b.dataset.tool, args = JSON.parse(b.dataset.args);
  log("model: tools/call " + name); const r = await rpc("tools/call", { name, arguments: args });
  frame.contentWindow.postMessage({ jsonrpc: "2.0", method: "ui/notifications/tool-result", params: r.result }, "*");
});
document.getElementById("theme").onclick = () => { theme = theme === "light" ? "dark" : "light"; frame.contentWindow.postMessage({ jsonrpc: "2.0", method: "ui/notifications/host-context-changed", params: { theme } }, "*"); };
</script>`;

http.createServer((req, res) => {
  if (req.method === "POST" && req.url === "/rpc") {
    let body = ""; req.on("data", (c) => (body += c)); req.on("end", async () => { const r = await rpc(JSON.parse(body).method, JSON.parse(body).params); res.setHeader("content-type", "application/json"); res.end(JSON.stringify(r)); });
  } else { res.setHeader("content-type", "text/html; charset=utf-8"); res.end(hostPage); }
}).listen(port, "127.0.0.1", () => console.log(`host page: http://127.0.0.1:${port}/  (Ctrl+C to stop)`));
process.on("SIGINT", () => { child.kill(); process.exit(0); });
