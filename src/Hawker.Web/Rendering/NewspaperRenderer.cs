using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Hawker.Web.Rendering;

/// <summary>Renders the newspaper Razor template to a standalone HTML document.</summary>
public sealed class NewspaperRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
{
    public async Task<string> RenderHtmlAsync(NewspaperModel model)
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(NewspaperTemplate.Model)] = model });
            var output = await renderer.RenderComponentAsync<NewspaperTemplate>(parameters);
            return output.ToHtmlString();
        });
    }
}
