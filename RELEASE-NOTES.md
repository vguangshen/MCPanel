# MCPanel 1.3.49

- 手动“停止 / 重启 Tomcat”现在会在共享 Java 进程退出后同步关闭旧的 `MCPanel Tomcat Server` CMD，避免旧窗口停留在 `pause` 阶段。
- 再次启动共享 Tomcat 前也会清理残留的 MCPanel Tomcat 控制台；重启顺序保持“停止旧 Java → 关闭旧 CMD → 启动新的可见 Catalina CMD”。
- CMD 清理只匹配 MCPanel 自己的 `run-tomcat-server.cmd` 或精确窗口标题 `MCPanel Tomcat Server`，不会关闭用户普通命令提示符窗口。
- 保留 1.3.48 的登录启动行为：`MCPanel.exe --tray` 启动后不再等待 8 秒，若共享 Tomcat 未运行则立即使用与手动启动相同的 `EnvironmentRuntimeService.LaunchTomcatConsole(...)`，弹出可见 Catalina CMD。
- 专用“以 Catalina 方式启动”入口与产品级 Catalina 操作保持不变；独立 Java 产品实例不会因 Windows 登录被自动启动。
