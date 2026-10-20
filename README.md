# Rapoo VT1 Air Max 控制协议

本文说明 Rapoo VT1 Air Max 2.4 GHz 接收器的 DPI、档位开关、回报率和电量协议。

## 调试编译与查看 UI 效果

修改 `src\Rapoo.App\MainWindow.xaml` 等 UI 文件后，先关闭正在运行的应用，再在项目根目录的 PowerShell 中执行：

```powershell
# 编译 Debug 版本，成功后启动，查看本次 UI 修改的效果
.\build.ps1
if ($LASTEXITCODE -eq 0) {
    Start-Process '.\src\Rapoo.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Rapoo.App.exe'
}
```

每次修改后重复上述步骤即可，无需生成或重新安装安装包。先关闭旧进程可避免编译时输出文件被占用，并确保查看的是新版本界面。

如果只想编译而不启动：

```powershell
.\build.ps1
# 等价的直接编译命令：
dotnet build .\src\Rapoo.App\Rapoo.App.csproj -c Debug -p:Platform=x64 -p:BuildInParallel=false -m:1
```

调试编译需要 Windows、.NET 10 SDK 和项目所需的 Windows SDK，不需要 Inno Setup。上面的启动命令仅运行应用；如需断点调试，可在 Visual Studio 中打开 `RapooControl.sln`，选择 `Debug`、`x64`，将 `Rapoo.App` 设为启动项目后按 `F5`。

## 轻量运行与通信日志

DPI 档位、启用数量和回报率在连接时从鼠标读取，编辑区不加载本地 `settings.json` 或预设默认值，已移除“恢复默认值”。读取失败时保持未读取状态并禁用编辑，重新连接可重试。应用 DPI 后会再次读回完整配置，界面以读回结果为准。设备将启用档位排在前面，因此重读后档位编号按设备存储顺序排列，不保留本地旧排序。

电量和当前 DPI 仅来自设备被动上报的最近一条有效报文，无主动电量查询或定时刷新；数值不变时不刷新界面。DPI 菜单会高亮与报告值相同且已启用的档位，并标注“当前 DPI”。报告不包含档位编号，因此多个启用档位具有相同 DPI 时都会高亮；编辑区与设备配置不一致或没有匹配档位时，不推测当前档位。

“当前回报率”显示从设备读回的配置值，不是下拉框选择值，也不是移动输入的实测频率。连接时复用完整配置读取，应用 DPI 后随完整配置读回，应用回报率后只读取回报率确认，后台不轮询，不采集鼠标移动输入。读取失败或应答无法识别时显示 `—`，不会用本地保存值代替；重新连接可重试。断线后清空当前值。通过其他软件修改配置不会立即同步，需要重新连接读取。

通信日志只保留最近 128 条不同内容，相同方向、相同报文只留最新一条，不持续写入硬盘。日志面板收起时不生成显示文本；旧版已有的 `communication.log` 不会继续追加。DPI 和回报率配置不再写入本地文件，以鼠标保存的实际配置为准。日志经过合并，不能作为完整的抓包时序记录。

缓存与报文解析检查（不连接鼠标，无额外测试框架依赖）：

```powershell
dotnet build .\tests\Rapoo.Core.Checks\Rapoo.Core.Checks.csproj -p:BuildInParallel=false -m:1
dotnet run --project .\tests\Rapoo.Core.Checks\Rapoo.Core.Checks.csproj --no-build --no-restore
```

可选实机检查：在第二条命令末尾加上 `-- --device`，将连接接收器并执行一次完整配置读取事务，显示回报率、七档 DPI 和启用状态，不修改设备设置。

## 构建 Windows 安装包

