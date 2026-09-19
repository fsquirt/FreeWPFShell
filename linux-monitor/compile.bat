@echo off
echo " _       ______  ___________ __  __________    __ "
echo "| |     / / __ \/ ____/ ___// / / / ____/ /   / / "
echo "| | /| / / /_/ / /_   \__ \/ /_/ / __/ / /   / /  "
echo "| |/ |/ / ____/ __/  ___/ / __  / /___/ /___/ /___"
echo "|__/|__/_/   /_/    /____/_/ /_/_____/_____/_____/"
echo "                                                  "

set "PROJECT_DIR=%~1"
if "%PROJECT_DIR%"=="" set "PROJECT_DIR=%~dp0"
echo [INFO] Cargo工作目录: %PROJECT_DIR%
pushd "%PROJECT_DIR%"

:: 1. 检测 Cargo 是否已安装
where cargo >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] 未找到 cargo 命令。请确保已安装 Rust 环境并添加到系统 PATH 中。
    exit /b 1
)

:: 2. 检测 Rust 编译目标 (x86_64-unknown-linux-musl) 是否已安装
rustup target list --installed | findstr /c:"x86_64-unknown-linux-musl" >nul
if %errorlevel% neq 0 (
    echo [WARNING] 未检测到 x86_64-unknown-linux-musl 目标，正在安装...
    rustup target add x86_64-unknown-linux-musl
    if %errorlevel% neq 0 (
        echo [ERROR] 自动安装 target 失败，请手动运行: rustup target add x86_64-unknown-linux-musl
        exit /b 1
    )
)

:: 3. 清理旧环境
echo [INFO] 清理旧编译产物...
if exist "target" rmdir /s /q target
if exist "linux-monitor" del linux-monitor

:: 4. 编译
echo [INFO] 开始执行 Cargo Release 编译...
cargo zigbuild --target x86_64-unknown-linux-musl --release
if %errorlevel% neq 0 (
    echo [ERROR] Cargo 编译过程中出错。
    exit /b 1
)

:: 5. 复制 linux-monitor 到 linux-monitor 根目录
echo [INFO] 移动 linux-monitor 到当前目录...
copy /Y "target\x86_64-unknown-linux-musl\release\linux-monitor" "linux-monitor"
if %errorlevel% neq 0 (
    echo [ERROR] 复制 linux-monitor 到项目目录失败。
    exit /b 1
)

echo [SUCCESS] Rust 模块已就绪。
exit /b 0