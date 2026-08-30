/* =============================================================================
   SslExpireNotify - DUMMY DATA FOR A TEST DATABASE ONLY.

   ##########################################################################
   #  DO NOT RUN THIS ON PRODUCTION.                                        #
   #  It DELETEs from CUSTOMER, KSC_USERS and SSL_Certificate, which are    #
   #  tables owned by other KSC systems and hold real data.                 #
   ##########################################################################

   To run it, set @ConfirmTestDatabase to 1 below. The script refuses to do
   anything until you do.

   The rows are dated relative to today so every alert level is exercised:
     * NOTICE   (~25 days left)
     * WARNING  (~12 days left)
     * URGENT   (~4 days left)
     * EXPIRED  (2 certificates for the same sales owner -> grouped digest)
     * CONTRACT_RENEWAL (SSLExpiredDate + 199 >= OrderEndDate)
     * OrderEndDate NULL (falls back to CERT_RENEWAL + a warning)
     * a broken sales email (alert is created, no mail is sent)
   ============================================================================= */

SET NOCOUNT ON;
GO

DECLARE @ConfirmTestDatabase BIT = 0;   -- <<< set to 1 to allow this script to run

IF @ConfirmTestDatabase = 0
BEGIN
    RAISERROR(N'seed.sql refused to run: set @ConfirmTestDatabase = 1 first, and only ever on a TEST database.', 16, 1);
    RETURN;
END

DECLARE @Today DATE = CAST(GETDATE() AS DATE);

/* Child rows first so the foreign keys hold. */
DELETE FROM dbo.EmailLog;
DELETE FROM dbo.CertificateAlert;
DELETE FROM dbo.JobRunHistory;
DELETE FROM dbo.SSL_Certificate;
DELETE FROM dbo.CUSTOMER;
DELETE FROM dbo.KSC_USERS;

/* ---------------------------------------------------------------- sales users */
INSERT INTO dbo.KSC_USERS (USERID, USERNAME, [PASSWORD], FIRST_NAME, LAST_NAME, EMAIL, STATUS, LASTUPDATE)
VALUES
    (1001, N'somchai',  N'x', N'สมชาย',  N'ใจดี',      N'somchai.j@ksc.net',  1, GETDATE()),
    (1002, N'wanida',   N'x', N'วนิดา',  N'สุขใจ',     N'wanida.s@ksc.net',   1, GETDATE()),
    (1003, N'nopadol',  N'x', N'นพดล',   N'ตั้งมั่น',   N'not-an-email',       1, GETDATE());  -- malformed on purpose

/* ------------------------------------------------------------------ customers */
INSERT INTO dbo.CUSTOMER (CUSTOMERID, COMPANYNAME, DISPLAYNAME, CUSTOMERSTATUS, REGISTERDATE, LASTUPDATE)
VALUES
    (5001, N'Alpha Trading Co., Ltd.',  N'Alpha Trading',  1, GETDATE(), GETDATE()),
    (5002, N'Beta Logistics Co., Ltd.', N'Beta Logistics', 1, GETDATE(), GETDATE()),
    (5003, N'Gamma Health Co., Ltd.',   NULL,              1, GETDATE(), GETDATE()),  -- DISPLAYNAME null -> COMPANYNAME
    (5004, N'Delta Media Co., Ltd.',    N'Delta Media',    1, GETDATE(), GETDATE());

/* --------------------------------------------------------------- certificates */
INSERT INTO dbo.SSL_Certificate
    (SSL_Cert_ID, CustomerId, CommonName, DomainName, OrderStartDate, OrderEndDate,
     SSLStartDate, SSLExpiredDate, EmailAlert, SalesID, SSLStatus, CreatedDate)
