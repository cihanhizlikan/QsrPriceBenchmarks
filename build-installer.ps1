#requires -Version 5.1
<#
  Builds a self-contained MSI for the QSR Price Benchmarks WPF app.

  Output:  Installer\bin\Release\QsrPriceBenchmarks-Setup.msi

  The resulting MSI needs NOTHING on the target PC — no .NET 10 runtime and no
  Playwright/Chromium download. Everything is bundled.

  -UseSystemBrowser builds a much smaller MSI that drives the PC's installed
  Microsoft Edge (preinstalled on Windows 10/11) instead of bundling Chromium.
  Use it when you'd rather rely on the system browser than ship ~150 MB.

  Build-machine prerequisites (one-time):
    * .NET 10 SDK            (you already have this to build the app)
    * PowerShell 7 (pwsh)    https://aka.ms/powershell   — used to install Chromium
    * WiX v5 SDK             restored automatically on first build of the .wixproj
    * Internet access        only on THIS machine, to download Chromium once
                             (not needed with -UseSystemBrowser)

  Usage (from the solution root):
    pwsh ./build-installer.ps1
    pwsh ./build-installer.ps1 -UseSystemBrowser   # smaller MSI, uses system Edge
    pwsh ./build-installer.ps1 -Rid win-arm64      # for ARM64 Windows targets
#>
param(
    [string]$Rid = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$UseSystemBrowser
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$publish = Join-Path $root 'publish\ui'
$uiProj  = Join-Path $root 'Ui\QsrPriceBenchmarks.Ui.csproj'
$wixProj = Join-Path $root 'Installer\QsrPriceBenchmarks.Installer.wixproj'

Write-Host "==> [1/4] Cleaning $publish" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "==> [2/4] Publishing self-contained WPF app ($Rid, $Configuration)" -ForegroundColor Cyan
# self-contained=true bundles the .NET 10 runtime; NOT single-file so the MSI
# can harvest individual files. Trimming is OFF (WPF + reflection libs break).
dotnet publish $uiProj `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=none `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($UseSystemBrowser) {
    Write-Host "==> [3/4] Configuring app to use the system browser (Microsoft Edge)" -ForegroundColor Cyan
    # Ship a marker file the app reads at startup (BrowserSession.ResolveBrowserChannel)
    # so it drives installed Edge via Playwright's channel instead of a Chromium build.
    Set-Content -Path (Join-Path $publish 'browser-channel.txt') -Value 'msedge' -NoNewline -Encoding utf8
    Write-Host "    No Chromium bundled — the target PC must have Microsoft Edge (Win10/11 default)."
}
else {
    Write-Host "==> [3/4] Bundling Chromium into the app folder" -ForegroundColor Cyan
    # Install browsers INTO the publish tree (ms-playwright\). At runtime the app
    # auto-discovers this folder (see BrowserSession.EnsureBrowsersPath), so the
    # target PC never downloads anything.
    $browsersDir = Join-Path $publish 'ms-playwright'
    $playwrightPs1 = Join-Path $publish 'playwright.ps1'
    if (-not (Test-Path $playwrightPs1)) {
        throw "playwright.ps1 not found in publish output ('$playwrightPs1'). " +
              "Did the Playwright package get published? Check Core.csproj."
    }
    if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) {
        throw "PowerShell 7 (pwsh) is required to install Chromium. Install from https://aka.ms/powershell"
    }
    $env:PLAYWRIGHT_BROWSERS_PATH = $browsersDir
    pwsh $playwrightPs1 install chromium
    if ($LASTEXITCODE -ne 0) { throw "playwright chromium install failed ($LASTEXITCODE)" }
}

Write-Host "==> [4/4] Building MSI" -ForegroundColor Cyan
# The MSI architecture must match the published app's RID, otherwise harvested
# components mismatch INSTALLFOLDER (ProgramFiles64Folder) and every file trips
# ICE80. Map the RID to the WiX platform value.
switch ($Rid) {
    'win-x64'   { $wixPlatform = 'x64' }
    'win-arm64' { $wixPlatform = 'arm64' }
    'win-x86'   { $wixPlatform = 'x86' }
    default     { throw "Unsupported RID '$Rid'. Use win-x64, win-arm64, or win-x86." }
}
dotnet build $wixProj -c $Configuration -p:PublishDir=$publish -p:InstallerPlatform=$wixPlatform
if ($LASTEXITCODE -ne 0) { throw "WiX build failed ($LASTEXITCODE)" }

$msi = Join-Path $root "Installer\bin\$Configuration\QsrPriceBenchmarks-Setup.msi"
Write-Host ""
if (Test-Path $msi) {
    $sizeMb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
    Write-Host "SUCCESS: $msi  ($sizeMb MB)" -ForegroundColor Green
    Write-Host "Give this single .msi file to the user — double-click installs everything." -ForegroundColor Green
} else {
    Write-Warning "Build reported success but MSI not found at expected path: $msi"
    Write-Host "Check Installer\bin\$Configuration\ for the output."
}
