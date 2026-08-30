# SslExpireNotify

Windows Service (.NET 10 Worker Service) ที่รันทุกวันเวลา **00:30** เพื่อตรวจวันหมดอายุของ SSL Certificate
ในระบบ KSC แล้วส่งอีเมลแจ้งเตือนถึง Sales ผู้ดูแล (และถึงลูกค้าได้ถ้าเปิดใช้)

- **Scheduler**: Quartz.NET — cron `0 30 0 * * ?` ผูกกับ `TimeZoneInfo` จาก config โดยตรง ไม่พึ่ง timezone ของ OS
- **Database**: SQL Server ผ่าน Dapper (อ่านตารางเดิมของ KSC แบบ read-only, เขียนเฉพาะตารางของระบบนี้)
- **Email**: KSC Mail API เป็นช่องทางหลัก + SMTP fallback (MailKit) เมื่อ Mail API ล่ม
- **Resilience**: Polly retry + circuit breaker ฝั่ง Mail API, Polly retry สำหรับ DB transient fault, `sp_getapplock` กันหลาย instance รันซ้อน
- **Logging**: Serilog อ่าน config ทั้งหมดจาก `appsettings.json` ทุกบรรทัดของรอบเดียวกันมี `RunId` กำกับ

ทุกค่าที่ต้องปรับ (connection string, Mail API URL, cron, ระดับแจ้งเตือน, path ของ template, log) อยู่ใน
`appsettings.json` ทั้งหมด — ไม่มีค่าไหน hardcode ในโค้ด

---

## โครงสร้างโปรเจกต์

```
SslExpireNotify.sln
├── src/SslExpireNotify.Worker/
│   ├── Program.cs                 Host + UseWindowsService + Serilog + Quartz + options validation
│   ├── appsettings.json           ค่า config ทั้งหมด
│   ├── Jobs/SslExpireCheckJob.cs  งานหลัก (job lock, JobRunHistory, STEP 1–4)
│   ├── Services/                  logic ทั้งหมด (ดูตารางด้านล่าง)
│   ├── Repositories/              Dapper: SSL_Certificate (read-only), Alert, JobRunHistory
│   ├── Models/                    POCO + สถานะ/ประเภทแบบ constant
│   ├── Options/                   binding + validator ของแต่ละ section
│   └── Templates/                 อีเมล HTML 6 ไฟล์ (copy ไป output ตอน build)
├── tests/SslExpireNotify.Tests/   xUnit — 133 tests
├── Database/schema.sql            สร้างตารางของระบบนี้ (idempotent)
├── Database/seed.sql              ข้อมูลทดสอบ (test DB เท่านั้น)
├── installer/SslExpireNotify.Installer/   WiX v7 (MSBuild SDK-style)
├── deploy/build-package.ps1       publish + MSI + zip ในคำสั่งเดียว
└── deploy/README-DEPLOY.md        คู่มือสำหรับผู้ติดตั้ง
```

### Services

| ไฟล์ | หน้าที่ |
|---|---|
| `CertificateAlertService` | ผูก STEP 1–4 เข้าด้วยกัน, ครอบ try/catch รายใบ, นับสถิติของรอบ |
| `AlertPlanner` | logic ล้วนของ STEP 2–3: เลือกระดับ, supersede, สร้างใหม่ / ส่งซ้ำ / ข้าม |
| `AlertLevelResolver` | จับคู่วันคงเหลือกับระดับที่ Severity สูงสุดจาก config |
| `ResendPolicy` | ถึงรอบส่งซ้ำหรือยัง (`RepeatEveryDays` เทียบ `LastNotifiedAt`) |
| `NotificationTypeResolver` | CERT_RENEWAL หรือ CONTRACT_RENEWAL (`SSLExpiredDate + 199` เทียบ `OrderEndDate`) |
| `NotificationGrouper` | จัดกลุ่ม EXPIRED ตาม SalesID, ที่เหลือส่งรายใบ |
| `RecipientResolver` | ประกอบผู้รับ (To/Cc/ลูกค้า) และเหตุผลเมื่อส่งไม่ได้ |
| `EmailComposer` | เลือก template + subject แล้ว render placeholder ทั้งหมด |
| `EmailTemplateService` | อ่านไฟล์ HTML (cache ต่อรอบ), replace แบบ graceful, สร้าง `{certRows}` |
| `MailApiEmailSender` | HttpClient + Polly retry (3 ครั้ง, exponential) + circuit breaker |
| `SmtpEmailSender` | ช่องทางสำรองด้วย MailKit (retry 2 ครั้ง) |
| `CompositeEmailSender` | ตัดสินใจ MailApi vs SMTP ตาม `PreferredChannel` และสุขภาพของ Mail API |
| `JobLockService` | `sp_getapplock` กันหลาย instance รันพร้อมกัน |

