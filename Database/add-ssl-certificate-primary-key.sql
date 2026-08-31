/* =============================================================================
   SslExpireNotify - add a PRIMARY KEY to SSL_Certificate.SSL_Cert_ID

   สำหรับส่งให้ DBA / เจ้าของระบบเดิมที่ดูแลตาราง SSL_Certificate โดยเฉพาะ
   (แยกออกมาจาก Database/schema.sql เพื่อให้ทีมที่ดูแลตารางนี้ตรวจสอบ/อนุมัติ
   ได้เฉพาะส่วนที่แตะตารางของตัวเอง โดยไม่ต้องเกี่ยวกับตารางใหม่ที่ SslExpireNotify
   สร้างเพิ่ม)

   ทำอะไร
     * ตรวจสอบว่า SSL_Certificate มี PRIMARY KEY อยู่แล้วหรือยัง
     * ถ้ายังไม่มี -> เพิ่ม PRIMARY KEY บนคอลัมน์ SSL_Cert_ID (ชื่อ constraint
       PK_SSL_Certificate)
     * ถ้ามีอยู่แล้ว -> ไม่ทำอะไรเลย (idempotent, รันซ้ำได้ปลอดภัย)

   ทำไมต้องมี
     SslExpireNotify ต้องสร้างตาราง CertificateAlert ที่มี FOREIGN KEY อ้างอิงกลับ
     ไปที่ SSL_Certificate.SSL_Cert_ID ซึ่ง SQL Server กำหนดว่าคอลัมน์ปลายทางของ
     FOREIGN KEY ต้องมี UNIQUE หรือ PRIMARY KEY constraint อยู่ก่อน

   ผลกระทบต่อข้อมูล
     * ไม่มีการ INSERT / UPDATE / DELETE แถวใด ๆ ทั้งสิ้น
     * ไม่แตะคอลัมน์อื่นนอกจาก SSL_Cert_ID
     * ถ้าคอลัมน์ SSL_Cert_ID ยังเป็น NULL ได้อยู่ (ไม่ควรจะเป็นแบบนั้นในทางปฏิบัติ
       เพราะเป็น ID) จะถูกเปลี่ยนเป็น NOT NULL ก่อน เพื่อให้ใส่ PRIMARY KEY ได้
       ตาม constraint ของ SQL Server เอง (PRIMARY KEY ต้อง NOT NULL) — ถ้ามีแถวที่
       SSL_Cert_ID เป็น NULL อยู่จริง ขั้นตอนนี้จะ error ทันที (ดูหัวข้อ "ก่อนรัน" ด้านล่าง)
     * ไม่ล็อกตารางนานเกินสมควรสำหรับตารางขนาดทั่วไป — ALTER TABLE ADD CONSTRAINT
       PRIMARY KEY ต้องสแกนทั้งตารางเพื่อสร้าง clustered/nonclustered index ใหม่
       (ตามค่า default ของ SQL Server จะเป็น clustered ถ้ายังไม่มี clustered index
       อยู่ก่อน) แนะนำให้รันนอกช่วง peak traffic หรือใน maintenance window

   ก่อนรัน (แนะนำให้ DBA รันเช็คนี้ก่อนเสมอ)
     ตรวจสอบว่ามีค่า SSL_Cert_ID ซ้ำกัน หรือเป็น NULL หรือไม่ (ถ้ามี ต้องแก้ข้อมูล
     ก่อน ไม่งั้น ALTER TABLE ด้านล่างจะ error):

         SELECT SSL_Cert_ID, COUNT(*) AS Occurrences
         FROM dbo.SSL_Certificate
         GROUP BY SSL_Cert_ID
         HAVING COUNT(*) > 1 OR SSL_Cert_ID IS NULL;

     ถ้า query ข้างบนไม่คืนแถวใดเลย แปลว่าปลอดภัยที่จะรันสคริปต์นี้ต่อ

   รันซ้ำได้ไหม
     ได้ — รันกี่ครั้งก็ได้ ถ้ามี PRIMARY KEY อยู่แล้วจะข้ามไปเฉย ๆ ไม่ error

   Rollback (ถ้าต้องการถอนออกภายหลัง)
         ALTER TABLE dbo.SSL_Certificate DROP CONSTRAINT PK_SSL_Certificate;
   ============================================================================= */

SET NOCOUNT ON;
GO

-- sqlcmd scripting directive: stop the whole script on the first error, so the
-- RAISERROR guard below actually halts execution instead of just printing and
-- falling through to the ALTER TABLE statements that depend on it.
:on error exit
GO

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
    PRINT N'dbo.SSL_Certificate already has a primary key on SSL_Cert_ID - nothing to do.';
END
GO

/* -----------------------------------------------------------------------------
   ตรวจผลหลังรัน
   -------------------------------------------------------------------------- */
SELECT
    kc.name  AS ConstraintName,
    ic.index_column_id,
    c.name   AS ColumnName
FROM sys.key_constraints kc
JOIN sys.index_columns ic
    ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
JOIN sys.columns c
    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SSL_Certificate')
  AND kc.type = 'PK';
GO
