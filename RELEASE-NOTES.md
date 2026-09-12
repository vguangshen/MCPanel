# MCPanel 1.3.43

- 总/共享 Tomcat Server 的“启动”和“重启”改为可见 CMD 控制台启动，使用 `catalina.bat run`，Catalina 标准输出和异常可直接在窗口中观察。
- 共享 Tomcat 不再通过 MCPanelTomcat Windows Service 隐藏启动；首次启动/重启会尽力停止并删除旧的服务包装器，后续不再由 SCM 在后台拉起共享 Tomcat。
- Tomcat 新安装流程不再注册或启动隐藏 Windows 服务，安装完成后直接打开可见的 Tomcat Server CMD 控制台。
- 保留环境卡片上的“以 Catalina 方式启动”按钮和产品管理中的 Catalina 启动入口；普通“启动”与“重启”同时改为可见 Catalina CMD 控制台模式。
- 控制台中的 Tomcat 退出后窗口会保留并暂停，便于查看最后的异常和 Catalina 输出；MCPanel 仍不执行 HTTP 就绪诊断。
