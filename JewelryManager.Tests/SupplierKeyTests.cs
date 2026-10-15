using JewelryManager.Api.Features.Expenses;

namespace JewelryManager.Tests;

public class SupplierKeyTests
{
    [Theory]
    [InlineData("זהב כהן", "זהב כהן בע\"מ")]
    [InlineData("Gold Ltd", "gold")]
    [InlineData("  א.ב  גולד ", "אב גולד")]
    public void SameSupplier_WrittenDifferently_HasTheSameKey(string a, string b)
    {
        Assert.Equal(ExpensesService.SupplierKey(a), ExpensesService.SupplierKey(b));
    }

    [Fact]
    public void DifferentSuppliers_KeepDifferentKeys()
    {
        Assert.NotEqual(ExpensesService.SupplierKey("זהב כהן"), ExpensesService.SupplierKey("זהב לוי"));
    }

    [Fact]
    public void SuffixInsideAWord_IsNotStripped()
    {
        Assert.Equal("princess", ExpensesService.SupplierKey("Princess"));
    }
}
