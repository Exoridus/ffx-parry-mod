@echo off
setlocal EnableExtensions EnableDelayedExpansion

if /I "%~1"=="build" (
    set "DEFAULT_TARGET=Help"
    set "HELP_TARGET=Help"
    set "CLI_TARGET=Cli"
    set "LOCK_LABEL=build"
    set "SMOKE_ONLY_VALUE=%BUILD_CMD_SMOKE_ONLY%"
    set "ALLOW_NESTED_VALUE=%BUILD_CMD_ALLOW_NESTED%"
    shift
    goto mode_ready
)

echo ERROR: missing or invalid launcher mode. Expected "build".
exit /B 2

:mode_ready
call :ensure_dotnet
if %ERRORLEVEL% NEQ 0 exit /B %ERRORLEVEL%

set "LOCKDIR="
set "LOCK_ENABLED=1"
if /I "%SMOKE_ONLY_VALUE%"=="1" set "LOCK_ENABLED=0"
if /I "%ALLOW_NESTED_VALUE%"=="1" set "LOCK_ENABLED=0"

if "%LOCK_ENABLED%"=="1" (
    if not exist ".nuke" mkdir ".nuke" >NUL 2>&1
    set "LOCKDIR=.nuke\cli.lock"

    if exist "!LOCKDIR!" (
        echo ERROR: another %LOCK_LABEL% invocation appears to be running, or a stale lock exists.
        echo If this is stale, remove "!LOCKDIR!" and retry.
        exit /B 9
    )

    mkdir "!LOCKDIR!" >NUL 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo ERROR: failed to create %LOCK_LABEL% lock directory "!LOCKDIR!".
        exit /B 9
    )

    >"!LOCKDIR!\started.txt" echo %DATE% %TIME%
)

set "NUKE_ARGS="
set "HAS_VERBOSITY=0"
set "REQUESTED_VERBOSITY="
set "RESOLVED_VERBOSITY="
set "NUKE_VERBOSITY="
set "PARSE_ERROR=0"

if "%~1"=="" (
    set "NUKE_ARGS=--target %DEFAULT_TARGET%"
    goto run_nuke
)

set "CMD=%~1"

if /I "%CMD%"=="-h" goto global_help
if /I "%CMD%"=="--help" goto global_help
if /I "%CMD%"=="help" goto global_help

if /I "%CMD:~0,1%"=="-" (
    goto append_passthrough_args
)

if /I "%~2"=="-h" goto workflow_help
if /I "%~2"=="--help" goto workflow_help

set "NUKE_ARGS=--target %CLI_TARGET% --workflow %CMD%"
shift

:append_workflow_args
if "%~1"=="" goto run_nuke
set "FORWARD_ARG=%1"
call :normalize_common_arg "%~1" "%~2" "%CMD%"
if "%PARSE_ERROR%"=="1" goto :parse_failed
if "%SKIP_CURRENT%"=="1" (
    if "%SHIFT_EXTRA%"=="1" shift
    shift
    goto append_workflow_args
)
if defined FORWARD_OVERRIDE set "FORWARD_ARG=!FORWARD_OVERRIDE!"
set "NUKE_ARGS=%NUKE_ARGS% %FORWARD_ARG%"
if "%SHIFT_EXTRA%"=="1" shift
shift
goto append_workflow_args

:append_passthrough_args
if "%~1"=="" goto run_nuke
set "FORWARD_ARG=%1"

call :normalize_common_arg "%~1" "%~2" ""
if "%PARSE_ERROR%"=="1" goto :parse_failed
if "%SKIP_CURRENT%"=="1" (
    if "%SHIFT_EXTRA%"=="1" shift
    shift
    goto append_passthrough_args
)
if defined FORWARD_OVERRIDE set "FORWARD_ARG=!FORWARD_OVERRIDE!"

if defined NUKE_ARGS (
    set "NUKE_ARGS=%NUKE_ARGS% !FORWARD_ARG!"
) else (
    set "NUKE_ARGS=!FORWARD_ARG!"
)
if "%SHIFT_EXTRA%"=="1" shift
shift
goto append_passthrough_args

:global_help
if "%~2"=="" (
    set "NUKE_ARGS=--target %HELP_TARGET%"
) else (
    set "HELP_ARG2=%~2"
    if /I "!HELP_ARG2:~0,1!"=="-" (
        set "NUKE_ARGS=--target %HELP_TARGET%"
        shift
        goto append_passthrough_args
    ) else (
        set "NUKE_ARGS=--target %HELP_TARGET% --workflow %~2"
    )
)
goto run_nuke

:workflow_help
set "NUKE_ARGS=--target %HELP_TARGET% --workflow %CMD%"
goto run_nuke

:parse_failed
set "EXIT_CODE=2"
goto :cleanup_and_exit

:run_nuke
call :resolve_verbosity
if %ERRORLEVEL% NEQ 0 (
    set "EXIT_CODE=%ERRORLEVEL%"
    goto :cleanup_and_exit
)

