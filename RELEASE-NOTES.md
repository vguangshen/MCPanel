# MCPanel 1.3.39

- 修复“以 Catalina 方式启动”仍执行自动就绪诊断的问题：打开 Catalina 前台窗口后立即返回，不再等待所有端口，也不再逐个应用执行 HTTP 就绪检查。
- 删除 Catalina 启动流程中的“诊断模式”状态与完成判定，避免 Tomcat 已经正常在后台/前台启动却被 MCPanel 因应用 HTTP 检查失败误报为启动失败。
- Catalina 窗口只负责展示 Tomcat 原始控制台输出；MCPanel 不再根据应用页面是否能返回 HTTP 响应来决定 Catalina 是否启动成功。
