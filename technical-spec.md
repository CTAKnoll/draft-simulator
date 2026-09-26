# Draft Simulator: Technical Specification

## 1. Document Status

This document is the implementation contract for the first working version of Draft Simulator. It supplements `high-level-design.md` and resolves the major platform, networking, storage, packaging, and application-architecture choices.

An implementation should follow this document without selecting a different framework, networking model, or state-management architecture. The application must produce a valid fallback Cockatrice export without `data.csv`; the exact optional `data.csv` enrichment schema remains intentionally deferred and is documented in Section 18.

## 2. Fixed Technical Decisions

- Language: C#.
- Runtime: current LTS .NET, targeting .NET 10 for the initial implementation.
- UI framework: Avalonia UI.
- UI architecture: MVVM using `CommunityToolkit.Mvvm`.
- Initial operating system: 64-bit Windows.
- Secondary future targets: macOS and other desktop operating systems.
- Distribution during private development: self-contained portable ZIP using Steam AppID 480 (`Spacewar`).
- Networking: Steam lobbies plus `ISteamNetworkingSockets` peer-to-peer connections.
- Steam C# binding: Steamworks.NET, pinned to a known release and hidden behind a project-owned adapter.
- Authority model: the host is authoritative for the lobby, pack generation, selections, collections, and every game-state transition.
- Serialization: strict `System.Text.Json` DTOs for control messages and a fixed binary envelope for asset chunks.
- Image processing: SkiaSharp.
- Transferred image format: WebP, maximum long edge 1200 pixels, quality 80, preserving aspect ratio and never upscaling.
- Asset storage: disk-backed session directories with bounded in-memory caches.
- Persistence: no draft history or result persistence; only configuration, reconnect information, and active-session assets are persisted locally.

Unity, Python, GameNetworkingSockets standalone, WebSockets, a custom relay, and a central matchmaking service are not part of the MVP architecture.

## 3. AppID 480 Constraints

AppID 480 is permitted by this design only as a temporary friends-and-family development mechanism. Valve documents it as an example/development AppID, not as a permanent product distribution method.

The implementation must therefore:

- Read the AppID from one MSBuild property rather than scattering `480` through the code.
- Put `steam_appid.txt` beside the executable in development and friends-test builds.
- Keep all game protocol logic independent of AppID 480.
- Tag lobbies with a unique Draft Simulator marker and protocol version to avoid unrelated Spacewar lobbies.
- Use exact lobby metadata filters and never show unfiltered AppID 480 lobbies.
- Make replacement with a dedicated Steam AppID a configuration and packaging change, not a networking rewrite.

Steam invitations can notify an already-running AppID 480 process. Steam cannot reliably launch the locally distributed Draft Simulator executable through `steam://run/480`; users must launch Draft Simulator before accepting an invitation. A dedicated AppID distributed through Steam removes this limitation.

## 4. Repository and Solution Structure

Use the following structure:

```text
draft-simulator/
  high-level-design.md
  technical-spec.md
  DraftSimulator.sln
  Directory.Build.props
  src/
    DraftSimulator.App/
    DraftSimulator.Core/
    DraftSimulator.Protocol/
    DraftSimulator.Infrastructure/
  tests/
    DraftSimulator.Core.Tests/
    DraftSimulator.Protocol.Tests/
    DraftSimulator.Infrastructure.Tests/
    DraftSimulator.Integration.Tests/
  third_party/
    Steamworks.NET/
```

Project responsibilities:

- `DraftSimulator.App`: Avalonia application, views, view models, navigation, dependency registration, and application startup/shutdown.
- `DraftSimulator.Core`: domain models, validation, pack generation, draft state machine, host authority rules, and interfaces for infrastructure dependencies. It must not reference Avalonia, Steamworks, filesystem implementations, or SkiaSharp.
- `DraftSimulator.Protocol`: wire DTOs, message codes, binary asset framing, serializers, protocol-version checks, and protocol validation.
- `DraftSimulator.Infrastructure`: Steamworks adapter, Steam transport worker, filesystem scanning, INI and JSON persistence, image transformation, session cache, logging, and Cockatrice export implementation.
- Test projects: tests scoped to the corresponding production project, with end-to-end state tests using an in-memory fake transport.

Steamworks.NET should be vendored or referenced at a pinned commit/release rather than tracking a moving branch. Preserve all required third-party license notices. Copy Valve's `steam_api64.dll` into publish output.

## 5. Runtime Architecture

### 5.1 Shared Executable

Host Mode and Client Mode run from the same executable. The start screen offers:

- Host.
- Join by room code.
- Reconnect, when valid reconnect state exists.

The Host and Join flows include a player-name field. Prefill it from `client-state.json` when available. The host must enter a valid name before creating a lobby, and a new client must enter a valid name before joining. The lobby screen continues to allow name changes until the draft starts.

Steam must be running. If Steam initialization fails, show a local predefined error explaining that Steam must be started, then remain on the start screen or exit cleanly.

### 5.2 Single-Writer Session State

All session-state mutations must be serialized through one `SessionCoordinator` command queue. This avoids locks and races between the UI, Steam callbacks, countdown timers, and network messages.

Sources of commands include:

- Local UI intents.
- Parsed transport messages.
- Steam connection events.
- Countdown and timeout events.
- Image preparation and transfer completion events.

The coordinator processes commands one at a time and publishes immutable view snapshots. Avalonia view models consume those snapshots on the UI dispatcher. Views must not mutate domain objects directly.

### 5.3 Steam Worker

Steam operations run through a dedicated `SteamTransport` worker. The worker owns Steam callbacks and connection polling.

The worker loop must:

