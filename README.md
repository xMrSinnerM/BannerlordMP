# BannerlordMP — co-op campaign for Mount & Blade II: Bannerlord

A Bannerlord module that lets two or more players share one campaign map. Each player controls their own
hero and party; the host's game runs the world.

> **Status: v0.1, early prototype. Untested in-game so far.** It compiles against the official
> Bannerlord **1.4.8.119303** reference assemblies and the game-independent logic is unit tested, but
> nobody has played it yet. Expect desyncs; see [Known limitations](#known-limitations).

## How it plays

- **Shared time control.** Pause, play and fast-forward (UI buttons or Space) control one shared world
  clock. With `TimeArbitration=LastRequestWins` (default), anyone can change the speed for everyone. With
  `Consensus`, the world runs at the slowest speed any player on the map asked for, so anyone pausing
  pauses everyone.
- **Battles happen apart from the world.** When a client enters a battle (or any other 3D scene: town
  streets, taverns, arenas), the world keeps running for everyone else. The parties in that battle are
  frozen on the host until the result comes back.
- **Fast-forward after battle.** When you leave the battle, your clock is behind the world. Your game
  fast-forwards (16× by default) and replays what happened while you were away until it catches up.
- **The host is special.** The host's machine simulates the world, so while the **host** is in a battle
  the world pauses for everyone. To avoid that, run a **dedicated host**: a separate game instance (on
  another PC, or a second instance on yours) started with `mp.server`. Nobody plays on it, so it
  never pauses the world, and every player, you included, joins as a client.

## Install

1. Install [Harmony for Bannerlord](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006) (`Bannerlord.Harmony`).
2. Copy `artifacts/Modules/BannerlordMP` (see [Build](#build)) into `<Bannerlord>/Modules/`.
3. Enable **Bannerlord MP Campaign** in the launcher, below Harmony and the official modules.
4. Every player needs the same mod version and the same game version (1.4.8).

## Play

1. **Host:** start or load a campaign. Give each friend a hero who leads a party: in the clan screen
   (Parties tab), create a party led by a companion or clan member. Run `mp.heroes` in the console
   (Alt + ~) to see the hero ids. **Save**, and send the save file to your friends
   (`Documents/Mount and Blade II Bannerlord/Game Saves/`).
2. **Host:** run `mp.host` (or `mp.host <port>`; the default is 7777/UDP, forward it for internet play).
   For a dedicated host, run `mp.server` instead and leave that game window running. Every player then
   joins it with `mp.join`, each with their own hero.
3. **Each friend:** load that same save, then run `mp.join <host-ip> <hero_id>`. You take control of that
   hero; your world catches up to the host's.

| Command | What it does |
|---|---|
| `mp.host [port]` | Host the loaded campaign and play on it |
| `mp.server [port]` | Run this game as a dedicated world host (nobody plays here) |
| `mp.join <address> <hero_id> [port]` | Join a host, playing as `hero_id` |
| `mp.heroes` | List the clan heroes players can take |
| `mp.status` | Show the players, the shared speed and sync state |
| `mp.say <text>` | Chat |
| `mp.leave` | Leave or stop hosting |

Settings are in `Modules/BannerlordMP/config.ini` (player name, port, time mode, catch-up speed).
The log is `Modules/BannerlordMP/BannerlordMP.log`.

## Build

Requires the .NET SDK 8+. You don't need the game installed: the build uses the
`Bannerlord.ReferenceAssemblies` NuGet package.

```sh
dotnet build -c Release                  # builds everything and lays out artifacts/Modules/BannerlordMP
dotnet test                              # runs the core unit tests
dotnet build src/BannerlordMP -c Release -p:GameFolder="C:\path\to\Mount & Blade II Bannerlord"   # also installs into the game
```

To target another game version, change `GameVersion` in `src/BannerlordMP/BannerlordMP.csproj` to a
version published on NuGet, and update the `DependedModule` versions in `_Module/SubModule.xml`.

## Known limitations

What v0.1 synchronizes: the clock, party positions, which parties are destroyed, and the outcome of
battles a client fights (casualties, prisoners, the player's gold). Each client still runs its own copy of
the AI and economy, and those copies drift apart. Not synchronized yet:

- **Parties spawned after the session starts** (new bandits, caravans, lord parties) exist only on the
  machine that spawned them.
- **Settlements, economy, diplomacy, kingdoms, quests, relations, marriages, sieges and raids** are
  simulated separately on every machine.
- **Heroes in battle results:** deaths, captures and releases of heroes are not applied on the host
  (regular troops are).
- **Joint battles:** players cannot fight in the same battle or attack each other. Encounters with
  another player's party are blocked.
- **Joining** requires every player to load the same save by hand. The save is not transferred
  automatically.
- **The host in a battle** pauses the world for everyone, unless you use a dedicated host (`mp.server`).
  The dedicated host still needs a full game window. A headless server is not possible yet.
- **Joining players take a companion party inside the host's clan.** They don't get a clan of their own.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it works and the roadmap.