---

## Build

ต้องมี **.NET 10 SDK**

```powershell
dotnet build                      # ทั้ง solution
dotnet test                       # unit tests
dotnet run --project src/SslExpireNotify.Worker    # รันเป็น console app (ทดสอบบนเครื่อง dev)
```

### สร้างแพ็กเกจติดตั้ง (publish + MSI + zip)

```powershell
.\deploy\build-package.ps1
```

ได้ผลลัพธ์ใน `dist\`

```
SslExpireNotify-v1.0.0.msi
SslExpireNotify-v1.0.0-deploy.zip     (MSI + database/schema.sql + database/seed.sql + README-DEPLOY.md)
```

เลขเวอร์ชันอ่านจาก `<Version>` ใน `SslExpireNotify.Worker.csproj` ที่เดียว แล้วส่งต่อให้ MSI
(override ได้ด้วย `-Version 1.2.0`)

> **หมายเหตุเรื่อง WiX v7**: ตั้งแต่ v6 เป็นต้นมา WiX ต้องยอมรับ Open Source Maintenance Fee (OSMF) EULA
> ก่อนถึงจะ build ได้ ทำครั้งเดียวต่อเครื่อง build:
> ```powershell
> dotnet build installer/SslExpireNotify.Installer -t:AcceptEula -p:EulaId=<OSMF-EULA-id>
> ```
> ดูรายละเอียดที่ https://wixtoolset.org/osmf/ — `build-package.ps1` จะเตือนล่วงหน้าถ้ายังไม่ได้ทำ
> ถ้าองค์กรไม่ต้องการสมัคร OSMF สามารถเปลี่ยน `Sdk="WixToolset.Sdk/7.0.0"` และเวอร์ชันของ
> `WixToolset.UI.wixext` / `WixToolset.Util.wixext` ใน `.wixproj` เป็น `6.0.2` ได้ทันที
> (ตัว authoring ใช้ schema เดียวกัน ไม่ต้องแก้ไฟล์ `.wxs` เลย)

---

## ติดตั้งและใช้งานบน production

ดูรายละเอียดทั้งหมดใน [`deploy/README-DEPLOY.md`](deploy/README-DEPLOY.md) — สรุปสั้น ๆ

1. รัน `Database/schema.sql` บน SQL Server
2. `msiexec /i SslExpireNotify-v1.0.0.msi`
3. แก้ `appsettings.json` (connection string, Mail API URL, SMTP fallback, `AckBaseUrl`)
4. ทดสอบด้วย `RunOnStartup = true` + `DryRun = true` แล้วดู log
5. ตั้งกลับเป็น `false` ทั้งคู่ แล้ว `Start-Service SslExpireNotify`

MSI **ไม่ start service ให้อัตโนมัติ** โดยตั้งใจ เพราะต้องแก้ config ก่อนเสมอ

### รัน schema / seed

```powershell
sqlcmd -S SQLSERVER01 -d KSC_SSL -E -i .\Database\schema.sql   # ปลอดภัย รันซ้ำได้
```

`seed.sql` มี `DELETE` ล้างตาราง `CUSTOMER` / `KSC_USERS` / `SSL_Certificate` — **ห้ามรันบน production**
และต้องแก้ `@ConfirmTestDatabase = 1` ในไฟล์ก่อนถึงจะทำงาน

### ทดสอบด้วย RunOnStartup

```json
"Job": { "RunOnStartup": true, "DryRun": true }
```

`RunOnStartup` = รัน job ทันที 1 ครั้งตอน service start (เพิ่มจาก cron ปกติ ไม่ได้แทนที่)
`DryRun` = ทำทุกอย่างยกเว้นเขียน DB และส่งเมลจริง — log บอกว่าจะส่งอะไรถึงใคร
ทั้งสองค่าเป็นอิสระต่อกัน: `RunOnStartup` คุมจังหวะเวลา, `DryRun` คุมผลข้างเคียง

---

## การทำงานของ job

```
STEP 1  Auto-Resolve   ปิด alert ที่ SSLExpiredDate ถูกขยายออกไปแล้ว (Resolved)
STEP 2  Scan           ดึง cert ที่ SSLStatus อยู่ใน ActiveSslStatusValues แล้วคำนวณวันคงเหลือ
STEP 3  Plan           เลือกระดับ → ตัดสิน NotificationType → supersede ระดับต่ำกว่า → สร้าง/ส่งซ้ำ/ข้าม
STEP 4  Send           จัดกลุ่มแล้วส่ง: EXPIRED รวมเป็นเมลเดียวต่อ Sales, ที่เหลือส่งรายใบ
        Purge          ลบ JobRunHistory ที่เกิน JobRunHistoryRetentionDays
