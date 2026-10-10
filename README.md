# BannerlordMP — co-op campaign for Mount & Blade II: Bannerlord

A Bannerlord module that lets two or more players share one campaign map. Each player controls their own
hero and party; the host's game runs the world.

> **Status: v0.3.25, early prototype. Untested in-game so far.** It compiles against the official
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
- **Time skip after battle.** When you leave the battle, your clock is behind the world. Your game jumps
  straight to the world's time and shows what happened meanwhile. As in single player, where a battle
  takes no time, your party eats no food, pays no wages and loses no troops to low morale for the time
  you spent in the battle. (`SkipTimeAfterBattles=false` in `config.ini` fast-forwards through it instead,
  16× by default.) A player who is offline doesn't use up food either.
- **The host is special.** The host's machine simulates the world, so while the **host** is in a battle
  the world pauses for everyone. To avoid that, run a **dedicated host**: a separate game instance (on
  another PC, or a second instance on yours) started with `mp.server`. Nobody plays on it, so it
  never pauses the world, and every player, you included, joins as a client.

## Install

1. Copy `artifacts/Modules/BannerlordMP` (see [Build](#build)) into `<Bannerlord>/Modules/`.
2. Enable **Bannerlord MP Campaign** in the launcher, below the official modules.
3. Harmony (the patching library) is included. If you also use the
   [Harmony mod](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006) for other mods, keep it
   above everything else in the load order.
4. Every player needs the same mod version and the same game version (1.4.8).

## Play

Everything is in the main menu.

**Host Co-op Campaign** first asks which save to load: every save with its date and in-game day, server
saves first (**BannerlordMP_Autosave** is the latest one, with every player hero in it), the one you used
last marked, or a new sandbox campaign. Then one screen shows every setting: world, mode (play on this PC or
dedicated server), server name, password, number of player heroes, and who can find it (Steam friends,
Steam invites, public, or LAN/IP only). Click a line to change it, then **Start server**. Your choices are
remembered. With
Steam, you're offered the invite dialog once the server is up.

**Join Co-op Campaign** opens the server browser: **Rejoin** your last server at the top, then Steam friends'
servers, public Steam servers, LAN servers, and **Direct connect** for an IP address. Accepting a Steam invite
connects straight away. Enter the server password if it has one, then:
- **Your hero** (your last one is listed first): enter its password and play.
- **Create a new hero**, two ways:
  - **Character creator (like single player)**: the game's own creation screens from a new sandbox
    campaign: culture, face and body, background and upbringing (skills, attributes, focus, traits),
    age, banner and clan name. When you confirm the last screen, that local campaign is closed again
    without saving, you rejoin the server automatically, and you choose your hero's password. The server
    builds the same hero in its world and starts it exactly like a new single-player game: the gear,
    gold, food and troops creation gave you, and nothing more.
  - **Quick create**: one screen for name, culture, gender and password. A plain start: random face
    from their culture, attributes 2, a recruit's skills and gear, 1000 gold, a little food, no troops.

  Either way the hero gets their own clan and a party near a town of their culture.

Your game then downloads the server's current world, loads it, and puts you in control of your hero.

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
| `mp.save` | Save the server's world now (as `BannerlordMP_Autosave`) |
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
| Towns and castles changing hands, wars, peace, clans joining or leaving kingdoms, heroes dying, lords taken prisoner or freed | Applied on every machine; changes *you* cause (taking a castle, joining a kingdom) go to the server first |
| Town and village markets | When you enter, your game gets the server's real stock, gold, prosperity and prices; when you leave, what you bought and sold goes back to the server, so the next player finds the market as you left it |
| Your own party and hero | Gold, troops, prisoners, items, the equipment your hero wears (battle and civilian), health, skill xp, attribute and focus points, clan renown and influence, kept in agreement with the server: it pays wages, eats food and heals; you buy, recruit, loot and level up |
| Sieges | A siege you start reaches the server and everyone sees it; the server's sieges (AI or other players) appear for everyone. Lifting or ending one is shared too |
| Being attacked | AI parties hunt players on the server; when one catches you, the battle starts on your machine |
| Battle results | Losses and destroyed parties applied to the real world |
| Your money | Your game computes your clan's daily wages and income exactly as single player does; the server never changes your gold on its own |
| Kingdom votes | When your kingdom must decide something, a pop-up asks for your vote (option, then how strongly to back it, with its influence cost; a ruler picks the outcome). The AI never votes for you: no answer means you abstain |
| Your party | Only you manage it. On the server, the AI doesn't upgrade your troops, buy or sell for you, recruit, or make troops desert because of a lord's wage limit. Desertion from low morale or an oversized party still happens, as in single player |
| Your clan's decisions | Only you make them. On the server, the AI cannot make your clan join or leave a kingdom, marry off your hero, declare war or make peace for your faction, propose kingdom decisions in your name, or replace your clan leader. Forced changes (a kingdom being destroyed) still happen |

## Known limitations

- **Kingdom decisions screen:** in your game it shows the decisions from when you joined. Vote through the
  pop-up the server sends instead (see above).
- **Not shared yet:**
  - two players trading in the same town at the same time each see the market as it was when they entered
    (both trades still count on the server)
  - workshops, tournaments and other settlement details beyond the market
  - siege progress details (siege engines, bombardment) beyond the siege itself
  - raids
  - quests
  - relations
  - marriages
  - companions joining or leaving
  - clan parties and caravans you create yourself
  - joint battles (players can't fight in the same battle or attack each other)
- **Mirrored parties are stand-ins.** A party the server spawns after you joined appears with the right
  name, clan, troops and position, but as a generic party (its map icon may look different).
- **Heroes in battle results:** deaths, captures and releases are shared. A *player's* hero being captured
  is not handled yet, and lords moved to or from a dungeon or sold to a ransom broker aren't either.
- **Steam features** (relay, lobbies, invites) depend on the game's own Steam integration delivering
  Steam callbacks to mods. That's untested; LAN and direct IP don't depend on it.
- **Character creator heroes** start near a town of their culture, not where single player would start
  them. The server keeps their values within what creation can give (attributes up to 10, skills up to
  150, focus up to 5, gold up to 5000, 20 troops of tier 3 or lower, 100 inventory items, no item worth
  more than 10000, renown up to 100), so a modified client can't send a rich or maxed-out hero.
  A player whose hero is captured, or has lost their party, can't join until that's handled.
- **The host in a battle** pauses the world for everyone, unless you use a dedicated host. The dedicated
  host still needs a full game window; a headless server is not possible yet.
- **The host's save list** gains a `BannerlordMP_Server` save, written each time someone joins, and a
  `BannerlordMP_Autosave` written every 5 minutes (`AutoSaveMinutes` in `config.ini`). Joined players'
  games don't autosave: the server's save is the real one. After the server has saved once, the host menu
  picks `BannerlordMP_Autosave` as the world next time, and "Exit to main menu" on the server saves first.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it works and the roadmap.
