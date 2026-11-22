using System.Runtime.CompilerServices;

namespace ShopFlow.Contracts.Tests;

/// <summary>
/// Text comparison with the approved snapshot in the test project's <c>Snapshots/</c> directory.
/// </summary>
/// <remarks>
/// On a difference a <c>*.received.json</c> file with the new version is created next to the snapshot. Approving the
/// change means replacing the <c>*.approved.json</c> file with it (or running the tests with the
/// <c>SHOPFLOW_ACCEPT_SNAPSHOTS=1</c> variable) and including the diff in the pull request.
/// </remarks>
internal static class SchemaSnapshot
{
    public static void Match(string actual, string name, [CallerFilePath] string callerFile = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(callerFile)!, "Snapshots");
        var approvedPath = Path.Combine(directory, $"{name}.approved.json");
        var receivedPath = Path.Combine(directory, $"{name}.received.json");
        var normalized = Normalize(actual);

        Directory.CreateDirectory(directory);

        if (Environment.GetEnvironmentVariable("SHOPFLOW_ACCEPT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(approvedPath, normalized);
            File.Delete(receivedPath);
            return;
        }

        if (File.Exists(approvedPath) && Normalize(File.ReadAllText(approvedPath)) == normalized)
        {
            File.Delete(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, normalized);

        Assert.Fail(File.Exists(approvedPath)
            ? $"Schema of {name} changed. Review {receivedPath} against {approvedPath}; " +
              "if the change is backward compatible, replace the approved file. Otherwise publish a new contract version."
            : $"No approved schema snapshot for {name}. Review {receivedPath} and rename it to {Path.GetFileName(approvedPath)}.");
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd() + "\n";
}
