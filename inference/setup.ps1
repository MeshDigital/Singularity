# Sets up Singularity's AI worker in this folder: a Python 3.11 virtual environment (.venv) with PyTorch
# (CUDA 12.8 when an NVIDIA GPU is present), the model stages, and the model weights.
#
#   powershell -ExecutionPolicy Bypass -File setup.ps1 [-DryRun] [-SkipModels] [-Cpu]
#
# Downloads several GB (PyTorch with CUDA ~3 GB, models ~5 GB) and takes a while. Safe to run again: it picks up
# where it stopped. Singularity finds the worker by itself once .venv exists here.
param(
    [switch]$DryRun,      # print what would happen, change nothing
    [switch]$SkipModels,  # don't download model weights now (the worker names what's missing)
    [switch]$Cpu          # CPU-only PyTorch even with an NVIDIA GPU (much slower)
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$venv = Join-Path $here ".venv"
$python = Join-Path $venv "Scripts\python.exe"

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Run($exe, [string[]]$arguments) {
    Write-Host "> $exe $($arguments -join ' ')" -ForegroundColor DarkGray
    if ($DryRun) { return }
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "'$exe $($arguments -join ' ')' failed with exit code $LASTEXITCODE" }
}

function Find-Python311 {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        & $py.Source -3.11 -c "import sys" 2>$null
        if ($LASTEXITCODE -eq 0) { return @($py.Source, "-3.11") }
    }
    foreach ($candidate in @("$env:LOCALAPPDATA\Programs\Python\Python311\python.exe", "$env:ProgramFiles\Python311\python.exe")) {
        if (Test-Path $candidate) { return @($candidate) }
    }
    return $null
}

try {
    Write-Host "Singularity AI worker setup" -ForegroundColor Green
    Write-Host "Folder: $here"
    if ($DryRun) { Write-Host "(dry run: nothing is changed)" -ForegroundColor Yellow }

    if (-not (Test-Path $python)) {
        Step "Python 3.11"
        $base = Find-Python311
        if (-not $base) {
            Write-Host "Python 3.11 isn't installed; installing it for this user with winget."
            Run "winget" @("install", "--id", "Python.Python.3.11", "-e", "--scope", "user", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity")
            $base = Find-Python311
            if (-not $base -and -not $DryRun) { throw "Python 3.11 still isn't found. Install it from python.org and run this again." }
            if (-not $base) { $base = @("py", "-3.11") }
        }
        Step "Virtual environment"
        Run $base[0] (@($base | Select-Object -Skip 1) + @("-m", "venv", $venv))
    }
    else {
        Step "Virtual environment already there"
    }

    Step "pip"
    Run $python @("-m", "pip", "install", "--upgrade", "pip")

    $gpu = (Get-Command nvidia-smi -ErrorAction SilentlyContinue) -and -not $Cpu
    Step ("PyTorch (" + $(if ($gpu) { "CUDA 12.8, for the NVIDIA GPU" } else { "CPU only: separation and alignment will be slow" }) + ")")
    $index = if ($gpu) { "https://download.pytorch.org/whl/cu128" } else { "https://download.pytorch.org/whl/cpu" }
    Run $python @("-m", "pip", "install", "torch>=2.7", "torchaudio>=2.7,<2.9", "--index-url", $index)

    Step "The worker and its model stages"
    Run $python @("-m", "pip", "install", "$here[ml]")

    if (-not $SkipModels) {
        Step "Model weights"
        Push-Location $here
        try { Run $python @("-m", "singularity_inference.models", "fetch") } finally { Pop-Location }
    }

    Write-Host ""
    Write-Host "Done. Restart Singularity, or open Settings > System and it finds the worker." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Setup stopped: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Run it again to continue from here."
}
if (-not $DryRun -and [Environment]::UserInteractive) { Read-Host "Press Enter to close" | Out-Null }
