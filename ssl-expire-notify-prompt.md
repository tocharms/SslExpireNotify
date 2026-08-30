# Prompt: สร้างระบบแจ้งเตือนวันหมดอายุ SSL Certificate (ssl-expire-notify)

สร้างโปรเจกต์ **.NET 10 Worker Service** (LTS เวอร์ชันล่าสุดที่เสถียร) ภาษา C# ชื่อ `SslExpireNotify`
ทำงานเป็น **Windows Service แบบ scheduled task** รัน**ทุกวัน เวลา 00:30** ตามเวลาเครื่อง (Asia/Bangkok)
เพื่อส่ง email แจ้งเตือนลูกค้าก่อน SSL Certificate หมดอายุ

---

## 1. Tech Stack (บังคับใช้)

| ส่วน | เทคโนโลยี |
|---|---|
| Runtime | .NET 10 (LTS), C# ล่าสุด |
| Project type | Worker Service + `Microsoft.Extensions.Hosting.WindowsServices` (`UseWindowsService()`) |
| Scheduler | **Quartz.NET** — cron `0 30 0 * * ?` (**ทุกวัน 00:30**) ผูก `TimeZoneInfo` ตรงจาก `Job:TimeZoneId` ไม่พึ่ง timezone ของเครื่อง OS และต้องอ่าน cron จาก appsettings.json ได้ |
| Database | **SQL Server** — เข้าถึงด้วย **Dapper** + `Microsoft.Data.SqlClient` |
| Logging | **Serilog** — config ทั้งหมดอ่านจาก appsettings.json (Console + rolling file), enrich ด้วย `RunId` ทุกบรรทัดต่อรอบ job |
| Resilience | **Polly** — retry ทั้ง Mail API (3 ครั้ง, exponential backoff) และ DB transient fault (3 ครั้ง) + Circuit Breaker ครอบ Mail API ทั้งรอบ job |
| Concurrency | `[DisallowConcurrentExecution]` (ระดับ process) + `sp_getapplock` (ระดับ DB กันหลาย instance) |
| Email | **KSC Mail API** (หลัก) ผ่าน `HttpClient` — พร้อม **SMTP fallback** (`MailKit`) เมื่อ Mail API ล่ม; เลือกช่องทางได้ผ่าน `MailApi:PreferredChannel` |

ห้าม hardcode ค่า config ใด ๆ ใน code — connection string, Mail API URL, cron, log ต้องแก้ได้ที่ `appsettings.json` ทั้งหมด

---

## 2. โครงสร้าง appsettings.json (ต้องได้แบบนี้)

```json
{
  "ConnectionStrings": {
    "SslNotifyDb": "Server=.;Database=SslExpireNotify;User Id=app_user;Password=***;TrustServerCertificate=True;"
  },
  "Job": {
    "CronSchedule": "0 30 0 * * ?",
    "TimeZoneId": "SE Asia Standard Time",
    "MisfirePolicy": "FireOnceNow",
    "RunOnStartup": false,
    "DryRun": false,
    "JobRunHistoryRetentionDays": 90,
    "ActiveSslStatusValues": [ 1 ],
    "ContractThresholdDays": 199,
    "AlertLevels": [
      { "Level": "NOTICE",  "Days": 30, "Severity": 1, "RepeatEveryDays": 7 },
      { "Level": "WARNING", "Days": 15, "Severity": 2, "RepeatEveryDays": 7 },
      { "Level": "URGENT",  "Days": 7,  "Severity": 3, "RepeatEveryDays": 1 },
      { "Level": "EXPIRED", "Days": 0,  "Severity": 4, "RepeatEveryDays": 1 }
    ]
  },
  "MailApi": {
    "Url": "https://203.155.1.17/ksctracking_mailapi/api/email/send",
    "From": "noreplay@ksc.net",
    "FromDisplayName": "ksc mail alert",
    "TimeoutSeconds": 30,
    "AllowInvalidCertificate": false,
    "CircuitBreakerFailureThreshold": 5,
    "CircuitBreakerBreakSeconds": 300,
    "PreferredChannel": "Auto"
  },
  "Recipients": {
    "Cc": "",
    "SendToCustomer": false
  },
  "SmtpFallback": {
    "Enabled": true,
    "Host": "smtp.ksc.net",
    "Port": 587,
    "UseStartTls": true,
    "Username": "",
    "Password": "",
    "SenderEmail": "noreplay@ksc.net",
    "SenderName": "ksc mail alert",
    "TimeoutSeconds": 30
  },
  "AckBaseUrl": "https://your-app/ack",
  "EmailTemplates": {
    "TemplateFiles": {
      "NOTICE":  "Templates/ssl-expiry-notice.html",
      "WARNING": "Templates/ssl-expiry-warning.html",
      "URGENT":  "Templates/ssl-expiry-urgent.html",
      "EXPIRED": "Templates/ssl-expiry-notice-expired.html"
    },
    "ContractTemplateFile": "Templates/ssl-contact-notice-expired.html",
    "CustomerTemplateFiles": {
      "NOTICE":  "Templates/ssl-expiry-notice.html",
      "WARNING": "Templates/ssl-expiry-warning.html",
      "URGENT":  "Templates/ssl-expiry-urgent.html",
      "EXPIRED": "Templates/ssl-expiry-notice-expired-customer.html"
    },
    "Subjects": {
      "NOTICE":  "[แจ้งเตือน] ใบรับรอง SSL Certificate {domain} จะหมดอายุใน {days} วัน",
      "WARNING": "[สำคัญ] ใบรับรอง SSL Certificate {domain} จะหมดอายุใน {days} วัน",
      "URGENT":  "[ด่วน] ใบรับรอง SSL Certificate {domain} จะหมดอายุใน {days} วัน",
      "EXPIRED_GROUP": "[หมดอายุแล้ว] ใบรับรอง SSL Certificate ในความดูแลของท่าน {certCount} รายการ — กรุณาดำเนินการ",
      "CONTRACT": "[แจ้งเตือนต่อสัญญา] สัญญาบริการผลิตภัณฑ์ SSL Certificate {domain} — ใบรับรองจะหมดอายุใน {days} วัน",
      "CONTRACT_EXPIRED": "[แจ้งเตือนต่อสัญญา] SSL Certificate {domain} หมดอายุแล้ว {daysOverdue} วัน — กรุณาต่อสัญญาผลิตภัณฑ์",
      "CONTRACT_EXPIRED_REPEAT": "[แจ้งเตือนต่อสัญญาครั้งที่ {notifyCount}] SSL Certificate {domain} หมดอายุแล้ว {daysOverdue} วัน",
      "CUSTOMER_EXPIRED": "[หมดอายุแล้ว] ใบรับรอง SSL Certificate {domain} หมดอายุเมื่อ {expireDate}",
      "CUSTOMER_EXPIRED_REPEAT": "[แจ้งเตือนครั้งที่ {notifyCount}] ใบรับรอง SSL Certificate {domain} หมดอายุแล้ว {daysOverdue} วัน"
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": { "Microsoft": "Warning", "Quartz": "Warning" }
    },
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "logs/ssl-notify-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30
        }
      }
    ]
  }
}
```

- `Job:RunOnStartup = true` → รัน job ทันที 1 ครั้งตอน start (ไว้ใช้ทดสอบ) นอกเหนือจาก cron ปกติ
- `Job:TimeZoneId` → **ต้องผูก Quartz trigger กับ timezone นี้ตรงๆ** (`TimeZoneInfo.FindSystemTimeZoneById(...)`) ห้ามพึ่ง timezone ของเครื่อง OS เฉยๆ เพราะถ้าเครื่อง production ตั้งผิดเป็น UTC จะรันผิดเวลาโดยไม่มีใครรู้
- `Job:MisfirePolicy` → นโยบายเมื่อเครื่องไม่ได้ทำงานตอนถึงเวลา (เช่น ปิดซ่อมบำรุง 00:30 พอดี): `"FireOnceNow"` = รันทันทีที่เครื่องกลับมาออนไลน์, `"DoNothing"` = ข้ามรอบนั้นไปเลย รอรอบถัดไป
- `Job:DryRun = true` → รัน logic ทั้งหมด (scan, จับระดับ, จัดกลุ่ม, render template) แต่**ไม่เรียก Mail API จริง** แค่ log ว่าจะส่งอะไรถึงใคร ใช้ทดสอบการเปลี่ยนแปลง config/template บน production ได้อย่างปลอดภัยก่อนเปิดใช้จริง (แยกจาก `RunOnStartup` ซึ่งควบคุมแค่จังหวะเวลา)
- `Job:JobRunHistoryRetentionDays` → จำนวนวันที่เก็บประวัติการรันใน `JobRunHistory` (ดูข้อ 7.6) ก่อน purge ทิ้ง
- `Job:AlertLevels` → **นิยามระดับแจ้งเตือนทั้งหมดอยู่ที่นี่ที่เดียว** (ไม่ใช้ตารางในฐานข้อมูล): `Days` = เกณฑ์วันคงเหลือ, `Severity` = ลำดับความรุนแรง (มากกว่า = รุนแรงกว่า), `RepeatEveryDays` = ความถี่ส่งซ้ำ (7 = สัปดาห์ละครั้ง, 1 = ทุกวัน) — เพิ่ม/ลบ/แก้ระดับได้โดยไม่ต้อง build ใหม่
- Serilog ต้องใช้ `ReadFrom.Configuration()` เท่านั้น เพื่อให้ปรับ level/ปลายทาง log ได้จากไฟล์ config โดยไม่แก้ code
- `EmailTemplates:TemplateFiles` → map AlertLevel ไปยังไฟล์ HTML template (path แก้ได้จาก config)
- `EmailTemplates:ContractTemplateFile` → template เดียวสำหรับการแจ้งเตือน**ต่อสัญญาผลิตภัณฑ์** ใช้กับทุกระดับ (ดู Notification Type ในข้อ 4)
- `EmailTemplates:CustomerTemplateFiles` → template ของ**เมลถึงลูกค้า** (ใช้เมื่อ `Recipients:SendToCustomer = true`) — NOTICE/WARNING/URGENT ใช้ไฟล์ร่วมกับเมล Sales ได้เพราะเนื้อความเขียนพูดกับลูกค้าอยู่แล้ว ส่วน EXPIRED ต้องใช้ไฟล์แยก (`-expired-customer`) เพราะไฟล์ EXPIRED ของ Sales เป็นตาราง list พูดกับ Sales
- `EmailTemplates:Subjects` → หัวข้อเมลต่อ level แก้ได้จาก config; `EXPIRED_GROUP` ใช้กับเมลกลุ่ม EXPIRED ต่อ Sales; กลุ่ม `CONTRACT*` ใช้กับการแจ้งเตือนต่อสัญญาผลิตภัณฑ์
- `Job:ContractThresholdDays` (default 199) → เกณฑ์ตัดสินว่าเป็นการแจ้งเตือนต่อสัญญาผลิตภัณฑ์หรือไม่ (ดูข้อ 4)