构建环境需要 Windows、.NET 10 SDK、项目所需的 Windows SDK，以及 [Inno Setup 6.3 或更新版本](https://jrsoftware.org/isdl.php)。在项目根目录执行：

```powershell
.\build-installer.ps1
# 指定版本或编译器位置：
.\build-installer.ps1 -Version 1.0.1 -InnoCompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

输出为 `artifacts\installer\RapooControl-1.0.0-Setup-x64.exe`，只需分发这一个文件。安装包使用依赖框架的发布方式，不内置 .NET、Windows App Runtime 或 Visual C++ 运行库。安装后应用由安装目录内的 EXE、DLL 和 PRI 等文件共同组成，不能单独移动应用 EXE。

安装到当前用户的 `%LOCALAPPDATA%\Programs\RapooControl`，支持 Windows 10 2004 或更新版本的 x64 系统。安装时检测并复用现有依赖；缺失时从微软下载 .NET 10 x64 运行时、Visual C++ x64 运行库以及与构建版本匹配的 Windows App Runtime。系统运行库安装可能弹出管理员授权，Windows App Runtime 则注册到当前用户。下载的安装程序会验证微软数字签名，失败时停止应用安装并允许重试；遇到重启要求时提示重启并跳过立即启动应用。

依赖齐全时可离线安装；缺失依赖时需要联网。失败日志位于 `%LOCALAPPDATA%\RapooControl\installer-prerequisites.log`。卸载只删除应用文件和快捷方式，保留用户配置以及共享运行库。安装包默认不签名；对外正式发布时可另行配置代码签名证书。

依赖版本及包标识在构建时从 NuGet SDK 元数据自动读取。诊断依赖（只读，不安装）可对本次构建的 staging 目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\Install-Prerequisites.ps1 -CheckOnly -RequirementsPath '.\artifacts\installer-staging\<本次构建目录>\requirements.json'
```

## 设备与传输

| 项目 | 值 |
| --- | --- |
| USB Vendor ID | `0x24AE` |
| USB Product ID | `0x1234` |
| 配置接口 | USB Interface `1` |
| 配置输出报告 ID | `0xBA` |
| 配置报告长度 | 32 字节，未使用的尾部字节填 `00` |
| 配置写入 | HID `SET_REPORT`，`wValue=0x02BA`（Output Report） |
| 配置应答读取 | HID `GET_REPORT`，`wValue=0x01BA`（Input Report） |
| 电量输入报告 ID | `0xBB` |
| 电量输入端点 | `0x84` |

C# 使用 `HidD_SetOutputReport` 写入配置，使用 `HidD_GetInputReport` 读取配置应答。电量通过 HID 输入报告接收。

以下字节偏移均从 `0` 开始，包含报告 ID；报文中的多字节 DPI 数值使用小端序。

## DPI 与档位开关

支持 7 个 DPI 档位，每档可独立启用或关闭，至少启用一档。DPI 范围为 **50–45000**，步进为 **50**。

### 配置报文

| 偏移 | 长度 | 内容 |
| --- | ---: | --- |
| 0–3 | 4 | 固定值 `BA A5 A5 10` |
| 4–7 | 4 | 第一条为 `88 08 00 00`，第二条为 `C8 08 00 00` |
| 8–21 | 14 | 7 个 16 位无符号 DPI 值，小端序 |
| 22 | 1 | 启用档位数减一，范围 `00`–`06` |
| 23–31 | 9 | `00` |

DPI 值按以下规则排列：

1. 启用档位在前，按界面档位顺序排列。
2. 关闭档位在后，同样按界面档位顺序排列。
3. 全部启用时，依次为档位 1–7。

例如，启用 4 档时，偏移 22 为 `03`；启用全部 7 档时为 `06`。1450 DPI 编码为 `AA 05`。

### 写入顺序

依次发送以下 5 条 32 字节 Output Report；省略部分以 `00` 补齐：

```text
BA A5 A4 18 98 08 00 00 ...
BA A5 A5 10 88 08 00 00 <14 字节 DPI 列表> <启用数减一> ...
BA A5 A5 10 C8 08 00 00 <14 字节 DPI 列表> <启用数减一> ...
BA A5 A4 18 98 08 00 00 ...
BA A5 A3 00 00 00 00 00 ...
```

两条 DPI 配置报文的 DPI 列表和启用数量必须一致。

### 编码示例

七档分别为 800、1450、400、1200、3200、6400、45000 DPI，且全部启用时，第一条 DPI 配置报文为：

```text
BA A5 A5 10 88 08 00 00 20 03 AA 05 90 01 B0 04 80 0C 00 19 C8 AF 06 00 00 00 00 00 00 00 00 00
```

第二条仅将偏移 4 的 `88` 改为 `C8`。

## 回报率

### 当前值读取

连接和应用 DPI 后使用 `BA A5 A4 18 80 08 00 00 …` 一次读取 0x0880 起始的 24 字节配置。去掉可选的 BA 报告 ID 后，应答前 4 字节为状态，偏移 4 为回报率代码，偏移 12–25 为七个小端序 DPI，偏移 26 为启用档位数减一。前 N 档启用，其余关闭。应用回报率后则只读下面的 4 字节区域，避免不必要的数据读取。

发送以下 32 字节请求，随后等待约 50 ms，以 HID `GET_REPORT` 读取 Input BA；应答未就绪时，在同一次事务中最多尝试 5 次，每次间隔 50 ms，不重复发送读取请求，不启动后台轮询：

```text
BA A5 A4 04 80 08 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

抓包应答为 `01 00 00 00 CODE 01 01 FF`，`CODE` 使用下表映射；解析兼容 Windows HID 缓冲区的前导 `BA` 报告 ID 及尾部补零。只在这次明确的读取请求后解析，不将其他配置应答当成回报率。例：`01 00 00 00 82 01 01 FF` 表示设备配置为 4000 Hz。

依据：`125hz.pcapng` 第 7–10 帧、`250-2000hz.pcapng` 第 433–436 / 567–570 帧、`1k-4k-8k.pcapng` 第 3975–3978 帧。此处读取的是设备配置值，不用于证明实际输入达到该频率。

支持 **125 / 250 / 500 / 1000 / 2000 / 4000 / 8000 Hz**。

| 回报率 | 单字节协议代码 |
| ---: | --- |
| 125 Hz | `08` |
| 250 Hz | `04` |
| 500 Hz | `02` |
| 1000 Hz | `01` |
| 2000 Hz | `84` |
| 4000 Hz | `82` |
| 8000 Hz | `81` |

### 配置报文

| 偏移 | 长度 | 内容 |
| --- | ---: | --- |
| 0–7 | 8 | 固定值 `BA A5 A5 04 80 08 00 00` |
| 8 | 1 | 回报率协议代码 |
| 9–11 | 3 | 固定值 `01 01 FF` |
| 12–31 | 20 | `00` |

### 写入顺序

依次发送以下 4 条 32 字节 Output Report；省略部分以 `00` 补齐：

```text
BA A5 A4 04 80 08 00 00 ...
BA A5 A5 04 80 08 00 00 <回报率代码> 01 01 FF ...
BA A5 A3 00 00 00 00 00 ...
BA A5 A4 18 80 08 00 00 ...
```

例如，8000 Hz 的完整配置报文为：

```text
BA A5 A5 04 80 08 00 00 81 01 01 FF 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

## 电量与当前 DPI

电量输入报告有效数据为 7 字节：

```text
BB B0 71 <DPI 低字节> <DPI 高字节> 01 <电量百分比>
```

| 偏移 | 长度 | 内容 |
| --- | ---: | --- |
| 0–2 | 3 | 固定值 `BB B0 71` |
| 3–4 | 2 | 当前 DPI，16 位无符号整数，小端序 |
| 5 | 1 | 固定值 `01` |
| 6 | 1 | 电量百分比，整数范围 0–100，即 `00`–`64` |

例如：

```text
BB B0 71 AA 05 01 23
```

表示当前 DPI 为 **1450**，电量为 **35%**。
