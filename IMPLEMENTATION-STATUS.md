# Implementation Status

## Current State

The application is complete at the code and automated-test level pending human QA. It implements the .NET 10/Avalonia desktop UI, Steam lobby and P2P transport, strict protocol validation, card scanning, Scryfall set import, pack generation, disk-backed image transfer, authoritative drafting, force-ready, disconnect retention and reconnection, completion, lobby reopening, clipboard output, and fallback Cockatrice XML export.

Verification completed on September 25, 2026:

- 158 tests passed on .NET SDK 10.0.401.
- Release build completed with zero warnings.
- Self-contained Windows x64 publish completed at `artifacts/publish/win-x64-net10/`.
- Publish contains `DraftSimulator.App.exe`, `steam_api64.dll`, and `steam_appid.txt` containing AppID 480.

## Remaining Blockers

1. **Real Steam validation is required.** Automated tests use fake transports. Run the Windows publish with two Steam accounts/machines to verify AppID 480 lobby search, relay connectivity, invitations, asset transfer, disconnects, and direct reconnect.
2. **Windows UI smoke testing is required.** XAML compiles, but the complete UI has not been interactively exercised on Windows in this environment. Exercise Scryfall import, existing-folder reuse, forced replacement against the live API, the live countdown, hover preview/optional zoom, and constrained-window scrolling.

Reconnect now persists the host, lobby, session, room, and player identity; connects directly to the retained host without rejoining a closed lobby; verifies the retained SteamID and session; reuses complete or partial cached assets; and applies a fresh personalized snapshot or completion result only after asset verification. Integration tests cover latest-state restoration, cache-hit transfer avoidance, stale-session rejection, completion reconnect, reconnect-state cleanup, and graceful host closure.

The all-ready lobby countdown displays each remaining second to hosts and clients. Draft cards have a larger side preview and a per-client, opt-in hover zoom overlay; clicking still selects the card. Lobby and draft side panels scroll when their available height is constrained.

## Offline Debug Mode

Debug builds can exercise the real multi-process UI and session flow on one machine without Steam. The transport uses named pipes, shared room-code discovery, stable profile identities, and isolated profile data. Build a dedicated, self-contained `DraftSimulator.Debug.exe` that defaults to offline host profile with `publish-offline-debug.ps1`.

```text
powershell -ExecutionPolicy Bypass -File .\publish-offline-debug.ps1
.\artifacts\publish\offline-debug-countdown-zoom-win-x64\DraftSimulator.Debug.exe
.\artifacts\publish\offline-debug-countdown-zoom-win-x64\DraftSimulator.Debug.exe --profile client1
```

Use a different profile name for every concurrently running process. One process chooses **Host a draft** and the other joins with its displayed room code. Release builds reject `--offline`, and real Steam QA remains required.

## Intentional Deferrals

- Optional `data.csv` enrichment is not defined. Export currently produces valid fallback Cockatrice v4 XML and warns when `data.csv` is present.
- AppID 480 is for private testing only. Friends must launch the application before accepting Steam invitations. A dedicated AppID should replace it before broader distribution.

## Environment Notes

- `global.json` requires .NET SDK `10.0.401`.
- The SDK used in this session was installed temporarily at `/tmp/opencode/dotnet10`; a future environment should install .NET 10 normally.
- The Git repository has no commits yet and all project files are currently untracked.