- Drain outbound transport commands.
- Call `SteamAPI.RunCallbacks()`.
- Poll connection-state changes.
- Receive all available Steam networking messages.
- Enqueue parsed transport events for `SessionCoordinator`.
- Run approximately 30 times per second while the application is active.

Do not execute Avalonia UI operations on the Steam worker. Do not parse or transform images on the Steam worker.

### 5.4 Offline Debug Transport

Debug builds may run without Steam by using `--offline --profile <name>`. This development-only mode uses machine-local named pipes and must not open TCP or UDP ports.

- Each application process uses a distinct profile name and stable locally persisted debug peer ID.
- Profile-specific configuration, reconnect state, logs, and session assets are isolated from normal Steam data and from other offline processes.
- Hosts publish room-code lobby records into a shared machine-local debug registry. Clients on the same machine use the normal room-code Join flow.
- The normal `SessionCoordinator`, wire protocol, authority checks, asset transfer, drafting, disconnect, and reconnect behavior remain in use.
- Steam invitations are unavailable in offline mode.
- Release builds reject the offline startup option. Offline mode does not replace any required real-Steam acceptance testing.

## 6. Local Files and Configuration

Use `%LocalAppData%\DraftSimulator` as the application data root.

```text
DraftSimulator/
  host.ini
  client-state.json
  Logs/
  Sessions/
    <session-id>/
      manifest.json
      assets/
        <sha256>.webp
        <sha256>.partial
```

### 6.1 Host INI

Create `host.ini` with defaults on first Host Mode launch if it does not exist. Use `Microsoft.Extensions.Configuration.Ini`; do not implement a custom parser.

Initial values:

```ini
[Assets]
MaxCardCount=1000
MaxSourceImageBytes=2097152
MaxDecodedPixels=25000000
MaxSessionAssetBytes=2147483648
MaxMemoryCacheBytes=268435456
MaxPackSize=1000
MaxDraftCardInstances=10000
OutputMaxLongEdge=1200
OutputWebPQuality=80
TransferChunkBytes=65536
TransferStallSeconds=60

[Protocol]
ControlMessageMaxBytes=262144

[Scryfall]
Directory=<LocalAppData>\DraftSimulator\Scryfall
```

Rules:

- Invalid, missing, negative, or nonsensical asset/protocol values cause a predefined host configuration error rather than silent fallback.
- The optional Scryfall `Directory` value defaults to `<LocalAppData>\DraftSimulator\Scryfall` when absent or blank, preserving compatibility with existing `host.ini` files. Its parent directory is created when needed.
- Limits may be edited manually between application runs.
- The application does not provide an INI editor in the UI.
- The 2 GB session limit is a disk-backed transformed-asset limit, not a memory-allocation target.
- Use 64-bit byte counts throughout asset scanning and transfer accounting.
- All modes use the embedded asset-cache defaults. If `host.ini` exists, its cache and protocol values also apply in Client Mode; source scanning and draft-generation values apply only in Host Mode.

### 6.2 Client State

Write `client-state.json` atomically by writing a temporary file and replacing the previous file. It may contain:

- Last host SteamID.
- Last Steam lobby ID.
- Last application session ID.
- Last normalized room code when applicable.
- Last accepted player name.
- Whether the previous session ended normally.

SteamID is the reconnect identity. Do not generate a separate client identity when Steam is available.

This intentionally supersedes the platform-neutral generated client identifier described in `high-level-design.md`; SteamID provides the persistent identity now that Steam is the fixed MVP transport.

Do not persist collections, pack contents, settings, or draft history in `client-state.json`.

## 7. Core Domain Model

Use strongly typed IDs rather than passing raw strings throughout the core. IDs may wrap `Guid`, `ulong`, or SHA-256 values as appropriate.

Required concepts:

- `SessionId`: unique application session identifier.
- `PlayerId`: internal identifier mapped to one SteamID for the session.
- `CardDefinitionId`: a GUID identifying one source image file. Distinct files always have distinct definitions, even if their bytes are identical.
- `AssetHash`: SHA-256 of transformed WebP bytes.
- `CardInstanceId`: one occurrence of a card within the draft. Replacement mode may create multiple instances from one definition.
- `PackId`: one generated pack.
- `Rarity`: one of the six supported rarity values.
- `PlayerState`: identity, name, order, connection state, lobby ready state, draft locked state, current pack, and collection.
- `CardDefinition`: host-only source path, filename-derived fallback name, rarity, optional metadata, and scan information.
- `CardInstance`: instance ID, definition ID, and asset hash once transformed.
- `Pack`: ordered or unordered collection of card instance IDs plus its current holder.
- `DraftSettings`: replacement mode, per-rarity settings, pack size, pick size, packs per player, and direction rule.
- `DraftState`: roster, turn order, round, pick number, active direction, packs, collections, and phase.

Source filesystem paths are host-only infrastructure data and must never enter client view models or wire DTOs.

## 8. Application and Session States

Application screen states:

- `Start`.
- `Lobby`.
- `StartingCountdown`.
- `PreparingSession`.
- `Drafting`.
- `Complete`.
- `SessionError`.

Host session phases:

- `LobbyOpen`.
- `StartingCountdown`.
- `PreparingAssets`.
- `Drafting`.
- `Complete`.
- `Closed`.

Only transitions explicitly supported by the state machine are legal. Invalid local commands are ignored with a local diagnostic. Invalid remote commands receive a predefined protocol or invalid-state error and may cause disconnection if repeated or structurally malformed.

## 9. Card Directory Scanning

The selected root directory is scanned only by the host.

Recognized immediate child folder names, matched case-insensitively:

- `Common`.
- `Uncommon`.
- `Rare`.
- `Super Rare`.
- `Ultra Rare`.
- `Mythic Rare`.
- `Special`.
- `Bonus`.

