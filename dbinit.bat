@echo off
setlocal EnableExtensions
chcp 65001 >nul

if not defined PGPASSWORD goto :missing_pgpassword
if not defined ADMIN_PW goto :missing_admin_password

if not defined PGHOST set "PGHOST=localhost"
if not defined PGPORT set "PGPORT=5432"
if not defined PGUSER set "PGUSER=postgres"
if not defined PGDATABASE set "PGDATABASE=backup_monitor"
set "PGCLIENTENCODING=UTF8"

if defined PSQL goto :psql_ready
for %%V in (18 17 16 15 14) do if not defined PSQL if exist "C:\Program Files\PostgreSQL\%%V\bin\psql.exe" set "PSQL=C:\Program Files\PostgreSQL\%%V\bin\psql.exe"
if not defined PSQL goto :missing_psql

:psql_ready
echo === Create database %PGDATABASE% if needed ===
"%PSQL%" -U "%PGUSER%" -h "%PGHOST%" -p "%PGPORT%" -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname='%PGDATABASE%'" | findstr /x "1" >nul
if not errorlevel 1 goto :database_ready
"%PSQL%" -U "%PGUSER%" -h "%PGHOST%" -p "%PGPORT%" -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE %PGDATABASE% ENCODING 'UTF8' TEMPLATE template0;"
if errorlevel 1 goto :fail

:database_ready
set "BACKUPMONITOR_MIGRATION_DIRECTORY=%~dp0src\database"
set "BACKUPMONITOR_MIGRATION_LOG=%TEMP%\BackupMonitor-migration.log"

echo === Apply V001-V014 through MigrationRunner ===
if defined BACKUPMONITOR_MIGRATION_RUNNER goto :run_external_runner
where dotnet >nul 2>nul
if errorlevel 1 goto :missing_dotnet
dotnet run --project "%~dp0src\src\BackupMonitor.Api\BackupMonitor.Api.csproj" --configuration Release -- --migrate --migration-directory "%BACKUPMONITOR_MIGRATION_DIRECTORY%"
goto :migration_finished

:run_external_runner
"%BACKUPMONITOR_MIGRATION_RUNNER%" --migrate --migration-directory "%BACKUPMONITOR_MIGRATION_DIRECTORY%"

:migration_finished
if errorlevel 1 goto :fail

echo === Verify admin account and all migrations ===
"%PSQL%" -U "%PGUSER%" -h "%PGHOST%" -p "%PGPORT%" -d "%PGDATABASE%" -Atc "SELECT count(*) FROM users WHERE username='admin' AND status='active'" | findstr /x "1" >nul
if errorlevel 1 goto :verify_admin_failed
"%PSQL%" -U "%PGUSER%" -h "%PGHOST%" -p "%PGPORT%" -d "%PGDATABASE%" -Atc "SELECT count(DISTINCT left(version,4)) FROM schema_migrations WHERE left(version,4) IN ('V001','V002','V003','V004','V005','V006','V007','V008','V009','V010','V011','V012','V013','V014')" | findstr /x "14" >nul
if errorlevel 1 goto :verify_migrations_failed

echo.
echo DONE: database initialized and V001-V014 applied.
exit /b 0

:missing_pgpassword
echo ERROR: set PGPASSWORD to the PostgreSQL superuser password before running dbinit.bat.
exit /b 1

:missing_admin_password
echo ERROR: set ADMIN_PW to the initial BackupMonitor admin password before running dbinit.bat.
exit /b 1

:missing_psql
echo ERROR: PostgreSQL psql.exe was not found. Set PSQL to its full path.
exit /b 1

:missing_dotnet
echo ERROR: dotnet.exe was not found. Install the .NET 8 SDK or set BACKUPMONITOR_MIGRATION_RUNNER.
exit /b 1

:verify_admin_failed
echo ERROR: the active admin account was not found after migration.
goto :fail

:verify_migrations_failed
echo ERROR: V001-V014 were not all recorded in schema_migrations.
goto :fail

:fail
echo ERROR: database initialization failed. See %BACKUPMONITOR_MIGRATION_LOG% for migration details.
exit /b 1
