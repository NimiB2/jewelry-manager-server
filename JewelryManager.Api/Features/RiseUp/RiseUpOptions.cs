namespace JewelryManager.Api.Features.RiseUp;

/// <summary>
/// The RiseUp connection. The token comes from the env var RiseUp__ApiToken (Render), never from the repository.
/// RiseUp tokens expire after 30 days, so the owner replaces this value there once a month.
/// </summary>
public class RiseUpOptions
{
    public const string SectionName = "RiseUp";

    private string _apiToken = "";

    public string ApiToken { get => _apiToken; set => _apiToken = value?.Trim() ?? ""; }

    public bool CanSync => ApiToken.Length > 0;
}
