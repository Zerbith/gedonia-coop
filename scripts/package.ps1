# Builds the friends' package: dist\GedoniaCoop-<version>\ and a ZIP of it.
# Contains only the mod, the installer and the instructions - no game files,
# no BepInEx (the installer downloads that, hash-checked).
#   -Public  the public release (dist\public\): without the German tables (they
#            hold the game's own texts) and without the readme parts about them
param([switch]$Public)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& "$root\scripts\build.ps1" | Out-Null
$version = (Select-String -Path "$root\src\GedoniaCoop\Plugin.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$name = "GedoniaCoop-$version"
$out = if ($Public) { "$root\dist\public" } else { "$root\dist" }
$dir = "$out\$name"
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Force $dir | Out-Null

Copy-Item "$root\build\GedoniaCoop.dll" $dir
if (-not $Public -and (Test-Path "$root\src\GedoniaCoop\lang")) {
    New-Item -ItemType Directory -Force "$dir\lang" | Out-Null
    Copy-Item "$root\src\GedoniaCoop\lang\de-*.tsv" "$dir\lang"
}
Copy-Item "$root\installer\install.ps1", "$root\installer\Installieren.cmd", "$root\installer\Deinstallieren.cmd", "$root\installer\Install.cmd", "$root\installer\Uninstall.cmd" $dir
foreach ($doc in 'LIESMICH.txt', 'README.txt') {
    $text = (Get-Content "$root\installer\$doc" -Raw -Encoding UTF8).Replace('{VERSION}', $version)
    if ($Public) {
        # The section about the German translation (up to the next heading) and the installer line about it.
        $text = [regex]::Replace($text, '(?ms)^(DEUTSCH|GERMAN TRANSLATION)\r?\n-+\r?\n.*?(?=^[A-Z][^\r\n]*\r?\n-{3,}\r?\n)', '')
        $text = [regex]::Replace($text, ' und die deutschen Texte', '')
        $text = [regex]::Replace($text, ' and the German texts', '')
    }
    Set-Content "$dir\$doc" $text -Encoding UTF8
}
Set-Content "$dir\VERSION.txt" $version -Encoding ascii

$zip = "$out\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Get-ChildItem $dir | ForEach-Object { "{0,-22} {1,8:N0} bytes" -f $_.Name, $_.Length }
"zip: $zip ({0:N0} bytes)  sha256 {1}" -f (Get-Item $zip).Length, (Get-FileHash $zip).Hash
