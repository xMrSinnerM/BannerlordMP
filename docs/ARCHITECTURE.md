# Architecture

## Projects

| Project | Target | Purpose |
|---|---|---|
| `src/BannerlordMP.Core` | netstandard2.0 | Game-independent: wire protocol, shared-clock arbitration, clock sync / catch-up, catch-up buffer. No TaleWorlds references, fully unit tested. |
| `src/BannerlordMP` | net472 | The Bannerlord module: entry point, LiteNetLib transport, host/client sessions, Harmony patches, console commands. |
| `tests/BannerlordMP.Core.Tests` | net8.0 | xUnit tests for the core. |

All direct calls into the TaleWorlds campaign API go through `Game/GameBridge.cs`, so changes after a game
update are mostly confined to that one file.

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

1. **In-game validation of v0.1** (this is the next step): host/join, time control, puppet movement,
   battle freeze, result, catch-up.
2. **Save transfer on join:** the host sends its save to the client, which loads it automatically.
3. **World replication:** spawn and despawn parties by host command; stop clients from running world AI;
   sync settlement ownership, sieges, raids, wars and peace.
4. **Interactions:** trade, recruiting, quests, dialogue outcomes, and clan/kingdom actions, each sent as a
   host-validated command.
5. **Separate clans per player**, hero death and capture sync, and joint battles (players fighting in the
   same battle) using the multiplayer mission stack.
6. **Dedicated or headless host**, so the host player's battles no longer pause the world.
