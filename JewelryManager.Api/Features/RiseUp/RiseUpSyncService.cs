using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Features.RiseUp.Dtos;
using JewelryManager.Api.Features.Statements;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.RiseUp;

/// <summary>Pulls one cashflow month from RiseUp and hands its transactions to the statement checklist.</summary>
public class RiseUpSyncService(IRiseUpClient client, IOptions<RiseUpOptions> options, StatementsService statements)
{
    public async Task<RiseUpSyncResponse> SyncMonthAsync(string month)
    {
        var config = options.Value;
        if (!config.CanSync) throw new BadRequestException("החיבור ל-RiseUp עדיין לא הוגדר");

        var json = await client.GetTransactionsJsonAsync(config.ApiToken, month);
        return await statements.ImportRiseUpAsync(month, RiseUpTransactionParser.Parse(json));
    }
}
