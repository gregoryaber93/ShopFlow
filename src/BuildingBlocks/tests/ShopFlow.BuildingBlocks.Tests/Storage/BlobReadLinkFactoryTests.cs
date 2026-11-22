using System.Text;
using System.Web;
using Azure.Storage.Blobs.Models;
using ShopFlow.Azure.Storage;

namespace ShopFlow.BuildingBlocks.Tests.Storage;

public sealed class BlobReadLinkFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void User_delegation_link_is_read_only_short_lived_https_and_served_through_front_door()
    {
        var link = BlobReadLinkFactory.BuildReadLink(
            new Uri("https://stshopflowdocsprodweu.blob.core.windows.net"),
            "stshopflowdocsprodweu",
            "labels",
            "2026/11/order/shipment.pdf",
            Key(expiresIn: TimeSpan.FromDays(1)),
            Now,
            TimeSpan.FromMinutes(15),
            new Uri("https://files.shopflow.example"));

        link.Host.ShouldBe("files.shopflow.example");
        link.AbsolutePath.ShouldBe("/labels/2026/11/order/shipment.pdf");

        var query = HttpUtility.ParseQueryString(link.Query);
        query["sp"].ShouldBe("r");
        query["spr"].ShouldBe("https");
        query["se"].ShouldBe("2026-11-21T12:15:00Z");
        query["skoid"].ShouldNotBeNullOrEmpty();
        query["sig"].ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Link_that_would_outlive_its_delegation_key_is_refused()
    {
        // The key is valid for one more hour, but the link would have to work for 72 hours: Storage would reject it after an hour.
        Should.Throw<ArgumentException>(() => BlobReadLinkFactory.BuildReadLink(
            new Uri("https://stshopflowdocsprodweu.blob.core.windows.net"),
            "stshopflowdocsprodweu",
            "invoices",
            "2026/11/order/FV-2026-11-000001.pdf",
            Key(expiresIn: TimeSpan.FromHours(1)),
            Now,
            TimeSpan.FromHours(72),
            publicBaseUrl: null));
    }

    // Positional arguments pick one of the factory's two overloads (with named arguments the call is ambiguous).
    private static UserDelegationKey Key(TimeSpan expiresIn) => BlobsModelFactory.UserDelegationKey(
        Guid.NewGuid().ToString(),
        Guid.NewGuid().ToString(),
        Now.AddMinutes(-5),
        Now + expiresIn,
        "b",
        "2025-01-05",
        Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")));
}
