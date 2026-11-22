using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShopFlow.OrderSaga.Activities;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.UnitTests;

public sealed class OrdersApiClientTests
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000aa");

    [Fact]
    public async Task Confirmation_is_sent_with_the_etag_read_just_before()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, """{"status":"submitted"}""", etag: "\"v1\"")
            .Then(HttpStatusCode.OK, """{"status":"confirmed"}""", etag: "\"v2\"");

        var outcome = await Client(handler).ConfirmAsync(new ConfirmOrderInput(OrderId, "TRK"), TestContext.Current.CancellationToken);

        outcome.ShouldBe(OrdersCallOutcome.Applied);
        handler.Requests[1].Method.ShouldBe(HttpMethod.Put);
        handler.Requests[1].IfMatch.ShouldBe("\"v1\"");
    }

    [Fact]
    public async Task Precondition_failed_triggers_a_fresh_read_and_retry()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, """{"status":"submitted"}""", etag: "\"v1\"")
            .Then(HttpStatusCode.PreconditionFailed, "{}")
            .Then(HttpStatusCode.OK, """{"status":"submitted"}""", etag: "\"v2\"")
            .Then(HttpStatusCode.OK, """{"status":"confirmed"}""");

        var outcome = await Client(handler).ConfirmAsync(new ConfirmOrderInput(OrderId, null), TestContext.Current.CancellationToken);

        outcome.ShouldBe(OrdersCallOutcome.Applied);
        handler.Requests[3].IfMatch.ShouldBe("\"v2\"");
    }

    [Fact]
    public async Task Order_already_in_target_state_is_not_modified_again()
    {
        // The activity re-runs after a lost response: the first run already confirmed the order.
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, """{"status":"confirmed"}""", etag: "\"v2\"");

        var outcome = await Client(handler).ConfirmAsync(new ConfirmOrderInput(OrderId, null), TestContext.Current.CancellationToken);

        outcome.ShouldBe(OrdersCallOutcome.Applied);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Conflict_with_pending_cancellation_is_reported_to_the_saga()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, """{"status":"cancellationRequested"}""", etag: "\"v3\"")
            .Then(HttpStatusCode.Conflict, """{"title":"cancellation pending","code":"order.cancellation_pending"}""");

        var outcome = await Client(handler).ConfirmAsync(new ConfirmOrderInput(OrderId, null), TestContext.Current.CancellationToken);

        outcome.ShouldBe(OrdersCallOutcome.CancellationPending);
    }

    private static OrdersApiClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://orders.test/") }, NullLogger<OrdersApiClient>.Instance);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? ETag)> _script = new();

        public List<(HttpMethod Method, string Path, string? IfMatch)> Requests { get; } = [];

        public ScriptedHandler Then(HttpStatusCode status, string body, string? etag = null)
        {
            _script.Enqueue((status, body, etag));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.IfMatch.FirstOrDefault()?.Tag));

            var (status, body, etag) = _script.Dequeue();
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (etag is not null)
            {
                response.Headers.ETag = new EntityTagHeaderValue(etag);
            }

            return Task.FromResult(response);
        }
    }
}
