using System.Runtime.InteropServices;

namespace LtzPrintBridge.Core;

/// <summary>Windows spooler access (winspool.drv). RAW datatype sends bytes to the printer untouched by the driver.</summary>
public static class WinSpool
{
    const int PRINTER_ENUM_LOCAL = 0x2, PRINTER_ENUM_CONNECTIONS = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRINTER_INFO_2
    {
        public string pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
        public IntPtr pDevMode;
        public string pSepFile, pPrintProcessor, pDatatype, pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DOC_INFO_1 { public string pDocName; public string? pOutputFile; public string pDataType; }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buf, int size, out int needed, out int returned);
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool OpenPrinter(string name, out IntPtr h, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern int StartDocPrinter(IntPtr h, int level, ref DOC_INFO_1 di);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndDocPrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool AbortPrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool StartPagePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndPagePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool WritePrinter(IntPtr h, byte[] buf, int count, out int written);
    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetDefaultPrinter(System.Text.StringBuilder? buf, ref int size);

    public sealed record Queue(string Name, string Driver, string Port, uint Status, uint Attributes);

    public static List<Queue> List()
    {
        var list = new List<Queue>();
        if (!OperatingSystem.IsWindows()) return list;
        int flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out int needed, out _);
        if (needed <= 0) return list;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(flags, null, 2, buf, needed, out _, out int count)) return list;
            int sz = Marshal.SizeOf<PRINTER_INFO_2>();
            for (int i = 0; i < count; i++)
            {
                var pi = Marshal.PtrToStructure<PRINTER_INFO_2>(IntPtr.Add(buf, i * sz));
                list.Add(new Queue(pi.pPrinterName ?? "", pi.pDriverName ?? "", pi.pPortName ?? "", pi.Status, pi.Attributes));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public static string? DefaultName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        int size = 0; GetDefaultPrinter(null, ref size);
        if (size <= 0) return null;
        var sb = new System.Text.StringBuilder(size);
        return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    public static (string text, string level) Describe(uint status, uint attributes)
    {
        const uint WORK_OFFLINE = 0x400;
        // PRINTER_STATUS_* values from winspool.h
        if ((attributes & WORK_OFFLINE) != 0 || (status & 0x80) != 0) return ("Offline", "bad");
        if ((status & 0x10) != 0) return ("Paper out", "bad");
        if ((status & 0x8) != 0) return ("Paper jam", "bad");
        if ((status & 0x40) != 0) return ("Paper problem", "bad");
        if ((status & 0x400000) != 0) return ("Door open", "bad");
        if ((status & 0x1000) != 0) return ("Not available", "bad");
        if ((status & 0x2) != 0) return ("Error", "bad");
        if ((status & 0x100000) != 0) return ("Needs attention", "bad");
        if ((status & 0x1) != 0) return ("Paused", "warn");
        if ((status & 0x60000) != 0) return ("Ribbon or toner low", "warn");
        if ((status & 0x4600) != 0) return ("Printing", "ok");
        return ("Ready", "ok");
    }

    public static (string text, string level)? QueueStatus(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var q = List().FirstOrDefault(x => x.Name == name);
        return q == null ? ("Not installed", "bad") : Describe(q.Status, q.Attributes);
    }

    /// <summary>USB, serial, network or file, judged from the Windows port name (USB001, DOT4_001, COM1, IP_10.0.0.5, ...).</summary>
    public static string ClassifyPort(string port)
    {
        var p = (port ?? "").Trim().ToUpperInvariant();
        if (p.Length == 0) return "";
        if (p.StartsWith("USB") || p.StartsWith("DOT4") || p.Contains("USB")) return "USB";
        if (p.StartsWith("COM")) return "Serial";
        if (p.StartsWith("LPT")) return "Parallel";
        if (p.StartsWith("IP_") || p.StartsWith("WSD") || p.StartsWith("\\\\") || System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d{1,3}(\.\d{1,3}){3}") || p.Contains("HTTP")) return "Network";
        if (p.StartsWith("FILE") || p.StartsWith("PORTPROMPT") || p.StartsWith("NUL") || p.StartsWith("XPSPORT") || p.StartsWith("SHRFAX")) return "File";
        return "Other";
    }

    /// <summary>PDF, XPS, fax and OneNote printers cannot print labels and only add clutter.</summary>
    public static bool IsVirtual(string name, string port)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("print to pdf") || n.Contains("xps document writer") || n.Contains("onenote") || n.StartsWith("fax") || n.Contains("send to ")) return true;
        return ClassifyPort(port) == "File";
    }

    [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetJob(IntPtr h, int jobId, int level, IntPtr buf, int size, out int needed);

    /// <summary>Looks the job up in the Windows queue. A job that is no longer there has been delivered to the printer port.</summary>
    public static (string state, string message)? JobState(string queue, int jobId)
    {
        if (!OperatingSystem.IsWindows() || jobId <= 0) return null;
        if (!OpenPrinter(queue, out var h, IntPtr.Zero)) return null;
        try
        {
            GetJob(h, jobId, 1, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return ("printed", "The job left the Windows print queue.");   // ERROR_INVALID_PARAMETER: no such job any more
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetJob(h, jobId, 1, buf, needed, out _)) return ("printed", "The job left the Windows print queue.");
                // JOB_INFO_1: JobId (padded to pointer size), six string pointers, then the Status flags
                uint st = (uint)Marshal.ReadInt32(buf, IntPtr.Size * 7);
                if ((st & 0x1080) != 0) return ("printed", "Windows reports the job as printed.");                     // PRINTED | COMPLETE
                if ((st & 0x2) != 0) return ("attention", "Windows reports an error for this job.");
                if ((st & 0x40) != 0) return ("attention", "Out of labels");
                if ((st & 0x20) != 0) return ("attention", "The printer is offline.");
                if ((st & 0x600) != 0) return ("attention", "The printer needs attention.");                           // BLOCKED_DEVQ | USER_INTERVENTION
                if ((st & 0x1) != 0) return ("attention", "The job is paused in the Windows queue.");
                return ("printing", "Waiting in the Windows print queue.");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
    }

    /// <returns>The Windows job number.</returns>
    public static int WriteRaw(string queue, byte[] data, string docName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows spooler printing needs Windows.");
        if (!OpenPrinter(queue, out var h, IntPtr.Zero)) throw new IOException($"Cannot open printer '{queue}' (error {Marshal.GetLastWin32Error()}).");
        try
        {
            bool done = false;
            var di = new DOC_INFO_1 { pDocName = docName, pOutputFile = null, pDataType = "RAW" };
            int job = StartDocPrinter(h, 1, ref di);
            if (job == 0) throw new IOException($"StartDocPrinter failed (error {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!StartPagePrinter(h)) throw new IOException($"StartPagePrinter failed (error {Marshal.GetLastWin32Error()}).");
                try
                {
                    int off = 0;
                    while (off < data.Length)
                    {
                        var chunk = data.Length - off > 65536 ? data.AsSpan(off, 65536).ToArray() : data.AsSpan(off).ToArray();
                        if (!WritePrinter(h, chunk, chunk.Length, out int w) || w <= 0) throw new IOException($"WritePrinter failed (error {Marshal.GetLastWin32Error()}).");
                        off += w;
                    }
                    done = true;
                }
                finally { EndPagePrinter(h); }
            }
            finally { if (done) EndDocPrinter(h); else AbortPrinter(h); } // never release half a label to the printer
            return job;
        }
        finally { ClosePrinter(h); }
    }
}