---

## 3. Database Schema (SQL Server)

### 3.1 ตารางที่**มีอยู่แล้ว**ในระบบ (ข้อมูลเป็น read-only)

ระบบนี้อ่านข้อมูลจากตารางเดิมของ KSC 3 ตาราง — แอป**อ่านข้อมูลอย่างเดียว (read-only)** ห้าม INSERT/UPDATE/DELETE
ยกเว้นเรื่องเดียว: `schema.sql` ต้องตรวจสอบและ**เพิ่ม PRIMARY KEY ให้ `SSL_Certificate.SSL_Cert_ID` ถ้ายังไม่มี** (ตรวจจาก `sys.key_constraints` ก่อน แล้วค่อย `ALTER TABLE ... ADD CONSTRAINT PK_SSL_Certificate PRIMARY KEY (SSL_Cert_ID)`) เพื่อรองรับ FK จาก `CertificateAlert`:

```sql
-- ลูกค้า (มีอยู่แล้ว)
CREATE TABLE [CUSTOMER](
    [CUSTOMERID] [numeric](10, 0) NOT NULL,
    [PIPELINE_CUSTID] [numeric](10, 0) NULL,
    [W4ID] [nvarchar](20) NULL,
    [BRANCHID] [numeric](2, 0) NULL,
    [COMPANYNAME] [nvarchar](100) NULL,
    [DISPLAYNAME] [nvarchar](100) NULL,      -- ใช้เป็นชื่อลูกค้าในเมล ({customerName})
    [CUSTOMERTYPE] [numeric](2, 0) NULL,
    [CUSTOMERINDUSTRY] [numeric](2, 0) NULL,
    [CUSTOMERCLASS] [numeric](2, 0) NULL,
    [CUSTOMERSTATUS] [numeric](2, 0) NULL,
    [TAXTYPE] [numeric](2, 0) NULL,
    [TAXNUMBER] [nvarchar](20) NULL,
    [ACCTMANAGER] [numeric](4, 0) NULL,
    [USERID] [numeric](4, 0) NULL,
    [CUSTOMER_CONSENT] [nchar](1) NULL,
    [ACCEPT_CONSENT] [nchar](1) NULL,
    [REGISTERDATE] [datetime2](7) NULL,
    [LASTUPDATE] [datetime2](7) NULL
);

-- ผู้ใช้ภายใน / Sales (มีอยู่แล้ว) — อีเมล Sales เป็นผู้รับหลัก (To)
CREATE TABLE [KSC_USERS](
    [USERID] [numeric](4, 0) NOT NULL,
    [STAFFCODE] [nvarchar](50) NULL,
    [OLD_STAFFCODE] [nvarchar](50) NULL,
    [USERNAME] [nvarchar](30) NOT NULL,
    [PASSWORD] [nvarchar](40) NOT NULL,
    [FIRST_NAME] [nvarchar](50) NULL,
    [LAST_NAME] [nvarchar](50) NULL,
    [HEADID] [numeric](4, 0) NULL,
    [DEPARTMENTID] [numeric](2, 0) NULL,
    [SECTIONID] [numeric](2, 0) NULL,
    [POSITIONID] [numeric](4, 0) NULL,
    [GROUPID] [numeric](3, 0) NULL,
    [GROUP_ASSIGID] [nvarchar](5) NULL,
    [EMAIL] [nvarchar](50) NULL,             -- อีเมล Sales (1 คนมีอีเมลเดียว)
    [PHONE] [nvarchar](50) NULL,
    [MOBILE] [nvarchar](50) NULL,
    [STATUS] [numeric](2, 0) NULL,
    [HEAD_FLAG] [smallint] NULL,
    [LOCKDATE] [datetime] NULL,
    [PASSWORDUPDATE] [datetime] NULL,
    [LASTUPDATE] [datetime] NOT NULL,
    [attemptcount] [int] NULL
);

-- SSL Certificate ที่ต้อง monitor (มีอยู่แล้ว)
CREATE TABLE [SSL_Certificate](
    [SSL_Cert_ID] [int] NOT NULL CONSTRAINT PK_SSL_Certificate PRIMARY KEY,
    [CustomerId] [numeric](10, 0) NULL,      -- → CUSTOMER.CUSTOMERID
    [WebServerType] [numeric](4, 0) NULL,
    [CommonName] [nvarchar](255) NULL,
    [DomainName] [nvarchar](255) NULL,
    [OrderType] [numeric](4, 0) NULL,
    [OrderStartDate] [datetime] NULL,        -- Contract Period เริ่ม
    [OrderEndDate] [datetime] NULL,          -- Contract Period สิ้นสุด
    [SSLStartDate] [datetime] NULL,
    [SSLExpiredDate] [datetime] NULL,        -- ★ ใช้คำนวณวันหมดอายุเพื่อแจ้งเตือน
    [SSLActualExpiredDate] [datetime] NULL,
    [CalculateExpiryDay] [int] NULL,
    [BAN] [nvarchar](50) NULL,
    [AssetNum] [nvarchar](50) NULL,
    [IdCard] [nvarchar](50) NULL,
    [TaxId] [nvarchar](50) NULL,
    [EmailAlert] [nvarchar](50) NULL,        -- อีเมลลูกค้า (To ของเมลลูกค้า เมื่อเปิด Recipients:SendToCustomer)
    [ServerOwner] [numeric](4, 0) NULL,
    [SalesID] [numeric](8, 0) NULL,          -- → KSC_USERS.USERID (ผู้รับ To)
    [SSLStatus] [numeric](4, 0) NULL,        -- 1=Active, 2=Pending, 3=Inactive
    [CreatedDate] [datetime] NOT NULL,
    [UpdatedDate] [datetime] NULL
);
```

**Relations:**
- `SSL_Certificate.SSL_Cert_ID = CertificateAlert.CertificateId` (FK จริง — ดูข้อ 3.2)
- `SSL_Certificate.CustomerId = CUSTOMER.CUSTOMERID` (ไม่มี FK — join ด้วย key เอง)
- `SSL_Certificate.SalesID = KSC_USERS.USERID` (ไม่มี FK — join ด้วย key เอง)

### 3.2 ตารางใหม่ที่ระบบนี้ต้องสร้าง

สร้างไฟล์ `Database/schema.sql` สร้าง**เฉพาะ**ตารางใหม่ต่อไปนี้ (รันซ้ำได้ ใช้ `IF NOT EXISTS`):

