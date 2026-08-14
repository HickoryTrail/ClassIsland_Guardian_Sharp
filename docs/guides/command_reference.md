# 命令参考

所有管理员交互均由同一个 guardian.exe 提供。安装完成后，使用以下路径：

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" <command>
~~~

除 help 外，命令都需要提升权限。manage 和 uninstall 在设置管理密码后还需要
通过密码校验。

## install

~~~powershell
.\guardian.exe install
~~~

只能从完整发行包根目录执行。安装器要求同级存在：

~~~text
guardian.exe
drivers\file.sys
drivers\process.sys
drivers\registry.sys
recovery\recovery.wim
~~~

安装器会询问 ClassIsland 根目录和可选管理密码，然后创建 Guardian 与 Recovery
目录、两份兼容数据库、初始快照、服务、boot-start 驱动和 Recovery BCD 项。
安装仅支持全新环境；如果旧版 Guardian 或旧目录残留，先按旧版流程卸载并重启。

## manage

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" manage
~~~

管理菜单提供：

- 临时或持续暂停保护，以及恢复保护；
- 创建、列出、恢复、删除 ClassIsland 快照；
- 打开 Guardian 或 Recovery 日志；
- 进入卸载准备流程。

在管理会话存活期间，服务会根据受保护的命名事件暂停监控。该暂停是临时状态，
关闭管理程序后会自动失效。

## uninstall

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" uninstall
~~~

该命令停止服务、删除服务注册表项、写入卸载标记，并创建一次性高权限登录任务。
它不会立即删除 boot-start 驱动和安装目录；请重启，让任务在驱动卸载后完成延迟
清理。

不要手动执行清理步骤或手动删除受保护文件。

## cleanup-uninstall

~~~powershell
& "$env:ProgramFiles\Guardian\guardian.exe" cleanup-uninstall
~~~

这是仅供安装器创建的登录任务使用的内部命令。它要求存在卸载标记，随后移除
Recovery BCD 项、Recovery 目录、驱动文件、卸载任务和 Guardian 安装目录。
普通管理员不应直接调用此命令。

## help

~~~powershell
.\guardian.exe help
~~~

没有参数、help、--help 与 -h 都会显示可用的公开命令。未知命令会显示相同的
帮助并返回非零退出码。
