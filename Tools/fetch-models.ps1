<#
.SYNOPSIS
    Copies the Demucs stem-separation model into Tools/Essentia/models.

.DESCRIPTION
    Singularity's only bundled model is demucs-4s.onnx (vocal/instrumental separation). It is
    kept out of git. By default it is copied from a sibling ORBIT-Pure checkout; pass -Source to
    copy it from another folder. DemucsModelManager also looks in %APPDATA%\Singularity\Models,
    so placing the file there works too.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\..\ORBIT-Pure\Tools\Essentia\models")
)

$ErrorActionPreference = "Stop"
$modelName = "demucs-4s.onnx"
$dest = Join-Path $PSScriptRoot "Essentia\models"
$sourceFile = Join-Path $Source $modelName
$targetFile = Join-Path $dest $modelName

if (Test-Path $targetFile) {
    Write-Host "$modelName is already present in $dest"
    return
}

if (-not (Test-Path $sourceFile)) {
    throw "$modelName not found in $Source. Pass -Source with a folder that contains it."
}

# An un-smudged LFS pointer is a tiny text file; copying it would give a corrupt model.
$file = Get-Item $sourceFile
if ($file.Length -lt 1024 -and (Get-Content $file.FullName -TotalCount 1) -like "version https://git-lfs*") {
    throw "$modelName in the source is an LFS pointer, not the model. Run 'git lfs pull' in that checkout first."
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $sourceFile $targetFile
Write-Host "Copied $modelName into $dest"
