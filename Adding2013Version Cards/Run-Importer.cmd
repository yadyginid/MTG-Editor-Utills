@echo off
setlocal
if "%~2"=="" (
  echo Usage: Run-Importer.cmd "EXTRACTED_MAGIC_2013" "MAGIC_DUELS" ["PROFILE_FILE"]
  exit /b 2
)
if "%~3"=="" (
  dotnet run --project "%~dp0Adding2013VersionCards.csproj" -c Release -- --source "%~1" --game "%~2"
) else (
  dotnet run --project "%~dp0Adding2013VersionCards.csproj" -c Release -- --source "%~1" --game "%~2" --profile "%~3"
)
exit /b %errorlevel%
