# Publishes a new version of Cubeland to the web (GitHub Pages). Run it by double-clicking publish-update.bat.
#
# Each update is numbered by the date it's published plus which update of the day it is: YY.MM.DD_N.
#   The first update on 14 November 2026 is 26.11.14_1, the second that day is 26.11.14_2, and so on.
# The newest version is what players get at the main link (the next time they refresh, after a minute or two).
# A copy of every version is kept in versions\<number>\, and versions.html lists them all so people can still play old ones.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)   # write files as plain UTF-8 (no BOM)
$draftHeader = "# Changes for the next update, one per line. publish-update.bat shows this list when you publish,`r`n" +
               "# puts it in the change log, then empties this file. Lines starting with # are ignored.`r`n"

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
if (Test-Path $listFile) { foreach ($v in (ConvertFrom-Json ([IO.File]::ReadAllText($listFile)))) { $list += $v } }
$today = Get-Date -Format 'yy.MM.dd'
$n = 1
foreach ($v in $list) { if ($v.version -match "^$([regex]::Escape($today))_(\d+)$") { $n = [Math]::Max($n, [int]$Matches[1] + 1) } }
$ver = "${today}_$n"

# the change log for this version: the list in next-changes.txt (Claude adds a line there for each change we make),
# or one you type in now. Players see it in the game's "What's new" and on the All versions page.
$draftFile = "$PSScriptRoot\next-changes.txt"
$changes = @()
if (Test-Path $draftFile) {
  $changes = @([IO.File]::ReadAllLines($draftFile) | ForEach-Object { $_.Trim() -replace '^[-*]\s*', '' } | Where-Object { $_ -and -not $_.StartsWith('#') })
}
if ($changes.Count) {
  Write-Host ''
  Write-Host "Changes in version ${ver}:"
  $changes | ForEach-Object { Write-Host "  - $_" }
  $ok = Read-Host 'Press Enter to use this list, or type N to write your own'
  if ($ok -match '^[nN]') { $changes = @() }
}
if (-not $changes.Count) {
  Write-Host ''
  Write-Host "List what changed in version $ver, one thing per line. Press Enter on an empty line when you're done."
  while ($true) { $line = Read-Host '-'; if (-not $line) { break }; $changes += $line.Trim() }
}
if (-not $changes.Count) { $changes = @('Small fixes and tweaks') }
$notes = $changes[0]

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
$entry = [pscustomobject]@{ version = $ver; date = (Get-Date -Format 'yyyy-MM-dd'); notes = $notes; changes = [object[]]$changes }
$list = @($entry) + @($list | Where-Object { $_.version -ne $ver })
[IO.File]::WriteAllText($listFile, (ConvertTo-Json -InputObject $list -Depth 4), $utf8)
# start a fresh draft list for the next update
[IO.File]::WriteAllText($draftFile, $draftHeader, $utf8)

# (the commit message goes through a file, so quote marks in the change log can't confuse git)
$msgFile = Join-Path $env:TEMP 'cubeland-commit-message.txt'
$trailer = if ($env:CUBELAND_COMMIT_TRAILER) { "`n$env:CUBELAND_COMMIT_TRAILER`n" } else { '' }   # (used when Claude publishes for you)
[IO.File]::WriteAllText($msgFile, "Version ${ver}: $notes`n`n" + (($changes | ForEach-Object { "- $_" }) -join "`n") + "`n" + $trailer, $utf8)
git add -A
git commit -q -F $msgFile
if ($LASTEXITCODE) { throw 'Saving the update failed (git commit). Nothing was uploaded.' }
git tag "v$ver"
Write-Host 'Uploading...'
git push -q
if ($LASTEXITCODE) { throw 'Uploading failed (git push). Check your internet connection and run this again.' }
git push -q --tags
Write-Host ''
Write-Host "Done! Version $ver will be live in a minute or two."
$url = (Get-Content "$PSScriptRoot\.game-url" -ErrorAction SilentlyContinue)
if ($url) { Write-Host "Game link: $url"; Write-Host "All versions: ${url}versions.html" }
