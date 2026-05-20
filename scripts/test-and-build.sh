#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN_PROJECT="$ROOT/XelsCombatAI/XelsCombatAI.csproj"
TOOL_PROJECT="$ROOT/tools/FightReview/FightReview.csproj"
TOOL_TEST_PROJECT="$ROOT/tools/FightReview.Tests/FightReview.Tests.csproj"
WORKFLOWS_DIR="${XELS_DALAMUD_WORKFLOWS_DIR:-$ROOT/../XelsDalamud.Workflows}"
PACKAGE_SCRIPT="$WORKFLOWS_DIR/scripts/package-plugin.py"
PACKAGE_OUT="$ROOT/artifacts"

RUN_TOOLS=1
RUN_TOOL_TESTS=1
RUN_PLUGIN=1
RUN_FORMAT=0
RUN_PACKAGE=0
TOOL_TEST_ARGS=()

usage() {
  cat <<EOF
Usage: scripts/test-and-build.sh [options]

Options:
  --skip-tools       Build only the Dalamud plugin.
  --skip-plugin      Build and test only FightReview tooling.
  --skip-tool-tests  Build FightReview but do not run FightReview.Tests.
  --format           Verify C# formatting.
  --package          Build the release zip with XelsDalamud.Workflows/scripts/package-plugin.py.
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

if [[ "$RUN_PACKAGE" -eq 1 && "$RUN_PLUGIN" -eq 0 ]]; then
  fail "--package requires the plugin build. Do not combine --package with --skip-plugin."
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
  ECOMMONS_PROJECT="$ROOT/third_party/ECommons/ECommons/ECommons.csproj"
  [[ -f "$ECOMMONS_PROJECT" ]] || fail "ECommons was not found at '$ECOMMONS_PROJECT'. Run git submodule update --init --recursive."
fi

if [[ "$RUN_TOOLS" -eq 1 ]]; then
  BMR_PROJECT="$ROOT/third_party/BossmodReborn/BossMod/BossModReborn.csproj"
  [[ -f "$BMR_PROJECT" ]] || fail "BossMod Reborn checkout was not found at '$BMR_PROJECT'. Run git submodule update --init --recursive or use --skip-tools."

  if [[ "$RUN_TOOL_TESTS" -eq 1 ]]; then
    DEFAULT_FFXIV_GAME_PATH="$HOME/Games/steam/debian-installation/steamapps/common/FINAL FANTASY XIV Online"
    if [[ -z "${FFXIV_GAME_PATH:-}" && -d "$DEFAULT_FFXIV_GAME_PATH" ]]; then
      export FFXIV_GAME_PATH="$DEFAULT_FFXIV_GAME_PATH"
    fi

    if [[ -z "${FFXIV_GAME_PATH:-}" ]]; then
      TOOL_TEST_ARGS+=(--skip-game-data)
    else
      [[ -d "$FFXIV_GAME_PATH" ]] || fail "FFXIV_GAME_PATH does not exist: '$FFXIV_GAME_PATH'."
    fi
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
    run dotnet run --project "$TOOL_TEST_PROJECT" -c Release -p:EnableWindowsTargeting=true --no-restore -- "${TOOL_TEST_ARGS[@]}"
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
  [[ -f "$PACKAGE_SCRIPT" ]] || fail "Reusable package script was not found at '$PACKAGE_SCRIPT'. Clone XelsDalamud.Workflows beside this repo or set XELS_DALAMUD_WORKFLOWS_DIR."
  rm -rf "$PACKAGE_OUT"
  run python "$PACKAGE_SCRIPT" \
    --project "$PLUGIN_PROJECT" \
    --configuration Release \
    --internal-name XelsCombatAI \
    --output-dir "$PACKAGE_OUT" \
    --no-build
fi

printf '\nValidation completed.\n'
