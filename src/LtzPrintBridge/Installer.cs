using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LtzPrintBridge;

/// <summary>
/// Windows install and uninstall, built into the app so one file does everything. Per user: no administrator rights.
///   PrintBridge.exe                         asks to install when started from anywhere but the install folder
///   PrintBridge.exe --install [--silent]    install or update without asking
///   PrintBridge.exe --uninstall [--silent]  remove (silent keeps settings and the trusted certificate)
///   PrintBridge.exe --portable              run from the current folder without installing
/// </summary>
internal static class Installer
{
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PrintBridge";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const uint MB_YESNO = 4, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40, MB_ICONERROR = 0x10, MB_TOPMOST = 0x40000;
    const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string InstallDir => Environment.GetEnvironmentVariable("LTZ_INSTALL_DIR") is { Length: > 0 } d ? d : Path.Combine(Local, "Programs", "PrintBridge");
    public static string InstalledExe => Path.Combine(InstallDir, "PrintBridge.exe");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PrintBridge.lnk");
    static string Self => Environment.ProcessPath ?? "";
    static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    static bool Ask(string text) => MessageBoxW(IntPtr.Zero, text, "PrintBridge", MB_YESNO | MB_ICONQUESTION | MB_TOPMOST) == IDYES;
    static void Tell(string text, bool error = false) => MessageBoxW(IntPtr.Zero, text, "PrintBridge", (error ? MB_ICONERROR : MB_ICONINFORMATION) | MB_TOPMOST);

    static bool InInstallDir() =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(Self))?.TrimEnd('\\'), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool IsInstalled() => File.Exists(InstalledExe) && Registry.CurrentUser.OpenSubKey(UninstKey) != null;

    /// <summary>Returns an exit code when the run was an install or uninstall, or null to start the app normally.</summary>
    public static int? Handle(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return null;
        bool silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Has("--uninstall")) { Uninstall(silent); return 0; }
            if (Has("--install")) { Install(silent); return 0; }
            if (Has("--portable") || Has("--minimized") || InInstallDir()) return null;
            if (Environment.GetEnvironmentVariable("LTZ_DATA_DIR") is { Length: > 0 }) return null; // development and tests
            var update = IsInstalled();
            var q = update
                ? "PrintBridge is already installed on this computer.\n\nUpdate it with this version?\n\nYes = Update and open    No = Run this copy without installing"
                : "Install PrintBridge on this computer?\n\nIt adds a Start menu shortcut and can be removed from Settings > Apps.\nNo administrator rights are needed.\n\nYes = Install    No = Run without installing";
            if (!Ask(q)) return null;
            Install(false);
            return 0;
        }
        catch (Exception e)
        {
            Tell("PrintBridge could not finish: " + e.Message, true);
            return 1;
        }
    }

    static void KillOthers()
    {
        foreach (var p in Process.GetProcessesByName("PrintBridge"))
        {
            try { if (p.Id != Environment.ProcessId) { p.Kill(true); p.WaitForExit(4000); } } catch { }
            p.Dispose();
        }
    }

    static void Install(bool silent)
    {
        KillOthers();
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(Self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(Self, InstalledExe, true); break; }
                catch (IOException) when (i < 8) { Thread.Sleep(500); } // old copy still closing
            }
        }
        MakeShortcut();
        using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", "PrintBridge");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Lifetrenz");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --silent");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }
        // Keep "Start when I sign in" pointing at the installed copy if it was already on.
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (run?.GetValue("PrintBridge") != null) run.SetValue("PrintBridge", $"\"{InstalledExe}\" --minimized");
        if (!silent) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
    }

    static void MakeShortcut()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcuts are not available.");
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic link = shell.CreateShortcut(StartMenuLink);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.Description = "Label printing for web applications";
        link.IconLocation = InstalledExe + ",0";
        link.Save();
    }

    static void Uninstall(bool silent)
    {
        bool wipe = false;
        if (!silent)
        {
            if (!Ask("Remove PrintBridge from this computer?\n\nYes = Remove    No = Cancel")) return;
            wipe = Ask("Also delete your PrintBridge settings, print history and the trusted certificate?\n\nChoose No if you plan to install PrintBridge again.");
        }
        KillOthers();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue("PrintBridge", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false);
        try { File.Delete(StartMenuLink); } catch { }
        if (wipe)
        {
            try { Directory.Delete(Path.Combine(Local, "LtzPrintBridge"), true); } catch { }
            try { Process.Start(new ProcessStartInfo("certutil.exe", $"-user -delstore Root \"PrintBridge Local CA ({Environment.MachineName})\"") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(15000); } catch { }
        }
        // A running program cannot delete itself, so a short-lived helper removes the file once this process has exited.
        var cmd = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{InstalledExe}\" & rmdir \"{InstallDir}\"";
        if (!silent) Tell("PrintBridge was removed.");
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false }); // last step: the helper waits ~2 s for this process to end
    }

}
