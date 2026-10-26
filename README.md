# Gedonia Co-op

Online co-op for **Gedonia** (the original single-player RPG, Steam app 1114220 - not Gedonia 2).
A BepInEx 5 plugin that lets two or more players play the open world and story together,
over Steam (friends, invites, relay - no port forwarding) or in the home network (LAN).

Status: **playable alpha** (0.6.0). Tested with Steam build 16727998.

Unofficial fan project, not affiliated with or endorsed by the developer of Gedonia.
No game files are included; you need your own copy of the game.

## What is shared

- **Players:** your real character model, animations, looks and gear, weapons, arrows and spells,
  Steam name above the head, markers on world map, minimap and compass, sounds (steps, swings, hits).
- **Enemies:** the same enemies for everyone, one owner per enemy (hand-over when players split up),
  hits from everyone count, everyone gets the XP and their own loot.
- **Story:** the host's story counts. Dialogue answers and other story calls are run on every machine
  in the host's order; quests are repaired if a player falls behind; late joiners catch up.
- **Shared conversations:** players near an NPC can join a conversation (F) and vote on answers.
  An answer with a stat check (e.g. charisma 4) is open if one of the group meets it.
- **World:** chests, ore and herbs (taken once for all), world events, expeditions including their
  generated dungeons, travelling merchants and bards, time of day and weather, companions and summons,
  the Northern Kingdoms.
- **Dying:** respawn crystals in the villages instead of the reload-only death screen.
- Pause menu does not stop the game in co-op. Optional German translation (see below).

## Install (players)

Download the ZIP from the releases page, unpack it, close Gedonia and run `Install.cmd`.
The installer finds Gedonia through Steam, backs up your saves, downloads BepInEx 5.4.23.5 from its
GitHub release (SHA-256 checked) and copies the plugin. `Uninstall.cmd` removes exactly what it added.
Details: [installer/README.txt](installer/README.txt) (English), [installer/LIESMICH.txt](installer/LIESMICH.txt) (German).

Everyone needs the same mod version. Main menu: "Host co-op" / "Join co-op"; in game: the co-op
button in the top bar or F8.

## How it works

- Unity 2019.4 Mono game without any netcode; the plugin patches it with HarmonyX.
- Transport: Steam P2P through the game's own Steamworks.NET (lobbies, invites, rich presence),
  or plain UDP (port 7790, LAN discovery on 7791) with its own reliable channel.
- Remote players are puppets: a stripped clone of the player model driven by state, animator and
  appearance messages.
- Enemies, followers and world events use actor ownership with epochs: the host decides the owner,
  the owner simulates and publishes, everyone else mirrors and forwards hits.
- Story: dialogue answers and story triggers go through the host's ordered story log; every machine
  runs the game's own story function, so world state, quests and dialogue states stay identical.
- Save safety: each player keeps their own save; the host's world changes are sent to new players.

## Build

Requirements: Windows, Gedonia with BepInEx 5 installed (run the game once after installing BepInEx),
the .NET Framework 4 `csc.exe` that ships with Windows (C# 5).

```
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -GameDir "C:\path\to\Gedonia"
```

`scripts\package.ps1` builds the player ZIP into `dist\`.

## Automated tests

`scripts\test-local.ps1` starts two copies of the game on one PC (host and friend, UDP or a mock of the
Steam API) and drives them with test steps and bots (`-Scenario walk|fight|story|loot|event|expfull|
dialogue|...`). Each run logs what both sides saw (positions, animation match, ownership, story events,
votes) and takes screenshots. `scripts\check-ownership.ps1` checks that no enemy is simulated twice.

The second copy is made of hard links (`scripts\make-client-copy.ps1`); `scripts\set-product-name.ps1`
gives each copy its own Unity product name so test runs never touch the real saves or settings.
You need your own test save named `coop-test.es3` in `backups\`.

## German translation

The plugin can show the whole game in German (button "Deutsch" in the main menu). The translation
tables pair the game's own English texts with the German ones, so they are not in this repository;
`tools/TextExtract.cs` and the plugin's export steps rebuild the English side from a local copy.

## Known issues

- Steam overlay invites may not open when the game is started outside Steam; the in-game friend
  list (co-op menu) works instead.
- Story cutscenes and story teleports affect every player.
- Arena, Survival and Dungeon mode (separate main menu modes) are not tuned for co-op.
- Sounds of effects that only exist as visuals on one machine can be missing for the others.

## License

MIT, see [LICENSE](LICENSE). Gedonia and its assets belong to their owner.

Built with the [Co-op Anything](https://github.com/cranebeaker/coop-anything) playbook.
