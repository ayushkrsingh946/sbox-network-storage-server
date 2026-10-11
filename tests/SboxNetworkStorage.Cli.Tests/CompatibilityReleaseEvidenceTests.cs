using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class CompatibilityReleaseEvidenceTests
{
    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SboxNetworkStorage.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }
    private static async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "compat-release.py"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python 3 is required for compatibility release gates.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }
    [Fact]
    public async Task Matrix_is_schema_valid_and_release_requires_exact_commit_evidence()
    {
        var valid = await RunAsync();
        Assert.Equal(0, valid.Exit);
        var missing = await RunAsync("--server", "0.5.0", "--commit", new string('a', 40));
        Assert.NotEqual(0, missing.Exit);
        Assert.Contains("No editor-verified pass", missing.Output);
        var path = Path.GetTempFileName();
        try
        {
            var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "compat", "library-matrix.json")))!;
            matrix["tested"] = new JsonArray(new JsonObject
            {
                ["server"] = "0.5.0", ["serverCommit"] = new string('a', 40), ["library"] = "legacy", ["revision"] = 309133,
                ["engine"] = "26.10.02", ["database"] = new JsonArray("sqlite", "postgres"), ["result"] = "pass",
                ["evidence"] = new JsonArray("https://github.com/sbox-cool/sbox-network-storage-server/issues/54")
            });
            File.WriteAllText(path, matrix.ToJsonString());
            var wrongCommit = await RunAsync("--matrix", path, "--server", "0.5.0", "--commit", new string('b', 40));
            Assert.NotEqual(0, wrongCommit.Exit);
            var rightCommit = await RunAsync("--matrix", path, "--server", "0.5.0", "--commit", new string('a', 40));
            Assert.Equal(0, rightCommit.Exit);
            Assert.Contains("26.10.02", rightCommit.Output);
            var source = Path.Combine(RepositoryRoot(), "compat", "library-matrix.json");
            Assert.Equal(0, (await RunAsync("--matrix", path, "--policy-source", source)).Exit);
            matrix["latest"] = "1.3.0";
            File.WriteAllText(path, matrix.ToJsonString());
            var changedPolicy = await RunAsync("--matrix", path, "--policy-source", source);
            Assert.NotEqual(0, changedPolicy.Exit);
            Assert.Contains("source policy", changedPolicy.Output);
            matrix["latest"] = null;
            matrix["tested"]![0]!["result"] = "ci-only";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.NotEqual(0, (await RunAsync("--matrix", path, "--server", "0.5.0", "--commit", new string('a', 40))).Exit);
            var explicitCi = await RunAsync("--matrix", path, "--server", "0.5.0", "--commit", new string('a', 40), "--allow-ci-only");
            Assert.Equal(0, explicitCi.Exit);
            Assert.Contains("CI-only release", explicitCi.Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Published_library_metadata_is_accepted_but_cannot_backdate_a_successor()
    {
        var path = Path.GetTempFileName();
        try
        {
            var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "compat", "library-matrix.json")))!;
            matrix["libraries"]!.AsArray().Add(new JsonObject
            {
                ["version"] = "1.1.0", ["revision"] = 310500, ["released"] = "2025-10-02",
                ["minServer"] = "0.5.0", ["status"] = "supported"
            });
            matrix["deprecated"] = new JsonArray(new JsonObject
            {
                ["range"] = "<1.1.0", ["announced"] = "2026-01-01", ["successorReleased"] = "2025-10-01",
                ["sunset"] = "2026-04-02", ["replacement"] = "1.1.0",
                ["issue"] = "https://github.com/sbox-cool/sbox-network-storage-server/issues/57"
            });
            File.WriteAllText(path, matrix.ToJsonString());
            var mismatch = await RunAsync("--matrix", path);
            Assert.NotEqual(0, mismatch.Exit);
            Assert.Contains("successor date", mismatch.Output);
            matrix["deprecated"]![0]!["successorReleased"] = "2025-10-02";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.Equal(0, (await RunAsync("--matrix", path)).Exit);
            matrix["deprecated"]![0]!["range"] = ">1.0.0 ===2.0.0";
            File.WriteAllText(path, matrix.ToJsonString());
            var malformed = await RunAsync("--matrix", path);
            Assert.NotEqual(0, malformed.Exit);
            Assert.Contains("range token", malformed.Output);
            matrix["deprecated"]![0]!["range"] = "<1.1.0";
            matrix["libraries"]![1]!["minServer"] = "2147483648.0.0";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.NotEqual(0, (await RunAsync("--matrix", path)).Exit);
            matrix["libraries"]![1]!["minServer"] = "";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.NotEqual(0, (await RunAsync("--matrix", path)).Exit);
        }
        finally { File.Delete(path); }
    }
}
