using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Resilience;
using ShopFlow.Web.Idempotency;

namespace ShopFlow.BuildingBlocks.Tests.Web;

public sealed class IdempotencyEndpointFilterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly IdempotencyEndpointFilter _filter;
    private readonly IServiceProvider _services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
    private int _handlerCalls;

    public IdempotencyEndpointFilterTests()
    {
        _filter = new IdempotencyEndpointFilter(new InMemoryIdempotencyStore(_time), _time, NullLogger<IdempotencyEndpointFilter>.Instance);
    }

    [Fact]
    public async Task First_request_executes_the_handler_and_writes_its_response()
    {
        var http = Request("key-1", """{"sku":"A","qty":1}""");

        await InvokeAsync(http);

        _handlerCalls.ShouldBe(1);
        http.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        http.Response.Headers.Location.ToString().ShouldBe("/orders/42");
        ReadBody(http).ShouldContain("\"id\":42");
    }

    [Fact]
    public async Task Retry_with_the_same_key_and_body_replays_the_stored_response_without_executing_again()
    {
        await InvokeAsync(Request("key-1", """{"sku":"A","qty":1}"""));

        var retry = Request("key-1", """{"sku":"A","qty":1}""");
        await InvokeAsync(retry);

        _handlerCalls.ShouldBe(1);
        retry.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.Response.Headers[ShopFlowHttpHeaders.IdempotentReplayed].ToString().ShouldBe("true");
        retry.Response.Headers.Location.ToString().ShouldBe("/orders/42");
        ReadBody(retry).ShouldContain("\"id\":42");
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_rejected()
    {
        await InvokeAsync(Request("key-1", """{"sku":"A","qty":1}"""));

        var misuse = Request("key-1", """{"sku":"A","qty":5}""");
        await InvokeAsync(misuse);

        misuse.Response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        _handlerCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_from_a_different_customer_is_a_different_request()
    {
        await InvokeAsync(Request("key-1", """{"sku":"A"}""", customer: "customer-a"));
        await InvokeAsync(Request("key-1", """{"sku":"A"}""", customer: "customer-b"));

        _handlerCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_request_with_the_same_key_gets_409_while_the_first_one_runs()
    {
        // The customer clicked "Order" twice: the second request arrives while the first is still running.
        DefaultHttpContext? second = null;

        await InvokeAsync(Request("key-1", "{}"), whileHandlerRuns: async () =>
        {
            second = Request("key-1", "{}");
            await InvokeAsync(second);
        });

        _handlerCalls.ShouldBe(1);
        second.ShouldNotBeNull();
        second.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        second.Response.Headers.RetryAfter.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Missing_key_is_a_bad_request()
    {
        var http = Request(key: null, "{}");

        await InvokeAsync(http);

        http.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        _handlerCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(StatusCodes.Status201Created, true)]
    [InlineData(StatusCodes.Status400BadRequest, true)]
    [InlineData(StatusCodes.Status409Conflict, false)]
    [InlineData(StatusCodes.Status412PreconditionFailed, false)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, false)]
    public void Only_deterministic_outcomes_are_remembered(int statusCode, bool remembered)
    {
        IdempotencyEndpointFilter.ShouldRemember(statusCode).ShouldBe(remembered);
    }

    private async Task InvokeAsync(DefaultHttpContext http, Func<Task>? whileHandlerRuns = null)
    {
        var result = await _filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), async _ =>
        {
            _handlerCalls++;
            if (whileHandlerRuns is not null)
            {
                await whileHandlerRuns();
            }

            return TypedResults.Created("/orders/42", new { id = 42 });
        });

        // Just like ASP.NET Core does after returning from the filter chain.
        if (result is IResult executable)
        {
            await executable.ExecuteAsync(http);
        }
    }

    private DefaultHttpContext Request(string? key, string body, string customer = "customer-a")
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/v1/orders";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Response.Body = new MemoryStream();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", customer)], "test"));

        if (key is not null)
        {
            http.Request.Headers[ShopFlowHttpHeaders.IdempotencyKey] = key;
        }

        return http;
    }

    private static string ReadBody(HttpContext http)
    {
        http.Response.Body.Position = 0;
        return new StreamReader(http.Response.Body).ReadToEnd();
    }
}
