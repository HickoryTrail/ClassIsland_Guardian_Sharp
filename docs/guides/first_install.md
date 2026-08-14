# 首次安装

## 安装前确认

1. 使用 Windows 10/11 x64，并以管理员账户操作。
2. 已安装可正常启动的 ClassIsland。
3. 当前没有 Guardian 安装：C:\Program Files\Guardian 和 C:\GuardianRecovery 必须都不存在。
4. 已阅读[不兼容插件名单](incompatible_plugin.md)。
5. 驱动签名策略必须允许发行包中的驱动加载；测试签名驱动需要按实际签名策略启用测试模式并重启。

> [!CAUTION]
> 当前版本不迁移旧版安装。先使用旧版自己的卸载流程完成卸载并重启，再安装此版本。不要覆盖旧目录或混用旧版文件。

## 安装步骤

1. 从发行页下载并解压完整包。不要移动或删除 drivers、recovery\recovery.wim。
2. 以管理员身份打开 PowerShell 或命令提示符，并切换到解压目录。
3. 运行：

~~~powershell
.\guardian.exe install
~~~

4. 输入 ClassIsland 根目录。若 ClassIsland 正在运行，安装器会预填检测到的目录；确认目录中包含 ClassIsland.exe 和 app-* 子目录。
5. 可选设置管理密码。密码只保存为 SHA-256 摘要，没有找回机制。
6. 等待安装器完成以下工作：
   - 创建 C:\Program Files\Guardian 和 C:\GuardianRecovery；
   - 创建两个兼容的 guardian_config.db 配置副本；
   - 复制 guardian.exe、驱动文件和 recovery.wim；
   - 注册 guardian Windows 服务和三个 boot-start 驱动；
   - 创建初始 ClassIsland 快照和 Recovery BCD 项。
7. 重启电脑。服务会在有活动用户会话后启动 ClassIsland。

## 安装后检查

在提升权限的终端中执行：

~~~powershell
sc.exe query guardian
& "$env:ProgramFiles\Guardian\guardian.exe" manage
~~~

guardian 服务处于运行状态且 ClassIsland 已在当前登录会话启动时，安装即正常。若配置文件损坏，服务会将下一次启动切换到 Recovery BCD 项。
