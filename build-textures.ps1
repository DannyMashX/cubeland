# Packs every PNG in the textures folder (and its subfolders), every model .json in the model folder and every shared
# data file in the data folder into textures.js so the game can use them.
# Run update-textures.bat (or this script) again whenever you change a texture or model.
#   textures\blocks\*.png and textures\items\*.png  -> named by file name       (e.g. "dirt")
#   textures\blocks\sheets\*.png                    -> cut into its pictures    (see below)
#   any other subfolder                             -> named by folder path     (e.g. "entity/mobs/danthechocolateman/body")
#   model\*.json                                    -> window.MODELS by name    (e.g. "danthechocolateman")
# Texture sheets: textures\blocks\sheets\<name>.png is a 3x3 grid laid out like the block's face, cut into the pictures
# the game asks for: "<name>", "<name>_top_half", ... and the pictures built from their quarters ("<name>_no_top_left",
# "<name>_slab_side", ...), as textures\sheet-layout.json says (the desktop edition reads the same file). A fully
# see-through cell is a picture that isn't there. A loose file with the same name as a picture from a sheet wins (with
# a warning), so a half-changed set still works.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dir  = Join-Path $root 'textures'
$sheetDir = Join-Path $dir 'blocks\sheets'
$packed = [ordered]@{}   # name -> base64 PNG

function PngBase64($bmp) {
  $ms = New-Object IO.MemoryStream
  $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
  $b = [Convert]::ToBase64String($ms.ToArray()); $ms.Dispose(); return $b
}
function IsEmpty($bmp) {   # every pixel fully see-through?
  $r = New-Object Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
  $data = $bmp.LockBits($r, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
  [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
  $bmp.UnlockBits($data)
  for ($i = 3; $i -lt $bytes.Length; $i += 4) { if ($bytes[$i] -ne 0) { return $false } }
  return $true
}
# copy a w x h block of pixels exactly (no smoothing, no blending)
function CopyRect($g, $src, $sx, $sy, $dx, $dy, $w, $h) {
  $g.DrawImage($src, (New-Object Drawing.Rectangle $dx, $dy, $w, $h), (New-Object Drawing.Rectangle $sx, $sy, $w, $h), [Drawing.GraphicsUnit]::Pixel)
}

# 1. loose pictures
foreach ($f in Get-ChildItem $dir -Filter *.png -Recurse | Sort-Object FullName) {
  if ($f.DirectoryName -eq $sheetDir) { continue }   # (sheets: below)
  $rel = $f.FullName.Substring($dir.Length + 1) -replace '\\', '/'
  $folder = ($rel -split '/')[0].ToLower()
  if ($rel -notmatch '/' -or $folder -eq 'blocks' -or $folder -eq 'items') { $name = $f.BaseName.ToLower() }
  else { $name = ($rel.Substring(0, $rel.Length - 4)).ToLower() }
  $packed[$name] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName))
  Write-Host "Packed $rel"
}

