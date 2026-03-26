# FreeWPFShell - Windows SSH 客户端

FreeWPFShell 是一款基于 WPF 开发的Windows SSH 客户端。并集成了一个由 Rust 编写的轻量级远程监控 Agent。

---

## 核心特性

- **🛡️ 保护隐私**: 当选择将密码保存在Windows凭据管理器中的时候，必须使用Pin码或者Windows账户密码才能解密密码
- **🖥️ 高性能终端**: 基于 Windows Terminal 的 `WpfTerminalControl` 控件实现，支持流畅的ANSI渲染和鼠标点击。
- **📂 SFTP 资源管理器**: 终端与文件管理并排显示，支持拖拽上传下载、右键上下文菜单，以及实时的传输状态图标展示。
- **📡 SSH 隧道管理器**: 实时显示和管理所有连接中建立的本地/远程端口转发规则，支持动态添加，并直接绑定到活跃物理会话。
- **⚡ Rust 监控 Agent**:
  - 自动部署：连接主机后自动将轻量级 Rust 编译出的 binary 部署到 `/tmp`。
  - 实时快照：通过 SSH 隧道收集 CPU、内存、磁盘和进程统计信息。
  - 空闲自动退出：15 秒内未收到数据请求将自动退出

---

## 📸 界面预览

![主界面预览](https://www.cloudyou.top/images/wpfshell2.png)
![主界面预览](https://www.cloudyou.top/images/wpfshell1.png)

---

## Todo 

- 自定义终端界面背景颜色/图片
- 图形化进程管理页面
- 路由追踪页面
- 内置文本编辑器
- ...

---

## 🛠️ 编译

### 环境要求
- 大于120G的磁盘空间和大于20G的内存
- Visual Studio 2026，安装了C++ .NET WinUI桌面开发组件
- Windows 11 SDK
- Rust已安装并添加到环境变量
- zig已安装并添加到环境变量
- PowerShell 7.6.0

### 编译步骤

#### 1. 配置Rust编译环境
进入 `linux-monitor` 目录并运行下面命令：
```powershell
cargo install cargo-zigbuild
rustup target add x86_64-unknown-linux-musl
```

#### 2. 编译 Microsoft.Terminal.Wpf
选择一个至少有120GB空闲位置的目录，并运行下面命令
```powershell
git clone https://github.com/microsoft/terminal
cd .\terminal\
Import-Module .\tools\OpenConsole.psm1
Set-MsBuildDevEnvironment
Invoke-OpenConsoleBuild
cd .\src\cascadia\WpfTerminalControl\
msbuild WpfTerminalControl.csproj /t:Pack /p:Configuration=Release /p:Platform=x64
```

#### 3. 为项目还原 Microsoft.Terminal.Wpf 包
进入项目根目录，使用命令行：
```pwsh
dotnet add package Microsoft.Terminal.Wpf --source "Microsoft.Terminal.Wpf.0.1.0.nupkg所在路径"
```

#### 4. 编译主程序
使用命令行：
```pwsh
dotnet build
```

---

## 📝 许可协议
MIT