```sql
-- 1) Alert หลัก
CREATE TABLE CertificateAlert (
    AlertId             BIGINT IDENTITY(1,1) PRIMARY KEY,
    CertificateId       INT NOT NULL
        CONSTRAINT FK_CertificateAlert_SSL_Certificate
        REFERENCES [SSL_Certificate](SSL_Cert_ID),  -- relation: SSL_Certificate.SSL_Cert_ID = CertificateAlert.CertificateId
    AlertLevel          NVARCHAR(20) NOT NULL,
    NotificationType    NVARCHAR(20) NOT NULL DEFAULT 'CERT_RENEWAL',
                        -- CERT_RENEWAL = ต่ออายุใบรับรองปกติ / CONTRACT_RENEWAL = ต่อสัญญาผลิตภัณฑ์
    ExpireDateSnapshot  DATE NOT NULL,             -- SSLExpiredDate ณ ตอนสร้าง alert (ตัดเวลาออก เก็บเฉพาะวันที่)
    DaysRemaining       INT NOT NULL,
    AlertStatus         NVARCHAR(20) NOT NULL DEFAULT 'Pending',
                        -- Pending / Noted / Acknowledged / Resolved / Superseded
    AckToken            UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    AckTokenExpireAt    DATETIME2 NULL,
    AcknowledgedAt      DATETIME2 NULL,
    AcknowledgedBy      NVARCHAR(320) NULL,
    ResolvedAt          DATETIME2 NULL,
    NewExpireDate       DATE NULL,
    LastNotifiedAt      DATETIME2 NULL,            -- ส่งเมลครั้งล่าสุดเมื่อไหร่
    NotifyCount         INT NOT NULL DEFAULT 1,    -- ส่งไปแล้วกี่ครั้ง
    CreatedAt           DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT CK_AlertStatus CHECK (AlertStatus IN ('Pending','Noted','Acknowledged','Resolved','Superseded'))
);
CREATE UNIQUE INDEX UQ_CertificateAlert_Cycle
    ON CertificateAlert (CertificateId, AlertLevel, ExpireDateSnapshot);
CREATE UNIQUE INDEX UQ_CertificateAlert_AckToken
    ON CertificateAlert (AckToken);
CREATE INDEX IX_CertificateAlert_Status
    ON CertificateAlert (AlertStatus) INCLUDE (CertificateId, AlertLevel);

-- 2) Log การส่ง email
CREATE TABLE EmailLog (
    EmailLogId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    AlertId         BIGINT NOT NULL REFERENCES CertificateAlert(AlertId),
    RecipientEmail  NVARCHAR(320) NULL,            -- NULL เมื่อไม่มีอีเมลผู้รับเลย (ดู EmailLog Failed ในกติกาผู้รับ)
    RecipientType   NVARCHAR(10)  NOT NULL DEFAULT 'To',
    Subject         NVARCHAR(500) NULL,            -- subject ที่ render แล้วจริง
    SendStatus      NVARCHAR(20)  NOT NULL,        -- Success / Failed
    Channel         NVARCHAR(10)  NOT NULL DEFAULT 'MailApi',  -- MailApi / Smtp
    ErrorMessage    NVARCHAR(MAX) NULL,
    RetryCount      INT NOT NULL DEFAULT 0,
    SentAt          DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);
CREATE INDEX IX_EmailLog_Alert ON EmailLog (AlertId);

-- 3) ประวัติการรัน job (dead-man's-switch สำหรับ monitor ว่า job ยังทำงานอยู่หรือไม่)
CREATE TABLE JobRunHistory (
    RunId               UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
    StartedAt           DATETIME2 NOT NULL,
    FinishedAt          DATETIME2 NULL,
    Status              NVARCHAR(20) NOT NULL DEFAULT 'Running',
                        -- Running / Completed / Failed
    CertificatesScanned INT NULL,
    AlertsCreated       INT NULL,
    EmailsSent          INT NULL,
    EmailsFailed         INT NULL,
    EmailsSentViaFallback INT NULL,       -- จำนวนที่ต้องสลับไปใช้ SMTP fallback
    ErrorSummary        NVARCHAR(MAX) NULL,      -- exception message ถ้า job ล้มทั้งรอบ
    IsDryRun            BIT NOT NULL DEFAULT 0
);
CREATE INDEX IX_JobRunHistory_StartedAt ON JobRunHistory (StartedAt DESC);
```

พร้อม seed data ใน `Database/seed.sql`:
- Dummy data ของ `CUSTOMER`, `KSC_USERS`, `SSL_Certificate` **สำหรับ test DB เท่านั้น** (ห้ามรันบน production — ตารางเหล่านี้เป็นของระบบอื่นและมีข้อมูลจริง)

> หมายเหตุ: **ไม่มี**ตาราง EmailTemplate — เนื้อหาเมลใช้ไฟล์ HTML template ในโฟลเดอร์ `Templates/` แทน (ดูข้อ 5)

---

## 4. Business Logic ของ Job (สำคัญที่สุด — ต้องทำตามลำดับนี้)

Job class ชื่อ `SslExpireCheckJob` (implement `IJob` ของ Quartz, ใส่ `[DisallowConcurrentExecution]`)

### STEP 1 — Auto-Resolve (ต้องรันก่อน scan เสมอ)
```sql
UPDATE a
SET a.AlertStatus   = 'Resolved',
    a.ResolvedAt    = SYSDATETIME(),
    a.NewExpireDate = CAST(c.SSLExpiredDate AS DATE)
FROM CertificateAlert a
JOIN SSL_Certificate c ON c.SSL_Cert_ID = a.CertificateId
WHERE a.AlertStatus IN ('Pending','Noted','Acknowledged')
  AND CAST(c.SSLExpiredDate AS DATE) > a.ExpireDateSnapshot;
```
Log จำนวนแถวที่ Resolved

### STEP 2 — Scan certificate
- ดึง `SSL_Certificate` ที่ `SSLExpiredDate IS NOT NULL` และ `SSLStatus` อยู่ใน `Job:ActiveSslStatusValues`
  → ค่า SSLStatus: **1=Active, 2=Pending, 3=Inactive** — **default config คือ `[ 1 ]` (เฉพาะ Active)** ปรับได้ใน appsettings.json; ถ้าตั้งเป็น array ว่าง = ไม่กรอง SSLStatus เลย
- คำนวณ `days = DATEDIFF(DAY, วันนี้, CAST(SSLExpiredDate AS DATE))` (★ ใช้ **SSLExpiredDate** เท่านั้น ไม่ใช่ SSLActualExpiredDate/OrderEndDate)
- จับคู่ AlertLevel จาก `Job:AlertLevels` ใน config โดยเลือก **ระดับที่ Severity สูงสุดที่ `days <= Days`**
  (ค่าเริ่มต้น: `days <= 0` → EXPIRED, `<= 7` → URGENT, `<= 15` → WARNING, `<= 30` → NOTICE)
- ไม่เข้าเงื่อนไขไหนเลย → ข้าม
- ระดับจะ**เลื่อนขึ้นเองตามวันที่ใกล้หมดอายุ** เช่น cert เดิมอยู่ NOTICE พอเหลือ 15 วันจะกลายเป็น WARNING โดยอัตโนมัติ

### การตัดสิน Notification Type (ทำหลังได้ AlertLevel)
เกณฑ์: เทียบ `SSLExpiredDate + ContractThresholdDays (199)` กับ `OrderEndDate`

| เงื่อนไข | NotificationType | ความหมาย | Template |
|---|---|---|---|
| `DATEADD(DAY, 199, SSLExpiredDate) < OrderEndDate` | `CERT_RENEWAL` | สัญญายังเหลืออีกนาน → แค่ต่ออายุใบรับรองตามปกติ | 4 ไฟล์ตาม AlertLevel (notice/warning/urgent/expired) |
| `DATEADD(DAY, 199, SSLExpiredDate) >= OrderEndDate` | `CONTRACT_RENEWAL` | สัญญาเหลือไม่พอออกใบรับรองรอบใหม่ → ต้อง**ต่อสัญญาผลิตภัณฑ์** | `ContractTemplateFile` ไฟล์เดียวทุกระดับ |
| `OrderEndDate IS NULL` | `CERT_RENEWAL` | ไม่ทราบวันสิ้นสุดสัญญา → ปฏิบัติแบบปกติ + log warning | 4 ไฟล์ปกติ |

- เกณฑ์ 199 วันอ่านจาก `Job:ContractThresholdDays` ห้าม hardcode
- Alert cycle, การส่งซ้ำ EXPIRED รายสัปดาห์, และ lifecycle Pending/Acknowledged/Resolved **เหมือนกันทั้งสองประเภท** ต่างกันเฉพาะ template + subject
- บันทึกค่า `NotificationType` ลง `CertificateAlert` ทุกครั้งที่สร้าง alert (เพื่อ audit และเพื่อให้การส่งซ้ำใช้ประเภทเดิมตาม alert ไม่คำนวณใหม่)

### ผู้รับเมล (ต่อ 1 certificate)
ใช้กติกาเดียวกันทั้งระบบ ทุกประเภทและทุกระดับ:
- **To** = อีเมลของ Sales: `KSC_USERS.EMAIL` โดย join `SSL_Certificate.SalesID = KSC_USERS.USERID`
- **Cc** = `Recipients:Cc` จาก appsettings.json (ค่าว่าง = ไม่ใส่ Cc)
**เมลถึงลูกค้า (เผื่ออนาคต) — ควบคุมด้วย `Recipients:SendToCustomer`** (default `false`)
- `false` → ส่งเฉพาะเมลถึง Sales ตามกติกาข้างต้น (พฤติกรรมปัจจุบัน)
- `true` → ส่ง **เมลแยกอีกฉบับถึงลูกค้า** (ไม่ใช่ใส่ลูกค้าเป็น Cc ในเมลของ Sales):
  - **To** = `SSL_Certificate.EmailAlert`
  - **Cc** = `Recipients:Cc` (ตัวเดียวกับเมลของ Sales)
  - ส่งได้เฉพาะ **cert รายใบ** — ต่อ 1 certificate = 1 ฉบับถึงลูกค้ารายนั้น
  - Template: `EmailTemplates:CustomerTemplateFiles[level]` สำหรับ CERT_RENEWAL / `ContractTemplateFile` สำหรับ CONTRACT_RENEWAL
  - Subject: `CUSTOMER_EXPIRED` / `CUSTOMER_EXPIRED_REPEAT` (เมื่อ `NotifyCount > 1`) สำหรับ EXPIRED — ระดับอื่นใช้ `Subjects[level]` ร่วมกับเมล Sales
  - **เมลกลุ่ม EXPIRED (กลุ่ม 2) ไม่ถูกแตกส่งถึงลูกค้า** เพราะเมลกลุ่มเป็นมุมมองของ Sales ไม่ใช่ของลูกค้า หากอนาคตต้องการส่งถึงลูกค้าในกรณี EXPIRED ให้ส่งแยกรายใบด้วย template สำหรับลูกค้าโดยเฉพาะ (ยังไม่ implement ในเฟสนี้)
  - `EmailAlert` เป็น NULL/ว่าง/format ผิด → ข้ามเฉพาะเมลลูกค้า, log warning, `EmailLog` Failed (ErrorMessage = "no customer email") — **เมลถึง Sales ยังส่งตามปกติ** ไม่ถือว่า alert ล้มเหลว
  - บันทึก `EmailLog` แยกแถวของเมลลูกค้า เพื่อ audit ว่าฉบับไหนส่งถึงใคร
