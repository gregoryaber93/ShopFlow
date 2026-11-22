using Microsoft.AspNetCore.Http;
using ShopFlow.Web.Http;

namespace ShopFlow.BuildingBlocks.Tests.Web;

public sealed class ConcurrencyHttpExtensionsTests
{
    [Fact]
    public void Strong_etag_from_cosmos_is_accepted_verbatim()
    {
        var request = WithIfMatch("\"0000d986-0000-0d00-0000-655f2a1b0000\"");

        request.TryGetIfMatch(out var etag).ShouldBeTrue();
        etag.ShouldBe("\"0000d986-0000-0d00-0000-655f2a1b0000\"");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"weak\"")]
    [InlineData("")]
    [InlineData("not-quoted")]
    public void Wildcard_weak_missing_or_malformed_etag_is_rejected(string header)
    {
        WithIfMatch(header).TryGetIfMatch(out _).ShouldBeFalse();
    }

    private static HttpRequest WithIfMatch(string value)
    {
        var http = new DefaultHttpContext();
        if (value.Length > 0)
        {
            http.Request.Headers.IfMatch = value;
        }

        return http.Request;
    }
}
