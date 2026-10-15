@echo off
setlocal EnableExtensions EnableDelayedExpansion
pushd "%~dp0"
call "%~dp0build\cli-launch.cmd" build %*
set "RC=%ERRORLEVEL%"
popd
exit /B %RC%
