namespace JewelryManager.Mcp;

/// <summary>Words that usually mark a task as big or vague, so a breakdown is offered early.</summary>
public static class TaskHeuristics
{
    private static readonly string[] BigWords =
    [
        "לסדר", "לארגן", "להתחיל", "לבנות", "קמפיין", "האתר", "לתכנן", "לשפר", "לעדכן את כל", "את כל ה", "לעשות סדר",
        "להקים", "לצלם את", "לעבור על", "לכתוב את", "קולקציה", "מיתוג", "לשדרג",
    ];

    public static bool LooksBig(string title) =>
        title.Length >= 50 || BigWords.Any(w => title.Contains(w, StringComparison.Ordinal));
}
