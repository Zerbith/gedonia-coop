# Builds the friends' package: dist\GedoniaCoop-<version>\ and a ZIP of it.
# Contains only the mod, the installer and the instructions - no game files,
# no BepInEx (the installer downloads that, hash-checked).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& "$root\scripts\build.ps1" | Out-Null
$version = (Select-String -Path "$root\src\GedoniaCoop\Plugin.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$name = "GedoniaCoop-$version"
$dir = "$root\dist\$name"
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Force $dir | Out-Null

Copy-Item "$root\build\GedoniaCoop.dll" $dir
New-Item -ItemType Directory -Force "$dir\lang" | Out-Null
if (Test-Path "$root\src\GedoniaCoop\lang") { Copy-Item "$root\src\GedoniaCoop\lang\de-*.tsv" "$dir\lang" }
Copy-Item "$root\installer\install.ps1", "$root\installer\Installieren.cmd", "$root\installer\Deinstallieren.cmd", "$root\installer\Install.cmd", "$root\installer\Uninstall.cmd" $dir
(Get-Content "$root\installer\LIESMICH.txt" -Raw).Replace('{VERSION}', $version) | Set-Content "$dir\LIESMICH.txt" -Encoding UTF8
(Get-Content "$root\installer\README.txt" -Raw).Replace('{VERSION}', $version) | Set-Content "$dir\README.txt" -Encoding UTF8
Set-Content "$dir\VERSION.txt" $version -Encoding ascii

$zip = "$root\dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Get-ChildItem $dir | ForEach-Object { "{0,-22} {1,8:N0} bytes" -f $_.Name, $_.Length }
"zip: $zip ({0:N0} bytes)  sha256 {1}" -f (Get-Item $zip).Length, (Get-FileHash $zip).Hash
