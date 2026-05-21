# AGENTS.md

## Scope

These instructions apply to `external/`.

This directory is a read-only reference workspace for external plugins and upstream projects that Xel's Combat AI inspects for IPC, shared data, API contracts, or reflected runtime behavior.

Compile-time dependencies are intentionally not kept here. They live as pinned git submodules under `third_party/` so builds are reproducible while this workspace can stay refreshable for discovery.

Root repository instructions apply only for how to use this subtree as reference material. Do not apply root C# style, formatting, validation, or refactor rules to external checkouts.

## Rules

- Do not edit, format, refactor, delete, or rename external plugin files.
- Do not stage or commit external plugin source, binaries, build outputs, repository metadata, or nested `.git` directories.
- Do not add external reference code to project files, package manifests, release artifacts, or build scripts. Compile-time dependencies belong under `third_party/`.
- Use these files only to inspect IPC contracts, payload shapes, public API behavior, reflected member names, and integration details.
- Prefer current external source evidence over memory when working with BossMod Reborn, Avarice, RotationSolver Reborn, or Dalamud integration behavior.
- When using external evidence in a change, summarize the contract that was verified rather than copying upstream code into this repository.
- If an integration requires an upstream code change, make that change in the upstream repository and then refresh the local checkout after the upstream branch includes it.

## Reference Map

- `Dalamud/` is the canonical source for framework behavior, service interfaces, lifecycle contracts, generated docs, and ImGui bindings. Start here for `Dalamud.Plugin`, `Dalamud.Plugin.Services`, `Dalamud.Game`, `Dalamud.Interface`, `Dalamud.IoC`, `Dalamud.Bindings.ImGui`, and framework update behavior.
- `SamplePlugin/` is the current official minimal plugin pattern. Use it for entrypoint, service injection, config, command, window, and dispose conventions before inventing a local pattern.
- `DalamudPackager/` explains build/package behavior, generated manifest expectations, and packager targets. Use it for SDK/package metadata questions.
- `DalamudPluginsD17/` is useful for public repo manifest and submission metadata examples. It is not a runtime API reference.
- `FFXIVClientStructs/` is the source for game structs, enums, native interop shapes, and unsafe client memory types exposed through Dalamud.
- `Lumina/` is the source for game data access and Excel sheet behavior used through `IDataManager`.
- `FfxivDatamining/` is raw game data reference material for IDs, sheets, statuses, actions, maps, and UI resources. Treat it as data evidence, not runtime API behavior.
- `Avarice/`, `RotationSolverReborn/`, and `VNavmesh/` are integration references for IPC names, strategy payloads, shared data, reflected members, and optional movement services.
- BossMod Reborn and ECommons are build dependencies under `third_party/`; use those submodules when verifying their APIs.

## Research Workflow

- Use `rg` against these checkouts before relying on memory for service names, event names, IPC names, payload shapes, reflected member names, or disposal ownership.
- For Xel's Combat AI code changes, prefer the API version the project actually builds against: local repository usage, installed dev assemblies under `$DALAMUD_HOME`, and NuGet package metadata can be more relevant than upstream `master`.
- If a checkout is missing or stale and the answer depends on current upstream behavior, run `external/fetch-sources.sh` before drawing conclusions.
- When upstream source and installed assemblies disagree, mention the version mismatch and validate with `dotnet build` before treating the upstream source as authoritative.

## XelsDevBridge Context

- XelsDevBridge can provide live local game/UI observations while researching external APIs, but it does not replace upstream source or installed assembly contracts.
- Use read-only DevBridge queries to compare current runtime state against external source evidence when that helps explain IPC payloads, addon names, object state, condition flags, or UI behavior.
- Do not edit nested external git checkouts to add local agent instructions. Keep durable guidance in this repository's `AGENTS.md` files and treat external checkouts as refreshable read-only references.

## Refreshing References

- `sources.json` is the source of truth for external reference repository URLs and tracked branches.
- Run `external/fetch-sources.sh` from anywhere in the repository to clone or refresh the ignored reference checkouts.
- Reference checkouts should track the latest remote branch heads, not pinned commits. Build dependency versions are tracked by `.gitmodules` and the gitlink commits under `third_party/`.
