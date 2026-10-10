param([switch]$Portable)

$projectDirectory = $PSScriptRoot
Set-Location -LiteralPath $projectDirectory

$ErrorActionPreference = 'Stop'

Get-Process -Name WindowsCodexUsageTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200

foreach ($directory in @('bin', 'obj')) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
        Write-Host "Removed $directory"
    }
}

if ($Portable) {
    & .\build.ps1 -Portable
}
else {
    & .\build.ps1
}

if ($LASTEXITCODE -ne 0) {
    throw 'Clean build failed.'
}

Write-Host 'Clean build complete.'
