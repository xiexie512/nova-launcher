@echo off
rem 编译 C 插件示例：产物输出到上级插件文件夹（c_demo.dll）
rem 用法: 在 plugins\src 下运行 build.bat
setlocal
cd /d "%~dp0"
cc -std=c11 -O2 -shared c_demo.c -o ..\c_demo.dll
if errorlevel 1 exit /b 1
echo 已生成: %~dp0..\c_demo.dll
