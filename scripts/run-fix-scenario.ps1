# Runs one test-local.ps1 scenario, stops the copies and keeps logs and screenshots in build\fixes\<Tag>.
# Prints the error and exception counts of both BepInEx logs and Player.logs. Foreign to the mod:
# AsyncActivatorManager.Add (MediumStr_LeatherworkingTeacher in Weystane), reported as a note.
param([Parameter(Mandatory)][string]$Scenario, [Parameter(Mandatory)][int]$Seconds, [string]$NoFix = "", [string]$Tag = "", [string]$Transport = "udp", [int]$ClientDelay = 3)
$root = Split-Path -Parent $PSScriptRoot
if (-not $Tag) { $Tag = $Scenario + $(if ($NoFix) { '-control' } else { '' }) }
$out = "$root\build\fixes\$Tag"
Remove-Item -Recurse $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
& "$root\scripts\test-local.ps1" -Stop
Start-Sleep 2
& "$root\scripts\test-local.ps1" -Scenario $Scenario -NoFix $NoFix -Transport $Transport -ClientDelay $ClientDelay
Start-Sleep -Seconds $Seconds
& "$root\scripts\test-local.ps1" -Stop
Start-Sleep 3
foreach ($p in @(@('game', 'GedoniaH', 'host'), @('game2', 'GedoniaC', 'client'))) {
    $log = "$root\$($p[0])\BepInEx\LogOutput.log"
    $player = "$env:USERPROFILE\AppData\LocalLow\Oleg Kazakov\$($p[1])\Player.log"
    if (Test-Path $log) { Copy-Item $log "$out\$($p[2]).log" -Force }
    if (Test-Path $player) { Copy-Item $player "$out\$($p[2])-player.log" -Force }
    $shots = "$root\$($p[0])\BepInEx\coop-shots"
    if (Test-Path $shots) { Get-ChildItem "$shots\*" -File | Where-Object { $_.Name -notmatch '_\d+\.png$' } | Copy-Item -Destination $out -Force -ErrorAction SilentlyContinue }
    if (Test-Path "$out\$($p[2]).log") {
        $bad = @(Select-String -Path "$out\$($p[2]).log" -Pattern '^\[(Error|Fatal)|^\[Warning' )
        "{0}: BepInEx errors/warnings {1}" -f $p[2], $bad.Count
        $bad | Select-Object -First 8 | ForEach-Object { "  " + $_.Line }
    }
    if (Test-Path "$out\$($p[2])-player.log") {
        $ex = @(Select-String -Path "$out\$($p[2])-player.log" -Pattern 'Exception')
        "{0}: Player.log exceptions {1}" -f $p[2], $ex.Count
        $ex | Select-Object -First 8 | ForEach-Object { "  " + $_.Line }
    }
}
"logs in $out"
