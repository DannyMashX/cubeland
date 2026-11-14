# Publishes a new version of Cubeland to the web (GitHub Pages). Run it by double-clicking publish-update.bat.
#
# Each update gets a version number, MAJOR.MINOR.PATCH (like 0.1.0). You pick what kind of update it is:
#   1 = small fix or tweak  -> 0.1.0 becomes 0.1.1
#   2 = new features        -> 0.1.0 becomes 0.2.0
#   3 = huge milestone      -> 0.1.0 becomes 1.0.0
# The newest version is what players get at the main link (the next time they refresh, after a minute or two).
# A copy of every version is kept in versions\<number>\, and versions.html lists them all so people can still play old ones.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)   # write files as plain UTF-8 (no BOM)

Write-Host 'Packing textures and models...'
& "$PSScriptRoot\build-textures.ps1" | Out-Null

# the version number lives in index.html: const GAME_VERSION = '0.1.0';
$html = [IO.File]::ReadAllText("$PSScriptRoot\index.html")
$m = [regex]::Match($html, "const GAME_VERSION = '(\d+)\.(\d+)\.(\d+)';")
if (-not $m.Success) { throw 'Could not find GAME_VERSION in index.html' }
$major = [int]$m.Groups[1].Value; $minor = [int]$m.Groups[2].Value; $patch = [int]$m.Groups[3].Value

$listFile = "$PSScriptRoot\versions.json"
$list = @()
if (Test-Path $listFile) { $list = @([IO.File]::ReadAllText($listFile) | ConvertFrom-Json) }

if ($list.Count -eq 0) {
  # first ever publish: release the version that's in the game now
  Write-Host "First release: version $major.$minor.$patch"
} else {
  git add -A
  if (-not (git status --porcelain)) {
    Write-Host ''
    Write-Host 'Nothing has changed since the last update, so there is nothing to publish.'
    exit
  }
  Write-Host ''
  Write-Host "The current version is $major.$minor.$patch. What kind of update is this?"
  Write-Host "  1 = small fix or tweak  ($major.$minor.$($patch + 1))"
  Write-Host "  2 = new features        ($major.$($minor + 1).0)"
  Write-Host "  3 = huge milestone      ($($major + 1).0.0)"
  do { $kind = Read-Host 'Type 1, 2 or 3' } while ($kind -notin '1', '2', '3')
  if ($kind -eq '1') { $patch++ }
  elseif ($kind -eq '2') { $minor++; $patch = 0 }
  else { $major++; $minor = 0; $patch = 0 }
}
$ver = "$major.$minor.$patch"
$notes = Read-Host "What's in version $ver? (a few words, or just press Enter)"
if (-not $notes) { $notes = if ($list.Count -eq 0) { 'First release' } else { 'Updates' } }

# put the new number in the game, and keep a copy of this version in versions\<number>\
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
