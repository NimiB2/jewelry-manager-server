using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Incomes.Dtos;

/// <summary>Body of POST /incomes and PUT /incomes/{id}: income entered by hand (not from an order).</summary>
public record SaveIncomeDto(
    DateOnly Date,
    IncomeCategory Category,
    [Required, MaxLength(300)] string Description,
    [Range(0.01, 100_000_000)] decimal Amount);

public record IncomeResponse(
    Guid Id,
    DateOnly Date,
    IncomeCategory Category,
    string Description,
    decimal Amount,
    Guid? OrderId);
