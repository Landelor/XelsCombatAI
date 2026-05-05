#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST="${SCRIPT_DIR}/sources.json"

if ! command -v git >/dev/null 2>&1; then
  echo "git is required" >&2
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required to read ${MANIFEST}" >&2
  exit 1
fi

python3 - "${MANIFEST}" <<'PY' | while IFS=$'\t' read -r name url branch; do
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    sources = json.load(handle)

for name, source in sources.items():
    print(f"{name}\t{source['url']}\t{source['branch']}")
PY
  target="${SCRIPT_DIR}/${name}"
  echo "==> ${name} latest origin/${branch}"

  if [ ! -d "${target}/.git" ]; then
    rm -rf "${target}"
    mkdir -p "${target}"
    git -C "${target}" init -q
    git -C "${target}" remote add origin "${url}"
  else
    git -C "${target}" remote set-url origin "${url}"
  fi

  git -C "${target}" fetch --depth=1 origin "${branch}"
  git -C "${target}" checkout -B "${branch}" -q FETCH_HEAD

  if [ -f "${target}/.gitmodules" ]; then
    git -C "${target}" submodule update --init --recursive --depth=1
  fi
done
