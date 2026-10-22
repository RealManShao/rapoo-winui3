# 开发与构建

以下命令均在项目根目录的 PowerShell 中执行。设备协议见 [PROTOCOL.md](PROTOCOL.md)。

## 开发环境

- Windows 10 2004（内部版本 19041）或更新版本，x64。
- .NET 10 SDK。
- 项目所需的 Windows SDK 和 WinUI 构建工具。相关 NuGet 包会在首次构建时还原，需要联网。
- 生成安装包时额外需要 [Inno Setup 6.3 或更新版本](https://jrsoftware.org/isdl.php)；日常调试无需安装它。

应用使用 WinUI 3，目标框架为 `net10.0-windows10.0.19041.0`，仅构建 `win-x64`。当前依赖版本由 `src/Rapoo.App/Rapoo.App.csproj` 定义，包括 Windows App SDK `2.0.1`、Windows SDK BuildTools `10.0.28000.1839` 和 Windows SDK 包 `10.0.19041.57`。

| 路径 | 内容 |
| --- | --- |
| `src/Rapoo.App` | WinUI 界面与视图模型 |
| `src/Rapoo.Core` | 配置模型、协议编码与解析、有限容量日志 |
| `src/Rapoo.Hid` | Windows HID 通信 |
| `tests/Rapoo.Core.Checks` | 无额外测试框架依赖的检查程序 |
| `packaging` | Inno Setup 脚本与运行依赖安装辅助脚本 |

## 调试与查看界面

修改 XAML 或 C# 后，先关闭正在运行的应用，再编译并启动：

```powershell
.\build.ps1
if ($LASTEXITCODE -eq 0) {
    Start-Process '.\src\Rapoo.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Rapoo.App.exe'
}
```

关闭旧进程可避免输出文件被占用，并确保看到本次修改后的界面。查看 UI 不需要重新生成或安装安装包。

只编译时运行 `.\build.ps1`；等价的直接命令为：

```powershell
dotnet build .\src\Rapoo.App\Rapoo.App.csproj -c Debug -p:Platform=x64 -p:BuildInParallel=false -m:1
```

需要断点调试时，在 Visual Studio 中打开 `RapooControl.sln`，选择 `Debug` 和 `x64`，将 `Rapoo.App` 设为启动项目后按 `F5`。

## 运行检查

离线检查覆盖日志容量、去重与并发访问，电量与 DPI 解析，七种回报率、HID 报告前缀、完整配置和无效应答处理。无需连接鼠标：

```powershell
dotnet build .\tests\Rapoo.Core.Checks\Rapoo.Core.Checks.csproj -p:BuildInParallel=false -m:1
if ($LASTEXITCODE -eq 0) {
    dotnet run --project .\tests\Rapoo.Core.Checks\Rapoo.Core.Checks.csproj --no-build --no-restore
}
```

成功时输出以 `PASS:` 开头的结果。检查程序通过抛出异常报告失败。

可选实机检查需插入支持的 2.4 GHz 接收器，并唤醒鼠标：

```powershell
dotnet run --project .\tests\Rapoo.Core.Checks\Rapoo.Core.Checks.csproj --no-build --no-restore -- --device
```

`--device` 会连接接收器，读取并打印回报率、七档 X/Y DPI、启用状态、传感器参数和底部 DIY 动作，**不修改设备设置**。该模式单独运行实机读取，不同时执行前述离线检查。请保持实体鼠标唤醒。

可选的实机写入验证会先读取当前配置，再原值重写传感器参数、DIY 和 X/Y DPI，提交后比较读回值，不使用默认配置。需明确添加 `--verify-writes`：

```powershell
dotnet run --project .\tests\Rapoo.Core.Checks\Rapoo.Core.Checks.csproj --no-build --no-restore -- --device --verify-writes
```

未知 DIY 动作会拒绝重写。此验证不测试其他物理键、组合键或宏。

## 发布目录

```powershell
# Release 发布：附带 Windows App SDK，目标计算机需安装 .NET 10 x64 运行时
.\build.ps1 -Publish

# Release 发布：同时附带 .NET 运行时
.\build.ps1 -Publish -SelfContained
```

输出均为 `artifacts/RapooControl`。发布时保留完整目录，应用 EXE 依赖同目录中的 DLL、PRI 等文件。切换发布方式时应使用干净的输出目录，避免前一次构建的运行库残留。安装包构建会自动使用全新的 staging 目录。

## 构建安装包

```powershell
.\build-installer.ps1

# 指定版本与 Inno Setup 编译器位置
.\build-installer.ps1 -Version 1.0.1 -InnoCompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

默认版本为 `1.0.0`，输出 `artifacts/installer/RapooControl-1.0.0-Setup-x64.exe`。分发时只需这个安装文件。脚本在 `PATH` 和常见安装目录中查找 `ISCC.exe`，也可通过参数指定。

安装包使用框架依赖发布，不内置 .NET、Windows App Runtime 或 Visual C++ 运行库。脚本从还原后的 NuGet SDK 元数据读取 Windows App Runtime 版本与包标识，生成 staging 目录中的 `requirements.json`，并检查必需文件及不应混入的运行库。

安装行为：

- 默认安装到当前用户的 `%LOCALAPPDATA%\Programs\RapooControl`。
- 复用已有依赖；缺失时从微软下载 .NET 10 x64 运行时、Visual C++ x64 运行库及与构建匹配的 Windows App Runtime。依赖齐全时可离线安装。
- 下载的安装程序必须通过微软数字签名验证。系统运行库安装可能请求管理员授权；Windows App Runtime 注册到当前用户。
- 依赖安装要求重启时，提示重启并跳过立即启动应用。
- 卸载删除应用文件和快捷方式，保留用户目录下的数据与共享运行库。

安装包默认不签名，正式分发时可另行配置代码签名。

### 依赖诊断

对本次安装包构建产生的 staging 目录进行只读检查，替换下方占位目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\Install-Prerequisites.ps1 -CheckOnly -RequirementsPath '.\artifacts\installer-staging\<本次构建目录>\requirements.json'
```

使用 x64 Windows PowerShell，保留同一 staging 目录中的 `Microsoft.WindowsAppRuntime.Bootstrap.dll`。`-CheckOnly` 不下载或安装依赖，输出 JSON 中的 `DotNet`、`VisualCpp`、`WindowsAppRuntime` 布尔值；此模式退出码为 `0`，判断是否齐全应查看这些值。

安装失败时查看 `%LOCALAPPDATA%\RapooControl\installer-prerequisites.log`。安装器允许在处理网络连接或管理员授权问题后重试。

## 配置读取与日志行为

连接时从设备读取 DPI、启用数量和回报率；读取失败时保持未读取状态并禁用编辑，重新连接可重试。应用 DPI 后重新读取完整配置，应用回报率后只读回报率确认。界面以设备读回值为准，不从本地 `settings.json` 恢复 DPI 或回报率，也不保存这两类配置。

设备将启用档位存放在前，因此读回后的编号按设备存储顺序排列。电量与当前 DPI 取自最近一条有效的被动报告，不主动查询或定时刷新；相同值不重复刷新界面。报告未提供档位编号，多个启用档位使用同一 DPI 时会同时高亮；没有匹配项时不推测当前档位。

“当前回报率”是设备配置读回值，不是下拉框待应用的选项或鼠标移动输入的实测频率。读取失败时显示 `—`，断线后清空。其他软件修改设备后，需要重新连接才能读取新配置。

通信日志只在内存中保留最近 128 条不同内容，相同方向与报文只保留最新一条；面板收起时不生成显示文本。旧版 `communication.log` 不再追加。去重后的日志用于诊断，无法还原完整抓包时序。

七种回报率协议代码已内置，无需创建本地扩展配置。协议代码见 [PROTOCOL.md](PROTOCOL.md)。

## 维护图标

`packaging/generate-icon.cjs` 从 `src/Rapoo.App/Assets/RapooLogo.svg` 生成多尺寸的 `Rapoo.ico`，仅维护图标时需要 Node.js 和 `sharp`。

若已有 `sharp`，可将其模块目录传给脚本：

```powershell
node .\packaging\generate-icon.cjs 'C:\path\to\node_modules\sharp'
```

若当前 Node.js 环境可以直接解析 `sharp`，则运行：

```powershell
node .\packaging\generate-icon.cjs
```
