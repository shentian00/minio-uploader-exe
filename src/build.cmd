@echo off
rem Build minio-uploader.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Output: ..\minio-uploader.exe
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /out:..\minio-uploader.exe ^
  /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Web.Extensions.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  Program.cs
exit /b %ERRORLEVEL%
