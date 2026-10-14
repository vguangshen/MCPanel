# MCPanel 1.3.61

- 修复更新中 DLL 可以读取但无法删除时，主程序被先删除、回滚又被同一 DLL 阻断的问题。新文件及配置先在暂存目录准备，原 EXE 保留到最后一次原子替换。
- 内容相同的运行库保留原位，即使被其他进程打开也不反复删除。需要变更的文件先检查占用；提交期间出现新占用时，仅撤回已完成的改动，并继续尝试其余文件的恢复。
- 更新事务增加安装目录锁，避免两个更新器同时替换程序。继续保留诊断、组件、数据库、站点和其他运行数据；成功更新后清理旧程序回滚副本。
- 新增 `Restore-Update.ps1`，用于 1.3.58–1.3.60 旧更新器造成的程序缺失。恢复工具校验并安装完整 1.3.61 包，从原目录或回滚副本恢复自定义配置，保留原文件，跳过内容相同的被占用 DLL，无需再调用旧更新器。
- Windows CI 增加真实 1.3.58 更新器删除 EXE 的复现、新版更新器验证和恢复工具端到端验证，并覆盖文件锁、只读 EXE、提交失败回滚和更新互斥。

程序缺失时，下载本 Release 的 `Restore-Update.ps1`，在管理员 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:USERPROFILE\Downloads\Restore-Update.ps1" -InstallDirectory "D:\MCPanel"
```

请从托盘菜单退出面板。恢复工具自动下载并校验本版本；原文件保留在 `StoreData\Updates\Recovery`，原有回滚副本保留。看到 `Recovery complete` 后直接启动 `MCPanel.exe`。支持 `-WhatIf` 预览；离线使用时，通过 `-PackageFile` 指定与 `.zip.sha256` 同目录的完整 ZIP 包。
