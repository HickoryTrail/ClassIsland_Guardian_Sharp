# ClassIsland Guardian

ClassIsland Guardian 是面向 Windows 10/11 x64 的 ClassIsland 守护与预启动修复工具。它由一个 Windows 服务、三个 boot-start 驱动、快照存储和独立 WinPE 恢复程序组成。

> [!CAUTION]
> Guardian 会写入 BCD 并注册 boot-start 驱动。请先在测试机或虚拟机中完成验收。安装、卸载和 WinPE 镜像注入均需要管理员权限。

## 运行架构

~~~mermaid
flowchart LR
    A["管理员"] --> B["guardian.exe manage"]
    B --> C["受限全局命名事件"]
    C --> D["guardian 服务 (LocalSystem)"]
    D --> E["活动用户会话中的 ClassIsland"]
    D --> F["file.sys / process.sys / registry.sys"]
    D --> G["快照与 BCD"]
    G --> H["recovery.wim"]
    H --> I["独立 recovery.exe"]
~~~

- `guardian.exe` 是唯一的用户态可执行文件。由 SCM 启动时，它作为服务名为 `guardian` 的 LocalSystem 服务运行；交互运行时提供 `install`、`manage`、`uninstall` 与内部 `cleanup-uninstall` 命令。
- 服务在活动用户会话中启动 ClassIsland，监控进程数量，清理 IFEO Debugger 劫持，并按“常规启动、历史快照恢复、逃逸式启动”的顺序恢复异常状态。
- 管理会话通过仅允许 `SYSTEM` 与管理员访问的全局命名事件发送心跳，服务据此暂时暂停监控；管理程序结束后，保护会在数秒内自动恢复。
- `recovery.exe` 是独立的 .NET 10 NativeAOT 单文件程序。它被注入 `recovery.wim`，在 WinPE 中执行修复、更新、回滚、BCD 切换和 `wpeutil reboot`，不依赖 Guardian 服务、SQLite 或 .NET 运行时。
- `file.sys`、`process.sys` 与 `registry.sys` 保留既有内核保护逻辑。注册表目标改为 `guardian` 服务，用户态白名单只接受已安装的 `guardian.exe`。

## 发行包

发行包严格只包含以下文件：

~~~text
guardian.exe
drivers/
  file.sys
  process.sys
  registry.sys
recovery/
  recovery.wim
~~~

从提升权限的终端在解压目录执行：

~~~powershell
.\guardian.exe install
~~~

安装器只支持全新安装：`C:\Program Files\Guardian`、`C:\GuardianRecovery` 以及 Guardian 驱动/服务均不能残留。安装后使用：

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" manage
& "$env:ProgramFiles\Guardian\guardian.exe" uninstall
~~~

完整操作说明见 [文档目录](docs/README.md)。

维护者可先阅读[架构与运行流程](docs/guides/architecture.md)、[命令参考](docs/guides/command_reference.md)和仓库级 [AGENTS.md](AGENTS.md)。它们说明服务与 WinPE 的交互、持久化兼容合同、发布包约束和验证边界。

## 开发与验证

- `src/ClassIslandGuardian.Guardian`：Guardian Windows 服务和交互命令宿主。
- `src/ClassIslandGuardian.Recovery`：独立的 WinPE Recovery 程序。
- `src/ClassIslandGuardian.Common`：BCD、路径、日志、命令和文件系统共享逻辑。
- `drivers`：C 驱动源码。
- `tests`：SQLite schema、密码、快照、命令路由、BCD 与 Recovery 自检。

~~~powershell
dotnet build ClassIslandGuardian.slnx --disable-build-servers
dotnet run --project tests\ClassIslandGuardian.Tests\ClassIslandGuardian.Tests.csproj --no-build
~~~

两个用户态程序固定为 .NET 10 `win-x64`：`SelfContained`、`PublishSingleFile`、`PublishTrimmed`、`PublishAot` 与 `InvariantGlobalization` 均已启用。发布、驱动构建和 WinPE 离线注入验证见 [开发指南](docs/guides/development.md)。

## 许可证

本项目使用 [GPL-3.0](LICENCE.txt) 许可证。
