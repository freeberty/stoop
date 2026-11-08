namespace Intersect.Common;

public enum ErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, PaymentRequired, RateLimited }

/// <summary>Expected, user-facing failure. The API layer maps it to an RFC 7807 problem response.</summary>
public sealed class AppException : Exception
{
    public ErrorKind Kind { get; }
    public string Code { get; }
    public IReadOnlyDictionary<string, object?>? Extensions { get; }

    public AppException(ErrorKind kind, string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(message)
    {
        Kind = kind;
        Code = code;
        Extensions = extensions;
    }

    public static AppException Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static AppException Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);
    public static AppException Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static AppException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static AppException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static AppException RateLimited(string code, string message) => new(ErrorKind.RateLimited, code, message);
    public static AppException PaymentRequired(string code, string message, IReadOnlyDictionary<string, object?>? ext = null)
        => new(ErrorKind.PaymentRequired, code, message, ext);
}
