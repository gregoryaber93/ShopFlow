using System.Text;
using ShopFlow.Functions;

namespace ShopFlow.BuildingBlocks.Tests.Functions;

public sealed class EasyAuthPrincipalTests
{
    [Fact]
    public void Role_is_read_from_the_platform_header()
    {
        var json = """{"auth_typ":"aad","name_typ":"name","role_typ":"roles","claims":[{"typ":"name","val":"APIM"},{"typ":"roles","val":"Carrier.Webhooks.Write"}]}""";
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var principal = EasyAuthPrincipal.Parse(header)!;

        principal.IsInRole("Carrier.Webhooks.Write").ShouldBeTrue();
        principal.IsInRole("Warehouse.Operator").ShouldBeFalse();
        principal.Name.ShouldBe("APIM");
        EasyAuthPrincipal.Parse("not-base64!").ShouldBeNull();
    }
}
