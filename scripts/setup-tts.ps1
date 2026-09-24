<#
.SYNOPSIS
    Creates the Python environment for `murch dub`: torch (CUDA 12.8) + coqui-tts (XTTS-v2 voice cloning).

.DESCRIPTION
    murch itself is .NET; the voice-cloning TTS models are PyTorch, so they live in a separate venv and are driven
    through scripts/tts_worker.py. Needs Python 3.10, 3.11 or 3.12 on the machine (3.13+ is not supported by torch/coqui yet).

    Default location: <repo>\tools\tts-venv (picked up automatically). Use -Target to put it elsewhere and set
    MURCH_TTS_PYTHON to <Target>\Scripts\python.exe.

.EXAMPLE
    .\scripts\setup-tts.ps1
    .\scripts\setup-tts.ps1 -Python "C:\Python311\python.exe" -Target "$env:LOCALAPPDATA\Bwl.Murching\tts-venv"
    .\scripts\setup-tts.ps1 -Chatterbox      # also install Resemble Chatterbox (multilingual, MIT)
#>
param(
    [string]$Python = "",
    [string]$Target = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\tts-venv"),
    [switch]$Cpu,
    [switch]$Chatterbox
)
$ErrorActionPreference = 'Stop'

if (-not $Python) {
    foreach ($v in '3.12', '3.11', '3.10') {
        $found = & py "-$v" -c "import sys; print(sys.executable)" 2>$null
        if ($found) { $Python = $found.Trim(); break }
    }
    if (-not $Python) { throw "Python 3.10-3.12 not found. Install it (winget install Python.Python.3.12) or pass -Python." }
}
Write-Host "Python : $Python"
Write-Host "Target : $Target"

& $Python -m venv $Target
$pip = Join-Path $Target "Scripts\python.exe"
& $pip -m pip install --upgrade pip

if ($Cpu) {
    & $pip -m pip install torch==2.8.0 torchaudio==2.8.0
} else {
    & $pip -m pip install torch==2.8.0 torchaudio==2.8.0 --index-url https://download.pytorch.org/whl/cu128   # 2.9+ needs torchcodec + FFmpeg shared DLLs
}
& $pip -m pip install coqui-tts "transformers>=4.43,<5"   # coqui-tts 0.27 breaks with transformers 5.x
if ($Chatterbox) { & $pip -m pip install chatterbox-tts }

& $pip -c "import torch, TTS; print('torch', torch.__version__, 'cuda:', torch.cuda.is_available()); print('coqui-tts', TTS.__version__)"
Write-Host ""
Write-Host "Done. XTTS-v2 weights (~1.9 GB) download on first use into %LOCALAPPDATA%\tts. Licence: Coqui Public Model License (non-commercial)."
Write-Host "Try:  murch dub video.mp4 --to ru"
