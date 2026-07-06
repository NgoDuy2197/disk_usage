# Chuyen icon.jpg -> icon.ico (nhieu kich co, PNG-in-ICO, Vista+)
# Duoc goi tu build.bat
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$jpg = Join-Path $PSScriptRoot 'icon.jpg'
$ico = Join-Path $PSScriptRoot 'icon.ico'
if (-not (Test-Path $jpg)) { Write-Host '[BO QUA] Khong co icon.jpg'; exit 0 }

$src = [System.Drawing.Image]::FromFile($jpg)
try {
    $sizes = 256, 64, 48, 32, 16
    $pngs = New-Object System.Collections.ArrayList
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        # scale kieu "fill" (cat bot phan thua, khong meo hinh)
        $scale = [Math]::Max($s / $src.Width, $s / $src.Height)
        $w = [int][Math]::Ceiling($src.Width * $scale)
        $h = [int][Math]::Ceiling($src.Height * $scale)
        $g.DrawImage($src, [int](($s - $w) / 2), [int](($s - $h) / 2), $w, $h)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        [void]$pngs.Add($ms.ToArray())
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($ico)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0)              # reserved
    $bw.Write([UInt16]1)              # type = icon
    $bw.Write([UInt16]$sizes.Count)   # so luong anh
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }   # 0 nghia la 256
        $bw.Write([Byte]$dim)                       # width
        $bw.Write([Byte]$dim)                       # height
        $bw.Write([Byte]0)                          # palette
        $bw.Write([Byte]0)                          # reserved
        $bw.Write([UInt16]1)                        # planes
        $bw.Write([UInt16]32)                       # bpp
        $bw.Write([UInt32]$pngs[$i].Length)         # kich thuoc du lieu
        $bw.Write([UInt32]$offset)                  # offset du lieu
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
    $bw.Close()
    $fs.Close()
    Write-Host "[OK] Da tao icon.ico ($($sizes -join ', ') px)"
}
finally { $src.Dispose() }
