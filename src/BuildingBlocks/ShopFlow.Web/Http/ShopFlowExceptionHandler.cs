using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Web.Http;

/// <summary>Turns exceptions into ProblemDetails. Unexpected errors do not reveal details to the client.</summary>
public sealed partial class ShopFlowExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ShopFlowExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var (status, title, code) = exception switch
        {
            // Someone changed the resource between the client's read and its write. The client should read the
            // resource again and make the decision once more.
            ConcurrencyConflictException => (StatusCodes.Status412PreconditionFailed, "The resource was modified by another request", "concurrency_conflict"),
            DomainException domain => (StatusCodes.Status422UnprocessableEntity, domain.Message, domain.Code),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "The request is malformed", "bad_request"),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Client closed the request", "client_closed"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", "internal_error"),
        };

        if (status >= 500)
        {
            LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Extensions =
                {
                    ["code"] = code,

                    // AZURE: traceId is operation_Id in Application Insights. A client reporting an error gives it to support,
                    // and support finds the whole request trace with one query.
                    ["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier,
                },
            },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private partial void LogUnhandled(Exception exception, string method, string path);
}
