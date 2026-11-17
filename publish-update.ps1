# Publishes a new version of Cubeland to the web (GitHub Pages). Run it by double-clicking publish-update.bat.
#
# You name each update's version when you publish (e.g. Alpha 0.0.1.1). Press Enter instead to use the date and time
#   it's published (24-hour clock), YY.MM.DD.HHMM: 4:08 pm on 14 November 2026 is 26.11.14.1608. That was the style
#   before Alpha 0.0.1.0 (and the first three versions used 26.11.14_1 to _3).
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

# the version: the name you give it (like Alpha 0.0.1.1), or, if you just press Enter, today's date and the time
# right now (24-hour clock), YY.MM.DD.HHMM, e.g. 4:08 pm on 14 November 2026 = 26.11.14.1608 (9:05 am = 26.11.14.905)
$listFile = "$PSScriptRoot\versions.json"
$list = @()
if (Test-Path $listFile) { foreach ($v in (ConvertFrom-Json ([IO.File]::ReadAllText($listFile)))) { $list += $v } }
$ver = if ($env:CUBELAND_VERSION) { $env:CUBELAND_VERSION } else {   # (CUBELAND_VERSION is set when Claude publishes for you)
  Write-Host ''
  Write-Host "Last version: $(if ($list.Count) { $list[0].version } else { 'none' })"
  Read-Host 'Version for this update (e.g. Alpha 0.0.1.1), or press Enter to use the date and time'
}
$ver = "$ver".Trim()
if (-not $ver) { $now = Get-Date; $ver = $now.ToString('yy.MM.dd') + '.' + [int]$now.ToString('HHmm') }
if ($ver -match "[^A-Za-z0-9 ._-]") { throw "A version can only have letters, numbers, spaces, dots, dashes and underscores." }
if ($list | Where-Object { $_.version -eq $ver }) { throw "Version $ver was already published. Pick another version and run this again." }
# its folder in versions\ (and its tag): lower case with dashes for spaces, so "Alpha 0.0.1.0" is alpha-0.0.1.0
$folder = ($ver.ToLower() -replace '\s+', '-')
# a name for big updates, about what they add (like "Fired Up" for clay and the furnace); small ones have none
$title = if ($env:CUBELAND_VERSION) { "$env:CUBELAND_TITLE" } else {   # (CUBELAND_TITLE is set when Claude publishes for you)
  Read-Host 'Name for this update (for big updates, e.g. Fired Up), or press Enter for none'
}
$title = "$title".Trim()

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
# tag textures.js with the version too, so browsers load the new letters/textures instead of an old saved copy
$html = [regex]::Replace($html, 'src="textures\.js(\?v=[^"]*)?"', "src=`"textures.js?v=$folder`"")
[IO.File]::WriteAllText("$PSScriptRoot\index.html", $html, $utf8)
# the desktop (Godot) game uses the same version number (multiplayer only connects when they match);
# its download is built separately with build-launcher.ps1
$godotFile = "$PSScriptRoot\project.godot"
if (Test-Path $godotFile) {
  $proj = [IO.File]::ReadAllText($godotFile)
  $proj = [regex]::Replace($proj, 'config/version="[^"]*"', "config/version=`"$ver`"")
  [IO.File]::WriteAllText($godotFile, $proj, $utf8)
}
$dir = "$PSScriptRoot\versions\$folder"
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$PSScriptRoot\index.html", "$PSScriptRoot\textures.js" $dir -Force

# add it to the top of the list of versions
$entry = [ordered]@{ version = $ver }
if ($title) { $entry.title = $title }
$entry.folder = $folder; $entry.date = (Get-Date -Format 'yyyy-MM-dd'); $entry.notes = $notes; $entry.changes = [object[]]$changes
$entry = [pscustomobject]$entry
$list = @($entry) + @($list | Where-Object { $_.version -ne $ver })
$json = ConvertTo-Json -InputObject $list -Depth 4
[IO.File]::WriteAllText($listFile, $json, $utf8)
# the same list as a script, which the game and versions.html load (works even when opened straight from your computer)
[IO.File]::WriteAllText("$PSScriptRoot\versions.js", "// Generated by publish-update.ps1 - do not edit by hand.`r`nwindow.VERSIONS = $json;`r`n", $utf8)
# start a fresh draft list for the next update
[IO.File]::WriteAllText($draftFile, $draftHeader, $utf8)

# (the commit message goes through a file, so quote marks in the change log can't confuse git)
$msgFile = Join-Path $env:TEMP 'cubeland-commit-message.txt'
$trailer = if ($env:CUBELAND_COMMIT_TRAILER) { "`n$env:CUBELAND_COMMIT_TRAILER`n" } else { '' }   # (used when Claude publishes for you)
[IO.File]::WriteAllText($msgFile, "Version ${ver}$(if ($title) { " ($title)" }): $notes`n`n" + (($changes | ForEach-Object { "- $_" }) -join "`n") + "`n" + $trailer, $utf8)
git add -A
git commit -q -F $msgFile
if ($LASTEXITCODE) { throw 'Saving the update failed (git commit). Nothing was uploaded.' }
git tag "v$folder"
Write-Host 'Uploading...'
git push -q
if ($LASTEXITCODE) { throw 'Uploading failed (git push). Check your internet connection and run this again.' }
git push -q --tags
Write-Host ''
Write-Host "Done! Version $ver will be live in a minute or two."
$url = (Get-Content "$PSScriptRoot\.game-url" -ErrorAction SilentlyContinue)
if ($url) { Write-Host "Game link: $url"; Write-Host "All versions: ${url}versions.html" }
