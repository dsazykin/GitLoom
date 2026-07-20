#!/usr/bin/env bash
# =============================================================================
# The automated Client-closure gate (ADR-0001 payoff) — step 2h.
# =============================================================================
# Publishes an exe HEAD and asserts its dependency closure is FREE of the agent
# platform. This is the AUTOMATED version of the manual `.deps.json` proof: the
# whole point of the two-head split (Mainguard.Client.App / Mainguard.Pro.App)
# is that the plain Git-client head references Mainguard.App.Shell ONLY, so its
# published closure physically cannot contain the agent platform. This script
# proves it every CI run — and you can run it LOCALLY the same way CI does.
#
# It inspects the published `<head>.deps.json` (the authoritative runtime closure
# manifest) AND the publish output dir for any of these assemblies:
#     Mainguard.Agents, Mainguard.Agents.UI, Mainguard.Protos,
#     Docker.DotNet, Porta.Pty, Grpc
#
# Usage:
#   build/ci/verify-client-closure.sh                      # gate the Client head (must be FREE)
#   build/ci/verify-client-closure.sh --head Mainguard.Pro.App --mode present
#                                                          # positive control (must CONTAIN)
#   build/ci/verify-client-closure.sh --publish-dir <dir>  # inspect an existing publish dir
#
# Portable: uses `dotnet` if present (CI / Linux), else `dotnet.exe` (local WSL).
# Override with DOTNET_CLI=<path>. Publishes to a gitignored `.../publish/` dir.
#
# Exit: 0 = assertion held, 1 = assertion FAILED (the gate), 2 = usage/tooling error.
set -euo pipefail

# The agent-platform assemblies the Client head's closure must never contain.
FORBIDDEN=(
  "Mainguard.Agents"
  "Mainguard.Agents.UI"
  "Mainguard.Protos"
  "Docker.DotNet"
  "Porta.Pty"
  "Grpc"
)

HEAD="Mainguard.Client.App"
MODE="absent"        # absent = the real gate (must be FREE); present = positive control (must CONTAIN)
PUBLISH_DIR=""       # supply to skip publishing and inspect an existing dir

usage() { sed -n '2,33p' "${BASH_SOURCE[0]}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --head)        HEAD="${2:?--head needs a project name}"; shift 2 ;;
    --mode)        MODE="${2:?--mode needs absent|present}"; shift 2 ;;
    --publish-dir) PUBLISH_DIR="${2:?--publish-dir needs a path}"; shift 2 ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$MODE" in absent|present) ;; *) echo "::error::--mode must be 'absent' or 'present'" >&2; exit 2 ;; esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

# Pick the .NET CLI. CI runners have `dotnet`; a local WSL shell usually only has `dotnet.exe`.
DOTNET="${DOTNET_CLI:-}"
if [ -z "$DOTNET" ]; then
  if command -v dotnet    >/dev/null 2>&1; then DOTNET="dotnet"
  elif command -v dotnet.exe >/dev/null 2>&1; then DOTNET="dotnet.exe"
  else echo "::error::neither 'dotnet' nor 'dotnet.exe' is on PATH (set DOTNET_CLI)" >&2; exit 2; fi
fi

if [ -z "$PUBLISH_DIR" ]; then
  PROJ="${HEAD}/${HEAD}.csproj"
  [ -f "$PROJ" ] || { echo "::error::project not found: $PROJ (run from the repo, pass --head)" >&2; exit 2; }
  # The 'publish' leaf is matched by .gitignore's [Pp]ublish/, so nothing here is ever tracked.
  PUBLISH_DIR="artifacts/closure-check/${HEAD}/publish"
  echo "==> Publishing ${HEAD} (framework-dependent, Release) via ${DOTNET} …"
  rm -rf "artifacts/closure-check/${HEAD}"
  LOG="$(mktemp)"
  if ! "$DOTNET" publish "$PROJ" -c Release -o "$PUBLISH_DIR" -v minimal --nologo >"$LOG" 2>&1; then
    echo "::error::dotnet publish failed for ${PROJ}:" >&2; cat "$LOG" >&2; rm -f "$LOG"; exit 2
  fi
  rm -f "$LOG"
fi

# The head's OWN runtime closure manifest. Pick it by name: a Pro publish co-locates the elevated
# helper's .deps.json too, so "whichever sorts first" would inspect the wrong one.
DEPS="$PUBLISH_DIR/$HEAD.deps.json"
if [ ! -f "$DEPS" ]; then
  DEPS="$(ls "$PUBLISH_DIR"/*.deps.json 2>/dev/null | head -n1 || true)"
fi
[ -n "$DEPS" ] && [ -f "$DEPS" ] || { echo "::error::no .deps.json under ${PUBLISH_DIR}" >&2; exit 2; }

echo "==> Head        : ${HEAD}"
echo "==> Publish dir : ${PUBLISH_DIR}"
echo "==> Closure     : ${DEPS}"
echo "==> Mode        : ${MODE} (absent = must be FREE of the agent platform; present = positive control)"
echo "----------------------------------------------------------------------"

HITS=()
for tok in "${FORBIDDEN[@]}"; do
  found=""
  # 1) the authoritative closure manifest (library keys + runtime file entries)
  grep -Fq "$tok" "$DEPS" && found="deps.json"
  # 2) belt & suspenders: an actual assembly of that name copied to the publish dir
  if ls "$PUBLISH_DIR"/"$tok"*.dll >/dev/null 2>&1; then
    found="${found:+$found + }publish dir"
  fi
  if [ -n "$found" ]; then
    HITS+=("$tok  (via $found)")
    printf '  [FOUND]   %-20s via %s\n' "$tok" "$found"
  else
    printf '  [absent]  %s\n' "$tok"
  fi
done

echo "----------------------------------------------------------------------"

if [ "$MODE" = "present" ]; then
  # Positive control: prove the gate is not vacuous — the Pro head SHOULD carry the platform.
  if [ "${#HITS[@]}" -gt 0 ]; then
    echo "POSITIVE CONTROL PASS: ${HEAD} carries the agent platform (as expected)."
    exit 0
  fi
  echo "::error::POSITIVE CONTROL FAILED: ${HEAD} had NONE of the agent-platform assemblies — the gate's matcher is broken/vacuous." >&2
  exit 1
fi

# Default 'absent' mode: the real gate.
if [ "${#HITS[@]}" -gt 0 ]; then
  echo "::error::CLOSURE GATE FAILED: ${HEAD} pulled the agent platform into its closure — the Client head must be free of it." >&2
  exit 1
fi
echo "CLOSURE GATE PASS: ${HEAD} closure is FREE of the agent platform."
exit 0
