using Stoop.Common;
using Microsoft.AspNetCore.Mvc;

namespace Stoop.Api.Infrastructure;

/// <summary>Consistent RFC 7807 errors: AppException becomes a typed problem, everything else a generic 500.</summary>
public sealed class ProblemDetailsMiddleware(RequestDelegate next, ILogger<ProblemDetailsMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            await WriteAsync(context, ex);
        }
        catch (BadHttpRequestException ex)
        {
            await WriteAsync(context, AppException.Validation("bad_request", ex.Message));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // client went away
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "internal_error",
                "Internal Server Error", "An unexpected error occurred.", null);
        }
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.PaymentRequired => StatusCodes.Status402PaymentRequired,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static Task WriteAsync(HttpContext context, AppException ex)
        => WriteProblemAsync(context, StatusFor(ex.Kind), ex.Code, ex.Kind.ToString(), ex.Message, ex.Extensions);

    public static async Task WriteProblemAsync(HttpContext context, int status, string code, string title, string detail,
        IReadOnlyDictionary<string, object?>? extensions)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Type = $"urn:stoop:problem:{code}" };
        problem.Extensions["code"] = code;
        if (extensions is not null) foreach (var (k, v) in extensions) problem.Extensions[k] = v;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