- แม้ flag เป็น `false` ก็ให้ repository **ดึงคอลัมน์ `EmailAlert` มาด้วยเสมอ** และ map ลง model เพื่อให้เปิดใช้ได้ทันทีโดยไม่ต้องแก้ query
- **เมลถึง Sales กับเมลถึงลูกค้าเป็นอิสระต่อกันโดยสมบูรณ์** — ฉบับใดฉบับหนึ่งล้มเหลว (ไม่มีอีเมล/format ผิด/ส่งไม่สำเร็จ) ไม่กระทบการส่งอีกฉบับ แต่ละฉบับมี `EmailLog` แถวของตัวเอง
- กรณี Sales email เป็น NULL/ว่าง หรือ format ไม่ถูกต้อง → สร้าง alert ตามปกติแต่ไม่ส่งเมล, log warning พร้อม SSL_Cert_ID/DomainName, บันทึก `EmailLog` เป็น Failed (ErrorMessage = "no recipient") และคง `LastNotifiedAt = NULL` เพื่อให้รอบถัดไปลองใหม่หลังแก้ข้อมูล
- ตรวจ format อีเมลก่อนส่งเสมอ (คอลัมน์เป็น nvarchar(50) อาจมีข้อมูลสกปรก)

### การส่งเมล (Mail API เป็นหลัก + SMTP fallback)
ช่องทางหลักคือ POST JSON ไปที่ `MailApi:Url` (อ่านจาก appsettings.json ห้าม hardcode):

```json
{
  "from": "noreplay@ksc.net",
  "fromdisplayname": "ksc mail alert",
  "to": "<อีเมล To ตามกติกาผู้รับ>",
  "subject": "<subject ที่ render จาก EmailTemplates:Subjects>",
  "body": "<HTML ที่ render จากไฟล์ template แล้ว>",
  "isHtml": true,
  "cc": "<Recipients:Cc จาก config — ค่าว่างให้ส่งเป็นสตริงว่าง>"
}
```

ข้อกำหนด:
- `from` / `fromdisplayname` อ่านจาก `MailApi:From` / `MailApi:FromDisplayName` ใน config
- `Recipients:Cc` → อีเมล Cc ที่ใช้กับเมล**ทุกฉบับ**ในระบบ (เช่น หัวหน้าทีมขาย/ฝ่ายที่ต้องรับสำเนา) รองรับหลายอีเมลคั่นด้วย `,` — ค่าว่าง = ส่งโดยไม่มี Cc
- `Recipients:SendToCustomer` → เผื่ออนาคตที่ต้องส่งถึงลูกค้าโดยตรง (default `false` = ไม่ส่ง) — ใช้ผลเดียวกันทั้งช่องทาง Mail API และ SMTP fallback ดูกติกาผู้รับ
- เมลถึง Sales: `to` = อีเมล Sales, `cc` = `Recipients:Cc` — เมลถึงลูกค้า (เมื่อ `SendToCustomer = true`): `to` = `EmailAlert`, `cc` = `Recipients:Cc`
- ใช้ `HttpClient` ผ่าน `IHttpClientFactory` + timeout จาก `MailApi:TimeoutSeconds`
- `MailApi:AllowInvalidCertificate = true` → ข้ามการตรวจ TLS certificate (endpoint เป็น IP ภายใน cert อาจไม่ match) — default `false`; **ถ้าเปิดไว้ ต้อง log คำเตือนระดับ Warning ทุกครั้งที่ service start** เพื่อไม่ให้ลืมปิดตอน production
- **เกณฑ์สำเร็จ (Mail API)**: HTTP 2xx = ส่งสำเร็จ → `EmailLog.SendStatus = 'Success'`, `EmailLog.Channel = 'MailApi'`
- **ล้มเหลว (Mail API)**: timeout / 5xx → Polly retry 3 ครั้ง (exponential backoff); 4xx → ไม่ retry (ข้อมูล request ผิด — ไม่ควร fallback ไป SMTP เพราะ 4xx มักเป็นปัญหาข้อมูล เช่น อีเมลผิด format ไม่ใช่ปัญหาช่องทาง)
- ห้าม log เนื้อหา `body` ทั้งก้อนลง Serilog (ใหญ่เกินไป) — log เฉพาะ to/cc/subject/status/channel

**ช่องทางสำรอง (SMTP fallback) — ดูรายละเอียดตัดสินใจสลับช่องทางที่ข้อ 7.4:**
- เมื่อ fallback ทำงาน ให้ส่งด้วย `MailKit` ผ่านค่าใน `SmtpFallback:*` เนื้อหา (`to`/`cc`/`subject`/`body`) เหมือนกับที่จะส่งผ่าน Mail API ทุกอย่าง
- สำเร็จ → `EmailLog.SendStatus = 'Success'`, `EmailLog.Channel = 'Smtp'`
- ล้มเหลวทั้งสองช่องทาง → `EmailLog.SendStatus = 'Failed'`, `ErrorMessage` รวมข้อความจากทั้ง Mail API และ SMTP, `Channel` บันทึกเป็นช่องทางสุดท้ายที่ลอง

### STEP 3 — สร้าง/อัปเดต alert (ต่อ certificate)
key ของ alert คือ `(CertificateId, AlertLevel, ExpireDateSnapshot = CAST(SSLExpiredDate AS DATE) ปัจจุบัน)`

**3.1 ตรวจว่า cert นี้ถูกสั่งหยุดแจ้งเตือนไปแล้วหรือยัง**
ถ้าใน cycle เดียวกัน (`CertificateId` + `ExpireDateSnapshot` เดิม) มี alert ใดก็ตามที่ `AlertStatus IN ('Acknowledged','Resolved')` → **ข้าม cert นี้ทั้งหมด ไม่ส่งอะไรอีก**
(หมายเหตุ: `Noted` **ไม่**นับเป็นการหยุด — ดูเรื่องปุ่มรับทราบด้านล่าง)

**3.2 เลื่อนระดับ (supersede)**
ถ้ามี alert เดิมใน cycle เดียวกันที่ `AlertLevel` **Severity ต่ำกว่า** ระดับปัจจุบัน และอยู่ในสถานะ `Pending` หรือ `Noted` → UPDATE เป็น `AlertStatus = 'Superseded'`
(เช่น cert ที่เคยอยู่ NOTICE พอเข้า WARNING แล้ว alert NOTICE จะหยุดส่งเอง ไม่ต้องรอ ack)

**3.3 กรณี A — ยังไม่มี alert ของระดับปัจจุบัน**
→ INSERT `CertificateAlert` (Pending, AckToken ใหม่, `NotifyCount = 1`, `NotificationType`) แล้วเข้าคิวส่งเมลใน STEP 4

**3.4 กรณี B — มี alert ของระดับปัจจุบันอยู่แล้ว: ตรวจว่าถึงรอบส่งซ้ำหรือยัง**
ส่งซ้ำได้เมื่อครบทั้ง 2 ข้อ:
- `AlertStatus IN ('Pending','Noted')` — **`Noted` ยังส่งซ้ำต่อ** เพราะเป็นแค่การบันทึกว่าผู้รับเห็นแล้ว
- `LastNotifiedAt IS NULL` **หรือ** `LastNotifiedAt <= DATEADD(DAY, -RepeatEveryDays, วันนี้)` โดย `RepeatEveryDays` อ่านจาก `Job:AlertLevels` ของระดับนั้น

→ เข้าคิวส่งซ้ำใน STEP 4 ด้วย **AckToken เดิม**, `NotifyCount += 1`

**สรุปความถี่ตาม config เริ่มต้น** — NOTICE/WARNING ส่งซ้ำทุก 7 วัน, URGENT/EXPIRED ส่งทุกวัน
เนื่องจาก job รันทุกวัน การเทียบ `LastNotifiedAt` จึงเป็นตัวคุมความถี่จริง ไม่ใช่ตาราง cron

**3.5 กรณี C — นอกเหนือจากนั้น** → ข้าม (ยังไม่ถึงรอบส่งซ้ำ หรือ alert ถูก Superseded/ปิดไปแล้ว)

### ปุ่มรับทราบ — พฤติกรรมขึ้นกับ `AlertLevel` ของ token เท่านั้น (ไม่ขึ้นกับว่าใช้ template ไฟล์ไหน)
ทุกเมลมีปุ่มรับทราบชี้ไป `{ackLink}` = `{AckBaseUrl}?tokens={token1},{token2},...` (แม้มี alert เดียวก็ใช้ format list ที่มี 1 ค่า) แต่ Ack endpoint ต้องแยกพฤติกรรมตาม `AlertLevel` ของ token:

