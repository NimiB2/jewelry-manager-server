using JewelryManager.Api.Common.Configuration;
using Npgsql;

namespace JewelryManager.Tests;

public class PostgresConnectionStringTests
{
    [Fact]
    public void KeyValueString_IsLeftAsIs()
    {
        const string text = "Host=localhost;Database=jewelry;Username=u;Password=p";

        Assert.Equal(text, PostgresConnectionString.Normalize(text));
    }

    [Theory]
    [InlineData("postgres://jewelry:s%40cret@dpg-abc.frankfurt-postgres.render.com:5432/jewelry_db")]
    [InlineData("postgresql://jewelry:s%40cret@dpg-abc.frankfurt-postgres.render.com/jewelry_db")]
    public void PlatformUrl_BecomesKeyValueWithDecodedPassword(string url)
    {
        var parsed = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(url));

        Assert.Equal("dpg-abc.frankfurt-postgres.render.com", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("jewelry_db", parsed.Database);
        Assert.Equal("jewelry", parsed.Username);
        Assert.Equal("s@cret", parsed.Password);
    }
}
