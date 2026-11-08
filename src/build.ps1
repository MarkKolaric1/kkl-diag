# Builds "KKL Diag.exe" next to this folder using the C# compiler that ships with Windows.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$out = Split-Path $src -Parent
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = "$fw\WPF"

# csc needs a short temp path
$tmp = Join-Path $out 'build-tmp'
New-Item -ItemType Directory -Force $tmp | Out-Null
$oldTmp = $env:TMP; $oldTemp = $env:TEMP
$env:TMP = $tmp; $env:TEMP = $tmp
try {

# App icon (rounded blue square with a lightning bolt), regenerated if missing
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap 256, 256
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $r = 56
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc(256 - $r, 0, $r, $r, 270, 90)
    $path.AddArc(256 - $r, 256 - $r, $r, $r, 0, 90); $path.AddArc(0, 256 - $r, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(37, 99, 235))), $path)
    $font = New-Object Drawing.Font 'Segoe MDL2 Assets', 120
    $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString([string][char]0xE945, $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 8, 256, 256), $fmt)
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $png = $ms.ToArray()
    $w = New-Object IO.BinaryWriter ([IO.File]::Create($ico))
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]1)              # ICONDIR
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0) # 256x256
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$png.Length); $w.Write([UInt32]22)
    $w.Write($png); $w.Close()
}

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out\KKL Diag.exe" "/win32icon:$ico" `
    "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" "/r:$fw\System.Management.dll" "/r:System.dll" "/r:System.Core.dll" `
    "/resource:$src\Shell.xaml,KklDiag.Shell.xaml" `
    (Get-ChildItem "$src\*.cs" | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built: $out\KKL Diag.exe"
}
finally {
    $env:TMP = $oldTmp; $env:TEMP = $oldTemp
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

