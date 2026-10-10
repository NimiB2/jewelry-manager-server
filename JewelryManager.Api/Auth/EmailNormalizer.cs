namespace JewelryManager.Api.Auth;

/// <summary>
/// One rule for comparing emails: an invited address typed on a phone ("Name@Gmail.com ") must still
/// match what Google reports on the first sign-in.
/// </summary>
public static class EmailNormalizer
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