| `AlertLevel` ของ token | ผลเมื่อกดปุ่ม | สถานะที่บันทึก | ส่งซ้ำต่อไหม |
|---|---|---|---|
| NOTICE / WARNING / URGENT | บันทึกว่าผู้รับเห็นแล้ว | `Noted` (เฉพาะ alert ใบนั้น) | **ยังส่งซ้ำตามความถี่เดิม** |
| EXPIRED | หยุดแจ้งเตือน | `Acknowledged` (ทุก alert ใน cycle ของ cert ที่เกี่ยวข้อง) | **หยุดถาวร** จนกว่าจะต่ออายุ |

กติกานี้ใช้เหมือนกันทั้ง 2 ประเภท (CERT_RENEWAL และ CONTRACT_RENEWAL) — **สำคัญ**: `ssl-contact-notice-expired.html` ใช้ไฟล์เดียวกันทุกระดับ (ตามที่ระบุไว้ในข้อ Notification Type) ดังนั้นปุ่มในไฟล์นี้**ต้องเป็น placeholder แบบไดนามิก ไม่ใช่ข้อความตายตัว**:

| Placeholder | ระดับอื่น (NOTICE/WARNING/URGENT) | ระดับ EXPIRED |
|---|---|---|
| `{ackButtonLabel}` | `✓ รับทราบ กำลังดำเนินการ / Acknowledged – In Progress` | `✓ รับทราบ — หยุดการแจ้งเตือน / Acknowledge – Stop Alerts` |

(ไม่ใช้คำว่า "ทั้งหมด/All" ในกรณี EXPIRED ของไฟล์นี้ เพราะ CONTRACT_RENEWAL ส่งรายใบเสมอ ไม่ใช่เมลกลุ่มหลายรายการแบบ `ssl-expiry-notice-expired.html`)

ไฟล์ที่เหลือมีคำขึ้นต้น/ปุ่มตายตัวอยู่แล้วเพราะรู้ระดับล่วงหน้าจากชื่อไฟล์ ไม่ต้องใช้ placeholder นี้:
- `notice.html` / `warning.html` / `urgent.html` → ใช้เฉพาะระดับ NOTICE/WARNING/URGENT อยู่แล้ว → ปุ่มคงที่แบบ "กำลังดำเนินการ"
- `ssl-expiry-notice-expired.html` (Sales, เมลกลุ่ม) / `ssl-expiry-notice-expired-customer.html` (ลูกค้า, EXPIRED เท่านั้น) → ใช้เฉพาะระดับ EXPIRED อยู่แล้ว → ปุ่มคงที่แบบ "หยุดการแจ้งเตือน/AcknowledgeAll"

- ทั้งสองกรณีบันทึก `AcknowledgedAt` และ `AcknowledgedBy` เสมอ (แม้สถานะจะเป็น `Noted` ก็บันทึกสองคอลัมน์นี้เหมือนกัน ใช้ชื่อคอลัมน์เดิมทั้งคู่)
- กดซ้ำที่ระดับเดิมไม่ทำให้เกิด error — update timestamp ทับได้ (idempotent)
- alert ที่เป็น `Noted` แล้วเลื่อนระดับขึ้น จะถูก `Superseded` ตามปกติ และ alert ระดับใหม่เริ่มที่ `Pending`

### เงื่อนไขหยุดแจ้งเตือน (มี 2 ทางเท่านั้น)
1. **Auto-Resolve** — มีการขยาย `SSL_Certificate.SSLExpiredDate` ออกไป → STEP 1 ปิดให้อัตโนมัติ (`Resolved`)
2. **AcknowledgeAll** — ผู้รับกดปุ่ม "รับทราบทั้งหมด" ในเมลระดับ **EXPIRED** → alert ทั้งหมดใน cycle นั้นเป็น `Acknowledged`

นอกจาก 2 ทางนี้ ระบบจะแจ้งเตือนต่อเนื่องตามความถี่ของแต่ละระดับ ไม่หยุดเอง

### STEP 4 — จัดกลุ่มและส่งเมล (รูปแบบการส่งขึ้นกับประเภท + ระดับ)

จาก alert ที่เข้าคิวส่งในรอบนี้ แบ่งการส่งเป็น 3 กลุ่ม:

**กลุ่ม 1 — CERT_RENEWAL + NOTICE/WARNING/URGENT: ส่งรายใบ (เหมือนเดิม)**
- 1 certificate = 1 เมล ใช้ template ตามระดับ (notice / warning / urgent)
- ผู้รับ: ตามกติกาผู้รับ (To = Sales, Cc = `Recipients:Cc`)

**กลุ่ม 2 — CERT_RENEWAL + EXPIRED: จัดกลุ่มตาม Sales (แบบใหม่)**
- `GROUP BY SalesID` — Sales 1 คนที่ดูแลใบรับรองหมดอายุหลายใบ ได้รับ**เมลสรุปฉบับเดียว**ต่อรอบ
- ใช้ template `ssl-expiry-notice-expired.html` (ปรับเป็นแบบ list แล้ว): ส่วน CONTRACT DETAILS เป็นตารางรายการ รองรับหลาย record มีคอลัมน์ Customer / Domain / **Expired Date (ขวาสุด)** — C# สร้างแถวจาก row template ในไฟล์ แล้วแทนที่ `{certRows}` (ใบเดียวก็ใช้ list เดียวกัน มี 1 แถว)
- Subject ใช้ key `EXPIRED_GROUP` (มี `{certCount}`)
- ผู้รับ: ตามกติกาผู้รับ (To = Sales, Cc = `Recipients:Cc`)
- **ปุ่ม "รับทราบทั้งหมด / AcknowledgeAll"**: `{ackLink}` = `{AckBaseUrl}?tokens={token1},{token2},...`
  กดครั้งเดียว → alert ทุกใบในเมล **และ alert ระดับอื่นใน cycle เดียวกันของ cert เหล่านั้น** เปลี่ยนเป็น `Acknowledged` ทั้งหมด = หยุดแจ้งเตือน cert เหล่านั้นถาวรจนกว่าจะต่ออายุ
- อัปเดตทุก alert ในกลุ่ม: `LastNotifiedAt = ตอนนี้`, `NotifyCount += 1` (เฉพาะที่เป็นการส่งซ้ำ) และ INSERT `EmailLog` **1 แถวต่อ alert** (Subject เดียวกัน) เพื่อให้ audit ย้อนได้ว่าใบไหนถูกรวมในเมลฉบับไหน
- เคส Sales ไม่มีอีเมล: ไม่ส่งเมลกลุ่มนั้น แต่ยังสร้าง/อัปเดต alert ตามปกติ + log warning + `EmailLog` Failed (ตามกติกาผู้รับ)

**กลุ่ม 3 — CONTRACT_RENEWAL (ทุกระดับ): เหมือนเดิมไม่เปลี่ยนแปลง**
- ส่งรายใบด้วย `ssl-contact-notice-expired.html` + subject กลุ่ม `CONTRACT*` ตามที่กำหนดไว้

### การเลือก Email Template (แบบไฟล์ HTML — ไม่ใช้ database)
- **CERT_RENEWAL — NOTICE/WARNING/URGENT**: Body อ่านจาก `EmailTemplates:TemplateFiles[level]`; Subject จาก `Subjects[level]`
- **CERT_RENEWAL — EXPIRED (เมลกลุ่มต่อ Sales)**: Body จาก `TemplateFiles["EXPIRED"]` (แบบ list); Subject ใช้ `EXPIRED_GROUP` เสมอ (ไม่มี subject รายใบสำหรับ EXPIRED ฝั่ง Sales แล้ว เพราะ NotifyCount ของแต่ละใบในกลุ่มไม่เท่ากัน)
- **CONTRACT_RENEWAL**: Body อ่านจาก `EmailTemplates:ContractTemplateFile` **ไฟล์เดียวทุกระดับ**; Subject:
  - ยังไม่หมดอายุ (NOTICE/WARNING/URGENT) → `CONTRACT`
  - EXPIRED ครั้งแรก → `CONTRACT_EXPIRED`
  - EXPIRED ส่งซ้ำ (`NotifyCount > 1`) → `CONTRACT_EXPIRED_REPEAT`
- **ไฟล์ template ทั้ง 6 ไฟล์แนบมากับโปรเจกต์แล้วครบทุกไฟล์** (`notice`, `warning`, `urgent`, `-expired` ฝั่ง Sales, `-expired-customer` ฝั่งลูกค้า, `contact-notice-expired`) **ไม่ต้องสร้างหรือแต่งเนื้อหาเพิ่ม** — วางไว้ที่ `Templates/` ตาม path ใน config ข้อ 2 ได้เลย โค้ดมีหน้าที่แค่อ่านไฟล์ + replace placeholder เท่านั้น
- ไฟล์ `ssl-expiry-notice-expired.html` (เมลกลุ่มฝั่ง Sales) ใช้ `{certRows}` ที่ C# สร้างจาก row template ในไฟล์ (ดูรายละเอียด placeholder ด้านล่าง) — ไม่มี `{notifyCount}` ในไฟล์นี้เพราะแต่ละ cert ในกลุ่มมี NotifyCount ไม่เท่ากัน
- ไฟล์ `ssl-contact-notice-expired.html` ต้องใช้ placeholder `{ackButtonLabel}` แบบไดนามิก (ดูตารางปุ่มรับทราบด้านบน) เพราะไฟล์เดียวใช้ได้ทุกระดับรวม EXPIRED
- ไฟล์ template ทั้งหมดต้อง copy ไป output directory ตอน build (`CopyToOutputDirectory=PreserveNewest`)
- อ่านไฟล์ครั้งเดียวแล้ว cache ใน memory ต่อรอบ job (ไม่อ่าน disk ทุก cert)

