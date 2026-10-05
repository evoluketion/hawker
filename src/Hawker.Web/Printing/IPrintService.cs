using Hawker.Core.Domain;

namespace Hawker.Web.Printing;

public sealed record PrinterInfo(string Name, bool IsDefault);

/// <param name="PrinterName">Null prints to the system default printer.</param>
public sealed record PrintOptions(string? PrinterName, int Copies, bool Duplex, PaperSize PaperSize)
{
    public static PrintOptions For(Edition edition) =>
        new(string.IsNullOrWhiteSpace(edition.PrinterName) ? null : edition.PrinterName, Math.Max(1, edition.Copies), edition.Duplex, edition.PaperSize);
}

/// <param name="JobId">The print system's job reference, when it reports one.</param>
public sealed record PrintResult(string Printer, string? JobId);

public sealed class PrintException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends a finished PDF to a printer.</summary>
public interface IPrintService
{
    Task<IReadOnlyList<PrinterInfo>> ListPrintersAsync(CancellationToken cancellationToken = default);

    /// <exception cref="PrintException">The printer or print system rejected the job.</exception>
    Task<PrintResult> PrintAsync(string pdfPath, PrintOptions options, CancellationToken cancellationToken = default);
}
