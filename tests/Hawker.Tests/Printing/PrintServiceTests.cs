using System.ComponentModel;
using Hawker.Core.Domain;
using Hawker.Web.Printing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawker.Tests.Printing;

public class PrintServiceTests
{
    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(respond(fileName, arguments));
        }
    }

    private static readonly PrintOptions OfficeDuplexA4 = new("Office Laser", 2, Duplex: true, PaperSize.A4);

    [Fact]
    public void Cups_arguments_set_printer_copies_duplex_paper_and_collation()
    {
        var args = CupsPrintService.BuildArguments("/data/archive/The Morning Hawker/2026-10-05_0600.pdf", OfficeDuplexA4);

        Assert.Equal(
            ["-d", "Office Laser", "-n", "2", "-o", "sides=two-sided-long-edge", "-o", "media=A4", "-o", "collate=true", "-o", "fit-to-page",
             "--", "/data/archive/The Morning Hawker/2026-10-05_0600.pdf"],
            args);
    }

    [Fact]
    public void Cups_default_printer_single_sided_letter()
    {
        var args = CupsPrintService.BuildArguments("/tmp/x.pdf", new PrintOptions(null, 1, Duplex: false, PaperSize.Letter));

        Assert.DoesNotContain("-d", args);
        Assert.Contains("sides=one-sided", args);
        Assert.Contains("media=Letter", args);
    }

    [Fact]
    public async Task Cups_print_returns_the_job_id()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(0, "request id is Office_Laser-42 (1 file(s))\n", ""));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        var result = await service.PrintAsync("/tmp/x.pdf", OfficeDuplexA4);

        Assert.Equal("Office_Laser-42", result.JobId);
        Assert.Equal("lp", runner.Calls.Single().File);
    }

    [Fact]
    public async Task Cups_errors_become_print_exceptions_with_the_reason()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(1, "", "lp: The printer or class does not exist.\n"));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        var error = await Assert.ThrowsAsync<PrintException>(() => service.PrintAsync("/tmp/x.pdf", OfficeDuplexA4));
        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public async Task Missing_cups_tools_give_a_helpful_message()
    {
        var runner = new FakeRunner((_, _) => throw new Win32Exception(2, "No such file or directory"));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        var error = await Assert.ThrowsAsync<PrintException>(() => service.PrintAsync("/tmp/x.pdf", OfficeDuplexA4));
        Assert.Contains("Install CUPS", error.Message);
    }

    [Fact]
    public async Task A_timed_out_print_warns_to_check_before_reprinting()
    {
        var runner = new FakeRunner((_, _) => throw new TimeoutException("lp did not finish"));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        var error = await Assert.ThrowsAsync<PrintException>(() => service.PrintAsync("/tmp/x.pdf", OfficeDuplexA4));
        Assert.Equal(PrintException.TimedOutMessage, error.Message);
        Assert.Single(runner.Calls); // never retried
    }

    [Fact]
    public async Task A_failed_print_is_attempted_exactly_once()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(1, "", "lp: Unable to print."));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        await Assert.ThrowsAsync<PrintException>(() => service.PrintAsync("/tmp/x.pdf", OfficeDuplexA4));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Cups_lists_printers_and_marks_the_default()
    {
        var runner = new FakeRunner((_, args) => args[0] == "-e"
            ? new ProcessResult(0, "Office_Laser\nKitchen_Inkjet\n", "")
            : new ProcessResult(0, "system default destination: Kitchen_Inkjet\n", ""));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        var printers = await service.ListPrintersAsync();

        Assert.Equal([new PrinterInfo("Office_Laser", false), new PrinterInfo("Kitchen_Inkjet", true)], printers);
    }

    [Fact]
    public async Task Cups_with_no_printers_lists_none()
    {
        // What lpstat really prints with no printers configured: nothing for -e, a message for -d, both exit 0.
        var runner = new FakeRunner((_, args) => args[0] == "-e"
            ? new ProcessResult(0, "", "")
            : new ProcessResult(0, "no system default destination\n", ""));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        Assert.Empty(await service.ListPrintersAsync());
    }

    [Fact]
    [Trait("Category", "System")]
    public async Task Real_lpstat_lists_printers_without_error()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/lpstat"))
        {
            return;
        }

        var printers = await new CupsPrintService(new ProcessRunner(), NullLogger<CupsPrintService>.Instance).ListPrintersAsync();

        Assert.True(printers.Count(p => p.IsDefault) <= 1);
    }

    [Fact]
    public void Cups_feeds_from_the_chosen_tray()
    {
        var args = CupsPrintService.BuildArguments("/tmp/x.pdf", OfficeDuplexA4 with { PaperSource = "Main" });

        Assert.Contains("InputSlot=Main", args);
        Assert.DoesNotContain(CupsPrintService.BuildArguments("/tmp/x.pdf", OfficeDuplexA4), a => a.StartsWith("InputSlot="));
    }

    [Fact]
    public void Trays_are_read_from_lpoptions_with_the_default_marked()
    {
        // Real output from a Canon TS5300 series.
        var output = """
            PageSize/Media Size: 3.5x5 *A4 Letter
            InputSlot/Media Source: *Auto Main Rear
            Duplex/Duplex: *None DuplexNoTumble DuplexTumble
            """;

        Assert.Equal(
            [new PaperSourceInfo("Auto", true), new PaperSourceInfo("Main", false), new PaperSourceInfo("Rear", false)],
            CupsPrintService.ParsePaperSources(output));
        Assert.Empty(CupsPrintService.ParsePaperSources("PageSize/Media Size: *A4 Letter"));
    }

    [Fact]
    public async Task Cups_asks_the_named_printer_for_its_trays()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(0, "InputSlot/Media Source: *Auto Main Rear\n", ""));
        var service = new CupsPrintService(runner, NullLogger<CupsPrintService>.Instance);

        var trays = await service.ListPaperSourcesAsync("TS5300_series");

        Assert.Equal(3, trays.Count);
        Assert.Equal("lpoptions", runner.Calls[0].File);
        Assert.Equal(["-p", "TS5300_series", "-l"], runner.Calls[0].Args);
    }

    [Fact]
    public void Sumatra_feeds_from_the_chosen_bin()
    {
        var args = WindowsPrintService.BuildArguments("paper.pdf", OfficeDuplexA4 with { PaperSource = "Cassette, upper" });

        Assert.Contains("duplexlong,2x,paper=A4,fit,bin=Cassette  upper", args);
    }

    [Fact]
    [Trait("Category", "System")]
    public async Task Real_lpoptions_lists_trays_for_each_installed_printer()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/lpoptions"))
        {
            return;
        }

        var service = new CupsPrintService(new ProcessRunner(), NullLogger<CupsPrintService>.Instance);
        foreach (var printer in await service.ListPrintersAsync())
        {
            var trays = await service.ListPaperSourcesAsync(printer.Name);
            Assert.True(trays.Count(t => t.IsDefault) <= 1, $"{printer.Name} reported more than one default tray");
        }
    }

    [Fact]
    public void Sumatra_arguments_print_silently_with_settings()
    {
        var args = WindowsPrintService.BuildArguments(@"C:\ProgramData\Hawker\archive\paper.pdf", OfficeDuplexA4);

        Assert.Equal(
            ["-print-to", "Office Laser", "-print-settings", "duplexlong,2x,paper=A4,fit", "-silent", "-exit-on-print", @"C:\ProgramData\Hawker\archive\paper.pdf"],
            args);
    }

    [Fact]
    public void Sumatra_uses_the_default_printer_when_none_is_named()
    {
        var args = WindowsPrintService.BuildArguments("paper.pdf", new PrintOptions(null, 1, Duplex: false, PaperSize.Letter));

        Assert.Equal(["-print-to-default", "-print-settings", "simplex,1x,paper=letter,fit", "-silent", "-exit-on-print", "paper.pdf"], args);
    }

    [Fact]
    public void Configured_sumatra_path_must_exist()
    {
        Assert.Null(WindowsPrintService.FindSumatra("/definitely/not/here/SumatraPDF.exe"));
    }

    [Fact]
    public void Print_options_come_from_the_edition()
    {
        var edition = new Edition { Name = "E", PrinterName = "  ", Copies = 0, Duplex = false, PaperSize = PaperSize.Letter, PaperSource = " " };

        Assert.Equal(new PrintOptions(null, 1, false, PaperSize.Letter, null), PrintOptions.For(edition));
        Assert.Equal("Main", PrintOptions.For(new Edition { Name = "E", PaperSource = "Main" }).PaperSource);
    }

    [Fact]
    [Trait("Category", "System")]
    public async Task Real_lp_rejects_an_unknown_printer()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/lp"))
        {
            return;
        }

        var service = new CupsPrintService(new ProcessRunner(), NullLogger<CupsPrintService>.Instance);
        var pdf = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<PrintException>(() =>
                service.PrintAsync(pdf, OfficeDuplexA4 with { PrinterName = "Hawker_No_Such_Printer" }));
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
