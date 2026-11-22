using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using ShopFlow.Functions;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.Functions.Functions;

/// <summary>
/// Operator panel: view the saga state and make a decision for an instance waiting for a human.
/// </summary>
/// <remarks>
/// AZURE: <c>AuthorizationLevel.Anonymous</c> does not mean unprotected. Function keys (Function/Admin) are shared
/// secrets we avoid. Instead the function app has the built-in App Service authentication ("Easy Auth") enabled with
/// the Entra ID provider (infra/modules/compute/function-app-flex.bicep): the platform validates the token before the
/// request reaches the code and passes the identity in the <c>X-MS-CLIENT-PRINCIPAL</c> header. The platform
/// overwrites that header, so a client cannot forge it. The code only checks the role. In addition the app has public
/// access disabled and is reachable only through its private endpoint, from inside the VNet.
/// </remarks>
public sealed class SagaOperatorFunctions
{
    public const string OperatorRole = "Saga.Operator";

    [Function("saga-status")]
    public static async Task<IActionResult> GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sagas/{orderId:guid}")] HttpRequest request,
        Guid orderId,
        [DurableClient] DurableTaskClient client)
    {
        if (!IsOperator(request))
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        var instance = await client.GetInstanceAsync(OrderSagaInstance.IdFor(orderId), getInputsAndOutputs: false, request.HttpContext.RequestAborted);
        if (instance is null)
        {
            return new NotFoundResult();
        }

        return new OkObjectResult(new
        {
            instance.InstanceId,
            RuntimeStatus = instance.RuntimeStatus.ToString(),
            CustomStatus = instance.SerializedCustomStatus is { } status ? JsonDocument.Parse(status).RootElement : (JsonElement?)null,
            instance.CreatedAt,
            instance.LastUpdatedAt,
        });
    }

    [Function("saga-resolution")]
    public static async Task<IActionResult> Resolve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sagas/{orderId:guid}/resolution")] HttpRequest request,
        Guid orderId,
        [DurableClient] DurableTaskClient client)
    {
        if (!IsOperator(request))
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        var body = await request.ReadFromJsonAsync<ResolutionRequest>(request.HttpContext.RequestAborted);
        if (body is null || !Enum.TryParse<OperatorAction>(body.Action, ignoreCase: true, out var action))
        {
            return new BadRequestObjectResult(new { error = "Body must be { \"action\": \"retry\" | \"markResolved\", \"note\": \"...\" }." });
        }

        var instanceId = OrderSagaInstance.IdFor(orderId);
        var instance = await client.GetInstanceAsync(instanceId, request.HttpContext.RequestAborted);
        if (instance is null || instance.IsCompleted)
        {
            return new ConflictObjectResult(new { error = "The saga is not running." });
        }

        var resolution = new OperatorResolution(action, EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.Name ?? "unknown", body.Note);
        await client.RaiseEventAsync(instanceId, SagaEvents.OperatorResolution, resolution, request.HttpContext.RequestAborted);

        return new AcceptedResult();
    }

    private static bool IsOperator(HttpRequest request) =>
        EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.IsInRole(OperatorRole) == true;

    private sealed record ResolutionRequest(string Action, string? Note);
}

