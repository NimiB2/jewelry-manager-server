# חיבור Shopify — הגדרה חד-פעמית

המערכת **קוראת בלבד** מהחנות: מוצרים (לקישור) והזמנות חדשות (ל-webhook). היא לא כותבת לחנות ולא נוגעת בלקוחות או בתשלומים.

> שמות התפריטים ב-Shopify משתנים מדי פעם. אם משהו לא נראה כמו שכתוב כאן, מחפשים את הרעיון (אפליקציה מותאמת, הרשאות קריאה, Webhooks) ולא את השם המדויק.

## 1. אפליקציה מותאמת בחנות
1. ב-Shopify Admin: **Settings ← Apps and sales channels ← Develop apps** (או Dev Dashboard, לפי מה שמוצג).
2. **Create an app**, שם למשל `Jewelry Manager`.
3. **Configure Admin API scopes**: לסמן **רק**:
   - `read_products`
   - `read_orders`
4. **Install app**.
5. לשמור שני ערכים (**Admin API access token** מוצג פעם אחת בלבד, אל תשלח אותו בצ'אט ואל תשמור אותו בקובץ):
   - **Admin API access token** (מתחיל ב-`shpat_`)
   - **API secret key** (משמש לחתימת ה-webhook)

## 2. משתני סביבה ב-Render
**jewelry-api ← Environment**, שלושה ערכים חדשים:

| שם | ערך |
|---|---|
| `Shopify__ShopDomain` | כתובת החנות, למשל `my-shop.myshopify.com` (בלי `https://`) |
| `Shopify__AccessToken` | ה-Admin API access token |
| `Shopify__WebhookSecret` | ה-API secret key |

Save Changes, והשרת יופעל מחדש.

## 3. ייבוא המוצרים
1. באתר: **מוצרים ← ייבוא מהחנות**.
2. "קישור כל ההתאמות הברורות", ואז עוברים על השאר: בחירה מהרשימה, או "הוספה כמוצר חדש".
3. מוצר שנוסף נשמר עם שם ומחיר בלבד ועם התג "חסרים פרטים". משלימים סוג וחומר כשנוח.

## 4. ה-webhook של הזמנות חדשות
ב-Shopify Admin: **Settings ← Notifications ← Webhooks ← Create webhook**:

| שדה | ערך |
|---|---|
| Event | **Order creation** |
| Format | **JSON** |
| URL | `https://<כתובת-השרת>/webhooks/shopify/orders` |

## 5. בדיקה
1. מבצעים הזמנת ניסיון בחנות.
2. תוך שניות היא מופיעה בראש מסך **הזמנות**, תחת "ממתינות לאישור".
3. בוחרים מוצר לכל שורה שלא זוהתה, ומאשרים.

## אם ההזמנה לא מגיעה
ביומן השרת ב-Render (Logs) מחפשים שורות `Shopify`:

| השורה | משמעות |
|---|---|
| `Shopify webhook rejected: bad signature` | `Shopify__WebhookSecret` שגוי. לנסות את הסוד השני שמוצג ב-Shopify (סוד החתימה של ה-webhook או ה-API secret key של האפליקציה) |
| `Shopify webhook rejected: unexpected shop` | `Shopify__ShopDomain` לא תואם לחנות ששלחה |
| `Shopify order #...: Created` | ההזמנה התקבלה |
| `Shopify order #...: Duplicate` | כבר התקבלה (Shopify שלחה שוב) |
| `Shopify webhook ignored` | ההודעה חתומה אבל אינה הזמנה תקינה |
