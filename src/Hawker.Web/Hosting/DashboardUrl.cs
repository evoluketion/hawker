namespace Hawker.Web.Hosting;

public static class DashboardUrl
{
    /// <summary>The default must stay in step with the Kestrel endpoint in appsettings.json.</summary>
    public const string Default = "http://localhost:5080";

    public static string For(IConfiguration configuration) => configuration["Kestrel:Endpoints:Http:Url"] ?? Default;
}
