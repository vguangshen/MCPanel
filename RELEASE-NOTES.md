# MCPanel 1.3.52

- 修复 `TomcatConsoleWindowManager` 的 nullable 静态分析警告，保持现有 Tomcat 控制台识别逻辑不变，同时让正式构建回到零 C# warning 目标。
- 发布工作流升级到 `actions/checkout@v7` 与 `actions/setup-dotnet@v6`，移除旧 Node 20 Action 运行时带来的弃用警告。
- 更新 README 的托盘行为说明，使文档与当前“隐藏主窗口、继续队列监督”的实际实现一致。
- 重写 GitHub Release 发布说明：以当前自动化 release workflow 为标准流程，手工打包示例改为直接读取 `MCPanel.csproj` 版本，避免文档版本号长期过期。
- 保留 Legacy HTTPS manifest、旧数据目录迁移、旧配置键和旧凭据解密等升级兼容路径，不影响现有安装继续升级。
- 正式发布流程继续执行完整 regression、Win-x64 publish、更新包、SHA-256 与 manifest 校验。
