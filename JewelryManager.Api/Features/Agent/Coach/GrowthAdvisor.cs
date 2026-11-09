using JewelryManager.Api.Features.Analytics;

namespace JewelryManager.Api.Features.Agent.Coach;

public static class FocusArea
{
    public const string Advertising = "advertising";
    public const string NewJewelry = "new_jewelry";
    public const string Website = "website";
    public const string Customers = "customers";
    public const string Admin = "admin";
    public const string Occasion = "occasion";

    public static string Label(string area) => area switch
    {
        Advertising => "פרסום ותוכן",
        NewJewelry => "תכשיטים חדשים",
        Website => "האתר והחנות",
        Customers => "לקוחות",
        Admin => "סדר וניהול",
        Occasion => "הכנה למועד",
        _ => area,
    };
}

public record FocusOption(string Area, string Reason, string FirstStep, int Score);

/// <summary>
/// Chooses what the business should concentrate on from signals in her own numbers, never from a hunch.
/// Each option carries its reason and one small first step, so a suggestion can be judged and acted on.
/// </summary>
public static class GrowthAdvisor
{
    public static List<FocusOption> Options(GrowthSnapshot s, int adminOpen, (string Name, int Days)? nextOccasion, IReadOnlySet<string>? sleeping = null)
    {
        var options = new List<FocusOption>();

        if (nextOccasion is { Days: <= 56 } occasion)
            options.Add(new FocusOption(FocusArea.Occasion, $"בעוד {occasion.Days} ימים: {occasion.Name}", "להחליט אם לעשות מבצע ובאיזה מוצרים", 90));

        if (s.OrdersPrior30DayAverage >= 3 && s.OrdersLast30Days < s.OrdersPrior30DayAverage * 0.75m)
            options.Add(new FocusOption(FocusArea.Advertising,
                $"ב-30 הימים האחרונים היו {s.OrdersLast30Days} הזמנות, לעומת ממוצע של {s.OrdersPrior30DayAverage:0.#} קודם",
                "לפרסם פוסט אחד עם המוצר הנמכר ביותר", 80));

        if (s.DaysSinceLastProductAdded is >= 60)
            options.Add(new FocusOption(FocusArea.NewJewelry, $"לא נוסף מוצר חדש כבר {s.DaysSinceLastProductAdded} ימים", "לבחור רעיון אחד למוצר חדש ולשרטט אותו", 70));

        if (s.ProductsNeedingDetails > 0)
            options.Add(new FocusOption(FocusArea.Website, $"ל-{s.ProductsNeedingDetails} מוצרים חסרים פרטים (סוג או חומר)", "להשלים פרטים לשלושה מוצרים", 60));

        if (adminOpen > 0)
            options.Add(new FocusOption(FocusArea.Admin, $"יש {adminOpen} דברים פתוחים בניהול (קבלות, התאמות)", "לטפל בשלוש הוצאות בלי קבלה", 55));

        if (s.RepeatCustomerRate < 0.15m && s.OrdersLast30Days + s.OrdersPrior30DayAverage * 3 >= 10)
            options.Add(new FocusOption(FocusArea.Customers, $"רק {s.RepeatCustomerRate * 100:0}% מהלקוחות קנו יותר מפעם אחת", "לשלוח הודעת תודה ללקוחה אחת מהחודש האחרון", 50));

        var result = options.Where(o => sleeping is null || !sleeping.Contains(o.Area)).OrderByDescending(o => o.Score).Take(3).ToList();

        // Always give a real choice of three, even in a quiet period.
        foreach (var fallback in Defaults)
        {
            if (result.Count >= 3) break;
            if (result.All(o => o.Area != fallback.Area) && (sleeping is null || !sleeping.Contains(fallback.Area))) result.Add(fallback);
        }

        return result;
    }

    private static readonly FocusOption[] Defaults =
    [
        new(FocusArea.Advertising, "אין סימן מיוחד; פרסום קבוע הוא מה שמחזיק מכירות", "לתכנן שלושה פוסטים לשבוע", 10),
        new(FocusArea.NewJewelry, "אין סימן מיוחד; חידוש קטן שומר על עניין", "לרשום שלושה רעיונות לתכשיט חדש", 9),
        new(FocusArea.Customers, "אין סימן מיוחד; קשר עם לקוחות קודמות מחזיר מכירות", "לשלוח הודעה קצרה ללקוחה אחת", 8),
        new(FocusArea.Website, "אין סימן מיוחד; חנות מסודרת מוכרת טוב יותר", "לעבור על דף מוצר אחד ולשפר את התיאור והתמונה", 7),
        new(FocusArea.Admin, "אין סימן מיוחד; סדר בניהול חוסך זמן ודאגות", "לסגור שלושה דברים פתוחים בניהול", 6),
    ];
}
