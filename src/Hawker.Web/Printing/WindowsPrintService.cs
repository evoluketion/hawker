using System.Runtime.Versioning;
using Hawker.Core.Domain;
using Microsoft.Extensions.Options;
using PrinterSettings = System.Drawing.Printing.PrinterSettings;

namespace Hawker.Web.Printing;

public sealed class PrintingOptions
{
    /// <summary>Full path to SumatraPDF.exe. When unset, the app folder and the usual install locations are searched.</summary>
    public string? SumatraPath { get; set; }
}

/// <summary>
/// Prints on Windows with SumatraPDF's command line, which prints a PDF silently (no dialog, no viewer window) with
/// copies, duplex and paper size, and works from a Windows service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrintService(IProcessRunner runner, IOptions<PrintingOptions> options, ILogger<WindowsPrintService> logger) : IPrintService
{
    private static readonly TimeSpan PrintTimeout = TimeSpan.FromMinutes(3);

    public Task<IReadOnlyList<PrinterInfo>> ListPrintersAsync(CancellationToken cancellationToken = default)
    {
        var defaultName = new PrinterSettings().PrinterName;
        IReadOnlyList<PrinterInfo> printers = PrinterSettings.InstalledPrinters
            .Cast<string>()
            .Select(name => new PrinterInfo(name, string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Task.FromResult(printers);
    }

    public async Task<PrintResult> PrintAsync(string pdfPath, PrintOptions printOptions, CancellationToken cancellationToken = default)
    {
        var sumatra = FindSumatra(options.Value.SumatraPath)
            ?? throw new PrintException("SumatraPDF was not found. Install it, or set Hawker:Printing:SumatraPath to SumatraPDF.exe.");

        var result = await runner.RunAsync(sumatra, BuildArguments(pdfPath, printOptions), PrintTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? $"exit code {result.ExitCode}" : result.StandardError.Trim();
            throw new PrintException($"Printing failed ({detail}). Check that the printer \"{printOptions.PrinterName ?? "default"}\" exists and is online.");
        }

        var printer = printOptions.PrinterName ?? new PrinterSettings().PrinterName;
        logger.LogInformation("Sent {Pdf} to {Printer}", pdfPath, printer);
        return new PrintResult(printer, null);
    }

    public static IReadOnlyList<string> BuildArguments(string pdfPath, PrintOptions options)
    {
        var arguments = new List<string>();
        if (options.PrinterName is { } printer)
        {
            arguments.AddRange(["-print-to", printer]);
        }
        else
        {
            arguments.Add("-print-to-default");
        }

        // Long-edge duplex reads like a book when stapled in the top-left corner; "fit" scales to the paper.
        var settings = string.Join(',',
            options.Duplex ? "duplexlong" : "simplex",
            $"{options.Copies}x",
            options.PaperSize == PaperSize.Letter ? "paper=letter" : "paper=A4",
            "fit");
        arguments.AddRange(["-print-settings", settings, "-silent", "-exit-on-print", pdfPath]);
        return arguments;
    }

    public static string? FindSumatra(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.exe"),
            Path.Combine(AppContext.BaseDirectory, "SumatraPDF.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumatraPDF", "SumatraPDF.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
