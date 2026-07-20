#!/usr/bin/env bash
#
# publish-connector.sh — build, package, and bundle the WritersBlock MCP connector.
#
# Produces, under dist/, for each target RID:
#   - a self-contained single-file executable (no .NET runtime install required),
#   - a compressed archive preserving the executable bit (.tar.gz for macOS, .zip for Windows),
#   - a SHA-256 checksum file,
# plus an MCPB one-click bundle (.mcpb) for Claude Desktop built from the osx-arm64 binary.
#
# Trimming is deliberately OFF: the connector is reflection-heavy (System.Text.Json schema
# export, Duende OIDC, the MCP SDK) and trimming silently breaks those paths.
#
# Runnable locally and in CI. Requires: dotnet 10 SDK, tar, zip, shasum, and (for the .mcpb)
# npx with network access to fetch @anthropic-ai/mcpb.
#
# Usage:
#   ./Scripts/publish-connector.sh                 # build everything (both RIDs + .mcpb)
#   ./Scripts/publish-connector.sh --no-mcpb       # skip the .mcpb bundle step
#   ./Scripts/publish-connector.sh --rids osx-arm64
#
set -euo pipefail

# --- locate paths (script lives in Scripts/ at the repo root) -------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CSPROJ="$PROJECT_DIR/WritersBlock.Mcp.csproj"
DIST_DIR="$PROJECT_DIR/dist"
MCPB_DIR="$PROJECT_DIR/Mcpb"

# --- options -------------------------------------------------------------------------------------
RIDS=("osx-arm64" "win-x64")
BUILD_MCPB=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-mcpb) BUILD_MCPB=0; shift ;;
    --rids)    IFS=' ' read -r -a RIDS <<< "$2"; shift 2 ;;
    -h|--help)
      grep -E '^#( |$)' "$0" | sed -E 's/^# ?//'
      exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

# --- resolve version from the csproj (single source of truth) ------------------------------------
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -1)"
if [[ -z "$VERSION" ]]; then
  echo "ERROR: could not read <Version> from $CSPROJ" >&2
  exit 1
fi
echo "==> WritersBlock MCP connector v$VERSION"

rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

# Map a .NET RID to the executable file name it emits.
exe_name_for() {
  case "$1" in
    win-*) echo "writersblock-mcp.exe" ;;
    *)     echo "writersblock-mcp" ;;
  esac
}

# --- publish + archive each RID ------------------------------------------------------------------
for RID in "${RIDS[@]}"; do
  echo
  echo "==> Publishing $RID (self-contained, single-file, no trimming) …"
  PUB_DIR="$PROJECT_DIR/bin/publish/$RID"
  rm -rf "$PUB_DIR"

  # Single-file, self-contained, NO trimming (reflection-heavy deps).
  #
  # Native-library self-extraction and in-file compression are BOTH left OFF on purpose:
  # either one forces the single-file bootstrap to extract+reload the native shim libraries
  # (libSystem.Native, etc.) at startup, which corrupts the P/Invoke path that
  # System.Diagnostics.Process uses to read child-process pipes. That surfaces as a fatal
  # AccessViolationException in Interop.Sys.Fcntl.GetIsNonBlocking the moment the connector
  # shells out to `/usr/bin/security` (Keychain) or `xdg-open`/`open` (browser launch).
  # With both off, native libs are embedded and loaded in place — the emitted binary is a
  # single standalone executable (plus non-runtime .pdb/.xml), and it compresses well inside
  # the .tar.gz/.zip/.mcpb we ship anyway.
  dotnet publish "$CSPROJ" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=false \
    -p:EnableCompressionInSingleFile=false \
    -p:PublishTrimmed=false \
    -o "$PUB_DIR" \
    --nologo -v minimal

  EXE="$(exe_name_for "$RID")"
  if [[ ! -f "$PUB_DIR/$EXE" ]]; then
    echo "ERROR: expected published binary '$EXE' not found in $PUB_DIR" >&2
    exit 1
  fi
  chmod +x "$PUB_DIR/$EXE" 2>/dev/null || true

  BASENAME="writersblock-mcp-$VERSION-$RID"
  case "$RID" in
    win-*)
      # zip preserves nothing executable-wise on Windows, which is fine (.exe is intrinsic).
      ARCHIVE="$DIST_DIR/$BASENAME.zip"
      ( cd "$PUB_DIR" && zip -q -X "$ARCHIVE" "$EXE" )
      ;;
    *)
      # tar via a top-level dir; -p (via GNU/BSD default) preserves the +x bit we just set.
      ARCHIVE="$DIST_DIR/$BASENAME.tar.gz"
      ( cd "$PROJECT_DIR/bin/publish" && tar -czf "$ARCHIVE" -C "$RID" "$EXE" )
      ;;
  esac

  # Per-artifact checksum, computed with basenames so the file is portable.
  ( cd "$DIST_DIR" && shasum -a 256 "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256" )

  SIZE="$(du -h "$ARCHIVE" | cut -f1 | tr -d ' ')"
  echo "    -> $(basename "$ARCHIVE")  ($SIZE)"
done

# --- MCPB bundle (Claude Desktop one-click install) ----------------------------------------------
# Bundles the osx-arm64 self-contained binary. The manifest source of truth is Mcpb/manifest.json;
# we stage it next to the binary under server/ and pack. Requires the osx-arm64 RID to have been
# published above.
if [[ "$BUILD_MCPB" -eq 1 ]]; then
  OSX_PUB="$PROJECT_DIR/bin/publish/osx-arm64"
  if [[ ! -f "$OSX_PUB/writersblock-mcp" ]]; then
    echo
    echo "WARNING: skipping .mcpb — osx-arm64 was not published this run (use --rids to include it)."
  elif [[ ! -f "$MCPB_DIR/manifest.json" ]]; then
    echo
    echo "WARNING: skipping .mcpb — $MCPB_DIR/manifest.json not found."
  else
    echo
    echo "==> Building MCPB bundle (osx-arm64) …"
    STAGE="$PROJECT_DIR/bin/mcpb-stage"
    rm -rf "$STAGE"
    mkdir -p "$STAGE/server"

    # Manifest at bundle root; binary under server/ so entry_point = server/writersblock-mcp.
    cp "$MCPB_DIR/manifest.json" "$STAGE/manifest.json"
    cp "$OSX_PUB/writersblock-mcp" "$STAGE/server/writersblock-mcp"
    chmod +x "$STAGE/server/writersblock-mcp"

    # Stamp the version from the csproj into the staged manifest so it always matches the build.
    node -e '
      const fs = require("fs");
      const p = process.argv[1], v = process.argv[2];
      const m = JSON.parse(fs.readFileSync(p, "utf8"));
      m.version = v;
      fs.writeFileSync(p, JSON.stringify(m, null, 2) + "\n");
    ' "$STAGE/manifest.json" "$VERSION"

    MCPB_OUT="$DIST_DIR/writersblock-mcp-$VERSION-osx-arm64.mcpb"
    npx --yes @anthropic-ai/mcpb pack "$STAGE" "$MCPB_OUT"

    ( cd "$DIST_DIR" && shasum -a 256 "$(basename "$MCPB_OUT")" > "$(basename "$MCPB_OUT").sha256" )

    MCPB_SIZE="$(du -h "$MCPB_OUT" | cut -f1 | tr -d ' ')"
    echo "    -> $(basename "$MCPB_OUT")  ($MCPB_SIZE)"
    rm -rf "$STAGE"
  fi
fi

echo
echo "==> Done. Artifacts in $DIST_DIR:"
ls -1 "$DIST_DIR"
