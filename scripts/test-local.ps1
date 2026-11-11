# Two-copy local co-op test: host (game) stands still, client (game2) runs the
# bot (walk, attack, look change at 10 s, gear swap at 45 s). Both autoload
# coop-test.es3 from their own data folders (LocalLow\Oleg Kazakov\GedoniaH /
# GedoniaC) and save screenshots to BepInEx\coop-shots. Stop them with -Stop.
#   -Transport udp          plain UDP on 127.0.0.1:7777
#   -Transport steam-mock   Steam code path over MockSteam, 5% unreliable packet loss;
#                           the host invites from the F8 panel, the client accepts with F9
#   -Scenario walk          client walks, attacks the air, changes look and gear (default)
#   -Scenario fight         client's bot fights the nearest hostile enemy; the host
#                           stands still and owns the enemies near both of them
#   -ClientDelay <s>        start the friend this many seconds after the host
param([switch]$Stop, [ValidateSet('udp', 'steam-mock')] [string]$Transport = 'udp',
      [ValidateSet('walk', 'fight', 'story', 'loot', 'clock', 'apart', 'lossy', 'death', 'companions', 'event', 'latejoin', 'farloot', 'chestgold', 'chestlid', 'looks', 'bow', 'ui', 'worlds', 'dungeon', 'expedition', 'expfull', 'eventhandover', 'summons', 'restpoints', 'survey', 'menu', 'german', 'map', 'lan', 'respawn', 'respawnnorth', 'travellers', 'pause', 'dialogue', 'effects', 'party', 'scaling', 'trade', 'chat', 'voicemute', 'fixsurvey', 'mountride', 'spells', 'tooltips', 'woodevent', 'doors', 'doorsjoin', 'questloop', 'viewvoice', 'topbar', 'uistyle', 'uiskin', 'deathscreen', 'mountshare', 'tradefield', 'mountsurvey', 'crystalcamp', 'statues', 'respawnstatues', 'statuecheck', 'thunderspots', 'title', 'revive', 'spectate', 'ping', 'statuemenu', 'goldshare', 'groupchest', 'emotestats', 'cutskip', 'modfallback', 'voicesurvey', 'dungeondeath', 'atlas', 'atlasnorth', 'questrun', 'storyfix', 'escort', 'storyparts', 'northcheck', 'northlate')] [string]$Scenario = 'walk',
      [string]$NoFix = '',
      [ValidateSet('de-on', 'de-off', 'en-on', 'en-off')] [string]$VoiceCase = 'de-on',
      [int]$ClientDelay = 3, [string]$ExpPattern = '*|*|*',
      [string]$DialogSteps = 'dialogsurvey@15,gotovillage:Weystane@18,dialogsurvey@30', [string]$DialogClientSteps = 'none@999',
      [string]$QuestRun = 'all', [int]$QuestFrom = 0, [int]$QuestMax = 0, [ValidateSet('alternate', 'host', 'client')] [string]$QuestActor = 'alternate', [switch]$QuestNorth)
