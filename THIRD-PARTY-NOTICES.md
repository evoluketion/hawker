# Third-party notices

Hawker includes or downloads the following third-party software and fonts. Each remains under its own licence.

## Bundled programs

| Component | Licence | Notes |
|---|---|---|
| [SumatraPDF](https://www.sumatrapdfreader.org) | GPL-3.0 | Windows package only, as an unmodified separate program in `tools/`, used for silent printing. Source: <https://github.com/sumatrapdfreader/sumatrapdf> |
| [.NET runtime](https://github.com/dotnet/runtime) | MIT | Included in the self-contained packages |
| [Playwright](https://github.com/microsoft/playwright-dotnet) driver and Node.js | MIT (Playwright); MIT and others (Node.js) | `.playwright/` folder. Drives the browser that renders PDFs. |
| Chromium | BSD-3-Clause and others | Linux only. Downloaded by Playwright on first use, not shipped in the package. |

## Fonts

Embedded in the newspaper template, all under the [SIL Open Font License 1.1](https://openfontlicense.org). The licence texts are included as `Rendering/Fonts/OFL-*.txt`, both in the source and in the packages.

- Source Serif 4 (Adobe)
- Playfair Display (Claus Eggers Sørensen)
- Libre Franklin (Impallari Type)
- UnifrakturMaguntia (j. 'mach' wust)

## Libraries

| Package | Licence |
|---|---|
| Anthropic C# SDK | MIT |
| AngleSharp | MIT |
| SmartReader | Apache-2.0 |
| SkiaSharp | MIT |
| QRCoder | MIT |
| Cronos | MIT |
| CronExpressionDescriptor | MIT |
| Microsoft.EntityFrameworkCore.Sqlite, ASP.NET Core, System.Drawing.Common | MIT |
| Serilog.AspNetCore, Serilog.Sinks.File | Apache-2.0 |
