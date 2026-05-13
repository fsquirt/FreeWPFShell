# FreeWPFShell - Windows SSH 客户端

FreeWPFShell 是一款基于 WPF 开发的Windows SSH 客户端。并集成了一个由 Rust 编写的轻量级远程监控 Agent。

---

## 下载

<a href="https://get.microsoft.com/installer/download/9pb305xqm927?referrer=appbadge" target="_self">
  <img src="https://get.microsoft.com/images/zh-cn%20dark.svg" width="200"/>
</a>

---

## 核心特性

### 1. 高性能自定义终端
- **GPU 加速渲染**: 基于 Microsoft Terminal 的高性能终端渲染，支持真彩色及 ANSI 256 色显示。
- **自定义背景**: 支持设置图片背景（JPG/PNG/BMP），内置 GPU 高斯模糊 Shader，支持 6 种拉伸模式。
- **模式自适应**: 自动识别 VT100/xterm 控制序列，Normal Mode 与 Application Mode (DECCKM) 无缝切换，完美兼容 `htop`、`vim` 等 TUI 程序。
- **外观自定义**: 支持自定义字体、字号及纯色背景色。
- **中文支持**: 连接时自动注入 `LANG` 环境变量，中文及 Unicode 字符正常显示。

### 2. 文件管理 (SFTP)
- **双向传输**: 文件及文件夹的递归上传与下载，带进度显示。
- **远程文件编辑**: 双击远程文件自动下载并用系统编辑器打开，保存后自动回传。
- **服务器端极速复制**: 同服务器内粘贴时转化为 `cp -a` 命令在服务器本地执行，无需网络绕行。
- **权限可视化**: 以 `rwx` 格式展示 Unix 文件权限。

### 3. 集成系统监控
- **Rust 探针**: 自动向远程主机部署轻量级 Rust 代理，通过 SSH 隧道提供实时系统状态。
- **混合监控**: 探针不可用时自动回退到 Shell 解析模式，兼容不同 Linux 发行版。
- **实时仪表盘**: 侧边栏提供 CPU、内存、网络 IO 及磁盘空间的图表展示。

### 4. 系统管理
- **进程管理**: 查看所有进程及详细信息（PID/PPID/状态/优先级/CPU 时间/文件描述符/内存详情/ulimit/cwd/命令行/信号/TTY），支持 Kill 和 Killall。
- **Systemd 服务管理**: 查看服务状态（活动状态/子状态/加载状态/PID/用户/用户组），支持 start/stop/restart 及日志查看。
- **Cron 任务管理**: 查看、添加、删除、启用/禁用 crontab 任务。
- **登录日志**: 查看用户登录/登出记录 (`/var/log/wtmp`) 及失败登录尝试 (`/var/log/btmp`)。
- **网络连接**: 查看所有 TCP/UDP 连接及其所属进程。

### 5. 网络与隧道工具
- **路由追踪**: 自定义超时和最大跳数，并发探测，每跳展示 IP 地理位置。
- **SSH 隧道管理**: 统一管理本地/远程端口转发，支持随会话自动启动与销毁。

### 6. 安全
- **凭据保护**: 集成 Windows 凭据管理器，支持通过 Windows Hello 解密 SSH 密码。

---

## 📸 界面预览

![主界面预览](https://www.cloudyou.top/images/FreeWPFshell/FreeWPFShellMainForm.png)
![主界面预览](https://www.cloudyou.top/images/FreeWPFshell/FWS2.png)
![主界面预览](https://www.cloudyou.top/images/FreeWPFshell/FWS3.png)
![主界面预览](https://www.cloudyou.top/images/FreeWPFshell/FWS4.png)

---

## Todo 

- [x] 自定义终端界面背景颜色/图片
- [x] 图形化进程管理页面
- [x] 路由追踪页面
- [x] 文本编辑器

---

## 🛠️ 编译

### 环境要求
- 大于120G的磁盘空间和大于20G的内存
- Visual Studio 2026，安装了C++ .NET WinUI桌面开发组件，v143生成工具，v145生成工具
- Windows 11 SDK
- Rust已安装并添加到环境变量
- zig已安装并添加到环境变量
- PowerShell 7.6.0 +

### 编译步骤

#### 1. 配置Rust编译环境
进入 `linux-monitor` 目录并运行下面命令：
```powershell
cargo install cargo-zigbuild
rustup target add x86_64-unknown-linux-musl
```

#### 2. 编译 Microsoft.Terminal.Wpf
选择一个至少有120GB空闲位置的目录，并运行下面命令

> 因为原版的Microsoft Terminal不支持自定义图像背景，所以需要clone我这个分支版本

```pwsh
git clone https://github.com/fsquirt/terminal
cd .\terminal\
Import-Module .\tools\OpenConsole.psm1
Set-MsBuildDevEnvironment
Invoke-OpenConsoleBuild
msbuild OpenConsole.slnx /p:Configuration=Release /p:Platform=x64 /p:DebugSymbols=false /maxCpuCount:20
msbuild OpenConsole.slnx /p:Configuration=Release /p:Platform=x86 /p:DebugSymbols=false /maxCpuCount:20
msbuild OpenConsole.slnx /p:Configuration=Release /p:Platform=ARM64 /p:DebugSymbols=false /maxCpuCount:20
cd .\src\cascadia\WpfTerminalControl\
msbuild WpfTerminalControl.csproj /t:Pack /p:Configuration=Release /p:DebugSymbols=false /maxCpuCount:20
```

nupkg文件会生成在 `.\bin\x64\Release\WpfTerminalControl` 目录下。

或者，你也可以下载我已经编译好的Microsoft.Terminal.Wpf: 

https://www.cloudyou.top/files/Microsoft.Terminal.Wpf.0.1.0.nupkg

#### 3. 为项目还原 Microsoft.Terminal.Wpf 包
进入项目根目录，使用命令行：
```pwsh
dotnet add package Microsoft.Terminal.Wpf --source "Microsoft.Terminal.Wpf.0.1.0.nupkg所在路径"
```

#### 4. 编译MicaWPF
> 因为原版MicaWPF的ComboBox图标是坏掉的，所以需要使用我这个分支版本
```pwsh
git clone https://github.com/fsquirt/MicaWPF
cd .\MicaWPF\
dotnet build --configuration Release
mv .\src\MicaWPF\bin\Release\MicaWPF.1.0.0.nupkg .
mv .\src\MicaWPF.Core\bin\Release\MicaWPF.Core.1.0.0.nupkg .
cd ..
git clone https://github.com/fsquirt/MicaWPFRuntimeComponent
cd .\MicaWPFRuntimeComponent\
msbuild .\MicaWPFRuntimeComponent.sln /p:Configuration=Release
mv .\MicaWPF.Projection\nuget\MicaWPFRuntimeComponent.1.1.9.nupkg ..\MicaWPF\
```

或者，你也可以下载我已经编译好的MicaWPF: 

https://www.cloudyou.top/files/MicaWPF.1.0.0.nupkg

https://www.cloudyou.top/files/MicaWPF.Core.1.0.0.nupkg

https://www.cloudyou.top/files/MicaWPFRuntimeComponent.1.1.9.nupkg

#### 5.为项目还原 MicaWPF
进入项目根目录，使用命令行：
```pwsh
dotnet add package MicaWPF --source "MicaWPF项目根目录"
```

#### 6. 编译主程序
使用命令行：
```pwsh
dotnet build
```
或者这样
```pwsh
dotnet publish FreeWPFShell.csproj -c Release -r win-x64 -o ./publish --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:PublishTrimmed=false
```

---

## 📝 许可协议
MIT