Rules:

- Do not recurse below a rarity folder.
- Ignore unrelated root files and folders except optional root `data.csv`.
- Match `.jpg`, `.jpeg`, `.png`, and `.webp` case-insensitively.
- A missing rarity folder is valid and is omitted from the configuration UI.
- If multiple immediate folders normalize to the same rarity, merge their files into that rarity.
- The card fallback name is the filename without its extension.
- Fallback names must be nonempty and globally unique under `StringComparer.OrdinalIgnoreCase`; duplicate names are a blocking scan error. Conceptual duplicate cards must use distinct filenames.
- If valid candidate files exceed `MaxCardCount`, report a blocking scan error rather than silently truncating the pool.
- Reject individual source files larger than `MaxSourceImageBytes`.
- Inspect image headers, enforce the pixel limit, then fully decode each image once during scanning and immediately dispose it. This catches truncated or corrupt data without retaining decoded images.
- Reject images whose decoded width multiplied by height exceeds `MaxDecodedPixels`.
- Use checked arithmetic for dimensions and byte totals.
- Skip unreadable or invalid files and create host-visible warnings.

Warnings do not block a draft unless the remaining usable pool cannot satisfy the settings. Never send warning details, paths, or filenames to clients.

### 9.1 Scryfall Set Import

The host may import a set using a set name or set code. Imports populate the configured Scryfall directory with a sibling directory named from the canonical set name; filesystem-invalid characters are replaced, and a set-code suffix disambiguates collisions. Each set directory contains the standard rarity folders and is selected and scanned after import.

- Resolve names/codes from a locally cached Scryfall set catalog, refreshing that catalog at most once per 24 hours.
- Search cards with `GET https://api.scryfall.com/cards/search?q=set%3A<code>&unique=prints&order=set` and follow each supplied `next_page` URI.
- Wait at least one second between card-search pages. Send Scryfall's required `User-Agent` and `Accept` headers, respect 429 responses, and restrict API pagination to HTTPS `api.scryfall.com`.
- Use the full-card PNG image when available, with `large` and `normal` image fallbacks. For multifaced cards use the front face image. Download from HTTPS Scryfall image hosts with at most four concurrent image requests.
- Map Scryfall `common`, `uncommon`, `rare`, `mythic`, `special`, and `bonus` directly to `Common`, `Uncommon`, `Rare`, `Mythic Rare`, `Special`, and `Bonus` folders. `Super Rare` and `Ultra Rare` remain available for user-provided directories.
- Make filenames unique using card name and collector number, adding a short Scryfall ID suffix only on collision. The filename stem remains the fallback export name.
- Enforce `MaxCardCount` and `MaxSourceImageBytes`. Cards without an available image or whose image exceeds the source byte limit are skipped and counted in the host-visible result. Other failed HTTP requests abort the import.
- With **Force fetch** unchecked, reuse and scan an existing set directory without fetching card pages or images. With it checked, download to a sibling staging directory and replace only a matching Scryfall-managed set directory after successful completion. Cancellation or failure preserves the prior import. A non-Scryfall directory is never overwritten.
- Keep a completion marker in each imported set folder and cache the set catalog for at least 24 hours. Do not add `data.csv`; export names use unique filename stems until a metadata schema is defined.

The **Refresh** button performs a complete rescan and rebuilds the detected-rarity configuration. The host cannot browse or refresh while ready.

## 10. Draft Configuration Validation

Validation runs after every host setting change, player-count change, directory refresh, and immediately before preparation.

Required checks:

- Player count is between one and eight, inclusive, counting the host.
- Pack size, packs per player, and cards per pick are positive integers.
- Pack size does not exceed `MaxPackSize`.
- Active player count multiplied by packs per player multiplied by pack size does not exceed `MaxDraftCardInstances`.
- Every rarity minimum and maximum is nonnegative.
- Every relative weight is a finite, nonnegative `double`; weights do not need to sum to 100.
- Every rarity minimum is no greater than its maximum.
- The sum of minimums does not exceed pack size.
- Effective maximums can fill the pack.
- A rarity with zero weight has an effective maximum equal to its minimum.
- A rarity with equal minimum and maximum has an effective maximum equal to that value.
- After fixed contributions, at least one positive-weight rarity can fill every remaining slot.
- A rarity requiring cards contains at least one valid definition in replacement mode.
- Without replacement, valid definitions for every rarity are at least `effectiveMaximum * totalPackCount`.
- `totalPackCount` equals active player count multiplied by packs per player.
- All integer multiplication uses checked 64-bit arithmetic.

The no-replacement availability check is deliberately conservative. It guarantees that weighted generation cannot exhaust a rarity even if every pack reaches that rarity's effective maximum.

Blocking validation failures appear as red host-only errors at the bottom of the lobby and prevent readiness or startup as appropriate.

## 11. Pack Generation

All packs for the entire draft are generated during `PreparingAssets` before any assets are sent.

### 11.1 Randomness

- Generate a session seed with `RandomNumberGenerator`.
- Construct an injected pseudorandom source from that seed for pack generation, player-order shuffling, and forced random selections.
- Retain the seed in host diagnostics for reproducing defects, but do not display or send it to clients.
- Tests must supply deterministic random sources.

### 11.2 Player Order

Shuffle the active player list once at game start. The result is the circular turn order. One-player drafts produce a one-entry circle and pass packs back to the same player.

### 11.3 Per-Pack Algorithm

For every pack:

