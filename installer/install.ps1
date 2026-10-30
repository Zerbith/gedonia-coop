# Gedonia Co-op installer.
#   install.ps1                  find Gedonia, back up the saves, install BepInEx (if missing) and the mod
#   install.ps1 -Uninstall       remove exactly what this installer added
#   -GameDir <folder>            use this Gedonia folder instead of searching Steam
#   -BackupDir <folder>          where to put the save backup (default: Documents\Gedonia-Coop-Backups)
#   -Yes                         don't ask questions (answers "yes")
param(
    [string]$GameDir,
    [switch]$Uninstall,
    [string]$BackupDir = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Gedonia-Coop-Backups'),
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Pinned BepInEx: official GitHub release, checked by SHA-256 before use.
$bepUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'
$bepSha = '82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4'
# The Gedonia build the mod was tested with (Steam build 16727998).
$testedExe = 'B375205374C5355991FE1C91577CEB729AD9FBC2FC6534FE4EF897288A5DF91B'
$manifestName = 'coop-install.txt'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Ask($question) {
    if ($Yes) { return $true }
    $a = Read-Host "$question [j/n]"
    return $a -match '^(j|ja|y|yes)$'
}

function Find-Gedonia {
    $steam = $null
    try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch { }
    if (-not $steam) { $steam = "${env:ProgramFiles(x86)}\Steam" }
    $libs = @($steam -replace '/', '\')
    $vdf = Join-Path $libs[0] 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"') { $libs += ($m.Matches[0].Groups[1].Value -replace '\\\\', '\') }
    }
    foreach ($lib in ($libs | Select-Object -Unique)) {
        $dir = Join-Path $lib 'steamapps\common\Gedonia'
        if (Test-Path (Join-Path $dir 'Gedonia.exe')) { return $dir }
    }
    return $null
}

Say ''
Say '=== Gedonia Co-op ===' 'Cyan'
if (-not $GameDir) { $GameDir = Find-Gedonia }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'Gedonia.exe'))) {
    Say 'Gedonia wurde nicht gefunden. Starte den Installer mit -GameDir "<Gedonia-Ordner>".' 'Red'
    Say 'Gedonia was not found. Start the installer with -GameDir "<Gedonia folder>".' 'Red'
    exit 1
}
$GameDir = (Resolve-Path $GameDir).Path
Say "Gedonia-Ordner: $GameDir"

if (Get-Process Gedonia -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GameDir\*" }) {
    Say 'Gedonia laeuft gerade. Bitte zuerst das Spiel beenden.' 'Red'
    Say 'Gedonia is running. Please close the game first.' 'Red'
    exit 1
}

$manifest = Join-Path $GameDir "BepInEx\$manifestName"
$plugin = Join-Path $GameDir 'BepInEx\plugins\GedoniaCoop.dll'

