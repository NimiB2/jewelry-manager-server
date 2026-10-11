using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Orders;
using JewelryManager.Api.Features.Shopify;

namespace JewelryManager.Tools;

public record OrderHistoryResult(
    int Created,
    int AlreadyThere,
    int Completed,
    int Open,
    int MatchedLines,
    int UnmatchedLines,
    List<string> UnmatchedTitles,
    List<SkippedStoreOrder> Skipped,
    List<string> Failed);

/// <summary>
/// Brings the store's old orders (the "Export orders" CSV) into the system through the same order service
/// the app uses, so they get numbers, income and (for the ones still to make) a task like any other order.
/// Re-running is safe: an order that is already here is skipped.
/// </summary>
public class OrderHistoryImporter(AppDbContext db, Guid businessId)
{
    public async Task<OrderHistoryResult> ImportAsync(string csvPath, bool dryRun)
    {
        var csv = ShopifyOrdersCsvReader.Read(await File.ReadAllTextAsync(csvPath));
        var orders = new OrdersService(db, ProductImporter.TenantFor(businessId));

        int created = 0, already = 0, completed = 0, open = 0, matched = 0, unmatched = 0;
        var titles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();

        foreach (var order in csv.Orders)
        {
            try
            {
                var outcome = await orders.ImportHistoricalOrderAsync(order, dryRun);
                if (!outcome.Created)
                {
                    already++;
                    continue;
                }

                created++;
                if (order.Fulfilled) completed++;
                else open++;

                matched += outcome.MatchedLines;
                unmatched += outcome.UnmatchedTitles.Count;
                foreach (var title in outcome.UnmatchedTitles) titles.Add(title);
            }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                failed.Add($"{order.DisplayName} — {ex.Message}");
            }
        }

        return new OrderHistoryResult(created, already, completed, open, matched, unmatched, [.. titles], csv.Skipped, failed);
    }
}
