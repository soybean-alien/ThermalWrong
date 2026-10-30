# ThermalWrong by soybean_alien
# https://github.com/soybean-alien/ThermalWrong · MIT License
#
# Renders tools/banner/banner.html to docs/assets/banner.png (2560x1280) and docs/assets/social-preview.png
# (1280x640, GitHub's social preview; must stay under 1 MB) with a local headless Chrome or Edge.
#   powershell -ExecutionPolicy Bypass -File tools\banner\render.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$browser = @("C:\Program Files\Google\Chrome\Application\chrome.exe", "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
             "C:\Program Files\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "Chrome or Edge is needed" }

# The page is laid out at 1280x640 and loads its fonts from Google Fonts.
New-Item -ItemType Directory -Force "$root\docs\assets" | Out-Null
$html = (Resolve-Path "$PSScriptRoot\banner.html").Path
$profile = Join-Path $env:TEMP "thermalwrong-banner-profile"
foreach ($r in @(@{ Out = "$root\docs\assets\banner.png"; Scale = "2" }, @{ Out = "$root\docs\assets\social-preview.png"; Scale = "1" })) {
    $p = Start-Process $browser -PassThru -WindowStyle Hidden -ArgumentList @("--headless=new", "--disable-gpu", "--hide-scrollbars",
        "--user-data-dir=$profile", "--force-device-scale-factor=$($r.Scale)", "--window-size=1280,640",
        "--virtual-time-budget=8000", "--screenshot=$($r.Out)", ([Uri]$html).AbsoluteUri)
    $p.WaitForExit(60000) | Out-Null
    Write-Host "$(Split-Path $r.Out -Leaf): $([math]::Round((Get-Item $r.Out).Length / 1KB)) KB"
}

# The film grain can push the PNG past GitHub's 1 MB limit; fall back to a high-quality JPEG next to it.
$social = Get-ChildItem "$root\docs" -Recurse -Filter "social-preview.png" | Select-Object -First 1
if ($social.Length -ge 1MB - 48KB) {
    Add-Type -AssemblyName System.Drawing
    $img = [System.Drawing.Image]::FromFile($social.FullName)
    $enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
    $q = New-Object System.Drawing.Imaging.EncoderParameters 1
    $q.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
    $jpg = [IO.Path]::ChangeExtension($social.FullName, ".jpg")
    $img.Save($jpg, $enc, $q); $img.Dispose(); Remove-Item $social.FullName
    Write-Host "social-preview.jpg: $([math]::Round((Get-Item $jpg).Length / 1KB)) KB (PNG was over 1 MB)"
}
