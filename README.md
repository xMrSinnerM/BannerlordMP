# BannerlordMP — co-op campaign for Mount & Blade II: Bannerlord

A Bannerlord module that lets two or more players share one campaign map. Each player controls their own
hero and party; the host's game runs the world.

> **Status: v0.3, early prototype. Untested in-game so far.** It compiles against the official
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

Everything is in the main menu.

**Host Co-op Campaign**
1. Pick a server name, a password (optional), how many player heroes the server allows, and who can
   find it: Steam friends, Steam invite only, Steam public, or LAN/direct IP only.
2. Choose **Play on this PC** or **Dedicated server**. On a dedicated server nobody plays and the world
   never pauses for battles.
3. Pick a save, or start a new sandbox campaign. The server starts once the world is on the map. With
   Steam, you're offered the Steam invite dialog.

**Join Co-op Campaign**
1. The server browser lists Steam friends' servers, public Steam servers and LAN servers, plus
   **Direct connect** for an IP address. Accepting a Steam invite connects straight away.
2. Enter the server password if it has one.
3. Pick your hero and enter its password, or **Create a new hero**: name, culture, gender and a hero
   password. A new hero gets their own clan, a party at a town of their culture, 20 troops and 5000 gold.
4. Your game downloads the server's current world, loads it, and puts you in control of your hero.

Nobody can play your hero without its password. The server never receives passwords in clear: it
stores only a salted, stretched key and checks a one-time proof at each login. Slots are kept on the
server in `Modules/BannerlordMP/Servers/`, not in the save, because the save is sent to every player.

**Steam vs direct IP.** Over Steam, connections go through Valve's relay: encrypted, no port
forwarding, and IP addresses stay hidden. Direct IP and LAN use UDP port 7777. Forward it for internet
play. That traffic is not encrypted (passwords are still never sent).

| Console command (Alt + ~) | What it does |
|---|---|
| `mp.host [port]` / `mp.server [port]` | Host the campaign that's already loaded (normal or dedicated), with settings from `config.ini` |
| `mp.join <address[:port]>` | Join by address from the main menu |
| `mp.invite` | Open the Steam invite dialog (host) |
| `mp.slots` / `mp.removeslot <n>` | List player heroes / free a slot (the hero stays in the world as an AI lord) |
| `mp.status` | Players, shared speed, sync state |
| `mp.say <text>` | Chat |
| `mp.leave` | Leave or stop hosting |

Defaults for the host menu, the port and time control live in `Modules/BannerlordMP/config.ini`.
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

## What is shared

The server is the only machine that runs the world. Each player's game is a mirror of it: their own
campaign simulation (AI, economy, spawning, diplomacy) is switched off, and the server sends them what
happens.

| Shared now | How |
|---|---|
| Clock and speed | Shared time control (see above) |
| Every party's position | Smoothed between updates |
| Parties appearing and disappearing | Bandits, caravans, lords, new player heroes... mirrored on every machine |
| Troops of parties near you | Kept accurate, so the army you attack is the real one |
| Towns and castles changing hands, wars, peace, clans joining or leaving kingdoms, heroes dying | Applied on every machine; changes *you* cause (taking a castle, joining a kingdom) go to the server first |
| Your own party and hero | Gold, troops, prisoners, items, health, skill xp, attribute and focus points, kept in agreement with the server: it pays wages, eats food and heals; you buy, recruit, loot and level up |
| Being attacked | AI parties hunt players on the server; when one catches you, the battle starts on your machine |
| Battle results | Losses and destroyed parties applied to the real world |

## Known limitations

- **Not shared yet:**
  - settlement economies and markets (prosperity, stock, prices, so buying doesn't empty the server's market)
  - sieges in progress
  - raids
  - quests
  - relations
  - marriages
  - companions joining or leaving
  - your equipment
  - clan parties and caravans you create yourself
  - joint battles (players can't fight in the same battle or attack each other)
- **Mirrored parties are stand-ins.** A party the server spawns after you joined appears with the right
  name, clan, troops and position, but as a generic party (its map icon may look different).
- **Heroes in battle results:** deaths, captures and releases of heroes are not applied on the host
  (regular troops are).
- **Steam features** (relay, lobbies, invites) depend on the game's own Steam integration delivering
  Steam callbacks to mods. That's untested; LAN and direct IP don't depend on it.
- **New heroes** use a random face from their culture's templates; there's no face editor yet. A
  player whose hero is captured, or has lost their party, can't join until that's handled.
- **The host in a battle** pauses the world for everyone, unless you use a dedicated host. The dedicated
  host still needs a full game window; a headless server is not possible yet.
- **The host's save list** gains a `BannerlordMP_Server` save, written each time someone joins.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it works and the roadmap.
