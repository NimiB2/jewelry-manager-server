# העלאה ל-Render — מדריך צעד אחר צעד

המערכת עולה כשני חלקים: **השרת + מסד הנתונים** (repo `jewelry-manager-server`) ו**האתר** (repo `jewelry-manager-client`).
הקבצים `render.yaml` בכל repo מתארים מה להקים, כך שלא צריך לבנות שום דבר ידנית.

> אף סוד לא נכנס ל-git. כל מפתח מוזן פעם אחת בלוח הבקרה של Render, בשדה שהוא מבקש.

## 0. לפני שמתחילים
- חשבון ב-[render.com](https://render.com) (הרשמה עם GitHub הכי נוחה), ואמצעי תשלום.
- לוודא ש-4 הדברים האלה מוכנים אצלך:
  1. הקובץ `firebase-adminsdk.json` (חשבון השירות של Firebase).
  2. מפתח ה-OpenAI ומפתח ה-Anthropic.
  3. ערכי `VITE_FIREBASE_*` של האתר (מקובץ `.env.local` בתיקיית `client`).
  4. כתובת המייל של Google שאיתה אתה נכנס (תהיה הבעלים).

## 1. השרת והמסד
1. ב-Render: **New → Blueprint**.
2. לבחור את ה-repo `jewelry-manager-server`. Render יקרא את `render.yaml` ויציג: שרת (`jewelry-api`), מסד (`jewelry-db`) ודיסק (`invoices`).
3. הוא יבקש ערכים לשדות הבאים:

| שדה | מה לשים |
|---|---|
| `Firebase__CredentialJson` | **כל התוכן** של `firebase-adminsdk.json` (פתח בעורך טקסט, העתק הכול, הדבק) |
| `Seed__OwnerEmail` | המייל של Google שלך |
| `InvoiceReader__ApiKey` | מפתח OpenAI |
| `InvoiceReader__Fallback__ApiKey` | מפתח Anthropic |
| `Cors__ClientOrigin` | משאירים ריק כרגע, ממלאים בשלב 3 |

4. **Apply**. ההקמה הראשונה לוקחת כמה דקות.
5. כשזה "Live", לפתוח `https://<כתובת-השרת>/health`. אמור להופיע `{"status":"ok"}`.

> אם Render מתלונן על שם תוכנית (`starter` / `basic-256mb`), לבחור תוכנית מהרשימה בלוח הבקרה. השמות עלולים להשתנות.

## 2. האתר
1. **New → Blueprint** ולבחור את `jewelry-manager-client`.
2. למלא:

| שדה | מה לשים |
|---|---|
| `VITE_API_URL` | כתובת השרת מהשלב הקודם, בלי `/` בסוף |
| `VITE_FIREBASE_*` (6 שדות) | מאותם ערכים שב-`.env.local` |

3. אחרי שהבנייה מסתיימת, מקבלים כתובת של האתר.

## 3. לחבר בין השניים
1. בשרת: להגדיר `Cors__ClientOrigin` = כתובת האתר (למשל `https://jewelry-client.onrender.com`, בלי `/` בסוף). השרת יופעל מחדש לבד.
2. ב-[Firebase Console](https://console.firebase.google.com): **Authentication → Settings → Authorized domains → Add domain**, ולהוסיף את כתובת האתר (בלי `https://`). בלי זה הכניסה עם Google תיכשל.

## 4. לבדוק
1. לפתוח את האתר ולהיכנס עם Google.
2. להוסיף הוצאה ולצרף חשבונית, ולראות שהטופס מתמלא.
3. ביומן השרת ב-Render (Logs) אמורה להופיע השורה `Invoice read by openai/gpt-6-luna`.

## 5. הנתונים הקיימים
המסד בענן מתחיל **ריק** (עם הגדרות ברירת מחדל ובעלים אחד). הנתונים שלך עדיין על המחשב. את העברתם עושים בנפרד ובזהירות (גיבוי מקומי, ייצוא וייבוא), ולא נוגעים בזה לפני שמוודאים שהכול עובד.
