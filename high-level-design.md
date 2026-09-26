# Draft Simulator: High-Level Design

## 1. Purpose

Draft Simulator is an extremely lightweight, desktop-first application for running multiplayer drafts using arbitrary card images supplied by the host. It is not tied to a particular card game. A host selects a local card directory, configures pack composition and draft behavior, and runs a session for up to eight players.

Players join the host, receive packs of card images, select cards, and pass the remaining cards around a circular turn order. Each player's collected cards remain private during the draft. At the end, players can inspect and copy their own card lists, while the host can export the available card set in Cockatrice-compatible XML format.

The primary target is a desktop PC experience, with macOS as a secondary target. Touch and mobile-specific interaction are not requirements.

## 2. Operating Modes

The application has two launch modes:

- **Host Mode:** Creates and controls a lobby on the host's machine. The host is also a drafting player and counts toward the player limit.
- **Client Mode:** Connects to a host and participates as a player.

A lobby supports up to eight total players, including the host. A one-player draft is allowed even though it has little practical value.

Remote internet play must be supported, not only local-network play. The exact connection mechanism, addressing UX, NAT traversal strategy, and whether a human-friendly game code is used are technical design decisions. A game code that merely hashes an IP address would not itself provide routing or NAT traversal.

## 3. Card Directory

### 3.1 Directory Structure

The host selects a local directory containing zero or more immediate child folders with the following names:

- `Common`
- `Uncommon`
- `Rare`
- `Super Rare`
- `Ultra Rare`
- `Mythic Rare`
- `Special`
- `Bonus`

Folder matching is case-insensitive. Any subset of the rarity folders may be present. A rarity that is not present does not appear in the host's rarity configuration.

The host may import a Scryfall set into a set-specific directory. Scryfall rarities `common`, `uncommon`, `rare`, `mythic`, `special`, and `bonus` map to the corresponding folders above.

Rarity folders must be immediate children of the selected directory. Unrelated files and folders are ignored.

Each rarity folder contains card image files. Supported image formats are:

- JPG
- JPEG
- PNG
- WEBP

Each valid image file is one distinct card in the application's internal card pool. If a host wants multiple copies of the same conceptual card, each copy must be represented by a separately named image file. The host may use configuration and duplicate conceptual cards with distinct filenames to create nonuniform distributions.

The default card name is the image filename without its extension. Card names are not required in the drafting UI because the primary card presentation is the image.

### 3.2 Optional Metadata

The selected root directory may contain an optional `data.csv` with card metadata for Cockatrice export. Its exact schema will be defined when the export feature is implemented.

If metadata is absent or incomplete:

- The filename without its extension is used as the card name.
- Other required export fields receive suitable default text or values.

### 3.3 Refreshing and Invalid Files

The host UI provides a **Browse** control for selecting the card directory and a nearby **Refresh** control for rescanning it.

Unreadable, corrupt, or otherwise invalid image files are skipped and shown to the host as warnings. Warnings do not prevent starting a legal draft. If skipped files leave too few valid cards to satisfy the selected configuration, the condition becomes a blocking validation error.

## 4. Lobby

### 4.1 Player Identity and Joining

Clients may join only while the host's lobby is open. Joining after a draft has started is not allowed.

Each client installation stores a persistent, randomly generated client identifier. A reconnecting client is recognized by this identifier rather than by IP address, because multiple players may share one public IP address. The client's local file also retains the previous host connection information so the start screen can offer a **Reconnect** button with that information preloaded. A recognized client receives its previous player name automatically.

Players choose a unique display name subject to these rules:

- Maximum length of 16 characters.
- ASCII characters only.
- Spaces are allowed within the name.
- Leading and trailing spaces are removed.
- Uniqueness is case-insensitive, so names such as `Alice` and `alice` conflict.

Players leave by closing their client. A dedicated leave button is not required.

### 4.2 Lobby Player Controls

The lobby displays its player list to all participants. The host has a **Kick** button beside client players. Kicking is an AFK-management mechanism rather than a ban: a kicked player may immediately attempt to rejoin while the lobby remains open.

Every player, including the host, has a ready state. Readiness means that the player is available to begin; it does not signify approval of the host's settings, which are not visible to clients.

Readiness behavior is as follows:

- The host cannot edit settings while the host is ready.
- The host may unready to resume editing.
- Setting changes, directory refreshes, joins, and departures do not automatically unready players.
- Once all current players are ready, the UI displays a live countdown, such as **Game starts in 3... 2... 1...**, and begins the game after a short delay.
- The intended countdown is approximately three seconds.
- If the all-ready condition ceases during the countdown, such as from an unready, join, departure, or disconnection, the countdown is canceled.

## 5. Host Configuration

The host can configure the following while not ready:

- Whether cards are selected with replacement or without replacement.
- Minimum cards per pack for each detected rarity.
- Maximum cards per pack for each detected rarity.
- Relative selection weight for each detected rarity.
- Total cards per pack.
- Cards selected by each player per pick.
- Number of packs per player.
- Pack passing direction.

Direction options are:

- Always clockwise.
- Always counterclockwise.
- Start clockwise, then alternate after each pack round.
- Start counterclockwise, then alternate after each pack round.

### 5.1 Rarity Composition

Rarity minimums and maximums apply independently to every pack. Pack generation first satisfies all rarity minimums. Remaining slots are assigned randomly by relative rarity weight while respecting every rarity maximum.

For eligible rarities, the probability of choosing rarity `r` for an available slot is conceptually:

`weight(r) / sum(weights of all currently eligible rarities)`

