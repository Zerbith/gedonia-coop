# Compiles the plugin with the .NET Framework csc that ships with Windows
# (C# 5) against the game's own assemblies, then copies it into both copies.
#   -GameDir  a Gedonia folder with BepInEx installed (default: the test copy game\)
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $GameDir) { $GameDir = "$root\game" }
$managed = "$GameDir\Gedonia_Data\Managed"
$core = "$GameDir\BepInEx\core"
$out = "$root\build\GedoniaCoop.dll"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$refs = @(
    "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\netstandard.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.PhysicsModule.dll",
    "$managed\UnityEngine.TextRenderingModule.dll", "$managed\UnityEngine.ScreenCaptureModule.dll",
    "$managed\UnityEngine.AnimationModule.dll", "$managed\UnityEngine.ParticleSystemModule.dll", "$managed\UnityEngine.AudioModule.dll",
    "$managed\UnityEngine.IMGUIModule.dll", "$managed\UnityEngine.InputLegacyModule.dll",
    "$managed\Assembly-CSharp.dll", "$managed\Assembly-CSharp-firstpass.dll", "$managed\AstarPathfindingProject.dll",
    "$core\BepInEx.dll", "$core\0Harmony.dll"
) | ForEach-Object { "/r:$_" }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /nostdlib /noconfig /target:library /optimize /out:$out $refs (Get-ChildItem "$root\src\GedoniaCoop\*.cs").FullName
if ($LASTEXITCODE -ne 0) { throw "csc failed" }

foreach ($game in "$root\game", "$root\game2") {
    if (Test-Path $game) {
        New-Item -ItemType Directory -Force "$game\BepInEx\plugins" | Out-Null
        Copy-Item $out "$game\BepInEx\plugins\GedoniaCoop.dll" -Force
        Remove-Item "$game\BepInEx\plugins\de*.tsv" -ErrorAction SilentlyContinue
        # The German tables hold the game's own texts, so they are not in the public repo.
        if (Test-Path "$root\src\GedoniaCoop\lang") { Copy-Item "$root\src\GedoniaCoop\lang\de-*.tsv" "$game\BepInEx\plugins\" -Force }
    }
}
"built $out"
