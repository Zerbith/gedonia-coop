# Pairs the quest runner's [qrun] lines of host (game\) and friend (game2\) and writes a
# PASS/FAIL table (player bots T2). Run after scripts\test-local.ps1 -Scenario questrun.
#   -Out  markdown file (default build\atlas\questrun-<time>.md)
# Verdicts per step:
#   PASS      the step's story function ran on both PCs, quest state and touched world entries equal
#   LOCAL     it ran on one PC only (a story path that is not synced)
#   DIFF      both ran it, but quest state or world entries differ afterwards
#   NORUN     it ran nowhere (precondition not met, wrong trigger in the atlas, or nothing happened)
#   NOACT     the actor could not act (object/NPC not loaded, item unknown)
#   SKIP      the runner has no verb for the step's trigger (mystery panel, world event, cutscene...)
param([string]$Out)
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = "$root\build\atlas\questrun-$(Get-Date -Format yyyyMMdd-HHmm).md" }

function Read-Probes($log) {
    $p = @{}; $a = @{}
    foreach ($l in Get-Content $log -Encoding UTF8) {
        if ($l -match '\[qrun\] probe (\d+) quest=''(.*)'' step=''(.*)'' fn=(\S*) fnRan=(\d) state=(\S+) wdb=(\S+) ran=(.*?)( note=(.*))?$') {
            $p[[int]$Matches[1]] = [pscustomobject]@{ Quest = $Matches[2]; Step = $Matches[3]; Fn = $Matches[4]; Ran = $Matches[5]; State = $Matches[6]; Wdb = $Matches[7]; RanList = $Matches[8]; Note = $Matches[10] }
        }
        elseif ($l -match '\[qrun\] act (\d+): (.*)$') { $a[[int]$Matches[1]] = $Matches[2] }
        elseif ($l -match '\[qrun\] step (\d+) .* by (host|client)$') { $a["by$($Matches[1])"] = $Matches[2] }
    }
    return @{ P = $p; A = $a }
}
$h = Read-Probes "$root\game\BepInEx\LogOutput.log"
$c = Read-Probes "$root\game2\BepInEx\LogOutput.log"
$rows = @(); $count = @{}
foreach ($i in ($h.P.Keys | Sort-Object)) {
    $hp = $h.P[$i]; $cp = $c.P[$i]
    $by = $h.A["by$i"]
    $act = if ($by -eq 'client') { $c.A[$i] } else { $h.A[$i] }
    $v =
        if ($hp.Note -like 'SKIP*') { 'SKIP' }
        elseif (-not $cp) { 'NOPROBE' }
        elseif ($act -match 'not loaded|unknown|already dead|not supported') { 'NOACT' }
        elseif ($hp.Ran -eq '1' -and $cp.Ran -eq '1') { if ($hp.State -eq $cp.State -and $hp.Wdb -eq $cp.Wdb) { 'PASS' } else { 'DIFF' } }
        elseif ($hp.Ran -eq '1' -or $cp.Ran -eq '1') { 'LOCAL' }
        else { 'NORUN' }
    $count[$v] = 1 + [int]$count[$v]
    $rows += "| $i | $($hp.Quest) | $($hp.Step) | $($hp.Fn) | $by | $v | H $($hp.State) / C $($cp.State) | $(if ($hp.Wdb -eq $cp.Wdb) { 'same' } else { 'differs' }) | $($act -replace '\|', '/') $(if ($hp.Note) { $hp.Note }) |"
}
$sum = ($count.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
$md = @("# Quest runner $(Get-Date -Format 'yyyy-MM-dd HH:mm')", '', "Steps: $($h.P.Count) - $sum", '',
        '| # | quest | step | function | actor | verdict | quest state host / client | world entries | action |',
        '|---|---|---|---|---|---|---|---|---|') + $rows
$md | Set-Content $Out -Encoding UTF8
"$Out"
"steps=$($h.P.Count) $sum"
