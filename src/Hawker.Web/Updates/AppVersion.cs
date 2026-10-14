using System.Reflection;

namespace Hawker.Web.Updates;

/// <summary>Hawker's own version, and the versions in release tags (0.3.0 or v0.3.0), as three-part numbers.</summary>
public static class AppVersion
{
    public static Version Current { get; } = Parse(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()) ?? new Version(0, 0, 0);

    /// <summary>Reads 1.2, 1.2.3 or v1.2.3, ignoring any suffix such as -beta or +commit.</summary>
    public static Version? Parse(string? text)
    {
        var trimmed = (text ?? "").Trim().TrimStart('v', 'V');
        var end = trimmed.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
        {
            trimmed = trimmed[..end];
        }

        return Version.TryParse(trimmed, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : null;
    }

    public static string Display(Version version) => version.ToString(3);
}
