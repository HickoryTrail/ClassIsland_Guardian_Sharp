# 架构与运行流程

## 组成

ClassIsland Guardian 由四个运行单元组成：

| 单元 | 职责 | 运行位置 |
| --- | --- | --- |
| guardian.exe | 安装、管理、卸载，以及名为 guardian 的 Windows 服务 | 已安装的 Windows |
| recovery.exe | 修复、更新、回滚和 BCD 切换 | recovery.wim 中的 WinPE |
| file.sys、process.sys、registry.sys | 文件、进程和注册表保护 | Windows 内核 |
| ClassIslandGuardian.Common | 路径、日志、BCD、命令和文件树的共享实现 | 两个 C# 程序 |

guardian.exe 和 recovery.exe 都是 .NET 10 win-x64 自包含单文件 NativeAOT
发布物。发行时不携带 .NET 运行时、Python、PyInstaller 或外置 SQLite DLL。

~~~mermaid
flowchart TB
    Admin["管理员终端"] --> Cmd["guardian.exe install / manage / uninstall"]
    Cmd --> Signal["Global\\ClassIslandGuardian_ManagementActive"]
    SCM["Windows SCM"] --> Service["guardian 服务 (LocalSystem)"]
    Signal --> Service
    Service --> Session["活动用户会话"]
    Session --> CI["ClassIsland"]
    Service --> Database["guardian_config.db"]
    Service --> Snapshot["快照 ZIP"]
    Service --> BCD["BCD 恢复项"]
    Service --> Drivers["file.sys / process.sys / registry.sys"]
    BCD --> WIM["recovery.wim"]
    WIM --> Recovery["WinPE recovery.exe"]
    Recovery --> RecoveryData["GuardianRecovery"]
~~~

## Guardian 服务生命周期

当 Service Control Manager 启动 guardian.exe 时，程序通过 Windows 服务宿主
运行，服务名固定为 guardian，账户为 LocalSystem。交互方式启动同一 EXE 时，
程序进入命令入口，而不是启动服务。

服务启动后会：

1. 创建仅对 SYSTEM 和本地管理员开放的全局管理事件，并尽力把服务进程标记为关键进程。
2. 读取 guardian_config.db。读取失败时，选择 Recovery BCD 启动项并每 30 秒重试。
3. 删除一次性保护暂停标记 .tempstopprotect，再进入五秒轮询。
4. 在有活动用户会话时检查 ClassIsland 进程数。缺失时优先常规启动；常规启动失败时创建自动快照、尝试历史快照恢复，最后执行逃逸式启动。
5. 检测到多个目标进程时重启 ClassIsland。每次启动前会尽力清除相应的 IFEO Debugger 项。

服务不会在 Session 0 中启动 ClassIsland。它通过活动用户令牌创建进程，因此
ClassIsland 仍在用户桌面中运行。

## 管理交互

guardian.exe manage 需要管理员权限；若配置了密码，还会验证保存的 SHA-256
摘要。管理进程每秒向受 ACL 保护的命名事件发送一次心跳，服务接收事件后在
六秒宽限期内暂停进程守护。管理进程退出后，心跳停止，服务自动恢复监控。

持久与一次性暂停均以安装目录中的标记文件表示：

| 标记 | 行为 |
| --- | --- |
| .tempstopprotect | 暂停至下一次服务启动；服务启动时删除 |
| .stopprotect | 持续暂停，直到管理界面恢复保护 |
| 无标记 | 服务执行正常监控与恢复 |

这套信号替代了旧版通过检测单独管理程序进程来暂停保护的方式。

## 数据与兼容性

默认路径如下：

| 内容 | 路径 |
| --- | --- |
| Guardian 主程序 | C:\Program Files\Guardian\guardian.exe |
| Guardian 数据与日志 | C:\Program Files\Guardian\data |
| 主配置数据库 | C:\Program Files\Guardian\data\guardian_config.db |
| Recovery WIM 与稳定副本 | C:\GuardianRecovery |
| Recovery 数据与快照副本 | C:\GuardianRecovery\data |
| 已安装驱动 | C:\Windows\System32\drivers |

Guardian 使用 Windows 自带 winsqlite3.dll，通过 P/Invoke 读取与写入既有
SQLite schema：

~~~text
paths(id, classisland_path, classisland_process_name, classisland_launcher_name)
config(id, password)
~~~

密码保存为小写十六进制 SHA-256 摘要。快照以 ZIP 文件同时写入 Guardian 和
Recovery 的 data\snapshot 目录，以便常规服务与 WinPE 都能保留相同的恢复资料。

## Recovery 状态机

WinPE 通过 X:\Windows\System32\startnet.cmd 完成 wpeinit 后启动独立的
recovery.exe。程序在 A: 到 Z: 查找 GuardianRecovery 目录，再依据标记选择
操作：

1. 存在 .rollback：回滚到 rollback 中的程序和驱动，然后删除该标记。
2. 否则存在 .update：备份当前程序和驱动到 rollback，安装 update，保留 data，
   删除 .update，并创建 .rollback。
3. 否则：从 stable 副本修复 Guardian 与三个驱动，保留 data。

回滚和普通修复会把 BCD 默认项指回 Windows；更新会设置 Recovery 为默认项，
并设置 Windows 为下一次单次启动目标。成功处理后，程序调用 wpeutil reboot。

## 驱动边界

三个 C 驱动的保护实现维持原有逻辑。迁移后的接口约束仅包括：

- 服务注册表保护目标使用 guardian，而不是旧的 launcher；
- 用户态受信任路径指向已安装的 guardian.exe；
- 发布包仍只交付 file.sys、process.sys 和 registry.sys。

驱动源码和用户态服务之间的这些名称、路径约定属于兼容合同。修改任意一方时，
必须同步审查另一方、更新测试或 CI 检查，并在发布前于测试机或虚拟机完成验证。
