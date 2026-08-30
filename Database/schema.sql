/* =============================================================================
   SslExpireNotify - schema for the tables this service owns.
   Safe to run repeatedly (every statement is guarded).

   Scope:
     * Creates CertificateAlert, EmailLog and JobRunHistory.
     * Adds a PRIMARY KEY to the existing SSL_Certificate.SSL_Cert_ID when it has
       none, because CertificateAlert needs it as a foreign key target.

   The service is READ ONLY on SSL_Certificate, CUSTOMER and KSC_USERS.
   ============================================================================= */

SET NOCOUNT ON;
GO

/* -----------------------------------------------------------------------------
   0) SSL_Certificate must have a primary key so CertificateAlert can reference it.
   -------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.SSL_Certificate', N'U') IS NULL
BEGIN
    RAISERROR(N'Table dbo.SSL_Certificate was not found. Run this script against the KSC database that holds it.', 16, 1);
END
GO

IF OBJECT_ID(N'dbo.SSL_Certificate', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints kc
        WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SSL_Certificate')
          AND kc.type = 'PK')
BEGIN
    PRINT N'Adding PK_SSL_Certificate on dbo.SSL_Certificate (SSL_Cert_ID)...';

    -- A primary key column must be NOT NULL.
    IF EXISTS (
        SELECT 1
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'dbo.SSL_Certificate')
          AND c.name = N'SSL_Cert_ID'
          AND c.is_nullable = 1)
    BEGIN
        ALTER TABLE dbo.SSL_Certificate ALTER COLUMN SSL_Cert_ID INT NOT NULL;
    END

    ALTER TABLE dbo.SSL_Certificate
        ADD CONSTRAINT PK_SSL_Certificate PRIMARY KEY (SSL_Cert_ID);
END
ELSE
BEGIN
    PRINT N'dbo.SSL_Certificate already has a primary key - nothing to do.';
END
GO

/* -----------------------------------------------------------------------------
   1) CertificateAlert - one row per (certificate, level, expiry snapshot).
   -------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.CertificateAlert', N'U') IS NULL
BEGIN
    PRINT N'Creating dbo.CertificateAlert...';

    CREATE TABLE dbo.CertificateAlert (
        AlertId             BIGINT IDENTITY(1,1) PRIMARY KEY,
        CertificateId       INT NOT NULL
            CONSTRAINT FK_CertificateAlert_SSL_Certificate
            REFERENCES dbo.SSL_Certificate(SSL_Cert_ID),
        AlertLevel          NVARCHAR(20) NOT NULL,
        NotificationType    NVARCHAR(20) NOT NULL
            CONSTRAINT DF_CertificateAlert_NotificationType DEFAULT 'CERT_RENEWAL',
        ExpireDateSnapshot  DATE NOT NULL,
        DaysRemaining       INT NOT NULL,
        AlertStatus         NVARCHAR(20) NOT NULL
            CONSTRAINT DF_CertificateAlert_AlertStatus DEFAULT 'Pending',
        AckToken            UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_CertificateAlert_AckToken DEFAULT NEWID(),
        AckTokenExpireAt    DATETIME2 NULL,
        AcknowledgedAt      DATETIME2 NULL,
        AcknowledgedBy      NVARCHAR(320) NULL,
        ResolvedAt          DATETIME2 NULL,
        NewExpireDate       DATE NULL,
        LastNotifiedAt      DATETIME2 NULL,
        NotifyCount         INT NOT NULL
            CONSTRAINT DF_CertificateAlert_NotifyCount DEFAULT 1,
        CreatedAt           DATETIME2 NOT NULL
            CONSTRAINT DF_CertificateAlert_CreatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT CK_AlertStatus CHECK (AlertStatus IN ('Pending','Noted','Acknowledged','Resolved','Superseded'))
    );
END
ELSE
BEGIN
    PRINT N'dbo.CertificateAlert already exists - skipping.';
END
GO

IF OBJECT_ID(N'dbo.CertificateAlert', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CertificateAlert_Cycle' AND object_id = OBJECT_ID(N'dbo.CertificateAlert'))
BEGIN
    CREATE UNIQUE INDEX UQ_CertificateAlert_Cycle
        ON dbo.CertificateAlert (CertificateId, AlertLevel, ExpireDateSnapshot);
END
GO

IF OBJECT_ID(N'dbo.CertificateAlert', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CertificateAlert_AckToken' AND object_id = OBJECT_ID(N'dbo.CertificateAlert'))
BEGIN
    CREATE UNIQUE INDEX UQ_CertificateAlert_AckToken
        ON dbo.CertificateAlert (AckToken);
END
GO

IF OBJECT_ID(N'dbo.CertificateAlert', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CertificateAlert_Status' AND object_id = OBJECT_ID(N'dbo.CertificateAlert'))
BEGIN
    CREATE INDEX IX_CertificateAlert_Status
        ON dbo.CertificateAlert (AlertStatus) INCLUDE (CertificateId, AlertLevel);
END
GO

/* -----------------------------------------------------------------------------
   2) EmailLog - one row per alert per sent (or attempted) email.
   -------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.EmailLog', N'U') IS NULL
BEGIN
    PRINT N'Creating dbo.EmailLog...';

    CREATE TABLE dbo.EmailLog (
        EmailLogId      BIGINT IDENTITY(1,1) PRIMARY KEY,
        AlertId         BIGINT NOT NULL REFERENCES dbo.CertificateAlert(AlertId),
        RecipientEmail  NVARCHAR(320) NULL,
        RecipientType   NVARCHAR(10)  NOT NULL
            CONSTRAINT DF_EmailLog_RecipientType DEFAULT 'To',
        Subject         NVARCHAR(500) NULL,
        SendStatus      NVARCHAR(20)  NOT NULL,
        Channel         NVARCHAR(10)  NOT NULL
            CONSTRAINT DF_EmailLog_Channel DEFAULT 'MailApi',
        ErrorMessage    NVARCHAR(MAX) NULL,
        RetryCount      INT NOT NULL
            CONSTRAINT DF_EmailLog_RetryCount DEFAULT 0,
        SentAt          DATETIME2 NOT NULL
            CONSTRAINT DF_EmailLog_SentAt DEFAULT SYSDATETIME()
    );
END
ELSE
BEGIN
    PRINT N'dbo.EmailLog already exists - skipping.';
END
GO

IF OBJECT_ID(N'dbo.EmailLog', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmailLog_Alert' AND object_id = OBJECT_ID(N'dbo.EmailLog'))
BEGIN
    CREATE INDEX IX_EmailLog_Alert ON dbo.EmailLog (AlertId);
END
GO

/* -----------------------------------------------------------------------------
   3) JobRunHistory - proof that the job actually ran (dead-man's-switch data).
   -------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.JobRunHistory', N'U') IS NULL
BEGIN
    PRINT N'Creating dbo.JobRunHistory...';

    CREATE TABLE dbo.JobRunHistory (
        RunId                 UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
            CONSTRAINT DF_JobRunHistory_RunId DEFAULT NEWID(),
        StartedAt             DATETIME2 NOT NULL,
        FinishedAt            DATETIME2 NULL,
        Status                NVARCHAR(20) NOT NULL
            CONSTRAINT DF_JobRunHistory_Status DEFAULT 'Running',
        CertificatesScanned   INT NULL,
        AlertsCreated         INT NULL,
        EmailsSent            INT NULL,
        EmailsFailed          INT NULL,
        EmailsSentViaFallback INT NULL,
        ErrorSummary          NVARCHAR(MAX) NULL,
        IsDryRun              BIT NOT NULL
            CONSTRAINT DF_JobRunHistory_IsDryRun DEFAULT 0
    );
END
ELSE
BEGIN
    PRINT N'dbo.JobRunHistory already exists - skipping.';
END
GO

IF OBJECT_ID(N'dbo.JobRunHistory', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobRunHistory_StartedAt' AND object_id = OBJECT_ID(N'dbo.JobRunHistory'))
BEGIN
    CREATE INDEX IX_JobRunHistory_StartedAt ON dbo.JobRunHistory (StartedAt DESC);
END
GO

PRINT N'SslExpireNotify schema is up to date.';
GO