### Placeholder หลัก (ใช้ในไฟล์ notice / warning / urgent / contact-notice / expired-customer)
| Placeholder | ค่า (จาก database) |
|---|---|
| `{customerName}` | `CUSTOMER.DISPLAYNAME` (join จาก `SSL_Certificate.CustomerId`; NULL → ใช้ `COMPANYNAME`) |
| `{domain}` | `SSL_Certificate.DomainName` (NULL → ใช้ `CommonName`) |
| `{contractPeriod}` | `OrderStartDate – OrderEndDate` format "18 September 2025 – 18 September 2026" (ค่าใดค่าหนึ่ง NULL → แสดง "-") |
| `{expireDate}` | `ExpireDateSnapshot` format "18 September 2026" |
| `{expireDateThai}` | `ExpireDateSnapshot` format ไทย พ.ศ. เช่น "18 กันยายน 2569" |
| `{expireDateEn}` | `ExpireDateSnapshot` format "18 September 2026" |
| `{days}` | วันคงเหลือ คำนวณ ณ ตอนส่ง (`DATEDIFF(DAY, วันนี้, ExpireDateSnapshot)`) — ใช้กับ NOTICE/WARNING/URGENT |
| `{daysOverdue}` | `DATEDIFF(DAY, ExpireDateSnapshot, วันนี้)` — ใช้กับ EXPIRED |
| `{notifyCount}` | `CertificateAlert.NotifyCount` — ใช้ใน Subject เท่านั้น ไม่มีในตัว body ของไฟล์ใด |
| `{ackLink}` | `{AckBaseUrl}?tokens={token1},{token2},...` |
| `{orderEndDateThai}` / `{orderEndDateEn}` | `OrderEndDate` format ไทย พ.ศ. / อังกฤษ ค.ศ. — ใช้เฉพาะ `ssl-contact-notice-expired.html` |
| `{certStatusLineThai}` | render โดย C#: ก่อนหมดอายุ = "จะหมดอายุในอีก {days} วัน (วันที่ {expireDateThai})" / หลังหมดอายุ = "ได้หมดอายุไปแล้วเมื่อวันที่ {expireDateThai} ({daysOverdue} วันที่ผ่านมา)" — ใช้เฉพาะ `ssl-contact-notice-expired.html` |
| `{certStatusLineEn}` | render โดย C#: "will expire in {days} days (on {expireDateEn})" / "expired on {expireDateEn} ({daysOverdue} days ago)" — ใช้เฉพาะ `ssl-contact-notice-expired.html` |
| `{notifyCountLineThai}` | `NotifyCount > 1` → "นี่คือการแจ้งเตือนครั้งที่ {notifyCount} — " / ครั้งแรก → สตริงว่าง — ใช้ใน `ssl-contact-notice-expired.html` และ `ssl-expiry-notice-expired-customer.html` |
| `{notifyCountLineEn}` | `NotifyCount > 1` → "This is notification number {notifyCount} — " / ครั้งแรก → สตริงว่าง — ใช้ใน 2 ไฟล์เดียวกับด้านบน |

### Placeholder คำขึ้นต้น + ปุ่มรับทราบแบบไดนามิก (ใช้ในไฟล์ที่ส่งได้ทั้ง Sales และลูกค้า และ/หรือ ทุกระดับ: notice / warning / urgent / contact-notice):
| Placeholder | เมลถึง Sales | เมลถึงลูกค้า |
|---|---|---|
| `{greetingThai}` | `เรียน คุณ{saleName}` | `เรียน ท่านลูกค้า` |
| `{greetingEn}` | `Dear {saleName},` | `Dear Customer,` |

| Placeholder | AlertLevel ≠ EXPIRED | AlertLevel = EXPIRED |
|---|---|---|
| `{ackButtonLabel}` | `✓ รับทราบ กำลังดำเนินการ / Acknowledged – In Progress` | `✓ รับทราบ — หยุดการแจ้งเตือน / Acknowledge – Stop Alerts` |

`{ackButtonLabel}` มีใช้เฉพาะใน `ssl-contact-notice-expired.html` เท่านั้น (ไฟล์เดียวที่ใช้ข้ามทุกระดับ) — ไฟล์อื่นมีข้อความปุ่มตายตัวเพราะรู้ระดับจากชื่อไฟล์อยู่แล้ว

C# เป็นผู้ประกอบค่าตามผู้รับ/ระดับก่อน replace — ส่วนไฟล์ `ssl-expiry-notice-expired.html` (Sales เท่านั้น) และ `ssl-expiry-notice-expired-customer.html` (ลูกค้าเท่านั้น) มีคำขึ้นต้นตายตัวในไฟล์อยู่แล้ว ไม่ใช้ placeholder คำขึ้นต้นนี้

**Placeholder เฉพาะเมลกลุ่ม EXPIRED (template expired แบบ list):**
| Placeholder | ค่า |
|---|---|
| `{saleName}` | `KSC_USERS.FIRST_NAME + ' ' + LAST_NAME` ของ Sales ผู้รับ (ใช้ใน `{greetingThai}` ด้วย) |
| `{certCount}` | จำนวน certificate ในเมลฉบับนั้น |
| `{certRows}` | แถว `<tr>` ที่ C# สร้างจาก row template ในไฟล์ (ต่อ 1 cert: `{customerName}`, `{domain}`, `{expiredDate}`) เรียงตาม ExpiredDate เก่าสุดก่อน |
| `{expiredDate}` (ใน row) | `ExpireDateSnapshot` ของใบนั้น format "18 Sep 2026" |

การ replace placeholder ต้องเป็นแบบ **graceful**: placeholder ที่ไม่มีในไฟล์ template ให้ข้ามเงียบ ๆ ไม่ error (แต่ละ template ใช้ placeholder ไม่ครบทุกตัว)

---

## 5. โครงสร้างโปรเจกต์ที่ต้องการ

```
SslExpireNotify/
├── SslExpireNotify.sln
├── src/SslExpireNotify.Worker/
│   ├── Program.cs                  (Host + UseWindowsService + Serilog + Quartz DI)
│   ├── appsettings.json
│   ├── Jobs/SslExpireCheckJob.cs
│   ├── Services/
│   │   ├── ICertificateAlertService.cs / CertificateAlertService.cs
│   │   ├── IEmailTemplateService.cs  / EmailTemplateService.cs   (อ่านไฟล์ HTML + replace placeholder)
│   │   ├── IEmailSender.cs           / MailApiEmailSender.cs     (HttpClient เรียก KSC Mail API + Polly retry + circuit breaker)
│   │   ├── IEmailSender.cs           / SmtpEmailSender.cs        (MailKit — ช่องทางสำรอง)
│   │   ├── CompositeEmailSender.cs                                (ตัดสินใจ MailApi vs Smtp ตาม PreferredChannel — ดูข้อ 7.4)
│   │   └── IJobLockService.cs        / JobLockService.cs         (sp_getapplock กันหลาย instance รันซ้อน)
│   ├── Templates/
│   │   ├── ssl-expiry-notice.html                 (NOTICE)
│   │   ├── ssl-expiry-warning.html                (WARNING)
│   │   ├── ssl-expiry-urgent.html                 (URGENT)
│   │   ├── ssl-expiry-notice-expired.html          (เมล Sales — ตาราง list)
│   │   ├── ssl-expiry-notice-expired-customer.html (เมลลูกค้า — รายใบ)
│   │   └── ssl-contact-notice-expired.html   (แจ้งเตือนต่อสัญญาผลิตภัณฑ์ — ไฟล์เดียวทุกระดับ)
│   ├── Repositories/  (Dapper: SslCertificate อ่านจาก SSL_Certificate+CUSTOMER+KSC_USERS แบบ read-only, Alert, EmailLog, JobRunHistory)
│   ├── Models/        (POCO ตรงกับตาราง + enum AlertLevel, AlertStatus)
│   └── Options/       (JobOptions, MailApiOptions, EmailTemplateOptions — bind ด้วย IOptions<T>, validate ตอน startup)
├── Database/
│   ├── schema.sql
│   └── seed.sql
├── installer/SslExpireNotify.Installer/
│   ├── SslExpireNotify.Installer.wixproj   (WiX v7 — MSBuild SDK-style)
│   ├── Package.wxs                          (product, service install, upgrade rules)
│   ├── Folders.wxs / Components.wxs         (โครงสร้างไฟล์ + appsettings/Templates)
│   └── License.rtf
├── deploy/
│   └── build-package.ps1    (publish + build MSI ในคำสั่งเดียว)
└── README.md
```

## 6. ข้อกำหนดเพิ่มเติม

