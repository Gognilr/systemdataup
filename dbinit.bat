@echo off
chcp 65001 >nul
if "%PGPASSWORD%"=="" (
  echo ERROR: PGPASSWORD is not set.
  echo   set PGPASSWORD=^<postgres password^>  ^&^&  dbinit.bat
  exit /b 1
)
set "PGCLIENTENCODING=UTF8"
set "PSQL=C:\Program Files\PostgreSQL\18\bin\psql.exe"

echo === [1/6] Create database backup_monitor ===
"%PSQL%" -U postgres -h localhost -tc "SELECT 1 FROM pg_database WHERE datname='backup_monitor'" | findstr "1" >nul
if not errorlevel 1 (
  echo Database already exists, skip creation.
) else (
  "%PSQL%" -U postgres -h localhost -c "CREATE DATABASE backup_monitor ENCODING 'UTF8' TEMPLATE template0;"
  if errorlevel 1 goto :fail
)

echo === [2/6] Run V001__initial_schema.sql ===
"%PSQL%" -U postgres -h localhost -d backup_monitor -v ON_ERROR_STOP=1 -f "%~dp0src\database\V001__initial_schema.sql"
if errorlevel 1 goto :fail

echo === [3/6] Run V002__refresh_tokens_and_batch_idempotency.sql ===
"%PSQL%" -U postgres -h localhost -d backup_monitor -v ON_ERROR_STOP=1 -f "%~dp0src\database\V002__refresh_tokens_and_batch_idempotency.sql"
if errorlevel 1 goto :fail

echo === [4/6] Run V003__scheduled_locks.sql ===
"%PSQL%" -U postgres -h localhost -d backup_monitor -v ON_ERROR_STOP=1 -f "%~dp0src\database\V003__scheduled_locks.sql"
if errorlevel 1 goto :fail

echo === [5/6] Run V004__idempotency_keys.sql ===
"%PSQL%" -U postgres -h localhost -d backup_monitor -v ON_ERROR_STOP=1 -f "%~dp0src\database\V004__idempotency_keys.sql"
if errorlevel 1 goto :fail

echo === [6/6] Verify ===
"%PSQL%" -U postgres -h localhost -d backup_monitor -c "SELECT count(*) AS table_count FROM pg_tables WHERE schemaname='public';"
"%PSQL%" -U postgres -h localhost -d backup_monitor -c "SELECT 'client_groups' AS seed_table, count(*) AS rows FROM client_groups UNION ALL SELECT 'roles', count(*) FROM roles UNION ALL SELECT 'system_settings', count(*) FROM system_settings UNION ALL SELECT 'task_templates', count(*) FROM backup_task_templates ORDER BY 1;"

echo === DONE ===
exit /b 0

:fail
echo === FAILED (see errors above) ===
exit /b 1
