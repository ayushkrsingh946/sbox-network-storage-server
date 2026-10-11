namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>Finds files in the repository checkout the tests run from (the matrix, its schema, scripts).</summary>
internal static class CompatTestPaths
{
    public static string RepoRoot { get; } = FindRoot();

    public static string Matrix => Path.Combine(RepoRoot, "compat", "library-matrix.json");

    public static string Schema => Path.Combine(RepoRoot, "compat", "library-matrix.schema.json");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "compat")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("The repository root (global.json + compat/) was not found above " + AppContext.BaseDirectory);
    }
}