1. Add each rarity's configured minimum.
2. Select a random eligible definition for each required card instance and immediately remove it from the global pool in no-replacement mode.
3. While the pack has fewer than the configured pack size, determine eligible rarities that remain below effective maximum and have positive weight.
4. Select a rarity using `weight / sum(eligible weights)`.
5. Select a random definition from that rarity.
6. Immediately remove that definition from the global pool in no-replacement mode.
7. Create a new `CardInstanceId` for the occurrence.

With replacement, the same definition may occur multiple times in one pack and across the draft. Without replacement, one definition may occur at most once in the entire draft.

Generate packs grouped by pack round, with one initial pack per player position per round. Packs remain hidden host state until the owning client is allowed to see them.

## 12. Lobby and Start Flow

### 12.1 Host Creation

The host initializes Steam, creates a public/searchable Steam lobby with a member limit of eight, and creates a P2P listen socket using `ISteamNetworkingSockets::CreateListenSocketP2P`. Public visibility is required for typed room-code lookup and normal Steam friend presence; the room-code handshake remains the application access check.

Lobby metadata keys:

```text
ds_app=draft-simulator
ds_protocol=1
ds_room_hash=<lowercase SHA-256 hex>
ds_open=1|0
```

Use an exact application marker and protocol filter for all searches.

### 12.2 Room Code

- Generate 10 random Crockford Base32 characters using `RandomNumberGenerator`.
- Display the code as two groups of five characters, for example `ABCDE-FGHJK`.
- Normalize user input by removing ASCII spaces and hyphens and converting to uppercase.
- Reject characters outside the Crockford Base32 alphabet.
- Compute `ds_room_hash` as lowercase SHA-256 hex over UTF-8 bytes of the normalized code.
- Search Steam lobbies by exact `ds_app`, `ds_protocol`, `ds_room_hash`, and `ds_open` values.
- Send the normalized original room code in the initial authenticated application handshake.
- The host compares room codes in constant time after hashing.

The room code is the application access token. This threat model assumes trusted friends and does not require protection against determined online guessing.

### 12.3 Friend Join

Users may join through a Steam invitation or Steam friend lobby presence. A joining SteamID that is an immediate friend of the host or was explicitly invited may bypass the room-code field. All other joins require the room code.

With AppID 480, the application should already be running before an invitation is accepted.

### 12.4 Names and Readiness

Player names must:

- Contain only printable ASCII characters from space through `~`.
- Be trimmed at both ends.
- Contain at least one non-space character.
- Be no longer than 16 characters after trimming.
- Be unique under `StringComparer.OrdinalIgnoreCase`.

The host is a player and counts toward the eight-player limit.

Ready behavior:

- Client settings are not sent to or shown to clients.
- A client's ready state means availability, not approval of settings.
- The host cannot edit settings, browse, or refresh while host-ready.
- The host can unready and resume editing.
- Setting changes, refreshes, joins, and departures do not automatically unready existing players.
- All-ready starts an approximately three-second countdown.
- The host and clients display the remaining whole seconds (3, 2, 1); canceling readiness or changing the roster cancels the countdown.
- The countdown cancels when all-ready becomes false because of unready, join, departure, or disconnect.
- Countdown completion closes the lobby to new joins by setting `ds_open=0` and disabling lobby joinability.

### 12.5 Kicking

Kicking sends a predefined `Kicked` message, closes the application connection, removes the player from host state, and instructs the cooperative client to leave the Steam lobby. Kicking is not a ban; the same SteamID may rejoin immediately while the lobby is open.

During `LobbyOpen` or `StartingCountdown`, a disconnected remote player is removed from the application roster immediately, releases their name, and cancels the countdown when applicable. Steam normally removes a disconnected process from its lobby; on graceful application disconnection, explicitly call `LeaveLobby`. A subsequent connection is handled as a new lobby join, though the previous validated name may be prefilled locally.

### 12.6 P2P Connection Lifecycle

- Use Steam virtual port `0` for the single Draft Simulator service.
- Call `ISteamNetworkingUtils::InitRelayNetworkAccess` during startup to warm relay access.
- For a normal join, the client enters the Steam lobby, reads the lobby owner SteamID, and calls `ConnectP2P` for that identity and virtual port.
- For a reconnect to an active draft whose lobby is closed to joins, the client calls `ConnectP2P` directly with the stored host SteamID and proves the retained session ID in `Hello`; it does not need to re-enter the Steam lobby.
- The host receives the connection callback, confirms that the SteamID is a lobby member or retained reconnecting player, and accepts the connection.
- The client must send `Hello` within five seconds of connection acceptance.
- The host completes room-code or friend authorization before accepting any session command other than `Hello`.
- Maintain one Steam networking connection per remote player for control messages and assets.
- Use Steam connection close reasons for diagnostics, but map them to project-owned error codes before displaying them.
- Never expose or request an IP address and never open a direct listening TCP or UDP port.

## 13. Session Preparation and Asset Pipeline

### 13.1 Host Preparation

After countdown completion:

1. Freeze the roster and settings.
2. Revalidate card files referenced by the last scan.
3. Generate player order and every pack.
4. Determine unique card definitions used by any generated pack.
5. Process only those used definitions.
6. Decode one source image at a time.
7. Recheck decoded dimensions against `MaxDecodedPixels`.
8. Resize without upscaling so the long edge is at most `OutputMaxLongEdge`.
9. Preserve aspect ratio.
10. Encode as WebP at `OutputWebPQuality`.
11. Compute SHA-256 over the final encoded bytes.
12. Write the file atomically as `assets/<sha256>.webp`.
13. Deduplicate transformed assets by hash while retaining distinct card definitions and instances.
14. Stop with a host preparation error if transformed assets exceed `MaxSessionAssetBytes`.
15. Verify that the session volume has enough free disk space for current output plus at least 64 MB of reserve.
16. Write a host manifest and begin client transfer.

