# MCPanel 1.3.45

- IIS/.NET 产品安装与“修复绑定”统一将应用程序池托管管道模式设置为 `Integrated`（集成），不再按旧 Framework 版本切换为 `Classic`。
- 继续使用 Windows IIS 自带的 `appcmd.exe` 显式写入 `/managedPipelineMode:Integrated`；CLR v2.0 / v4.0 / 无托管 CLR 的检测逻辑保持不变。
- 32 位应用程序开关与现有产品架构判断保持不变；重新绑定已有 IIS 产品时也会把对应应用池修正为集成模式。