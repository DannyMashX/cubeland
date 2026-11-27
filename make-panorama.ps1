# Takes the main menu's panorama: six pictures (panorama\0.jpg .. 5.jpg, a cube around one spot) that the main menu
# slowly turns through, in both the desktop and web versions. Take a new one for each update, somewhere that shows off
# what the update adds. It uses the desktop version (Godot) to grow a fresh Biomes world (or, with -Earth, an Earthlike one)
# and take the pictures.
#
#   .\make-panorama.ps1 -Seed pumpkin -At 503.5,80,170.5
#   .\make-panorama.ps1 -Earth -Size 1024 -Seed 777 -At 515.5,400,513.5
#
#   -Seed   the world's seed, as you'd type it when making a world (text or a number)
#   -At     where the camera stands: x,y,z (pick y a little above the ground, so you can see out over things)
#   -Time   the time of day (0.3 = morning sun, 0.5 = noon)
#   -Small  grow a 640 x 640 world instead of a 1280 x 1280 one (Biomes)
#   -Earth  an Earthlike world instead (its sea is at y 320), -Size 1024 or 2048 across (2048 if left out)
#   -Stay   how many seconds to let the land grow first (8; 30 for Earthlike, whose land grows round the camera)
#   -Godot  the Godot console .exe (found by itself if it's in your user folder's Tools\godot)
# The old pictures are replaced. Open the game afterwards to see the new panorama behind the main menu.
param(
  [Parameter(Mandatory = $true)][string]$Seed,
  [Parameter(Mandatory = $true)][string[]]$At,
  [double]$Time = 0.3,
  [switch]$Small,
  [switch]$Earth,
  [int]$Size = 2048,
  [int]$Stay = 0,
  [string]$Godot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$At = ($At -join ',') -replace '\s', ''   # (-At 1,2,3 arrives as a list)

if (-not $Godot) {
  $Godot = Get-ChildItem (Join-Path $env:USERPROFILE 'Tools\godot') -Recurse -Filter '*mono*console.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
  if (-not $Godot) { throw 'Could not find Godot. Pass its console .exe with -Godot.' }
}

# the seed as a number, the same way the game turns text into one
$n = 0L
if ($Seed -match '^-?\d+$') { $n = [int64]$Seed } else { foreach ($c in $Seed.ToCharArray()) { $n = ($n * 31 + [int][char]$c) -band 0xFFFFFFFFL } }
$n = $n -band 0xFFFFFFFFL; if ($n -ge 0x80000000L) { $n -= 0x100000000L }

# build the desktop version first (so the pictures come from the code as it is now)
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $dotnet) { $env:DOTNET_ROOT = Split-Path $dotnet; & $dotnet build (Join-Path $root 'Cubeland.csproj') -nologo -v q | Out-Null }

# take them (with its own settings folder, so your own settings and worlds aren't touched)
$tmp = Join-Path $env:TEMP 'cubeland-panorama'
New-Item -ItemType Directory -Force $tmp | Out-Null
$oldAppData = $env:APPDATA; $env:APPDATA = $tmp
$culture = [Globalization.CultureInfo]::InvariantCulture
$out = Join-Path $root 'panorama'
if ($Stay -le 0) { $Stay = if ($Earth) { 30 } else { 8 } }
$kind = if ($Earth) { @('--earth', "--size=$Size") } else { @('--biomes') }
$args2 = @('--path', $root, '--', "--seed=$n") + $kind + @('--play', "--at=$At,0,0", "--time=$($Time.ToString($culture))", "--stay=$Stay", "--shot=$tmp\shot.png", "--panorama=$out")
if ($Small -and -not $Earth) { $args2 += '--small' }
try { $log = & $Godot @args2 2>&1 | Out-String } finally { $env:APPDATA = $oldAppData }
if ($log -notmatch 'panorama saved') { Write-Host $log; throw 'The panorama was not taken (see above).' }

# let Godot pick up the new pictures
$ErrorActionPreference = 'Continue'   # (Godot writes harmless lines to stderr here)
& $Godot --headless --path $root --import 2>&1 | Out-Null
Write-Host "Done - the new panorama is in $out (seed $Seed = $n, at $At)."
