#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN_PROJECT="$ROOT/XelsCombatAI/XelsCombatAI.csproj"
TOOL_PROJECT="$ROOT/tools/FightReview/FightReview.csproj"
TOOL_TEST_PROJECT="$ROOT/tools/FightReview.Tests/FightReview.Tests.csproj"
PACKAGE_SCRIPT="$ROOT/scripts/package-release.sh"

RUN_TOOLS=1
RUN_TOOL_TESTS=1
RUN_PLUGIN=1
RUN_FORMAT=0
RUN_PACKAGE=0

usage() {
  cat <<EOF
Usage: scripts/test-and-build.sh [options]

Options:
  --skip-tools       Build only the Dalamud plugin.
  --skip-plugin      Build and test only FightReview tooling.
  --skip-tool-tests  Build FightReview but do not run FightReview.Tests.
  --format           Verify C# formatting.
  --package          Build the release zip with scripts/package-release.sh.
  -h, --help         Show this help text.
EOF
}

fail() {
  echo "error: $*" >&2
  exit 1
}

run() {
  printf '\n+'
  printf ' %q' "$@"
  printf '\n'
  "$@"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-tools)
      RUN_TOOLS=0
      RUN_TOOL_TESTS=0
      ;;
    --skip-plugin)
      RUN_PLUGIN=0
      ;;
    --skip-tool-tests)
      RUN_TOOL_TESTS=0
      ;;
    --format)
      RUN_FORMAT=1
      ;;
    --package)
      RUN_PACKAGE=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      fail "unknown option: $1"
      ;;
  esac
  shift
done

if [[ "$RUN_PLUGIN" -eq 0 && "$RUN_TOOLS" -eq 0 ]]; then
  fail "--skip-plugin and --skip-tools cannot be used together."
fi

if [[ "$(uname -s)" == "Linux" ]]; then
  DEFAULT_DALAMUD_HOME="$HOME/.xlcore/dalamud/Hooks/dev"
  if [[ -z "${DALAMUD_HOME:-}" && -d "$DEFAULT_DALAMUD_HOME" ]]; then
    export DALAMUD_HOME="$DEFAULT_DALAMUD_HOME"
  fi

  if [[ -z "${DALAMUD_HOME:-}" ]]; then
    fail "DALAMUD_HOME is required on Linux. Expected default path '$DEFAULT_DALAMUD_HOME', or set DALAMUD_HOME explicitly."
  fi

  [[ -d "$DALAMUD_HOME" ]] || fail "DALAMUD_HOME does not exist: '$DALAMUD_HOME'."
fi

NEEDS_PLUGIN_PROJECT="$RUN_PLUGIN"
if [[ "$RUN_TOOLS" -eq 1 && "$RUN_TOOL_TESTS" -eq 1 ]]; then
  NEEDS_PLUGIN_PROJECT=1
fi

if [[ "$NEEDS_PLUGIN_PROJECT" -eq 1 ]]; then
  ECOMMONS_PROJECT="$ROOT/external/ECommons/ECommons/ECommons.csproj"
  [[ -f "$ECOMMONS_PROJECT" ]] || fail "ECommons was not found at '$ECOMMONS_PROJECT'. Run external/fetch-sources.sh to clone or refresh external references."
fi

if [[ "$RUN_TOOLS" -eq 1 ]]; then
  BMR_PROJECT="$ROOT/external/BossmodReborn/BossMod/BossModReborn.csproj"
  [[ -f "$BMR_PROJECT" ]] || fail "BossMod Reborn checkout was not found at '$BMR_PROJECT'. Run external/fetch-sources.sh or use --skip-tools."

  if [[ "$RUN_TOOL_TESTS" -eq 1 ]]; then
    DEFAULT_FFXIV_GAME_PATH="$HOME/Games/steam/debian-installation/steamapps/common/FINAL FANTASY XIV Online"
    if [[ -z "${FFXIV_GAME_PATH:-}" && -d "$DEFAULT_FFXIV_GAME_PATH" ]]; then
      export FFXIV_GAME_PATH="$DEFAULT_FFXIV_GAME_PATH"
    fi

    if [[ -z "${FFXIV_GAME_PATH:-}" ]]; then
      fail "FFXIV_GAME_PATH is required for FightReview.Tests. Expected default path '$DEFAULT_FFXIV_GAME_PATH', or use --skip-tool-tests."
    fi

    [[ -d "$FFXIV_GAME_PATH" ]] || fail "FFXIV_GAME_PATH does not exist: '$FFXIV_GAME_PATH'."
  fi
fi

if [[ "$RUN_PLUGIN" -eq 1 ]]; then
  run dotnet restore "$PLUGIN_PROJECT" -p:EnableWindowsTargeting=true
  run dotnet build "$PLUGIN_PROJECT" -c Release -p:EnableWindowsTargeting=true --no-restore
fi

if [[ "$RUN_TOOLS" -eq 1 ]]; then
  run dotnet restore "$TOOL_PROJECT" -p:EnableWindowsTargeting=true
  run dotnet build "$TOOL_PROJECT" -c Release -p:EnableWindowsTargeting=true --no-restore

  if [[ "$RUN_TOOL_TESTS" -eq 1 ]]; then
    run dotnet restore "$TOOL_TEST_PROJECT" -p:EnableWindowsTargeting=true
    run dotnet run --project "$TOOL_TEST_PROJECT" -c Release -p:EnableWindowsTargeting=true --no-restore
  fi
fi

if [[ "$RUN_FORMAT" -eq 1 ]]; then
  if [[ "$RUN_PLUGIN" -eq 1 ]]; then
    run dotnet format "$PLUGIN_PROJECT" --verify-no-changes --no-restore
  fi

  if [[ "$RUN_TOOLS" -eq 1 ]]; then
    run dotnet format "$TOOL_PROJECT" --verify-no-changes --no-restore
  fi
fi

if [[ "$RUN_PACKAGE" -eq 1 ]]; then
  run "$PACKAGE_SCRIPT"
fi

printf '\nValidation completed.\n'
