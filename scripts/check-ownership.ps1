# Checks the enemy-ownership rule from both test logs: an enemy must never be
# simulated by two machines at once. Both copies log "[owned] id,id,..." every
# 2 s with a wall-clock stamp; samples less than 1.1 s apart are compared.
# An id owned on both sides in one sample pair can be a handoff in flight; the
# same id in two pairs in a row is a real double owner and fails the check.
param(
    [string]$HostLog = (Join-Path (Split-Path -Parent $PSScriptRoot) 'game\BepInEx\LogOutput.log'),
    [string]$ClientLog = (Join-Path (Split-Path -Parent $PSScriptRoot) 'game2\BepInEx\LogOutput.log')
)

function Read-Owned($path) {
    $list = @()
    foreach ($m in Select-String -Path $path -Pattern '(\d\d):(\d\d):(\d\d\.\d+) \[owned\] ?(.*)$') {
        $g = $m.Matches[0].Groups
        $t = [int]$g[1].Value * 3600 + [int]$g[2].Value * 60 + [double]::Parse($g[3].Value, [Globalization.CultureInfo]::InvariantCulture)
        $ids = @()
        if ($g[4].Value.Trim().Length -gt 0) { $ids = $g[4].Value.Trim().Split(',') }
        $list += ,@($t, $ids)
    }
    return $list
}

$h = Read-Owned $HostLog
$c = Read-Owned $ClientLog
$pairs = 0; $transient = 0; $prev = @{}; $double = @{}
foreach ($hs in $h) {
    $best = $null; $bestDt = 1.1
    foreach ($cs in $c) { $dt = [Math]::Abs($cs[0] - $hs[0]); if ($dt -lt $bestDt) { $bestDt = $dt; $best = $cs } }
    if ($best -eq $null) { continue }
    $pairs++
    $both = @($hs[1] | Where-Object { $best[1] -contains $_ })
    $now = @{}
    foreach ($id in $both) { $now[$id] = $true; if ($prev.ContainsKey($id)) { $double[$id] = $true } }
    if ($both.Count -gt 0) { $transient++ }
    $prev = $now
}
$maxH = ($h | ForEach-Object { $_[1].Count } | Measure-Object -Maximum).Maximum
$maxC = ($c | ForEach-Object { $_[1].Count } | Measure-Object -Maximum).Maximum
"samples host=$($h.Count) client=$($c.Count) compared=$pairs; most owned at once: host=$maxH client=$maxC"
"pairs with an id on both sides: $transient (handoffs in flight)"
if ($double.Count -gt 0) { "RESULT: FAIL - owned by both machines in consecutive samples: " + ($double.Keys -join ', ') }
elseif ($pairs -eq 0) { "RESULT: FAIL - no comparable samples" }
else { "RESULT: OK - no enemy was simulated by two machines" }
