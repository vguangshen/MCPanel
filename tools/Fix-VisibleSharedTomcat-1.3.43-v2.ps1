$ErrorActionPreference = 'Stop'

# Keep this legacy retry helper safe and consistent with the final v3 migration.
# The dedicated environment-card "以 Catalina 方式启动" button (Tag="CatalinaRun")
# and the per-product Catalina action must both remain available.
& (Join-Path $PWD 'tools\Fix-VisibleSharedTomcat-1.3.43-v3.ps1')
