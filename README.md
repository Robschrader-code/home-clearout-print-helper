# Home Clearout Print Helper v0.2

A portable Windows companion for Home Clearout Buddy. Test an installed Windows
printer, then connect it to the household label queue. A phone can explicitly send
labels to one designated computer, without a paid printing service or a browser
print dialog on that phone.

## Connect the household queue

1. Deploy the companion web PR and migrations 0005–0008 before connecting. In the
   signed-in web app, open **Labels → Connect / manage printing computers**.
2. Name this computer and create a connection code. In the helper choose your tested
   physical printer/paper mode, save settings, and open **Connected printing**.
3. Paste the code and save. In the web app, select **Use this computer**. Repeat setup
   separately on the second computer; only one is designated at a time.
4. Click **Enable processing** in the helper. Leave this window open. On a phone or
   desktop, open an item's label dialog and click **Send to Windows helper**.
5. The queue uses the selected label size; it does not inherit the manual test size.
   Check paper, alignment and QR. In the web app's Labels page, confirm checked output.
   Windows acceptance alone is never physical confirmation.

Processing starts paused on every launch. Closing Connected printing pauses it.
There is no Windows service, tray process, auto-start installation or external
publishing. Existing browser queue jobs stay manual unless explicitly sent to the helper.
PDF/OneNote/XPS file printers remain available for manual tests, not unattended jobs.
One job uses one sheet in Letter mode; this version does not lay out adhesive sheet grids.

The code contains the public backend configuration and a random print-only secret.
It grants only minimal label payloads, not photo/inventory queries or account access.
Only its SHA-256 hash is stored server-side. Windows DPAPI CurrentUser protects the
local credential in `connection.dpapi`; copying it to another account/computer will
not work. Never share the code. Revoke a lost connection in the web app. No password,
service-role key or household refresh token is stored by the helper.

A server lease and local single-instance guard prevent competing helpers. A flushed,
atomic `pending-print.json` records intent before any physical side effect. Network
acknowledgments can be retried; spooling cannot. An interrupted/uncertain job pauses
processing for owner review in Labels. Check Windows' queue and paper before resolving
it; paused/closed helpers cannot retract already accepted Windows jobs. If a computer
crashes while claiming/spooling, an owner can resolve the stale attempt after two
minutes, or a replacement helper marks it Uncertain after the old lease expires.
Submitted jobs can be confirmed immediately after checking physical output.

For lost/revoked credentials: resolve the outstanding job in the web app, pause the
helper, reset its local connection, revoke the old computer, and create a new code.
Reset preserves the old local journal for review and never cancels a Windows job.

## Try it on your computer

1. Download `HomeClearout-PrintHelper-v0.2.0-win-x64.zip` from the successful
   **Build Windows Print Helper** Actions run (instructions below).
2. Extract it and open **HomeClearout.PrintHelper.exe**. No installer, Visual Studio,
   administrator privileges or separately installed .NET runtime is needed.
3. Select your installed Brother laser printer, **Letter sheet**, and the default
   **2 × 1.5 inches** label size. A Windows driver must already be installed.
4. Leave the sample `HC-00001` / `Test Inventory Item`, or edit them. Set the QR URL
   you want to test. `https://example.com/` is an explicit placeholder, not an
   invented production item link; manual tests never invent a Home Clearout record.
5. Review the preview and click **Print Test Label** once. The selected label prints
   at its actual dimensions in the upper-left printable area of the Letter sheet.
6. Measure the output, check text and scan the QR using your phone. Confirm physical
   output only after checking it. Check the Windows queue before retrying a missing
   print to avoid duplicates. Put labels on packaging, not merchandise.

Microsoft Print to PDF is useful before paper testing and prompts for an output file.
Windows accepting a job does **not** mean the label physically printed. v0.2 cannot
detect empty paper, bad alignment or an offline printer that accepts queued jobs.

### Label sizes and thermal printers

Supported sizes: **2 × 1**, **2 × 1.5** (default), and **2 × 2 inches**. Choose **Letter
sheet (8.5 × 11)** for ordinary laser printers, or **Label roll** for matching media.
The app sends page/label dimensions in hundredths of an inch through PrintDocument;
it never scales the label to fill a Letter sheet. Leave printer-driver scaling at
100% and disable Fit/Shrink options. Use matching media, orientation and custom size
in the driver. A clipping error asks you to correct driver settings or test on Letter.

The app is printer-brand independent. Neither CLABEL nor Brother nor NIIMBOT hardware
is required for generation. Each physical driver's custom-size/margin behavior still
needs a real printer test. The screen preview uses Windows display DPI; monitor
calibration can differ from physical inches. Printed dimensions are checked separately.

### Settings and moving computers

Settings save on Print, Save settings and normal exit under:

`%LOCALAPPDATA%\HomeClearout\PrintHelper\settings.json`