- ทุก step ต้อง log ด้วย Serilog: จำนวน cert ที่ scan, alert ใหม่, ส่งซ้ำ, resolved, email สำเร็จ/ล้มเหลว
- ส่งเมลล้มเหลว: Polly retry 3 ครั้ง ถ้ายังล้มเหลว → บันทึก `EmailLog` เป็น Failed พร้อม ErrorMessage แล้ว**ทำ cert ตัวถัดไปต่อ** ห้าม job ล้มทั้งรอบ
- ถ้าส่งเมลล้มเหลวทุกผู้รับในกรณี A ให้ยัง INSERT alert ไว้ (จะได้ไม่สร้างซ้ำ) แต่ `LastNotifiedAt = NULL` เพื่อให้รอบถัดไปส่งใหม่ได้
- ใช้ transaction ครอบเฉพาะจุดที่จำเป็น (INSERT alert + first EmailLog)
- README.md อธิบาย: วิธี build, วิธีติดตั้งด้วย MSI (ดูข้อ 8), วิธีรัน schema/seed, วิธีทดสอบด้วย `RunOnStartup = true`
- เขียน unit test ให้ logic เลือก AlertLevel จาก config, logic คุมความถี่ส่งซ้ำ (RepeatEveryDays 7 vs 1 เทียบ LastNotifiedAt), logic supersede เมื่อเลื่อนระดับ, logic ปุ่มรับทราบ (Noted ที่ระดับ NOTICE/WARNING/URGENT ต้องยังส่งซ้ำ vs Acknowledged ที่ EXPIRED ต้องหยุด), logic ตัดสิน NotificationType (เทียบ SSLExpiredDate+199 กับ OrderEndDate รวมเคส OrderEndDate NULL), logic จัดกลุ่ม EXPIRED ตาม SalesID (รวมเคส Sales ไม่มีอีเมล → ถอยเป็นรายใบ), logic ประกอบผู้รับ (Cc จาก config, flag SendToCustomer เปิด/ปิด, เมลกลุ่มต้องไม่แตกส่งถึงลูกค้า, เมลลูกค้าล้มเหลวต้องไม่กระทบเมล Sales), logic เลือก subject (level / EXPIRED_GROUP / CONTRACT*) และ logic replace placeholder แบบ graceful รวมการสร้าง {certRows} (xUnit)
- เพิ่ม unit test เฉพาะด้าน reliability: validation ของ `Job:AlertLevels` ตอน startup (severity/days ไม่สอดคล้องกันต้อง throw), การที่ 1 certificate throw exception ระหว่าง STEP 2/3/4 ต้องไม่ทำให้ certificate อื่นในรอบเดียวกันหยุดประมวลผล, `DryRun = true` ต้องไม่มีการเขียนลง `CertificateAlert`/`EmailLog` เลย
- เพิ่ม unit test สำหรับ `CompositeEmailSender`: `PreferredChannel` ทั้ง 3 ค่าต้องเลือกช่องทางถูกต้อง, 4xx จาก Mail API ต้องไม่ fallback ไป SMTP, timeout/5xx ครบ retry ต้อง fallback, circuit breaker เปิดแล้วต้องข้าม Mail API ไปเรียก SMTP ทันทีโดยไม่ retry ซ้ำ, ล้มเหลวทั้งสองช่องทางต้องรวม ErrorMessage จากทั้งคู่

## 7. ความเสถียรและการทำงานทนทาน (Reliability — สำคัญ)

### 7.1 การป้องกัน job รันซ้อนกัน
- `[DisallowConcurrentExecution]` ของ Quartz ป้องกันได้แค่ภายใน process เดียว **ไม่พอสำหรับกรณีมี 2 instance ของ service รันพร้อมกัน** (เช่น deploy พลาดตอน MSI upgrade ทำให้ service เก่ายังไม่ทันหยุดสนิท)
- ต้องเพิ่มชั้นป้องกันที่สองระดับฐานข้อมูล: เรียก `sp_getapplock` (resource name คงที่ เช่น `'SslExpireNotifyJob'`, mode `Exclusive`, timeout สั้น ~5 วินาที) ก่อนเริ่ม STEP 1 ทุกครั้ง — ถ้าเอา lock ไม่ได้ให้ log แล้วจบ job ทันทีโดยไม่ทำอะไร (ถือว่ามีอีก instance กำลังรันอยู่)
- ปล่อย lock ใน `finally` เสมอ ไม่ว่า job จะสำเร็จหรือ error

### 7.2 ความทนทานระหว่างสแกน (per-certificate isolation)
- Loop ใน STEP 2/3/4 ต้อง **wrap try/catch รายตัวรอบ certificate แต่ละใบ** ถ้าตัวใดพัง (ข้อมูลผิดปกติ, domain เกิน, format แปลก) ให้ log error พร้อม `SSL_Cert_ID` แล้ว **ข้ามไปตัวถัดไปทันที** ห้ามปล่อยให้ exception ตัวเดียวทำให้ทั้งรอบ job ล้มและ cert ที่เหลือไม่ถูกประมวลผลเลย
- นับจำนวน error ต่อรอบ ถ้าเกินเกณฑ์ที่ผิดปกติ (เช่น >50% ของ cert ทั้งหมด) ให้ log เป็น Warning ระดับสูงเพื่อสื่อว่าอาจมีปัญหาเชิงระบบ (เช่น DB connection กระตุก) ไม่ใช่แค่ข้อมูลผิดปกติทีละราย

### 7.3 Resilience ของการเชื่อมต่อฐานข้อมูล
- เพิ่ม Polly retry policy สำหรับ DB call (ไม่ใช่แค่ Mail API) ครอบ transient fault เช่น connection timeout, deadlock (SQL error 1205) — retry 3 ครั้ง เว้นช่วงสั้นๆ (200ms, 500ms, 1s)
- Query ที่มีโอกาสชนกันเอง (เช่น UPDATE...Superseded พร้อมกับ INSERT alert ใหม่) ให้ครอบด้วย transaction ระดับ `READ COMMITTED` ตามปกติของ Dapper ไม่ต้องยกระดับ isolation เว้นแต่เจอปัญหาจริง

### 7.4 Resilience ของการส่งเมล (Mail API + SMTP fallback)

**สถาปัตยกรรม**: `IEmailSender` เดิมมี 2 implementation คือ `MailApiEmailSender` และ `SmtpEmailSender` (ใช้ `MailKit`) โดยมี `CompositeEmailSender` ครอบอีกชั้นเป็นตัวตัดสินใจว่าจะเรียกตัวไหน ตาม `MailApi:PreferredChannel`

**Logic การตัดสินใจต่อ 1 ฉบับที่จะส่ง:**

| `PreferredChannel` | พฤติกรรม |
|---|---|
| `MailApiOnly` | เรียก Mail API เท่านั้น ล้มเหลว = `Failed` ไม่มี fallback |
| `SmtpOnly` | เรียก SMTP เท่านั้น ข้าม Mail API ไปเลย |
| `Auto` (default) | ดูตารางด้านล่าง |

**Logic ของโหมด `Auto`:**
1. ถ้า circuit breaker ของ Mail API เปิดอยู่ (จากรอบนี้) → **ข้าม Mail API ไปเรียก SMTP ทันที** ไม่ต้องเสียเวลา retry ที่รู้อยู่แล้วว่าจะพัง
2. ถ้า circuit breaker ยังปิดอยู่ → เรียก Mail API ตามปกติ (Polly retry 3 ครั้งสำหรับ timeout/5xx)
   - สำเร็จ → จบ, `Channel = 'MailApi'`
   - ล้มเหลวด้วย 4xx → **ไม่ fallback ไป SMTP** (ปัญหาข้อมูล ไม่ใช่ปัญหาช่องทาง) → `Failed` ทันที
   - ล้มเหลวด้วย timeout/5xx ครบ retry → นับเข้า circuit breaker counter แล้ว **fallback ไป SMTP** ถ้า `SmtpFallback:Enabled = true`
3. เรียก SMTP (ถ้าถึงขั้นนี้): มี retry ของตัวเอง (2 ครั้ง พอประมาณ ไม่ต้องเข้มเท่า Mail API)
   - สำเร็จ → `Channel = 'Smtp'`
   - ล้มเหลว → `Failed`, `ErrorMessage` รวมข้อความจากทั้งสองช่องทาง

**Logging (บังคับ):**
- ทุกครั้งที่ fallback ไป SMTP ต้อง log **Warning** ระดับสูง พร้อมเหตุผล (circuit open / retry exhausted) — ไม่ปล่อยให้การสลับช่องทางเงียบจนไม่มีใครรู้ว่า Mail API มีปัญหา
- สรุปท้ายรอบ job ใน `JobRunHistory.EmailsSentViaFallback` และ log บรรทัดสรุป ถ้า > 0 ให้ log เป็น Warning เพื่อดึงความสนใจทีมงาน แม้เมลจะส่งสำเร็จทั้งหมดก็ตาม (เพราะแปลว่า Mail API มีปัญหาที่ควรตรวจสอบ)

**ข้อควรระวัง**: ต้องยืนยันกับทีม infra ว่า `SmtpFallback:Host` เป็น SMTP relay ที่ได้รับอนุญาตให้ส่งในนามโดเมน `ksc.net` (SPF/DKIM) ไม่งั้นเมลที่ fallback ไปทาง SMTP อาจถูกจัดเป็น spam ที่ปลายทาง

### 7.5 Configuration validation ตอน startup
- ตอน service start ต้อง validate `Job:AlertLevels` ก่อนรับ request ใดๆ: `Severity` ต้องไม่ซ้ำกัน, เรียงจาก `Days` มากไปน้อยตาม `Severity` น้อยไปมากอย่างสอดคล้องกัน (severity สูงสุดต้อง `Days` น้อยสุด) — ถ้า config ผิดให้ **fail fast** (throw exception ตอน start, service ไม่ start เลย) พร้อม log ข้อความบอกว่าแก้ตรงไหน แทนที่จะปล่อยให้ job รันแล้วให้ผลลัพธ์แปลกๆ แบบเงียบๆ
- validate ค่าที่จำเป็นอื่นด้วยเช่นกัน: `MailApi:Url`, `ConnectionStrings:SslNotifyDb`, `AckBaseUrl` ต้องไม่ว่าง

