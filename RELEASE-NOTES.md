# MCPanel 1.3.51

- 清理 1.3.11 / 1.3.12 遗留的一次性 Tomcat 迁移脚本，移除不再参与当前构建和运行的历史维护文件。
- 清理旧 Tomcat Windows Service 的注册/启动 API；保留旧服务检测、停止、删除与兼容入口，确保老版本安装仍可平滑退役历史服务。
- 修正 Windows 登录恢复行为：与普通“启动/重启”一致使用标准 `startup.bat` / `start` 模式，不再隐式进入 `catalina.bat run`。
- 修正 Tomcat 安装完成后的首次启动，同样使用标准 start 模式；`catalina.bat run` 仅保留给显式“以 Catalina 方式启动”的诊断操作。
- 保留 .NET Framework 4.6.2 所需运行时兼容层、编译器 polyfill 和旧数据目录迁移逻辑，避免清理影响现有用户升级。
- 已通过完整 regression 与 Win-x64 publish 验证。
