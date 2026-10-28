# MCPanel 1.3.67

- 修复供应商 SVN 产品下载安装与增量更新在短暂网络故障时直接中断的问题：仅对可恢复的 SharpSvn 仓库 I/O 异常自动重试，最多尝试 3 次，重试间隔分别为 2 秒和 4 秒。
- 重试期间保留已下载的 SVN 工作副本，继续使用现有 Checkout/Update 恢复机制；不重试明显的认证、权限、证书及仓库不存在等永久性错误，取消下载可立即中断等待。
- 产品安装失败日志补充各次尝试的异常类型、HRESULT、内部异常链及经过脱敏的仓库地址，便于定位 SVN REPORT、网络连接和服务端错误。
- 添加 SVN 异常链诊断回归测试；保留现有产品部署、安装队列及在线更新流程。

# MCPanel 1.3.66

- 修复环境卡片翻转后 UI 被异常撑高：卡片尺寸由正面内容确定，背面列表不再改变外层等高网格和相邻卡片的位置。
- Tomcat 产品端口列表和数据库连接信息在卡片内部滚动；大量记录、长字段及多次翻转不会拉长整页。
- 连接信息标题为返回按钮预留空间，长标题和实例名显示省略号并保留完整提示。
- 保留原有翻转动画和响应式布局，补充真实点击、连续点击、窄窗口、多记录滚动和窗口缩放恢复的 WPF 回归测试。

# MCPanel 1.3.65

- 修复 MySQL 重装时的失败恢复：目录切换失败后先取回暂存目录中的原数据库；恢复被占用阻断时保留数据与备份，并明确提示恢复路径。
- MySQL 暂存、运行目录及回滚备份保持在同一磁盘，修复中文安装路径映射到系统盘后无法跨盘移动目录的问题；兼容不带顶层文件夹的安装包。
- 完整卸载会清理中文安装路径映射目录内由 MCPanel 创建的 MySQL 恢复目录，保留不属于该命名规则的相邻内容。
- MySQL 重装和失败清理必须确认服务及进程正常停止；服务停止超时、非托管服务、进程重新出现或服务待删除时中止操作。维护进程统一使用兼容 MySQL 5.6 的 mysqladmin shutdown，超时不强制终止数据库；启动服务不再终止已有客户端或备份进程。
- 修复产品已部署成功后，备份或暂存文件被占用导致错误回滚、删除新产品的情况；清理未完成时保留已安装产品并给出路径提示。
- 配置和下载文件替换改为原子提交，替换文件丢失或被占用时保留原有可用文件。
- SQL Server 本机证书改用兼容 Windows Server 2012 的 certreq 和系统证书库 API，避免旧版命令参数或 PowerShell 模块冲突阻断连接。
- 证书私钥权限按 SQL Server 实际服务账户授予，保留原有强制加密设置；证书切换后重启失败会恢复原绑定，原本停止的服务保持停止。

官方依据：
- Microsoft Windows Server 2012 certreq 文档：https://learn.microsoft.com/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn296456(v=ws.11)
- MySQL Windows 服务停止与移除：https://dev.mysql.com/doc/refman/8.0/en/windows-start-service.html
- MySQL 5.7.9 新增 SHUTDOWN SQL 语句：https://dev.mysql.com/doc/relnotes/mysql/5.7/en/news-5-7-9.html

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
