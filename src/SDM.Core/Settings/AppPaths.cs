namespace SDM.Core.Settings;

/// <summary>
/// Where settings and the download list live. Normally %AppData%\SDM; in the portable version (a file named
/// "portable.txt" next to the program) in a "Data" folder beside the program, so the whole folder can move
/// to a USB stick or another PC.
/// </summary>
public static class AppPaths
{
    public const string PortableMarker = "portable.txt";

    private static string? _override;

    /// <summary>For tests: use this folder instead.</summary>
    public static void OverrideDataDirectory(string? path) => _override = path;

    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string DataDirectory => _override
        ?? (IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDM"));
}
