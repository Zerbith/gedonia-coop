GEDONIA CO-OP  -  Version {VERSION}  (test version)
===================================================

This mod lets you play Gedonia (the first one, not Gedonia 2) together - two or more
players. Two ways:
  - VIA STEAM: invite friends, also over the internet. No port forwarding, no IP addresses.
  - HOME NETWORK (LAN): everyone on the same network (e.g. at home on the same router).
    Runs directly between the PCs, without Steam servers.

This is a test version. Please read the notes about your saves first.


REQUIREMENTS
------------
- Everyone owns Gedonia on Steam (tested with Steam build 16727998).
- Everyone has the SAME version of this mod ({VERSION}).
- Via Steam: Steam is running and you are Steam friends.
- Home network: all PCs on the same network.


INSTALL
-------
1. Close Gedonia.
2. Unpack the folder from the ZIP file anywhere (e.g. on the desktop).
3. Double-click "Install.cmd" (same as "Installieren.cmd").
   The installer
     - finds Gedonia through Steam,
     - backs up your saves to Documents\Gedonia-Coop-Backups,
     - downloads the mod loader BepInEx 5.4.23.5 from GitHub (checksum verified),
     - copies the mod and the German texts into the game.
   Windows may ask whether the file may run: "More info" and "Run anyway".
   An older version of the mod is simply updated - no need to uninstall first.
4. Start Gedonia from Steam as usual.

If Gedonia is not found, start the installer from a command prompt like this:
   Install.cmd -GameDir "D:\path\to\Gedonia"

Uninstall: double-click "Uninstall.cmd". It removes exactly what the installer added
(BepInEx too, if the installer brought it). Your saves stay.


HOW TO PLAY TOGETHER
--------------------
Main menu:
  "Host co-op"  -> choose "Via Steam" or "In the home network (LAN)",
                   then load your save as usual.
  "Join co-op"  -> lists Steam friends who are hosting and games in your home network.
                   Click "Join", then load your save.
                   If the home network search finds nothing: type in the host's address
                   (shown in the host's co-op menu, e.g. 192.168.0.20).

In game: co-op button on the right of the top bar (or key F8):
  - status (connected? how many players? your home network address),
  - "Invite friends" (Steam host): friend list with "Invite",
  - "Leave co-op",
  - if you are not connected yet, you can also host or join here.
Steam invites can also be accepted in the Steam chat or with F9 (F10 = decline).

Everyone plays with their OWN save. The HOST's world and story count.


HOME NETWORK (LAN) - WINDOWS FIREWALL
-------------------------------------
The first time you host in the home network, Windows asks whether Gedonia may use the
network: allow "Private networks". Without that nobody can join.
Used ports: UDP 7790 (game) and 7791 (home network search).
Declined by mistake? Windows Settings > Privacy & security > Windows Security >
Firewall & network protection > "Allow an app through firewall" > tick Gedonia under "Private".


WHAT IS SHARED
--------------
- You see each other with your looks, gear, weapons, arrows and spells, and your Steam name
  above your head.
- Maps: on the world map (M), minimap and compass the others are a blue arrow with their
  name. If someone is far away, the arrow at the edge points their way.
  Someone in a dungeon is shown grey at the last place outside.
- Enemies: the same enemies for everyone. Hits count for everyone, everyone gets the
  experience, everyone has their own loot.
- Quests and dialogues: the result counts for everyone. When a quest is handed in, everyone
  gets the full reward.
- Shared conversations: when someone talks to an NPC, the others nearby get a note. Walk to
  the same NPC and press F to join. Then a click on an answer is a vote (votes are shown
  next to the answers):
    * If you all agree, the answer is taken at once.
    * Otherwise the vote of whoever started the conversation counts after 10 seconds.
      Change it in <Gedonia folder>\BepInEx\config\gedonia.coop.cfg, section
      [Dialogue], VoteSeconds (0 = only when you all agree).
    * An answer with a requirement (e.g. charisma 4) can be chosen if ONE of you meets it.
      Gold and items EVERYONE must have (the story takes them from everyone).
    * Whoever leaves the conversation is out; the others go on.
  Talking alone works as always.
- Chests, ore, herbs: what one takes is gone for the others.
- Time of day and weather are the same for everyone (the host's). If one sleeps, all sleep.
- Travellers (merchants, bards, sages, shamans) are in the same place for everyone.
- Sounds: footsteps, swings, hits and shouts of the other players and of the enemies are
  heard by everyone, from the right place.
- World events, expeditions, dungeons and the other worlds (Northern Kingdoms) are played
  together.
- Everyone sees the companions and summons.
- The pause menu (Esc) does not pause the game in co-op - the world goes on for the others.


DYING IN CO-OP
--------------
The villages have glowing respawn crystals. Stand in front of one, look at it and press F
(like talking to someone) to bind yourself to it. If you die in co-op, you get up again at
the last crystal you bound to after 10 seconds (without a crystal:
where you entered the world).
Crystals: Weystane, Steepport, Stawford, Fayreach, Korram, Ertoron, the explorers' camp,
Erynard, the Ironclad clan and the Sons of Thunder.


ABOUT YOUR SAVES - IMPORTANT
----------------------------
- In co-op the mod changes your save (quests from the host, shared chests).
  Best use a SEPARATE save (character) for co-op.
- The installer backs up your saves to Documents\Gedonia-Coop-Backups first.
  To restore: close Gedonia, copy the contents of a backup folder to
  %USERPROFILE%\AppData\LocalLow\Oleg Kazakov\Gedonia
- Steam Cloud syncs Gedonia saves. So restore a backup while Steam is running and start
  the game afterwards.


GERMAN TRANSLATION
------------------
The mod also brings a German translation: button "Deutsch" next to "English" at the bottom
left of the main menu. English stays the default.


KNOWN LIMITS (test version)
---------------------------
- An in-game chat comes later.
- The "Uncharted Islands" cannot be reached in this game version.
- Arena, Survival and Dungeon mode (separate modes in the main menu) are not tuned for co-op -
  play the normal game ("Campaign / open world").
- Cutscenes and teleports from the story affect both players.
- Sounds of spell/effect visuals may be missing for the others.


PROBLEMS?
---------
Please send this file to whoever gave you the mod:
   <Gedonia folder>\BepInEx\LogOutput.log
(after playing, close Gedonia first). Add a short note: what happened, when, who was host,
via Steam or home network.
