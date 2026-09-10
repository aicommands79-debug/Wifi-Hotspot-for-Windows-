@echo off
title Windows 11 Wi-Fi Hotspot & Web Portali
rem En yeni surum klasorunu bul ve calistir (Yayin\r01, r02, ...)
set "BASE=%~dp0Yayin"
for /f "delims=" %%i in ('dir "%BASE%" /b /ad /o-n 2^>nul') do (
  start "" "%BASE%\%%i\Win11HotspotManager.exe"
  exit /b 0
)
echo Yayin klasoru bulunamadi: %BASE%
pause
