/* =============================================================================
   SslExpireNotify - schema for the tables this service owns.
   Safe to run repeatedly (every statement is guarded).

   Scope:
     * Creates CertificateAlert, EmailLog and JobRunHistory.

   Prerequisite: dbo.SSL_Certificate.SSL_Cert_ID must already have a PRIMARY KEY
   (CertificateAlert references it as a foreign key). Run
   Database/add-ssl-certificate-primary-key.sql first - it is a separate script
   so the team that owns SSL_Certificate can review/approve that change on its
   own, without this script touching their table.

   The service is READ ONLY on SSL_Certificate, CUSTOMER and KSC_USERS.
   ============================================================================= */

SET NOCOUNT ON;
GO

-- sqlcmd scripting directive: stop the whole script on the first error from here on,
-- so the RAISERROR guards below actually halt execution instead of just printing and
-- falling through to statements (like the CREATE TABLE below) that depend on them.
:on error exit
GO

/* -----------------------------------------------------------------------------
   0) Guard: SSL_Certificate must already have a primary key on SSL_Cert_ID.
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
        JOIN sys.index_columns ic
            ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
        JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SSL_Certificate')
          AND kc.type = 'PK'
          AND c.name = N'SSL_Cert_ID')
BEGIN
    RAISERROR(N'dbo.SSL_Certificate has no PRIMARY KEY on SSL_Cert_ID. Run Database/add-ssl-certificate-primary-key.sql first.', 16, 1);
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
