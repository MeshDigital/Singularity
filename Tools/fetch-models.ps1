<#
.SYNOPSIS
    Populates Tools/Essentia/models with the AI models Singularity inherits from ORBIT.

.DESCRIPTION
    The models (~770 MB) are kept out of git to avoid burning the account's shared Git LFS quota.
    By default they are copied from a sibling ORBIT-Pure checkout (which stores them in LFS).
    Pass -Source to copy from somewhere else. Existing files are skipped.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\..\ORBIT-Pure\Tools\Essentia\models")
)

$ErrorActionPreference = "Stop"
$dest = Join-Path $PSScriptRoot "Essentia\models"

if (-not (Test-Path $Source)) {
    throw "Model source not found: $Source`nClone https://github.com/MeshDigital/Orbit-pure next to this repo (with git lfs installed) or pass -Source."
}

New-Item -ItemType Directory -Force $dest | Out-Null
$copied = 0
foreach ($file in Get-ChildItem $Source -File) {
    # An un-smudged LFS pointer is a tiny text file; copying it would give a corrupt model.
    if ($file.Length -lt 1024 -and (Get-Content $file.FullName -TotalCount 1) -like "version https://git-lfs*") {
        throw "$($file.Name) in the source is an LFS pointer, not the model. Run 'git lfs pull' in the ORBIT-Pure checkout first."
    }
    $target = Join-Path $dest $file.Name
    if (-not (Test-Path $target)) {
        Copy-Item $file.FullName $target
        $copied++
    }
}
Write-Host "Copied $copied model file(s) into $dest"
