# WritersBlock.Mcp

A small .NET 10 console app that runs **on the user's machine** and exposes the WritersBlock
API to MCP-capable AI clients (Claude Desktop, Claude Code, VS Code/Copilot, Cursor).

It speaks **MCP over stdio**, fetches the tool catalog from the WritersBlock API's manifest
endpoint at startup, registers one MCP tool per API action, and dispatches each tool call as an
ordinary authenticated HTTPS request. Each user runs their own connector, and everything it sends
the API is indistinguishable from normal traffic.

> **Sibling: the hosted remote connector.** WritersBlock also serves a **remote MCP endpoint**
> (streamable HTTP) at `https://writersblock.jadedsoftware.com/mcp`, added on claude.ai as a custom
> connector (OAuth client `writersblock-mcp-remote`) — no install, works from claude.ai web and the
> **Claude mobile apps**, but exposes a **curated ~40-tool subset** instead of this connector's full
> catalog. That endpoint lives in the main WritersBlock repo (`Services/Mcp/Remote/`), not here; it
> reuses the same manifest catalog server-side. End-user setup:
> [INSTALL.md § Remote connector](INSTALL.md#0-no-install-option--remote-connector-claudeai-web--mobile).

The connector signs the user in with their normal WritersBlock account through the **system
browser** (Authorization Code + PKCE, loopback redirect), stores the tokens in the **OS credential
store**, and refreshes them silently. The Phase-1 static-token escape hatch (`--token`) is preserved
for scripted/dev use.

## Installing (end users)

**See [INSTALL.md](INSTALL.md)** for the end-user install guide: the no-install **remote connector**
(claude.ai web + mobile), the one-click Claude Desktop `.mcpb` bundle, manual archive downloads with
per-client config snippets (Claude Desktop, Claude Code, VS Code, Cursor), the macOS Gatekeeper
note, first-run sign-in, and troubleshooting. The rest of this README is developer-oriented
reference.

> **Deployment prerequisite (production).** The connector talks to two server-side deploys that must
> already be live: the WritersBlock **API** must serve `GET /api/mcp/manifest`, and the **auth
> server** must have the `writersblock-mcp` OAuth client registered. Until both ship, the connector
> starts but lists **zero tools** and/or login fails — develop against a local API + auth server
> (below) in the meantime.

## Status

- Version **1.0.0**. The MCP `serverInfo.version` and the outbound `User-Agent`
  (`WritersBlock.Mcp/<version>`) both derive from the assembly version — bump `<Version>` in the
  csproj and both follow.
- Distributed as self-contained single-file binaries (osx-arm64, win-x64) plus an osx-arm64 `.mcpb`
  bundle. Build them with [`Scripts/publish-connector.sh`](#packaging--release).
- The binaries are **not yet code-signed or notarized** (macOS Gatekeeper needs a one-time
  `xattr -d com.apple.quarantine`; see INSTALL.md). Signing/notarization and a Windows `.mcpb` are
  planned follow-ups.

## Signing in — the first tool call pops a browser

You don't run any login step by hand. The **first tool call** with no stored token:

1. starts a tiny loopback web server on the first free callback port (see below),
2. opens your **system browser** to the WritersBlock sign-in page,
3. waits (up to 180s) for you to sign in with your normal account,
4. completes the OAuth code+PKCE exchange, shows a "Login complete — you can close this tab"
   page, and stores your tokens in the OS keychain.

Subsequent calls reuse the stored access token and **refresh it silently** (proactively, ~60s
before expiry, using the refresh token) — you won't see the browser again until the refresh token
itself expires or you sign out. Existing subscription/access gating is unchanged: the token
carries the same `writersblock` app-role claims the API already requires.

You can also drive login explicitly with the CLI verbs below.

## CLI verbs

Run the binary with a verb as the first argument to manage auth, instead of starting the MCP
server. Verb output goes to **stdout** (these are human commands); the plain MCP-server invocation
keeps stdout protocol-clean.

| Command | What it does |
|---|---|
| `writersblock-mcp login` | Force an interactive browser login now (honors `--authority` / `--insecure`). |
| `writersblock-mcp logout` | Delete stored tokens for the configured authority. |
| `writersblock-mcp status` | Report authority, signed-in user (from the id_token), token expiry, and storage backend. |
| `writersblock-mcp` *(no verb)* | Start the MCP stdio server (the mode your AI client uses). |

```bash
writersblock-mcp login   --authority https://auth.jadedsoftware.com
writersblock-mcp status  --authority https://auth.jadedsoftware.com
writersblock-mcp logout  --authority https://auth.jadedsoftware.com
```

## How it works

1. On launch it resolves a token (order below) and `GET`s `{apiBase}/api/mcp/manifest`,
   unwraps the standard WritersBlock envelope, and caches the raw JSON + `ETag` under the OS
   local-app-data folder (`WritersBlock.Mcp/`). Startup **never** pops a browser — if you're not
   signed in yet it serves the cached manifest (or starts with zero tools) and defers login to
   the first tool call, so the connector always boots fast and keeps stdout clean.
2. It sends `If-None-Match` on subsequent launches; on `304`, or on any fetch failure
   (network, `5xx`), it falls back to the cached manifest and warns on stderr.
3. Each MCP tool call is turned into the matching `GET/POST/PUT/PATCH/DELETE` request:
   route tokens substituted, query/header params applied, JSON `body` passed through, base64
   `formFile` params sent as `multipart/form-data`. The response envelope is unwrapped —
   success returns `data`; API failures return an MCP `isError` result carrying `message` +
   `errors` (never a protocol error, so an agent can read validation failures).
4. **401 handling:** on a `401` the connector refreshes the token and retries once; if it's still
   `401`, it clears the stored tokens and (unless `--no-interactive-login` is set) re-runs the
   browser login and retries once more. Persistent failures surface as a readable `isError`.

**Tools update automatically from the server manifest** — a new API action ships to every
installed connector on its next launch, with zero connector release and zero registration step.

## Token resolution order (every outbound API call)

1. **Static token** — `--token` / `WRITERSBLOCK_MCP_TOKEN`, if provided. The escape hatch: OAuth
   is skipped entirely and the token is used as-is (no refresh; a `401` just reports it's stale).
2. **Stored OAuth tokens** — refreshed proactively when within ~60s of expiry.
3. **Interactive login** — if there's no valid stored token. Suppressed by `--no-interactive-login`,
   in which case the call returns a clear error telling you to run `writersblock-mcp login`.

## Configuration

Environment variable, overridden by the CLI flag:

| Setting | Env var | Flag | Default |
|---|---|---|---|
| API base URL | `WRITERSBLOCK_API_URL` | `--api-url <url>` | `https://writersblock.jadedsoftware.com` (prod) |
| OAuth authority | `WRITERSBLOCK_AUTHORITY` | `--authority <url>` | `https://auth.jadedsoftware.com` (prod) |
| Static bearer token (escape hatch) | `WRITERSBLOCK_MCP_TOKEN` | `--token <token>` | *(none)* |
| Skip TLS validation (dev self-signed certs) | — | `--insecure` | off |
| Never open a browser | — | `--no-interactive-login` | off (login allowed) |

`--insecure` applies to **all** HTTP: the API calls *and* the authority discovery/token/refresh
calls, and it relaxes discovery issuer/endpoint checks — required against the dev auth server's
self-signed `https://localhost:6010`.

All server-mode logging goes to **stderr** (stdout is the MCP transport and must stay clean
JSON-RPC).

### Dev vs. prod

```bash
# Production — the defaults. Both --api-url (prod app URL) and --authority (prod auth) are the
# built-in defaults, so bare `writersblock-mcp` targets production. Pass them explicitly if you like:
writersblock-mcp --api-url https://writersblock.jadedsoftware.com --authority https://auth.jadedsoftware.com

# Local dev — you MUST override all three (self-signed certs on both the API and the auth server):
writersblock-mcp \
  --api-url   https://localhost:6001 \
  --authority https://localhost:6010 \
  --insecure
```

## Callback ports (firewall note)

Login binds a loopback HTTP listener on the **first free** port from this fixed, ordered list and
uses that port's URI as the OAuth redirect:

```
http://127.0.0.1:8171/callback
http://127.0.0.1:8172/callback
http://127.0.0.1:8173/callback
http://127.0.0.1:8174/callback
```

These four URIs are registered on the auth server as an **exact-match** allow-list — no other
port or host will work. The listener is bound only to `127.0.0.1` (loopback) and only for the few
seconds of the login round-trip, so no inbound firewall rule is needed. If a local firewall blocks
loopback listeners, or all four ports are in use, login fails with a clear message; free a port or
allow local loopback and retry.

## Where tokens are stored (per OS)

Tokens are namespaced per **authority host**, so a dev login and a prod login never collide.

| OS | Backend | Location |
|---|---|---|
| macOS | Keychain (`/usr/bin/security`) | generic-password item, service `writersblock-mcp:<authority-host>` |
| Windows | DPAPI (`ProtectedData`, CurrentUser) | encrypted file under `%LOCALAPPDATA%\WritersBlock.Mcp\tokens\` |
| Other (Linux) | Plain file, `0600` | `~/.local/share/WritersBlock.Mcp/tokens/` (with a stderr warning — **not** OS-encrypted) |

`writersblock-mcp logout` removes the entry for the current authority. Revocation is enforced
server-side by the auth server regardless of what's stored locally.

## Build

```bash
dotnet build WritersBlock.Mcp.csproj
```

The output executable is named `writersblock-mcp` (e.g.
`bin/Debug/net10.0/writersblock-mcp` on macOS/Linux, `writersblock-mcp.exe`
on Windows). Run it via that binary, or via `dotnet <path-to>/writersblock-mcp.dll`.

## Packaging & release

`Scripts/publish-connector.sh` produces the shippable artifacts under `dist/`
(gitignored):

```bash
./Scripts/publish-connector.sh                 # both RIDs + the .mcpb bundle
./Scripts/publish-connector.sh --no-mcpb       # skip the bundle
./Scripts/publish-connector.sh --rids osx-arm64
```

It reads `<Version>` from the csproj and emits, for each RID:

- a **self-contained single-file** binary (`--self-contained -p:PublishSingleFile=true`), **no
  trimming** (reflection-heavy deps),
- an archive preserving the executable bit: `writersblock-mcp-<version>-osx-arm64.tar.gz` and
  `writersblock-mcp-<version>-win-x64.zip`,
- a `.sha256` checksum for each archive,

plus `writersblock-mcp-<version>-osx-arm64.mcpb` (Claude Desktop one-click bundle, built via
`npx @anthropic-ai/mcpb pack` from `Mcpb/manifest.json`).

> **Single-file gotcha (macOS):** native-library self-extraction and in-file compression are both
> disabled in the publish step. Either one makes the single-file bootstrap extract+reload the native
> shim libraries at startup, which corrupts the `System.Diagnostics.Process` pipe P/Invoke path and
> crashes the moment the connector shells out to `/usr/bin/security` (Keychain). With both off the
> binary is larger on disk (~84 MB) but is a single standalone file that compresses fine inside the
> shipped archive (~31 MB). See the comment in the publish script.

For the end-user install flow those artifacts feed, see **[INSTALL.md](INSTALL.md)**.

## Claude Desktop — `claude_desktop_config.json`

OAuth is the default; no token in the config. The first tool call opens a browser to sign in.

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

Local dev against the self-signed dev API + auth server:

```json
{
  "mcpServers": {
    "writersblock": {
      "command": "/absolute/path/to/writersblock-mcp",
      "args": [
        "--api-url", "https://localhost:6001",
        "--authority", "https://localhost:6010",
        "--insecure"
      ]
    }
  }
}
```

## Claude Code — `.mcp.json`

Place at the repo root (or your project root):

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

## Escape hatch: paste a token instead of logging in

For headless/scripted use, bypass OAuth entirely by supplying a bearer token — the connector uses
it verbatim and never opens a browser:

```json
{
  "mcpServers": {
    "writersblock": {
      "command": "/absolute/path/to/writersblock-mcp",
      "env": {
        "WRITERSBLOCK_API_URL": "https://localhost:6001",
        "WRITERSBLOCK_MCP_TOKEN": "<access-token>"
      }
    }
  }
}
```

Static tokens are **not** refreshed; when one expires, calls return a `401` error and you supply a
fresh one (or drop the setting to fall back to browser login).

## Smoke test with MCP Inspector

```bash
npx @modelcontextprotocol/inspector -- /absolute/path/to/writersblock-mcp \
  --api-url https://localhost:6001 --authority https://localhost:6010 --insecure
```

The Inspector's **Tools** tab lists the full catalog; pick a read-only tool (e.g. a `*_get_*`) and
run it. The first call opens a browser for sign-in; after that, dispatch, envelope unwrapping, and
silent refresh proceed without prompting.

## Notes

- The catalog is large (~1,255 tools). Consistent `entity_verb` names play well with Claude's
  deferred tool loading and VS Code's picker.
- Binary endpoints (DOCX, images, GeoJSON) round-trip as base64 with a 10 MB cap.
- `DELETE` tools carry `destructiveHint` so clients can gate them behind confirmation.
- Parallel tool calls that all need a login trigger exactly **one** browser window (login is gated
  by a semaphore).
