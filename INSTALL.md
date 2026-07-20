# Installing the WritersBlock MCP connector

The WritersBlock connector is a small app that runs **on your machine** and exposes the WritersBlock
API to MCP-capable AI clients (Claude Desktop, Claude Code, VS Code/Copilot, Cursor). It speaks MCP
over stdio and turns every WritersBlock API function into a tool. Nothing runs on WritersBlock's
servers — each request the connector makes is ordinary authenticated API traffic from your account.

> **Don't want to install anything?** WritersBlock also hosts a **remote connector** that works from
> claude.ai in the browser **and the Claude mobile apps** — no download, no local process. It exposes
> a curated subset of tools rather than the full catalog. See
> [Remote connector](#0-no-install-option--remote-connector-claudeai-web--mobile) below to decide
> which fits.

There are two ways to install the local connector:

- **[1. Claude Desktop — one-click bundle](#1-claude-desktop--one-click-mcpb-bundle)** (`.mcpb`) — easiest.
- **[2. Manual — download an archive](#2-manual--download-the-archive)** — for Claude Code, VS Code, Cursor, or Claude Desktop by hand.

Then see **[3. First run](#3-first-run--signing-in)** and **[4. Troubleshooting](#4-troubleshooting)**.

> **Deployment prerequisite.** Production use requires that the server side has shipped: the
> WritersBlock **API** must expose the `GET /api/mcp/manifest` endpoint, and the **auth server**
> must have the `writersblock-mcp` OAuth client registered. If either is not yet deployed, the
> connector installs and starts but lists **zero tools** and/or login fails. Until then, point the
> connector at a local dev environment (see the dev notes below).

---

## 0. No-install option — remote connector (claude.ai web & mobile)

WritersBlock hosts a remote MCP server at `https://writersblock.jadedsoftware.com/mcp` (streamable
HTTP). Adding it as a **custom connector** on claude.ai gives you WritersBlock tools with no
download at all, and — because connectors are configured per account — it then works from the
**Claude mobile apps** too. Custom connectors are available on all Claude plans (Free allows one).

**Setup (once, on claude.ai in a browser):**

1. Go to **Settings → Connectors → Add custom connector**.
2. Remote MCP server URL: `https://writersblock.jadedsoftware.com/mcp`
3. Open **Advanced settings** and enter OAuth Client ID: `writersblock-mcp-remote` (leave the
   client secret blank).
4. Save, then click **Connect** — your browser goes through the normal WritersBlock sign-in.
   Your existing account access and subscription gating apply unchanged.

Once connected on the web, enable the connector in a conversation's tools menu — on desktop, web,
or your phone.

**Remote vs. local — which should you use?**

| | Remote connector | Local connector (this document) |
|---|---|---|
| Install | None | Download binary / `.mcpb` |
| Works in | claude.ai web, Claude Desktop, **Claude mobile** | Claude Desktop, Claude Code, VS Code, Cursor — any stdio MCP client |
| Tool surface | **Curated subset** (~40 core tools: projects, documents, characters, places, timelines, story attributes…) | **Every** API function (1,250+ tools) |
| Auth | OAuth in the browser when you connect | OAuth via system browser on first tool call |

Use the remote connector for reading and writing your projects from anywhere, especially your
phone. Use the local connector when you need the complete tool catalog or a non-Claude MCP client.

> The remote endpoint requires the current server deployment (API `/mcp` endpoint + the
> `writersblock-mcp-remote` OAuth client on the auth server). If "Connect" fails with an
> authorization error, the server side likely hasn't shipped yet.

---

## 1. Claude Desktop — one-click `.mcpb` bundle

The bundle is the friendliest path. It ships the connector binary and its configuration inside a
single file that Claude Desktop installs with one click.

**Platform:** the current bundle contains the **macOS (Apple Silicon / arm64)** binary. On Windows,
use the [manual install](#2-manual--download-the-archive) below (a Windows `.mcpb` is a planned
follow-up).

1. Download `writersblock-mcp-<version>-osx-arm64.mcpb`.
2. Double-click it (or, in Claude Desktop, **Settings → Extensions → Install from file…**) and
   confirm the install dialog.
3. That's it — no JSON to edit. By default the connector targets the production WritersBlock
   deployment. On the first tool call, your browser opens to sign in (see
   [First run](#3-first-run--signing-in)).

**Optional settings** (Claude Desktop → the extension's **Configure** panel — you can ignore these
for normal use):

| Setting | Default | When to change it |
|---|---|---|
| **API base URL** | `https://writersblock.jadedsoftware.com` | Point at a local API, e.g. `https://localhost:6001`. |
| **OAuth authority** | `https://auth.jadedsoftware.com` | Point at a local auth server, e.g. `https://localhost:6010`. |

> The bundle cannot pass the dev-only `--insecure` flag (needed for the self-signed local auth
> server). If you're doing **local dev against `https://localhost:6010`**, use the
> [manual JSON config](#claude-desktop--claude_desktop_configjson) instead, which lets you add
> `--insecure`.

### macOS Gatekeeper note (unsigned binary)

The connector is **not yet code-signed or notarized.** The `.mcpb` install path generally avoids the
Gatekeeper prompt, but if macOS blocks the binary with *"cannot be opened because the developer
cannot be verified"*, clear the quarantine attribute on the bundled executable. After install the
binary lives inside Claude Desktop's extensions directory; the manual-install steps below show the
`xattr` command against a binary you extracted yourself, which is the same fix.

Code-signing + notarization is a planned improvement (see [the README](README.md#status)).

---

## 2. Manual — download the archive

Use this for Claude Code, VS Code, Cursor, or a hand-configured Claude Desktop.

1. Download the archive for your platform and unpack it:
   - **macOS (Apple Silicon):** `writersblock-mcp-<version>-osx-arm64.tar.gz`
     ```bash
     tar -xzf writersblock-mcp-<version>-osx-arm64.tar.gz
     ```
   - **Windows (x64):** `writersblock-mcp-<version>-win-x64.zip` — extract with Explorer or
     `Expand-Archive`.

2. *(Optional but recommended)* verify the checksum against the shipped `.sha256`:
   ```bash
   shasum -a 256 -c writersblock-mcp-<version>-osx-arm64.tar.gz.sha256
   ```

3. **macOS only — clear Gatekeeper quarantine.** The binary is **unsigned**, so macOS quarantines
   anything downloaded from a browser. Remove the quarantine flag once, up front:
   ```bash
   xattr -d com.apple.quarantine ./writersblock-mcp
   ```
   *(Honest note: this bypasses Gatekeeper's developer-verification for this file. It's necessary
   only because the binary isn't yet notarized; signing + notarization is a planned improvement.)*

4. Note the **absolute path** to the executable (`writersblock-mcp` on macOS, `writersblock-mcp.exe`
   on Windows) — you'll paste it into the client config below.

5. *(Optional)* confirm it runs and shows the version:
   ```bash
   ./writersblock-mcp status
   ```
   You should see `Version : <version>` and `API base URL : https://writersblock.jadedsoftware.com`.

### Config snippets

All snippets below default to **production**. For **local dev**, add
`--api-url https://localhost:6001 --authority https://localhost:6010 --insecure` to `args`.

#### Claude Desktop — `claude_desktop_config.json`

Location: **macOS** `~/Library/Application Support/Claude/claude_desktop_config.json`,
**Windows** `%APPDATA%\Claude\claude_desktop_config.json`.

```json
{
  "mcpServers": {
    "writersblock": {
      "command": "/absolute/path/to/writersblock-mcp",
      "args": ["--api-url", "https://writersblock.jadedsoftware.com"]
    }
  }
}
```

#### Claude Code — `.mcp.json`

Place at your project (or repo) root:

```json
{
  "mcpServers": {
    "writersblock": {
      "command": "/absolute/path/to/writersblock-mcp",
      "args": ["--api-url", "https://writersblock.jadedsoftware.com"]
    }
  }
}
```

Or register it from the CLI:

```bash
claude mcp add writersblock -- /absolute/path/to/writersblock-mcp --api-url https://writersblock.jadedsoftware.com
```

#### VS Code / GitHub Copilot — `.vscode/mcp.json`

```json
{
  "servers": {
    "writersblock": {
      "type": "stdio",
      "command": "/absolute/path/to/writersblock-mcp",
      "args": ["--api-url", "https://writersblock.jadedsoftware.com"]
    }
  }
}
```

#### Cursor — `~/.cursor/mcp.json` (or project `.cursor/mcp.json`)

```json
{
  "mcpServers": {
    "writersblock": {
      "command": "/absolute/path/to/writersblock-mcp",
      "args": ["--api-url", "https://writersblock.jadedsoftware.com"]
    }
  }
}
```

#### Local dev variant (any client)

```jsonc
"args": [
  "--api-url",   "https://localhost:6001",
  "--authority", "https://localhost:6010",
  "--insecure"
]
```

`--insecure` relaxes TLS validation for the self-signed dev API and auth server. Never use it
against production.

---

## 3. First run — signing in

You don't run a login step by hand. The **first tool call** with no stored session:

1. starts a tiny loopback web server on the first free callback port (8171–8174),
2. opens your **system browser** to the WritersBlock sign-in page,
3. waits for you to sign in with your normal WritersBlock account,
4. stores the tokens in your OS credential store and silently refreshes them afterward.

Your existing subscription/access gating applies unchanged — the connector adds no privilege your
account doesn't already have.

If you'd rather sign in up front (recommended when testing the install), run the `login` verb:

```bash
# production
/absolute/path/to/writersblock-mcp login

# local dev
/absolute/path/to/writersblock-mcp login --authority https://localhost:6010 --insecure
```

Other verbs: `status` (who am I / where do tokens live), `logout` (delete stored tokens).

---

## 4. Troubleshooting

**The connector lists zero tools.**
Almost always this means **you're not signed in yet** (or the manifest hasn't been cached). Run
`writersblock-mcp login` and restart the client — the tool catalog is fetched from the server after
you authenticate, and loads on the next launch. If login succeeds but tools are still zero, the
WritersBlock **API's `/api/mcp/manifest` endpoint may not be deployed yet** (see the deployment
prerequisite at the top).

**Where are the logs? (Claude Desktop)**
`~/Library/Logs/Claude/mcp-server-writersblock.log` on macOS
(`%APPDATA%\Claude\logs\mcp-server-writersblock.log` on Windows). All connector diagnostics go to
stderr, which Claude Desktop captures here. Start here for any "it's not working" question.

**The browser never opens / login times out.**
Login binds a loopback listener on the first free port from the fixed list
`http://127.0.0.1:8171–8174/callback`. These four URIs are registered on the auth server as an
**exact-match** allow-list — no other port works. The listener is bound to `127.0.0.1` only and only
for the few seconds of login, so no **inbound** firewall rule is needed; but if a local firewall
blocks loopback listeners, or all four ports are in use, login fails with a clear message. Free a
port (or allow local loopback) and retry.

**macOS says the binary "cannot be opened".**
The binary is unsigned. Clear the quarantine flag: `xattr -d com.apple.quarantine ./writersblock-mcp`
(manual install), or reinstall via the `.mcpb` bundle.

**A tool call returns 401 / "sign in".**
Your session expired and couldn't be refreshed. Run `writersblock-mcp logout` then
`writersblock-mcp login`, or just trigger a tool call to re-login.

**I want to check my auth state.**
`writersblock-mcp status` prints the version, authority, API URL, signed-in user, token expiry, and
where tokens are stored.
