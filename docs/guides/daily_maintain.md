# 日常维护和卸载

## 打开管理程序

所有交互式操作都由同一个 guardian.exe 完成。以管理员身份运行：

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" manage
~~~

如果已设置密码，管理程序先验证密码。管理会话会通过仅允许 `SYSTEM` 与管理员访问的全局命名事件发送暂停心跳；退出管理程序后，服务会在数秒内自动恢复监控。这不会改变持久保护状态。

## 保护控制

管理菜单中的“保护控制”有三个状态：

- 暂时关闭：写入 .tempstopprotect，服务下次启动时自动清除。
- 关闭保护：写入 .stopprotect，直到在菜单中恢复保护。
- 重新启动保护：删除以上两个标志文件。

更新 ClassIsland、编辑其目录或使用调试工具前，应先暂停保护。

## 快照

快照是 ClassIsland 根目录的 ZIP 备份，同时保存在：

~~~text
C:\Program Files\Guardian\data\snapshot
C:\GuardianRecovery\data\snapshot
~~~

管理程序可以创建、列出、恢复和删除快照。创建或恢复快照会先停止被守护的 ClassIsland 进程。建议在更新 ClassIsland、插件或主题后立即创建新快照。

当服务无法正常启动 ClassIsland 时，它会按以下顺序处理：

1. 先等待两秒，确认不是 ClassIsland 的自主重启；仅统计安装目录下一级 `app-*` 目录中的真实实例，同名外部程序不会被计入或结束；
2. 尝试普通启动器和当前 app 目录中的主程序。若启动期间出现多个可信实例，则立即清理并重新启动一次；
3. 创建“自动回滚前生成的快照”，恢复最近的历史快照后再次启动；
4. 尝试复制到临时目录的逃逸式启动。

## 日志

- Guardian 服务日志：C:\Program Files\Guardian\data\guardian.log
- WinPE Recovery 日志：C:\GuardianRecovery\recovery.log

管理菜单可以直接打开这两个日志文件。

## 卸载

在管理程序中选择卸载，或以管理员身份运行：

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" uninstall
~~~

卸载命令会再次验证密码、停止 guardian 服务、删除服务注册表项，并创建一次性登录任务。重启后该任务以 guardian.exe cleanup-uninstall 完成 BCD、Recovery、驱动文件和安装目录清理。

> [!CAUTION]
> 不要手动删除服务或受保护文件。正常卸载需要先重启，让 boot-start 驱动卸载后再执行延迟清理。
