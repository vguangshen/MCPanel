# MCPanel 1.3.47

- 恢复原版式的登录后后台运行体验：当 MCPanel 由 Windows 开机启动项以 `--tray` 模式启动时，会自动尝试恢复共享 Tomcat Server。
- 开机恢复使用隐藏的 `catalina.bat run` 进程，不弹出 CMD 窗口；首次检查前延迟 8 秒，并在完全未检测到共享运行实例时执行一次受控重试。
- 手动“启动 / 重启 Tomcat”仍保持 1.3.43 以来的可见 Catalina CMD 控制台，不改回 Windows Service 隐藏运行；专用“以 Catalina 方式启动”入口保持不变。
- 只自动恢复共享 Tomcat，不恢复历史 `MCPanelTomcatProducts` 单产品登录启动项，也不会自动启动独立 Java 产品实例。
- 开机恢复不重新注册旧 `MCPanelTomcat` Windows Service，因此不会与用户会话中的可见 Catalina 控制台争抢同一个共享 Tomcat。
- 开机恢复失败不会阻塞 Windows 登录或 MCPanel 托盘；详细状态写入 `tomcat-autostart.log` 供诊断。
