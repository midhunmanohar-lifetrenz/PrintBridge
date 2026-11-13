using System.Diagnostics;
using System.Security.Principal;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

/// <summary>
/// Runs only on Windows as administrator (GitHub's windows-latest runner qualifies). It creates a real Windows printer
/// that uses the stock "Generic / Text Only" driver and a TCP port pointing at a fake printer, so a job travels through
/// the actual Windows spooler exactly as it would for a Zebra, TSC or TVS printer.
/// </summary>
public class WindowsSpoolerTests
{
    static bool IsAdmin() => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static string Ps(string script)
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(60000); return o;
    }

    /// <summary>
    /// Creates a Windows printer for a test. Build machines often have the Print Spooler service switched off and no
    /// "Generic / Text Only" driver, so both are set up first. The test is skipped, with the reason, when Windows still refuses.
    /// </summary>
    public static void CreatePrinter(string name, string port, string addPort)
    {
        var o = Ps("Set-Service Spooler -StartupType Manual; Start-Service Spooler; " +
                   "if (-not (Get-PrinterDriver -Name 'Generic / Text Only' -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name 'Generic / Text Only' }; " +
                   $"{addPort}; Add-Printer -Name '{name}' -DriverName 'Generic / Text Only' -PortName '{port}'; " +
                   $"if (Get-Printer -Name '{name}' -ErrorAction SilentlyContinue) {{ 'PRINTER-READY' }}");
        Skip.IfNot(o.Contains("PRINTER-READY"), "This Windows machine cannot create a test printer: " + o.Trim().Replace("\r", "").Replace("\n", " "));
    }

    [SkippableFact]
    public async Task Raw_bytes_go_through_the_real_spooler_unchanged()
    {
        Skip.IfNot(IsAdmin(), "Needs Windows and an elevated session.");
        using var sink = new TcpSink();
        const string name = "LTZ Test Label Printer", port = "LTZTEST_PORT";
        try
        {
            CreatePrinter(name, port, $"Add-PrinterPort -Name {port} -PrinterHostAddress 127.0.0.1 -PortNumber {sink.Port}");

            var q = WinSpool.List().SingleOrDefault(x => x.Name == name);
            Assert.NotNull(q);
            Assert.Equal(port, q!.Port);

            var payload = new byte[] { 0x1B, 0x40, 0x5E, 0x58, 0x41, 0x00, 0xFF, 0x80, 0x0A, 0x5E, 0x58, 0x5A };
            WinSpool.WriteRaw(name, payload, "LTZ automated test");

            var got = await sink.WaitFor(1, 20000);
            Assert.Single(got);
            Assert.Equal(payload, got[0]);      // the driver must not alter raw data
        }
        finally { Ps($"Remove-Printer -Name '{name}' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name {port} -ErrorAction SilentlyContinue"); }
    }

    [SkippableFact]
    public async Task Registry_finds_the_spooler_printer_and_job_manager_prints_through_it()
    {
        Skip.IfNot(IsAdmin(), "Needs Windows and an elevated session.");
        using var sink = new TcpSink();
        const string name = "LTZ Test BPLE Printer", port = "LTZTEST_PORT2";
        try
        {
            CreatePrinter(name, port, $"Add-PrinterPort -Name {port} -PrinterHostAddress 127.0.0.1 -PortNumber {sink.Port}");
            var store = new SettingsStore(); var reg = new PrinterRegistry(store); await reg.RefreshAsync();
            var p = reg.Find("spool:" + name);
            Assert.NotNull(p);
            var r = await new JobManager(reg, store).PrintAsync(p!, Languages.Encode(Languages.Sample(PrintLanguage.Bple), PrintLanguage.Bple), "automated test");
            Assert.True(r.Ok, r.Message);
            Assert.Contains("TEST LABEL", System.Text.Encoding.UTF8.GetString((await sink.WaitFor(1, 20000)).Single()));
        }
        finally { Ps($"Remove-Printer -Name '{name}' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name {port} -ErrorAction SilentlyContinue"); }
    }

    [SkippableFact]
    public void Spooler_listing_does_not_throw_on_windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        var l = WinSpool.List(); Assert.NotNull(l);
    }
}
