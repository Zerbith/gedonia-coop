# Copies what belongs in the public repo (publish\gedonia-coop) from the
# workspace: an allowlist, so game files, saves, decompiled code, logs and the
# German tables (they hold the game's own English texts) never get in.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = "$root\publish\gedonia-coop"
if (-not (Test-Path $repo)) { throw "missing $repo" }

$copy = @(
    @{ From = 'src\GedoniaCoop\*.cs'; To = 'src\GedoniaCoop' },
    @{ From = 'scripts\*.ps1'; To = 'scripts' },
    @{ From = 'scripts\*.sh'; To = 'scripts' },
    @{ From = 'installer\*'; To = 'installer' },
    @{ From = 'tools\TextExtract.cs'; To = 'tools' }
)
foreach ($c in $copy) {
    $dest = Join-Path $repo $c.To
    if (Test-Path $dest) { Get-ChildItem $dest -File | Where-Object { $_.Name -like (Split-Path $c.From -Leaf) } | Remove-Item -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $root $c.From) $dest -Force
}
$n = (Get-ChildItem $repo -Recurse -File | Where-Object { $_.FullName -notlike "$repo\.git\*" }).Count
"synced -> $repo ($n files)"
