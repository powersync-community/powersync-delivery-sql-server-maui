-- Prepares the DeliveryApp database for PowerSync replication. Idempotent: safe to run again.
-- Run as sysadmin (it enables CDC and creates a server login), with the PowerSync user's password
-- passed as a sqlcmd variable:
--   sqlcmd ... -v POWERSYNC_DB_PASSWORD="..." -i db/powersync.sql
-- Follows https://docs.powersync.com/configuration/source-db/setup#sql-server

-- 1. Work in the app database.
USE DeliveryApp;
GO

-- 2. CDC capture runs as a SQL Server Agent job. With the Agent stopped, CDC can be enabled without
--    any error, but nothing is ever captured and PowerSync silently never sees a change. Fail loudly.
IF NOT EXISTS (
  SELECT 1 FROM sys.dm_server_services
  WHERE servicename LIKE N'SQL Server Agent%' AND status_desc = N'Running'
)
  THROW 50000, 'SQL Server Agent is not running, so CDC would capture nothing. In Docker, set MSSQL_AGENT_ENABLED=true on the sqlserver container.', 1;
GO

-- 3. Enable CDC on the database.
IF (SELECT is_cdc_enabled FROM sys.databases WHERE name = N'DeliveryApp') = 0
  EXEC sys.sp_cdc_enable_db;
GO

-- 4. Enable CDC on the replicated tables. The first call also creates the gating role cdc_reader,
--    which PowerSync checks the user is a member of (step 5).
IF NOT EXISTS (SELECT 1 FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dbo.stops'))
  EXEC sys.sp_cdc_enable_table
    @source_schema = N'dbo', @source_name = N'stops',
    @role_name = N'cdc_reader', @supports_net_changes = 0;
GO

IF NOT EXISTS (SELECT 1 FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dbo.deliveries'))
  EXEC sys.sp_cdc_enable_table
    @source_schema = N'dbo', @source_name = N'deliveries',
    @role_name = N'cdc_reader', @supports_net_changes = 0;
GO

-- 5. A login and user for PowerSync, with read-only access to the tables and CDC data.
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'powersync_user')
  CREATE LOGIN powersync_user WITH PASSWORD = N'$(POWERSYNC_DB_PASSWORD)', CHECK_POLICY = ON;
ELSE
  -- Keep the password in step with .env when the script is run again.
  ALTER LOGIN powersync_user WITH PASSWORD = N'$(POWERSYNC_DB_PASSWORD)';
-- Lets PowerSync read sys.dm_db_log_stats (SQL Server only, not Azure SQL).
GRANT VIEW SERVER PERFORMANCE STATE TO powersync_user;
GO

USE DeliveryApp;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'powersync_user')
  CREATE USER powersync_user FOR LOGIN powersync_user;
GRANT SELECT ON dbo.stops TO powersync_user;
GRANT SELECT ON dbo.deliveries TO powersync_user;
IF IS_ROLEMEMBER(N'cdc_reader', N'powersync_user') = 0
  ALTER ROLE cdc_reader ADD MEMBER powersync_user;
GRANT SELECT ON SCHEMA::cdc TO powersync_user;
-- Lets PowerSync read sys.dm_db_log_stats and sys.dm_db_partition_stats.
GRANT VIEW DATABASE PERFORMANCE STATE TO powersync_user;
GO

-- 6. PowerSync writes to this table to create regular checkpoints in the CDC stream.
IF OBJECT_ID(N'dbo._powersync_checkpoints', N'U') IS NULL
  CREATE TABLE dbo._powersync_checkpoints (
    id INT IDENTITY PRIMARY KEY,
    last_updated DATETIME NOT NULL DEFAULT GETUTCDATE()
  );
GO

IF NOT EXISTS (SELECT 1 FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dbo._powersync_checkpoints'))
  EXEC sys.sp_cdc_enable_table
    @source_schema = N'dbo', @source_name = N'_powersync_checkpoints',
    @role_name = N'cdc_reader', @supports_net_changes = 0;
GRANT SELECT, INSERT, UPDATE ON dbo._powersync_checkpoints TO powersync_user;
GO

-- 7. Scan the transaction log every second instead of every 5 (the docs' recommended capture job
--    settings). This is most of the delay between a server write and the change reaching devices.
--    New settings only apply once the capture job restarts, so restart it.
--    Stopping a job that isn't running (or starting one that is) raises Agent error 22022 from an
--    extended procedure, which TRY/CATCH can't trap and which makes `sqlcmd -b` exit 1. So check the
--    job's actual state first and only issue the call that applies.
EXEC sys.sp_cdc_change_job @job_type = N'capture', @pollinginterval = 1, @maxtrans = 5000, @maxscans = 10;
GO

DECLARE @job SYSNAME = N'cdc.' + DB_NAME() + N'_capture';
DECLARE @state NVARCHAR(10), @stopRequested BIT = 0, @tries INT = 0;

-- Poll the job's state in the current Agent session, once a second for up to ~20 s:
--   pending: a start was requested but the job isn't running yet. Enabling CDC on the first table
--            (step 4) requests one, and stopping or starting a pending job also fails with 22022.
--   running: stop it (once), then keep waiting for the stop to land.
--   idle:    done waiting.
WHILE @tries < 20
BEGIN
  SET @state = N'idle';
  SELECT @state = CASE
      WHEN ja.run_requested_date IS NULL OR ja.stop_execution_date IS NOT NULL THEN N'idle'
      WHEN ja.start_execution_date IS NULL THEN N'pending'
      ELSE N'running' END
  FROM msdb.dbo.sysjobactivity ja JOIN msdb.dbo.sysjobs j ON j.job_id = ja.job_id
  WHERE j.name = @job AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions);

  IF @state = N'idle'
    BREAK;
  IF @state = N'running' AND @stopRequested = 0
  BEGIN
    EXEC sys.sp_cdc_stop_job @job_type = N'capture';
    SET @stopRequested = 1;
  END
  WAITFOR DELAY '00:00:01';
  SET @tries += 1;
END

IF @state <> N'idle'
  THROW 50000, 'The CDC capture job did not stop in time, so the new settings are not applied. Run this script again.', 1;
EXEC sys.sp_cdc_start_job @job_type = N'capture';
GO

PRINT 'DeliveryApp is ready for PowerSync.';
GO
