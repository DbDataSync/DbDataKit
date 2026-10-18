#!/usr/bin/env node
// Wraps an MCP server started over stdio and adds the MCP Apps extension to the client capabilities of the `initialize` request.
//   node scripts/mcp-advertise-ui.mjs -- dbdatabuild ui mcp --project <dir>
// For trying the app with a host that supports it but does not say so in `initialize` (the reference basic-host of the ext-apps repository is one). The server shows the app and its
// app-only tools only to a client that advertises the extension, because a host that does not know it would show those tools to the model. Do not put this in front of a real agent host.
import { spawn } from "node:child_process";
import readline from "node:readline";

const at = process.argv.indexOf("--");
if (at < 0 || at === process.argv.length - 1) { console.error("usage: mcp-advertise-ui.mjs -- <command> [args...]"); process.exit(2); }
const child = spawn(process.argv[at + 1], process.argv.slice(at + 2), { stdio: ["pipe", "inherit", "inherit"] });
readline.createInterface({ input: process.stdin }).on("line", (line) => {
  try {
    const m = JSON.parse(line);
    if (m.method === "initialize") {
      m.params ??= {}; m.params.capabilities ??= {}; m.params.capabilities.extensions ??= {};
      m.params.capabilities.extensions["io.modelcontextprotocol/ui"] = { mimeTypes: ["text/html;profile=mcp-app"] };
      line = JSON.stringify(m);
    }
  } catch { /* not JSON: pass it on and let the server answer */ }
  child.stdin.write(line + "\n");
}).on("close", () => child.stdin.end());
child.on("exit", (code) => process.exit(code ?? 0));
