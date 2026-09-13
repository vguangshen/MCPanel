# MCPanel 1.3.54

- 精简 GitHub Release 的 .NET SDK 准备阶段：移除每次重新下载安装 `actions/setup-dotnet` 的步骤，直接使用 `windows-latest` 已预装的 .NET 10 SDK，并在发布前显式校验所选 SDK 仍属于 10.x。
- 新增 `global.json`，以 `10.0.100 + latestFeature` 约束构建始终使用 .NET 10，同时允许本机与 GitHub Runner 在 10.0.x feature band / patch 之间滚动，不绑定单一补丁版本。
- 引入 `actions/cache@v6` 缓存 NuGet global-packages；缓存键由项目 `.csproj`、`global.json` 与 NuGet 配置共同决定，依赖变化时自动生成新缓存。
- 两轮 CI 审计确认：无 `setup-dotnet` 时 Runner 可直接选择 .NET SDK 10.0.400；NuGet 冷 restore 基线约 20.31 秒，缓存命中后 cache restore + `dotnet restore` 约 10 秒量级。
- 保留 v1.3.53 已验证的 SharpSvn VC++ 2010 精确依赖策略、250 项 regression、Win-x64 publish、SHA-256 与 update manifest 发布校验链。
- 未修改 MCPanel 业务逻辑、SharpSvn/SVN 行为、Tomcat 兼容逻辑、旧数据迁移或 Legacy HTTPS manifest，本次仅优化发布基础设施与构建可重复性。
