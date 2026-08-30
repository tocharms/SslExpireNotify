# SslExpireNotify — คู่มือติดตั้ง (Deployment Guide)

แพ็กเกจนี้ประกอบด้วย

```
SslExpireNotify-v<version>.msi     ตัวติดตั้ง Windows Service
database/schema.sql                สร้างตารางของระบบนี้ (รันซ้ำได้)
database/seed.sql                  ข้อมูลทดสอบ — สำหรับ test DB เท่านั้น
README-DEPLOY.md                   ไฟล์นี้
```

> # ⚠️ ห้ามรัน `seed.sql` บนเครื่อง production เด็ดขาด
>
> `seed.sql` มีคำสั่ง `DELETE` ที่ล้างตาราง **CUSTOMER**, **KSC_USERS** และ **SSL_Certificate**
> ซึ่งเป็นตารางของระบบอื่นและมีข้อมูลลูกค้าจริง ใช้ได้เฉพาะฐานข้อมูลทดสอบเท่านั้น
> (สคริปต์มีตัวกันไว้ชั้นหนึ่ง: ต้องแก้ `@ConfirmTestDatabase = 1` ก่อนถึงจะทำงาน — อย่าแก้บน production)

---

## 1. ติดตั้งครั้งแรก

### 1.1 เตรียมฐานข้อมูล

รัน `database/schema.sql` บน SQL Server ที่เก็บตาราง `SSL_Certificate`

```powershell
sqlcmd -S SQLSERVER01 -d KSC_SSL -E -i .\database\schema.sql
```

สคริปต์จะ
- เพิ่ม `PRIMARY KEY` ให้ `SSL_Certificate.SSL_Cert_ID` ถ้ายังไม่มี (ตรวจก่อนเสมอ)
- สร้าง `CertificateAlert`, `EmailLog`, `JobRunHistory` พร้อม index
- ข้ามทุกอย่างที่มีอยู่แล้ว — รันซ้ำได้ปลอดภัย

**สิทธิ์ของ user ที่ service ใช้** ต้องมีอย่างน้อย
- `SELECT` บน `SSL_Certificate`, `CUSTOMER`, `KSC_USERS` (อ่านอย่างเดียว — service ไม่เขียนตารางเหล่านี้)
- `SELECT/INSERT/UPDATE/DELETE` บน `CertificateAlert`, `EmailLog`, `JobRunHistory`
- `EXECUTE` บน `sp_getapplock` / `sp_releaseapplock` (สิทธิ์ `public` มีอยู่แล้วตามปกติ)

### 1.2 ติดตั้ง MSI

```powershell
msiexec /i SslExpireNotify-v1.0.0.msi
```

- ติดตั้งที่ `C:\Program Files\KSC\SslExpireNotify\` (เปลี่ยนได้ในหน้าจอ Setup)
- ลงทะเบียน service ชื่อ `SslExpireNotify` (display name: *KSC SSL Expire Notify*)
- ตั้งเป็น **delayed auto-start**, account `LocalSystem`, restart อัตโนมัติ 60 วินาทีหลังล้มเหลว
- **ไม่ start service ให้อัตโนมัติ** — ต้องแก้ config ก่อน

### 1.3 แก้ `appsettings.json`

เปิด `C:\Program Files\KSC\SslExpireNotify\appsettings.json` แล้วแก้อย่างน้อย

| Key | ต้องแก้เป็น |
|---|---|
| `ConnectionStrings:SslNotifyDb` | connection string จริงของ SQL Server |
| `MailApi:Url` | URL ของ KSC Mail API |
| `MailApi:AllowInvalidCertificate` | `false` บน production (ถ้าเปิดไว้ service จะ log warning ทุกครั้งที่ start) |
| `Recipients:Cc` | อีเมลที่ต้องได้รับสำเนาทุกฉบับ (ว่าง = ไม่มี Cc) |
| `SmtpFallback:*` | host/port/บัญชีของ SMTP relay สำรอง หรือ `Enabled: false` ถ้าไม่ใช้ |
| `AckBaseUrl` | URL ของหน้า acknowledge (ลิงก์ในปุ่มของอีเมล) |

> **ก่อนเปิด SMTP fallback**: ยืนยันกับทีม infra ว่า `SmtpFallback:Host` เป็น relay ที่ได้รับอนุญาตให้ส่งในนามโดเมน `ksc.net` (SPF/DKIM) มิฉะนั้นเมลที่ fallback ไปทาง SMTP อาจถูกปลายทางจัดเป็น spam

### 1.4 ทดสอบด้วย DryRun ก่อนเปิดใช้จริง

ตั้งค่าใน `appsettings.json`

```json
"Job": { "RunOnStartup": true, "DryRun": true }
```

```powershell
Start-Service SslExpireNotify
Get-Content 'C:\Program Files\KSC\SslExpireNotify\logs\ssl-notify-*.log' -Tail 100
```

ใน log จะเห็นบรรทัด `DRY RUN: would send to ... subject ...` ครบทุกฉบับ
**ระหว่าง DryRun ระบบไม่เขียน `CertificateAlert` / `EmailLog` เลย** (มีแต่แถวใน `JobRunHistory` ที่ `IsDryRun = 1`)

เมื่อผลลัพธ์ถูกต้องแล้ว

```json
"Job": { "RunOnStartup": false, "DryRun": false }
```

```powershell
Restart-Service SslExpireNotify
```

จากนี้ job จะรันเองทุกวันเวลา **00:30** ตาม `Job:TimeZoneId` (`SE Asia Standard Time`)

---

## 2. อัปเกรดเป็นเวอร์ชันใหม่

```powershell
msiexec /i SslExpireNotify-v1.1.0.msi
```

MSI จะ stop service → แทนที่ไฟล์ → **ไม่ start ให้อัตโนมัติ**

1. ตรวจ `appsettings.default.json` (ไฟล์ config ของเวอร์ชันใหม่ที่วางไว้ข้าง ๆ) ว่ามี key ใหม่ที่ต้อง merge เข้า `appsettings.json` หรือไม่
   ```powershell
   cd 'C:\Program Files\KSC\SslExpireNotify'
   Compare-Object (Get-Content appsettings.json) (Get-Content appsettings.default.json)
   ```
2. `Start-Service SslExpireNotify`

สิ่งที่ **ไม่ถูกแตะ** ตอน upgrade
- `appsettings.json` (ตั้ง `NeverOverwrite` ไว้ — ค่าที่แก้ไว้จะคงอยู่เสมอ)
- โฟลเดอร์ `logs\` และไฟล์ log ทั้งหมด

---

## 3. ถอนการติดตั้ง

ผ่าน **Add/Remove Programs** หรือ

```powershell
msiexec /x SslExpireNotify-v1.0.0.msi /qn
```

- stop และลบ service ให้เรียบร้อย
- **คง `appsettings.json` และโฟลเดอร์ `logs\` ไว้** (ไม่ลบ config/log ของ production ทิ้ง)
- ตารางในฐานข้อมูลไม่ถูกลบ — ถ้าต้องการล้างจริง ให้ drop เองด้วยมือ

---

## 4. ติดตั้งแบบ silent (หลายเครื่อง)

```powershell
# ติดตั้ง path default
msiexec /i SslExpireNotify-v1.0.0.msi /qn