set "NUKE_TELEMETRY_OPTOUT=1"
set "NUKE_ARGS=%NUKE_ARGS% --verbosity %NUKE_VERBOSITY% --log-verbosity %RESOLVED_VERBOSITY%"

echo [NUKE] dotnet run --project build\Build.csproj -- %NUKE_ARGS%
if /I "%SMOKE_ONLY_VALUE%"=="1" (
    set "EXIT_CODE=0"
    goto :cleanup_and_exit
)

dotnet run --project build\Build.csproj -- %NUKE_ARGS%
set "EXIT_CODE=%ERRORLEVEL%"
goto :cleanup_and_exit

:cleanup_and_exit
if defined LOCKDIR if exist "%LOCKDIR%" rd /s /q "%LOCKDIR%" >NUL 2>&1
exit /B %EXIT_CODE%

:normalize_common_arg
set "SKIP_CURRENT=0"
set "SHIFT_EXTRA=0"
set "FORWARD_OVERRIDE="
set "ARG1=%~1"
set "ARG2=%~2"
set "ARG2_RAW=%2"
set "WORKFLOW=%~3"

if /I "%ARG1%"=="-n" (
    set "FORWARD_OVERRIDE=--dry-run"
    goto :eof
)

if /I "%ARG1%"=="-c" (
    set "SKIP_CURRENT=0"
    if "%ARG2%"=="" (
        echo ERROR: -c requires a config file path.
        set "PARSE_ERROR=1"
        goto :eof
    )
    set "FORWARD_OVERRIDE=--config-path %ARG2_RAW%"
    set "SHIFT_EXTRA=1"
    goto :eof
)

if /I "%ARG1%"=="--config" (
    echo ERROR: --config is no longer supported. Use --config-path or -c.
    set "PARSE_ERROR=1"
    goto :eof
)

set "CONFIG_PREFIX=!ARG1:~0,9!"
if /I "!CONFIG_PREFIX!"=="--config=" (
    echo ERROR: --config is no longer supported. Use --config-path or -c.
    set "PARSE_ERROR=1"
    goto :eof
)

if not "%WORKFLOW%"=="" (
    if /I "%ARG1%"=="--target" (
        echo ERROR: --target is no longer supported as workflow configuration override. Use --configuration.
        set "PARSE_ERROR=1"
        goto :eof
    )

    if /I "%ARG1%"=="--build-target" (
        echo ERROR: --build-target is no longer supported. Use --configuration.
        set "PARSE_ERROR=1"
        goto :eof
    )

    set "TARGET_PREFIX=!ARG1:~0,9!"
    if /I "!TARGET_PREFIX!"=="--target=" (
        echo ERROR: --target is no longer supported as workflow configuration override. Use --configuration.
        set "PARSE_ERROR=1"
        goto :eof
    )

    set "BUILD_TARGET_PREFIX=!ARG1:~0,15!"
    if /I "!BUILD_TARGET_PREFIX!"=="--build-target=" (
        echo ERROR: --build-target is no longer supported. Use --configuration.
        set "PARSE_ERROR=1"
        goto :eof
    )

)

:normalize_verbosity

if /I "%ARG1%"=="-v" (
    set "SKIP_CURRENT=1"
    if "%HAS_VERBOSITY%"=="1" (
        echo ERROR: verbosity specified multiple times.
        set "PARSE_ERROR=1"
        goto :eof
    )
    if "%ARG2%"=="" (
        echo ERROR: -v requires one of quiet^|minimal^|normal^|detailed^|diagnostic.
        set "PARSE_ERROR=1"
        goto :eof
    )
    set "HAS_VERBOSITY=1"
    set "REQUESTED_VERBOSITY=%ARG2%"
    set "SHIFT_EXTRA=1"
    goto :eof
)

if /I "%ARG1%"=="--verbosity" (
    set "SKIP_CURRENT=1"
    if "%HAS_VERBOSITY%"=="1" (
        echo ERROR: verbosity specified multiple times.
        set "PARSE_ERROR=1"
        goto :eof
    )
    if "%ARG2%"=="" (
        echo ERROR: --verbosity requires one of quiet^|minimal^|normal^|detailed^|diagnostic.
        set "PARSE_ERROR=1"
        goto :eof
    )
    set "HAS_VERBOSITY=1"
    set "REQUESTED_VERBOSITY=%ARG2%"
    set "SHIFT_EXTRA=1"
    goto :eof
)

if /I "%ARG1%"=="--quiet" (
    echo ERROR: --quiet is not supported. Use --verbosity quiet or -v quiet.
    set "PARSE_ERROR=1"
    goto :eof
)
if /I "%ARG1%"=="--verbose" (
    echo ERROR: --verbose is not supported. Use --verbosity detailed or -v detailed.
    set "PARSE_ERROR=1"
    goto :eof
)
if /I "%ARG1%"=="--trace" (
    echo ERROR: --trace is not supported. Use --verbosity diagnostic or -v diagnostic.
    set "PARSE_ERROR=1"
    goto :eof
)

