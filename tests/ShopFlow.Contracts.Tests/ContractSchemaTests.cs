using System.Text.Json;
using System.Text.Json.Schema;

namespace ShopFlow.Contracts.Tests;

/// <summary>
/// JSON schema snapshots of all contracts.
/// </summary>
/// <remarks>
/// WHY: a contract is the public API for independently deployed services. Every schema change (a new field, a type
/// change, a required field instead of an optional one) changes the <c>*.approved.json</c> file and shows up in the pull
/// request diff, where the contracts CODEOWNERS must approve it.
/// Review rule (docs/adr/0012):
/// <list type="bullet">
/// <item>adding an optional field: allowed within the same version,</item>
/// <item>adding a required field, removing a field or changing its type: a new contract version (V2) published side by
/// side with the old one.</item>
/// </list>
/// A change is accepted by replacing the <c>.approved.json</c> file with the generated <c>.received.json</c>.
/// </remarks>
public sealed class ContractSchemaTests
{
    // Formatting of the snapshot file only (a readable diff in the PR), not contract serialization.
    private static readonly JsonSerializerOptions SnapshotFormatting = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TheoryData<string> Contracts()
    {
        var data = new TheoryData<string>();
        foreach (var contract in MessageTypeRegistry.Default.Contracts.OrderBy(id => id.ToString(), StringComparer.Ordinal))
        {
            data.Add(contract.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Schema_matches_the_approved_snapshot(string contract)
    {
        var separator = contract.LastIndexOf(".v", StringComparison.Ordinal);
        var name = contract[..separator];
        var version = int.Parse(contract[(separator + 2)..], System.Globalization.CultureInfo.InvariantCulture);

        MessageTypeRegistry.Default.TryResolve(name, version, out var type).ShouldBeTrue();

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
            ContractJson.Options,
            type!,
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });

        SchemaSnapshot.Match(schema.ToJsonString(SnapshotFormatting), contract);
    }
}
