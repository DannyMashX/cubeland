# Builds the CubeLand Launcher (launcher\) and the desktop game, ready to upload as GitHub release files:
#   builds\CubeLand-Launcher-windows.zip       - what people download from the site
#   builds\CubeLand-<version>-windows.zip       - the game, which the launcher downloads
#                                                 (version = the same as the web game's, e.g. CubeLand-Alpha-0.0.5.1-windows.zip)
# The launcher uses your font and gui pictures: they're copied from textures\ into launcher\textures\ first.
# (and data\texture-names.json into launcher\data\: the font's letter names)
# Needs Godot 4.7.2 (.NET) and its export templates, at the paths below.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$godot = "$env:USERPROFILE\Tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"; $env:Path = "$env:USERPROFILE\.dotnet;" + $env:Path
$ver = ([regex]::Match((Get-Content project.godot -Raw), 'config/version="([^"]+)"')).Groups[1].Value

# your pictures, for the launcher
foreach ($d in 'font', 'gui') {
  $dst = "launcher\textures\$d"
  if (Test-Path $dst) { Remove-Item -Recurse -Force -LiteralPath $dst }
  New-Item -ItemType Directory -Force $dst | Out-Null
  Copy-Item -Recurse "textures\$d\*" $dst -Exclude *.import
}
# and the font's letter names (data\texture-names.json's FONT_NAMES, read by Skin.cs)
New-Item -ItemType Directory -Force launcher\data | Out-Null
Copy-Item data\texture-names.json launcher\data\texture-names.json

function Export($path, $out) {
  $ErrorActionPreference = 'Continue'   # (Godot prints harmless warnings, like skipping the launcher folder, which mustn't stop this)
  $dir = Split-Path $out
  if (Test-Path $dir) { Remove-Item -Recurse -Force -LiteralPath $dir }
  New-Item -ItemType Directory -Force $dir | Out-Null
  & $godot --headless --path $path --import 2>&1 | Out-Null
  $log = & $godot --headless --path $path --export-release "Windows" $out 2>&1
  $errors = $log | Where-Object { "$_" -match '^ERROR' }
  if ($errors) { $errors; throw "Exporting $path failed." }
  $zip = "$dir.zip"
  if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
  Compress-Archive -Path $dir -DestinationPath $zip -CompressionLevel Optimal
  "{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
}
New-Item -ItemType Directory -Force builds | Out-Null
if (-not (Test-Path builds\.gdignore)) { [IO.File]::WriteAllText("$PWD\builds\.gdignore", '') }

# The launcher is only rebuilt when something in launcher\ (its code, scenes or your font and gui pictures) has
# changed since the last build, since it takes about 10 minutes. Run with -Launcher to rebuild it anyway.
# (The fingerprint skips Godot's own working files, which change by themselves.)
$root = (Resolve-Path 'launcher').Path
$sha = [Security.Cryptography.SHA256]::Create()
$parts = Get-ChildItem -Recurse -File -LiteralPath $root |
  Where-Object { $_.FullName -notmatch '\\(\.godot|bin|obj)\\' -and $_.Extension -ne '.import' } |
  Sort-Object FullName |
  ForEach-Object { $_.FullName.Substring($root.Length) + ':' + [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($_.FullName))) }
$launcherHash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((@($godot) + $parts) -join "`n")))
$hashFile = "$PWD\builds\launcher.hash"
$launcherZip = "$PWD\builds\CubeLand-Launcher-windows.zip"
$unchanged = (Test-Path $launcherZip) -and (Test-Path $hashFile) -and ([IO.File]::ReadAllText($hashFile).Trim() -eq $launcherHash)
if ($unchanged -and -not ($args -contains '-Launcher')) {
  "Launcher unchanged since the last build - skipping it ($launcherZip)"
  & { $ErrorActionPreference = 'Continue'; & $godot --headless --path launcher --import 2>&1 | Out-Null }   # (puts back Godot's .import files for the freshly copied pictures)
} else {
  Export 'launcher' "$PWD\builds\CubeLand-Launcher-windows\CubeLand Launcher.exe"
  [IO.File]::WriteAllText($hashFile, $launcherHash)
}
$tag = $ver -replace '\s+', '-'   # "Alpha 0.0.5.1" -> "Alpha-0.0.5.1"
Export '.' "$PWD\builds\CubeLand-$tag-windows\CubeLand.exe"
