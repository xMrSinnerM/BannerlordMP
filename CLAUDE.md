# BannerlordMP — notes for Claude sessions

Co-op campaign mod for **Mount & Blade II: Bannerlord 1.4.8**: 2+ players share one campaign map, each
with their own hero, clan and party. The owner tests on one Windows PC with two game windows (one
dedicated server, one client) and reports bugs with `BannerlordMP.log` uploads. Nobody else plays it
yet. Read `README.md` (player guide, shared/limitations tables) and `docs/ARCHITECTURE.md` (design)
before changing behaviour.

## Working rules

- Develop on branch `claude/mb2-multiplayer-campaign-mod-fxq40d`, push with
  `git push -u origin claude/mb2-multiplayer-campaign-mod-fxq40d`. PR #1 into `main` already exists; don't
  open new PRs unless asked.
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
   (currently 7) and tell the owner to update both PCs.
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
  slot, `hp`, `x:` xp, `f:` focus, `a:` attributes, `inf`). Client predicts and sends deltas; host applies
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

- v0.3.24 attack popup looping after a siege + crash on re-attack → client-side validation and grace period.
- v0.3.23 host menu asks which save to load (with dates). v0.3.22 menu remembered the starting save instead
  of the autosave; save before exit.
- v0.3.21 double food consumption; enemies never engaged players (AI-disabled parties aren't chased).
- v0.3.20 host crash in `AiVisitSettlementBehavior` after a battle; time skip only after battles.
- v0.3.19 equipment in the ledger, lord capture/release events, town markets.
- v0.3.16–18 single-player character creator, single-player-like starts, time skip, party protections.

## Known limitations and next steps

See README "Known limitations". Agreed plan after the owner's testing:
1. The player's own hero being captured (prisoner on every machine, escape/ransom, rejoining while captured).
2. Relations, companions (hire/dismiss), marriages applied on the host.
3. Prisoners moved to dungeons / sold to ransom brokers.
Later: raids and siege details, clan parties and caravans, quests, joint battles (largest), no world pause
when a playing host fights, headless server research, correct icons for mirrored parties, live kingdom
decisions screen.
