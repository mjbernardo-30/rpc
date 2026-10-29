@echo off
REM Runs ReleaseChecker against the bundled sample repo (mirrors oms\SG\Database\...)
dotnet run --project "%~dp0ReleaseChecker.csproj" -- All --repo "%~dp0sample\oms" --report "%~dp0sample-report.txt"
echo Exit code: %ERRORLEVEL%
