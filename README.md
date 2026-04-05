# FreeWPFShell - Windows SSH 客户端

FreeWPFShell 是一款基于 WPF 开发的Windows SSH 客户端。并集成了一个由 Rust 编写的轻量级远程监控 Agent。

---

## 核心特性

### 1. 高性能自定义终端
- **GPU 加速渲染**: 基于 Microsoft Terminal 的高性能终端渲染，支持真彩色及 ANSI 256 色显示。
- **自定义背景**: 支持设置图片背景（JPG/PNG/BMP），并内置 GPU 端高斯模糊 Shader 处理，支持 6 种拉伸模式。
- **模式自适应**: 自动识别 VT100/xterm 控制序列，实现 **Normal Mode** 与 **Application Mode** (DECCKM) 的无缝切换，完美兼容 `htop`、`vim` 等 TUI 程序。
- **外观高度自定义**: 支持自定义终端字体、字号及纯色背景色。

### 2. 集成监控
- **Rust Native 探针**: 自动向远程主机部署轻量级 Rust 代理 (`linux-monitor`)，通过 SSH 隧道提供系统状态数据。
- **混合监控逻辑**: 当探针不可用时，自动回退到 Shell 解析模式，确保对不同 Linux 的兼容性。
- **实时仪表盘**: 侧边栏提供 CPU、内存、网络 IO 及磁盘空间的直观展示。

### 3. 文件管理
- **双向传输**: 支持文件及文件夹的递归上传与下载。
- **服务器端极速复制**: 在同一服务器内粘贴时，自动转化为 `cp -a` 命令在本地执行，无需经过网络绕行。

### 4. 网络与隧道工具
- **可视化路由追踪**: 支持自定义超时时间和最大跳数，并发探测，展示每一跳的地理位置。
- **SSH 隧道管理**: 统一管理本地/远程端口转发，支持随会话自动启动与销毁。

### 5. 安全与性能优化
- **凭据保护**: 集成 Windows 凭据管理器，支持通过 Windows Hello 解密 SSH 密码。
- **延迟补偿**: 实时显示连接阶段状态，提升高延迟主机的交互反馈。

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
- [ ] 内置文本编辑器

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

---

## 📝 许可协议
MIT