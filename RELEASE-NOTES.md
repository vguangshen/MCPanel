# MCPanel 1.3.64

- 修复 SQL Server 2017 / 2022 安装下载 404：采用微软官方版本下载入口，旧版默认地址在运行时、在线更新和安装器修复时自动迁移；自定义下载地址保持不变。
- 校验 SQL Server 下载重定向目标和安装包产品版本，避免下载入口转向其他版本后误装；过期或版本错误的缓存会重新下载。
- 下载失败日志显示组件安装包、HTTP 状态与请求/最终地址，便于直接定位失效来源。
- 恢复主窗口左上角原有 MCPanel Logo：修复安装器复用图片时引入的 WPF 资源与外部内容路径冲突。
- 环境小卡片翻转按钮改为本地矢量的卡片加环绕箭头，并补充明确的翻转提示。

官方入口：Microsoft Learn 的 SQL Server Express 安装介质列表：https://learn.microsoft.com/sql/database-engine/configure-windows/sql-server-express-localdb#installation-media 。
