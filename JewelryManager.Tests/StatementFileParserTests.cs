using System.Text;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Statements;

namespace JewelryManager.Tests;

public class StatementFileParserTests
{
    private static List<ParsedStatementLine> ParseBank(string csv) =>
        new StatementFileParser().Parse("bank.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), StatementSource.Bank);

    [Fact]
    public void MovementTypeHeader_IsTheDescription()
    {
        var lines = ParseBank("תאריך,סוג תנועה,חובה,זכות,יתרה\n30/09/2030,העברה לספק,150.50,,900\n");

        var line = Assert.Single(lines);
        Assert.Equal("העברה לספק", line.Description);
        Assert.Equal(150.50m, line.Amount);
    }

    [Fact]
    public void UnknownDescriptionHeader_FallsBackToTheLongestTextColumn()
    {
        var lines = ParseBank("תאריך,משהו מוזר,קוד,חובה,זכות\n01/10/2030,תשלום לחברת חשמל,12,80,\n02/10/2030,קניה בחנות,13,20,\n");

        Assert.Equal(["תשלום לחברת חשמל", "קניה בחנות"], lines.Select(l => l.Description));
    }
}
