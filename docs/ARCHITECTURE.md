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
  CreateHero(name, culture, gender, salt, key) ───► verify / create hero + clan + party → save world
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
- **Transports** (`Net/`): `NetTransport` (LiteNetLib UDP, which also answers LAN discovery broadcasts)
  and `SteamTransport` (Steam Networking Sockets over Valve's relay). The server listens on both
  through `CompositeTransport`. Steam lobbies (`Steam/SteamService`) only advertise the server and
  carry the host's Steam id for the browser and invites.
- **World transfer** (`Core/Transfer`): the server saves the live world, then streams it in 48 KB chunks
  (paced, with backpressure on Steam) and checks a SHA-256 hash at the end.

## Authority model

The host's campaign is the real world. Clients load the same save, take control of their own hero
(`ChangePlayerCharacterAction`), and from then on:

| Thing | Who decides | How it travels |
|---|---|---|
| World clock (speed + time) | Host (`TimeControlArbiter`) | `TimeState` at 2 Hz and on change |
| A client's own party movement | That client | `PartyState` at 10 Hz → host teleports the puppet party |
| Every other party's position | Host | `WorldSnapshot` (delta, full every 20th) at `SnapshotRateHz` |
| Parties ceasing to exist | Host | `PartyDestroyed` |
| A battle a client fights | That client | `BattleStarted` (host freezes the parties) → `BattleResult` (host applies rosters, destroys losers) |
| AI-vs-AI battles | Host | Blocked on clients by the `StartPartyEncounter` patch |

Other players' parties are puppets on every machine. Their AI is disabled and other parties ignore them
(`GameBridge.Freeze`), so only the network moves them.

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

## Separate battles and fast-forward

```
client enters battle ──► ActivityChanged(Mission) + BattleStarted(party ids)
                         host: freeze those parties, world keeps running
                         client: campaign not ticking; snapshots/destroys → CatchUpBuffer
client finishes      ──► BattleResult (rosters, destroyed, gold)
                         host: apply, release frozen parties
client back on map   ──► TimeSyncController sees the clock N hours behind → CatchUp
                         local campaign runs UnstoppableFastForward × CatchUpMultiplier
                         CatchUpBuffer.DrainUntil(localTime) replays world updates in order
                         within tolerance → FollowHost
```

`TimeSyncController` extrapolates host time from the observed rate between updates, with hysteresis
(fall `CatchUpThresholdHours` behind to start catching up, get within `ToleranceHours` to stop). A client
that drifts ahead pauses until the host catches up. The same mechanism covers joining a host whose world
is ahead of the save the client loaded.

`CatchUpBuffer` merges position snapshots (newest position per party wins, and its size is bounded) but
keeps every party removal, in order.

## Roadmap

The order is chosen so that each step removes a limitation and makes the next one easier.

1. **In-game validation** with a dedicated host: menus, Steam and LAN join, slots and passwords,
   world transfer, time control, puppet movement, battle freeze, result, catch-up.
2. **World replication (the key step).** Clients stop running world AI and the host streams all of it:
   spawn and despawn parties by host command, settlement ownership, sieges, raids, wars and peace. This
   removes the drift between machines, which causes most of the remaining limitations.
3. **Interactions as host-validated commands:** trade, recruiting, quests, dialogue outcomes, and
   clan/kingdom actions.
4. **Hero death and capture sync**, respawning, and a face editor for new heroes.
5. **Joint battles** (several players in one battle) using the multiplayer mission stack.
6. **Headless server (research).** The campaign map needs the game engine (map scene, navigation mesh),
   and TaleWorlds' headless dedicated server only ships the multiplayer modules. Loading the campaign
   modules into it might work or might hit a hard wall; until that's tried, the dedicated host is a
   normal game window.
