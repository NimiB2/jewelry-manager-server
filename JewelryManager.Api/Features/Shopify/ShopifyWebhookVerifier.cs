using System.Security.Cryptography;
using System.Text;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>
/// Checks the X-Shopify-Hmac-Sha256 header: Shopify signs the raw request body with the app's secret,
/// so a request without a matching signature is not from Shopify and must be dropped.
/// </summary>
public static class ShopifyWebhookVerifier
{
    public static bool IsValid(byte[] body, string? signatureHeader, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(signatureHeader.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret.Trim()));
        var expected = hmac.ComputeHash(body);

        // Constant time, so the comparison does not leak how many bytes matched.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
