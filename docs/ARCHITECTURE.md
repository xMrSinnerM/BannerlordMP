# Architecture

## Projects

| Project | Target | Purpose |
|---|---|---|
| `src/BannerlordMP.Core` | netstandard2.0 | Game-independent: wire protocol, shared-clock arbitration, clock sync / catch-up, catch-up buffer. No TaleWorlds references, fully unit tested. |
| `src/BannerlordMP` | net472 | The Bannerlord module: entry point, LiteNetLib transport, host/client sessions, Harmony patches, console commands. |
| `tests/BannerlordMP.Core.Tests` | net8.0 | xUnit tests for the core. |

All direct calls into the TaleWorlds campaign API go through `Game/GameBridge.cs`, so changes after a game
update are mostly confined to that one file.

## Joining

```
Main menu (no campaign loaded)                      Server (campaign on the map)
  connect (Steam relay, LAN or IP) ───────────────► AuthChallenge(server name, password?, salt, nonce)
  Hello(server-password proof) ───────────────────► verify → SlotList(slots, cultures)
  ClaimSlot(slot, hero-password proof)  or
  CreateHero(name, culture, gender, salt, key,
             character sheet?) ───────────────────► verify / create hero + clan + party → save world
                                         ◄──────── JoinAccepted(hero, resume token, size, hash) + SaveChunks
  verify SHA-256, write BannerlordMP_Join.sav,
  disconnect, load it
On the map
  reconnect, Hello(resume token) ──────────────────► redeem token (single use, 10 min) → Welcome
  take control of the hero; normal session from here
```

- **Passwords** (`Core/Security/PasswordProof`): PBKDF2 (100k iterations, 16-byte salt) gives a key; a
  login sends HMAC-SHA256(key, nonce) over a fresh per-connection nonce, so a captured login can't be
  replayed. A new hero's key is derived on the client; the server stores only salt and key.
- **Slots** (`Core/Slots/SlotRegistry`) are stored per campaign in `Modules/BannerlordMP/Servers/<id>.slots`
  on the server, never in the save. Offline players' heroes stay frozen where they logged off.
- **Character creator** (`Ui/CharacterCreator`): the client leaves the server, presses the game's own
  Sandbox new-game button and lets the player go through every creation screen. A second after the map
  opens it copies the main hero into a `HeroSheet` (`Game/HeroSheetBridge`: name, clan name, banner,
  culture, gender, age, body properties, attributes, skills, focus, traits, level, perks, both equipment
  sets, gold, troops, party inventory, clan renown and influence), ends that campaign (saving is blocked while it runs) and reconnects with the same
  server password. The sheet rides along with `CreateHero`; the server clamps it (`HeroSheetRules`),
  creates the hero from a lord template, strips what the template brings (lord party roster, food, gold,
  renown) and then applies the sheet, so the hero starts like a new single-player game. Quick create
  applies a plain basic start instead. The slot keeps the sheet, so a hero the server
  lost is rebuilt the same way.
