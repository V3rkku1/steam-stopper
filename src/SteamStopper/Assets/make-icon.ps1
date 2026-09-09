Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeIcon {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern bool DestroyIcon(IntPtr hIcon);
}
"@

function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::FromArgb(255, 14, 14, 14))
  $green = [System.Drawing.Color]::FromArgb(255, 50, 215, 75)
  $penW = [Math]::Max(2.0, $size / 13.0)
  $pen = New-Object System.Drawing.Pen $green, $penW
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $m = [float]($size * 0.2)
  $d = [float]($size - 2 * $m)
  $g.DrawEllipse($pen, $m, $m, $d, $d)
  $g.DrawLine($pen, [float]($size * 0.30), [float]($size * 0.70), [float]($size * 0.70), [float]($size * 0.30))
  $pen.Dispose()
  $g.Dispose()
  return $bmp
}

$out = "C:\Users\verne\Downloads\Steamtools\src\SteamStopper\Assets\app.ico"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

$bmp = New-IconBitmap 256
$ptr = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($ptr)
$fs = [System.IO.File]::Create($out)
$icon.Save($fs)
$fs.Dispose()
[void][NativeIcon]::DestroyIcon($ptr)
$bmp.Dispose()
Write-Output "Wrote $out ($((Get-Item $out).Length) bytes)"
