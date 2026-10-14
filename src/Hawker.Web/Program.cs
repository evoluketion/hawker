using System.Net;
using Hawker.Core;
using Hawker.Core.Articles;
using Hawker.Core.Data;
using Hawker.Core.Feeds;
using Hawker.Core.Selection;
using Hawker.Web;
using Hawker.Web.Components;
using Hawker.Web.Editions;
using Hawker.Web.Hosting;
using Hawker.Web.Printing;
using Hawker.Web.Rendering;
using Hawker.Web.Settings;
using Hawker.Web.Tray;
using Hawker.Web.Tray.Linux;
using Hawker.Web.Tray.Windows;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Headless.TranslateArgs(args),
    // A published build (which has its own wwwroot) always uses its own folder, wherever it is started from. During
    // development, `dotnet run` uses the project folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() || Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
        ? AppContext.BaseDirectory
        : null,
});

builder.Host.UseWindowsService(o => o.ServiceName = "Hawker");
builder.Host.UseSystemd();

var paths = new HawkerPaths(builder.Configuration["Hawker:DataDirectory"] ?? DefaultDataDirectory());
builder.Services.AddSingleton(paths);

using var instance = SingleInstance.TryAcquire(paths.DataDirectory);
if (instance is null)
{
    var url = DashboardUrl.For(builder.Configuration);
    Console.WriteLine($"Hawker is already running (data folder {paths.DataDirectory}). Open {url} to use it.");
    if (OpenDashboardOnStart.IsOn(builder.Configuration))
    {
        new BrowserLauncher(Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserLauncher>.Instance).Open(url);
    }
    return 2;
}

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
builder.Services.AddSingleton<ClaudeSettingsService>();
builder.Services.AddSingleton<LayoutEditorResolver>();
builder.Services.AddSingleton<GeneralSettingsService>();
builder.Services.AddSingleton<ClaudeConnectionTester>();
builder.Services.AddTransient<EditionStore>();
builder.Services.AddTransient<RunHistory>();
builder.Services.AddTransient<FeedStore>();
builder.Services.AddTransient<TestPagePrinter>();
builder.Services.Configure<PdfOptions>(builder.Configuration.GetSection("Hawker:Pdf"));
builder.Services.AddSingleton<NewspaperRenderer>();
builder.Services.AddSingleton<PdfRenderer>();
builder.Services.AddTransient<IEditionComposer, EditionComposer>();
builder.Services.AddSingleton<EditionRunQueue>();
builder.Services.AddSingleton<RunEvents>();
builder.Services.AddTransient<EditionRunService>();
builder.Services.AddHostedService<EditionRunWorker>();
builder.Services.AddHostedService<EditionScheduler>();
builder.Services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
builder.Services.AddHostedService<OpenDashboardOnStart>();
builder.Services.AddHostedService<TrayHostedService>();
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<ITrayIcon, WindowsTrayIcon>();
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<ITrayIcon, StatusNotifierTrayIcon>();
}
else
{
    builder.Services.AddSingleton<ITrayIcon, NullTrayIcon>();
}
builder.Services.Configure<PrintingOptions>(builder.Configuration.GetSection("Hawker:Printing"));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IPrintService, WindowsPrintService>();
}
else
{
    builder.Services.AddSingleton<IPrintService, CupsPrintService>();
}

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
// Archived papers, for viewing in the browser or downloading. Only files inside the archive folder are served.
app.MapGet("/runs/{id:int}/pdf", async (int id, bool? download, IDbContextFactory<HawkerDbContext> dbFactory, HawkerPaths paths) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var run = await db.EditionRuns.Include(r => r.Edition).SingleOrDefaultAsync(r => r.Id == id);
    if (run?.PdfPath is not { } pdfPath)
    {
        return Results.NotFound();
    }

    var full = Path.GetFullPath(pdfPath);
    var archive = Path.GetFullPath(paths.ArchiveDirectory) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(archive, StringComparison.Ordinal) || !File.Exists(full))
    {
        return Results.NotFound();
    }

    var name = $"{EditionRunService.Slug(run.Edition?.Name ?? "edition")}-{Path.GetFileName(full)}";
    return Results.File(full, "application/pdf", download == true ? name : null, enableRangeProcessing: true);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation("Hawker data directory: {DataDirectory}", paths.DataDirectory);
try
{
    app.Run();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    // The port belongs to another program; nothing has been scheduled yet because the scheduler waits for startup.
    // The host (and its logger) is already disposed here, so write straight to the console.
    Console.Error.WriteLine($"Another program is already using {DashboardUrl.For(builder.Configuration)}. " +
        "Close it, or set Kestrel:Endpoints:Http:Url to a different address.");
    return 3;
}

return 0;

static bool IsAddressInUse(Exception? ex) => ex switch
{
    null => false,
    Microsoft.AspNetCore.Connections.AddressInUseException => true,
    AggregateException aggregate => aggregate.InnerExceptions.Any(IsAddressInUse),
    _ => IsAddressInUse(ex.InnerException),
};

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