The selected printer, label height, paper mode and test URL persist. Files are written
atomically; corrupt/missing settings fall back with a warning instead of preventing
startup. An unavailable preferred printer stays selected with a warning, so the helper
cannot silently switch to a different physical printer. Click Refresh after installing
or reconnecting a driver. Settings remain local; copying the application to another
computer does not copy printer selection or require any cloud account.

## Download a GitHub Actions build

1. Open this repository's **Actions** tab and select **Build Windows Print Helper**.
2. Open a successful run for `codex/windows-print-helper-v0.1` or `main`, preferably the
   commit linked in the pull request. For a first run GitHub may require enabling Actions.
3. In **Artifacts**, download **HomeClearout-PrintHelper-v0.2.0-win-x64** while signed in.
   Extract GitHub's artifact ZIP, then extract the portable ZIP inside it.
4. Keep the included quick-start/checksum/notices files for reference. Run the EXE.

Artifacts expire after 30 days; rerun the workflow or use Run workflow after it is on
main. The workflow also runs on pushes to Codex branches and PRs. It builds/tests on
a Windows runner and uploads both the portable application and test results/previews.
This uses GitHub Actions' existing account allowances; no cloud printing service exists.

This prototype is **unsigned**. Windows may show reputation/security prompts. Verify
the download source and compare `Get-FileHash .\HomeClearout.PrintHelper.exe -Algorithm SHA256`
with `SHA256SUMS.txt`. Do not disable Defender, SmartScreen or other protections.
Code-signing distribution is future work.

## Build and test from source

Development uses the pinned .NET SDK **10.0.402** on Windows (`global.json`). End users receive the bundled runtime.
.NET 10 LTS was chosen instead of near-end-of-support .NET 8; see Microsoft's
[support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

```powershell
dotnet restore tests/HomeClearout.PrintHelper.Tests --locked-mode
dotnet build tests/HomeClearout.PrintHelper.Tests -c Release --no-restore
dotnet run --project tests/HomeClearout.PrintHelper.Tests -c Release --no-build -- artifacts/test-results
./scripts/publish.ps1
./scripts/smoke-portable.ps1
```

The automated test executable exits nonzero on any failed test. It independently
decodes QR images with ZXing.Net, checks dimensions/layout at 300 and 203 dpi, settings
round trips/corruption, unavailable printers, and actual WinForms controls/preview.
Add `--print-pdf` after the test output folder to exercise the installed Microsoft
Print to PDF driver. This is optional in CI; no test sends a physical paper job.
Source dependencies are locked. The production app uses only QRCoder (MIT); ZXing.Net
is test-only. The small UI/printing source produces a larger portable EXE because it
contains the .NET/WindowsDesktop runtime. Native runtime extraction uses the user's
temporary folder; ordinary local write access there is needed, never administrator access.

Publishing creates `artifacts/publish/HomeClearout.PrintHelper.exe` and the portable
ZIP under `artifacts/`. The build is self-contained, single-file for execution; the
ZIP also retains quick-start, SHA-256, and upstream license notices. No installer.

## Windows compatibility

Windows 11 x64 is the locally tested environment. Windows 10 x64, including Surface
Pro 4 hardware, is an intended compatibility target requiring an actual device test.
The application uses WinForms/GDI and standard Windows printing, with no Windows 11-only
APIs. This is an application compatibility target, not a claim Microsoft supports every
Windows 10 edition/runtime combination. Normal Windows 10 Home/Pro support ended
[October 14, 2025](https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro).
Keep the machine on applicable Microsoft security support. See the current
[.NET OS support matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
Bundling a runtime does not restore OS security support. Do not bypass Windows protections.

## Architecture and later versions

- `LabelSpec` validates input and physical dimensions; `LabelRenderer` is shared by
  preview, test images and PrintDocument and reserves the QR's four-module quiet zone.
  Text shrinks only to a legible minimum; overly long/dense labels are rejected.
- `IPrinterService` isolates driver discovery and submission from the UI. The real
  service checks printer availability, printable area and Letter/custom media. One
  STA worker handles one explicit print request; the UI prevents repeat clicks while busy.
- `SettingsStore` uses local atomic JSON replacement, without passwords or tokens.
- The local `--diagnostics <folder>` switch renders a test label and reports bundled
  x64 runtime startup/printer discovery without opening a window or printing.

`PrintQueueClient` is a narrowly scoped HTTP adapter; `QueueProcessor` separates
claim, durable intent, fenced spooling and acknowledgment. `PrintJournal` preserves
ambiguous outcomes; `ConnectionStore` protects the scoped credential with DPAPI.
Tray/startup operation remains future work. No telemetry is included.

See [test results and hardware checklist](docs/TESTING.md), [quick start](QUICKSTART.txt),
and [third-party notices](THIRD-PARTY-NOTICES.txt).
