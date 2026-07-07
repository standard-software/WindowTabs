@echo off
rem Build DisplayChangeSim.exe with the .NET Framework compiler (no SDK needed)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /out:DisplayChangeSim.exe /target:exe DisplayChangeSim.cs
if %ERRORLEVEL%==0 echo Built DisplayChangeSim.exe
