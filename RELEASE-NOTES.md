# MCPanel 1.3.44

- 恢复原版 ITMCStore 的默认开机自启动行为：MCPanel 第一次以正常界面模式启动时，会自动注册到当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 开机启动命令使用 `MCPanel.exe --tray`，Windows 登录后仅驻留系统托盘，不主动弹出主窗口。
- 默认注册只执行一次；用户随后在“设置”中关闭开机自启动后，MCPanel 不会在下次启动时强制重新开启。
- Windows Service、安装 Worker、产品 Worker、Tomcat 恢复进程以及在线更新辅助进程不会触发默认自启动注册。
