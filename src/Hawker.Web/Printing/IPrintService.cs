using Hawker.Core.Domain;

namespace Hawker.Web.Printing;

public sealed record PrinterInfo(string Name, bool IsDefault);

/// <param name="PrinterName">Null prints to the system default printer.</param>
/// <param name="PaperSource">The tray to feed from, as the printer names it; null lets the printer choose.</param>
public sealed record PrintOptions(string? PrinterName, int Copies, bool Duplex, PaperSize PaperSize, string? PaperSource = null)
{
    public static PrintOptions For(Edition edition) => new(
        string.IsNullOrWhiteSpace(edition.PrinterName) ? null : edition.PrinterName,
        Math.Max(1, edition.Copies),
        edition.Duplex,
        edition.PaperSize,
        string.IsNullOrWhiteSpace(edition.PaperSource) ? null : edition.PaperSource);
}

/// <param name="Name">What the print system calls the tray, e.g. "Main" or "Rear".</param>
public sealed record PaperSourceInfo(string Name, bool IsDefault);

/// <param name="JobId">The print system's job reference, when it reports one.</param>
public sealed record PrintResult(string Printer, string? JobId);

/// <param name="mayHavePrinted">
/// True when the job might have reached the printer despite the error (a timeout). Such failures are never retried.
/// </param>
public sealed class PrintException(string message, Exception? inner = null, bool mayHavePrinted = false) : Exception(message, inner)
{
    public bool MayHavePrinted { get; } = mayHavePrinted;

    /// <summary>
    /// A command that hangs may already have handed the job to the printer, and Hawker never retries prints, so the
    /// message tells the reader to check before reprinting.
    /// </summary>
    public const string TimedOutMessage =
        "The print command did not finish in time. The job may still have reached the printer, so check before reprinting.";
}

/// <summary>Sends a finished PDF to a printer.</summary>
public interface IPrintService
{
    Task<IReadOnlyList<PrinterInfo>> ListPrintersAsync(CancellationToken cancellationToken = default);

    /// <summary>The trays a printer reports; empty when it reports none or cannot be asked.</summary>
    /// <param name="printerName">Null for the system default printer.</param>
    Task<IReadOnlyList<PaperSourceInfo>> ListPaperSourcesAsync(string? printerName, CancellationToken cancellationToken = default);

    /// <exception cref="PrintException">The printer or print system rejected the job.</exception>
    Task<PrintResult> PrintAsync(string pdfPath, PrintOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks at the print queue after a job was accepted. Returns a description of a problem (job still waiting, printer
    /// stopped) or null if the job has gone through or the print system cannot tell. Never changes or resubmits the job.
    /// </summary>
    Task<string?> CheckJobAsync(PrintResult job, CancellationToken cancellationToken = default);
}
