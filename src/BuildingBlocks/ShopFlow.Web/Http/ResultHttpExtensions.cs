using Microsoft.AspNetCore.Http;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Web.Http;

public static class ResultHttpExtensions
{
    /// <summary>Turns an expected refusal from the Application layer into a ProblemDetails response (RFC 9457).</summary>
    public static IResult ToProblem(this Error error)
    {
        var statusCode = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return TypedResults.Problem(
            statusCode: statusCode,
            title: error.Message,
            type: $"https://docs.shopflow.example/errors/{error.Code}",
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