Never load all sources or transformed assets into memory. Decode, resize, hash, and write sequentially. Use a bounded LRU cache no larger than `MaxMemoryCacheBytes` for frequently displayed decoded images.

The transformed files are the immutable session snapshot. Changes to source files after transformation do not affect the active draft.

### 13.2 Manifest

The client asset manifest contains only:

- Application session ID.
- Protocol version.
- Asset hash.
- Encoded byte length.
- Pixel width and height.
- MIME type fixed to `image/webp`.
- Chunk count.

It does not contain source paths, source filenames, card names, metadata text, rarity configuration, or pack ownership.

### 13.3 Client Transfer

Client behavior:

1. Create or reopen the session cache directory.
2. Compare manifest hashes and lengths with complete cached files.
3. Send one `AssetNeed` control message listing missing or invalid hashes.
4. Verify that the session volume has enough free disk space for missing assets plus at least 64 MB of reserve.
5. Receive reliable asset chunks.
6. Write chunks to their calculated offsets in `<hash>.partial` without buffering the whole asset, tracking received indexes with a bounded bit set.
7. Verify final byte length and SHA-256.
8. Atomically rename the file to `<hash>.webp`.
9. Send `PreparationReady` only after every required asset verifies.

The host waits for every client. The preparation screen displays local progress and public per-player ready/failure status.

Use a no-progress timeout of `TransferStallSeconds`; do not impose a total timeout while progress continues.

### 13.4 Preparation Failure

If host processing, transfer, verification, disconnection, or disk-space validation fails:

- Cancel startup for every participant.
- Return all participants to the lobby.
- Reopen Steam lobby joinability.
- Mark the failing player unready, or mark the host unready for host-side preparation failure.
- Preserve successfully transformed and transferred hash-valid assets for retry.
- Show the host the failing player's name and a predefined reason code.
- Do not automatically restart while everyone remains ready.

## 14. Wire Protocol

### 14.1 General Rules

- Protocol version starts at `1`.
- All control messages use UTF-8 JSON.
- Control messages are limited to `ControlMessageMaxBytes` before parsing.
- JSON maximum depth is 16.
- Use source-generated `System.Text.Json` metadata.
- Use concrete DTOs only; do not use `dynamic`, `object`, `JsonNode`, or typeless polymorphic deserialization.
- Configure unmapped members as disallowed.
- Reject unknown message codes.
- Reject duplicate object properties if the selected parser permits detection.
- Enum values travel as numeric codes, not arbitrary strings.
- Every message is valid only in explicitly listed session phases.
- The transport SteamID determines the sender. Never trust a claimed player ID from a client payload.
- Send all control and asset messages with Steam's reliable delivery flag. The application does not use unreliable messages in the MVP.

Control envelope:

```json
{
  "v": 1,
  "code": 1,
  "requestId": "00000000-0000-0000-0000-000000000000",
  "payload": {}
}
```

Deserialize the envelope header, select the concrete payload DTO from the numeric code, then deserialize that DTO strictly.

### 14.2 Client-to-Host Codes

| Code | Name | Purpose |
|---:|---|---|
| 1 | `Hello` | Protocol version, lobby ID, optional normalized room code, reconnect session ID, and requested name. |
| 2 | `SetName` | Request a validated lobby display name. |
| 3 | `SetLobbyReady` | Set lobby ready or unready. |
| 4 | `SetSelection` | Replace the sender's current selected instance-ID set for the current pick revision. |
| 5 | `SetPickLocked` | Lock or unlock the current validated selection. |
| 6 | `AssetNeed` | List missing asset hashes after receiving a manifest. |
| 7 | `PreparationReady` | Confirm all required assets are verified locally. |
| 8 | `ReopenLobbyJoin` | Request entry into a reopened post-draft lobby. |

Host-only actions such as setting configuration, kicking, forcing ready, opening the lobby, and exporting are local coordinator commands and are never accepted as remote client messages.

### 14.3 Host-to-Client Codes

| Code | Name | Purpose |
|---:|---|---|
| 101 | `Welcome` | Accepted identity, session information, and reconnect result. |
| 102 | `Error` | Predefined error code and fatal/nonfatal flag. |
| 103 | `LobbySnapshot` | Personalized lobby state and public player list. |
| 104 | `CountdownStarted` | Countdown duration and generation number. |
| 105 | `CountdownCancelled` | Cancellation generation and predefined reason. |
| 106 | `AssetManifest` | Required transformed-asset manifest. |
| 107 | `PreparationSnapshot` | Public transfer status and local progress totals. |
| 108 | `DraftSnapshot` | Personalized current pack, own selection, own collection, and public player status. |
| 109 | `DraftCompleted` | One page of the recipient's final collection and sanitized export names. |
| 110 | `LobbyAvailability` | Whether the completed host has reopened the lobby. |
| 111 | `Kicked` | Host removed this client from the lobby. |
| 112 | `HostClosed` | Session ended because the host closed or failed. |

Prefer complete personalized snapshots over complicated gameplay deltas. Snapshots simplify reconnection and stale-message recovery.

Any manifest, snapshot, asset-request list, or completion result that would exceed `ControlMessageMaxBytes` must be split into pages. A paged message includes one snapshot UUID, revision, zero-based page index, page count, and no more than 200 variable-list entries. The receiver buffers pages by snapshot UUID, rejects conflicting duplicates, and applies the snapshot atomically only after every page arrives. A newer revision discards incomplete pages for an older revision.

### 14.4 Revisions and Idempotency

