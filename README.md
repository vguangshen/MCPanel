# MCPanel

Modern WPF panel for configuring and installing .NET and Java software.

## Install MCPanel

Download [MCPanel-Setup.exe](https://github.com/vguangshen/MCPanel/releases/latest/download/MCPanel-Setup.exe)
and choose an installation directory. The small Chinese native installer runs
without .NET, checks for .NET Framework 4.6.2 or newer and Visual C++ 2010 SP1 x64,
and downloads only missing dependencies from Microsoft. The application package
and dependency installers are bound to SHA-256 pins. The installer does not force
a Windows restart; when a prerequisite requests one, restart before launching.

Close the panel from its system-tray menu before installing over an existing copy.
If component services hold the program open, stop those services before retrying.
Repair/upgrade retains database and website data and merges original configuration,
including recovery from a missing executable and an existing rollback snapshot.
Uninstall removes owned panel files while retaining configuration and operational
data; registered component services that depend on the panel must be handled
before uninstalling it. In-app updates preserve the uninstaller and refresh its
file-ownership manifest. The portable ZIP remains available as a separate asset.

Build an installer for an existing version-matched release ZIP:

```powershell
.\Build-OnlineInstaller.ps1 -PackageFile 'C:\MCPanel-1.3.62.zip'
```

Pass `-PackageUrl 'https://downloads.example.com/MCPanel-1.3.62.zip'` to use your
own HTTPS download site; serve the exact same ZIP. The generated native EXE pins
that version and checksum. Its stable release asset name supports the download
link above without changing website code for each release. `Installer/dependencies.json`
is the pinned source for runtime and compiler metadata. Chinese translation is
vendored from the official Inno Setup 6.7.3 tag with its attribution.

For offline administrative deployment, place the matching official ZIP on the
computer and run `MCPanel-Setup.exe /PACKAGEFILE="C:\MCPanel-1.3.62.zip"`.
Missing Microsoft runtime packages still require network access. Silent deployment
supports standard Inno options such as `/VERYSILENT /NORESTART /DIR="D:\MCPanel"`.

## Current Scope

- Material-style shell with navigation for Home, Sites, Products, Environment, AI diagnostics, and settings.
- Home dashboard with uptime, version, CPU, memory, disk, and suite service status.
- Product management with local legacy product fallback, online catalog refresh, product search, download status, and progress.
- Product installation checks IIS/Tomcat and database prerequisites before downloading, then re-applies the original Store version/database configuration after every SVN update.
- Java/Tomcat products are restored in the background after Windows logon without opening the main panel window.
- Closing the main window hides it to the system tray while the lightweight queue supervision and durable install recovery remain active; use the tray icon to restore the window or the tray menu to exit MCPanel completely.
- When panel auto-start is enabled, Windows starts `MCPanel.exe --tray`, so sign-in does not construct or display the main window. Existing Nginx, Tomcat, database, and FRP processes continue independently while the panel UI is closed.
- Account API has a dedicated left navigation page for the embedded HTTP service, HMAC key lifecycle, health checks, redacted logs, start-stop, and optional logon auto-start. Its original routes and database methods are compiled into MCPanel; no standalone Account API executable is required.
- Product websites support Nginx-managed domains, PEM certificates, HTTPS redirect, WebSocket forwarding, and per-connection bandwidth limits.
- The Sites page can create and manage independent IIS websites, application pools, host bindings, PFX certificates, HTTPS redirect, MIME mappings, and site bandwidth limits.
- Product cache is stored beside the application under `Cache/products-cache.json`.
- SOAP integration for `http://regservice.itmc.cn/Service.asmx`.
- Download credentials for the legacy update/WebDAV/SVN store are kept in `McPanelStoreClient`.

## Code layout

The main window code-behind is kept focused on window lifecycle and UI event
routing. Its settings, product, and environment handlers live in
`MainWindow.*.cs` partials; reusable bindings are grouped under `ViewModels`,
`Controls`, `Converters`, and `Services`. The reliability tests follow the
same split under `MCPanel.Tests`, so adding a new feature does not require
extending one monolithic source file. Environment installers and runtime probes
are split into component partials (`EnvironmentInstaller.*.cs` and
`EnvironmentRuntimeService.*.cs`), with shared entry points for the current component controls.
Home and Environment controls share the same operation coordinator and Tomcat
mode-switch confirmation. Runtime samples run in the background; a component
operation invalidates older samples, and a failed probe retains the last known
state of that component while other cards continue updating.

Product installs use the durable queue worker; the previous single-request worker,
shared Tomcat HTTP-readiness loop, website status monitor, and single-task download
progress model have been removed. The progress overlay is used for uninstall work;
download state and controls belong to each queue item. `LegacyRuntimeCompatibility.cs`
provides APIs missing from the supported .NET Framework 4.6.2 runtime. Old storage,
credential, service-registration and update-channel migrations remain active upgrade
paths and are retained.

Disk snapshots and inactive queue-session maintenance live in dedicated services
under `Services`. Disk sampling runs off the UI thread at ten-second intervals;
CPU and uptime retain their one-second updates. Queue list snapshots remain stable
during progress updates and are replaced only when their membership changes.

UI tests use an explicit initialization option and a temporary queue data root to
render the real window without starting installers, runtime synchronization, or
startup registration. The public window constructor retains normal startup behavior.

The Account API page polls the authenticated `/health/live` endpoint without
opening database connections. Use its database check button to run the existing
`/health` diagnostic; results show their check time and expire after one minute.
The original cloud routes are unchanged. Stop the Account API before importing
bridge settings, then enable it again to apply the imported listener and signing
configuration.

## Environment download URLs

The published application reads environment package URLs from `MCPanel.exe.config` beside the executable. Edit the matching `Environment.*` values before the next installation; no rebuild is needed.

- `Environment.Iis.UrlRewriteUrl` — IIS URL Rewrite MSI. IIS itself is enabled through Windows features.
- `Environment.Nginx.PackageUrl` — Nginx package.
- `Environment.MySql.PackageUrl` — MySQL package.
- `Environment.SqlServer.Sql2025ExpressUrl`, `Sql2025EnterpriseDeveloperUrl`, `Sql2022ExpressUrl`, `Sql2017ExpressUrl`, `Sql2012ExpressX64Url`, `Sql2012ExpressX86Url`, `Sql2008ExpressX64Url`, and `Sql2008ExpressX86Url` — SQL Server installers for the supported editions and architectures.
- `Environment.Tomcat.PackageUrl` — Tomcat package.
- `Environment.Frp.PackageUrl` — FRP Windows amd64 download source. The default is the NJU `LatestRelease/` directory, without a versioned filename. Each installation resolves the latest stable release from GitHub, then downloads that version from the mirror with an official-download fallback. If the API is unavailable, MCPanel discovers the latest stable Windows amd64 ZIP in the mirror directory. Existing versioned URLs are updated dynamically; custom direct ZIP URLs must serve the resolved version. If neither source can establish the latest version, installation stops instead of reusing an older release. Caches, archive paths, and the installed version marker follow the resolved version; SHA-256 is checked when supplied by the official release. Starting or managing an installed client does not check for updates or download files.

Values must be full `http://` or `https://` URLs. MCPanel validates every catalog entry at startup and reports the exact missing or invalid keys. The installer refuses to start with an incomplete executable configuration instead of silently choosing a different package. Customized values are preserved when the application updates; the complete built-in catalog remains available for tests and explicit compatibility loading.

AI analysis uses the same `MCPanel.exe.config`: edit `Ai.Provider`, `Ai.Endpoint`, `Ai.Model`, and `Ai.ApiKey`. The API key is stored as plain text in this file because it is an administrator-managed deployment setting; keep the file access restricted.

## Run

```powershell
dotnet run --project .\MCPanel.csproj
```

## Publish

```powershell
.\Publish-WinX64.ps1
```

The default release is written to `D:\MCPanel`; pass `-OutputDirectory` to
choose another location. The canonical list of top-level entries preserved by
publishing, packaging, and in-app replacement is maintained in
`deployment-layout.json`. Its `preservedAppSettingKeys` list also controls
configuration merging for both in-app updates and publishing into an existing
installation, including explicitly empty AI settings. Conflicting legacy download
cache entries are retained for inspection rather than deleted. Older `StoreData\Runtime`,
`StoreData\Downloads`, `StoreData\Tools`, and `StoreData\ProductIcons`
directories are migrated on startup.

## Private GitHub Release updates

MCPanel can check the latest private GitHub Release from the repository named
by `GitHubUpdateRepository` in `MCPanel.exe.config`. The distributed default is
`vguangshen/MCPanel`.

On each computer, click **Settings → Software update → Configure GitHub** once
and save a fixed fine-grained GitHub token that is limited to that repository
and has only **Contents: read** permission. The token is protected with Windows
DPAPI for the current Windows user under `StoreData`; it is not written into
`MCPanel.exe.config`, logs, source control, or update packages. A cloud computer
therefore needs its own one-time token setup, but subsequent checks and downloads
run automatically.

The standard release path is automated by `.github/workflows/release.yml`. Update
`Version`, `FileVersion`, and `AssemblyVersion` in `MCPanel.csproj`, update
`RELEASE-NOTES.md`, then push a `main` commit whose message starts with
`Release MCPanel `. The workflow runs the full regression suite, performs the
Win-x64 publish, creates the versioned ZIP and SHA-256 sidecar, writes
`update-manifest.json`, and publishes the matching GitHub Release.

For a manual artifact build, read the version from the project instead of copying
a version number into documentation:

```powershell
$version = ([xml](Get-Content .\MCPanel.csproj -Raw)).Project.PropertyGroup.Version
.\Build-UpdatePackage.ps1 -Version $version -GitHubRelease -ReleaseNotes '修复说明'
```

Publish the ZIP, its `.sha256` sidecar, and `update-manifest.json` as assets of
the matching `v<version>` GitHub Release. MCPanel retrieves the release and assets
through the GitHub API over HTTPS, sends the token only to `api.github.com`,
follows signed asset redirects without forwarding the token, and verifies the
ZIP SHA-256 before any extraction or replacement.

## Recover an interrupted update

The updater prepares and merges the incoming program before changing installed
files. Unchanged dependencies stay in place even when another process has them
open. Changed files are checked for locks, replaced with a rename journal, and
rolled back individually on failure. `MCPanel.exe` stays present until the final
atomic executable replacement; rollback never deletes untouched dependencies.

Versions 1.3.58 through 1.3.60 run the updater from the installed version. For an
installation whose executable/configuration is already missing, download
`Restore-Update.ps1` from the 1.3.61 release and run it in administrator PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:USERPROFILE\Downloads\Restore-Update.ps1" -InstallDirectory "D:\MCPanel"
```

It downloads the pinned 1.3.61 package and verifies its SHA-256, restores the
program, and preserves original settings from the installation or rollback
snapshot. Database/website data and rollback copies are retained. It never runs
the old online updater or stops component services. Close the interactive panel
from its tray menu first. If a changed program file is locked, recovery reports
the exact path before modifying installed files. Add `-WhatIf` for a preview.

For offline recovery, place the official ZIP and its matching `.zip.sha256`
sidecar together and add `-PackageFile 'C:\Downloads\MCPanel-1.3.61.zip'`.
