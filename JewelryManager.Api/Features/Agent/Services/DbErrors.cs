using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JewelryManager.Api.Features.Agent.Services;

/// <summary>Tells "this row already exists" apart from a real database failure, which must never be swallowed.</summary>
public static class DbErrors
{
    private const string UniqueViolation = "23505";

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
