# Builds a second game copy for local co-op testing out of hard links to the
# host copy, so it costs almost no disk space. Files the client must own
# (app.info, BepInEx folder) are real copies, never links.
param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'game'),
    [string]$Dest = (Join-Path (Split-Path -Parent $PSScriptRoot) 'game2'),
    [string]$Product = 'Gedonia Coop Client'
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Dest) { throw "$Dest already exists" }

$srcRoot = (Resolve-Path $Source).Path.TrimEnd('\')
$bepRoot = Join-Path $srcRoot 'BepInEx'
$appInfo = Join-Path $srcRoot 'Gedonia_Data\app.info'

$n = 0
Get-ChildItem $srcRoot -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($srcRoot.Length + 1)
    $target = Join-Path $Dest $rel
    $dir = Split-Path $target
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    if ($_.FullName -eq $appInfo -or $_.FullName.StartsWith($bepRoot + '\')) {
        Copy-Item $_.FullName $target
    } else {
        New-Item -ItemType HardLink -Path $target -Target $_.FullName | Out-Null
    }
    $n++
}

$destAppInfo = Join-Path $Dest 'Gedonia_Data\app.info'
Remove-Item $destAppInfo
[IO.File]::WriteAllText($destAppInfo, "Oleg Kazakov`n$Product")
"$n files -> $Dest"