- `LobbySnapshot` has a monotonically increasing lobby revision.
- `DraftSnapshot` has a monotonically increasing draft revision and current pick revision.
- `SetSelection` and `SetPickLocked` must include the current pick revision.
- The host rejects stale or future revisions.
- Repeated identical ready or selection requests are idempotent.
- Once final lock processing begins, a racing unlock request is stale and must not reverse resolution.

### 14.5 Binary Asset Chunks

Asset chunks use one reliable Steam networking message with this fixed big-endian header:

```text
4 bytes  magic: ASCII DSAS
1 byte   binary protocol version: 1
1 byte   message type: 1 (asset chunk)
2 bytes  reserved: zero
16 bytes session UUID
32 bytes SHA-256 asset hash
4 bytes  chunk index
4 bytes  chunk count
4 bytes  payload length
N bytes  payload, at most TransferChunkBytes
```

Validate magic, version, reserved bytes, session ID, hash membership, chunk bounds, payload length, and total reconstructed length before writing. Reject malformed chunks without passing them to the UI.

## 15. Host Authority and Message Validation

Structurally valid client messages still require semantic validation.

The host must verify:

- The connection belongs to a current or reconnectable lobby member.
- The message is legal in the current session phase.
- The name satisfies all name rules and remains unique.
- A ready request is legal for that player.
- Selected card instances are distinct and belong to the sender's current pack.
- Selection count does not exceed the required pick count.
- `SetSelection` is rejected while that player is locked; the player must successfully unlock before changing the selection.
- Locking is allowed only when selection count exactly equals the required count.
- Unlocking is allowed only before all-player resolution begins.
- Asset hashes in `AssetNeed` belong to the active manifest.
- Preparation confirmation is accepted only after the client requested or already possessed every required asset.
- Reconnect session IDs and SteamIDs match retained state.

Do not render arbitrary remote error text. Host-to-client errors use an `ErrorCode` enum mapped to local text. The only user-originated text displayed to other users is the validated player name, rendered in plain text controls.

## 16. Draft Runtime Rules

### 16.1 Personalized Snapshot

Each `DraftSnapshot` contains:

- Draft revision and pick revision.
- Current pack instance IDs and corresponding asset hashes for that recipient.
- That recipient's selected instance IDs.
- That recipient's collection instance IDs and asset hashes.
- Authoritative required selection count, `CanLock`, and `CanUnlock` values for the recipient's current state.
- Public player order.
- Public connection and locked status.
- Public current-pack card counts.
- Active direction, pack round, and total pack rounds.

It must not contain other players' selections, collections, or private card identities.

### 16.2 Selection and Locking

- Clicking a card toggles local selection and submits the full selection set.
- **Lock In** is enabled only when exactly the configured pick count is selected.
- A locked player may unlock while at least one player remains unlocked and resolution has not begun.
- When the host processes the final lock, it resolves immediately and atomically.
- Selected instances move from pack to collection.
- Remaining packs move one player in the active circular direction.
- A new personalized snapshot is sent after accepted state changes and resolution.

If a pack contains fewer cards than cards-per-pick, the host automatically moves all remaining instances into that player's collection without requiring a selection or lock. If a pack contains exactly cards-per-pick, normal explicit selection and lock are required.

After initial pack assignment and every pass, the host runs automatic-pick resolution until the draft reaches a state that requires at least one manual selection or the current pack round ends. Automatic picks use the same atomic state-transition path as manual resolution.

### 16.3 Direction

Represent clockwise as `+1` and counterclockwise as `-1` over the circular player-order array.

- Always clockwise: every round uses `+1`.
- Always counterclockwise: every round uses `-1`.
- Start clockwise: round one uses `+1`, then alternate each round.
- Start counterclockwise: round one uses `-1`, then alternate each round.

Direction changes only after all packs in a pack round are exhausted.

### 16.4 Force Ready

The host sees **Force Ready** only after locking the host's own valid selection.

When invoked:

- Preserve every existing valid selection.
- For every unlocked player, randomly add distinct instances from that player's pack until the required count is reached.
- Lock every player.
- Resolve through the same final-lock path used by normal play.

The rule applies to connected and disconnected players.

### 16.5 Completion

The draft completes after every pre-generated pack round is exhausted.

The host sends each client only that client's final collection mapping:

- Card instance ID.
- Asset hash.
- Sanitized export card name.

Sanitize export names by trimming, replacing CR, LF, tab, and NUL with spaces, and limiting them to 256 Unicode scalar values. Names are never interpreted as markup.

Clipboard output contains one line per collected card instance:

```text
# {CardName}
```

## 17. Disconnect and Reconnect

### 17.1 Client Disconnect During Draft

- Mark the player disconnected but retain player state, current selection, pack, collection, and order.
- Notify the host using a local predefined status.
- Continue allowing **Force Ready** to complete that player's picks.
- Keep the SteamID-to-player mapping until the host closes or reopens the session.

### 17.2 Reconnect

- The client start screen offers **Reconnect** when `client-state.json` indicates an abnormal exit or active session.
- Reconnect uses the stored lobby ID, host SteamID, session ID, and optional room code.
- The host identifies the player by SteamID.
- The host sends a current manifest and personalized state snapshot.
- The client verifies cached assets and requests only missing hashes.
- Once assets are ready, the client resumes the current draft state.

A stale reconnect attempt receives a predefined `SessionNoLongerAvailable` error and remains on the start screen.

### 17.3 Host Failure

If the host closes, loses Steam, or terminates the authoritative session:

- Close the Steam lobby and transport connections when possible.
- Send `HostClosed` when graceful delivery is possible.
- Clients show a predefined host-disconnected error.
- The draft cannot continue and host migration is not attempted.

## 18. Completion, Reopened Lobby, and Cockatrice Export

