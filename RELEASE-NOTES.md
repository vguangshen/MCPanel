# MCPanel 1.3.42

- 删除“已安装网站”里的“检测访问”按钮以及对应的临时 HTTP GET 探测代码。
- 删除“已安装网站”的服务状态检测功能：移除状态筛选、刷新状态按钮、30 秒自动状态刷新、状态提示行以及卡片运行状态点；网站页只负责搜索、平台绑定、域名/SSL 和显式运行操作。
- 修复 Tomcat 偶发显示“运行中”但任务管理器已经没有 Java 进程的问题：端口监听只有在能够明确确认监听 PID 为 java.exe 时，才作为 Java 运行证据，未知 PID 不再按 Java 处理。
- “环境 → Tomcat Server”现在只表示共享/总 Tomcat Server 的状态；单应用独立/Catalina 实例继续在“网站”页各自管理，不再让环境页的“停止”按钮看起来无效。
- Tomcat 停止链移除 Windows Service Stop 后最多 20 秒的冗余等待，发送 SCM 停止请求后直接按真实 CATALINA_BASE Java 进程清理并验证。
- Windows Service 的普通 OnStop 不再重复执行 shutdown.bat；运行时停止统一交给 MCPanel 控制器，避免重复 shutdown、长时间无响应和旧停止动作打到新实例的竞态。系统关机路径仍保留强制清理。