set "PREFIX=!ARG1:~0,12!"
if /I "!PREFIX!"=="--verbosity=" (
    set "SKIP_CURRENT=1"
    if "%HAS_VERBOSITY%"=="1" (
        echo ERROR: verbosity specified multiple times.
        set "PARSE_ERROR=1"
        goto :eof
    )
    set "HAS_VERBOSITY=1"
    set "REQUESTED_VERBOSITY=!ARG1:~12!"
)

goto :eof

:resolve_verbosity
if "%HAS_VERBOSITY%"=="1" (
    set "NORMALIZED_VERBOSITY="
    set "RAW_VERBOSITY=%REQUESTED_VERBOSITY%"
    if /I "!RAW_VERBOSITY!"=="quiet" set "NORMALIZED_VERBOSITY=quiet"
    if /I "!RAW_VERBOSITY!"=="q" set "NORMALIZED_VERBOSITY=quiet"
    if /I "!RAW_VERBOSITY!"=="minimal" set "NORMALIZED_VERBOSITY=minimal"
    if /I "!RAW_VERBOSITY!"=="min" set "NORMALIZED_VERBOSITY=minimal"
    if /I "!RAW_VERBOSITY!"=="m" set "NORMALIZED_VERBOSITY=minimal"
    if /I "!RAW_VERBOSITY!"=="normal" set "NORMALIZED_VERBOSITY=normal"
    if /I "!RAW_VERBOSITY!"=="n" set "NORMALIZED_VERBOSITY=normal"
    if /I "!RAW_VERBOSITY!"=="detailed" set "NORMALIZED_VERBOSITY=detailed"
    if /I "!RAW_VERBOSITY!"=="detail" set "NORMALIZED_VERBOSITY=detailed"
    if /I "!RAW_VERBOSITY!"=="d" set "NORMALIZED_VERBOSITY=detailed"
    if /I "!RAW_VERBOSITY!"=="diagnostic" set "NORMALIZED_VERBOSITY=diagnostic"
    if /I "!RAW_VERBOSITY!"=="diag" set "NORMALIZED_VERBOSITY=diagnostic"
    if not defined NORMALIZED_VERBOSITY (
        echo ERROR: invalid --verbosity value "%REQUESTED_VERBOSITY%". Use quiet, minimal, normal, detailed, or diagnostic.
        exit /B 2
    )
    set "RESOLVED_VERBOSITY=!NORMALIZED_VERBOSITY!"
) else (
    set "RESOLVED_VERBOSITY=normal"
)

if /I "%RESOLVED_VERBOSITY%"=="quiet" (
    set "NUKE_VERBOSITY=quiet"
) else if /I "%RESOLVED_VERBOSITY%"=="minimal" (
    set "NUKE_VERBOSITY=minimal"
) else if /I "%RESOLVED_VERBOSITY%"=="normal" (
    set "NUKE_VERBOSITY=normal"
) else if /I "%RESOLVED_VERBOSITY%"=="detailed" (
    set "NUKE_VERBOSITY=verbose"
) else if /I "%RESOLVED_VERBOSITY%"=="diagnostic" (
    set "NUKE_VERBOSITY=verbose"
) else (
    echo ERROR: unsupported resolved verbosity "%RESOLVED_VERBOSITY%".
    exit /B 2
)

exit /B 0

:ensure_dotnet
where dotnet >NUL 2>&1
if !ERRORLEVEL! EQU 0 (
    dotnet --list-sdks | findstr /R /C:"^10\." >NUL
    if !ERRORLEVEL! EQU 0 exit /B 0
)

where winget >NUL 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: dotnet SDK 10.x missing and winget is unavailable.
    echo Install .NET SDK 10.x manually, then retry.
    exit /B 3
)

echo [MISSING] .NET SDK 10.x was not found.
echo This installation may require administrator privileges and can trigger a UAC prompt.
set "CONFIRM="
set /P CONFIRM=Install .NET SDK 10.x now? [y/N]:
if /I not "%CONFIRM%"=="Y" if /I not "%CONFIRM%"=="YES" (
    echo ERROR: installation declined.
    exit /B 3
)

echo Installing .NET SDK 10.x...
winget install --id "Microsoft.DotNet.SDK.10" -e --source winget --accept-source-agreements --accept-package-agreements --silent
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: failed to install .NET SDK 10.x.
    exit /B 4
)

where dotnet >NUL 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: dotnet command still not available. Open a new terminal and retry.
    exit /B 4
)

dotnet --list-sdks | findstr /R /C:"^10\." >NUL
if !ERRORLEVEL! NEQ 0 (
    echo ERROR: .NET SDK 10.x could not be verified after installation.
    exit /B 4
)

exit /B 0
