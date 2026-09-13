# MCPanel 1.3.50

- 修正环境页 Tomcat Server 的普通“启动”语义：不再复用 `catalina.bat run`，改为通过标准 `startup.bat` / `start` 链路打开普通 Tomcat CMD 窗口。
- “以 Catalina 方式启动”继续独立使用 `catalina.bat run`，保留前台持续输出以及退出后暂停查看错误的诊断行为。
- “重启”保持先停止再调用普通 `StartAsync`，因此重启后同样回到标准 `start` 模式，不会再误入 Catalina `run`。
- 普通启动与 Catalina 启动继续共享同一个 Tomcat 安装目录与配置，不改变产品端口、绑定或现有停止逻辑。
- 新增回归测试锁定 Start / Restart / Catalina 三条入口，防止后续再次合并。