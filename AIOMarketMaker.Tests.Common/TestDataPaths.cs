using System.Runtime.CompilerServices;

namespace AIOMarketMaker.Tests.Common;

public static class TestDataPaths
{
    private static readonly string DataRoot = ResolveDataRoot();

    public static string Listings => Path.Combine(DataRoot, "Listings");
    public static string Search => Path.Combine(DataRoot, "Search");
    public static string Descriptions => Path.Combine(DataRoot, "Descriptions");
    public static string Verification => Path.Combine(DataRoot, "Listings", "Verification");
    public static string Root => DataRoot;

    private static string ResolveDataRoot()
    {
        // Walk up from this source file to find the Data/ directory in the Common project
        var thisFile = GetThisFilePath();
        var projectDir = Path.GetDirectoryName(thisFile)!;
        var dataDir = Path.Combine(projectDir, "Data");

        if (Directory.Exists(dataDir))
        {
            return dataDir;
        }

        throw new DirectoryNotFoundException(
            $"Test data directory not found at {dataDir}. " +
            "Ensure AIOMarketMaker.Tests.Common/Data/ exists.");
    }

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;
}