### 18.1 Reopened Lobby

From the completion screen, the host may exit or reopen the lobby.

Reopening:

- Clears draft packs, selections, collections, and completion state.
- May retain the selected directory and current host settings in memory.
- Creates a new application roster containing only the unready host.
- Sends `LobbyAvailability` to completed remote clients and requires them to leave the old Steam lobby while remaining on their completion screens. Keep the P2P connection open long enough to deliver that notification.
- Completed remote clients do not occupy Steam or application roster slots and do not participate in the all-ready check until they choose to join.
- Restores a returning player's prior validated name when available and adds that player to the new roster as unready.
- Reopens Steam lobby joinability and sets `ds_open=1`.
- Displays a join-lobby action to completed clients.
- Allows players to join at their leisure rather than moving them automatically.

### 18.2 Cockatrice Export

The host exports the complete scanned card set, not only cards used in the draft and not player ownership.

Implementation boundaries:

- Define `ICardMetadataProvider` in Core.
- Define `CockatriceXmlExporter` in Infrastructure.
- Use `XmlWriter` or `XDocument`; never concatenate XML strings.
- Escape all names and metadata through the XML API.
- Use filename stem as the fallback card name.
- Block export if final card names are empty or duplicate under `StringComparer.OrdinalIgnoreCase`.
- Produce Cockatrice card database version 4.
- Use fallback set code `DRAFT`, long name `Draft Simulator Export`, and set type `Custom`.
- Omit the optional release date when no metadata supplies one.
- Emit one card element per valid scanned definition.
- Emit fallback card name, empty text, `Unknown` for both `type` and `maintype`, `3` for `tablerow`, and the mapped lowercase rarity name.
- Use the definition's GUID as that card's set UUID.
- Use the `DRAFT` set code in every fallback card set element.
- Export only from the host's scanned metadata snapshot.

The fallback above is sufficient for a complete Cockatrice-compliant XML export. The precise `data.csv` columns and optional field overrides still require a later product decision. Until that schema is specified, detect `data.csv`, show a host warning that enrichment is not yet supported, and export using the fallback values rather than guessing at its contents.

## 19. Temporary Asset Lifecycle

Normal completion flow:

- Keep the active session cache while the completion screen is open.
- When the user exits from the completion screen, delete that session directory and clear reconnect state.

Abnormal or mid-draft exit:

- Preserve the session directory and reconnect state.
- Do not delete partial files needed for a possible reconnect.

Subsequent startup actions:

- **Reconnect** preserves and reuses the matching session directory.
- **Host** deletes all abandoned prior session directories before creating a new host session.
- Joining a new room by code or invitation deletes abandoned prior session directories before joining.
- Reopening the same completed lobby may delete the completed draft's session assets only after the new lobby transition no longer needs them.

Deletion failures are logged and retried on the next startup. They do not block application use unless disk-space validation fails.

## 20. UI Implementation

Use Avalonia XAML views with view models from `CommunityToolkit.Mvvm`. Navigation is controlled by one application shell and current-screen view model.

Required screens:

- Start screen with Host, room-code entry, Join, and conditional Reconnect.
- Host/client lobby screen.
- Preparation and asset-transfer progress screen.
- Draft screen.
- Completion screen.
- Session error state or dialog.

Lobby requirements:

- Client player list and ready indicators.
- Connection status.
- Host kick controls.
- Host card-directory Browse and Refresh controls.
- Host settings controls for detected rarities and draft rules.
- Rarity controls disable the weight when minimum equals maximum. When weight is zero, keep the weight editable but disable maximum editing and visibly indicate that the rarity contributes exactly its minimum.
- Host settings hidden entirely from clients.
- Host validation warning and red blocking-error region at the bottom.
- Room code and Steam invite action for the host.

Draft requirements:

- Virtualized or lazy-loaded card grid.
- Click selection with a visible selected state.
- Pool cards display at 225 by 315 pixels. The side preview displays at 450 by 630 pixels, using the same transformed asset.
- Hovering a card updates the side preview. Each client has an independent **Hover zoom overlay** option, off by default; when enabled, hovering opens a large overlay that closes when the pointer leaves the card.
- The draft side panel scrolls vertically as needed while keeping pick controls accessible. Lobby settings and lobby controls each scroll vertically when constrained by the window size.
- Lock and unlock action.
- Host-only conditional Force Ready action.
- Public player status list.
- Own collection thumbnails along the bottom.
- No other-player collection or selection views.

Image loading requirements:

- Decode from transformed session files, not source paths.
- Keep decoded bitmaps in a bounded LRU cache.
- Dispose native Skia and Avalonia bitmap resources promptly.
- Load and decode away from the UI thread.
- Marshal only final bitmap assignment to the UI dispatcher.

All remote strings must use plain text controls. Do not use HTML, Markdown, rich text, XAML parsing, or dynamic templates from network data.

## 21. Errors and Logging

Define stable error enums for at least:

- Steam unavailable.
- Lobby creation failed.
- Lobby not found.
- Room code invalid.
- Room closed.
- Lobby full.
- Name invalid or duplicate.
- Protocol version mismatch.
- Malformed message.
- Invalid action for state.
- Host disconnected.
- Client disconnected.
- Source scan failed.
- Draft configuration invalid.
- Host asset preparation failed.
- Client asset transfer stalled.
- Client asset verification failed.
- Client disk space insufficient.
- Session no longer available.
- Kicked.

Map enums to local UI strings. A host may include a failing player's validated display name, but not arbitrary explanatory text.

Write rolling local logs under `%LocalAppData%\DraftSimulator\Logs`. Logs may contain IDs, phases, counts, error codes, revisions, and the host's reproduction seed. Avoid logging asset bytes, room codes, source paths, card metadata, or complete wire payloads by default.