# 2. sheets
if (Test-Path $sheetDir) {
  # (read with .NET's JSON reader: Windows PowerShell's ConvertFrom-Json can't take the "" key of the full picture)
  Add-Type -AssemblyName System.Web.Extensions
  $layout = (New-Object Web.Script.Serialization.JavaScriptSerializer).DeserializeObject((Get-Content (Join-Path $dir 'sheet-layout.json') -Raw))
  # grid: 3 or [3, 3] (cells across, down); cells: { suffix: [column, row] } or rows of suffixes
  if ($layout['grid'] -is [array]) { $n = [int]$layout['grid'][0]; $rows = [int]$layout['grid'][1] } else { $n = $rows = [int]$layout['grid'] }
  $cellAt = @{}
  if ($layout['cells'] -is [array]) { for ($r = 0; $r -lt $rows; $r++) { for ($q = 0; $q -lt $n; $q++) { $cellAt[[string]$layout['cells'][$r][$q]] = @($q, $r) } } }
  else { foreach ($k in $layout['cells'].Keys) { $cellAt[$k] = @([int]$layout['cells'][$k][0], [int]$layout['cells'][$k][1]) } }
  # the full picture's cell is "full" (or ""): its picture has no suffix, just the sheet's name
  $sfx = { param($s) if ([string]$s -eq 'full') { '' } else { [string]$s } }
  $fixed = @{}; foreach ($k in $cellAt.Keys) { $fixed[(& $sfx $k)] = $cellAt[$k] }; $cellAt = $fixed
  $quarterAt = @{ TL = @(0, 0); TR = @(1, 0); BL = @(0, 1); BR = @(1, 1); top_left = @(0, 0); top_right = @(1, 0); bottom_left = @(0, 1); bottom_right = @(1, 1) }
  foreach ($f in Get-ChildItem $sheetDir -Filter *.png | Sort-Object Name) {
    $base = $f.BaseName.ToLower()
    $img = New-Object Drawing.Bitmap $f.FullName
    if ($img.Width % $n -ne 0 -or $img.Width / $n * $rows -ne $img.Height -or ($img.Width / $n) % 2 -ne 0) {
      Write-Warning "sheets/$($f.Name) is $($img.Width)x$($img.Height): a sheet must be $n x $rows square cells, each an even number of pixels. Skipped."
      $img.Dispose(); continue
    }
    $c = $img.Width / $n; $h = $c / 2
    $sheet = $img.Clone((New-Object Drawing.Rectangle 0, 0, $img.Width, $img.Height), [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $img.Dispose()
    $cells = @{}   # suffix -> bitmap (only the cells that have something in them)
    foreach ($suffix in $cellAt.Keys) {
      $col = $cellAt[$suffix][0]; $row = $cellAt[$suffix][1]
      $bmp = $sheet.Clone((New-Object Drawing.Rectangle ($col * $c), ($row * $c), $c, $c), [Drawing.Imaging.PixelFormat]::Format32bppArgb)
      if (IsEmpty $bmp) { $bmp.Dispose(); continue }
      $cells[$suffix] = $bmp
    }
    $out = [ordered]@{}
    foreach ($k in $cells.Keys) { $out[$k] = $cells[$k] }
    foreach ($name in $layout['compose'].Keys) {
      $parts = $layout['compose'][$name]
      if (@($parts.Values | Where-Object { -not $cells.ContainsKey((& $sfx $_)) }).Count) { continue }   # (a cell it needs isn't there)
      $bmp = New-Object Drawing.Bitmap $c, $c, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
      $g = [Drawing.Graphics]::FromImage($bmp)
      $g.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
      $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
      $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
      foreach ($q in $parts.Keys) {
        $at = $quarterAt[$q]
        CopyRect $g $cells[(& $sfx $parts[$q])]($at[0] * $h) ($at[1] * $h) ($at[0] * $h) ($at[1] * $h) $h $h
      }
      $g.Dispose()
      $out[$name] = $bmp
    }
    foreach ($k in $out.Keys) {
      $name = $base + $k
      if ($packed.Contains($name)) { Write-Warning "$name is a loose file and also comes from sheets/$($f.Name): using the loose file" }
      else { $packed[$name] = PngBase64 $out[$k] }
    }
    foreach ($b in $out.Values) { $b.Dispose() }
    $sheet.Dispose()
    Write-Host "Cut sheets/$($f.Name) into $($out.Count) pictures"
  }
}

$lines = @('// Generated by build-textures.ps1 - do not edit by hand.', 'window.TEXTURES = {')
foreach ($k in $packed.Keys) { $lines += "  `"$k`": `"data:image/png;base64,$($packed[$k])`"," }
$lines += '};'
$lines += 'window.MODELS = {'
$mdir = Join-Path $root 'model'
if (Test-Path $mdir) {
  foreach ($f in Get-ChildItem $mdir -Filter *.json | Sort-Object Name) {
    $json = (Get-Content $f.FullName -Raw) -replace "(\r?\n)+", ' '
    $lines += "  `"$($f.BaseName.ToLower())`": $json,"
    Write-Host "Packed model $($f.Name)"
  }
}
$lines += '};'
# Shared data files: every data\*.json goes in here too, as window.GAME_DATA.<file name> ("numbers" for numbers.json).
# (In textures.js, not a file of its own, because the launcher downloads just index.html and textures.js for each web
# version.) The desktop edition reads the same files (res://data/*.json). A file that isn't valid JSON stops the build,
# so it can't break the game.
Add-Type -AssemblyName System.Web.Extensions
$lines += 'window.GAME_DATA = {'
$ddir = Join-Path $root 'data'
if (Test-Path $ddir) {
  foreach ($f in Get-ChildItem $ddir -Filter *.json | Sort-Object Name) {
    $json = ([IO.File]::ReadAllText($f.FullName)).Trim()
    try { $null = (New-Object Web.Script.Serialization.JavaScriptSerializer).DeserializeObject($json) }
    catch { throw "data\$($f.Name) isn't valid JSON: $($_.Exception.InnerException.Message) - textures.js was not changed." }
    $lines += "  `"$($f.BaseName.ToLower())`": $json,"
    Write-Host "Packed data $($f.Name)"
  }
}
$lines += '};'
Set-Content -Path (Join-Path $root 'textures.js') -Value $lines -Encoding UTF8
Write-Host 'Done - textures.js updated. Refresh the game to see your textures.'
