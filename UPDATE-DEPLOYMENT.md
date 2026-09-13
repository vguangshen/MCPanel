# MCPanel update deployment

## Private GitHub Release update channel (recommended)

The MCPanel source repository can remain private while the installed application
uses GitHub Releases as its update channel. Private Release assets require an
authenticated request, so do not embed a GitHub token in `App.config`,
`MCPanel.exe.config`, source code, or an update package.

### One-time client setup

1. Keep the configured `GitHubUpdateRepository` value in `MCPanel.exe.config`
   (the distributed default is `vguangshen/MCPanel`).
2. In GitHub, create a **fine-grained personal access token** restricted to this
   one repository with only **Contents: read** permission and an appropriate
   expiration date.
3. On each Windows or cloud-computer installation, open **Panel settings →
   Software update → Configure GitHub** and save the fixed token once.

The token is encrypted with Windows DPAPI for that Windows user and stored under
`StoreData\Updates\github-update-credential.json`; StoreData is preserved across
application updates. The application sends it only to `https://api.github.com`,
never to a release redirect destination, never writes it to logs, and never
ships it in an archive. A token created for one computer/user is intentionally
not portable to another Windows user; configure it separately on each cloud
computer.

### Publish a GitHub Release

The normal release path is `.github/workflows/release.yml` and should be used
instead of manually repeating the packaging steps.

1. Change `Version`, `FileVersion`, and `AssemblyVersion` in `MCPanel.csproj`.
2. Update `RELEASE-NOTES.md` for the target version.
3. Commit the validated source to `main` with a commit message that starts with:

   ```text
   Release MCPanel <version>
   ```

4. Push the commit. The release workflow then automatically:
   - runs the full reliability test suite;
   - performs the Win-x64 publish;
   - builds `MCPanel-<version>.zip`;
   - writes the `.sha256` sidecar and `update-manifest.json`;
   - creates the matching `v<version>` GitHub Release and uploads all three assets.

If you need to build the GitHub Release assets manually, read the version directly
from the project so the documentation never carries a stale hard-coded version:

```powershell
$version = ([xml](Get-Content .\MCPanel.csproj -Raw)).Project.PropertyGroup.Version
.\Publish-WinX64.ps1
.\Build-UpdatePackage.ps1 `
  -Version $version `
  -GitHubRelease `
  -ReleaseNotes (Get-Content .\RELEASE-NOTES.md -Raw)
```

The manifest is bound to the versioned ZIP name and SHA-256. MCPanel checks that
the Release tag, manifest, asset name, GitHub asset digest (when provided), and
downloaded ZIP hash agree before staging an update. The `.sha256` sidecar remains
available for the local/offline update workflow.

## Legacy HTTPS manifest channel

The legacy public HTTPS manifest path is retained only as a compatibility and
fallback channel. New deployments should prefer the private GitHub Release flow
above.

1. Change `Version`, `FileVersion`, and `AssemblyVersion` in `MCPanel.csproj`.
2. Run `Publish-WinX64.ps1`.
3. Build the hash-verified update artifacts using the current project version:

```powershell
$version = ([xml](Get-Content .\MCPanel.csproj -Raw)).Project.PropertyGroup.Version
.\Build-UpdatePackage.ps1 `
  -Version $version `
  -PackageBaseUrl https://example.com/store/updates `
  -ReleaseNotes '修复已知问题并改进稳定性。'
```

`Build-UpdatePackage.ps1` accepts `-PublishDirectory` when the publish output
was written outside the repository, for example `-PublishDirectory D:\MCPanel`.
If `-PackageBaseUrl` is omitted, the script creates a ZIP and its SHA-256
sidecar for local/offline updates but does not create a legacy online manifest.

4. Upload `MCPanel-<version>.zip` and `update-manifest.json` from
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

The updater replaces only application program files. The canonical list of
top-level entries preserved by publishing, packaging, and in-app replacement is
maintained in `deployment-layout.json`; keep that file beside the executable in
every release. It includes the embedded Account API state and legacy state files
so an existing installation can migrate without losing data.
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
