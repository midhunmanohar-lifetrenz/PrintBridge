using System.Diagnostics;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class MacCupsTests
{
    [Theory]
    [InlineData("usb://Zebra%20Technologies/ZTC%20GK420d?serial=1", "USB")]
    [InlineData("socket://10.0.0.5:9100", "Network")]
    [InlineData("ipp://printer.local/ipp/print", "Network")]
    [InlineData("dnssd://TSC%20TE244._ipp._tcp.local/", "Network")]
    [InlineData("serial:/dev/cu.usbserial", "Serial")]
    [InlineData("cups-pdf:/", "File")]
    public void Device_uris_are_classified(string uri, string expected) => Assert.Equal(expected, CupsSpool.Classify(uri));

    [Fact]
    public void Usb_uri_gives_a_readable_device_name() =>
        Assert.Equal("Zebra Technologies ZTC GK420d", CupsSpool.DeviceName("usb://Zebra%20Technologies/ZTC%20GK420d?serial=1"));

    static string Sh(string file, string args)
    {
        var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(30000); return o;
    }

    /// <summary>Creates a real CUPS queue with a stock Zebra ZPL driver that points at a fake printer, then prints through it.</summary>
    [SkippableFact]
    public async Task Raw_bytes_go_through_a_real_cups_queue_unchanged()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Needs macOS.");
        using var sink = new TcpSink();
        const string name = "PB_Test_Zebra";
        try
        {
            var o = Sh("/usr/sbin/lpadmin", $"-p {name} -E -v socket://127.0.0.1:{sink.Port} -m drv:///sample.drv/zebra.ppd");
            Skip.If(o.Contains("not authorized", StringComparison.OrdinalIgnoreCase) || o.Contains("forbidden", StringComparison.OrdinalIgnoreCase), "Not allowed to add printers: " + o);

            var q = CupsSpool.List().SingleOrDefault(x => x.Name == name);
            Assert.NotNull(q);
            Assert.Equal("Network", q!.Connection);
            Assert.Equal(("Ready", "ok"), CupsSpool.QueueStatus(name));

            var payload = new byte[] { 0x1B, 0x40, 0x5E, 0x58, 0x41, 0x00, 0xFF, 0x80, 0x0A, 0x5E, 0x58, 0x5A };
            CupsSpool.WriteRaw(name, payload, "PrintBridge test");

            var got = await sink.WaitFor(1, 20000);
            Assert.Single(got);
            Assert.Equal(payload, got[0]);
        }
        finally { Sh("/usr/sbin/lpadmin", $"-x {name}"); }
    }

    [SkippableFact]
    public void Missing_queue_reports_not_installed()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Needs macOS.");
        Assert.Equal(("Not installed", "bad"), CupsSpool.QueueStatus("PB_Does_Not_Exist"));
    }
}
