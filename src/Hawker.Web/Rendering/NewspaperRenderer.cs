using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Hawker.Web.Rendering;

/// <summary>Renders the newspaper Razor template to a standalone HTML document.</summary>
public sealed class NewspaperRenderer(IServiceScopeFactory scopes, ILoggerFactory loggerFactory)
{
    public async Task<string> RenderHtmlAsync(NewspaperModel model)
    {
        // Blazor's renderer needs scoped services, so each render gets its own scope rather than the root provider.
        await using var scope = scopes.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(NewspaperTemplate.Model)] = model });
            var output = await renderer.RenderComponentAsync<NewspaperTemplate>(parameters);
            return output.ToHtmlString();
        });
    }
}
