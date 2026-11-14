# Publishes a new version of Cubeland to the web (GitHub Pages). Run it by double-clicking publish-update.bat.
#
# Each update is numbered by the date it's published plus which update of the day it is: YY.MM.DD_N.
#   The first update on 14 November 2026 is 26.11.14_1, the second that day is 26.11.14_2, and so on.
# The newest version is what players get at the main link (the next time they refresh, after a minute or two).
# A copy of every version is kept in versions\<number>\, and versions.html lists them all so people can still play old ones.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)   # write files as plain UTF-8 (no BOM)

Write-Host 'Packing textures and models...'
& "$PSScriptRoot\build-textures.ps1" | Out-Null

git add -A
if (-not (git status --porcelain)) {
  Write-Host ''
  Write-Host 'Nothing has changed since the last update, so there is nothing to publish.'
  exit
}

# today's date, plus one more than the number of updates already published today
$listFile = "$PSScriptRoot\versions.json"
$list = @()
if (Test-Path $listFile) { $list = @([IO.File]::ReadAllText($listFile) | ConvertFrom-Json) }
$today = Get-Date -Format 'yy.MM.dd'
$n = 1
foreach ($v in $list) { if ($v.version -match "^$([regex]::Escape($today))_(\d+)$") { $n = [Math]::Max($n, [int]$Matches[1] + 1) } }
$ver = "${today}_$n"

$notes = Read-Host "What's in version ${ver}? (a few words, or just press Enter)"
if (-not $notes) { $notes = 'Updates' }

# put the new number in the game, and keep a copy of this version in versions\<number>\
$html = [IO.File]::ReadAllText("$PSScriptRoot\index.html")
$m = [regex]::Match($html, "const GAME_VERSION = '[^']*';")
if (-not $m.Success) { throw 'Could not find GAME_VERSION in index.html' }
$html = $html.Remove($m.Index, $m.Length).Insert($m.Index, "const GAME_VERSION = '$ver';")
[IO.File]::WriteAllText("$PSScriptRoot\index.html", $html, $utf8)
$dir = "$PSScriptRoot\versions\$ver"
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$PSScriptRoot\index.html", "$PSScriptRoot\textures.js" $dir -Force

# add it to the top of the list of versions
$entry = [pscustomobject]@{ version = $ver; date = (Get-Date -Format 'yyyy-MM-dd'); notes = $notes }
$list = @($entry) + @($list | Where-Object { $_.version -ne $ver })
[IO.File]::WriteAllText($listFile, (ConvertTo-Json -InputObject $list -Depth 3), $utf8)

git add -A
git commit -q -m "Version ${ver}: $notes"
git tag "v$ver"
Write-Host 'Uploading...'
git push -q
git push -q --tags
Write-Host ''
Write-Host "Done! Version $ver will be live in a minute or two."
$url = (Get-Content "$PSScriptRoot\.game-url" -ErrorAction SilentlyContinue)
if ($url) { Write-Host "Game link: $url"; Write-Host "All versions: ${url}versions.html" }
