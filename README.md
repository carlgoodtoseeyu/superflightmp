# Superflight Co-op

Fly Superflight with friends: everyone in the same world, each player visible to the others with name and score.
A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for the Steam version (Unity 2017.2, Mono). Nothing in the
game's files is modified: BepInEx loads the plugin at start.

Status: **alpha**. Direct IP (host and join by address) works and is tested automatically with two real game copies
on one PC. Steam friend invites (no port forwarding) are the next step.

It builds on [Phlarfl's SFMF Multiplayer](https://github.com/Phlarfl/SFMF/tree/master/Multiplayer/Multiplayer) (2018),
whose design it keeps: a host picks the world, the others follow its seed, and every player is a puppet on the other
machines. Thanks to Phlarfl, and to vicjohnson for keeping [SFMF](https://github.com/vicjohnson/SFMF) alive.

## What is shared

| | |
|---|---|
| World | The host's seed. Joining puts you in the host's world; a portal on a client keeps you in it. When the host enters a portal everyone follows. |
| Players | Position and rotation at 20 updates/s, name, score, colour. Remote players are a copy of the wingsuit body, tinted, with a name tag. |
| Scores | Each player keeps their own run and score (shown on the tag). Steam leaderboard uploads are off while in a session, so modded runs never touch the boards. |
| Not shared | Nothing else: the game has no enemies, items or story. |

## Install

1. Download `BepInEx_win_x64_5.4.23.6.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6)
   and extract it into the folder that contains `superflight.exe` (Steam: right-click the game, Manage, Browse local files).
   SHA-256 of that zip: `6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755`.
2. Start the game once and close it. BepInEx creates its folders.
3. Put `SuperflightCoop.dll` into `BepInEx\plugins\SuperflightCoop\`.
4. Start the game. A "Superflight Co-op" window appears (F1 hides and shows it).

Every player installs the same way.

## Play

- **Host:** type a port (default 4444), click **Host (direct IP)**. Forward that UDP port on your router, or use a
  VPN such as Tailscale or ZeroTier, and tell your friends your address.
- **Join:** type `host:port` and click **Join**. You land in the host's world.
- Fly. Your friends' wingsuits show up with their name and score.

Settings are in `BepInEx\config\org.coopanything.superflight.cfg` (name, port, send rate, leaderboard block, UI).

## Build

Needs Visual Studio Build Tools (the Roslyn `csc.exe`) and a game folder with BepInEx installed.

```
.\build.ps1 -GameDir <game folder> -Deploy <game folder>
```

The plugin compiles against the game's own `.NET 3.5` assemblies and BepInEx's `0Harmony.dll`; no NuGet packages.

## Test

`tests/run_two.py` starts two private copies of the game (host and client), drives both into flight with an in-game
bot, and checks through the plugin's test bridge that they share a world, see each other, keep exchanging state and
really move. It prints `RESULT: PASS` or `FAIL` and writes `tests/last-run.json` and two screenshots.

```
python -I tests\run_two.py --instances <copy0> <copy1> --coop-anything <clone of cranebeaker/coop-anything>
```

The copies must be private test copies, never the Steam install (see `games/superflight` in coop-anything for how
they are made: own product name so saves and PlayerPrefs stay apart, `steam_appid.txt` so Steam does not relaunch the
real game). The bridge speaks coop-anything's `tools/bridge` protocol: `--coop-bridge PORT` or the config entry turns it on.

Command-line switches for automation: `--coop-host PORT`, `--coop-join HOST:PORT`, `--coop-name NAME`,
`--coop-bridge PORT`, `--coop-endpoint-file PATH`, `--coop-autoflight`, `--coop-bot`.

## Known issues

- Direct IP only for now; a friend outside your network needs a forwarded port or a VPN.
- Remote players have no wing animation; the body is a still copy of the wingsuit.
- A client that dies keeps its own run and score; there is no shared scoring mode yet.

## License

MIT. Superflight is by GrizzlyGames; this project is not affiliated with them and ships none of the game's files.
