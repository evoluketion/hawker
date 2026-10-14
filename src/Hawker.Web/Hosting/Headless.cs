namespace Hawker.Web.Hosting;

/// <summary>
/// Headless mode: no console window, and the tray icon is the only UI. It is set with <c>Hawker:Headless</c> or the
/// <c>--headless</c> switch.
/// </summary>
public static class Headless
{
    public const string Switch = "--headless";

    /// <summary>Rewrites <c>--headless</c> as a setting, so the host's command-line configuration picks it up.</summary>
    public static string[] TranslateArgs(string[] args) =>
        args.Select(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase) ? "--Hawker:Headless=true" : a).ToArray();

    public static bool IsOn(IConfiguration configuration) => configuration.GetValue<bool>("Hawker:Headless");
}
