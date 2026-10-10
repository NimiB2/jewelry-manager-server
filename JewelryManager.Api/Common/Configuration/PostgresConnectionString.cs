using Npgsql;

namespace JewelryManager.Api.Common.Configuration;

/// <summary>
/// Hosting platforms hand out the database as a URL (postgres://user:pass@host/db), while Npgsql wants
/// key=value pairs. This accepts both so the same setting works locally and in the cloud.
/// </summary>
public static class PostgresConnectionString
{
    public static string Normalize(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return text;

        var uri = new Uri(text);
        var userInfo = uri.UserInfo.Split(':', 2);

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            // The platform's internal network is private; TLS is used when the server offers it.
            SslMode = SslMode.Prefer,
        }.ConnectionString;
    }
}
