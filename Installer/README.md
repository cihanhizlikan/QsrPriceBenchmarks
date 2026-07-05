# Installer — self-contained MSI

This builds a single `.msi` you can hand to a non-technical user. Double-clicking
it installs the app under *Program Files*, adds Start-menu + Desktop shortcuts,
and shows up in *Add/Remove Programs*. **The target PC needs no .NET runtime and
no Playwright/Chromium download** — all of it is bundled.

## Build it (on your machine)

```powershell
pwsh ./build-installer.ps1                   # bundles Chromium (fully self-contained)
pwsh ./build-installer.ps1 -UseSystemBrowser # smaller MSI, drives installed Edge
```

Output: `Installer\bin\Release\QsrPriceBenchmarks-Setup.msi`

### Which browser mode?

The app scrapes JS-rendered pages by driving a browser **engine** through
Playwright — this is automation, not a window the user clicks around in, and it
is separate from whatever browser they use day to day. Two ways to provide it:

| Mode | Browser used | MSI size | Target-PC requirement |
|------|--------------|----------|------------------------|
| default | Chromium build Playwright manages, bundled into the app | large (~250–400 MB) | none |
| `-UseSystemBrowser` | the PC's installed **Microsoft Edge** (or Chrome) | small | a current Edge/Chrome (Edge ships with Win10/11) |

`-UseSystemBrowser` writes a `browser-channel.txt` marker (`msedge`) next to the
exe; at startup `BrowserSession` reads it and tells Playwright to drive the
installed Edge via its channel feature, so no Chromium is shipped or downloaded.
You can also force this on any install by setting the `QSR_BROWSER_CHANNEL`
environment variable (`msedge` or `chrome`).

The script runs four steps:

1. `dotnet publish` the WPF app **self-contained** for `win-x64` → bundles .NET 10.
2. Installs Chromium into the publish folder (`ms-playwright\`) so scraping works offline.
3. Builds the MSI with WiX v5, harvesting the whole publish tree.
4. Prints the final `.msi` path and size.

For ARM64 Windows: `pwsh ./build-installer.ps1 -Rid win-arm64`.

## Build-machine prerequisites (one-time)

| Tool | Why | Get it |
|------|-----|--------|
| .NET 10 SDK | build/publish the app | already required to build the solution |
| PowerShell 7 (`pwsh`) | runs `playwright install` | https://aka.ms/powershell |
| WiX v5 SDK | builds the MSI | restored automatically on first `dotnet build` of the `.wixproj` |
| Internet | downloads Chromium once | only on the build machine |

## What the user does

1. Double-click `QsrPriceBenchmarks-Setup.msi`.
2. Accept the Windows UAC prompt (per-machine install into Program Files).
3. Launch **QSR Price Benchmarks** from the Start menu or Desktop.

That's it — no runtime install, no browser download.

## Notes

* **Size**: the default MSI is large (~250–400 MB) because it embeds the .NET
  runtime and a Chromium build. `-UseSystemBrowser` drops the Chromium portion
  (~150 MB) by driving the PC's installed Edge instead.
* **Versioning**: bump `Version` in `Package.wxs` for each release. Keep
  `UpgradeCode` **unchanged** so new versions upgrade in place rather than
  installing side-by-side.
* **Why a browser at all**: the app scrapes JS-rendered pages by driving a
  browser engine through Playwright (automation, not the user's everyday
  browser). Default builds bundle Chromium and `BrowserSession.EnsureBrowsersPath()`
  auto-detects the `ms-playwright` folder beside the exe; `-UseSystemBrowser`
  builds drive installed Edge via a channel instead. Dev builds (neither marker
  present) fall back to the normal per-user Playwright cache.
* **Not in the .sln**: the WiX project is intentionally left out of
  `QsrPriceBenchmarks.sln` so `dotnet build` of the solution doesn't require the
  publish output to exist. Build it via the script (or `dotnet build` the
  `.wixproj` directly after publishing).
