using System.Runtime.CompilerServices;
using System.Text.Json;
using PactNet;
using PactNet.Infrastructure.Outputters;

namespace ShopFlow.Pact.Tests;

internal static class PactEnvironment
{
    /// <summary>
    /// WHY: the Pact library (pact_ffi) sends anonymous usage statistics (version, operating system) to its authors' server by
    /// default. Tests must not make any calls off the machine, so we turn this off before the first test touches the library.
    /// In CI the same variable is set in the workflow.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableTelemetry() => Environment.SetEnvironmentVariable("PACT_DO_NOT_TRACK", "true");
}

/// <summary>
/// Pact tests run one after another: the consumer writes the pact file, the provider reads it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PactCollection
{
    public const string Name = "pact";
}

internal static class PactFiles
{
    /// <summary>
    /// The <c>pacts/</c> directory in the project sources (not in bin), so that the files end up in the repository.
    /// </summary>
    /// <remarks>
    /// WHY: pacts as files in the repository instead of a Pact Broker. In a monorepo the consumer and the provider are in one
    /// pull request, so a change to the consumer's expectations and its verification at the provider happen in the same CI
    /// run. A broker (PactFlow or self-hosted) is needed only with separate repositories and separate release cycles, for the
    /// "can-i-deploy" question.
    /// </remarks>
    public static string Directory { get; } = FindProjectDirectory();

    public static FileInfo For(string consumer, string provider) => new(Path.Combine(Directory, $"{consumer}-{provider}.json"));

    public static PactConfig Config(ITestOutputHelper output) => new()
    {
        PactDir = Directory,
        LogLevel = PactLogLevel.Warn,
        Outputters = [new XunitOutput(output)],
        DefaultJsonSettings = new JsonSerializerOptions(JsonSerializerDefaults.Web),
    };

    private static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShopFlow.Pact.Tests.csproj")))
            {
                return Path.Combine(directory.FullName, "pacts");
            }
        }

        throw new InvalidOperationException("Cannot find the ShopFlow.Pact.Tests project directory.");
    }
}

internal sealed class XunitOutput(ITestOutputHelper output) : IOutput
{
    public void WriteLine(string line) => output.WriteLine(line);
}

/// <summary>Sample values shared by the consumer and the provider (provider states recreate exactly this data).</summary>
internal static class PactExamples
{
    public static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-00000000aa01");
    public static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-00000000aa02");
    public static readonly Guid ShipmentId = Guid.Parse("0193a1b2-0000-7000-8000-00000000aa03");
    public static readonly DateTimeOffset DispatchedAt = new(2026, 11, 22, 9, 30, 0, TimeSpan.Zero);

    public const string GuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    public const string DateTimeOffsetPattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$";
}
