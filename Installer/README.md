# Installer — self-contained MSI

This builds a single `.msi` you can hand to a non-technical user. Double-clicking
it installs the app under *Program Files*, adds Start-menu + Desktop shortcuts,
and shows up in *Add/Remove Programs*. **The target PC needs no .NET runtime and
no Playwright/Chromium download** — all of it is bundled.

## Build it (on your machine)

Two publish profiles on the Ui project do the whole build — publish, bundle the
browser, build the MSI:

| Profile | MSI | Visual Studio | Terminal |
|---|---|---|---|
| **Installer** | bundles Chromium; fully self-contained (~355 MB) | right-click **QsrPriceBenchmarks.Ui → Publish → Installer → Publish** | `dotnet publish Ui -p:PublishProfile=Installer` |
| **InstallerSystemEdge** | drives the PC's installed Edge (~87 MB) | same, choosing **InstallerSystemEdge** | `dotnet publish Ui -p:PublishProfile=InstallerSystemEdge` |

Output: `Installer\bin\Release\QsrPriceBenchmarks-Setup.msi`. The steps after
the publish live in `Ui\Properties\PublishProfiles\Installer.targets`; no
PowerShell is involved. The older script still works and does the same:

```powershell
pwsh ./build-installer.ps1                   # = Installer profile
pwsh ./build-installer.ps1 -UseSystemBrowser # = InstallerSystemEdge profile
```

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

Both routes run the same steps:

1. Empty `publish\ui`, so nothing from an earlier build is harvested.
2. `dotnet publish` the WPF app **self-contained** for `win-x64` → bundles .NET 10
   (no `.pdb` or `.xml` doc files).
3. Install Chromium into the publish folder (`ms-playwright\`) so scraping works
   offline — or, for system Edge, write `browser-channel.txt`.
4. Build the MSI with WiX v5, harvesting the whole publish tree. A harvest that
   finds no folder fails the build rather than producing an empty MSI.

For ARM64 Windows: `pwsh ./build-installer.ps1 -Rid win-arm64`, or add
`-p:RuntimeIdentifier=win-arm64` to the `dotnet publish` line.

## Build-machine prerequisites (one-time)

| Tool | Why | Get it |
|------|-----|--------|
| .NET 10 SDK | build/publish the app | already required to build the solution |
| PowerShell 7 (`pwsh`) | only for `build-installer.ps1`; the publish profiles don't need it | https://aka.ms/powershell |
| WiX v5 SDK | builds the MSI | restored automatically on first `dotnet build` of the `.wixproj` |
| Internet | downloads Chromium once | only on the build machine |

## What the user does

1. Double-click `QsrPriceBenchmarks-Setup.msi`.
2. Accept the Windows UAC prompt (per-machine install into Program Files).
3. Launch **QSR Price Benchmarks** from the Start menu or Desktop.

An upgrade to a version that changes the database's shape asks once, on first
start, whether to start a new database: Yes keeps the old file beside it, renamed.

That's it — no runtime install, no browser download.

## Notes

* **Size**: the default MSI is large (~250–400 MB) because it embeds the .NET
  runtime and a Chromium build. `-UseSystemBrowser` drops the Chromium portion
  (~150 MB) by driving the PC's installed Edge instead.
* **Versioning**: `Version` in `Directory.Build.props` at the solution root is
  bumped with every change that ships (the working agreement sizes it) —
  `Package.wxs` reads it as `$(var.ProductVersion)`, and so do the assemblies
  and the in-app version badge. Keep `UpgradeCode` **unchanged** so new
  versions upgrade in place rather than installing side-by-side.
* **Why a browser at all**: the app scrapes JS-rendered pages by driving a
  browser engine through Playwright (automation, not the user's everyday
  browser). Default builds bundle Chromium and `BrowserSession.EnsureBrowsersPath()`
  auto-detects the `ms-playwright` folder beside the exe; `-UseSystemBrowser`
  builds drive installed Edge via a channel instead. Dev builds (neither marker
  present) fall back to the normal per-user Playwright cache.
* **Not in the .sln**: the WiX project is intentionally left out of
  `QsrPriceBenchmarks.sln` so `dotnet build` of the solution doesn't require the
  publish output to exist. Build it through a publish profile (or the script, or
  `dotnet build` the `.wixproj` directly after publishing).
