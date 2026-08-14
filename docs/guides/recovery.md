# Recovery 与 WinPE

recovery.exe 是独立的 .NET 10 NativeAOT 单文件程序。它只使用文件系统、bcdedit 和 wpeutil，不依赖 Guardian 服务、SQLite 或 .NET Desktop Runtime。

构建流程将它注入 recovery.wim 的以下位置：

~~~text
X:\Windows\System32\recovery.exe
X:\Windows\System32\startnet.cmd
~~~

startnet.cmd 先执行 wpeinit，然后启动 recovery.exe。

## Recovery 模式

Recovery 在所有已挂载盘符中寻找 GuardianRecovery：

- 默认模式：用 stable\appdata、稳定驱动文件和 data 修复 Guardian。
- 存在 .update：备份当前程序到 rollback，应用 update，保留 data，再为下一次启动创建 .rollback。
- 存在 .rollback：从 rollback 恢复程序与驱动，并删除该标志。

完成后 Recovery 会调整 BCD 默认项或单次启动项，并通过 wpeutil reboot 重启。

## 验证边界

CI 会下载基础 WIM、注入 recovery.exe 和 startnet.cmd、提交镜像，再以只读方式重新挂载并比较 EXE SHA-256 与启动脚本内容。这证明发布镜像包含正确文件；实际 WinPE 启动仍应由发布者在目标硬件或虚拟机上验收。