VALUES
    -- NOTICE: 25 days left, contract runs well past the next certificate cycle.
    (1, 5001, N'www.alpha.co.th', N'www.alpha.co.th',
     DATEADD(YEAR, -1, @Today), DATEADD(YEAR,  2, @Today),
     DATEADD(YEAR, -1, @Today), DATEADD(DAY,  25, @Today), N'it@alpha.co.th', 1001, 1, GETDATE()),

    -- WARNING: 12 days left.
    (2, 5002, N'shop.beta.co.th', N'shop.beta.co.th',
     DATEADD(YEAR, -1, @Today), DATEADD(YEAR,  2, @Today),
     DATEADD(YEAR, -1, @Today), DATEADD(DAY,  12, @Today), N'admin@beta.co.th', 1001, 1, GETDATE()),

    -- URGENT: 4 days left.
    (3, 5003, N'portal.gamma.co.th', N'portal.gamma.co.th',
     DATEADD(YEAR, -1, @Today), DATEADD(YEAR,  2, @Today),
     DATEADD(YEAR, -1, @Today), DATEADD(DAY,   4, @Today), N'sysadmin@gamma.co.th', 1002, 1, GETDATE()),

    -- EXPIRED #1 and #2 share sales 1001 -> one grouped digest with two rows.
    (4, 5001, N'api.alpha.co.th', N'api.alpha.co.th',
     DATEADD(YEAR, -2, @Today), DATEADD(YEAR,  1, @Today),
     DATEADD(YEAR, -2, @Today), DATEADD(DAY,  -3, @Today), N'it@alpha.co.th', 1001, 1, GETDATE()),

    (5, 5002, N'mail.beta.co.th', N'mail.beta.co.th',
     DATEADD(YEAR, -2, @Today), DATEADD(YEAR,  1, @Today),
     DATEADD(YEAR, -2, @Today), DATEADD(DAY, -10, @Today), N'admin@beta.co.th', 1001, 1, GETDATE()),

    -- CONTRACT_RENEWAL: SSLExpiredDate + 199 days lands on/after OrderEndDate.
    (6, 5004, N'www.delta.co.th', N'www.delta.co.th',
     DATEADD(YEAR, -1, @Today), DATEADD(DAY, 40, @Today),
     DATEADD(YEAR, -1, @Today), DATEADD(DAY,  20, @Today), N'webmaster@delta.co.th', 1002, 1, GETDATE()),

    -- OrderEndDate unknown -> CERT_RENEWAL plus a warning in the log.
    (7, 5004, N'cdn.delta.co.th', N'cdn.delta.co.th',
     DATEADD(YEAR, -1, @Today), NULL,
     DATEADD(YEAR, -1, @Today), DATEADD(DAY,   6, @Today), N'webmaster@delta.co.th', 1002, 1, GETDATE()),

    -- Sales email is malformed: the alert is created, no mail goes out.
    (8, 5003, N'vpn.gamma.co.th', N'vpn.gamma.co.th',
     DATEADD(YEAR, -1, @Today), DATEADD(YEAR,  2, @Today),
     DATEADD(YEAR, -1, @Today), DATEADD(DAY,   2, @Today), N'sysadmin@gamma.co.th', 1003, 1, GETDATE()),

    -- Inactive certificate: ignored while ActiveSslStatusValues is [1].
    (9, 5001, N'old.alpha.co.th', N'old.alpha.co.th',
     DATEADD(YEAR, -3, @Today), DATEADD(YEAR, -1, @Today),
     DATEADD(YEAR, -3, @Today), DATEADD(DAY,  -5, @Today), N'it@alpha.co.th', 1001, 3, GETDATE()),

    -- Far from expiry: matches no alert level at all.
    (10, 5002, N'intranet.beta.co.th', N'intranet.beta.co.th',
     DATEADD(MONTH, -2, @Today), DATEADD(YEAR, 3, @Today),
     DATEADD(MONTH, -2, @Today), DATEADD(DAY, 300, @Today), N'admin@beta.co.th', 1002, 1, GETDATE());

PRINT N'Test data seeded. 10 certificates, 4 customers, 3 sales users.';
GO
