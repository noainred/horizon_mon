# Horizon Service Monitor — 단일 실행 파일 빌드 스크립트 (Windows, .NET 8 SDK 필요)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
  dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
  $exe = Join-Path $PSScriptRoot "publish\HorizonServiceMonitor.exe"
  Write-Host "빌드 완료: $exe"
} finally { Pop-Location }
