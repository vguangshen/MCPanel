# MCPanel 1.3.55

- 继续精简 GitHub Release 的 SharpSvn 前置依赖：移除 Chocolatey `vcredist2010` 安装路径，改为仅下载并静默安装 Microsoft 官方 VC++ 2010 SP1 x64 Redistributable，不再下载或安装无用的 x86 运行库。
- 下载后的 `vcredist_x64.exe` 在执行前强制通过 Authenticode 校验，要求签名状态为 `Valid` 且签名者为 Microsoft Corporation；安装后再次确认 `C:\Windows\System32\MSVCR100.dll` 存在。
- 独立 CI 审计确认直接 x64 路径下载约 0.70 秒、下载 + 验签 + 安装总计约 10.85 秒，最终 `MSVCR100.dll` 版本为 10.00.40219.325；此前 Chocolatey 路径通常约 30–40 秒。
- 保留 v1.3.54 的预装 .NET 10 SDK 校验、`global.json`、NuGet 依赖指纹缓存，以及 250 项 regression / Win-x64 publish / SHA-256 / update manifest 完整发布链。
- 未修改 MCPanel 业务逻辑、SharpSvn/SVN 行为、Tomcat 兼容逻辑、旧数据迁移或 Legacy HTTPS manifest，本次仅继续收缩发布基础设施的冗余耗时。
