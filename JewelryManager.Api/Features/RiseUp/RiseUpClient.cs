using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using JewelryManager.Api.Common.Exceptions;

namespace JewelryManager.Api.Features.RiseUp;

public interface IRiseUpClient
{
    Task<string> GetTransactionsJsonAsync(string token, string cashflowMonth);
}

/// <summary>Read-only calls to RiseUp. The token arrives per call and is never stored or logged here.</summary>
public class RiseUpClient(HttpClient http) : IRiseUpClient
{
    private const string TransactionsUrl = "https://input.riseup.co.il/api/external/transactions";
    private static readonly Regex MonthPattern = new(@"^\d{4}-\d{2}$");

    public async Task<string> GetTransactionsJsonAsync(string token, string cashflowMonth)
    {
        if (!MonthPattern.IsMatch(cashflowMonth)) throw new BadRequestException("החודש חייב להיות בפורמט YYYY-MM");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{TransactionsUrl}?cashflowMonth={cashflowMonth}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new BadRequestException("הטוקן של RiseUp לא תקין או שפג. צריך ליצור טוקן חדש"),
            HttpStatusCode.Forbidden => new BadRequestException("לטוקן של RiseUp חסרה הרשאה budget:read"),
            HttpStatusCode.TooManyRequests => new BadRequestException("יותר מדי בקשות ל-RiseUp, כדאי לנסות שוב בעוד דקה"),
            _ => new BadRequestException($"RiseUp החזירה שגיאה ({(int)response.StatusCode})"),
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        try
        {
            return await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new BadRequestException("לא הצלחנו להתחבר ל-RiseUp, כדאי לנסות שוב");
        }
    }
}
