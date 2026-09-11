# MCPanel 1.3.37

- 简化 Tomcat Server 启动：不再等待 Windows Service 状态切换，也不再将应用/端口探测结果绑定到启动进度条，避免 Tomcat 已正常运行却被误报“未按时启动”。
- Tomcat 环境状态改回以实际 Java 进程和配置端口为准；产品部署不再根据 Tomcat Windows 服务状态自动重启共享服务。
- Tomcat 安装、启动、停止和卸载保留原有后台服务机制，但服务控制改为发送命令后立即返回；若 SCM 时序异常而真实 Java 进程未启动，会回退到 startup.bat，卸载前也会按真实进程状态完成清理。
- 单应用 Tomcat 启停、产品卸载与共享 Tomcat 恢复流程均不再依赖 Windows Service 的 Running/Stopped 状态作为运行真相。
- AI 日志分析与面板设置移除“模型设置”按钮，模型参数继续仅从 MCPanel.exe.config 读取。
- 网站页移除“紧凑视图”和星标置顶，恢复直接显示完整网站卡片；保留“全部平台/Java/.NET-IIS”和“全部状态/运行中/停止”筛选。
- 发布前已完成完整回归测试与 Win-x64 发布验证，并增加 Tomcat Service 解耦相关回归覆盖。
