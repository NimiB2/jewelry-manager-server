using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Common.Exceptions;

namespace JewelryManager.Api.Features.RiseUp;

/// <summary>One RiseUp transaction, with only the fields the statement checklist needs.</summary>
public record RiseUpTransaction(
    string TransactionId,
    DateOnly Date,
    decimal Amount,
    bool IsIncome,
    string BusinessName,
    string SourceType,
    string? AccountNickname,
    bool IsExcluded,
    int? InstallmentNumber,
    int? TotalInstallments);

/// <summary>Reads the transactions answer. Pure, so it is tested without the network.</summary>
public static class RiseUpTransactionParser
{
    public static List<RiseUpTransaction> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            throw new BadRequestException("RiseUp החזירה תשובה בפורמט לא מוכר");

        var result = new List<RiseUpTransaction>();
        foreach (var node in transactions.EnumerateArray())
        {
            // A row without an id or a date cannot be de-duplicated or placed in a month, so it is left out.
            var id = ReadString(node, "transactionId");
            var date = ReadDate(node, "transactionDate");
            if (string.IsNullOrEmpty(id) || date is null) continue;
            if (!node.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number) continue;

            var isInstallment = ReadBool(node, "isInstallment");
            result.Add(new RiseUpTransaction(
                id,
                date.Value,
                amount.GetDecimal(),
                ReadBool(node, "isIncome"),
                ReadString(node, "businessName") ?? "",
                ReadString(node, "sourceType") ?? "",
                ReadString(node, "accountNickname"),
                ReadBool(node, "isExcluded"),
                isInstallment ? ReadInt(node, "installmentNumber") : null,
                isInstallment ? ReadInt(node, "totalNumberOfInstallments") : null));
        }

        return result;
    }

    private static string? ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBool(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? ReadInt(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    // RiseUp sends UTC midnight, so the date part is the transaction day.
    private static DateOnly? ReadDate(JsonElement node, string name) =>
        DateTimeOffset.TryParse(ReadString(node, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? DateOnly.FromDateTime(at.UtcDateTime)
            : null;
}
