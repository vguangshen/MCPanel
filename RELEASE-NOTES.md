# MCPanel 1.3.40

- 修复“已安装网站”中对单个 Java 产品执行“单独启动 / 以 Catalina 方式启动”后，搜索结果计数仍存在但网站卡片区域可能变成空白的问题。
- 网站标题计数与实际 ListBox 统一使用 WebsiteView，批量重建网站行后强制刷新同一视图，避免两个 CollectionView 状态不同步。
- Tomcat 产品操作完成后直接读取该产品真实 Java/CATALINA_BASE 运行状态并更新卡片，不再拿 30 秒缓存快照覆盖刚启动的状态；Catalina 仍不等待端口或 HTTP 就绪。
- 网站列表继续保持虚拟化，但由 Recycling 改为 Standard，并在运行状态刷新后主动将当前产品滚回可视区域，避免动态卡片与展开管理面板被回收后出现空白视口。
