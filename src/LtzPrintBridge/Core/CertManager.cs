using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LtzPrintBridge.Core;

/// <summary>
/// Creates a per-install local CA and a loopback server certificate. The CA private key is discarded right after
/// signing, so the trusted root can never be used to issue other certificates. Only the public CA certificate is
/// added to the current user's trust store.
/// </summary>
public sealed class CertManager
{
    readonly string _dir;
    /// <param name="dir">Folder for the certificate files. The normal data folder when omitted.</param>
    public CertManager(string? dir = null) { _dir = dir ?? AppPaths.Certs; Directory.CreateDirectory(_dir); }
    string PfxPath => Path.Combine(_dir, "server.pfx");
    public string CaPath => Path.Combine(_dir, "ltz-printbridge-ca.cer");
    const string Pw = "ltz-local";
    public X509Certificate2? ServerCert { get; private set; }
    public string? Error { get; private set; }
    /// <summary>True when this start created a new certificate, so the computer has to be asked to trust it again.</summary>
    public bool Regenerated { get; private set; }

    public void Ensure()
    {
        try
        {
            if (File.Exists(PfxPath) && File.Exists(CaPath))
            {
                try
                {
                    var c = new X509Certificate2(PfxPath, Pw, X509KeyStorageFlags.Exportable);
                    if (c.NotAfter > DateTime.UtcNow.AddDays(30)) { ServerCert = c; return; }
                    c.Dispose();
                }
                catch (Exception e) { Log.Warn("Stored certificate unreadable, creating a new one: " + e.Message); }
            }
            Generate();
        }
        catch (Exception e) { Error = e.Message; Log.Error("Certificate setup failed", e); }
    }

    void Generate()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest($"CN=PrintBridge Local CA ({Environment.MachineName})", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        using var ca = caReq.CreateSelfSigned(now, now.AddDays(800));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7F;
        using var signed = req.Create(ca, now, now.AddDays(397), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        File.WriteAllBytes(PfxPath, withKey.Export(X509ContentType.Pfx, Pw));
        File.WriteAllBytes(CaPath, ca.Export(X509ContentType.Cert));
        ServerCert = new X509Certificate2(PfxPath, Pw, X509KeyStorageFlags.Exportable);
        Regenerated = true;
        Log.Info("Generated new local certificate");
    }

    public bool IsTrusted()
    {
        try
        {
            if (!File.Exists(CaPath)) return false;
            if (OperatingSystem.IsMacOS()) return MacTrust.IsTrusted(CaPath);
            using var ca = new X509Certificate2(CaPath);
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false).Count > 0;
        }
        catch { return false; }
    }

    /// <summary>Adds the CA to the current user's trusted roots. Windows shows its own confirmation dialog.</summary>
    public (bool ok, string message) InstallTrust()
    {
        if (OperatingSystem.IsMacOS()) return MacTrust.Install(CaPath);
        if (!OperatingSystem.IsWindows()) return (false, "Certificate install is supported on Windows and macOS only.");
        try
        {
            using var ca = new X509Certificate2(CaPath);
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(ca);
            return (true, "Certificate installed.");
        }
        catch (Exception e) { return (false, "Not installed: " + e.Message); }
    }
}
