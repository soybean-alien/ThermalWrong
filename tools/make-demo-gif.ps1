# ThermalWrong by soybean_alien
# https://github.com/soybean-alien/ThermalWrong · MIT License
#
# Re-records docs\demo.gif: the bundled Poseidon sample exactly as ThermalWrong sends it to the
# screen, with the live CPU/GPU temperatures from this PC drawn on top. Needs TRCC's sensor helper
# running (it is while TRCC or ThermalWrong runs) and ffmpeg (TRCC ships one).
#   powershell -File tools\make-demo-gif.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$frames = Join-Path $env:TEMP 'thermalwrong-demo-frames'
$gif = Join-Path $root 'docs\demo.gif'
New-Item -ItemType Directory -Force (Split-Path $gif) | Out-Null
if (Test-Path $frames) { Remove-Item $frames -Recurse -Force }

# Every 3rd frame of the 30 fps clip = 10 fps over the full 9 second loop; keeps the GIF README-sized.
dotnet run --project (Join-Path $root 'src\ThermalWrong.Cli') -c Release -- frames (Join-Path $root 'samples\Poseidon Underwater.mp4') $frames --every 3
if ($LASTEXITCODE -ne 0) { throw 'frame export failed' }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { $ffmpeg = Join-Path $env:ProgramFiles 'ThermalRight-TRCC\TRCCCAP\ffmpeg.exe' }
$filter = 'scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96:stats_mode=full[p];[b][p]paletteuse=dither=bayer:bayer_scale=4'
& $ffmpeg -hide_banner -loglevel error -y -framerate 10 -i (Join-Path $frames 'frame%04d.jpg') -filter_complex $filter -loop 0 $gif
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed' }
Remove-Item $frames -Recurse -Force
"{0} ({1:N1} MB)" -f $gif, ((Get-Item $gif).Length / 1MB)
