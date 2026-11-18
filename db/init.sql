-- Creates the DeliveryApp database and its tables. Idempotent: safe to run again.

IF DB_ID(N'DeliveryApp') IS NULL
  CREATE DATABASE DeliveryApp;
GO

USE DeliveryApp;
GO

-- One row per package drop. Dispatch creates and cancels stops; drivers only record outcomes.
-- route_date is the UTC date. Good enough for a demo; a real app would use the warehouse's local date.
IF OBJECT_ID(N'dbo.stops', N'U') IS NULL
CREATE TABLE dbo.stops (
  id          UNIQUEIDENTIFIER NOT NULL CONSTRAINT pk_stops PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
  driver_id   NVARCHAR(64)     NOT NULL,
  route_date  DATE             NOT NULL,
  sequence    INT              NOT NULL,
  recipient   NVARCHAR(100)    NOT NULL,
  address     NVARCHAR(300)    NOT NULL,
  -- 'assigned' until the driver records an outcome. 'canceled' is set only by dispatch.
  status      NVARCHAR(16)     NOT NULL DEFAULT N'assigned'
              CONSTRAINT ck_stops_status CHECK (status IN ('assigned', 'delivered', 'failed', 'canceled')),
  updated_at  DATETIME2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
  -- For the foreign key in deliveries, which ties each delivery to its stop's driver.
  CONSTRAINT uq_stops_id_driver UNIQUE (id, driver_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_stops_driver_date' AND object_id = OBJECT_ID(N'dbo.stops'))
  CREATE INDEX ix_stops_driver_date ON dbo.stops (driver_id, route_date, sequence);
GO

-- One row per delivery attempt. A failed attempt can be followed by another attempt.
-- IDs and recorded_at come from the server.
IF OBJECT_ID(N'dbo.deliveries', N'U') IS NULL
CREATE TABLE dbo.deliveries (
  id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT pk_deliveries PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
  stop_id      UNIQUEIDENTIFIER NOT NULL,
  driver_id    NVARCHAR(64)     NOT NULL,
  outcome      NVARCHAR(16)     NOT NULL CONSTRAINT ck_deliveries_outcome CHECK (outcome IN ('delivered', 'failed')),
  note         NVARCHAR(500)    NULL,
  recorded_at  DATETIME2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT fk_deliveries_stop FOREIGN KEY (stop_id, driver_id)
    REFERENCES dbo.stops (id, driver_id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_deliveries_stop' AND object_id = OBJECT_ID(N'dbo.deliveries'))
  CREATE INDEX ix_deliveries_stop ON dbo.deliveries (stop_id, recorded_at);
GO
