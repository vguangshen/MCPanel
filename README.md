# MCPanel

Modern WPF panel for configuring and installing .NET and Java software.

## Current Scope

- Material-style shell with navigation for Home, Sites, Products, Environment, AI diagnostics, and settings.
- Home dashboard with uptime, version, CPU, memory, disk, and suite service status.
- Product management with local legacy product fallback, online catalog refresh, product search, download status, and progress.
- Product installation checks IIS/Tomcat and database prerequisites before downloading, then re-applies the original Store version/database configuration after every SVN update.
- Java/Tomcat products are restored in the background after Windows logon without opening the main panel window.
- Closing the main window keeps a lightweight system-tray process running. The complete WPF window is disposed to release UI memory; click the tray icon to create and activate it again, or use the tray menu to exit MCPanel completely.
- When panel auto-start is enabled, Windows starts `MCPanel.exe --tray`, so sign-in does not construct or display the main window. Existing Nginx, Tomcat, database, and FRP processes continue independently while the panel UI is closed.
- Account API has a dedicated left navigation page for the embedded HTTP service, HMAC key lifecycle, health checks, redacted logs, start-stop, and optional logon auto-start. Its original routes and database methods are compiled into MCPanel; no standalone Account API executable is required.
- Product websites support Nginx-managed domains, PEM certificates, HTTPS redirect, WebSocket forwarding, and per-connection bandwidth limits.
- The Sites page can create and manage independent IIS websites, application pools, host bindings, PFX certificates, HTTPS redirect, MIME mappings, and site bandwidth limits.
- Product cache is stored beside the application under `Cache/products-cache.json`.
- SOAP integration for `http://regservice.itmc.cn/Service.asmx`.
- Download credentials for the legacy update/WebDAV/SVN store are kept in `McPanelStoreClient`.

## Environment download URLs

The published application reads environment package URLs from `MCPanel.exe.config` beside the executable. Edit the matching `Environment.*` values before the next installation; no rebuild is needed.

- `Environment.Iis.UrlRewriteUrl` — IIS URL Rewrite MSI. IIS itself is enabled through Windows features.
- `Environment.Nginx.PackageUrl` — Nginx package.
- `Environment.MySql.PackageUrl` — MySQL package.
- `Environment.SqlServer.Sql2025ExpressUrl`, `Sql2022ExpressUrl`, `Sql2017ExpressUrl`, `Sql2008ExpressX64Url`, and `Sql2008ExpressX86Url` — Windows-version-specific SQL Server installers.
- `Environment.Tomcat.PackageUrl` — Tomcat package.
- `Environment.Frp.PackageUrl` — FRP Windows amd64 archive.

Values must be full `http://` or `https://` URLs. If a value is missing or invalid, MCPanel falls back to the built-in default. Customized values are preserved when the application updates.

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
choose another location. The script replaces program files while preserving
`StoreData`, the separate `AccountApi` component directory, `Runtime`,
`Downloads`, `Tools`, `web`, `Cache`, `Frp`, `Nginx`, `MySQL`, `MSSQL`, `Tomcat`,
`SSMS`, `Navicat Premium Lite`, and legacy Account API state files when they
already exist in the release directory. Older `StoreData\Runtime`,
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

For a GitHub Release, create its artifacts with:

```powershell
.\Build-UpdatePackage.ps1 -Version 1.1.20 -GitHubRelease -ReleaseNotes '修复说明'
```

Publish the ZIP, its `.sha256` sidecar, and `update-manifest.json` as assets of
the matching `v1.1.20` GitHub Release. MCPanel retrieves the release and assets
through the GitHub API over HTTPS, sends the token only to `api.github.com`,
follows signed asset redirects without forwarding the token, and verifies the
ZIP SHA-256 before any extraction or replacement.
