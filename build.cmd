@echo off
rem Builds BookmarkSync.exe and the extension for each browser. Needs the .NET SDK for the exe.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
