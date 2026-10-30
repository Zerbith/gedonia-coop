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
  above your head - also the others' spell and combat effects (healing, elements, weapon
  trails).
- Group: co-op menu (F8) -> "Group" to invite others (accept/decline there). Whoever invites
  leads the group and can remove members; anyone can leave. Members are shown on the left
  with a health bar. The group decides: shared combat experience, who appears on the map and
  how strong enemies get.
- Maps: on the world map (M), minimap and compass your group members are a blue arrow with
  their name. If someone is far away, the arrow at the edge points their way.
  Someone in a dungeon is shown grey at the last place outside.
- Enemies: the same enemies for everyone. Hits count for everyone.
  With several members of one group nearby (150 m) enemies get stronger: +50 % health and
  +20 % damage per extra member, kept while the fight lasts. Set in gedonia.coop.cfg,
  section [Scaling] (the host's settings count).
- Combat experience is shared in the group: whoever of the group is within 100 m in the same
  world gets exactly the
  points of the player where the enemy dies. So you level at the same pace (a gap from
  before stays).
- Trading: co-op menu (F8) -> "Trade", pick a player nearby (15 m). Both put in items and
  gold and press "Confirm"; any change means both confirm again. All or nothing: if the
  connection drops, everyone gets their goods back (or the other's, if the trade had gone
  through). Quest items cannot be traded.
- Loot: everyone has their own. Everyone can loot every body once; it stays for you until
  YOU have looted it.
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
      The same goes for gold and items (e.g. a rope): one of you having it is enough, and
      only ONE pays - whoever started the conversation, else the first who has it.
      Hired mercenaries go to whoever paid.
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
- Cutscenes are watched together: when one begins, everyone sees "A cutscene is about to
  start" at the top with a ring. When ready, hold F until the ring is full (1.5 seconds;
  letting go earlier starts over) - it turns green. It starts only once ALL are ready (there
  is no time limit). While the question is open, F doesn't talk or loot. In the cutscene
  you see only your own character.
- Transformations (werewolf, blood demon, elemental forms) are seen by the others.


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
In German, NPC dialogues and the journal's quest texts are read aloud and the voiced cutscenes speak German (AI voices, folder
BepInEx\plugins\voice-de). Turn off or lower: <Gedonia folder>\BepInEx\config\gedonia.coop.cfg,
section [VoiceOver] (Enabled, Volume).


KNOWN LIMITS (test version)
---------------------------
- An in-game chat comes later.
- The "Uncharted Islands" cannot be reached in this game version.
- Arena, Survival and Dungeon mode (separate modes in the main menu) are not tuned for co-op -
  play the normal game ("Campaign / open world").
- Teleports from the story affect both players.


PROBLEMS?
---------
Please send this file to whoever gave you the mod:
   <Gedonia folder>\BepInEx\LogOutput.log
(after playing, close Gedonia first). Add a short note: what happened, when, who was host,
via Steam or home network.
