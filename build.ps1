param([switch]$Portable)

$projectDirectory = $PSScriptRoot
Set-Location -LiteralPath $projectDirectory

$ErrorActionPreference = 'Stop'
Get-Process -Name WindowsCodexUsageTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200
if ($Portable) {
  dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
} else {
  dotnet build -c Release
}
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host 'Build complete.'