# ติดตั้ง path อื่น + เก็บ log ของ msiexec
msiexec /i SslExpireNotify-v1.0.0.msi /qn INSTALLFOLDER="D:\Services\SslExpireNotify" /l*v install.log

# ถอนการติดตั้ง
msiexec /x SslExpireNotify-v1.0.0.msi /qn
```

หลัง silent install ยังต้องแก้ `appsettings.json` แล้ว `Start-Service SslExpireNotify` เองเสมอ

---

## 5. ตรวจ log และแก้ปัญหา

### Log อยู่ที่ไหน

```
<installfolder>\logs\ssl-notify-YYYYMMDD.log     (rolling รายวัน เก็บ 30 ไฟล์)
```

ทุกบรรทัดของการรันรอบเดียวกันมี `RunId` เดียวกัน ใช้ไล่ย้อนได้ทั้งรอบ

```powershell
Select-String -Path .\logs\ssl-notify-*.log -Pattern '9f2c...' | Select-Object -First 50
```

### ตรวจว่า job รันจริงไหม

```sql
SELECT TOP 20 RunId, StartedAt, FinishedAt, Status, CertificatesScanned,
       AlertsCreated, EmailsSent, EmailsFailed, EmailsSentViaFallback, IsDryRun, ErrorSummary
FROM JobRunHistory
ORDER BY StartedAt DESC;
```

- ไม่มีแถวใหม่ในรอบ 24 ชม. = service ไม่ทำงาน หรือ cron ผิด
- `EmailsSentViaFallback > 0` = KSC Mail API มีปัญหา ถึงเมลจะส่งออกได้ก็ควรตรวจสอบ
- `Status = 'Failed'` → ดู `ErrorSummary`

### service ไม่ start

| อาการใน log / Event Viewer | สาเหตุที่พบบ่อย |
|---|---|
| `SslExpireNotify could not start` + ข้อความ validation | config ผิด — ข้อความบอกชัดว่า key ไหน เช่น `Job:AlertLevels is inconsistent...`, `MailApi:Url must not be empty`, `AckBaseUrl must not be empty` |
| `Job:TimeZoneId '...' is not a time zone known to this machine` | สะกด time zone ผิด (ต้องเป็น `SE Asia Standard Time`) |
| service start แล้วดับทันที ไม่มี log ในโฟลเดอร์ | ไม่มีสิทธิ์เขียน `logs\` — ถ้าเปลี่ยน service account จาก `LocalSystem` ต้องให้สิทธิ์ Modify บนโฟลเดอร์ติดตั้งเอง |
| `Login failed for user ...` | connection string / สิทธิ์ SQL ไม่ถูก |

ดู error ระดับ service เพิ่มเติมได้ที่ Event Viewer → Windows Logs → Application

### job ไม่ส่งเมลเลย

1. `Another instance holds the job lock` → มี service อีกตัวรันอยู่ (เช่น เครื่องเก่ายังไม่ถูกถอน) — ต้องเหลือ instance เดียว
2. `no recipient` ใน `EmailLog.ErrorMessage` → `KSC_USERS.EMAIL` ของ Sales ว่างหรือผิด format แก้ข้อมูลแล้วรอบถัดไปจะส่งเอง (`LastNotifiedAt` ยังเป็น NULL)
3. `no customer email` → `SSL_Certificate.EmailAlert` ผิด format — กระทบเฉพาะเมลถึงลูกค้า เมลถึง Sales ยังส่งปกติ
4. ไม่มี alert ใหม่เลย → ตรวจ `Job:ActiveSslStatusValues` (ค่า default `[1]` = เฉพาะ Active)

### สั่งให้รันทันทีเพื่อทดสอบ

ตั้ง `Job:RunOnStartup = true` (แนะนำให้คู่กับ `DryRun = true`) แล้ว `Restart-Service SslExpireNotify`
อย่าลืมตั้งกลับเป็น `false` หลังทดสอบเสร็จ