### 7.6 Observability — ประวัติการรันและ correlation
- ทุกครั้งที่ job เริ่ม ให้ INSERT แถวใหม่ใน `JobRunHistory` (`RunId` เป็น GUID ใหม่) และ UPDATE ให้ครบตอนจบ (`FinishedAt`, `Status`, ตัวนับต่างๆ)
- **`RunId` ต้องแนบไปกับทุกบรรทัด log ของรอบนั้น** (Serilog enrichment ด้วย `LogContext.PushProperty("RunId", ...)`) เพื่อไล่ debug ได้ว่ารอบไหนทำอะไรบ้าง
- ถ้า job ทั้งรอบ throw exception ไม่ทันจบปกติ ให้ catch ที่ระดับบนสุดของ `Execute()`, บันทึก `Status = 'Failed'`, `ErrorSummary` แล้ว re-throw ต่อให้ Quartz log ด้วย (ไม่กลืน exception เงียบๆ)
- `Job:DryRun = true` → บันทึก `JobRunHistory.IsDryRun = 1` และไม่มีการ INSERT/UPDATE ใดๆ ยกเว้นแถวของ JobRunHistory เอง (การรันแบบ dry run ต้องไม่ทิ้งร่องรอยใน CertificateAlert/EmailLog)
- purge แถวเก่าใน `JobRunHistory` ที่เกิน `Job:JobRunHistoryRetentionDays` ทุกรอบที่รัน (DELETE ท้าย STEP สุดท้าย)

### 7.7 ข้อกำหนดเพิ่มสำหรับ Ack Endpoint (เมื่อ implement ในเฟสถัดไป)
เผื่อไว้ล่วงหน้า แม้ยังไม่ implement ในเฟสนี้ (ดูข้อ 9 รายการสิ่งที่ยังไม่ทำ):
- token (`AckToken`) ต้องตรวจ `AckTokenExpireAt` ก่อนยอมรับ — หมดอายุแล้วต้องปฏิเสธและแจ้งผู้ใช้ให้ติดต่อฝ่ายขาย
- ควร rate-limit ต่อ IP กันการสุ่ม token (endpoint เป็น public-facing)
- กดซ้ำ token เดิมที่ยัง valid ต้องไม่ error (idempotent ตามที่ระบุไว้ในหัวข้อ "ปุ่มรับทราบ")

## 8. MSI Installer สำหรับติดตั้ง Production (สำคัญ)

สร้างตัวติดตั้งแบบ **MSI ด้วย WiX Toolset v7** (MSBuild SDK-style project ผ่าน NuGet — ไม่ต้องติดตั้ง WiX แยกบนเครื่อง build, ใช้ `dotnet build` ได้เลย)

### 8.1 ข้อกำหนดของ MSI

**การติดตั้ง (Install)**
- ติดตั้งไปที่ `C:\Program Files\KSC\SslExpireNotify\` (เปลี่ยน path ได้ตอนติดตั้งผ่าน UI หรือ `msiexec ... INSTALLFOLDER=...`)
- รวมผลลัพธ์จาก `dotnet publish` แบบ **self-contained win-x64 Release** ทั้งหมด (รวมโฟลเดอร์ `Templates/`) — เครื่อง production ไม่ต้องติดตั้ง .NET Runtime
- ลงทะเบียน **Windows Service** ด้วย `ServiceInstall` + `ServiceControl`:
  - ชื่อ service: `SslExpireNotify`
  - Display name: `KSC SSL Expire Notify`
  - Description: `KSC SSL Certificate Expiration Notification Service`
  - Start type: **delayed auto-start** (`Start="auto"` + `DelayedAutoStart="yes"`)
  - Account: `LocalSystem` (default)
  - **Recovery/failure actions**: ใช้ `util:ServiceConfig` จาก `WixToolset.Util.wixext` — restart หลังล้มเหลว 60 วินาที, reset ตัวนับ 1 วัน
- **ไม่ start service อัตโนมัติหลังติดตั้ง** (`ServiceControl` ให้ Start เฉพาะตอน... ไม่ใส่ Start="install") — ผู้ติดตั้งต้องแก้ `appsettings.json` (connection string, Mail API URL, SMTP fallback) ก่อนแล้วค่อย start เอง หน้าจอสุดท้ายของ installer ต้องแสดงข้อความเตือนเรื่องนี้
- สร้างโฟลเดอร์ `logs/` พร้อมสิทธิ์เขียนให้ service account

**การอัปเดต (Major Upgrade)**
- ใส่ `<MajorUpgrade DowngradeErrorMessage="..."/>` + `UpgradeCode` คงที่ (GUID เดียวตลอดทุกเวอร์ชัน — ห้ามเปลี่ยน)
- เลขเวอร์ชัน MSI ผูกกับ `<Version>` ใน `.csproj` (ส่งผ่าน MSBuild property ตัวเดียว ไม่แก้สองที่)
- ตอน upgrade: MSI ต้อง stop service → แทนที่ไฟล์ → (ไม่ start อัตโนมัติ เตือนให้ตรวจ config ก่อน)
- **`appsettings.json` ต้องไม่ถูกทับตอน upgrade**: แยกเป็น Component ของตัวเอง ตั้ง `NeverOverwrite="yes"` + `Permanent="no"` และไฟล์เวอร์ชันใหม่ให้ติดตั้งเป็น `appsettings.default.json` ไว้ข้าง ๆ เพื่อให้ผู้ดูแล merge key ใหม่เอง
- โฟลเดอร์ `logs/` และไฟล์ log ต้องไม่ถูกลบตอน upgrade/uninstall

**การถอนการติดตั้ง (Uninstall)**
- ผ่าน "Add/Remove Programs" หรือ `msiexec /x`
- Stop + ลบ service ให้เรียบร้อย
- **คงไฟล์ `appsettings.json` และ `logs/` ไว้** (ไม่ลบ config/log ของ production ทิ้ง)

**UI**
- ใช้ `WixToolset.UI.wixext` ชุด `WixUI_InstallDir` (เลือกโฟลเดอร์ติดตั้งได้ + License.rtf)
- รองรับ silent install: `msiexec /i SslExpireNotify-v1.0.0.msi /qn INSTALLFOLDER="D:\Services\SslExpireNotify"`

### 8.2 build-package.ps1 — build ทุกอย่างในคำสั่งเดียว
รันบนเครื่อง dev/build:
1. `dotnet publish src/SslExpireNotify.Worker -c Release -r win-x64 --self-contained true`
2. `dotnet build installer/SslExpireNotify.Installer -c Release` (WiX v7 build ผ่าน NuGet ได้เลย)
   - ไฟล์ publish จำนวนมาก ให้ใช้ **WiX Files element / harvesting** (`<Files Include="$(PublishDir)\**">`) ไม่ต้อง list ทีละไฟล์ แต่ต้อง exclude `appsettings.json` ออกจาก harvest แล้วประกาศเป็น Component แยกตามข้อ 8.1 (NeverOverwrite)
3. ได้ผลลัพธ์ `dist/SslExpireNotify-v{version}.msi`
4. สร้าง `dist/SslExpireNotify-v{version}-deploy.zip` บรรจุ: MSI + `database/schema.sql` + `database/seed.sql` + `README-DEPLOY.md`
   (database scripts ไม่รวมใน MSI เพราะรันบน SQL Server คนละเครื่องกับ service)

### 8.3 README-DEPLOY.md ต้องครอบคลุม
- ขั้นตอนติดตั้งครั้งแรก: รัน `schema.sql` บน SQL Server → ติดตั้ง MSI → แก้ `appsettings.json` (connection string, Mail API URL, SMTP fallback) → ทดสอบด้วย `RunOnStartup = true` แล้วปิดกลับ → `Start-Service SslExpireNotify`
- ขั้นตอน upgrade: รัน MSI เวอร์ชันใหม่ทับได้เลย → ตรวจ `appsettings.default.json` ว่ามี key ใหม่ต้อง merge ไหม → start service
- ตัวอย่างคำสั่ง silent install / uninstall สำหรับติดตั้งหลายเครื่อง
- วิธีตรวจ log และ troubleshoot service ไม่ start
- **คำเตือนตัวใหญ่: ห้ามรัน `seed.sql` บน production** (มี DELETE ล้างตาราง)

## 9. สิ่งที่ยังไม่ต้องทำในเฟสนี้

- Web API สำหรับปุ่ม Acknowledge (`/ack?token=...`) — จะทำเป็นโปรเจกต์แยกภายหลัง ตอนนี้แค่ generate ลิงก์ใน email ให้ถูก format
- Escalation Cc ภายใน — เผื่อโครงสร้างไว้ แต่ยังไม่ implement
- Watchdog/dead-man's-switch ที่คอยเช็ค `JobRunHistory` แล้วแจ้งเตือนถ้า job ไม่รันเกินเวลาที่กำหนด — เฟสนี้มีแค่ตาราง `JobRunHistory` ให้บันทึกไว้เป็นฐาน ยังไม่ต้องสร้างกลไกแจ้งเตือนแยก