No telemetry or remote logging is required.

## 22. Packaging

Publish Windows x64 as a self-contained, framework-dependent-free folder. Do not require an installer.

Baseline command:

```powershell
dotnet publish src/DraftSimulator.App/DraftSimulator.App.csproj -c Release -r win-x64 --self-contained true
```

Do not require single-file publishing. The output may contain the application executable, .NET runtime files, Avalonia/Skia native files, Steamworks.NET dependencies, `steam_api64.dll`, and `steam_appid.txt`.

Create two build profiles:

- `Development`: AppID 480, diagnostics enabled, launched from the build tree.
- `FriendsTest`: AppID 480, portable ZIP, diagnostics at normal level.

Define `SteamAppId` once as an MSBuild property with a default of `480`. Development and FriendsTest builds generate `steam_appid.txt` from that property and verify after Steam initialization that Steam reports the expected AppID. Future Steam builds with a dedicated AppID must set the property to the assigned value, omit `steam_appid.txt` from the depot, and allow Steam to provide the AppID.

## 23. Testing Strategy

### 23.1 Core Unit Tests

Cover:

- Every validation rule and boundary.
- Case-insensitive rarity scanning normalization through abstract scan inputs.
- Minimum and maximum composition.
- Zero-weight rarity behavior.
- Equal-minimum-and-maximum behavior.
- Weighted selection using deterministic random sources.
- Replacement duplicates.
- Global no-replacement uniqueness.
- Conservative no-replacement capacity validation.
- One through eight players.
- One-player pack passing.
- Every direction mode across multiple rounds.
- Pack sizes not divisible by pick size.
- Automatic collection when fewer than pick size remain.
- Normal lock/unlock races.
- Force Ready preserving existing selections.
- Disconnect, forced selection, and reconnect state.
- Completion and lobby reopening.

### 23.2 Protocol Tests

Cover:

- Round trips for every concrete DTO.
- Unknown and phase-invalid message codes.
- Unknown JSON properties.
- Oversized and deeply nested JSON.
- Stale and future revisions.
- Invalid IDs and selection counts.
- Truncated, oversized, out-of-range, duplicated, and wrong-session asset chunks.
- Hash and byte-length verification.
- Fuzzed malformed control and binary inputs without process crashes.

### 23.3 Infrastructure Tests

Cover:

- File extension and folder-name casing.
- Invalid and unreadable images.
- Source byte limits.
- Decoded pixel limits and decompression-bomb candidates.
- WebP resize dimensions and no-upscale behavior.
- Sequential processing under bounded memory.
- Content hash deduplication.
- Atomic writes and interrupted partial files.
- Cache reuse and cleanup policies.
- INI validation.
- Atomic client-state updates.
- XML escaping once export fields are specified.
- Scryfall set-name/code resolution, cached catalog behavior, pagination delay, all rarity mappings, image fallbacks and size limits, existing-folder reuse, and transactional force-fetch failure handling.

### 23.4 Integration Tests

Use an in-memory fake transport to run one host and up to seven clients in one test process. Cover full lobby-to-completion drafts, transfer failure rollback, disconnect/reconnect, kick/rejoin, malformed client messages, and host closure.

Perform manual Steam tests with separate Steam accounts and preferably separate machines or virtual machines:

- Room-code search under AppID 480.
- Exact filtering against unrelated Spacewar lobbies.
- Friend invitation while both applications are already running.
- P2P relay connectivity without port forwarding.
- Large asset transfer with bandwidth throttling.
- Steam interruption and reconnection.

For local UI investigation, launch separate Debug processes with distinct profiles:

```text
powershell -ExecutionPolicy Bypass -File .\publish-offline-debug.ps1
.\artifacts\publish\offline-debug-countdown-zoom-win-x64\DraftSimulator.Debug.exe
.\artifacts\publish\offline-debug-countdown-zoom-win-x64\DraftSimulator.Debug.exe --profile client1
```

## 24. Acceptance Criteria

The MVP is technically complete when:

- A self-contained Windows build runs without a separately installed .NET runtime.
- Up to eight Steam users, including the host, can join through room code or supported friend join without port forwarding.
- AppID 480 lobby searches never display unrelated Spacewar lobbies.
- The host can scan and validate up to configured limits without bulk-loading source images into memory.
- All packs are generated before transfer and only used unique card images are transformed and sent.
- Every client verifies and caches the same transformed assets before drafting begins.
- Transfer failure returns everyone safely to the lobby without a restart loop.
- The host remains authoritative and rejects malformed, stale, and semantically invalid client actions.
- Draft selections, passing, direction changes, force ready, disconnects, reconnects, and completion match `high-level-design.md`.
- Other players' selections and collections are absent from client snapshots.
- Temporary assets follow the required normal-exit and reconnect cleanup lifecycle.
- Players can copy their own final card list.
- The host can export a Cockatrice version 4 XML file using fallback metadata; optional `data.csv` enrichment remains deferred.
- The host can import a Scryfall set into the configured directory, and Special/Bonus cards scan, configure, draft, and export as distinct rarities.

## 25. Dedicated AppID Migration

When the project moves beyond private testing:

1. Purchase and configure a dedicated Steam AppID or Steam Playtest AppID.
2. Replace the configured AppID.
3. Upload the same portable build contents through SteamPipe without `steam_appid.txt`.
4. Configure executable launch options for Windows.
5. Verify Steam lobby, invitation, ownership, and relay behavior under the new AppID.
6. Keep protocol version `1` unless wire compatibility changes.

No core, protocol, pack-generation, image-pipeline, or UI architecture rewrite should be necessary.
