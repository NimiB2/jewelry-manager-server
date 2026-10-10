namespace JewelryManager.Api.Common.Exceptions;

/// <summary>
/// Base class for all domain exceptions.
/// The GlobalExceptionFilter maps these to HTTP status codes.
/// </summary>
public abstract class AppException(string message) : Exception(message);

/// <summary>Thrown when a requested resource does not exist. Maps to HTTP 404.</summary>
public class NotFoundException(string message) : AppException(message);

/// <summary>Thrown when a request is semantically invalid. Maps to HTTP 400.</summary>
public class BadRequestException(string message) : AppException(message);

/// <summary>Thrown when the caller is not authenticated or not recognized. Maps to HTTP 401.</summary>
public class UnauthorizedException(string message = "Unauthorized") : AppException(message);

/// <summary>Thrown when the caller lacks the required role. Maps to HTTP 403.</summary>
public class ForbiddenException(string message = "Forbidden") : AppException(message);

/// <summary>
/// Thrown when a valid request clashes with existing data and the user may confirm to go on
/// (e.g. a product already linked elsewhere). Maps to HTTP 409; Code lets the client tell which clash it is.
/// </summary>
public class ConflictException(string message, string code) : AppException(message)
{
    public string Code { get; } = code;
}