A rarity ceases to be eligible when it reaches its maximum. Configuration validation must ensure that card availability cannot be exhausted during generation.

Special cases:

- If a rarity's minimum equals its maximum, it always contributes exactly that number. Its weight has no effect and should be hidden or disabled.
- If a rarity's weight is zero, it contributes exactly its minimum, effectively treating its minimum as its maximum.

### 5.2 Replacement Modes

With replacement:

- An image file may be selected multiple times within or across packs.

Without replacement:

- Each image file may appear at most once across the entire draft, including all players and all pack rounds.
- Pre-game validation requires each rarity to contain enough valid images to satisfy that rarity's configured maximum for every pack that will exist in the draft.

Whether packs are generated before the game or just in time is an implementation detail, provided that all validation and observable behavior remain the same.

### 5.3 Validation

The host cannot start an invalid draft. Blocking problems are displayed as a red error message at the bottom of the host's screen.

Validation includes, at minimum:

- Every rarity minimum is no greater than its maximum.
- The sum of rarity minimums does not exceed the pack size.
- The configured rarities can collectively fill the pack without exceeding their maximums.
- At least one positive-weight rarity can fill slots remaining after fixed minimum contributions.
- Every rarity has enough usable cards for its required contributions.
- In without-replacement mode, every rarity has enough cards to satisfy its configured maximum across all packs in the entire draft.
- Invalid or unreadable files do not reduce the usable pool below these requirements.

Warnings about non-blocking file issues remain visible but do not prevent starting.

## 6. Draft Gameplay

### 6.1 Starting a Draft

When the ready countdown completes:

- Every player is assigned a position in a circular turn order.
- One pack is generated for each player according to the host's settings.
- Every player initially receives one of those packs.

The in-game player list displays turn order, connection state, lock state, and current pack card counts.

### 6.2 Card Display and Selection

The current pack is displayed as a grid of card images. Hovering over a card updates a larger side preview. Each client may independently enable a full-screen hover zoom overlay.

On a normal pick, a player must select exactly the configured cards-per-pick count before **Lock In** becomes available. Clicking cards selects or deselects them. A player may unlock and revise a locked choice while at least one player remains unlocked.

When every player is locked, choices resolve immediately and atomically:

- Selected cards are removed from each pack.
- Selected cards are added to the selecting player's private collection.
- Each remaining pack moves to the next player in the active direction.

No player may unlock after all players have locked and resolution has begun.

If a pack contains fewer cards than the configured cards-per-pick count, all remaining cards are added to that player's collection automatically. No manual selection or lock-in is required for that pack. If the pack contains exactly the configured count, normal selection and locking still apply.

### 6.3 Privacy and Status

During the draft:

- A player's current selection is private.
- A player's collected cards are private.
- Players can see other players' lock and connection status.
- Players can see turn order and card counts.
- Information may naturally be inferred when a previously seen pack returns, but the application does not directly reveal private collections.

Each player sees their collected cards as smaller images across the bottom of the game screen.

### 6.4 Force Ready

Once the host has locked their own selection, the host sees a **Force Ready** control. This allows the host to progress a stalled turn.

Using **Force Ready**:

- Preserves every player's existing selections.
- Randomly selects only enough additional cards for each unlocked player to satisfy the required pick count.
- Locks all players.
- Immediately resolves the turn through the normal all-locked behavior.

This applies to connected and disconnected players alike.

### 6.5 Pack Rounds and Direction

Packs continue circulating until they are empty. Once all packs in the current round are exhausted, a new pack is generated for every player unless the configured number of packs per player has been completed.

For an alternating direction setting, the direction changes after each completed pack round. For example, **Start clockwise** passes the first round clockwise, the second counterclockwise, and so on.

## 7. Disconnections and Session Failure

If a client disconnects during a draft:

- The host is notified.
- The player's place and draft state are retained for reconnection.
- Until the player reconnects, the host can use **Force Ready** to make the player's required choices randomly.
- A reconnecting client identified by its persistent client ID resumes its existing player identity and state.

If the host disconnects or closes the hosting application, the session ends for all clients and they receive an error message. Host migration is not required.

## 8. Draft Completion

The draft ends after every player has completed the configured number of packs and all cards from the final pack round have been collected.

At completion:

- Players can continue viewing their own collected card images.
- Each player can copy only their own card list to the clipboard.
- The host can export the available card set as Cockatrice-compatible set XML.

The clipboard format is one card per line:

```text
# {CardName}
```

`CardName` is the Cockatrice-compatible metadata name when available and otherwise the image filename without its extension.

The Cockatrice XML represents the complete available card set from the selected directory. It does not encode draft results, card ownership, or individual player collections.

## 9. Returning to the Lobby

After a completed draft, the host may exit or reopen the lobby. When the host marks the lobby open, players on the completed-draft screen are offered a button to join it at their leisure.

Reopening the lobby starts a fresh draft lifecycle:

- Previous collections and in-progress draft state are cleared.
- Existing host settings may be retained.
- Known players and names may be retained while they remain connected or rejoin.
- All players begin unready.

## 10. Persistence and Scope

This iteration does not persist draft history, draft results, or collections after the application/session closes. Local persistence is limited to information needed for client identity and reconnect convenience.

The following are explicitly outside or deferred from this high-level design:

- Mobile and touch-optimized interaction.
- Joining a draft already in progress as a new player.
- Host migration.
- Persistent draft history or result storage.
- Exporting all players' drafted lists from the host.
- Encoding ownership or draft results in the Cockatrice set XML.
- The final `data.csv` schema.
- Choice of application platform, networking stack, transport, NAT traversal method, and overall technical architecture.
