using System.Globalization;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Orders;

/// <summary>
/// The message a customer gets right after she orders: what she ordered, when, and what she paid, plus
/// the terms. It is built from the order, so it can be sent from the app, by email or by any other
/// channel later without being written twice.
/// </summary>
public static class CustomerMessageTemplate
{
    // A deposit is a part of the price. Paying at least this share of the catalog price counts as paying it
    // all (a sale or a coupon is not a deposit), so the message does not call it a deposit.
    private const decimal FullPaymentShare = 0.8m;

    public static string Build(Order order)
    {
        var items = string.Join(", ", order.Items.OrderBy(i => i.SortOrder).Select(Describe));
        return Build(order.Customer, items, order.Date, order.FinalAmount, PaidInFull(order));
    }

    // Only when every line is tied to a catalog price and was paid at (about) that price. When it is not
    // certain, the wording stays "deposit", the careful one.
    public static bool PaidInFull(Order order) =>
        order.Items.Count > 0
        && order.Items.All(i => i.ListPrice is > 0 && i.UnitPrice >= i.ListPrice * FullPaymentShare);

    public static string Build(string? customer, string items, DateOnly date, decimal paid, bool paidInFull = false)
    {
        var paidLabel = paidInFull ? "סכום ששולם" : "מקדמה ששולמה";
        var name = string.IsNullOrWhiteSpace(customer) ? "—" : customer.Trim();
        var when = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var amount = paid.ToString("#,##0.##", CultureInfo.InvariantCulture);

        return $"""
            היי🤍
            איזה כיף, ההזמנה שלך התקבלה!

            פרטי ההזמנה:
            • שם המזמין/ה: {name}
            • התכשיט: {items}
            • תאריך ההזמנה: {when}
            • {paidLabel}: {amount} ₪

            התכשיט שלך מיוצר במיוחד עבורך ויהיה מוכן תוך עד 21 ימי עסקים ממועד ההזמנה. כמובן שאעדכן אותך ברגע שהוא מוכן ✨

            כמה דברים שחשוב לדעת:
            • על התכשיט ניתנת אחריות לשנה מיום קבלתו.
            • האחריות אינה כוללת נזק שנגרם כתוצאה משימוש בלתי סביר בתכשיט או במקרה של אובדן.
            • תכשיט שיוצר בהזמנה אישית אינו ניתן להחזרה או להחלפה.

            אם יש לך שאלה, בקשה או כל דבר נוסף בדרך- אני תמיד כאן 🤍
            תודה שבחרת בי להכין עבורך משהו מיוחד.
            """;
    }

    // "Ring Name (48) ×2": the variant (size) in brackets, the quantity only when it is more than one.
    private static string Describe(OrderLineItem line)
    {
        var text = string.IsNullOrWhiteSpace(line.Note) ? line.Name : $"{line.Name} ({line.Note.Trim()})";
        return line.Quantity > 1 ? $"{text} ×{line.Quantity}" : text;
    }

    /// <summary>
    /// The number the way a WhatsApp link needs it: digits only, with the country code. A local Israeli
    /// number (05x...) gets 972. Anything that does not look like a phone number gives null.
    /// </summary>
    public static string? WhatsAppNumber(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var trimmed = phone.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        if (digits.StartsWith("00")) digits = digits[2..];
        else if (trimmed.StartsWith('+')) { /* already international */ }
        else if (digits.StartsWith('0')) digits = "972" + digits[1..];

        return digits.Length is >= 9 and <= 15 ? digits : null;
    }
}
