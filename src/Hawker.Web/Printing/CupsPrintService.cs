using System.Text.RegularExpressions;
using Hawker.Core.Domain;

namespace Hawker.Web.Printing;

/// <summary>Prints through CUPS with <c>lp</c> and lists printers with <c>lpstat</c> (Linux, and macOS).</summary>
public sealed partial class CupsPrintService(IProcessRunner runner, ILogger<CupsPrintService> logger) : IPrintService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<PrinterInfo>> ListPrintersAsync(CancellationToken cancellationToken = default)
    {
        var printers = await RunAsync("lpstat", ["-e"], cancellationToken);
        var defaultPrinter = await RunAsync("lpstat", ["-d"], cancellationToken);
        var defaultName = DefaultPrinter().Match(defaultPrinter.StandardOutput) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

        return printers.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new PrinterInfo(name, name == defaultName))
            .ToList();
    }

    public async Task<PrintResult> PrintAsync(string pdfPath, PrintOptions options, CancellationToken cancellationToken = default)
    {
        var arguments = BuildArguments(pdfPath, options);
        var result = await RunAsync("lp", arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PrintException($"Printing failed: {FirstLine(result.StandardError) ?? $"lp exited with code {result.ExitCode}"}");
        }

        var jobId = JobId().Match(result.StandardOutput) is { Success: true } job ? job.Groups[1].Value : null;
        logger.LogInformation("Sent {Pdf} to {Printer} as job {Job}", pdfPath, options.PrinterName ?? "default printer", jobId);
        return new PrintResult(options.PrinterName ?? "default printer", jobId);
    }

    public static IReadOnlyList<string> BuildArguments(string pdfPath, PrintOptions options)
    {
        var arguments = new List<string>();
        if (options.PrinterName is { } printer)
        {
            arguments.AddRange(["-d", printer]);
        }

        arguments.AddRange(["-n", options.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        // Long-edge duplex reads like a book when the pages are stapled in the top-left corner.
        arguments.AddRange(["-o", options.Duplex ? "sides=two-sided-long-edge" : "sides=one-sided"]);
        arguments.AddRange(["-o", options.PaperSize == PaperSize.Letter ? "media=Letter" : "media=A4"]);
        // Copies come out as complete sets, ready to staple, rather than all page 1s then all page 2s.
        arguments.AddRange(["-o", "collate=true"]);
        arguments.AddRange(["-o", "fit-to-page"]);
        arguments.Add("--");
        arguments.Add(pdfPath);
        return arguments;
    }

    private async Task<ProcessResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(command, arguments, CommandTimeout, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PrintException($"'{command}' was not found. Install CUPS (the cups-client package) to print.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new PrintException(PrintException.TimedOutMessage, ex);
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    // "system default destination: Office_Laser"
    [GeneratedRegex(@"default destination:\s*(.+)")]
    private static partial Regex DefaultPrinter();

    // "request id is Office_Laser-42 (1 file(s))"
    [GeneratedRegex(@"request id is (\S+)")]
    private static partial Regex JobId();
}