- **Transports** (`Net/`): `NetTransport` (LiteNetLib UDP, which also answers LAN discovery broadcasts)
  and `SteamTransport` (Steam Networking Sockets over Valve's relay). The server listens on both
  through `CompositeTransport`. Steam lobbies (`Steam/SteamService`) only advertise the server and
  carry the host's Steam id for the browser and invites.
- **World transfer** (`Core/Transfer`): the server saves the live world, then streams it in 48 KB chunks
  (paced, with backpressure on Steam) and checks a SHA-256 hash at the end.

## Authority model

The host's campaign is the real world. A joined client takes control of its hero and from then on its
campaign is a **mirror**: `WorldSimulationPatches` switch off its quarter-hourly, hourly, daily and
weekly campaign ticks and AI thinking, and every party except the player's own becomes a puppet (AI
off, holding still). Per-frame logic (the player's own movement, encounters, menus, dialogue) still runs.

The rule, as in BannerlordCoop's encounter design: **sync what actions produce, not the UI that
produced them.** Menus and dialogue run locally; their effects on the world travel as messages.

| Thing | Who decides | How it travels |
|---|---|---|
| World clock (speed and time) | Host (`TimeControlArbiter`) | `TimeState` at 2 Hz and on change |
| A client's own party movement | That client | `PartyState` at 10 Hz; the host teleports its puppet |
| Every other party's position | Host | `WorldSnapshot` (delta, full every 20th), smoothed by `PositionSmoother` |
| Parties appearing / disappearing | Host | `PartySpawned` / `PartyDestroyed`; clients create stand-ins (`WorldBridge.CreateMirrorParty`). Full snapshots self-heal: unknown ids → `PartyInfoRequest`, local extras missing twice → removed |
| Troops of parties near a player | Host | `PartyRoster` within 30 map units, when changed |
| Settlement owners, war, peace, clan ↔ kingdom, hero deaths | Host; a client proposes the ones its player caused | `WorldEvent`; every apply is idempotent, so the proposer can receive its own event back |
| The player's party and hero | Both, merged | Ledger (below) |
| AI attacking a player | Host detects, client fights | `EncounterRequest` → the client starts the encounter locally |
| A battle a client fights | That client | `BattleStarted` (host freezes the parties) → `BattleResult` (host applies enemy losses, destroys losers) |
| AI-vs-AI battles | Host | Clients can't start encounters that don't involve their own party |

### The ledger

A player's party is changed from both ends: the host's simulation pays wages, consumes food and heals;
the player buys, recruits, loots and levels up on their own machine. `WorldBridge.CaptureLedger` flattens
the party and hero into named counters (`g` gold, `m:`/`w:` troops and wounded, `p:`/`q:` prisoners,
`i:` items with modifiers, `hp`, `x:` skill xp, `f:` focus, `a:` attributes). `ClientLedger` (in Core)
sends local changes as numbered deltas; the host applies them, checking attribute and focus spending
against the hero's unspent points, and sends back its full state with the last sequence number it
applied. The client re-applies anything not yet acknowledged, so the player never sees their own action
undone and then redone. Reconciling waits while the player is in a battle.

## Shared time control

- `Campaign.SetTimeSpeed` (UI buttons) and the Space key are turned into a `TimeRequest`.
- Every other change the game makes to `Campaign.TimeControlMode` (menus auto-pausing, encounters
  stopping time) is blocked while a session is running. The session re-applies the shared speed each frame.
- `TimeControlArbiter` (host) turns requests plus player activities into one effective speed:
  - A player in a **mission** (or a conversation, if `DetachDuringConversations`) is *detached*: they
    can't change the clock and the world doesn't wait for them.
  - Menus and management screens are **not** detached; time keeps flowing there, as it does while
    waiting in a town in single player.
  - If the **host** is detached, the effective speed is Paused, because nothing simulates the world.
  - **Dedicated host** (`mp.server` / `DedicatedHost=true`): the host is not a player. It has no vote
    in consensus mode, the world stays paused while no client is connected, and its own party is
    parked and excluded from encounters, so the host never enters a mission.

## Separate battles and the time skip

```
client enters battle ──► ActivityChanged(Mission) + BattleStarted(party ids)
                         host: freeze those parties, world keeps running
                         client: campaign not ticking; snapshots/destroys → CatchUpBuffer
client finishes      ──► BattleResult (rosters, destroyed, gold)
                         host: apply, release frozen parties
client back on map   ──► clock N hours behind the host
                         SkipTimeAfterBattles (default): move the campaign clock straight to host time
                           (MapTimeTracker ticks), no upkeep for those days, replay the whole buffer
                         otherwise: TimeSyncController → CatchUp, UnstoppableFastForward × CatchUpMultiplier,
                           CatchUpBuffer.DrainUntil(localTime) replays world updates in order
                         within tolerance → FollowHost
```

While a player is in a battle or conversation, or offline, the host doesn't let their party eat or desert
from low morale (`PlayerPartyPatches.AwayUpkeep`), and the client skips that time's clan finances, so a
battle costs no campaign time, as in single player. The host also keeps its lord-party AI off players'
parties: no wage-limit desertion, auto-upgrades, food/horse buying, loot/prisoner selling or recruiting.

`TimeSyncController` extrapolates host time from the observed rate between updates, with hysteresis
(fall `CatchUpThresholdHours` behind to start catching up, get within `ToleranceHours` to stop). A client
that drifts ahead pauses until the host catches up. The same mechanism covers joining a host whose world
is ahead of the save the client loaded.

`CatchUpBuffer` merges position snapshots (newest position per party wins, and its size is bounded) but
keeps every party removal, in order.

## Roadmap

The order is chosen so that each step removes a limitation and makes the next one easier.

1. **In-game validation** with a dedicated host: menus, Steam and LAN join, slots and passwords,
   world transfer, time control, mirrored world, ledger, AI attacking players, battle results, catch-up.
2. **World replication: done in v0.3** (see above). Still open: settlement economies and markets,
   sieges and raids in progress, quests, relations, marriages, companions, equipment.
3. **Interactions as host-validated commands:** trade against the server's markets, recruiting from
   its notables, quests, dialogue outcomes, clan and kingdom actions, sieges and raids.
4. **Hero death and capture sync**, and respawning. (New heroes use the single-player character
   creator since v0.3.16.)
5. **Joint battles** (several players in one battle) using the multiplayer mission stack.
6. **Headless server (research).** The campaign map needs the game engine (map scene, navigation mesh),
   and TaleWorlds' headless dedicated server only ships the multiplayer modules. Loading the campaign
   modules into it might work or might hit a hard wall; until that's tried, the dedicated host is a
   normal game window.
