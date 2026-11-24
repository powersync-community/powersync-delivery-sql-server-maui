-- Adds what CommunityToolkit.Datasync needs to the existing tables. Idempotent: safe to run again.
-- Run after db/init.sql (which stays the baseline schema). No new logins, no SQL Server features beyond
-- plain columns, indexes and triggers.
-- Follows https://communitytoolkit.github.io/Datasync/in-depth/server/db/azuresql/ and the EntityTableData
-- shape (Id, UpdatedAt, Version, Deleted): https://communitytoolkit.github.io/Datasync/in-depth/server/

USE DeliveryApp;
GO

-- 1. Version: an opaque value that changes on every write, for optimistic concurrency (If-Match / 412).
--    ROWVERSION does that by itself, for every writer, including dispatch's plain SQL.
IF COL_LENGTH(N'dbo.stops', N'version') IS NULL
  ALTER TABLE dbo.stops ADD version ROWVERSION;
GO
IF COL_LENGTH(N'dbo.deliveries', N'version') IS NULL
  ALTER TABLE dbo.deliveries ADD version ROWVERSION;
GO

-- 2. Deleted: the soft-delete flag. Soft delete stays off in the table controllers (nothing deletes stops or
--    deliveries), but the entity base class maps the column, so it has to exist.
IF COL_LENGTH(N'dbo.stops', N'deleted') IS NULL
  ALTER TABLE dbo.stops ADD deleted BIT NOT NULL CONSTRAINT df_stops_deleted DEFAULT 0;
GO
IF COL_LENGTH(N'dbo.deliveries', N'deleted') IS NULL
  ALTER TABLE dbo.deliveries ADD deleted BIT NOT NULL CONSTRAINT df_deliveries_deleted DEFAULT 0;
GO

-- 3. UpdatedAt: what incremental pull filters on (updatedAt gt <last seen>). stops already has updated_at;
--    deliveries gets one. Existing rows get the time of this migration.
IF COL_LENGTH(N'dbo.deliveries', N'updated_at') IS NULL
  ALTER TABLE dbo.deliveries ADD updated_at DATETIME2(7) NOT NULL CONSTRAINT df_deliveries_updated_at DEFAULT SYSUTCDATETIME();
GO

-- 4. Keep updated_at current on every insert and update, whoever writes (the docs' trigger, per table).
--    Without it, a PUT through the table controller or an UPDATE by another system that doesn't set
--    updated_at would never be pulled by devices. (The docs use GETUTCDATE(), which is DATETIME with ~3 ms
--    resolution; SYSUTCDATETIME() matches the column and the toolkit's own SQL Server tests.)
CREATE OR ALTER TRIGGER dbo.stops_datasync ON dbo.stops AFTER INSERT, UPDATE AS
BEGIN
  SET NOCOUNT ON;
  UPDATE dbo.stops SET updated_at = SYSUTCDATETIME() WHERE id IN (SELECT id FROM inserted);
END
GO

CREATE OR ALTER TRIGGER dbo.deliveries_datasync ON dbo.deliveries AFTER INSERT, UPDATE AS
BEGIN
  SET NOCOUNT ON;
  UPDATE dbo.deliveries SET updated_at = SYSUTCDATETIME() WHERE id IN (SELECT id FROM inserted);
END
GO

-- 5. Pulls filter and sort on updated_at, scoped to the driver (the access control provider's data view).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_stops_datasync' AND object_id = OBJECT_ID(N'dbo.stops'))
  CREATE INDEX ix_stops_datasync ON dbo.stops (driver_id, updated_at);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_deliveries_datasync' AND object_id = OBJECT_ID(N'dbo.deliveries'))
  CREATE INDEX ix_deliveries_datasync ON dbo.deliveries (driver_id, updated_at);
GO

PRINT 'DeliveryApp is ready for Datasync.';
GO
