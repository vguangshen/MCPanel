# MCPanel 1.3.53

- 审计 vendored SharpSvn 原生依赖，确认当前 `SharpSvn.dll` 为 1.8009.3299.43，且 x64 SharpSvn / SharpPlink 仅直接依赖 `MSVCR100.dll`（Visual C++ 2010 Runtime）。
- 精简 GitHub Release CI：不再安装 `vcredist-all` 的 2005–2017 全套运行库；优先复用 Runner 已有的 x64 `MSVCR100.dll`，缺失时才安装 `vcredist2010` 并再次校验。
- 保留 `actions/checkout@v7`、`actions/setup-dotnet@v6` 和现有完整 regression / Win-x64 publish / SHA-256 / manifest 发布校验链。
- 未修改 SharpSvn、SVN 产品下载、旧数据迁移、Legacy HTTPS manifest 或 Tomcat 兼容行为，本次仅收缩发布基础设施中的冗余依赖。
