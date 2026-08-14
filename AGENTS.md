# ClassIsland Guardian repository guide

## Scope

This repository ships two .NET 10 NativeAOT executables and three existing
Windows kernel drivers:

- guardian.exe is both the interactive administrator entry point and the
  LocalSystem Windows service named guardian.
- recovery.exe is an independent WinPE console application injected into
  recovery.wim.
- drivers/file, drivers/process, and drivers/registry remain C driver
  projects. Do not change their protection behavior unless the task explicitly
  requires it.

The supported target is Windows 10/11 x64. A full install is intentionally
fresh-install only; do not add automatic migration from the removed Python
release.

## Repository layout

- src/ClassIslandGuardian.Common: paths, file operations, logging, command
  execution, and BCD handling shared by the two executables.
- src/ClassIslandGuardian.Guardian: service host, administrator commands,
  SQLite compatibility layer, process supervision, snapshots, and session
  launching.
- src/ClassIslandGuardian.Recovery: WinPE repair, update, rollback, BCD
  switching, and reboot behavior.
- drivers: boot-start C drivers and their Visual Studio solution.
- tests/ClassIslandGuardian.Tests: dependency-free executable self-tests.
- .github/workflows/build.yml: release build, NativeAOT closure checks, and
  offline WIM injection verification.
- docs: operator and maintainer documentation.

## Architecture constraints

- Keep guardian.exe as the only regular user-mode program in the release
  package. It owns install, manage, uninstall, and the internal
  cleanup-uninstall command.
- Keep recovery.exe independent of the service host, GuardianDatabase, and
  winsqlite3.dll. It must work inside WinPE without a .NET runtime.
- Guardian reads and writes the existing guardian_config.db schema through
  Windows winsqlite3.dll; do not add an SQLite package or ship an SQLite DLL.
- Preserve C:\Program Files\Guardian, C:\GuardianRecovery, snapshot names,
  log layout, password SHA-256 semantics, and the database table/column names
  unless compatibility is deliberately changed and documented.
- The management pause signal must remain an ACL-protected global named event
  restricted to SYSTEM and local administrators. Do not reintroduce a
  process-name-based config.exe check.
- The service must continue to launch ClassIsland in the active user session,
  not in Session 0.
- Preserve the recovery marker precedence: .rollback, then .update, then
  normal repair.
- Preserve the driver service name guardian and the trusted installed
  guardian.exe path in the driver sources.

## Build and validation

Run from the repository root:

~~~powershell
dotnet build ClassIslandGuardian.slnx -c Release --disable-build-servers
dotnet run --project tests\ClassIslandGuardian.Tests\ClassIslandGuardian.Tests.csproj -c Release --no-build
~~~

Build the drivers only with the required Visual Studio C++ toolchain and WDK:

~~~powershell
msbuild drivers\drivers.slnx /p:Configuration=Release /p:Platform=x64
~~~

Publish both executables as win-x64 self-contained NativeAOT builds:

~~~powershell
dotnet publish src\ClassIslandGuardian.Guardian\ClassIslandGuardian.Guardian.csproj -c Release -r win-x64 --self-contained true -p:StripSymbols=true -o temp\publish\guardian
dotnet publish src\ClassIslandGuardian.Recovery\ClassIslandGuardian.Recovery.csproj -c Release -r win-x64 --self-contained true -p:StripSymbols=true -o temp\publish\recovery
~~~

Before handing off a behavioral change, run the relevant build and self-tests.
For release or CI changes, also check that:

- each publish directory contains only its EXE plus an optional PDB;
- neither EXE depends on hostfxr, hostpolicy, coreclr, Python, or an external
  SQLite binary;
- recovery.exe has no winsqlite3 import;
- recovery WIM injection places the executable at
  X:\Windows\System32\recovery.exe and startnet.cmd invokes that path;
- the release package contains exactly guardian.exe, three .sys files, and
  recovery\recovery.wim.

## Source and release hygiene

- Do not add Python source, PyInstaller, launcher, config.exe, setup.exe, or
  uninstall.exe back into the tree or package.
- Do not commit bin, obj, temp, app, .artifacts, WIM mount directories, or
  published executables.
- Keep the code package-free where possible. The current projects rely on the
  Windows/.NET SDK surface and use direct P/Invoke where necessary for AOT
  compatibility.
- Do not replace bcdedit, dism, sc.exe, schtasks, or wpeutil calls with shell
  interpolation. Pass arguments through ICommandRunner.
- Recovery and installer operations modify BCD, services, drivers, and system
  directories. Avoid exercising those commands on a development machine unless
  the task explicitly requires elevated integration testing.

## Documentation

Update the relevant page under docs/guides whenever a command, storage path,
recovery marker, package layout, or validation contract changes. Keep
README.md and docs/README.md as the discoverable entry points.