```

### ระดับแจ้งเตือน (แก้ได้ที่ `Job:AlertLevels` ไม่ต้อง build ใหม่)

| Level | Days | Severity | ส่งซ้ำทุก |
|---|---|---|---|
| NOTICE | ≤ 30 | 1 | 7 วัน |
| WARNING | ≤ 15 | 2 | 7 วัน |
| URGENT | ≤ 7 | 3 | 1 วัน |
| EXPIRED | ≤ 0 | 4 | 1 วัน |

เลือกระดับที่ **Severity สูงสุดที่ `days <= Days`** ระดับจึงเลื่อนขึ้นเองเมื่อใกล้หมดอายุ
และ alert ระดับเดิมจะถูก `Superseded` โดยไม่ต้องรอใครกดรับทราบ

ตอน start service จะ validate ตารางนี้ก่อน ถ้า Severity ซ้ำ หรือเรียงไม่สอดคล้องกับ Days
service จะ **ไม่ start** พร้อมข้อความบอกว่าแก้ตรงไหน (fail fast แทนที่จะให้ผลลัพธ์แปลก ๆ เงียบ ๆ)

### เงื่อนไขหยุดแจ้งเตือน

มี 2 ทางเท่านั้น
1. **Auto-Resolve** — `SSLExpiredDate` ถูกขยายออกไป → STEP 1 ปิดให้เอง
2. **Acknowledge ที่ระดับ EXPIRED** — ผู้รับกดปุ่ม "รับทราบทั้งหมด" → alert ทุกใบใน cycle เป็น `Acknowledged`

กดปุ่มในเมลระดับ NOTICE/WARNING/URGENT จะบันทึกเป็น `Noted` เท่านั้น — **ยังส่งซ้ำตามความถี่เดิม**

### ผู้รับ

- **To** = อีเมล Sales (`KSC_USERS.EMAIL` join จาก `SSL_Certificate.SalesID`)
- **Cc** = `Recipients:Cc` ใช้กับเมลทุกฉบับ (คั่นหลายอีเมลด้วย `,`)
- **ลูกค้า** = เมลแยกอีกฉบับถึง `SSL_Certificate.EmailAlert` เมื่อ `Recipients:SendToCustomer = true`
  (ส่งได้เฉพาะรายใบ — เมลกลุ่ม EXPIRED ไม่ถูกแตกส่งถึงลูกค้า)

เมลถึง Sales กับเมลถึงลูกค้าเป็นอิสระต่อกัน ฉบับใดล้มเหลวไม่กระทบอีกฉบับ และมี `EmailLog` แถวของตัวเอง
ถ้าไม่มีอีเมลผู้รับเลย ระบบยังสร้าง alert ตามปกติแต่คง `LastNotifiedAt = NULL` เพื่อให้รอบถัดไปลองใหม่หลังแก้ข้อมูล

### ช่องทางส่งเมล (`MailApi:PreferredChannel`)

| ค่า | พฤติกรรม |
|---|---|
| `Auto` (default) | Mail API ก่อน → ถ้า timeout/5xx ครบ retry หรือ circuit เปิด → SMTP fallback |
| `MailApiOnly` | Mail API เท่านั้น ไม่มี fallback |
| `SmtpOnly` | SMTP เท่านั้น |

- **4xx ไม่ fallback** เพราะเป็นปัญหาข้อมูล (เช่นอีเมลผิด format) ไม่ใช่ปัญหาช่องทาง
- circuit breaker เปิดอยู่ → ข้าม Mail API ไป SMTP ทันที ไม่เสียเวลา retry
- ทุกครั้งที่ fallback จะ log **Warning** และท้ายรอบสรุปลง `JobRunHistory.EmailsSentViaFallback`
  ถ้ามากกว่า 0 จะ log Warning อีกครั้ง แม้เมลจะส่งสำเร็จทั้งหมด เพราะแปลว่า Mail API มีปัญหาที่ควรตรวจสอบ

---

## Email template

ไฟล์ HTML 6 ไฟล์ใน `src/SslExpireNotify.Worker/Templates/` (copy ไป output ตอน build)
โค้ดมีหน้าที่แค่อ่านไฟล์ + replace placeholder เท่านั้น — placeholder ที่ไม่มีในไฟล์จะถูกข้ามเงียบ ๆ

| ไฟล์ | ใช้กับ |
|---|---|
| `ssl-expiry-notice.html` / `-warning` / `-urgent` | CERT_RENEWAL ระดับ NOTICE / WARNING / URGENT (ใช้ได้ทั้งเมล Sales และลูกค้า) |
| `ssl-expiry-notice-expired.html` | CERT_RENEWAL ระดับ EXPIRED — เมลกลุ่มถึง Sales แบบตาราง list |
| `ssl-expiry-notice-expired-customer.html` | เมลถึงลูกค้า ระดับ EXPIRED |
| `ssl-contact-notice-expired.html` | CONTRACT_RENEWAL — ไฟล์เดียวใช้ทุกระดับ |

ไฟล์เมลกลุ่มมี row template อยู่ใน HTML comment โค้ดจะดึงออกมาสร้างแถวแล้วแทนที่ `{certRows}`
(comment ตัวนั้นถูกตัดออกก่อนส่งเสมอ) ส่วน `ssl-contact-notice-expired.html` ใช้ `{ackButtonLabel}`
แบบไดนามิกเพราะไฟล์เดียวต้องรองรับทั้งข้อความ "กำลังดำเนินการ" และ "หยุดการแจ้งเตือน"

---

## Tests

```powershell
dotnet test
```

ครอบคลุม
- เลือก AlertLevel จาก config รวมค่าขอบ และตาราง level ที่กำหนดเอง
- ความถี่ส่งซ้ำ (7 vs 1 วัน เทียบ `LastNotifiedAt`), `Noted` ต้องยังส่งซ้ำ, `Acknowledged`/`Resolved` ต้องหยุด
- supersede เมื่อเลื่อนระดับ, cycle เก่าไม่บล็อกวันหมดอายุใหม่, การส่งที่ล้มเหลวต้องถูกลองใหม่
- ตัดสิน NotificationType รวมเคสขอบ 199 วันพอดี และ `OrderEndDate` เป็น NULL
- จัดกลุ่ม EXPIRED ตาม SalesID รวมเคสไม่มี Sales → ถอยเป็นรายใบ
- ประกอบผู้รับ: Cc จาก config, `SendToCustomer` เปิด/ปิด, เมลกลุ่มไม่แตกส่งถึงลูกค้า, เมลลูกค้าล้มไม่กระทบเมล Sales
- เลือก subject (level / EXPIRED_GROUP / CONTRACT* / CUSTOMER_*)
- replace placeholder แบบ graceful, สร้าง `{certRows}`, HTML encode และ **render กับไฟล์ template จริงที่ ship ไปพร้อมกัน** เพื่อยืนยันว่าไม่มี placeholder ตกค้าง
- validate `Job:AlertLevels` และ config อื่นตอน startup (ผิดต้อง fail)
- 1 certificate พังต้องไม่ทำให้ใบอื่นในรอบเดียวกันหยุด
- `DryRun = true` ต้องไม่เขียน `CertificateAlert` / `EmailLog` เลย
- `CompositeEmailSender`: PreferredChannel ทั้ง 3 ค่า, 4xx ไม่ fallback, retry หมดแล้ว fallback,
  circuit เปิดต้องข้าม Mail API ทันที, ล้มทั้งสองช่องทางต้องรวม ErrorMessage จากทั้งคู่

---

## ยังไม่ทำในเฟสนี้

- **Web API สำหรับปุ่ม Acknowledge** (`/ack?tokens=...`) — เฟสนี้ generate ลิงก์ให้ถูก format เท่านั้น
  โครงสร้างข้อมูลพร้อมแล้ว (`AckToken`, `AckTokenExpireAt`, `AcknowledgedAt`, `AcknowledgedBy`)
  เมื่อ implement ต้อง: ตรวจ `AckTokenExpireAt` ก่อนรับ, rate-limit ต่อ IP (endpoint เป็น public-facing),
  และกดซ้ำ token เดิมที่ยัง valid ต้องไม่ error (idempotent)
- **Escalation Cc ภายใน** — เผื่อโครงสร้างไว้แต่ยังไม่ implement
- **Watchdog / dead-man's-switch** ที่คอยเช็คว่า job ไม่ได้รันเกินเวลา — เฟสนี้มีแค่ตาราง `JobRunHistory` เป็นฐานข้อมูลให้ตรวจ

---

## หมายเหตุการตัดสินใจที่เบี่ยงจากสเปกเล็กน้อย

1. **`appsettings.json` component ตั้ง `Permanent="yes"`** (สเปกเขียน `Permanent="no"`)
   เพราะข้อกำหนดในหัวข้อ Uninstall ระบุว่าต้อง **คงไฟล์ `appsettings.json` ไว้** ซึ่ง `Permanent="no"`
   จะทำให้ไฟล์ถูกลบตอน uninstall ส่วน `NeverOverwrite="yes"` ยังคงไว้ตามเดิมเพื่อกันการทับตอน upgrade
2. **Serilog sink args เพิ่ม `outputTemplate`** ที่มี `{RunId}` เพื่อให้ RunId ปรากฏในบรรทัด log จริง
   (ตัว property ถูก enrich อยู่แล้ว แต่ template เริ่มต้นของ Serilog ไม่แสดง property) — ยังแก้ได้จาก config เหมือนเดิม
3. **Subject ของเมลถึงลูกค้าที่เป็น CONTRACT_RENEWAL** ใช้กลุ่ม `CONTRACT*` ตาม NotificationType
   (ไม่ใช่ `CUSTOMER_EXPIRED`) เพื่อให้สอดคล้องกับ template ที่ใช้ซึ่งเป็นเนื้อหาเรื่องต่อสัญญา
4. **key เสริมที่ไม่ได้อยู่ในสเปก** — ไม่ต้องใส่ใน `appsettings.json` ก็ได้ ถ้าไม่ใส่จะใช้ค่า default
   | Key | Default | ใช้ทำอะไร |
   |---|---|---|
   | `Job:AckTokenValidDays` | 90 | อายุของ `AckTokenExpireAt` |
   | `Job:CertificateErrorWarningRatio` | 0.5 | สัดส่วน error ต่อรอบที่เกินแล้วให้ log Warning ว่าอาจเป็นปัญหาเชิงระบบ |
   | `MailApi:RetryBaseDelaySeconds` | 2 | ฐานของ exponential backoff ของ Mail API |
   | `SmtpFallback:RetryCount` | 2 | จำนวน retry ของช่องทาง SMTP |