$root = Split-Path -Parent $PSScriptRoot
if ($Stop) { Get-Process Gedonia -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" } | Stop-Process -Confirm:$false; return }

# Fresh test saves every run: the copies may autosave test changes into them.
$pristine = "$root\backups\coop-test.es3"
if (-not (Test-Path $pristine)) { throw "missing $pristine" }
foreach ($p in 'GedoniaH', 'GedoniaC') {
    Copy-Item $pristine "$env:USERPROFILE\AppData\LocalLow\Oleg Kazakov\$p\coop-test.es3" -Force
}
foreach ($g in "$root\game", "$root\game2") {
    # questrun: both PCs read the same plan (tools\atlas\build-atlas.ps1 writes it).
    # -QuestNorth: the plan built from the Northern Kingdoms dump only (build-atlas.ps1 -Out build\atlas\north).
    $plan = if ($QuestNorth) { "$root\build\atlas\north\quest-plan.tsv" } else { "$root\build\atlas\quest-plan.tsv" }
    if ($Scenario -eq 'questrun') { Copy-Item $plan "$g\BepInEx\quest-plan.tsv" -Force }
    Remove-Item "$g\BepInEx\LogOutput.log", "$g\BepInEx\coop-lobby.txt" -ErrorAction SilentlyContinue
    Remove-Item -Recurse "$g\BepInEx\coop-shots" -ErrorAction SilentlyContinue
}

$w = '-screen-fullscreen 0 -screen-width 960 -screen-height 540'
$common = '-coop-autoload coop-test.es3 -coop-shots 8 -coop-animlog 1'
if ($NoFix) { $common += " -coop-test-nofix $NoFix" }
if ($Transport -eq 'udp') {
    $hostNet = '-coop-host 7777 -coop-bind 127.0.0.1'
    $clientNet = '-coop-connect 127.0.0.1:7777'
} else {
    # The host invites the client from the in-game F8 friends list; the client
    # accepts from the in-game prompt (F9). Player 3 is listed but never runs.
    $hostNet = '-coop-host steam -coop-mock-steam 1 -coop-mock-loss 0.05 -coop-mock-friends 2,3 -coop-test-invite 2'
    $clientNet = '-coop-connect steam -coop-mock-steam 2 -coop-mock-loss 0.05 -coop-mock-friends 1 -coop-test-accept 3'
}
# story: the friend accepts "Protect the town" by dialogue; the host gives the
# same answer a second later (must run once), then accepts "Get a horse", then
# adds "Lost knowledge" behind the sync's back (the friend must be repaired).
# The friend also accepts "A greatest hero", which only it answers.
# apart: the friend fights near the start; 50 s in, the host walks away, so
# the enemies there must move to the friend (check with check-ownership.ps1).
# lossy: the fight with 20 % of all UDP packets dropped on both sides.
# death: the friend fights without healing until it dies, then reloads.
# looks: the friend turns female, puts on a helmet and cloak, hides and shows them, wields a
# mod weapon nobody has and sends an unknown helmet model; the host hides its own helmet.
# bow: the friend fights with a bow.
# worlds: the friend lists the dungeon portals, travels to the Northern Kingdoms at 25 s and back at
# 80 s; the host stays. Puppets, follower mirrors, arrows, season and weather must follow the worlds.
# dungeon: both go through the 'Blackness' portal near the start (a dungeon placed far away in the
# same world, at (280, 101, 3000)); the friend's bot fights inside.
# expedition: the host gets expedition rank 1 and rolls the sites; the friend must take the host's list,
# both go to the first site with a dungeon, the friend starts it (the host must start the same one with
# the same layout), then both raise and enter its dungeon (same seed, same tiles, shared enemies).
# expfull: the host rolls until a site has three phases the test can play (objects, destructibles or
# a pack, a pack or boss); both go there, the friend starts it, then both play every phase through the
# game's own calls until the expedition is solved; both must get the reward.
# eventhandover: like event, but the host (the event's owner) leaves mid-waves at 70 s and comes
# back at 130 s: the friend must take the event over (spawns, phases) and both must get the reward.
# summons: the friend raises a skeleton and summons a cat mid-session, sends its companions away and
# back, its skeleton dies, it summons the cat again; the host summons its own cat. Mirrors must follow.
# restpoints: both have a test weapon mod installed; the friend holds a torch and wields the mod weapon
# (the host's puppet must load the mod model and light the torch), sneaks (light off) and stops (on),
# then casts spell projectiles (the host must show them).
# respawn: the friend goes to Weystane, touches its respawn crystal, fights at the start with 20 hp
# and dies: it must stand up at Weystane 10 s later. Then it loads its save (must get the host's world
# changes - the host emptied a herb spot - again); later the host loads its save (the others must get
# the entries it turned back).
# respawnnorth: the friend travels to the Northern Kingdoms, visits Erynard (touches its crystal), the
# Ironclad clan and the Sons of Thunder (screenshots of each crystal), dies: back at Erynard.
# ui (use -Transport steam-mock): the host keeps the F8 panel open for 60 s from 20 s in world and
# logs what the game sees; click on and next to it by hand (or with a desktop tool) meanwhile.
# chestgold: both at a real chest with 15 gold; the friend takes one item, then all.
# chestlid: both at a wooden chest with an opening animation; the friend opens it.
# farloot: the host teleports ~380 m away (the herb spot unloads there), the
# friend gathers the herb spot, the host comes back: the spot must be gathered.
# latejoin (use -ClientDelay 80): the host alone accepts two quests (one changes
# NPC dialogues) and empties the nearest herb spot; the friend joins later and
# must end with the same quests, dialogue entries and empty herb spot.
# event: both teleport to the 'Goblin attack' world event at (111, 1, 466).
# Phase 0 needs 4 fortifications built: the host builds 2, the friend 2 (its
# extra try at 55 s must find none left); then the friend's bot fights the
# goblin waves (phase 1) and the shaman (phase 2). Both log the event state.
# companions: both have two saved summons (each must see the other's as
# mirrors); the friend brings Buta back (only the friend may get him); the host
# calls a champion by story function (same id on both); the friend fights.
# clock: the friend's clock jumps 5 h (like another save) and must be set back;
# the friend sleeps 8 h and everyone must skip; the host changes the weather
# twice and the friend must follow.
# pause (use -ClientDelay 35): the host opens the pause menu alone (time must stop), the friend
# walks and opens it in co-op (time must go on, the menu stays open), then closes it.
# travellers: the friend moves its travellers away (like another save), both go to the bard #15 by
# their own tables (must be the host's: same place, the friend mirrors it); the host leaves at 60 s (the
# friend must walk the bard and report it), comes back at 100 s by its table (must find the bard there).
# scaling: host scales +50 % health / +20 % damage per extra group member; the two form a group, the
# friend fights (its hits taken must be scaled, both must show the same max health), leaves the group
# at 60 s (unhurt enemies go back to 1 player 10 s later, hurt ones keep their numbers).
# chat (UDP with 20 % of all packets dropped; the friend's bot keeps walking in a circle): both type
# messages through the real text field (host German, friend English); while the friend types it must
# stand still and the game must see no keys, afterwards it walks on. The friend sends a burst (must stay
# in order), a too long text (field limit) and drops a draft with Esc; the host sends a dirty message
# past its own cleaning (the friend must clean it). Fake keys on the host: Esc opens the pause menu, Enter
# doesn't open the chat over it, Esc closes the menu, Enter opens the chat, Esc doesn't reach the game while
# the field is open, the real Esc path closes it without a pause menu.
# Compare the [chatline] entries of both logs, [chatprobe] for the keyboard.
# trade (the friend drives, the host answers automatically: accepts, offers 1 item + 10 gold,
# confirms after the friend): 1) a normal exchange, 2) the friend uses up its offered item -> cancelled,
# 3) the friend never answers the commit -> host aborts after 20 s, both get their goods back,
# 4) the friend misses the outcome -> its escrow file asks the host again -> delivered. Compare the
# [tradecheck] stacks: both inventories together must stay the same.
# party: the host kills an enemy (no group: the friend gets no XP), invites the friend (accepts; both
# log the group and map), kills again (XP shared), the friend leaves (not shared), is invited again and
# removed by the host; then the friend invites the host, who declines.
# loot: the friend opens the nearest chest, takes one item, then the rest; the
# host tries to open the same chest in between and must be turned away.
# viewvoice (host alone, game's landscape option 2): moves the mod's real sliders. View distance 100 % and 250 %
# from the same spot (viewstate, fps, shots), voice-over at 100, 200, 300, 0 % (voicepeak: samples before/after
# the gain, 4 s of dialogue each), the cutscene at 200 %; shots of both option panels. Leaves the sliders at the defaults.
# voicemute (host alone; -VoiceCase <mod language>-<game's voices option>): talks to an NPC with greeting
# clips twice, makes citizens bark, opens a merchant, then plays Cutscene2, logging every one-shot.
# scripts/check-voicemute.ps1 runs all four cases and judges the logs.
# The next five answer the friends' bug list (0.9.2). -NoFix <names> switches a fix off for a control run:
# mountride (-NoFix mount): the friend summons a mount (seen unridden), rides it, rides forward, gets off, sends it away,
# then summons the other seven kinds. Compare [mountstate]: the copy under the puppet, equal weights on the rider's layers.
# spells (-NoFix projectile,handglow,enemyshots): the friend casts each element of the real spell routine, the host
# fires a shot from its nearest enemy; then the friend's bot fights (real enemy shots). Compare [handstate] (no hand
# glows on when the spell is over) and "projectiles" in [effects]; [projcopy] says how far each copy flew.
# tooltips (host alone, -NoFix tooltips): every item's tooltip through the tables; BepInEx\coop-tooltip-missing.tsv
# lists what stays English; the hover on the weapon slot on a screenshot.
# woodevent (-NoFix woodevent): both at 'Logging', the friend gets 16 wood and walks into the circle. The host owns
# the event. Compare the circle's "active" in [eventstate] on the friend after the end.
# doors (-NoFix doors): both at a house. F opens and closes for both; a race resolves to one state; the door
# stays open while the friend stands there after the opener left, and closes after the friend leaves too;
# a door changed while the friend's cell is unloaded is right when it comes back; the friend joining late gets the table.
# topbar (host alone): [barmeasure] logs the screen x of every widget in the top 160 px (frame mid must be the
# screen middle, the skill-points "+" under the notch), shot of the bar. Step atlasdump writes the game's UI atlas
# to BepInEx/coop-atlas (png + sprite rects) to pick icons from.
# title (host alone): menu credit/mod version and loading-screen credit; [menuwidget]/[loadwidget] dumps, shots Title_menu, Title_loading1/2.
# uistyle (host alone): [uistyle] logs sprites/fonts of the visible game UI (pause menu, inventory) to copy the look.
# uiskin: group, F8 menu, group window, trade (friend asks, host answers), chat and join window in the game skin;
# shots Host_skinmenu/skinparty/skinchat/skinframes/skinjoin, Client_skintrade/skinchatrecent.
# statuemenu: the friend opens the Weystane statue menu (F), a wrong respec (4/4/0/0) is refused, 3/2/2/1 is taken for
# the fee; the host goes to Stawford, the friend travels from the menu to the statue nearest the host. [statuestate].
# ping: the host presses G (mark in the middle of the screen); the friend sees it (Client_pingseen), turns away and sees
# it and the host as arrows on the edge (Client_pingedge), then pings back. Log: [ping], [pingstate].
# spectate: the friend steps 40 m away and falls: its camera follows the host (Client_spectate), still while the host
# walks over (Client_spectatenear), own camera after being helped up (Client_spectateback). Log: "Spectate:", [respawnstate].
# revive: the friend falls and lies down (30 s while a teammate lives), the host walks up and holds F 3 s -> the friend
# stands up on the spot with half health; falls again and presses Space -> respawn at the statue. Shots Client_down/
# helpedup/down2/gaveup, Host_helphint/helping. Log: [respawnstate], "Help: stood", "helped up by".
# deathscreen: the friend dies in co-op (death screen without buttons, countdown in the panel), leaves co-op
# and dies again (the game's buttons are back). Shots Client_deathscreen / Client_deathalone.
# atlas / atlasnorth: host alone visits every streamed cell (main world: teleport tour; Northern Kingdoms: celltour,
# the cells loaded by name - teleporting there loaded nothing after the test travel) and writes
# BepInEx\atlas\*.tsv (NPCs, answers, story triggers); tools\atlas\build-atlas.ps1 turns it into the quest atlas.
# questrun (-QuestRun "<quest>;<quest>"|all, -QuestFrom n, -QuestMax n, -QuestActor alternate|host|client): the quest
# runner walks the planned steps (build\atlas\quest-plan.tsv); scripts\questrun-report.ps1 writes the PASS/FAIL table.
# -QuestNorth: both travel to the Northern Kingdoms first and the runner starts there (for NK quests).
# storyfix (-NoFix storystale,storyrandom,storypaths for the old behaviour): at Buta both have 500 gold; the host takes
# "Right of passage" (answer 7 closes answer 0), the friend clicks the closed answer 0 (10 gold) 6 s later: the host
# must refuse it ([storysync] refused, friend keeps its gold). Then ShipwreckRandomLoot runs with the same roll on both
# ([storyrnd] next= equal).
# escort (-NoFix escorts): both at Ned ('Lost in Nightmare'); the host takes the quest and starts Ned's walk, only the
# friend follows him ([escort] logs). The host's copy must walk too (any player near), the completion must run once
# through the story log on both (queststate objective 0 done on both, "Escort ... reached its end" once per PC at most).
# northcheck (-NoFix worldguard): both travel north 15 s after start (the host is still loading): [worldlog] must show the
# Northern Kingdoms streamers with a player and cells loading on both ([worldguard] repairs it on the host).
# northlate (-NoFix worldguard): the same travel 70 s after start, when the world has loaded (what players do).
# storyparts (-NoFix storyparts): both stand at 'A greatest hero'; the host runs GetHeroQuest behind the story sync (its ! mark
# goes off, only the world entry changes): the friend's [npclog] 43017135 must show exclam=False at 75 s too.
# Friend as payer (-NoFix friendpurse): questrun -QuestRun 'Preying to the nature spirits' -QuestActor client - the
# friend lacks BarbF42's items, so the step must stay open on both (before: the host completed it).
# questloop (-NoFix questloop): both take 'Get a horse', the host finishes objective 0 behind the sync's back (as a story
# function reached only on one PC). The friend's repair must take once: compare objectiveShowsFromRepairs in [queststate].
$hostTest = if ($Scenario -eq 'dialogue') { "-coop-test-story $DialogSteps" }
            elseif ($Scenario -eq 'storyfix') { '-coop-test-regen 1 -coop-test-story gotopos:1096;12;345@40,gold:500@41,qclick:86759340:7@48,goldlog@50,answer:ShipwreckRandomLoot@62,goldlog@66,queststate:Right_of_passage@70' }
            elseif ($Scenario -eq 'northcheck') { '-coop-test-story worldlog@12,north@15,worldlog@45,worldlog@100,worldlog@180' }
            elseif ($Scenario -eq 'northlate') { '-coop-test-story worldlog@60,north@70,worldlog@110,worldlog@160' }
            elseif ($Scenario -eq 'escort') { '-coop-test-regen 1 -coop-test-story gotopos:1478;15;-1370@40,answer:LostInNightmareTake@50,answer:LostInNightmareSeers1@56,escortlog:67648142@58,queststate:Lost_in_Nightmare@330' }
            elseif ($Scenario -eq 'storyparts') { '-coop-test-story gotopos:667;26;192@40,npclog:43017135@58,localfn:GetHeroQuest@60,npclog:43017135@75' }
            elseif ($Scenario -eq 'questrun') { "-coop-test-regen 1 -coop-questrun $($QuestRun -replace ' ', '_') -coop-questrun-from $QuestFrom -coop-questrun-max $QuestMax -coop-questrun-actor $QuestActor" + $(if ($QuestNorth) { ' -coop-questrun-world NorthernKingdoms -coop-test-story north@15' } else { '' }) }
            elseif ($Scenario -eq 'atlas') { '-coop-test-regen 1 -coop-test-story worldtour@45' }
            elseif ($Scenario -eq 'atlasnorth') { '-coop-test-regen 1 -coop-test-story north@15,celltour@70' }
            elseif ($Scenario -eq 'dungeondeath') { '-coop-test-story lang:de@8,portal:0@20,respawnstate@40,die@45,shot:dungeondown@48,respawnstate@50,respawnstate@85,shot:dungeonrevived@87' }
            elseif ($Scenario -eq 'voicesurvey') { '-coop-test-story lang:de@8,voicesurvey:Big__x-6_z302;Giant__x-185_z60;Giant__x166_z198;BigN__x30_z-240@45' }
            elseif ($Scenario -eq 'modfallback') { '-coop-test-story modcheck@30,facefriend@40,shot:modfallback@42,modcheck@45' }
            elseif ($Scenario -eq 'cutskip') { '-coop-test-story lang:de@8,cutscene:Cutscenes/Cutscene2@45,cutready@47,cutstate@50,shot:cutwait@60,cutskip@70,cutstate@72,cutskip@79,cutstate@81,shot:cuthost@83' }
            elseif ($Scenario -eq 'emotestats') { '-coop-test-regen 1 -coop-test-story lang:de@8,facefriend@50,shot:emotedrink@53,emote:0@56,killnear@60,statsstate@66,statsopen@72,shot:stats@74,statsopen@76,statsstate@78' }
            elseif ($Scenario -eq 'groupchest') { '-coop-test-regen 1 -coop-test-story cheststate@24,cheststate@29,cheststate@38,chestput@40,cheststate@44' }
            elseif ($Scenario -eq 'goldshare') { '-coop-test-regen 1 -coop-test-story gotopos:1416;24;318@15,goldstate@38,goldstate@60,coopmenu@62,shot:goldmenu@64,coopmenu@66 -coop-test-loot look:-1525955765@1' }
            elseif ($Scenario -eq 'statuemenu') { '-coop-test-regen 1 -coop-test-story lang:de@8,gotovillage:Stawford@40,statuestate@45,statuestate@72' }
            elseif ($Scenario -eq 'ping') { '-coop-test-regen 1 -coop-test-story lang:de@8,ping@50,pingstate@51,shot:pinghost@52,pingstate@58' }
            elseif ($Scenario -eq 'spectate') { '-coop-test-regen 1 -coop-test-story lang:de@8,respawnstate@50,gotofriend@58,holdhelp@63,respawnstate@68' }
            elseif ($Scenario -eq 'revive') { '-coop-test-regen 1 -coop-test-story lang:de@8,gotofriend@52,respawnstate@53,shot:helphint@54,holdhelp@55,shot:helping@57,respawnstate@60,gotofriend@74,respawnstate@76,respawnstate@100' }
            elseif ($Scenario -eq 'questloop') { '-coop-test-story answer:TakeHorseQuest@42,queststate:Get_a_horse@50,questobj:Get_a_horse:0@54,queststate:Get_a_horse@58,queststate:Get_a_horse@110' }
            elseif ($Scenario -eq 'fixsurvey') { '-coop-test-story lang:de@8,eventdump@44,tooltipaudit@46,gotopos:701;33;616@48,doorlist@60' }
            elseif ($Scenario -eq 'tooltips') { '-coop-test-story lang:de@8,tooltipaudit@44,window:OpenInventory@48,tooltipshow@52,shot:tooltip@54,window:OpenInventory@56' }
            elseif ($Scenario -eq 'mountride') { '-coop-test-story lang:de@8,facefriend@48,mountstate@49,facefriend@54,shot:mountidle@55,mountstate@60,facefriend@61,shot:mountriding@62,mountstate@70,facefriend@71,shot:mountmoving@72,mountstate@80,mountstate@85,mountstate@90,mountstate@100,mountstate@108,mountstate@116,mountstate@124,mountstate@132,mountstate@140,mountstate@150' }
            elseif ($Scenario -eq 'spells') { '-coop-test-regen 1 -coop-test-story facefriend@45,handstate@49,shot:castseen@52,shot:castseen2@53,handstate@54,effectssum@56,handstate@62,handstate@72,handstate@82,effectssum@90,enemyshot@92,enemyshot@94,enemyshot@96,effectssum@100,effectssum@160' }
            elseif ($Scenario -eq 'woodevent') { '-coop-test-regen 1 -coop-test-story lang:de@8,gotoevent:Logging@42,eventstate:Logging@50,eventstate:Logging@60,eventstate:Logging@68,eventstate:Logging@74,shot:woodafter@76,eventstate:Logging@85' }
            elseif ($Scenario -eq 'doors') { '-coop-test-regen 1 -coop-test-story gotopos:701;33;616@42,nocompanions@50,doorlist@58,gotodoor@60,doorstate@62,dooruse@64,doorstate@66,doorstate@70,doorstate@74,doorrace:open@76,doorstate@92,doorbesides@96,doorforce:open@98,doorwalk:6@100,doorexit@107,doorstate@112,doorstate@118,doorstate@132,gotodoor@136,dooruse@150,doorstate@152,doorstate@175,doorstate@188,gotopos:701;33;640@190,doorstate@196,doorstate@206' }
            elseif ($Scenario -eq 'doorsjoin') { '-coop-test-story gotopos:701;33;616@42,nocompanions@50,gotodoor@60,dooruse@64,doorstate@66,doorstate@100,doorstate@130' }
            elseif ($Scenario -eq 'thunderspots') { '-coop-test-story north@36,gotoxz:-1080;1245@70,gotocrystal@88,facecrystal@89,shot:t0@91,gotoxz:-1076;1232@93,face:0@96,shot:t1@98,gotoxz:-1062;1244@100,face:270@103,shot:t2@105' }
            elseif ($Scenario -eq 'statues') { '-coop-test-story gotovillage:Weystane@44,statuesurvey@60,gotopos:730;31;632@62,face:0@70,shot:angel@72' }
            elseif ($Scenario -eq 'crystalcamp') { "$(if ($NoFix -eq 'survey') { '-coop-test-crystalsurvey 1 -coop-test-crystalwait 15 ' })-coop-test-story gotovillage:Explorer's_camp@44,gotocrystal@66,crystalstate@67,facecrystal@68,shot:camp1@70,face:0@71,shot:camp2@73,face:180@74,shot:camp3@76" }
            elseif ($Scenario -eq 'mountsurvey') { '-coop-test-story mountlist@44,gotovillage:Steepport@46,mountlist@66,gotovillage:Stawford@68,mountlist@88,gotovillage:Fayreach@90,mountlist@110,gotovillage:Korram@112,mountlist@132,gotovillage:Ertoron@134,mountlist@154' }
            elseif ($Scenario -eq 'tradefield') { '-coop-test-story tradeauto@8' }
            elseif ($Scenario -eq 'mountshare') { '-coop-test-story lang:de@8,nocompanions@40,mountsummon:0@44,mountstate@47,mountstate@58,facefriend@63,shot:lent@64,mountstate@66,mountstate@80,mountnear@84,mountride@86,mountstate@89,mountgo:1@94,mountgo:0@98,camyaw:90@99,shot:twoseat@100,camyaw:180@101,shot:twoseat2@102,mountstate@103,mountgo:0@104,mountstate@106,mountoff@112,mountstate@116,mountstate@120' }
            elseif ($Scenario -eq 'uiskin') { '-coop-test-story lang:de@8,partyinvite@46,coopmenu@52,shot:skinmenu@54,coopmenu@56,partyui@58,shot:skinparty@60,partyui@62,tradeauto@63,chatopen@90,chattype:Hallo_aus_dem_Spiel-Skin@91,chatenter@93,chatopen@95,shot:skinchat@97,chatesc@99,shot:skinframes@102,lanlist@104,shot:skinjoin@107' }
            elseif ($Scenario -eq 'uistyle') { '-coop-test-story lang:de@8,shot:world@44,uistyle@45,window:OpenPauseMenu@47,shot:pause@49,uistyle@50,window:OpenPauseMenu@52,window:OpenInventory@54,shot:inventory@56,uistyle@57,window:OpenInventory@59,coopmenu@61,shot:coopmenu@63,coopmenu@65,partyui@66,shot:party@68,partyui@70,chatopen@72,shot:chat@74,chatesc@76' }
            elseif ($Scenario -eq 'topbar') { '-coop-test-story lang:de@8,barmeasure@44,shot:topbar@46' }
            elseif ($Scenario -eq 'viewvoice') { '-coop-test-landscape 2 -coop-test-voices 1 -coop-test-story lang:de@8,viewstate@46,face:0@47,fps:5@48,shot:view100a@54,face:180@55,shot:view100b@57,optionslide:view|1@60,viewstate@62,viewstate@75,viewstate@90,viewstate@105,viewstate@120,face:0@121,fps:5@122,shot:view250a@128,face:180@129,shot:view250b@131,optionslide:view|0@134,viewstate@136,viewstate@150,gotopos:701;33;616@152,voicepeak@164,talk:41341829@166,voicepeak@172,leavedialog@173,optionslide:voice|0.6667@174,talk:41341829@176,voicepeak@182,leavedialog@183,optionslide:voice|1@184,talk:41341829@186,voicepeak@192,leavedialog@193,optionslide:voice|0@194,talk:41341829@196,voicepeak@202,leavedialog@203,optionslide:voice|0.6667@204,cutscene:Cutscenes/Cutscene2@206,voicepeak@234,optionslide:voice|0.3333@270,window:OpenPauseMenu@272,optionsshow:1@274,shot:optvideo@276,optionsshow:2@278,shot:optaudio@280' }
            elseif ($Scenario -eq 'voicemute') { "-coop-test-voices $(if ($VoiceCase -like '*-on') { 1 } else { 0 }) -coop-test-story lang:$($VoiceCase.Substring(0, 2))@8,gotopos:701;33;616@44,audiolog:on@60,talk:41341829@62,leavedialog@66,talk:41341829@68,leavedialog@72,bark@74,merchanttalk@78,merchantanswer@82,cutscene:Cutscenes/Cutscene2@88" }
            elseif ($Scenario -eq 'scaling') { '-coop-test-regen 1 -coop-scale-health 0.5 -coop-scale-damage 0.2 -coop-test-story scalelog@14,partyinvite@16,partyinvite@24,scalelog@40,scalelog@60,shot:scalehost@62,scalelog@85,scalelog@110' }
            elseif ($Scenario -eq 'chat') { '-coop-udp-loss 0.2 -coop-test-story lang:de@8,chatprobe@44,chatopen@46,chattype:Hallo_vom_Host@47,chatumlauts@49,shot:chatopen@51,chatprobe@52,chatenter@53,chatlog@55,chatbad@80,chatfake:esc@100,chatprobe@102,chatfake:enter@103,chatprobe@104,chatfake:off@105,chatfake:esc@106,chatprobe@109,chatfake:enter@110,chatprobe@112,chatfake:esc@113,chatprobe@115,chatfake:off@116,chatesc@117,chatprobe@120,chatlog@125,shot:chathistory@126' }
            elseif ($Scenario -eq 'trade') { '-coop-test-story tradeauto@8,tradelog@12,tradelog@45,tradelog@60,tradelog@95,tradelog@150' }
            elseif ($Scenario -eq 'party') { '-coop-test-regen 1 -coop-test-story partylog@14,killnear@16,partyinvite@20,partylog@30,maplog@30,partyui@31,shot:partyhost@33,partyui@35,killnear@40,partylog@55,maplog@55,killnear@58,partyinvite@62,partykick@70,partylog@75,partydecline@82,partylog@86,xplog@88' }
            elseif ($Scenario -eq 'effects') { '-coop-test-regen 1 -coop-test-story intellect:2@14,xplog@15,effect:33@25,effect:7on@28,shot:effects@29,effectstate@30,shot:fight1@50,shot:fight2@52,xplog@60,bodies@85,lootbody@88,bodies@92,xplog@100,effectstate@100' }
            elseif ($Scenario -eq 'pause') { '-coop-test-story pause@15,timescale@17,timescale@19,unpause@20,timescale@22' }
            elseif ($Scenario -eq 'travellers') { '-coop-test-story travellers@12,gototraveller:15@15,travellers@30,shot:bard@40,travellers@50,gotopos:486;15;474@60,travellers@90,gototraveller:15@100,travellers@110,shot:bardback@112' }
            elseif ($Scenario -eq 'story') { '-coop-test-story answer:TakeProtectTownQuest@36,answer:TakeHorseQuest@45,unsynced:Lost_knowledge@55' }
            elseif ($Scenario -eq 'respawn') { '-coop-test-story answer:TakeHorseQuest@25,reloadsave@95' }
            elseif ($Scenario -eq 'lan') { '-coop-test-menu 1 -coop-test-story lanhost@12,coopmenu@40,shot:lanmenu@42,coopmenu@44' }
            elseif ($Scenario -eq 'map') { '-coop-test-story maplog@18,shot:near@18,compass:on@66,face:45@66,shot:compass@68,maplog@68,compass:off@69,shot:minimapfar@70,maplog@70,window:OpenWorldMap@72,shot:worldmap@75,maplog@75,window:OpenWorldMap@77,window:OpenWorldMap@95,shot:worldmapdungeon@98,maplog@98,window:OpenWorldMap@100' }
            elseif ($Scenario -eq 'survey') { '-coop-test-story mapdump@15,window:OpenWorldMap@18,shot:worldmap@21,window:OpenWorldMap@23' }
            elseif ($Scenario -eq 'german') { '-coop-test-menu 1 -coop-test-story coopmenu@14,shot:coopmenu@16,coopmenu@18,textexport@20,window:OpenInventory@22,shot:OpenInventory@25,window:OpenInventory@27,window:OpenCharacterSheet@30,shot:OpenCharacterSheet@33,window:OpenCharacterSheet@35,window:OpenSkillsWindow@38,shot:OpenSkillsWindow@41,window:OpenSkillsWindow@43,window:OpenJournal@46,shot:OpenJournal@49,window:OpenJournal@51,window:OpenWorldMap@54,shot:OpenWorldMap@57,window:OpenWorldMap@59,window:OpenRepWindow@62,shot:OpenRepWindow@65,window:OpenRepWindow@67,window:OpenPauseMenu@70,shot:OpenPauseMenu@73,window:OpenPauseMenu@75,window:OpenOptions@78,shot:OpenOptions@81,window:OpenOptions@83,lang:en@86,shot:english@89,lang:de@91,shot:german@94' }
            elseif ($Scenario -eq 'menu') { '-coop-test-menu 1 -coop-test-story coopmenu@15,shot:coopmenu@17,coopmenu@20,uidump@22,textexportall@24' }
            elseif ($Scenario -eq 'title') { '-coop-test-menu 1 -coop-test-titledump 1' }
            elseif ($Scenario -eq 'restpoints') { '-coop-test-look fakemod@8 -coop-test-story modcheck@30,modcheck@42,modcheck@52' }
            elseif ($Scenario -eq 'summons') { '-coop-test-story summon:cat@30' }
            elseif ($Scenario -eq 'eventhandover') { '-coop-test-story gotopos:111;5;476@20,useevent:2@40,gotopos:486;15;474@70,gotopos:111;5;476@130 -coop-test-events 1 -coop-test-regen 1' }
            elseif ($Scenario -eq 'expfull') { "-coop-test-story exprank:1@36,exproll:$ExpPattern@38,expgo:auto@44,expauto@62 -coop-test-events 1 -coop-test-regen 1" }
            elseif ($Scenario -eq 'expedition') { '-coop-test-story exprank:1@15,explist@20,expgo:dungeon@25,expdungeon@65,explist@90 -coop-test-events 1 -coop-test-regen 1' }
            elseif ($Scenario -eq 'dungeon') { '-coop-test-story portal:0@20 -coop-test-regen 1' }
            elseif ($Scenario -eq 'ui') { '-coop-test-ui 20' }
            elseif ($Scenario -eq 'looks') { '-coop-test-look helmet@20,hidehelmet@30' }
            elseif ($Scenario -eq 'loot') { '-coop-test-loot open@44' }
            elseif ($Scenario -eq 'clock') { '-coop-test-clock weather@55,weather@75' }
            elseif ($Scenario -eq 'apart') { '-coop-bot line -coop-bot-delay 50' }
            elseif ($Scenario -eq 'lossy') { '-coop-udp-loss 0.2' }
            elseif ($Scenario -eq 'companions') { '-coop-test-story answer:ArenaCallChampion@60 -coop-test-charisma 2 -coop-test-regen 1' }
            elseif ($Scenario -eq 'event') { '-coop-test-story gotopos:111;5;476@44,useevent:2@64 -coop-test-events 1 -coop-test-regen 1' }
            elseif ($Scenario -eq 'farloot') { '-coop-test-story gotopos:111;5;476@20,gotopos:486;15;474@75 -coop-test-loot look:280705368@1' }
            elseif ($Scenario -eq 'chestgold') { '-coop-test-regen 1 -coop-test-story gotopos:1416;24;318@15 -coop-test-loot look:-1525955765@1' }
            elseif ($Scenario -eq 'chestlid') { '-coop-test-regen 1 -coop-test-story gotopos:172;50;520@15 -coop-test-loot look:-281711968@1' }
            elseif ($Scenario -eq 'latejoin') { '-coop-test-story answer:TakeProtectTownQuest@30,answer:TakeHorseQuest@35 -coop-test-loot open@40,takeall@46' } else { '' }
$h = Start-Process "$root\game\Gedonia.exe" -WorkingDirectory "$root\game" -PassThru -ArgumentList "$w $hostNet -coop-name Host $common -coop-dump 1 $hostTest"
Start-Sleep $ClientDelay
# Cutscenes wait for F from everyone, so this one runs without a friend.
if ($Scenario -eq 'voicemute' -or $Scenario -eq 'viewvoice' -or $Scenario -eq 'topbar' -or $Scenario -eq 'uistyle' -or $Scenario -eq 'mountsurvey' -or $Scenario -eq 'crystalcamp' -or $Scenario -eq 'statues' -or $Scenario -eq 'thunderspots' -or $Scenario -eq 'fixsurvey' -or $Scenario -eq 'tooltips' -or $Scenario -eq 'title' -or $Scenario -eq 'voicesurvey' -or $Scenario -like 'atlas*') { "transport=$Transport scenario=$Scenario case=$VoiceCase host=$($h.Id)"; return }
$clientBot = if ($Scenario -eq 'dialogue') { "-coop-test-story $DialogClientSteps" }
             elseif ($Scenario -eq 'questrun') { '-coop-test-regen 1' + $(if ($QuestNorth) { ' -coop-test-story north@15' } else { '' }) }
             elseif ($Scenario -eq 'storyfix') { '-coop-test-regen 1 -coop-test-story gotopos:1093;12;341@40,gold:500@41,goldlog@50,qclick:86759340:0@54,goldlog@60,goldlog@66,queststate:Right_of_passage@70' }
             elseif ($Scenario -eq 'northcheck') { '-coop-test-story worldlog@12,north@15,worldlog@45,worldlog@100,worldlog@180' }
             elseif ($Scenario -eq 'northlate') { '-coop-test-story worldlog@60,north@70,worldlog@110,worldlog@160' }
             elseif ($Scenario -eq 'escort') { '-coop-test-regen 1 -coop-test-story gotopos:1475;15;-1374@40,escortfollow:67648142@57,escortlog:67648142@58,queststate:Lost_in_Nightmare@330' }
             elseif ($Scenario -eq 'storyparts') { '-coop-test-story gotopos:662;26;192@40,npclog:43017135@58,npclog:43017135@75' }
             elseif ($Scenario -eq 'dungeondeath') { '-coop-test-regen 1 -coop-test-story lang:de@8,respawnstate@60,respawnstate@120' }
             elseif ($Scenario -eq 'modfallback') { '-coop-test-look fakemod@8,modweapon@20 -coop-test-story modcheck@30,modcheck@45' }
             elseif ($Scenario -eq 'cutskip') { '-coop-test-story lang:de@8,cutscene:Cutscenes/Cutscene2@45,cutstate@50,cutstate@83,shot:cutskipped@84,cutready@88,cutstate@90,shot:cutalone@92' }
             elseif ($Scenario -eq 'emotestats') { '-coop-test-regen 1 -coop-test-story lang:de@8,emotemenu@46,shot:emotemenu@47,emotemenu@48,emote:1@52,facefriend@55,shot:hostsits@58,killnear@61,killnear@63,statsstate@66,statsstate@75' }
             elseif ($Scenario -eq 'groupchest') { '-coop-test-story lang:de@8,gotovillage:Weystane@12,gotocrystal@19,cheststate@24,chestput@26,cheststate@29,statuemenu@30,chestpage@31,shot:groupchest@33,chesttake:0@35,cheststate@38,cheststate@45' }
             elseif ($Scenario -eq 'goldshare') { '-coop-test-regen 1 -coop-test-story gotopos:1418;24;320@15,goldstate@38,goldstate@60,coopmenu@62,shot:goldmenu@64,coopmenu@66 -coop-test-loot look:-1525955765@1,open@40,take1@45,takeall@50' }
             elseif ($Scenario -eq 'statuemenu') { '-coop-test-story lang:de@8,gotovillage:Weystane@12,gotocrystal@19,statuestate@21,facecrystal@22,shot:statuehover@23,statuemenu@24,shot:statuemenu@26,gold:500@27,respec:4;4;0;0@28,respec:3;2;2;1@29,statuestate@30,statuemenu@31,statuestats@32,shot:statuestats@33,statueclose@34,statuestate@50,statuemenu@51,statuetravel@52,statuestate@60,shot:statuetravel@70,statuestate@72' }
             elseif ($Scenario -eq 'ping') { '-coop-test-story lang:de@8,pingstate@52,shot:pingseen@53,stepaway:30@54,turn:90@55,shot:pingedge1@56,turn:90@57,shot:pingedge2@58,turn:90@59,shot:pingedge3@60,ping@61,pingstate@63' }
             elseif ($Scenario -eq 'spectate') { '-coop-test-story lang:de@8,stepaway:40@44,die@48,respawnstate@52,shot:spectate@53,respawnstate@61,shot:spectatenear@61,respawnstate@68,shot:spectateback@69' }
             elseif ($Scenario -eq 'revive') { '-coop-test-story lang:de@8,die@48,shot:down@51,respawnstate@52,respawnstate@61,shot:helpedup@62,die@70,shot:down2@72,giveup@75,respawnstate@78,respawnstate@85,shot:gaveup@86' }
             elseif ($Scenario -eq 'questloop') { '-coop-test-story queststate:Get_a_horse@50,queststate:Get_a_horse@65,queststate:Get_a_horse@80,queststate:Get_a_horse@95,queststate:Get_a_horse@110' }
             elseif ($Scenario -eq 'mountride') { '-coop-test-story lang:de@8,mountsummon:0@44,mountstate@46,mountstate@49,mountnear@50,mountride@53,mountstate@58,mountgo:1@62,mountstate@68,mountstate@75,mountgo:0@76,mountoff@78,mountstate@84,mountaway@86,mountstate@89,mountsummon:1@92,mountstate@97,mountaway@99,mountsummon:2@100,mountstate@105,mountaway@107,mountsummon:3@108,mountstate@113,mountaway@115,mountsummon:4@116,mountstate@121,mountaway@123,mountsummon:5@124,mountstate@129,mountaway@131,mountsummon:6@132,mountstate@137,mountaway@139,mountsummon:7@140,mountstate@145,mountaway@147,mountstate@150' }
             elseif ($Scenario -eq 'spells') { '-coop-bot fight -coop-bot-delay 105 -coop-test-regen 1 -coop-test-story lockenemy@43,projslow:4@44,handstate@44,realcast:7@46,handstate@49,projslow:0@53,handstate@54,realcast:0@56,handstate@60,realcast:1@62,handstate@66,realcast:2@68,handstate@72,realcast:3@74,handstate@78,realcast:4@80,handstate@84,camyaw:90@86,shot:enemyseen@92,shot:enemyseen2@94,effectssum@96,effectssum@100,effectssum@160' }
             elseif ($Scenario -eq 'woodevent') { '-coop-test-regen 1 -coop-test-story lang:de@8,gotoevent:Logging@44,eventstate:Logging@52,eventwood:Logging:16@55,gotospot:Logging@58,eventstate:Logging@62,shot:woodfriendbefore@60,eventstate:Logging@68,shot:woodafter@70,eventstate:Logging@85' }
             elseif ($Scenario -eq 'doors') { '-coop-test-regen 1 -coop-test-story gotopos:701;33;618@44,nocompanions@52,doorlist@60,gotodoor@62,doorstate@64,doorstate@68,dooruse@70,doorstate@72,doorstate@74,doorrace:closed@76,doorstate@92,doorbesides@94,doorstate@109,doorstate@115,doorwalk:6@116,doorexit@123,doorstate@129,gotopos:486;15;474@135,doorstate@152,gotopos:701;33;618@165,doorstate@172,doorstate@185,gotopos:704;33;641@190,doorstate@208' }
             elseif ($Scenario -eq 'doorsjoin') { '-coop-test-story gotopos:701;33;618@44,nocompanions@52,gotodoor@60,doorstate@62,doorstate@68' }
            elseif ($Scenario -eq 'mountshare') { '-coop-test-story lang:de@8,nocompanions@40,mountnearcopy@48,mountstate@50,mountridecand@52,mountstate@55,mountgo:1@56,shot:borrowing@62,mountstate@63,mountgo:0@66,mountoff@70,mountstate@74,mountstate@80,mountseat@90,mountstate@92,shot:passenger@100,mountstate@101,mountstate@106,mountunseat@110,mountstate@113,mountstate@120' }
             elseif ($Scenario -eq 'deathscreen') { '-coop-test-story lang:de@8,die@48,uidump@51,shot:deathscreen@53,window:OpenPauseMenu@78,shot:pausecoop@80,window:OpenPauseMenu@81,loadtry@82,coopleave@84,window:OpenPauseMenu@87,shot:pausealone@89,window:OpenPauseMenu@90,die@92,shot:deathalone@96' }
             elseif ($Scenario -eq 'statuecheck') { '-coop-test-story lang:de@8,gotovillage:Weystane@44,facecrystal@56,pressF@57,compass:on@59,facecrystal@60,respawnmarkers@62,shot:compass1@63,face:200@64,respawnmarkers@65,shot:compass2@66,compass:off@68,gotovillage:Ertoron@70,gotocrystal@88,facecrystal@89,shot:ertoron@91,pressF@92,crystalstate@94,respawnmarkers@95,die@97,respawnmarkers@115,shot:ertoronrevived@117,north@120,gotoxz:-430;225@152,gotocrystal@164,facecrystal@165,shot:erynard@167,respawnmarkers@168,gotoxz:215;-830@171,gotocrystal@182,facecrystal@183,shot:ironclad@185,gotoxz:-1080;1245@188,gotocrystal@199,facecrystal@200,shot:thunder@202,respawnmarkers@203,pressF@204,crystalstate@206,die@208,respawnmarkers@226,shot:thunderrevived@228' }
             elseif ($Scenario -eq 'respawnstatues') { '-coop-test-story lang:de@8,gotovillage:Weystane@44,respawnmarkers@58,facecrystal@60,shot:statue@62,pressF@63,crystalstate@65,respawnmarkers@66,shot:minimap@67,gotovillage:Steepport@70,respawnmarkers@90,facecrystal@92,shot:statuecopy@94,window:OpenWorldMap@96,shot:worldmap@99,window:OpenWorldMap@101,die@104,respawnmarkers@122,shot:revived@124' }
             elseif ($Scenario -eq 'tradefield') { '-coop-bot circle -coop-test-story tradeask@44,tradeui@48,chatprobe@52,chatprobe@54,fieldfocus@56,chatprobe@58,chatprobe@62,shot:tradefield@60,fieldfocus:off@64,chatprobe@68,chatprobe@71,tradecancel@73' }
            elseif ($Scenario -eq 'uiskin') { '-coop-test-story lang:de@8,partyaccept@50,tradeask@66,tradestack:0@72,tradegold:5@74,tradeui@76,shot:skintrade@80,tradecancel@84,tradeui:off@86,shot:skinchatrecent@96' }
             elseif ($Scenario -eq 'scaling') { '-coop-bot fight -coop-bot-delay 30 -coop-test-regen 1 -coop-test-story scalelog@14,partyaccept@18,partyaccept@24,scalelog@32,scalelog@52,partyleave@60,scalelog@77,scalelog@102' }
             elseif ($Scenario -eq 'chat') { '-coop-udp-loss 0.2 -coop-bot circle -coop-test-story chatprobe@55,chatprobe@58,chatopen@60,chatprobe@62,chattype:Hallo_vom_Client_<b>fett</b>@62,chatprobe@70,chatenter@71,chatprobe@73,chatprobe@76,chatburst:5@82,chatopen@86,chatlong@87,chatprobe@96,chatenter@97,chatopen@100,chattype:nicht_senden@101,chatesc@104,chatlog@125,shot:chathistory@126,chatopen@128,shot:chatfull@130,chatesc@132' }
             elseif ($Scenario -eq 'trade') { '-coop-test-story tradelog@14,tradeask@18,tradestack:0@22,tradeui@22,shot:tradeopen@25,tradeconfirm@26,tradelog@32,tradeask@36,tradeitem:0@41,tradeuse@44,tradelog@47,tradeask@50,tradegold:7@55,tradestall@56,tradeconfirm@58,tradeunstall@82,tradelog@83,tradeask@86,tradeitem:0@91,tradedrop@92,tradeconfirm@94,tradelog@104,tradeui@120,shot:tradeui@122,tradeui:off@124,tradelog@140' }
             elseif ($Scenario -eq 'party') { '-coop-test-regen 1 -coop-test-story xplog@14,xplog@19,partyaccept@25,partylog@30,maplog@30,shot:partyclient@33,xplog@45,partyleave@50,partylog@55,maplog@55,xplog@62,partyaccept@66,partylog@75,partyinvite@78,partylog@86,xplog@88' }
             elseif ($Scenario -eq 'effects') { '-coop-bot fight -coop-bot-delay 36 -coop-test-regen 1 -coop-test-strong 10 -coop-test-story intellect:8@14,xplog@15,shot:hosteffect@27,effectstate@28,effect:slash@32,effect:5@34,xplog@60,bodies@80,bodies@96,lootbody@98,bodies@100,xplog@100,effectstate@100' }
             elseif ($Scenario -eq 'pause') { '-coop-bot circle -coop-test-story pause@30,timescale@32,shot:pause@33,timescale@40,unpause@45,timescale@47' }
             elseif ($Scenario -eq 'travellers') { '-coop-test-story travellershift@8,travellers@12,gototraveller:15@20,travellers@30,shot:bard@40,travellers@50,travellers@90,travellers@110,shot:bardback@112' }
             elseif ($Scenario -eq 'fight') { '-coop-bot fight -coop-test-regen 1' }
             elseif ($Scenario -eq 'lan') { '-coop-test-story lanlist@15,shot:lanlist@18,lanjoin@20,coopmenu@40,shot:lanmenu@42,coopmenu@44' }
            elseif ($Scenario -eq 'map') { '-coop-test-story gotovillage:Weystane@20,portal:0@80,gotopos:325;102;2981@88' }
             elseif ($Scenario -eq 'respawnnorth') { '-coop-test-story north@15,gotoxz:-430;225@45,gotocrystal@54,shot:erynard@56,gotoxz:215;-830@62,shot:ironclad@71,gotoxz:-1080;1245@77,shot:thunder@86,die@92,shot:revivednorth@112' }
             elseif ($Scenario -eq 'respawn') { '-coop-test-story lang:de@8,gotovillage:Weystane@12,gotocrystal@19,crystalstate@20,facecrystal@21,crystalstate@23,shot:crystalhover@23,pressF@24,crystalstate@25,statuebind@26,crystalstate@27,statuebind@28,statueclose@29,crystalstate@29,gotopos:486;15;474@30,respawnstate@100,shot:revived@102,reloadsave@110 -coop-bot fight -coop-bot-delay 32 -coop-test-hp 20' }
             elseif ($Scenario -eq 'restpoints') { '-coop-test-look fakemod@8,torch@15,modweapon@20 -coop-test-story modcheck@30,sneak:on@35,modcheck@42,sneak:off@47,modcheck@52,spellshot@57,spellshot@60' }
             elseif ($Scenario -eq 'summons') { '-coop-test-story summon:skeleton@20,summon:cat@25,cmd:moveaway@40,cmd:back@60,cmd:killsummon@75,summon:cat@85' }
             elseif ($Scenario -eq 'eventhandover') { '-coop-bot fight -coop-test-regen 1 -coop-test-strong 10 -coop-test-story gotopos:113;5;478@20,useevent:2@45,useevent:1@55 -coop-test-events 1' }
             elseif ($Scenario -eq 'expfull') { "-coop-test-story exppattern:$ExpPattern@5,expgo:auto@48,expstart@54,expauto@66 -coop-test-events 1 -coop-test-regen 1" }
             elseif ($Scenario -eq 'expedition') { '-coop-test-story explist@22,expgo:dungeon@28,expstart@35,explist@50,expdungeon@70,explist@95,exprank:1@100,explist@105 -coop-test-events 1 -coop-test-regen 1' }
             elseif ($Scenario -eq 'dungeon') { '-coop-test-story portal:0@30,gotopos:325;102;2981@45 -coop-bot fight -coop-test-regen 1' }
             elseif ($Scenario -eq 'worlds') { '-coop-test-story portals@15,north@25,gedonia@80' }
             elseif ($Scenario -eq 'looks') { '-coop-test-look female@15,helmet@22,cloak@22,hidehelmet@32,hidecloak@40,showall@48,modweapon@56,badmodel@64' }
             elseif ($Scenario -eq 'bow') { '-coop-test-look bow@12 -coop-bot fight -coop-test-regen 1' }
             elseif ($Scenario -eq 'story') { '-coop-test-story answer:TakeProtectTownQuest@35,answer:GetHeroQuest@40' }
             elseif ($Scenario -eq 'loot') { '-coop-test-loot open@40,take1@45,takeall@50' }
             elseif ($Scenario -eq 'clock') { '-coop-test-clock shift:5@30,sleep:8@45' }
             elseif ($Scenario -eq 'apart') { '-coop-bot fight -coop-test-regen 1' }
             elseif ($Scenario -eq 'lossy') { '-coop-bot fight -coop-test-regen 1 -coop-udp-loss 0.2' }
             elseif ($Scenario -eq 'death') { '-coop-bot fight -coop-test-hp 20 -coop-test-reload 15' }
             elseif ($Scenario -eq 'companions') { '-coop-bot fight -coop-test-regen 1 -coop-test-story answer:BringButaBack@40 -coop-test-charisma 2' }
             elseif ($Scenario -eq 'event') { '-coop-bot fight -coop-bot-delay 60 -coop-test-regen 1 -coop-test-strong 10 -coop-test-story gotopos:113;5;478@44,useevent:2@66,useevent:1@76 -coop-test-events 1' }
             elseif ($Scenario -eq 'farloot') { '-coop-test-loot look:280705368@1,open@45,takeall@51' }
             elseif ($Scenario -eq 'chestgold') { '-coop-test-regen 1 -coop-test-story gotopos:1418;24;320@15 -coop-test-loot look:-1525955765@1,open@40,take1@45,takeall@50' }
             elseif ($Scenario -eq 'chestlid') { '-coop-test-regen 1 -coop-test-story gotopos:174;50;522@15 -coop-test-loot look:-281711968@1,open@40' }
             elseif ($Scenario -eq 'latejoin') { '-coop-test-story none@999 -coop-test-loot look:280705368@1' }
             else { '-coop-bot circle -coop-look alt -coop-gear-swap 45' }
$c = Start-Process "$root\game2\Gedonia.exe" -WorkingDirectory "$root\game2" -PassThru -ArgumentList "$w $clientNet -coop-name Client $common $clientBot"
"transport=$Transport scenario=$Scenario host=$($h.Id) client=$($c.Id)"
