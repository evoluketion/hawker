# Hawker

Hawker turns your RSS feeds into a printed newspaper. On the schedule you choose, it gathers the latest stories, lays them out like a broadsheet and sends the paper to your printer, ready to staple.

Hawker never writes, rewrites or summarises anything. Headlines and articles print exactly as their sources published them. If you add a Claude API key, Claude acts only as the layout editor: it picks the front-page story, groups stories into sections and decides where long stories are cut.

## What it does

- **Feeds:** any RSS or Atom feed. Full articles are fetched from each story's web page, with the feed's own text as a fallback. Live blogs are skipped by default. Pages that aren't articles, such as product pages, never lead the paper and print as briefs, and a feed can leave them out altogether.
- **Editions:** a newspaper made from chosen feeds. For each edition you set:
  - **How many articles:** 5, 10, 15, 20, 30 or any number. Expect about 1½ stories per A4 page with pictures.
  - **Most recent or most popular:** popularity uses scores where feeds have them, such as Hacker News points through [hnrss.org](https://hnrss.org), with an optional minimum score.
  - **How far back to look:** 12 hours, 24 hours, 2 days, 3 days, 1 week or custom.
  - **When it prints:** once a day, several times a day, weekly on chosen days, monthly, or a custom cron expression, in any time zone.
  - **Printing:** printer, copies, double-sided, A4 or Letter, and black-and-white pictures.
- **QR codes:** every story ends with a QR code to its original article, so it can be read or shared online. Cut stories say "Continued online".
- **Archive:** every paper is saved as a PDF. Open, download or print any of them again.
- **Previews:** build a paper without printing it.

## Requirements

|  | Windows 10/11 (64-bit) | Linux (64-bit) |
|---|---|---|
| Printing | Bundled SumatraPDF, using any printer installed in Windows | CUPS (`lp`), installed on most desktops |
| PDF rendering | Microsoft Edge (already installed) | Chromium, downloaded automatically on first use (about 115 MB) |
| .NET | Included in the package | Included in the package |

The computer needs to be on and awake when a paper is due.

## Getting started

Download the latest release for [Windows](https://github.com/evoluketion/hawker/releases/latest/download/hawker-win-x64.zip) or [Linux](https://github.com/evoluketion/hawker/releases/latest/download/hawker-linux-x64.tar.gz), or pick a version from [all releases](https://github.com/evoluketion/hawker/releases).

### Windows

1. Unzip `hawker-win-x64.zip` somewhere permanent, for example `C:\Hawker`.
2. Double-click **Start Hawker.cmd**. A console window opens and, once Hawker is ready, the dashboard opens in your browser at <http://localhost:5080>.
3. Keep the console window open (you can minimise it). Closing it stops Hawker, and papers only print while Hawker is running.

To run Hawker without a console window, double-click **Start Hawker (headless).cmd** instead. Hawker then appears only as an icon in the notification area: click it to open the dashboard, or right-click it and choose **Quit Hawker** to stop it.

### Linux

```bash
tar -xzf hawker-linux-x64.tar.gz
cd hawker-<version>-linux-x64
./start-hawker.sh
```

The dashboard opens at <http://localhost:5080>. Press Ctrl+C in the terminal to stop Hawker.

To run Hawker without a terminal, start it with `./start-hawker.sh --headless`. The terminal can then be closed, and Hawker appears only as a tray icon: click it to open the dashboard, or choose **Quit Hawker** from its menu to stop it. KDE Plasma, Xfce, Cinnamon and most other desktops show the icon. Stock GNOME needs the [AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/) for it. Without a tray, Hawker still runs and prints; open the dashboard yourself, and stop Hawker with `pkill Hawker.Web`.

### First steps in the dashboard

1. **Feeds → Add feed:** paste a feed address and press **Test feed** to see what Hawker reads.
2. **Editions → New edition:** choose feeds, the number of articles, the look-back range, the schedule and the printer. Then press **Save and preview**.
3. **Settings:** print a test page. Optionally, add a Claude API key for smarter layouts.

If you start Hawker while it's already running, the second copy exits and the dashboard simply opens. In headless mode it shows a notice instead.

If you quit from the tray icon while a paper is being made, Hawker asks first. Quitting stops that paper, and you can print it again from the archive.

## Printing and failures

- Double-sided printing flips on the long edge, so stapled papers read like a book. Copies come out as complete sets.
- If a print fails, the paper stays in the archive marked **Built, not printed**, with the reason. Its stories remain available for the next edition. Print it again from the archive when the printer is ready.
- **Automatic print retries are off (0) by default**, because a printer can report an error after it has already received a job, which would mean duplicate papers. You can turn retries on in Settings after confirming a warning. A print that timed out is never retried.
- On Linux, Hawker checks the print queue a few minutes after printing and notes stuck jobs (for example "printer offline") in the run's log. It never resubmits a job.
- If the computer was off or asleep when a paper was due, Hawker prints it when it next starts, if it's no more than 2 hours late (adjustable in Settings). Older papers are skipped, so a long absence doesn't produce a pile of stale papers.

## Claude (optional)

Add an API key from [platform.claude.com](https://platform.claude.com/settings/keys) in **Settings**, or set the `ANTHROPIC_API_KEY` environment variable, which takes precedence. The key saved in Settings is stored encrypted on this computer.

A Claude Pro or Max subscription doesn't include API access, and Anthropic doesn't allow apps like Hawker to sign in with a subscription. Create an API account with the same email, add a little prepaid credit (billed separately from your subscription) and create a key.

- **Model:** Claude Opus 5.5 by default, changeable in Settings. A layout costs a few cents per paper.
- **What Claude sees:** each story's headline, source, date, score, word counts and its first ~60 words.
- **What Claude returns:** article IDs, sizes, cut points and section names chosen from a fixed list. It has no way to add text to the paper.
- **If Claude is unavailable or fails,** the standard layout is used and the run log says so.

## Where things are kept

| | Windows | Linux |
|---|---|---|
| Database, archive, logs | `C:\ProgramData\Hawker` | `~/.local/share/hawker` |

The **Settings → About** panel shows the exact paths. Logs roll daily and are kept for 30 days.

## Configuration

Most settings live in the dashboard. A few advanced ones go in `appsettings.json` next to the program, or in environment variables. Use a double underscore for nesting, for example `Hawker__DataDirectory`.

| Setting | Default | Purpose |
|---|---|---|
| `Hawker:DataDirectory` | see above | Where the database, archive and logs go |
| `Kestrel:Endpoints:Http:Url` | `http://localhost:5080` | The dashboard address. It only listens on this computer. |
| `Hawker:Headless` | `false` (`--headless` turns it on) | Run with only a tray icon and no console window. Never opens the browser by itself. |
| `Hawker:OpenDashboardOnStart` | `false` (the start scripts turn it on) | Open the dashboard in your browser once Hawker has started |
| `Hawker:Pdf:BrowserChannel` | `msedge` on Windows | Use an installed browser, such as `chrome`, for PDF rendering |
| `Hawker:Pdf:ExecutablePath` | none | Path to a specific Chromium-based browser |
| `Hawker:Printing:SumatraPath` | bundled `tools\SumatraPDF.exe` | A different SumatraPDF |

## Troubleshooting

- **Nothing prints:** open the paper in **Archive**; its log says what happened. Then try **Settings → Print test page**. On Linux, run `lpstat -p` to check the printer is enabled.
- **"SumatraPDF was not found":** make sure `tools\SumatraPDF.exe` is still next to `Hawker.Web.exe`, or set `Hawker:Printing:SumatraPath`.
- **PDF rendering fails on Linux:** the first paper downloads Chromium. If your distribution is missing libraries Chromium needs, install it with your package manager and set `Hawker:Pdf:BrowserChannel` to `chrome` or `Hawker:Pdf:ExecutablePath` to its path.
- **"Port in use":** Hawker is already running. Open <http://localhost:5080>.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test                        # run the tests
dotnet run --project src/Hawker.Web   # run in development (data goes in ./data)
build/package.sh                   # build both packages into artifacts/
build/package.sh win-x64           # or just one
```

The packaging script runs on Linux, macOS, or Windows with Git Bash. It downloads SumatraPDF for the Windows package.

### Releasing

Publish a release on GitHub with a version tag such as `0.2.0` (a leading `v` is fine). The **Release** workflow runs the tests, builds both packages with that version and attaches them to the release. The packages are attached under fixed names (`hawker-win-x64.zip`, `hawker-linux-x64.tar.gz`), which the download links above use.

Project layout:

- `src/Hawker.Core`: feeds, article extraction, selection, layout and scheduling. No web dependencies.
- `src/Hawker.Web`: the dashboard, the newspaper template, PDF rendering, printing and the scheduler.
- `tests/Hawker.Tests`: unit and integration tests.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the licences of bundled software and fonts.
