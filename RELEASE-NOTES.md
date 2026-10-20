# MCPanel 1.3.64

- 修复 SQL Server 2017 / 2022 安装下载 404：采用微软官方版本下载入口，旧版默认地址在运行时、在线更新和安装器修复时自动迁移；自定义下载地址保持不变。
- 校验 SQL Server 下载重定向目标和安装包产品版本，避免下载入口转向其他版本后误装；过期或版本错误的缓存会重新下载。
- 下载失败日志显示组件安装包、HTTP 状态与请求/最终地址，便于直接定位失效来源。
- 恢复主窗口左上角原有 MCPanel Logo：修复安装器复用图片时引入的 WPF 资源与外部内容路径冲突。
- 环境小卡片翻转按钮改为本地矢量的卡片加环绕箭头，并补充明确的翻转提示。
- SSMS 同时按操作系统与 SQL Server 版本匹配：Windows Server 2012 等旧系统使用 18.12.1，Windows 10 / Server 2016 使用 20.2.1，支持的较新系统使用 22；18—20 采用对应旧版安装参数，22 固定版本通道。
- SSMS 共用组件下载与缓存校验，拒绝 HTML 或损坏 EXE，下载失败可直接定位来源。
- 恢复脚本改为获取最新公开 Release，并绑定该版本的安装包及 SHA-256，避免固定恢复至旧版 1.3.61；恢复配置时也会迁移失效的 SQL Server 默认地址。
- 修正下载源迁移在 Windows PowerShell 5.1 与新版本之间的 JSON 数组兼容问题。

官方入口：Microsoft Learn 的 SQL Server Express 安装介质列表：https://learn.microsoft.com/sql/database-engine/configure-windows/sql-server-express-localdb#installation-media 。
