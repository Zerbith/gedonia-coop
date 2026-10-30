# Builds the friends' package: dist\GedoniaCoop-<version>\ and a ZIP of it.
# Contains only the mod, the installer and the instructions - no game files,
# no BepInEx (the installer downloads that, hash-checked).
#   -Public  the public release (dist\public\): without the German tables (they
#            hold the game's own texts) and without the readme parts about them
#   -NoVoice without the German voice-over (tts\render\ogg, ~100 MB)
param([switch]$Public, [switch]$NoVoice)
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
# German voice-over: generated from the game's texts, so private like the tables.
if (-not $Public -and -not $NoVoice -and (Test-Path "$root\tts\render\ogg")) {
    New-Item -ItemType Directory -Force "$dir\voice-de" | Out-Null
    Copy-Item "$root\tts\render\ogg\*.ogg" "$dir\voice-de"
    $speakers = Import-Csv "$root\tts\data\speaker_voices.tsv" -Delimiter "`t" | ForEach-Object { "$($_.speaker)`t$($_.voice)" }
    [IO.File]::WriteAllLines("$dir\voice-de\speakers.tsv", [string[]]$speakers, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path "$root\tts\render\cutscene\cutscenes.tsv") {
        New-Item -ItemType Directory -Force "$dir\voice-de\cutscene" | Out-Null
        Copy-Item "$root\tts\render\cutscene\*.ogg", "$root\tts\render\cutscene\cutscenes.tsv" "$dir\voice-de\cutscene"
    }
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
