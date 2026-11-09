using System.Net;
using System.Text;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Features.RiseUp;

namespace JewelryManager.Tests;

// All data here is invented. Real transactions never go into tests or docs.
public class RiseUpTests
{
    private const string Token = "riseup_pat_TEST_ONLY";

    private const string Page = """
        {"transactions":[
          {"transactionId":"t-1","transactionDate":"2026-06-15T00:00:00.000Z","businessName":"Sample Cafe",
           "isIncome":false,"amount":42.5,"sourceType":"creditCard","accountNickname":"Card A",
           "isExcluded":false,"isInstallment":false},
          {"transactionId":"t-2","transactionDate":"2026-06-20T00:00:00.000Z","businessName":"Sample Client",
           "isIncome":true,"amount":900,"sourceType":"checkingAccount","accountNickname":null,
           "isExcluded":true,"isInstallment":false},
          {"transactionId":"t-3","transactionDate":"2026-06-21T00:00:00.000Z","businessName":"Sample Shop",
           "isIncome":false,"amount":100,"sourceType":"creditCard","isInstallment":true,
           "installmentNumber":2,"totalNumberOfInstallments":6},
          {"transactionDate":"2026-06-22T00:00:00.000Z","businessName":"No id","isIncome":false,"amount":5},
          {"transactionId":"t-5","businessName":"No date","isIncome":false,"amount":5}
        ]}
        """;

    [Fact]
    public void Parse_ReadsAmountDateAndDirection()
    {
        var rows = RiseUpTransactionParser.Parse(Page);

        var cafe = rows.Single(r => r.TransactionId == "t-1");
        Assert.Equal(new DateOnly(2026, 6, 15), cafe.Date);
        Assert.Equal(42.5m, cafe.Amount);
        Assert.False(cafe.IsIncome);
        Assert.Equal("Sample Cafe", cafe.BusinessName);
        Assert.Equal("creditCard", cafe.SourceType);
        Assert.Equal("Card A", cafe.AccountNickname);
    }

    [Fact]
    public void Parse_KeepsIncomeAndExcludedFlags()
    {
        var client = RiseUpTransactionParser.Parse(Page).Single(r => r.TransactionId == "t-2");

        Assert.True(client.IsIncome);
        Assert.True(client.IsExcluded);
        Assert.Null(client.AccountNickname);
    }

    [Fact]
    public void Parse_ReadsInstallmentsOnlyWhenFlagged()
    {
        var rows = RiseUpTransactionParser.Parse(Page);

        var plan = rows.Single(r => r.TransactionId == "t-3");
        Assert.Equal(2, plan.InstallmentNumber);
        Assert.Equal(6, plan.TotalInstallments);

        var plain = rows.Single(r => r.TransactionId == "t-1");
        Assert.Null(plain.InstallmentNumber);
        Assert.Null(plain.TotalInstallments);
    }

    [Fact]
    public void Parse_SkipsRowsWithoutIdOrDate()
    {
        var rows = RiseUpTransactionParser.Parse(Page);

        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.BusinessName == "No id");
        Assert.DoesNotContain(rows, r => r.BusinessName == "No date");
    }

    [Fact]
    public void Parse_RejectsAnUnexpectedShape()
    {
        Assert.Throws<BadRequestException>(() => RiseUpTransactionParser.Parse("""{"other":[]}"""));
    }

    [Fact]
    public async Task Client_SendsTokenAsBearerHeader()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Page);
        var client = new RiseUpClient(new HttpClient(handler));

        await client.GetTransactionsJsonAsync(Token, "2026-06");

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal(Token, handler.Request.Headers.Authorization.Parameter);
        Assert.Contains("cashflowMonth=2026-06", handler.Request.RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "לא תקין")]
    [InlineData(HttpStatusCode.Forbidden, "budget:read")]
    [InlineData(HttpStatusCode.TooManyRequests, "יותר מדי")]
    public async Task Client_TranslatesErrorStatuses(HttpStatusCode status, string expectedText)
    {
        var client = new RiseUpClient(new HttpClient(new StubHandler(status, "{}")));

        var error = await Assert.ThrowsAsync<BadRequestException>(() => client.GetTransactionsJsonAsync(Token, "2026-06"));

        Assert.Contains(expectedText, error.Message);
        Assert.DoesNotContain(Token, error.Message);
    }

    [Theory]
    [InlineData("2026-6")]
    [InlineData("2026-06-01")]
    [InlineData("current")]
    public async Task Client_RejectsBadMonthWithoutCallingRiseUp(string month)
    {
        var handler = new StubHandler(HttpStatusCode.OK, Page);
        var client = new RiseUpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<BadRequestException>(() => client.GetTransactionsJsonAsync(Token, month));

        Assert.Null(handler.Request);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
