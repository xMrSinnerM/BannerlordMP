# BannerlordMP — notes for Claude sessions

Co-op campaign mod for **Mount & Blade II: Bannerlord 1.4.8**: 2+ players share one campaign map, each
with their own hero, clan and party. The owner tests on one Windows PC with two game windows (one
dedicated server, one client) and reports bugs with `BannerlordMP.log` uploads. Nobody else plays it
yet. Read `README.md` (player guide, shared/limitations tables) and `docs/ARCHITECTURE.md` (design)
before changing behaviour.

## Working rules

- PRs #1 and #2 are merged into `main`. Start new work on a fresh branch from `main` and open a PR for it.
- Commit message trailer (exactly):
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: <link of the current session>
  ```
  Never put model names in code, commits or docs.
- BannerlordCoop (Bannerlord Together) may inspire ideas, but its license forbids copying its code.
- The owner is not a programmer: explain results in plain terms, say what was not tested in-game, and ask
  for `BannerlordMP.log` (in `Modules/BannerlordMP/`) when a bug report has no log.

## Build, test, release

Needs the .NET 8 SDK (`apt-get install -y dotnet-sdk-8.0` in a fresh container).

```sh
dotnet test tests/BannerlordMP.Core.Tests          # Core unit tests (60 at v0.3.24), must stay green
dotnet build src/BannerlordMP -c Release           # the mod; output staged in artifacts/Modules/BannerlordMP
```

Release (every fix the owner should try):
1. Bump the version in **three** places: `Directory.Build.props` (`<Version>`),
   `src/BannerlordMP/_Module/SubModule.xml` (`v0.3.x`), and the status line in `README.md`.
2. If any message layout changed: bump `ProtocolVersion` in `src/BannerlordMP.Core/Protocol/MessageCodec.cs`
   (currently 13) and tell the owner to update both PCs.
3. `rm -rf artifacts && dotnet build src/BannerlordMP -c Release`, then
   `(cd artifacts && zip -qr ../BannerlordMP-v0.3.x.zip Modules)` (zips are git-ignored; delete old ones).
4. Commit, push, and send the zip to the owner. Install = replace `Modules/BannerlordMP` in the game folder
   (Windows may need *Unblock-File* on the DLLs). `config.ini` is overwritten on install.

### Checking game APIs

The reference assemblies (`Bannerlord.ReferenceAssemblies` 1.4.8.119303) have no method bodies, so verify
every type/member before using it:

```sh
dotnet build tools/bldump -c Release               # after restoring src/BannerlordMP once
dotnet tools/bldump/bin/Release/net8.0/bldump.dll MobileParty PaymentLimit   # members of a type (filter optional)
dotnet tools/bldump/bin/Release/net8.0/bldump.dll "~CampaignBehaviors.AiBehaviors"  # list type names
```

Internal types (e.g. `MapTimeTracker`) need `AccessTools.TypeByName`; watch for ambiguous names across
TaleWorlds namespaces (`MetaDataExtensions` exists three times).

## Layout

- `src/BannerlordMP.Core` (netstandard2.0, no game references, unit tested): wire protocol (`Protocol/`,
  message types 1–32), time arbitration and clock sync (`Time/`), ledger and catch-up buffer (`Sync/`),
  slots and passwords (`Slots/`, `Security/`), save transfer, `HeroSheet` + `HeroSheetRules`.
- `src/BannerlordMP` (net472, the mod): `SubModule.cs` (menu entries, ticks), `Session/` (HostSession
  split into `.cs`/`.World.cs`/`.Decisions.cs`, ClientSession, JoinConnection), `Game/` (all calls into
  the game: GameBridge, WorldBridge, HeroSheetBridge, MpCampaignBehavior), `Patches/` (Harmony), `Ui/`
  (host/join menus, character creator flow), `Net/` (LiteNetLib UDP + Steam relay), `Steam/`.
- Bundled at runtime: `0Harmony.dll` 2.4.2, `LiteNetLib.dll` 1.3.1; Steamworks.NET is compile-only (the
  game ships its own).

## How it works (short)

- **Host-authoritative world.** The server's campaign is the real one. Clients load a downloaded copy, turn
  off its periodic simulation (`WorldSimulationPatches`, `PeriodicEventManagerPatches`) and mirror the
  host: snapshots for positions, `PartySpawned`/`PartyDestroyed`, nearby rosters, and `WorldEvent`s
  (settlement owner, war, peace, clan↔kingdom, hero killed/captured/released, siege start/end). Clients
  propose world events their player causes; the host applies and relays; every apply is idempotent.
- **Ledger** (`WorldBridge.CaptureLedger`/`ApplyLedgerDelta`, `Core/Sync/Ledger.cs`): the player's own
  party/hero as counters (`g` gold, `m:`/`w:` troops, `p:`/`q:` prisoners, `i:` items, `e:` worn gear per
  slot, `hp`, `x:` xp, `f:` focus, `a:` attributes, `inf`, `ren` renown, `lvl`/`tx` level and hero xp, `uf`/`ua` unspent points, `k:` chosen perks). Client predicts and sends deltas; host applies
  and returns state. Gold is player-authoritative (host undoes AI changes; client runs daily clan finances).
  Food is eaten only on the host (client consumption is blocked, it was counting twice).
- **Players' parties on the host** look like AI lord parties. Patches stop the host's AI from managing them
  (`PlayerPartyPatches`: no wage-limit desertion, auto-upgrades, buying/selling, recruiting, hourly AI
  planning) and from deciding for players' clans (`PlayerClanProtectionPatches`, `KingdomVotePatches`).
  Their AI stays enabled with no decisions so enemy AI can still target them.
- **Battles** are fought on the player's machine; the host freezes the involved AI parties and applies the
  `BattleResult`. Enemies catch players through `HostSession.CheckPursuits` → `EncounterRequest`; the client
  validates it (`WorldBridge.WhyNoEncounter`) and has a 45 s grace per attacker after an encounter.
- **Time**: shared speed (`TimeControlArbiter`), client follows host clock (`TimeSyncController`). After a
  battle/conversation the client jumps its clock to host time (`GameBridge.JumpToHours`) with no upkeep for
  the missed hours; small drift uses the gentle fast-forward catch-up.
- **Joining**: password challenge → slot list → claim (hero password) or create (quick create, or the
  single-player character creator run in a throwaway local sandbox, captured as a `HeroSheet`) → host saves
  `BannerlordMP_Server`, streams it → client loads it and reconnects with a one-time resume token.
- **Saves**: host autosaves `BannerlordMP_Autosave` every 5 min, on player leave, and before "Exit to main
  menu" (`HostExitPatches`). Host menu asks which save to load first. Slots live in
  `Modules/BannerlordMP/Servers/<campaignId>.slots`, never in the save.
- **Markets**: entering a town/village requests `MarketState`; leaving sends a `MarketChange` diff.

## Recent history (latest first)

- v0.3.39 gear given to a new wife (and her pregnancy) were gone after a rejoin: she joined the player's party before
  the marriage reached the server, so the server refused that move (not their clan yet) and then refused her gear
  changes ("not in their party here"), losing the items → the client resends the spouse's party move after the
  marriage, and `HostSession.BringIntoParty` puts a free clan member the player has with them into their party
  instead of refusing. Pregnancies: `WorldEvent`s Pregnant (33) / GaveBirth (34); newborns reach clients on rejoin.
- v0.3.38 a companion made governor of a town they weren't in never arrived: single player sends them travelling and
  finishes the trip on an hourly turn of `TeleportationCampaignBehavior`, which is off on clients →
  `StateSync.TickOwnTravellers` gives the player's own clan's travellers that turn (also party leaders sent from afar).
- v0.3.37 the remaining gaps: AI/other players' armies mirrored on clients and joinable (host sends an AI army's
  field battles to an attached online player, detaches them for settlement encounters, never lets the AI call
  players); tournaments (29/30); quests (31: host removes the taken issue; `StateSync.TickQuests` runs quest turns
  on clients); battles with friends (`ClientSession.Allies.cs`, 32: nearby allied player parties join the battle on
  the fighting player's machine under its AI, losses applied on the host). Protocol 13 still (enum values only).
- v0.3.36 the owner asked for everything a player does to reach the server. `Game/StateSync.cs` + watchers in
  `ClientSession.State.cs` / `HostSession.State.cs`, `WorldEvent`s 19–28, protocol 13: garrison/dungeon roster changes,
  fief settings (governor, build queue, daily project, wage limit, auto-recruit; host sends levels, loyalty, food...),
  kingdom policies, party roles, relations, marriages, looted villages, companions' development (ledger hero keys via
  `WorldBridge.CaptureHero`/`ApplyCompanionDelta`), workshop ownership, caravans (`StateSync.CreateCaravan`), mercenary
  contracts (ClanKingdom B carries "m" and the award multiplier). Workshop/caravan income is unverified.
- v0.3.35 a kingdom a player founded existed only in their game (the server's copy of the clan stayed kingdomless),
  so the server never made their army and the called companion party never came → `WorldEvent` KingdomCreated (18,
  protocol 12): the founder's game describes it, the host founds it with `KingdomManager.CreateKingdom`, gives it the
  same id (relays held while founding, `WorldBridge.Founding`), and relays it. Refused army creations/calls are logged.
- v0.3.34 companion clan parties still stood still: `LordPartyComponent.CreateLordParty` treats the id as a prefix and
  adds `_party_1`, so the server's copy got a different id than the client's (and the client's copies of new AI lords
  too); nothing matched up, army joins found no party → `WorldBridge.CreateLordParty` sets the exact id; the host renames
  a clan party created under the wrong id; resending a clan party resends the player's army after it.
- v0.3.33 the host's siege AI assaulted for a besieging player (their puppet had a besiege order): it fought the battle
  itself (hundreds of the player's troops lost), took the town/castle, and the owner change ended the player's own
  siege halfway on the client (NRE in `KingdomManager.SiegeCompleted`) → attack menu stuck, Leave dead, crash on Attack.
  `PlayerPartyPatches.NoSettlementEncounters` keeps players' parties out of settlement encounters on the host; the
  client ignores owner changes for a settlement it is besieging. Log showed money was garrison wages (-3038/day).
- v0.3.31 a clan party made in a town stayed inside that town on the client (snapshots never move parties in a
  settlement) → `WorldBridge.LeaveSettlementIfMovedAway` takes stand-ins out once the server has them elsewhere;
  the host enables the new clan party's AI explicitly and logs its state.
- v0.3.30 a companion's clan party stood still and couldn't join armies: the server refused to create it because
  the companion was hired before v0.3.29 (not in the clan there) → `CreatePlayerClanParty` hires a clanless leader
  first, and the client resends its own clan parties missing from full snapshots (`ResendOwnClanParty`, every 30 s).
- v0.3.32 chosen perks were never synced, so they were gone after a rejoin → `k:` ledger keys (protocol 11). The attack
  menu looping after a siege: puppets with an old chase order could still start encounters natively → the client only
  accepts AI attacks it started from an `EncounterRequest`. Daily upkeep log lines on both sides (food, gold, finance
  breakdown) for the "money and food drop fast" report, whose cause isn't found yet.
- v0.3.29 companions hired on a client vanished on rejoin (the server never heard of them) → `WorldEvent`s
  CompanionJoined/CompanionLeft/HeroJoinedParty (15–17), checked by `HostSession.MayChangeCompanion`. Protocol 10.
- v0.3.28 a player's own hero can be captured: their game runs single-player captivity (menus, escape, ransom
  offers), the host makes the hero the same captor's prisoner and keeps their party leaderless and inactive
  (`WorldBridge.CapturePlayerHero`, `CaptivityPatches` stop it disbanding), `HostSession.TickCaptivity` tells the
  player when they're moved (dungeon) and gives a freed hero their party back; joining while captive works
  (`WorldBridge.LeaveCaptivityForJoin`). Ledger uses `WorldBridge.PartyHero` (leader or kept-party owner). Protocol 9.
  Also merged v0.3.26 (PR #3) into this branch.
- v0.3.27 clan parties a player creates and their own armies stood frozen (the client's AI is off and the host never
  heard of them) → host creates them from a client `PartySpawned`; army `WorldEvent`s 10–14; protocol 8.
- v0.3.26 the host's AI spent players' focus/attribute points on level-up (`HeroDevelopmentPatches`), and unspent
  points/level weren't in the ledger, so points vanished or went negative → `lvl`/`tx`/`uf`/`ua` keys.
- v0.3.25 clan renown was never synced, so it reset to the server's copy on rejoin → `ren` ledger key.
- v0.3.24 attack popup looping after a siege + crash on re-attack → client-side validation and grace period.
- v0.3.23 host menu asks which save to load (with dates). v0.3.22 menu remembered the starting save instead
  of the autosave; save before exit.
- v0.3.21 double food consumption; enemies never engaged players (AI-disabled parties aren't chased).
- v0.3.20 host crash in `AiVisitSettlementBehavior` after a battle; time skip only after battles.
- v0.3.19 equipment in the ledger, lord capture/release events, town markets.
- v0.3.16–18 single-player character creator, single-player-like starts, time skip, party protections.

## Known limitations and next steps

See README "Known limitations". Agreed plan after the owner's testing:
1. ~~The player's own hero being captured~~ (v0.3.28, untested in-game). Companions captured with the player
   still aren't synced.
2. Relations and marriages applied on the host (companion hire/dismiss done in v0.3.29).
3. Prisoners moved to dungeons / sold to ransom brokers.
Later: raids and siege details, clan parties and caravans, quests, joint battles (largest), no world pause
when a playing host fights, headless server research, correct icons for mirrored parties, live kingdom
decisions screen.