# ---------------- uninstall ----------------
if ($Uninstall) {
    if (-not (Test-Path $manifest)) {
        if (Test-Path $plugin) { Remove-Item $plugin; Say 'GedoniaCoop.dll entfernt (keine Installationsliste gefunden; BepInEx bleibt).' 'Yellow' }
        else { Say 'Gedonia Co-op ist hier nicht installiert.' 'Yellow' }
        exit 0
    }
    $lines = Get-Content $manifest
    $files = $lines | Where-Object { $_ -like 'file=*' } | ForEach-Object { $_.Substring(5) }
    foreach ($rel in $files) {
        $p = Join-Path $GameDir $rel
        if (Test-Path $p -PathType Leaf) { Remove-Item $p -Force }
    }
    Remove-Item $manifest -Force
    # What the mod itself wrote: language choice, crystal bindings, list of missing German texts, settings.
    Get-ChildItem (Join-Path $GameDir 'BepInEx\config') -Filter 'gedonia-coop-*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path (Join-Path $GameDir 'BepInEx/config') 'gedonia.coop.cfg') -Force -ErrorAction SilentlyContinue
    # Remove folders we created, deepest first, only if empty (configs or logs
    # BepInEx wrote since then stay with their folder).
    $dirs = $lines | Where-Object { $_ -like 'dir=*' } | ForEach-Object { $_.Substring(4) } | Sort-Object Length -Descending
    foreach ($rel in $dirs) {
        $p = Join-Path $GameDir $rel
        if ((Test-Path $p) -and -not (Get-ChildItem $p -Force)) { Remove-Item $p -Force }
    }
    if ($lines -contains 'bepinex=ours') {
        foreach ($rel in 'BepInEx\LogOutput.log', 'BepInEx\config\BepInEx.cfg', 'BepInEx\cache') {
            $p = Join-Path $GameDir $rel
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
        foreach ($rel in 'BepInEx\plugins', 'BepInEx\patchers', 'BepInEx\config', 'BepInEx\core', 'BepInEx') {
            $p = Join-Path $GameDir $rel
            if ((Test-Path $p) -and -not (Get-ChildItem $p -Force)) { Remove-Item $p -Force }
        }
    }
    Say 'Gedonia Co-op wurde entfernt.' 'Green'
    Say 'Gedonia Co-op was removed.' 'Green'
    if (Test-Path (Join-Path $GameDir 'BepInEx')) { Say "Hinweis: Der Ordner BepInEx bleibt, weil er noch andere Dateien enthaelt." 'Yellow' }
    exit 0
}

# ---------------- install ----------------
$exeHash = (Get-FileHash (Join-Path $GameDir 'Gedonia.exe') -Algorithm SHA256).Hash
if ($exeHash -ne $testedExe) {
    Say 'Achtung: Deine Gedonia-Version ist eine andere als die, mit der der Mod getestet wurde (Steam-Build 16727998).' 'Yellow'
    if (-not (Ask 'Trotzdem installieren?')) { exit 1 }
}

# 1. Save backup (the co-op session changes quests in your save).
$saves = Join-Path $env:USERPROFILE 'AppData\LocalLow\Oleg Kazakov\Gedonia'
if (Test-Path $saves) {
    $dest = Join-Path $BackupDir ('Gedonia-Saves-' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $saves '*') $dest -Recurse -Force
    Say "Spielstaende gesichert nach: $dest" 'Green'
} else {
    Say 'Keine Spielstaende gefunden (noch nie gespielt?) - nichts zu sichern.'
}

$added = New-Object System.Collections.Generic.List[string]
$dirsAdded = New-Object System.Collections.Generic.List[string]
$bepinex = 'existing'
# Update over an earlier install: keep what that one added (so uninstalling
# still removes the BepInEx it brought).
if (Test-Path $manifest) {
    $old = Get-Content $manifest
    if ($old -contains 'bepinex=ours') { $bepinex = 'ours' }
    $old | Where-Object { $_ -like 'file=*' } | ForEach-Object { $added.Add($_.Substring(5)) }
    $old | Where-Object { $_ -like 'dir=*' } | ForEach-Object { $dirsAdded.Add($_.Substring(4)) }
    Say ('Vorherige Installation gefunden (' + (($old | Where-Object { $_ -like 'version=*' }) -replace 'version=', '') + ') - wird aktualisiert.')
}

# 2. BepInEx (the mod loader), unless the game already has it.
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    $bepinex = 'ours'
    $zip = Join-Path $env:TEMP 'BepInEx_win_x64_5.4.23.5.zip'
    $local = Join-Path $here 'BepInEx_win_x64_5.4.23.5.zip'
    if (Test-Path $local) { Copy-Item $local $zip -Force }
    else {
        Say 'Lade BepInEx 5.4.23.5 von GitHub ...'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest $bepUrl -OutFile $zip -UseBasicParsing
    }
    $h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($h -ne $bepSha) { Remove-Item $zip; throw "BepInEx-Download hat die falsche Pruefsumme ($h). Abbruch, nichts installiert." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $archive.Entries) {
            if ($e.FullName.EndsWith('/')) { continue }
            $rel = $e.FullName -replace '/', '\'
            $target = Join-Path $GameDir $rel
            if (Test-Path $target) { continue } # never overwrite
            $dir = Split-Path $target
            if (-not (Test-Path $dir)) {
                $relDir = $dir.Substring($GameDir.Length).TrimStart('\')
                New-Item -ItemType Directory -Force $dir | Out-Null
                $dirsAdded.Add($relDir)
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target)
            $added.Add($rel)
        }
    } finally { $archive.Dispose() }
    Remove-Item $zip
    Say 'BepInEx installiert (Pruefsumme ok).' 'Green'
} else {
    Say 'BepInEx ist schon installiert - wird weiterverwendet.'
}

