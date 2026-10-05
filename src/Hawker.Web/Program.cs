using System.Net;
using Hawker.Core;
using Hawker.Core.Articles;
using Hawker.Core.Data;
using Hawker.Core.Feeds;
using Hawker.Core.Layout;
using Hawker.Core.Selection;
using Hawker.Web.Components;
using Hawker.Web.Editions;
using Hawker.Web.Rendering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts in System32, so anchor config and static files to the app folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

builder.Host.UseWindowsService(o => o.ServiceName = "Hawker");
builder.Host.UseSystemd();

var paths = new HawkerPaths(builder.Configuration["Hawker:DataDirectory"] ?? DefaultDataDirectory());
builder.Services.AddSingleton(paths);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.LogDirectory, "hawker-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

builder.Services.AddDbContextFactory<HawkerDbContext>(o => o.UseSqlite($"Data Source={paths.DatabasePath}"));

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Hawker")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory));
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<FeedFetcher>(http => ConfigureHttp(http, TimeSpan.FromSeconds(30)))
    .ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<PageExtractor>(http => ConfigureHttp(http, TimeSpan.FromSeconds(30)))
    .ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<ImageProcessor>(http => ConfigureHttp(http, TimeSpan.FromSeconds(30)))
    .ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddSingleton<SeenArticleStore>();
builder.Services.AddTransient<StorySelectionService>();
builder.Services.AddTransient<ArticlePreparationService>();
builder.Services.AddSingleton<ILayoutEditor, DeterministicLayoutEditor>();
builder.Services.Configure<PdfOptions>(builder.Configuration.GetSection("Hawker:Pdf"));
builder.Services.AddSingleton<NewspaperRenderer>();
builder.Services.AddSingleton<PdfRenderer>();
builder.Services.AddTransient<EditionComposer>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var db = app.Services.GetRequiredService<IDbContextFactory<HawkerDbContext>>().CreateDbContext())
{
    db.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation("Hawker data directory: {DataDirectory}", paths.DataDirectory);
app.Run();

static string DefaultDataDirectory() =>
    OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hawker")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hawker");

static void ConfigureHttp(HttpClient http, TimeSpan timeout)
{
    http.Timeout = timeout;
    // Many news sites refuse requests that don't look like a browser.
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Hawker/1.0; personal newspaper)");
    http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/*;q=0.8,*/*;q=0.7");
}

static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};
