-- Prepares DeliveryApp for Dotmim.Sync's SqlSyncChangeTrackingProvider. Idempotent: safe to run again.
-- Run as sysadmin (or a login with ALTER DATABASE), once, before the API's first sync.
--
-- This is the only thing a DBA has to do by hand. On the first sync the API itself (it connects as sa
-- in this demo) enables change tracking on each synced table and creates Dotmim.Sync's scope tables,
-- stored procedures and table types. NOTES.md lists everything it creates.

-- Change tracking for the database. SQL Server then records which rows changed (not their old values),
-- and cleans up after CHANGE_RETENTION. A device that hasn't synced for longer than that is "outdated"
-- and has to reinitialize (download everything again).
-- Production: pick the retention to match how long a device can stay offline.
IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID(N'DeliveryApp'))
  ALTER DATABASE DeliveryApp SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 14 DAYS, AUTO_CLEANUP = ON);
GO

SELECT DB_NAME(database_id) AS change_tracking_on, retention_period, retention_period_units_desc
FROM sys.change_tracking_databases;
GO
