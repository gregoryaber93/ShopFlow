using System.Net;
using System.Text;
using ShopFlow.Notifications.Application;
using ShopFlow.Notifications.Infrastructure.Channels;
using ShopFlow.Notifications.Infrastructure.Customers;

namespace ShopFlow.Notifications.UnitTests;

public sealed class GraphCustomerDirectoryTests
{
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000d1");

    [Fact]
    public async Task External_id_sign_in_email_is_used_when_mail_is_empty()
    {
        var json = """
            {
              "id": "0193a1b2-0000-7000-8000-0000000000d1",
              "displayName": "unknown",
              "givenName": "Anna",
              "mail": null,
              "otherMails": [],
              "identities": [
                { "signInType": "userPrincipalName", "issuer": "shopflowcustomers.onmicrosoft.com", "issuerAssignedId": "x@shopflowcustomers.onmicrosoft.com" },
                { "signInType": "emailAddress", "issuer": "shopflowcustomers.onmicrosoft.com", "issuerAssignedId": "anna@example.com" }
              ],
              "mobilePhone": null,
              "preferredLanguage": "pl-PL"
            }
            """;

        var contact = await GraphCustomerDirectory.LoadAsync(Graph(HttpStatusCode.OK, json), CustomerId, CancellationToken.None);

        contact.ShouldNotBeNull();
        contact.Email.ShouldBe("anna@example.com");
        contact.DisplayName.ShouldBe("Anna");
        contact.PreferredLanguage.ShouldBe("pl-PL");
    }

    [Fact]
    public async Task Deleted_account_is_reported_as_missing_not_as_an_error()
    {
        var contact = await GraphCustomerDirectory.LoadAsync(Graph(HttpStatusCode.NotFound, null), CustomerId, CancellationToken.None);

        contact.ShouldBeNull();
    }

    private static HttpClient Graph(HttpStatusCode status, string? json) =>
        new(new StubHandler(status, json)) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };

    private sealed class StubHandler(HttpStatusCode status, string? json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.Query.ShouldContain("$select=");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}

public sealed class AcsEmailMessageTests
{
    [Fact]
    public void Transactional_email_disables_tracking_sets_reply_to_and_carries_the_notification_id()
    {
        var operationId = Guid.CreateVersion7();
        var options = new CommunicationOptions
        {
            Endpoint = new Uri("https://acs-shopflow-prod.europe.communication.azure.com"),
            EmailSender = "DoNotReply@notify.shopflow.example",
            EmailReplyTo = "support@shopflow.example",
            SmsSender = "+48000000000",
        };

        var message = AcsEmailSender.Build(
            new OutgoingEmail(operationId, "jan@example.com", "Jan", new EmailContent("Subject", "<p>body</p>", "body"), IsRetry: false),
            options);

        message.UserEngagementTrackingDisabled.ShouldBe(true);
        message.SenderAddress.ShouldBe("DoNotReply@notify.shopflow.example");
        message.ReplyTo.ShouldHaveSingleItem().Address.ShouldBe("support@shopflow.example");
        message.Headers["X-ShopFlow-Notification-Id"].ShouldBe(operationId.ToString("D"));
        message.Content.PlainText.ShouldBe("body");
    }
}

public sealed class RecipientMaskTests
{
    [Theory]
    [InlineData("jan.kowalski@example.com", "j***@example.com")]
    [InlineData("+48600100200", "+48*****200")]
    [InlineData("12345", "***")]
    public void Recipient_is_masked_before_it_is_stored(string address, string expected) =>
        RecipientMask.Mask(address).ShouldBe(expected);
}
