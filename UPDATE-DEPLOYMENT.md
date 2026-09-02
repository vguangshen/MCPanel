# MCPanel update deployment

## Build and publish an update

1. Change `Version`, `FileVersion`, and `AssemblyVersion` in `MCPanel.csproj`.
2. Run `Publish-WinX64.ps1`.
3. Build the hash-verified update artifacts:

```powershell
.\Build-UpdatePackage.ps1 `
  -Version 1.0.1 `
  -PackageBaseUrl https://example.com/store/updates `
  -ReleaseNotes '修复已知问题并改进稳定性。'
```

`Build-UpdatePackage.ps1` accepts `-PublishDirectory` when the publish output
was written outside the repository, for example `-PublishDirectory D:\MCPanel`.
If `-PackageBaseUrl` is omitted, the script creates a ZIP and its SHA-256
sidecar for local/offline updates but does not create an online manifest.

4. Upload `MCPanel-1.0.1.zip` and `update-manifest.json` from
   `artifacts\updates` to the HTTPS website.
5. Set the public `update-manifest.json` URL in the installed
   `MCPanel.exe.config` file:

```xml
<add key="UpdateManifestUrl" value="https://example.com/store/updates/update-manifest.json" />
```

The software reads this value when the online update button is used; server users
do not need to enter or maintain the URL in the interface.

The client requires HTTPS and verifies the ZIP against the SHA-256 value in the
manifest before extraction. Local updates can use the same ZIP. Keep the adjacent
`.sha256` file beside a local ZIP to make local hash verification mandatory.

## Replacement rules

The updater replaces only application program files. These top-level directories
are preserved across every update: `StoreData`, the separate `AccountApi` component
directory, `Runtime`, `Downloads`, `Tools`, `web`, `Cache`, `Frp`, `Nginx`, `MySQL`, `MSSQL`, `Tomcat`, `SSMS`,
`Navicat Premium Lite`, and the embedded Account API state under `AccountApi`.
Legacy state files `config.ini`, `config.ini.previous`,
`device.identity`, `database.config`, `database.config.previous`, and `logs` are also
preserved when present so an existing installation can migrate without losing data.
Before replacement, the current program files are copied to
`StoreData\Updates\Rollback\Current`. A failed replacement is rolled back
automatically, and the result is shown after the application restarts. The
rollback copy is removed after a successful replacement so an installed
version does not retain a second copy of the old MCPanel program files.

Older installations may still contain component-owned data under
`StoreData\Runtime`, `StoreData\Downloads`, `StoreData\Tools`, or
`StoreData\ProductIcons`. MCPanel merges those folders into the corresponding
top-level locations (`Runtime`, `Downloads`, `Tools`, and `Cache\ProductIcons`)
when it starts. Conflicting files are retained in the old location and recorded
in `StoreData\Work\storage-layout-migration.log` instead of being overwritten.
