# 开发、发布和验证

## 前置条件

- .NET SDK 10 和 win-x64 NativeAOT 工具链。
- Visual Studio Build Tools / WDK，用于 drivers\drivers.slnx。
- 用于 Recovery 发布的 base.wim。CI 从仓库 recovery-base 发行资产下载该文件。
- 管理员权限和 DISM，用于本地离线 WIM 注入检查。

## 本地构建

~~~powershell
dotnet build ClassIslandGuardian.slnx --disable-build-servers
dotnet run --project tests\ClassIslandGuardian.Tests\ClassIslandGuardian.Tests.csproj --no-build
msbuild drivers\drivers.slnx /p:Configuration=Release /p:Platform=x64
~~~

## NativeAOT 发布

~~~powershell
dotnet publish src\ClassIslandGuardian.Guardian\ClassIslandGuardian.Guardian.csproj -c Release -r win-x64 --self-contained true -p:StripSymbols=true -o temp\publish\guardian
dotnet publish src\ClassIslandGuardian.Recovery\ClassIslandGuardian.Recovery.csproj -c Release -r win-x64 --self-contained true -p:StripSymbols=true -o temp\publish\recovery
~~~

每个发布目录的运行时文件只能是相应的 EXE（可选 PDB 不进入发行包）。使用 dumpbin /DEPENDENTS 验证不得出现 hostfxr、hostpolicy、coreclr、e_sqlite3 或 Python 依赖。

Guardian 使用 Windows 自带的 winsqlite3.dll 读取旧 SQLite schema；它不随包分发。Recovery 不引用该 DLL。

## 发布与 WIM 验证

GitHub Actions 会：

1. 拒绝任何 .py 文件、旧 launcher 目录和 Python 构建步骤；
2. 构建驱动、C# 解决方案和自检程序；
3. 发布两个 NativeAOT EXE 并检查 PE 依赖；
4. 将独立 recovery.exe 注入基础 WIM，提交并二次挂载校验哈希；
5. 创建仅含 guardian.exe、三个驱动和 recovery.wim 的发行包。

发行包不可包含 launcher.exe、config.exe、setup.exe、uninstall.exe、.py 或运行时 DLL。
