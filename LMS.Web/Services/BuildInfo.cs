using System.Reflection;

namespace LMS.Web.Services;

/// <summary>Product/build identity stamped into the assembly at compile time (LMS-CMP-001).
/// Surfaced in the UI so a deployed instance can always be identified without shell access.</summary>
public static class BuildInfo
{
    private static readonly Assembly Asm = typeof(BuildInfo).Assembly;

    /// <summary>Semantic version plus build metadata, e.g. "1.0.0+build.42".</summary>
    public static string InformationalVersion { get; } =
        Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Asm.GetName().Version?.ToString() ?? "unknown";

    /// <summary>Just the semantic part, e.g. "1.0.0".</summary>
    public static string Version { get; } = InformationalVersion.Split('+')[0];

    /// <summary>Build number from the pipeline ("0" for a local build).</summary>
    public static string BuildNumber { get; } =
        InformationalVersion.Contains("+build.") ? InformationalVersion.Split("+build.")[^1] : "0";

    public static string Product { get; } =
        Asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Learning Management System";

    /// <summary>UTC build timestamp, taken from the compiled assembly file.</summary>
    public static DateTime BuiltUtc { get; } = GetBuildTime();

    /// <summary>One-line identity for footers, logs and support tickets.</summary>
    public static string Display => $"v{Version} (build {BuildNumber}) · {BuiltUtc:dd MMM yyyy}";

    private static DateTime GetBuildTime()
    {
        try
        {
            var path = Asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return File.GetLastWriteTimeUtc(path);
        }
        catch { /* single-file / trimmed publish — fall through */ }
        return DateTime.UtcNow;
    }
}