# 3. The mod.
$pluginDir = Split-Path $plugin
if (-not (Test-Path $pluginDir)) { New-Item -ItemType Directory -Force $pluginDir | Out-Null; $dirsAdded.Add('BepInEx\plugins') }
Copy-Item (Join-Path $here 'GedoniaCoop.dll') $plugin -Force
$added.Add('BepInEx\plugins\GedoniaCoop.dll')
# German texts (de-*.tsv); older ones from an earlier version go first.
Get-ChildItem $pluginDir -Filter 'de-*.tsv' -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($t in Get-ChildItem (Join-Path $here 'lang') -Filter 'de-*.tsv' -ErrorAction SilentlyContinue) {
    Copy-Item $t.FullName (Join-Path $pluginDir $t.Name) -Force
    $added.Add("BepInEx\plugins\$($t.Name)")
}
# German voice-over (voice-de\*.ogg + speakers.tsv); an older set goes first.
$voiceSrc = Join-Path $here 'voice-de'
if (Test-Path $voiceSrc) {
    $voiceDir = Join-Path $pluginDir 'voice-de'
    if (Test-Path $voiceDir) { Get-ChildItem $voiceDir -File -Recurse | Where-Object { $_.Extension -eq '.ogg' -or $_.Extension -eq '.tsv' } | Remove-Item -Force }
    else { New-Item -ItemType Directory -Force $voiceDir | Out-Null; $dirsAdded.Add('BepInEx\plugins\voice-de') }
    # cutscene\ holds the German cutscene lines
    foreach ($sub in Get-ChildItem $voiceSrc -Directory) {
        $d = Join-Path $voiceDir $sub.Name
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force $d | Out-Null; $dirsAdded.Add("BepInEx\plugins\voice-de\$($sub.Name)") }
    }
    foreach ($f in Get-ChildItem $voiceSrc -File -Recurse) {
        $rel = $f.FullName.Substring($voiceSrc.Length).TrimStart('\')
        Copy-Item $f.FullName (Join-Path $voiceDir $rel) -Force
        $added.Add("BepInEx\plugins\voice-de\$rel")
    }
    Say 'Deutsche Sprachausgabe installiert.' 'Green'
}

$version = (Get-Content (Join-Path $here 'VERSION.txt') -ErrorAction SilentlyContinue | Select-Object -First 1)
$lines = @("version=$version", "bepinex=$bepinex", "installed=$(Get-Date -Format s)")
$lines += $added | Select-Object -Unique | ForEach-Object { "file=$_" }
$lines += $dirsAdded | Select-Object -Unique | ForEach-Object { "dir=$_" }
Set-Content -Path $manifest -Value $lines -Encoding UTF8

Say ''
Say "Fertig: Gedonia Co-op $version ist installiert." 'Green'
Say 'Starte Gedonia ganz normal ueber Steam. Im Hauptmenue gibt es jetzt "Co-op hosten" und "Co-op beitreten",'
Say 'im Spiel den Co-op-Knopf oben rechts in der Leiste (oder F8).'
if (Test-Path (Join-Path $here 'lang')) { Say 'Deutsch: Knopf "Deutsch" unten links im Hauptmenue.' }
Say 'Mehr in LIESMICH.txt.'
Say ''
Say "Done: Gedonia Co-op $version is installed. Start Gedonia from Steam as usual; the main menu now has" 'Green'
Say '"Host co-op" and "Join co-op", in game the co-op button in the top bar (or F8). More in README.txt.'
