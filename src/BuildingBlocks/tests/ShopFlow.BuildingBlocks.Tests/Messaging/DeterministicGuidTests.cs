using ShopFlow.Messaging;

namespace ShopFlow.BuildingBlocks.Tests.Messaging;

public sealed class DeterministicGuidTests
{
    [Fact]
    public void Same_name_gives_same_guid()
    {
        DeterministicGuid.Create("order-1/ReserveStock/1").ShouldBe(DeterministicGuid.Create("order-1/ReserveStock/1"));
    }

    [Fact]
    public void Different_names_give_different_guids()
    {
        DeterministicGuid.Create("order-1/ReserveStock/1").ShouldNotBe(DeterministicGuid.Create("order-1/ReserveStock/2"));
    }

    [Fact]
    public void Guid_is_marked_as_rfc9562_version_8()
    {
        DeterministicGuid.Create("anything").Version.ShouldBe(8);
    }
}
