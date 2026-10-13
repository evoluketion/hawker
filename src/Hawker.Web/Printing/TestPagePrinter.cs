using System.Net;
using Hawker.Core;
using Hawker.Core.Domain;
using Hawker.Web.Rendering;

namespace Hawker.Web.Printing;

/// <summary>Prints a one-page test sheet so printer settings can be checked without building an edition.</summary>
public sealed class TestPagePrinter(PdfRenderer pdfRenderer, IPrintService printer, HawkerPaths paths, TimeProvider time)
{
    /// <summary>One attempt, no retries, like every print the user starts by hand.</summary>
    public async Task<PrintResult> PrintAsync(PrintOptions options, CancellationToken cancellationToken = default)
    {
        var html = Html(options, time.GetLocalNow());
        var pdf = await pdfRenderer.RenderAsync(html, "Hawker test page", options.PaperSize, cancellationToken);
        var path = Path.Combine(paths.ArchiveDirectory, "hawker-test-page.pdf");
        await File.WriteAllBytesAsync(path, pdf, cancellationToken);
        return await printer.PrintAsync(path, options with { Copies = 1 }, cancellationToken);
    }

    private static string Html(PrintOptions options, DateTimeOffset now) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Hawker test page</title>
        <style>
          @page { size: {{(options.PaperSize == PaperSize.Letter ? "Letter" : "A4")}}; }
          body { font-family: {{NewspaperFontFallback}}; margin: 0; }
          h1 { font-size: 34pt; border-bottom: 3pt solid #000; padding-bottom: 4mm; }
          .box { border: 1pt solid #000; padding: 6mm; margin-top: 10mm; }
          dl { display: grid; grid-template-columns: 45mm 1fr; gap: 2mm 6mm; font-size: 12pt; }
          dt { font-weight: bold; }
        </style></head>
        <body>
          <h1>Hawker test page</h1>
          <p>If you can read this, Hawker can reach the printer.</p>
          <div class="box"><dl>
            <dt>Printer</dt><dd>{{WebUtility.HtmlEncode(options.PrinterName ?? "System default")}}</dd>
            <dt>Paper</dt><dd>{{options.PaperSize}}</dd>
            <dt>Tray</dt><dd>{{WebUtility.HtmlEncode(options.PaperSource ?? "Printer's choice")}}</dd>
            <dt>Double-sided</dt><dd>{{(options.Duplex ? "Yes (this is a single page, so only one side prints)" : "No")}}</dd>
            <dt>Printed</dt><dd>{{now:dddd d MMMM yyyy, HH:mm}}</dd>
          </dl></div>
        </body></html>
        """;

    private const string NewspaperFontFallback = "Georgia, 'Times New Roman', serif";
}
