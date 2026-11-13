using System.Diagnostics;

namespace LtzPrintBridge.Core;

/// <summary>Trusts the per-install local CA in the user's login keychain. macOS asks for the user's password once.</summary>
public static class MacTrust
{
    static (int code, string output) Security(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch { } return (-1, "Timed out waiting for the macOS password prompt."); }
        return (p.ExitCode, o.Result + e.Result);
    }

    public static bool IsTrusted(string caPath)
    {
        try { return Security("verify-cert", "-c", caPath, "-p", "ssl", "-q").code == 0; }
        catch { return false; }
    }

    public static (bool ok, string message) Install(string caPath)
    {
        try
        {
            var keychain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");
            var (code, o) = Security("add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", keychain, caPath);
            return code == 0 ? (true, "Certificate installed.") : (false, "Not installed: " + o.Trim());
        }
        catch (Exception e) { return (false, "Not installed: " + e.Message); }
    }
}
