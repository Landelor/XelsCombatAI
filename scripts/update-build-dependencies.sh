#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKSPACE_DIR="${XELS_WORKSPACE_DIR:-$ROOT/..}"
REFERENCES_DIR="${XCAI_REFERENCES_DIR:-$WORKSPACE_DIR/XelsCombatAIReferences}"
BMR_PATH="$REFERENCES_DIR/BossmodReborn"
ECOMMONS_PATH="$REFERENCES_DIR/ECommons"
BMR_REPO="FFXIV-CombatReborn/BossmodReborn"
BMR_URL="https://github.com/$BMR_REPO.git"
ECOMMONS_URL="https://github.com/NightmareXIV/ECommons.git"
ECOMMONS_BRANCH="master"

fail() {
  echo "error: $*" >&2
  exit 1
}

run() {
  printf '+'
  printf ' %q' "$@"
  printf '\n'
  "$@"
}

ensure_clone() {
  local path="$1"
  local name="$2"
  local url="$3"

  if [[ ! -d "$path/.git" && ! -f "$path/.git" ]]; then
    rm -rf "$path"
    mkdir -p "$(dirname "$path")"
    run git clone "$url" "$path"
  else
    run git -C "$path" remote set-url origin "$url"
  fi

  if [[ -n "$(git -C "$path" status --porcelain)" ]]; then
    fail "$name has local changes. Commit, stash, or discard them before updating the reference checkout."
  fi
}

latest_bossmod_release_tag() {
  if command -v gh >/dev/null 2>&1; then
    local tag
    if tag="$(gh release view --repo "$BMR_REPO" --json tagName --jq .tagName 2>/dev/null)" && [[ -n "$tag" ]]; then
      printf '%s\n' "$tag"
      return
    fi
  fi

  local python_bin=""
  if command -v python3 >/dev/null 2>&1; then
    python_bin="python3"
  elif command -v python >/dev/null 2>&1; then
    python_bin="python"
  fi

  if command -v curl >/dev/null 2>&1 && [[ -n "$python_bin" ]]; then
    local -a curl_args=(-fsSL)
    if [[ -n "${GITHUB_TOKEN:-}" ]]; then
      curl_args+=(-H "Authorization: Bearer $GITHUB_TOKEN")
    fi

    local tag
    if tag="$(
      curl "${curl_args[@]}" "https://api.github.com/repos/$BMR_REPO/releases/latest" \
        | "$python_bin" -c 'import json, sys; print(json.load(sys.stdin).get("tag_name", ""))' 2>/dev/null
    )" && [[ -n "$tag" ]]; then
      printf '%s\n' "$tag"
      return
    fi
  fi

  git ls-remote --tags --refs "$BMR_URL" 'refs/tags/[0-9]*' \
    | sed 's#.*refs/tags/##' \
    | sort -V \
    | tail -n 1
}

cd "$ROOT"

ensure_clone "$BMR_PATH" "BossMod Reborn" "$BMR_URL"
ensure_clone "$ECOMMONS_PATH" "ECommons" "$ECOMMONS_URL"

bmr_tag="$(latest_bossmod_release_tag)"
[[ -n "$bmr_tag" ]] || fail "could not resolve the latest BossMod Reborn release tag."

echo "BossMod Reborn latest release: $bmr_tag"
run git -C "$BMR_PATH" fetch --tags origin
bmr_commit="$(git -C "$BMR_PATH" rev-list -n 1 "$bmr_tag")"
[[ -n "$bmr_commit" ]] || fail "could not resolve BossMod Reborn tag '$bmr_tag'."
run git -C "$BMR_PATH" checkout --detach "$bmr_commit"
run git -C "$BMR_PATH" submodule update --init --recursive

echo "ECommons latest branch: origin/$ECOMMONS_BRANCH"
run git -C "$ECOMMONS_PATH" fetch origin "$ECOMMONS_BRANCH"
ecommons_commit="$(git -C "$ECOMMONS_PATH" rev-parse FETCH_HEAD)"
[[ -n "$ecommons_commit" ]] || fail "could not resolve ECommons branch '$ECOMMONS_BRANCH'."
run git -C "$ECOMMONS_PATH" checkout --detach "$ecommons_commit"

echo
echo "Reference checkouts refreshed in $REFERENCES_DIR:"
printf '  BossMod Reborn: %s\n' "$(git -C "$BMR_PATH" rev-parse HEAD)"
printf '  ECommons: %s\n' "$(git -C "$ECOMMONS_PATH" rev-parse HEAD)"
