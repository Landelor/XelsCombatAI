# AGENTS.md

## Scope

These instructions apply to `external/`.

This directory is a read-only reference workspace for external plugins that Xel's Combat AI integrates with through IPC, shared data, or reflected runtime behavior.

## Rules

- Do not edit, format, refactor, delete, or rename external plugin files.
- Do not stage or commit external plugin source, binaries, build outputs, repository metadata, or nested `.git` directories.
- Do not add external plugin code to project files, package manifests, release artifacts, or build scripts.
- Use these files only to inspect IPC contracts, payload shapes, public API behavior, reflected member names, and integration details.
- If an integration requires an upstream code change, make that change in the upstream repository and then refresh the local checkout after the upstream branch includes it.

## Refreshing References

- `sources.json` is the source of truth for external repository URLs and tracked branches.
- Run `external/fetch-sources.sh` from anywhere in the repository to clone or refresh the ignored reference checkouts.
- Reference checkouts should track the latest remote branch heads, not pinned commits.
