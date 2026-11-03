# Runs test-local.ps1 -Scenario voicemute for each case (host alone in game\) and judges the logs.
# Only the log is checked: which clips were started, not what they sound like.
#   de-*: no spoken greeting on the camera, the German dialogue voice plays, the cutscene plays de_ clips
#   en-on: greetings play, no German anywhere, the cutscene plays the original clips
#   en-off: no greetings, no German, no VoiceOverClip speech at all (AudioTrack clips would still play)
param([string[]]$Cases = @('en-off', 'de-off', 'en-on', 'de-on'), [int]$Seconds = 165)
$root = Split-Path -Parent $PSScriptRoot
$out = "$root\build\voicemute"
New-Item -ItemType Directory -Force $out | Out-Null
$langFile = "$root\game\BepInEx\config\gedonia-coop-language.txt"
$langBefore = if (Test-Path $langFile) { Get-Content $langFile -Raw } else { $null }
$playerLog = "$env:USERPROFILE\AppData\LocalLow\Oleg Kazakov\GedoniaH\Player.log"
# Sounds without words, as GreetingMute.Wordless (they may play in every case).
$wordless = '^(ANIMAL_|Bark|Creature_|Ghost Laughing|Human_|Monster_|Whining)'
$failed = 0

function Check([string]$case, [string]$what, [bool]$ok, [string]$detail) {
    "  {0,-4} {1} {2}" -f $(if ($ok) { 'ok' } else { 'FAIL' }), $what, $detail
    if (-not $ok) { $script:failed++ }
}

try {
    foreach ($case in $Cases) {
        & "$root\scripts\test-local.ps1" -Scenario voicemute -VoiceCase $case
        Start-Sleep -Seconds $Seconds
        & "$root\scripts\test-local.ps1" -Stop
        Start-Sleep -Seconds 3
        Copy-Item "$root\game\BepInEx\LogOutput.log" "$out\$case.log" -Force
        if (Test-Path $playerLog) { Copy-Item $playerLog "$out\$case-player.log" -Force }
        $log = Get-Content "$out\$case.log"
        $german = $case -like 'de-*'; $on = $case -like '*-on'

        $camera = @($log | Select-String '\[audio\] oneshot (.+) on .* \(camera\)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        # DOOR_ is the sound of the shop window opening, the same source as the greetings.
        $spoken = @($camera | Where-Object { $_ -notmatch $wordless -and $_ -notmatch '^(de_|DOOR_)' })
        $cutsceneClips = @($log | Select-String '\[audio\] oneshot (.+) on voices$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        $bandits = @($log | Select-String '\[audio\] oneshot (.+) on \S*Bandit' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Where-Object { $_ -notmatch $wordless })
        $silent = @($log | Select-String '\[voice\] English greeting silent: ').Count
        $dialog = @($log | Select-String '\[voice\] playing ').Count
        $noFile = @($log | Select-String '\[voice\] no file').Count
        $oneShots = @($log | Select-String '\[audio\] oneshot (\S.*) on ' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        $deClips = @($oneShots | Where-Object { $_ -like 'de_*' })
        $talks = @($log | Select-String 'Test: talking to ').Count
        $merchant = @($log | Select-String 'Test: merchant answer ').Count
        $barks = @($log | Select-String 'Test: bark on (\d+) NPCs' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value }) | Select-Object -First 1
        $cutscene = @($log | Select-String "Test: cutscene '").Count
        $errors = @($log | Select-String '\[(Error|Fatal)\s*:').Count
        $exceptions = if (Test-Path "$out\$case-player.log") { @(Select-String -Path "$out\$case-player.log" -Pattern 'Exception').Count } else { -1 }
        # A bug of the game, not of this test: this Weystane NPC already has an Animator, so
        # AsyncActivatorManager.Add gets null from AddComponent<Animator>() when the area streams in.
        $gameBug = if (Test-Path "$out\$case-player.log") { @(Select-String -Path "$out\$case-player.log" -Pattern "Can't add component 'Animator' to MediumStr_LeatherworkingTeacher").Count } else { 0 }
        $activator = if (Test-Path "$out\$case-player.log") { @(Select-String -Path "$out\$case-player.log" -Pattern 'at AsyncActivatorManager\+<Add>').Count } else { 0 }
        if ($gameBug -ge 1 -and $activator -eq 1) { "  note exception of the game itself: AsyncActivatorManager.Add on MediumStr_LeatherworkingTeacher"; $exceptions-- }

        "== $case : camera clips [$($camera -join ', ')], silenced $silent, dialogue voice $dialog (no file $noFile), de_ one-shots $($deClips.Count), all one-shots $($oneShots.Count)"
        Check $case 'steps ran' ($talks -ge 2 -and $merchant -eq 1 -and $barks -ge 1 -and $cutscene -eq 1) "talks=$talks merchant=$merchant barks=$barks cutscene=$cutscene"
        if ($german) {
            Check $case 'no spoken English greeting on the camera' ($spoken.Count -eq 0) ($spoken -join ', ')
            # With the option off the game itself skips the call, so only the option-on case reaches the mute.
            if ($on) { Check $case 'greetings were silenced by the mod' ($silent -ge 1) "$silent" }
            Check $case 'no spoken bandit idle lines' ($bandits.Count -eq 0) ($bandits -join ', ')
            Check $case 'German dialogue voice plays' ($dialog -ge 2) "$dialog"
            Check $case 'all 8 cutscene voice clips play in German' ($deClips.Count -eq 8 -and $cutsceneClips.Count -eq 8) "de=$($deClips.Count) all=$($cutsceneClips.Count): $($cutsceneClips -join ', ')"
        } else {
            Check $case 'nothing silenced, no German' ($silent -eq 0 -and $dialog -eq 0 -and $deClips.Count -eq 0) "silenced=$silent voice=$dialog de=$($deClips.Count)"
            if ($on) {
                Check $case 'greetings play as in the original game' ($spoken.Count -ge 1) ($spoken -join ', ')
                Check $case 'bandit idle lines play' ($bandits.Count -ge 1) "$($bandits.Count)"
                Check $case 'cutscene speaks the 8 original clips' ($cutsceneClips.Count -eq 8) ($cutsceneClips -join ', ')
            } else {
                Check $case 'option off: no greeting, no bandit lines, no cutscene voice' ($spoken.Count -eq 0 -and $bandits.Count -eq 0 -and $cutsceneClips.Count -eq 0) "greetings=$($spoken.Count) bandits=$($bandits.Count) cutscene=$($cutsceneClips.Count)"
            }
        }
        Check $case 'no errors in the logs' ($errors -eq 0 -and $exceptions -le 0) "errors=$errors exceptions=$exceptions"
    }
}
finally {
    # The test copy goes back to the language it had (the default order ends with the option on).
    if ($langBefore -ne $null) { [IO.File]::WriteAllText($langFile, $langBefore) }
}
"failed checks: $failed"

