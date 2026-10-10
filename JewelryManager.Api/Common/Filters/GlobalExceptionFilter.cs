using JewelryManager.Api.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JewelryManager.Api.Common.Filters;

/// <summary>
/// Catches domain exceptions thrown anywhere in the pipeline and maps them to the
/// correct HTTP status code + a consistent JSON error shape.
///
/// This is the single place where exception → HTTP happens.
/// Controllers and services never deal with status codes directly.
/// </summary>
public class GlobalExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is ConflictException conflict)
        {
            context.Result = new ObjectResult(new { error = conflict.Message, code = conflict.Code })
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
            context.ExceptionHandled = true;
            return;
        }

        var (status, message) = context.Exception switch
        {
            NotFoundException ex   => (StatusCodes.Status404NotFound,       ex.Message),
            BadRequestException ex => (StatusCodes.Status400BadRequest,     ex.Message),
            UnauthorizedException ex => (StatusCodes.Status401Unauthorized, ex.Message),
            ForbiddenException ex  => (StatusCodes.Status403Forbidden,      ex.Message),
            _                      => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
        };

        context.Result = new ObjectResult(new { error = message })
        {
            StatusCode = status,
        };

        // Mark as handled so ASP.NET Core doesn't apply its own error page.
        context.ExceptionHandled = true;
    }
}
