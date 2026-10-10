# גיבוי המסד — הגדרה ושחזור

כל לילה GitHub מריץ משימה (`.github/workflows/db-backup.yml`) שמושכת עותק של המסד מ-Render, **מצפינה אותו**
ושולחת אותו ל-Backblaze B2. כלום לא נשמר ב-GitHub. כל הגיבויים נשמרים ולא נמחקים.

## הגדרה חד-פעמית

### 1. חשבון ו-Bucket ב-Backblaze
1. הרשמה ב-[backblaze.com](https://www.backblaze.com) (בחר **B2 Cloud Storage**).
2. **Buckets ← Create a Bucket**: שם ייחודי, למשל `jewelry-db-backups-<משהו-ייחודי>`, **Private**, ובלי הצפנה של Backblaze (אנחנו מצפינים בעצמנו).
3. **Application Keys ← Add a New Application Key**: להגביל ל-Bucket הזה, הרשאת **Read and Write**.
4. לשמור מיד (מוצגים פעם אחת): **keyID** ו-**applicationKey**.
5. בעמוד ה-Buckets לראות את ה-**Endpoint** של ה-Bucket, בצורה `s3.us-west-004.backblazeb2.com`.

### 2. כתובת חיצונית של המסד ב-Render
**jewelry-db ← Connect ← External** ← להעתיק את **External Database URL**.

### 3. סודות ב-GitHub
ב-repo `jewelry-manager-server`: **Settings ← Secrets and variables ← Actions ← New repository secret**. שישה סודות:

| שם | ערך |
|---|---|
| `DATABASE_URL` | ה-External Database URL מ-Render |
| `BACKUP_PASSPHRASE` | סיסמה חזקה שאתה בוחר להצפנה. **לשמור במקום בטוח, בלעדיה הגיבוי לא נפתח** |
| `B2_KEY_ID` | ה-keyID |
| `B2_APP_KEY` | ה-applicationKey |
| `B2_BUCKET` | שם ה-Bucket |
| `B2_ENDPOINT` | `https://` + ה-Endpoint, למשל `https://s3.us-west-004.backblazeb2.com` |

### 4. הרצה ראשונה ידנית
**Actions ← Database backup ← Run workflow**. אחרי כדקה אמור להופיע וי ירוק, וקובץ `jewelry-db-....dump.gpg` ב-Bucket.

## שחזור
1. להוריד את הקובץ הרצוי מ-Backblaze.
2. לפתוח את ההצפנה (יבקש את הסיסמה):
   ```
   gpg --output backup.dump --decrypt jewelry-db-2026-10-10_0230.dump.gpg
   ```
3. לטעון למסד **ריק או חדש** (לא למסד חי שבו יש נתונים, אלא אם רוצים להחליף אותם):
   ```
   pg_restore --clean --if-exists --no-owner --dbname "<כתובת המסד>" backup.dump
   ```

## חשוב
- **לבדוק שחזור אחת לכמה זמן.** גיבוי שלא נבדק הוא לא גיבוי.
- **קבצי החשבוניות** (הדיסק של Render) **לא נכללים** בגיבוי הזה, רק המסד.
- אם המשימה נכשלת, GitHub שולח מייל לבעל ה-repo.
