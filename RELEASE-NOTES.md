# MCPanel 1.3.38

- 修复 MySQL 8.4 `caching_sha2_password` 在本机 TCP 验证时出现 `ERROR 2061: Authentication requires secure connection` 的误报：凭据验证现在支持 `--get-server-public-key`，并兼容旧客户端回退。
- MySQL 启动/重启后增加最长 20 秒的凭据就绪重试；即使最终 root 凭据验证未通过，也不会再把已经正常运行的 MySQL 服务判定为“重启失败”，同时仍然绝不自动重置数据库密码。
- MySQL 服务操作现在把实际结果/警告返回到界面，避免“服务已运行”和“凭据验证失败”混成同一个失败状态。
- 加固 Tomcat 重启：SCM 控制请求在真实共享 Java 进程未出现时会进行有限重试，避免前一次服务停止尚未完全收尾时直接错过重启。
- Tomcat `startup.bat` 回退启动后会再次确认真实共享 Java 进程，避免脚本返回成功但 Tomcat 实际未存活时产生假成功。
- Tomcat Windows 服务停止阶段不再在延迟窗口末尾强制清理一个可能已经重新启动的新共享进程；真正的运行状态与停止确认仍由 MCPanel 按共享 `CATALINA_BASE` Java 进程判断。
- 修复 Tomcat 已完全运行后点击“重启”偶发无法停止的问题：不再依赖可能未监听的 8005 shutdown 端口；共享 Tomcat 停止改为按 `CATALINA_BASE` 精确结束共享 Java 进程，并保持单应用实例不受影响。
- 增加 MySQL 凭据验证与 Tomcat 重启竞态的回归测试。
