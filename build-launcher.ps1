# Builds the CubeLand Launcher (launcher\) and the desktop game, ready to upload as GitHub release files:
#   builds\CubeLand-Launcher-windows.zip       - what people download from the site
#   builds\CubeLand-<version>-windows.zip       - the game, which the launcher downloads
#                                                 (version = the same as the web game's, e.g. CubeLand-Alpha-0.0.5.1-windows.zip)
# The launcher uses your font and gui pictures: they're copied from textures\ into launcher\textures\ first.
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
Export 'launcher' "$PWD\builds\CubeLand-Launcher-windows\CubeLand Launcher.exe"
$tag = $ver -replace '\s+', '-'   # "Alpha 0.0.5.1" -> "Alpha-0.0.5.1"
Export '.' "$PWD\builds\CubeLand-$tag-windows\CubeLand.exe"
