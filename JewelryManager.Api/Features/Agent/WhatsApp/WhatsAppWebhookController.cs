using System.Security.Cryptography;
using System.Text;
using JewelryManager.Api.Features.Agent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.WhatsApp;

/// <summary>
/// The address Meta calls. It only checks that the call is genuine, drops the messages in the inbox and answers
/// at once; the work happens afterwards in the background. Anonymous because Meta has no login: the signature is the proof.
/// </summary>
[ApiController]
[Route("webhooks/whatsapp")]
[AllowAnonymous]
public class WhatsAppWebhookController(AgentInbox inbox, IOptions<AgentOptions> options, ILogger<WhatsAppWebhookController> log) : ControllerBase
{
    /// <summary>Meta's one-time handshake when the webhook is set up.</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var config = options.Value;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.WhatsApp.VerifyToken)) return NotFound();

        var same = token is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(config.WhatsApp.VerifyToken));

        return mode == "subscribe" && same && challenge is not null ? Content(challenge, "text/plain") : Forbid();
    }

    [HttpPost]
    [RequestSizeLimit(1_000_000)]
    public async Task<IActionResult> Receive()
    {
        var config = options.Value;
        if (!config.Enabled) return NotFound();

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();

        if (!WhatsAppSignature.IsValid(body, Request.Headers["X-Hub-Signature-256"].ToString(), config.WhatsApp.AppSecret))
        {
            log.LogWarning("WhatsApp webhook refused: bad signature");
            return Unauthorized();
        }

        foreach (var message in WhatsAppPayloadParser.Parse(Encoding.UTF8.GetString(body), config.WhatsApp.PhoneNumberId))
        {
            if (!inbox.TryEnqueue(message)) log.LogWarning("Agent inbox is full; a message was dropped");
        }

        return Ok();
    }
}
