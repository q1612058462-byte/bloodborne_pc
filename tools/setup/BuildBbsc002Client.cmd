@echo off
setlocal EnableExtensions
rem Build the pinned BBSC v0.0.2 client after applying bbsc-0.0.2-client-1.8.7.patch.
rem Usage: BuildBbsc002Client.cmd <recursive BBSC source directory> <empty output directory>
if "%~1"=="" exit /b 2
if "%~2"=="" exit /b 2
set "BBSC_SOURCE=%~f1"
set "BBSC_OUTPUT=%~f2"
set "BBSC_MOD=%BBSC_SOURCE%\emulator\mod"
set "BBSC_EXTERNALS=%BBSC_SOURCE%\emulator\externals"
if not exist "%BBSC_MOD%\bbsc_mod.cpp" exit /b 3
if not exist "%BBSC_EXTERNALS%\fmt\include\fmt\format.h" exit /b 4
if not exist "%BBSC_EXTERNALS%\xbyak\xbyak\xbyak.h" exit /b 4
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" exit /b 5
for /f "usebackq delims=" %%V in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS_ROOT=%%V"
if not defined VS_ROOT exit /b 5
call "%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 5
set "CLANG_CL=%VS_ROOT%\VC\Tools\Llvm\x64\bin\clang-cl.exe"
if not exist "%CLANG_CL%" set "CLANG_CL=C:\Program Files\LLVM\bin\clang-cl.exe"
if not exist "%CLANG_CL%" exit /b 6
if exist "%BBSC_OUTPUT%\bbsc.dll" exit /b 7
if exist "%BBSC_OUTPUT%\bbsc-launch.exe" exit /b 7
if not exist "%BBSC_OUTPUT%\obj" mkdir "%BBSC_OUTPUT%\obj"
set FLAGS=/nologo /std:c++latest /O2 /MT /EHsc /W3 /DNOMINMAX /DWIN32_LEAN_AND_MEAN /D_CRT_SECURE_NO_WARNINGS /DBBSC_MOD /DFMT_HEADER_ONLY /I "%BBSC_MOD%\shim" /I "%BBSC_SOURCE%\emulator\src" /I "%BBSC_EXTERNALS%\fmt\include" /I "%BBSC_EXTERNALS%\xbyak"
"%CLANG_CL%" %FLAGS% /LD "%BBSC_MOD%\bbsc_mod.cpp" "%BBSC_MOD%\bbsc_hooks.cpp" "%BBSC_MOD%\bbsc_relay.cpp" "%BBSC_MOD%\bbsc_channel.cpp" "%BBSC_MOD%\bbsc_http.cpp" "%BBSC_MOD%\bbsc_discord.cpp" "%BBSC_SOURCE%\emulator\src\core\bbsc_memscan.cpp" "%BBSC_SOURCE%\emulator\src\core\bloodborne_re.cpp" /Fo"%BBSC_OUTPUT%\obj\\" /Fe"%BBSC_OUTPUT%\bbsc.dll" /link ws2_32.lib winhttp.lib user32.lib
if errorlevel 1 exit /b 8
"%CLANG_CL%" %FLAGS% "%BBSC_MOD%\bbsc_launch.cpp" /Fo"%BBSC_OUTPUT%\obj\\" /Fe"%BBSC_OUTPUT%\bbsc-launch.exe" /link user32.lib comdlg32.lib /SUBSYSTEM:WINDOWS /ENTRY:wmainCRTStartup
if errorlevel 1 exit /b 9
exit /b 0
