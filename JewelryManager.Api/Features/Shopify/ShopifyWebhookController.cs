using System.Text;
using JewelryManager.Api.Data.Entities;
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

        var json = Encoding.UTF8.GetString(body);

        // One address serves every order event; the store names the event in a header. A request
        // without it is the original "new order" delivery.
        var topic = Request.Headers["X-Shopify-Topic"].ToString().Trim().ToLowerInvariant();

        return topic switch
        {
            // "paid" carries the same full order: an order that was unpaid at creation arrives here once paid.
            "" or "orders/create" or "orders/paid" => await ReceiveNewOrder(json),
            "orders/cancelled" => await ReceiveChange(json, StoreOrderAlert.Cancelled, topic),
            "orders/updated" => await ReceiveChange(json, StoreOrderAlert.Updated, topic),
            "orders/delete" => await ReceiveChange(json, StoreOrderAlert.Deleted, topic),
            _ => IgnoreTopic(topic),
        };
    }

    private async Task<IActionResult> ReceiveNewOrder(string json)
    {
        var incoming = ShopifyOrderParser.Parse(json);
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

    private async Task<IActionResult> ReceiveChange(string json, StoreOrderAlert alert, string topic)
    {
        // A deletion carries only the id, so the full order is read only for an update (to compare it).
        var incoming = alert == StoreOrderAlert.Updated ? ShopifyOrderParser.Parse(json) : null;
        var externalId = incoming?.ExternalId ?? ShopifyOrderParser.ParseId(json);

        if (externalId is null)
        {
            logger.LogWarning("Shopify webhook {Topic} ignored: no order id", topic);
            return Ok();
        }

        var result = await import.ApplyChangeAsync(externalId, alert, incoming?.Fingerprint());
        logger.LogInformation("Shopify order {Id} {Topic}: {Result}", externalId, topic, result);

        return result == StoreOrderImportResult.NoBusiness ? StatusCode(StatusCodes.Status500InternalServerError) : Ok();
    }

    // Acknowledged, so the store does not keep retrying an event this system does not use.
    private IActionResult IgnoreTopic(string topic)
    {
        logger.LogInformation("Shopify webhook ignored: topic {Topic} is not used", topic);
        return Ok();
    }
}
