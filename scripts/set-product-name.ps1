# Gives a game COPY its own Unity product name so its saves
# (Application.persistentDataPath) and PlayerPrefs registry key are separate
# from the real game's. Unity reads both natively from PlayerSettings in
# globalgamemanagers; app.info alone only moves Player.log.
#
# "Gedonia" serializes as int32 length + 7 bytes + 1 pad byte = 12 bytes, and
# any 5-8 char name also takes 12 bytes, so no offsets in the file change.
param(
    [Parameter(Mandatory)] [string]$Game,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9]{5,8}$')] [string]$Product
)
$ErrorActionPreference = 'Stop'
$file = Join-Path $Game 'Gedonia_Data\globalgamemanagers'
if ($Game -like '*\steamapps\common\*') { throw 'Refusing to touch the real install' }

$bytes = [IO.File]::ReadAllBytes($file)
$offset = 4148  # found by search on buildid 16727998; checked below
$company = [Text.Encoding]::ASCII.GetBytes('Oleg Kazakov')
$len = [BitConverter]::ToInt32($bytes, $offset)
$current = [Text.Encoding]::ASCII.GetString($bytes, $offset + 4, $len)
$companyOk = [BitConverter]::ToInt32($bytes, 4132) -eq 12 -and
    [Text.Encoding]::ASCII.GetString($bytes, 4136, 12) -eq 'Oleg Kazakov'
if (-not $companyOk -or $len -lt 5 -or $len -gt 8) { throw "Unexpected layout at $offset (len=$len)" }
if ($current -eq $Product) { "already $Product"; return }

[Array]::Copy([BitConverter]::GetBytes([int]$Product.Length), 0, $bytes, $offset, 4)
for ($i = 0; $i -lt 8; $i++) { $bytes[$offset + 4 + $i] = 0 }
$p = [Text.Encoding]::ASCII.GetBytes($Product)
[Array]::Copy($p, 0, $bytes, $offset + 4, $p.Length)

# Write a new file instead of editing in place: game2 hard-links game's files.
$tmp = "$file.new"
[IO.File]::WriteAllBytes($tmp, $bytes)
Remove-Item $file
Move-Item $tmp $file

# Marks the folder as a test copy: the plugin then refuses to run on the
# real save folder (a normal install has no marker and uses the real saves).
Set-Content -Path (Join-Path $Game 'coop-test-copy') -Value 'Developer test copy' -Encoding ascii

$appInfo = Join-Path $Game 'Gedonia_Data\app.info'
Remove-Item $appInfo
[IO.File]::WriteAllText($appInfo, "Oleg Kazakov`n$Product")
"$Game : '$current' -> '$Product'"
