# MCPanel 1.3.48

- Windows 登录后由 `MCPanel.exe --tray` 恢复共享 Tomcat 时不再等待 8 秒，检测到 Tomcat 已安装且尚未运行后立即启动。
- 开机恢复不再使用隐藏的 `catalina.bat run` 路径，也不再进行 45 秒后台探测、延迟重试或第二次隐藏启动。
- 开机恢复与平时手动“启动 / 重启 Tomcat”统一使用 `EnvironmentRuntimeService.LaunchTomcatConsole(...)`，直接弹出可见的 `MCPanel Tomcat Server` Catalina CMD 控制台。
- 专用“以 Catalina 方式启动”入口与产品级 Catalina 操作保持不变；历史 `MCPanelTomcatProducts` 单产品登录启动项仍不会恢复。
- 如果共享 Tomcat 已经运行，登录启动不会重复打开第二个 Catalina 控制台；失败信息继续写入 `tomcat-autostart.log`。
- 手动“停止 / 重启 Tomcat”现在会在共享 Java 进程退出后同步关闭旧的 `MCPanel Tomcat Server` CMD；再次启动前也会清理残留的 MCPanel Tomcat 控制台，避免多次重启后遗留旧窗口。
