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
      [ValidateSet('walk', 'fight', 'story', 'loot', 'clock', 'apart', 'lossy', 'death', 'companions', 'event', 'latejoin', 'farloot', 'chestgold', 'chestlid', 'looks', 'bow', 'ui', 'worlds', 'dungeon', 'expedition', 'expfull', 'eventhandover', 'summons', 'restpoints', 'survey', 'menu', 'german', 'map', 'lan', 'respawn', 'respawnnorth', 'travellers', 'pause', 'dialogue', 'effects')] [string]$Scenario = 'walk',
      [int]$ClientDelay = 3, [string]$ExpPattern = '*|*|*',
      [string]$DialogSteps = 'dialogsurvey@15,gotovillage:Weystane@18,dialogsurvey@30', [string]$DialogClientSteps = 'none@999')
$root = Split-Path -Parent $PSScriptRoot
if ($Stop) { Get-Process Gedonia -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" } | Stop-Process -Confirm:$false; return }

# Fresh test saves every run: the copies may autosave test changes into them.
$pristine = "$root\backups\coop-test.es3"
if (-not (Test-Path $pristine)) { throw "missing $pristine" }
foreach ($p in 'GedoniaH', 'GedoniaC') {
    Copy-Item $pristine "$env:USERPROFILE\AppData\LocalLow\Oleg Kazakov\$p\coop-test.es3" -Force
}
foreach ($g in "$root\game", "$root\game2") {
    Remove-Item "$g\BepInEx\LogOutput.log", "$g\BepInEx\coop-lobby.txt" -ErrorAction SilentlyContinue
    Remove-Item -Recurse "$g\BepInEx\coop-shots" -ErrorAction SilentlyContinue
}

$w = '-screen-fullscreen 0 -screen-width 960 -screen-height 540'
$common = '-coop-autoload coop-test.es3 -coop-shots 8 -coop-animlog 1'
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
# loot: the friend opens the nearest chest, takes one item, then the rest; the
# host tries to open the same chest in between and must be turned away.
$hostTest = if ($Scenario -eq 'dialogue') { "-coop-test-story $DialogSteps" }
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
            elseif ($Scenario -eq 'restpoints') { '-coop-test-look fakemod@8 -coop-test-story modcheck@30,modcheck@42,modcheck@52' }
            elseif ($Scenario -eq 'summons') { '-coop-test-story summon:cat@30' }
            elseif ($Scenario -eq 'eventhandover') { '-coop-test-story gotopos:111;5;476@20,useevent:2@40,gotopos:486;15;474@70,gotopos:111;5;476@130 -coop-test-events 1 -coop-test-regen 1' }
            elseif ($Scenario -eq 'expfull') { "-coop-test-story exprank:1@12,exproll:$ExpPattern@14,expgo:auto@20,expauto@40 -coop-test-events 1 -coop-test-regen 1" }
            elseif ($Scenario -eq 'expedition') { '-coop-test-story exprank:1@15,explist@20,expgo:dungeon@25,expdungeon@65,explist@90 -coop-test-events 1 -coop-test-regen 1' }
            elseif ($Scenario -eq 'dungeon') { '-coop-test-story portal:0@20 -coop-test-regen 1' }
            elseif ($Scenario -eq 'ui') { '-coop-test-ui 20' }
            elseif ($Scenario -eq 'looks') { '-coop-test-look helmet@20,hidehelmet@30' }
            elseif ($Scenario -eq 'loot') { '-coop-test-loot open@44' }
            elseif ($Scenario -eq 'clock') { '-coop-test-clock weather@55,weather@75' }
            elseif ($Scenario -eq 'apart') { '-coop-bot line -coop-bot-delay 50' }
            elseif ($Scenario -eq 'lossy') { '-coop-udp-loss 0.2' }
            elseif ($Scenario -eq 'companions') { '-coop-test-story answer:ArenaCallChampion@60 -coop-test-charisma 2 -coop-test-regen 1' }
            elseif ($Scenario -eq 'event') { '-coop-test-story gotopos:111;5;476@20,useevent:2@40 -coop-test-events 1 -coop-test-regen 1' }
            elseif ($Scenario -eq 'farloot') { '-coop-test-story gotopos:111;5;476@20,gotopos:486;15;474@75 -coop-test-loot look:280705368@1' }
            elseif ($Scenario -eq 'chestgold') { '-coop-test-regen 1 -coop-test-story gotopos:1416;24;318@15 -coop-test-loot look:-1525955765@1' }
            elseif ($Scenario -eq 'chestlid') { '-coop-test-regen 1 -coop-test-story gotopos:172;50;520@15 -coop-test-loot look:-281711968@1' }
            elseif ($Scenario -eq 'latejoin') { '-coop-test-story answer:TakeProtectTownQuest@30,answer:TakeHorseQuest@35 -coop-test-loot open@40,takeall@46' } else { '' }
$h = Start-Process "$root\game\Gedonia.exe" -WorkingDirectory "$root\game" -PassThru -ArgumentList "$w $hostNet -coop-name Host $common -coop-dump 1 $hostTest"
Start-Sleep $ClientDelay
$clientBot = if ($Scenario -eq 'dialogue') { "-coop-test-story $DialogClientSteps" }
             elseif ($Scenario -eq 'effects') { '-coop-bot fight -coop-bot-delay 36 -coop-test-regen 1 -coop-test-strong 10 -coop-test-story intellect:8@14,xplog@15,shot:hosteffect@27,effectstate@28,effect:slash@32,effect:5@34,xplog@60,bodies@80,bodies@96,lootbody@98,bodies@100,xplog@100,effectstate@100' }
             elseif ($Scenario -eq 'pause') { '-coop-bot circle -coop-test-story pause@30,timescale@32,shot:pause@33,timescale@40,unpause@45,timescale@47' }
             elseif ($Scenario -eq 'travellers') { '-coop-test-story travellershift@8,travellers@12,gototraveller:15@20,travellers@30,shot:bard@40,travellers@50,travellers@90,travellers@110,shot:bardback@112' }
             elseif ($Scenario -eq 'fight') { '-coop-bot fight -coop-test-regen 1' }
             elseif ($Scenario -eq 'lan') { '-coop-test-story lanlist@15,shot:lanlist@18,lanjoin@20,coopmenu@40,shot:lanmenu@42,coopmenu@44' }
            elseif ($Scenario -eq 'map') { '-coop-test-story gotovillage:Weystane@20,portal:0@80,gotopos:325;102;2981@88' }
             elseif ($Scenario -eq 'respawnnorth') { '-coop-test-story north@15,gotoxz:-430;225@45,gotocrystal@54,shot:erynard@56,gotoxz:215;-830@62,shot:ironclad@71,gotoxz:-1080;1245@77,shot:thunder@86,die@92,shot:revivednorth@112' }
             elseif ($Scenario -eq 'respawn') { '-coop-test-story lang:de@8,gotovillage:Weystane@12,gotocrystal@19,crystalstate@20,facecrystal@21,crystalstate@23,shot:crystalhover@23,pressF@24,crystalstate@26,pressF@27,crystalstate@28,gotopos:486;15;474@30,shot:revived@70,reloadsave@80 -coop-bot fight -coop-bot-delay 32 -coop-test-hp 20' }
             elseif ($Scenario -eq 'restpoints') { '-coop-test-look fakemod@8,torch@15,modweapon@20 -coop-test-story modcheck@30,sneak:on@35,modcheck@42,sneak:off@47,modcheck@52,spellshot@57,spellshot@60' }
             elseif ($Scenario -eq 'summons') { '-coop-test-story summon:skeleton@20,summon:cat@25,cmd:moveaway@40,cmd:back@60,cmd:killsummon@75,summon:cat@85' }
             elseif ($Scenario -eq 'eventhandover') { '-coop-bot fight -coop-test-regen 1 -coop-test-strong 10 -coop-test-story gotopos:113;5;478@20,useevent:2@45,useevent:1@55 -coop-test-events 1' }
             elseif ($Scenario -eq 'expfull') { "-coop-test-story exppattern:$ExpPattern@5,expgo:auto@24,expstart@30,expauto@42 -coop-test-events 1 -coop-test-regen 1" }
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
             elseif ($Scenario -eq 'event') { '-coop-bot fight -coop-test-regen 1 -coop-test-strong 10 -coop-test-story gotopos:113;5;478@20,useevent:2@45,useevent:1@55 -coop-test-events 1' }
             elseif ($Scenario -eq 'farloot') { '-coop-test-loot look:280705368@1,open@45,takeall@51' }
             elseif ($Scenario -eq 'chestgold') { '-coop-test-regen 1 -coop-test-story gotopos:1418;24;320@15 -coop-test-loot look:-1525955765@1,open@40,take1@45,takeall@50' }
             elseif ($Scenario -eq 'chestlid') { '-coop-test-regen 1 -coop-test-story gotopos:174;50;522@15 -coop-test-loot look:-281711968@1,open@40' }
             elseif ($Scenario -eq 'latejoin') { '-coop-test-story none@999 -coop-test-loot look:280705368@1' }
             else { '-coop-bot circle -coop-look alt -coop-gear-swap 45' }
$c = Start-Process "$root\game2\Gedonia.exe" -WorkingDirectory "$root\game2" -PassThru -ArgumentList "$w $clientNet -coop-name Client $common $clientBot"
"transport=$Transport scenario=$Scenario host=$($h.Id) client=$($c.Id)"
