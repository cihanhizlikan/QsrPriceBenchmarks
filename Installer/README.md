# Installer — self-contained setup program

This builds a single `QsrPriceBenchmarks-Setup.exe` you can hand to a non-technical user. Running it installs the
app under *Program Files*, adds Start-menu and Desktop shortcuts, and shows up in *Settings → Apps*. **The target
PC needs no .NET runtime and no Playwright/Chromium download** — all of it is bundled.

The setup is written for **Inno Setup 7** (`QsrPriceBenchmarks.iss`).

## Build it (on your machine)

Two publish profiles on the Ui project do the whole build — publish, bundle the browser, compile the setup:

| Profile | Setup | Visual Studio | Terminal |
|---|---|---|---|
| **Installer** | bundles Chromium; fully self-contained | right-click **QsrPriceBenchmarks.Ui → Publish → Installer → Publish** | `dotnet publish Ui -p:PublishProfile=Installer` |
| **InstallerSystemEdge** | drives the PC's installed Edge, much smaller | same, choosing **InstallerSystemEdge** | `dotnet publish Ui -p:PublishProfile=InstallerSystemEdge` |

Output: `Installer\bin\Release\QsrPriceBenchmarks-Setup.exe`. The steps after the publish live in
`Ui\Properties\PublishProfiles\Installer.targets`:

1. Empty `publish\ui`, so nothing from an earlier build goes into the setup.
2. `dotnet publish` the WPF app **self-contained** for `win-x64`, which bundles .NET 10 (no `.pdb` or `.xml` doc
   files).
3. Install Chromium into the publish folder (`ms-playwright\`) so scraping works offline — or, for system Edge,
   write `browser-channel.txt`.
4. Compile `QsrPriceBenchmarks.iss` with Inno Setup's `ISCC.exe`, packing the whole publish folder. A missing or
   empty publish folder fails the compile rather than producing an empty setup.

For ARM64 Windows add `-p:RuntimeIdentifier=win-arm64` to the `dotnet publish` line.

### Which browser mode?

The app scrapes JS-rendered pages by driving a browser **engine** through Playwright — this is automation, not a
window the user clicks around in, and it is separate from whatever browser they use day to day.

| Profile | Browser used | Target-PC requirement |
|------|--------------|------------------------|
| **Installer** | the Chromium build Playwright manages, bundled into the app | none |
| **InstallerSystemEdge** | the PC's installed **Microsoft Edge** | a current Edge (it ships with Windows 10/11) |

The Edge profile writes a `browser-channel.txt` marker (`msedge`) next to the exe; at startup `BrowserSession`
reads it and tells Playwright to drive the installed Edge, so no Chromium is shipped or downloaded. Setting the
`QSR_BROWSER_CHANNEL` environment variable (`msedge` or `chrome`) forces the same on any install.

## Build-machine prerequisites (one-time)

| Tool | Why | Get it |
|------|-----|--------|
| .NET 11 SDK | build and publish the app, which targets .NET 10 | already required to build the solution (`global.json`) |
| Inno Setup 7 | compiles the setup | https://jrsoftware.org/isdl.php — found through its own Apps entry; `-p:InnoSetupCompiler=<path to ISCC.exe>` overrides |
| Internet | downloads Chromium once | only on the build machine |

## What the user does

1. Run `QsrPriceBenchmarks-Setup.exe` and accept the Windows UAC prompt (it installs for every user, into Program
   Files).
2. Launch **QSR Price Benchmarks** from the Start menu or Desktop.

**Upgrading** is running a newer setup: it replaces the installed version in place. A setup older than the installed
version refuses and says so. **A 3.x install made by the earlier MSI is removed automatically** before the new files
are copied, so there is never a second entry in Apps. The database is never touched by installing, upgrading or
uninstalling: it lives in `%LOCALAPPDATA%\QsrPriceBenchmarks`. An upgrade to a version that changes the database's
shape asks once, on first start, whether to start a new database; Yes keeps the old file beside it, renamed.

## Notes

* **Size**: the default setup is large because it carries the .NET runtime and a Chromium build; the Edge profile
  drops the Chromium portion.
* **Versioning**: `Version` in `Directory.Build.props` at the solution root is bumped with every change that ships
  (the working agreement sizes it); `Installer.targets` passes it to the compiler as the setup's `AppVersion`, and
  the assemblies and the in-app version badge read the same number. Keep `AppId` in `QsrPriceBenchmarks.iss`
  **unchanged**: it is how a new setup finds the installed one.
* **Why a browser at all**: default builds bundle Chromium and `BrowserSession.EnsureBrowsersPath()` finds the
  `ms-playwright` folder beside the exe; Edge builds drive installed Edge instead. Development builds (neither
  marker present) use the normal per-user Playwright cache.
