using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>
/// Where the store delivers new orders. No Firebase sign-in here: the request is trusted only when its
/// HMAC signature matches the app secret and it names the connected store. Nothing is ever sent back.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("webhooks/shopify")]
public class ShopifyWebhookController(
    IOptions<ShopifyOptions> options, ShopifyOrderImportService import, ILogger<ShopifyWebhookController> logger) : ControllerBase
{
    [HttpPost("orders")]
    [RequestSizeLimit(1_000_000)]
    public async Task<IActionResult> Orders()
    {
        var shop = options.Value;

        // Not connected yet: the address simply does not exist.
        if (!shop.CanReceiveOrders) return NotFound();

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();

        if (!ShopifyWebhookVerifier.IsValid(body, Request.Headers["X-Shopify-Hmac-Sha256"].ToString(), shop.WebhookSecret))
        {
            logger.LogWarning("Shopify webhook rejected: bad signature");
            return Unauthorized();
        }

        var domain = Request.Headers["X-Shopify-Shop-Domain"].ToString();
        if (!string.Equals(domain, shop.ShopDomain, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Shopify webhook rejected: unexpected shop");
            return Unauthorized();
        }

        var incoming = ShopifyOrderParser.Parse(Encoding.UTF8.GetString(body));
        if (incoming is null)
        {
            // Signed but not a usable order: acknowledged, so the store does not retry it for days.
            logger.LogWarning("Shopify webhook ignored: the body is not a usable order");
            return Ok();
        }

        var result = await import.ImportAsync(incoming);
        logger.LogInformation("Shopify order {Order}: {Result}", incoming.DisplayName, result);

        return result == StoreOrderImportResult.NoBusiness ? StatusCode(StatusCodes.Status500InternalServerError) : Ok();
    }
}
