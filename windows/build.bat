@echo off
chcp 65001 >nul
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo 找不到 C# 编译器 csc.exe（Windows 自带 .NET Framework 4.x 应该都有）
  pause & exit /b 1
)
echo 正在编译...
"%CSC%" /nologo /target:winexe /out:"鑫洋V8电梯卡工具.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll XyV8Tool.cs
if errorlevel 1 ( echo 编译失败 & pause & exit /b 1 )
echo 编译完成：鑫洋V8电梯卡工具.exe
echo.
echo 跑一下自检（结果同时写入 自检结果_selftest.txt）...
set XYV8_NOMSG=1
"鑫洋V8电梯卡工具.exe" --selftest
type 自检结果_selftest.txt
pause
