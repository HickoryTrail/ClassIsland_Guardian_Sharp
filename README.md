# ClassIsland Guardian

> ⌜念念不忘，必有回响⌟

`ClassIsland Guardian` 是一款适用于 `Windows` 平台的、功能强大的 `ClassIsland` 守护工具。

本仓库是 [GYM-Latest/ClassIsland_Guardian 上游仓库](https://github.com/GYM-Latest/ClassIsland_Guardian) 的 C# 重写版本，使用 `.NET 10 NativeAOT`。本项目保留上游项目的保护目标和数据兼容约定，但是驱动部分和原上游项目不兼容。

> [!CAUTION]
> **严禁直接使用本项目发行包覆盖原项目可执行文件。**
>
> 本项目的驱动与原项目的驱动不兼容，若强行替换可执行文件，可能导致严重后果。请先运行原项目的卸载程序（可以选择保留数据文件），并删除原项目的可执行程序，然后运行本项目的安装程序。

有关 `ClassIsland` 的信息，敬请参阅 [ClassIsland GitHub](https://github.com/ClassIsland/ClassIsland)。

> [!WARNING]
> **项目仍处于早期测试阶段，稳定性可能不足。**
>
> 此版本会写入 BCD、注册 boot-start 驱动，并修改受保护的系统目录。请谨慎部署，建议先在**虚拟机或测试机**完成验收。
>
> **请勿在生产环境部署！** 如在使用过程中遇到问题，欢迎提交 Issue。

## 功能

### 应用层守护

- 由名为 `guardian` 的 LocalSystem Windows 服务监控 `ClassIsland.Desktop.exe`；服务在活动用户会话中启动 ClassIsland。
- 仅统计已配置 ClassIsland 安装目录下 `app-*` 中的真实实例，防止外部同名实例干扰。
- 支持手动创建、列出、恢复和删除 `ClassIsland` 目录的历史快照；常规启动失败时会尝试历史快照恢复。
- 映像劫持对抗：启动前自动清理针对 ClassIsland 进程的 IFEO Debugger 劫持项。
- 逃逸式启动：当正常启动和快照恢复均失败时，尝试从活动用户临时目录中的副本启动。
- 完整的操作日志记录，便于排查问题。

### 驱动级守护

- 保留上游的 `file.sys`、`process.sys` 和 `registry.sys` 内核保护逻辑，保护 Guardian 程序本体与预启动修复文件。
- 驱动服务名固定为 `guardian`，用户态受信任路径固定为已安装的 `guardian.exe`。

### 预启动修复

- 使用独立的 `recovery.exe`，它被注入 `recovery.wim` 并在 WinPE 中运行。
- 在 Windows 启动之前执行修复、更新、回滚与 BCD 切换；恢复标记优先级为 `.rollback`、`.update`、常规修复。
- 发现 Guardian 损坏时，从稳定副本对 Guardian 和驱动做无损修复。

### 其他

- `guardian.exe` 是发行包中的常规用户态程序，提供 `install`、`manage`、`uninstall` 和内部 `cleanup-uninstall` 命令。
- `guardian.exe manage` 通过仅允许 `SYSTEM` 与本地管理员访问的全局命名事件，临时暂停进程守护。
- 支持密码锁定，并使用 SHA-256 摘要语义避免明文密码泄露。

## 软件截图/短宣传片

> 下图和短宣传片来自上游 Python 版本，其中的 `config.exe` 不存在于本仓库；当前版本使用 `guardian.exe manage` 管理保护策略。

![上游 config.exe 配置页面](https://cdn.luogu.com.cn/upload/image_hosting/0wmadd6q.png)

[观看上游短宣传片 ->](https://www.bilibili.com/video/BV1DcgV6LEF7/)

## 使用

> [!IMPORTANT]
> **详细安装与管理说明请参阅 [ClassIsland Guardian 文档](docs/README.md)。**

### 系统要求

- Windows 10/11 x64。
- 管理员权限。
- 建议在虚拟机或测试环境中先行验证。

### 下载与安装

- [本仓库 GitHub Releases](https://github.com/HickoryTrail/ClassIsland_Guardian_Sharp/releases)

从本仓库发行包根目录的提升权限终端执行：

```powershell
.\guardian.exe install
```

发行包严格只包含 `guardian.exe`、三个驱动文件和 `recovery\recovery.wim`。安装仅支持全新安装：`C:\Program Files\Guardian`、`C:\GuardianRecovery` 以及 Guardian 驱动/服务均不能残留。

安装后使用以下命令打开管理程序或卸载：

```powershell
& "$env:ProgramFiles\Guardian\guardian.exe" manage
& "$env:ProgramFiles\Guardian\guardian.exe" uninstall
```

请勿将上游 Python 发行包中的 `config.exe`、`setup.exe`、`uninstall.exe` 或 `launcher.exe` 用于本版本。

## 开发/编译

> [!IMPORTANT]
> **开发环境、NativeAOT 发布、驱动构建和 recovery.wim 离线注入验证见 [开发指南](docs/guides/development.md)。**

### 项目结构

- `src/ClassIslandGuardian.Guardian`：Guardian Windows 服务和交互命令宿主。
- `src/ClassIslandGuardian.Recovery`：独立的 WinPE Recovery 程序。
- `src/ClassIslandGuardian.Common`：BCD、路径、日志、命令和文件系统共享逻辑。
- `drivers`：保留的 Ring0 内核驱动 C 源码。
- `tests`：无需测试框架的可执行自测。

构建与自测：

```powershell
dotnet build ClassIslandGuardian.slnx -c Release --disable-build-servers
dotnet run --project tests\ClassIslandGuardian.Tests\ClassIslandGuardian.Tests.csproj -c Release --no-build
```

两个用户态程序为 .NET 10 `win-x64` 自包含单文件 NativeAOT 发布物。用户无需安装.NET Runtime。

## 上游仓库

- [GYM-Latest/ClassIsland_Guardian](https://github.com/GYM-Latest/ClassIsland_Guardian)：原始 Python 版本，提供本项目延续的功能目标、驱动代码和兼容性背景。
- [HickoryTrail/ClassIsland_Guardian_Sharp](https://github.com/HickoryTrail/ClassIsland_Guardian_Sharp)：本仓库的公开地址，维护 C# / .NET 10 实现。

> [!NOTE]
> 本仓库独立维护。功能、发布包与操作步骤请以本 README 和 `docs` 为准；上游文档中的 Python 程序名和命令不适用于本仓库。

## 许可证

本项目采用 [GPL-3.0 License](https://www.gnu.org/licenses/gpl-3.0.html) 许可证。有关详细信息，敬请参见 [LICENCE.txt](LICENCE.txt)。

## 致谢

1. 感谢 [ClassIsland](https://github.com/ClassIsland/ClassIsland) 本体，这个项目因你而生，也因你而不断进化。
2. 感谢上游 [ClassIsland_Guardian](https://github.com/GYM-Latest/ClassIsland_Guardian) 的维护者与贡献者，为本仓库提供功能背景和持续参考。
3. 感谢所有贡献者，每一行代码、每一个 Issue、每一次讨论，都在让 CIG 变得更好。
4. 感谢 [SignPath Foundation](https://signpath.org)，为开源项目提供免费的代码签名服务，让驱动能够被信任。
5. 感谢 [洛谷云图床](https://www.luogu.com.cn/image)，为上游项目文档提供稳定的图床支持。
6. 感谢你，让这个项目有了存在的意义。
