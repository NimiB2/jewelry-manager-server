using JewelryManager.Api.Auth;

namespace JewelryManager.Tests;

public class EmailNormalizerTests
{
    [Theory]
    [InlineData("wife@gmail.com", "wife@gmail.com")]
    [InlineData("Wife@Gmail.com", "wife@gmail.com")]
    [InlineData("  wife@gmail.com \n", "wife@gmail.com")]
    public void Normalize_TrimsAndLowercases(string typed, string expected) =>
        Assert.Equal(expected, EmailNormalizer.Normalize(typed));
}
