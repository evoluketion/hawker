using System.Net;
using Hawker.Core;
using Hawker.Web.Editions;
using Hawker.Web.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawker.Tests.Updates;

/// <summary>An update service talking to a fake GitHub, with its data folder in a temporary directory.</summary>
public sealed class UpdateFixture : IDisposable
{
    public UpdateFixture(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, string environment = "Production")
    {
        Respond = respond ?? (_ => Json(Release("99.0.0")));
        Paths = new HawkerPaths(Path.Combine(Path.GetTempPath(), $"hawker-update-test-{Guid.NewGuid():N}"));
        Lifetime = new FakeLifetime();
        Runs = new EditionRunQueue();
        Service = new UpdateService(new Factory(this), Paths, new ConfigurationBuilder().Build(), new Environment(environment), Lifetime, Runs,
            new LaunchArguments(["--headless"]), NullLogger<UpdateService>.Instance);
    }

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }
    public List<Uri> Requested { get; } = [];
    public HawkerPaths Paths { get; }
    public FakeLifetime Lifetime { get; }
    public EditionRunQueue Runs { get; }
    public UpdateService Service { get; }

    public static string Release(string tag, bool prerelease = false, bool draft = false) => $$"""
        {
          "tag_name": "{{tag}}", "name": "Hawker {{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/evoluketion/hawker/releases/tag/{{tag}}",
          "published_at": "2026-10-14T09:00:00Z",
          "body": "- Keeps headlines whole\n- Updates itself",
          "assets": [
            { "name": "hawker-win-x64.zip", "size": 1000, "digest": "sha256:ABCDEF", "browser_download_url": "https://github.com/evoluketion/hawker/releases/download/{{tag}}/hawker-win-x64.zip" },
            { "name": "hawker-linux-x64.tar.gz", "size": 2000, "browser_download_url": "https://github.com/evoluketion/hawker/releases/download/{{tag}}/hawker-linux-x64.tar.gz" },
            { "name": "insecure.zip", "size": 1, "browser_download_url": "http://example.com/insecure.zip" }
          ]
        }
        """;

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Paths.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Factory(UpdateFixture fixture) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(fixture));
    }

    private sealed class Handler(UpdateFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.Requested.Add(request.RequestUri!);
            return Task.FromResult(fixture.Respond(request));
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Hawker.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class FakeLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => new(canceled: true);
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public bool Stopped { get; private set; }

    public void StopApplication() => Stopped = true;
}
