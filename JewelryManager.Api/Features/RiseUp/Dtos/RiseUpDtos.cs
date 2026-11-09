namespace JewelryManager.Api.Features.RiseUp.Dtos;

/// <summary>What one sync did: how many transactions were new, and how many were already in the checklist.</summary>
public record RiseUpSyncResponse(int Added, int AlreadyKnown, int AutoIgnored, int Pending, int AutoMatched);
