@echo off
rem Standalone build: ffmpeg embedded as a gzip resource, extracted on first run.
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - .NET Framework 4.x required
    exit /b 1
)

if not exist ffmpeg.exe.gz (
    echo ffmpeg.exe.gz not found - copy it from WebmStickerPatch\src or create with make-ffmpeg-gz.ps1
    exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /win32manifest:app.manifest /win32icon:app.ico ^
    /resource:ffmpeg.exe.gz,ffmpeg.gz ^
    /resource:uxlive-logo.png,uxlive.png ^
    /resource:phosphor-icons.ttf,phosphor.ttf ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /out:..\StickerStudio-Standalone.exe Program.cs Common.cs Controls.cs ChromaKey.cs VideoDoc.cs ExportPipeline.cs EditorUI.cs EditorView.cs

if %errorlevel%==0 (
    echo OK: ..\StickerStudio-Standalone.exe
) else (
    echo BUILD FAILED
    exit /b 1
)
