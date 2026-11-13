using System.Text;
using System.Text.RegularExpressions;

namespace LtzPrintBridge.Core;

public static class Languages
{
    public static string Display(PrintLanguage l) => l switch
    {
        PrintLanguage.Zpl => "ZPL II", PrintLanguage.Tspl => "TSPL / TSPL2", PrintLanguage.Epl => "EPL2",
        PrintLanguage.Bple => "BPLE (EPL-style)", PrintLanguage.Cpcl => "CPCL", PrintLanguage.Dpl => "DPL",
        PrintLanguage.EscPos => "ESC/POS", PrintLanguage.Pdf => "Driver only (no label commands)",
        PrintLanguage.Ezpl => "EZPL (Godex)", PrintLanguage.Ppla => "PPLA (DPL-style)", PrintLanguage.Pplb => "PPLB (EPL-style)",
        PrintLanguage.Fingerprint => "Fingerprint / Direct Protocol", PrintLanguage.Ipl => "IPL", PrintLanguage.Sbpl => "SBPL (SATO)",
        PrintLanguage.Tpcl => "TPCL (Toshiba)", _ => "Not set"
    };

    public static readonly PrintLanguage[] Choices =
    {
        PrintLanguage.Zpl, PrintLanguage.Tspl, PrintLanguage.Epl, PrintLanguage.Bple, PrintLanguage.Cpcl, PrintLanguage.Dpl, PrintLanguage.EscPos,
        PrintLanguage.Ezpl, PrintLanguage.Ppla, PrintLanguage.Pplb, PrintLanguage.Fingerprint, PrintLanguage.Ipl, PrintLanguage.Sbpl, PrintLanguage.Tpcl
    };

    static bool Has(string s, params string[] any) => any.Any(s.Contains);
    static bool Rx(string s, string pattern) => Regex.IsMatch(s, pattern);

    /// <summary>
    /// Best-effort guess from printer or driver name. The user can always override it. The rules are checked against
    /// about 500 real printer and driver names in the test suite (tests/data/printer_catalog.json).
    /// </summary>
    public static (string vendor, PrintLanguage lang) Guess(string name, string driver)
    {
        var s = (name + " " + driver).ToLowerInvariant();
        var vendor = Vendor(s);

        // 1. Drivers that state the emulation in their name win over the brand.
        if (Has(s, "bplz", "pplz", "gzpl", "szpl", "bpl-z", "zsim")) return (vendor, PrintLanguage.Zpl);
        if (Has(s, "bple")) return (vendor, PrintLanguage.Bple);
        if (Has(s, "bplb")) return (vendor, PrintLanguage.Tspl);
        if (Has(s, "pplb")) return (vendor, PrintLanguage.Pplb);
        if (Has(s, "ppla")) return (vendor, PrintLanguage.Ppla);
        if (Has(s, "gepl", "sepl", "esim") || Rx(s, @"\bepl2?\b")) return (vendor, PrintLanguage.Epl);
        if (Has(s, "sdpl") || Rx(s, @"\bdpl\b")) return (vendor, PrintLanguage.Dpl);
        if (Rx(s, @"\bcpcl\b")) return (vendor, PrintLanguage.Cpcl);
        if (Rx(s, @"\b(tspl2?|fbpl)\b")) return (vendor, PrintLanguage.Tspl);
        if (Rx(s, @"\bipl\b")) return (vendor, PrintLanguage.Ipl);
        if (Rx(s, @"-\s*dp\b") || Has(s, "fingerprint", "direct protocol")) return (vendor, PrintLanguage.Fingerprint);
        if (Rx(s, @"\bezpl\b")) return (vendor, PrintLanguage.Ezpl);
        if (Rx(s, @"\b(esc/?pos|pos-?\d\d)\b")) return (vendor, PrintLanguage.EscPos);
        if (Rx(s, @"\bzpl\b")) return (vendor, PrintLanguage.Zpl);

        // 2. Otherwise the brand and model decide.
        switch (vendor)
        {
            case "TVS-E": // LP 46 Lite / Plus are TSPL machines; Dlite, Neo and LP 45 are SNBC-built and speak BPLE
                if (Rx(s, @"lp\s*4[56]\s*(lite|plus)")) return (vendor, PrintLanguage.Tspl);
                if (Rx(s, @"lp\s*4[56]")) return (vendor, PrintLanguage.Bple);
                return (vendor, PrintLanguage.Auto);
            case "SNBC": return (vendor, PrintLanguage.Bple);
            case "Zebra":
                if (Rx(s, @"2844-?z|2844\s*z\b")) return (vendor, PrintLanguage.Zpl);
                if (Rx(s, @"\b(t?lp|tlp)\s*-?\s*(2844|2824|2742|3842|2722)")) return (vendor, PrintLanguage.Epl);
                if (Rx(s, @"zq3[12]0(?!\s*plus)|\b(mz|qln?|rw|imz)\s*\d{3}|comtec")) return (vendor, PrintLanguage.Cpcl);
                return (vendor, PrintLanguage.Zpl);
            case "TSC": return (vendor, PrintLanguage.Tspl);
            case "Honeywell": // PC, PM, PX, PD models come from the Intermec line (Fingerprint); the rest from Datamax (DPL)
                return (vendor, Rx(s, @"\bp[cmxd]\d") && !s.Contains("datamax") ? PrintLanguage.Fingerprint : PrintLanguage.Dpl);
            case "Datamax": return (vendor, PrintLanguage.Dpl);
            case "Intermec": return (vendor, Rx(s, @"\bpf8") ? PrintLanguage.Epl : PrintLanguage.Fingerprint);
            case "Godex": return (vendor, PrintLanguage.Ezpl);
            case "Argox": return (vendor, PrintLanguage.Ppla);
            case "Citizen": // "Z" models are the Zebra-emulation variant, "E" the Eltron one, plain models Datamax emulation
                if (Rx(s, @"cl-[a-z]\d{3,4}z\b|cl-[a-z]\d{3,4}ii+z\b")) return (vendor, PrintLanguage.Zpl);
                if (Rx(s, @"cl-[a-z]\d{3,4}e\b")) return (vendor, PrintLanguage.Epl);
                if (Rx(s, @"\bcl[ps]?-")) return (vendor, PrintLanguage.Dpl);
                return (vendor, PrintLanguage.EscPos);
            case "SATO": return (vendor, PrintLanguage.Sbpl);
            case "Toshiba": return (vendor, PrintLanguage.Tpcl);
            case "Brother":
                if (Rx(s, @"td-?\s*4\d{3}tn")) return (vendor, PrintLanguage.Tspl);
                return (vendor, PrintLanguage.Pdf);
            case "DYMO": return (vendor, PrintLanguage.Pdf);
            case "Epson":
                if (Rx(s, @"cw-c|colorworks|tm-c\d")) return (vendor, Rx(s, @"tm-c3[45]00") ? PrintLanguage.Pdf : PrintLanguage.Zpl); // ColorWorks: ESC/Label is ZPL-compatible
                return (vendor, PrintLanguage.EscPos);
            case "Star":
                if (Rx(s, @"tsp1\d\d") && !s.Contains("iv")) return (vendor, PrintLanguage.Pdf); // TSP100 family (before the IV) is raster only
                return (vendor, PrintLanguage.Auto);
            case "Bixolon":
                if (Rx(s, @"\bsrp-|\bspp-")) return (vendor, PrintLanguage.EscPos);
                return (vendor, PrintLanguage.Auto); // SLCS, no entry for it
            case "Rollo":
                if (Rx(s, @"x1040|wireless")) return (vendor, PrintLanguage.Pdf);
                return (vendor, PrintLanguage.Tspl);
            case "Xprinter": case "Gprinter": case "Rongta":
                if (Rx(s, @"\b(80|58)\s*mm|xp-?(58|80|[cnqtv]\d{3})|gp-?(l?80|58|u80|c80)|rp-?(80|58|3\d\d)\b|receipt|pos")) return (vendor, PrintLanguage.EscPos);
                return (vendor, PrintLanguage.Tspl);
            case "HPRT": case "iDPRT": case "Gainscha": case "Deli": case "Munbyn": case "Wincode": case "Jadens": case "Phomemo": case "Beeprt":
                return (vendor, PrintLanguage.Tspl);
        }
        return (vendor, PrintLanguage.Auto);
    }

    static string Vendor(string s)
    {
        if (Has(s, "tvs") || Rx(s, @"\blp\s*4[456]\b")) return "TVS-E";
        if (Has(s, "snbc", "btp-")) return "SNBC";
        if (Has(s, "zebra", "zdesigner", "comtec") || Rx(s, @"\b(zd[2-6]\d\d|zt[1-6]\d\d|zq[3-6]\d\d|g[kxct]4[23]\d|[lt]?lp\s*28[24]4)")) return "Zebra";
        if (Has(s, "tsc ") || s.StartsWith("tsc") || Rx(s, @"\b(te2\d\d|te3\d\d|ttp-|tdp-|da2\d\d|tx[236]\d\d|ml[23]40|mb[23]40)")) return "TSC";
        if (Has(s, "honeywell")) return "Honeywell";
        if (Has(s, "datamax")) return "Datamax";
        if (Has(s, "intermec")) return "Intermec";
        if (Has(s, "godex")) return "Godex";
        if (Has(s, "argox")) return "Argox";
        if (Has(s, "citizen")) return "Citizen";
        if (Has(s, "sato")) return "SATO";
        if (Has(s, "toshiba", "tec b-")) return "Toshiba";
        if (Has(s, "brother")) return "Brother";
        if (Has(s, "dymo")) return "DYMO";
        if (Has(s, "epson")) return "Epson";
        if (Has(s, "bixolon")) return "Bixolon";
        if (Rx(s, @"\bstar\b")) return "Star";
        if (Has(s, "rollo")) return "Rollo";
        if (Has(s, "xprinter") || Rx(s, @"\bxp-")) return "Xprinter";
        if (Has(s, "gprinter") || Rx(s, @"\bgp-")) return "Gprinter";
        if (Has(s, "rongta")) return "Rongta";
        if (Has(s, "idprt")) return "iDPRT";
        if (Has(s, "hprt")) return "HPRT";
        if (Has(s, "gainscha") || Rx(s, @"\bga-\d")) return "Gainscha";
        if (Rx(s, @"\bdeli\b")) return "Deli";
        if (Has(s, "munbyn", "itpp")) return "Munbyn";
        if (Has(s, "wincode")) return "Wincode";
        if (Rx(s, @"\bjd-\d|jadens")) return "Jadens";
        if (Has(s, "phomemo") || Rx(s, @"\bpm-\d")) return "Phomemo";
        if (Has(s, "beeprt")) return "Beeprt";
        return "Unknown";
    }

    public static string Sample(PrintLanguage l, string name = "TEST LABEL", string id = "123456789") => l switch
    {
        PrintLanguage.Zpl => $"^XA\n^PW400\n^LL200\n^CI28\n^FO20,20^A0N,36,36^FD{name}^FS\n^FO20,70^BCN,60,Y^FD{id}^FS\n^FO20,170^A0N,22,22^FDPrintBridge test^FS\n^XZ\n",
        PrintLanguage.Tspl => $"SIZE 50 mm,25 mm\r\nGAP 2 mm,0 mm\r\nDIRECTION 1\r\nCLS\r\nTEXT 20,20,\"3\",0,1,1,\"{name}\"\r\nBARCODE 20,60,\"128\",60,1,0,2,2,\"{id}\"\r\nTEXT 20,165,\"2\",0,1,1,\"PrintBridge test\"\r\nPRINT 1,1\r\n",
        PrintLanguage.Epl or PrintLanguage.Bple or PrintLanguage.Pplb => $"\nN\nq400\nQ200,24\nA20,20,0,3,1,1,N,\"{name}\"\nB20,60,0,1,2,5,60,B,\"{id}\"\nA20,165,0,2,1,1,N,\"PrintBridge test\"\nP1\n",
        PrintLanguage.Cpcl => $"! 0 200 200 220 1\r\nTEXT 4 0 20 20 {name}\r\nBARCODE 128 1 1 50 20 70 {id}\r\nTEXT 7 0 20 160 PrintBridge test\r\nPRINT\r\n",
        PrintLanguage.Dpl or PrintLanguage.Ppla => $"\u0002L\r\nD11\r\n121100000200020{name}\r\n1A5100000100030{id}\r\nE\r\n",
        PrintLanguage.EscPos => $"\u001b@{name}\n{id}\nPrintBridge test\n\n\n\u001dV\u0001",
        PrintLanguage.Ezpl => $"^Q25,3\n^W50\n^H10\n^P1\n^L\nAC,20,20,1,1,0,0,{name}\nBQ,20,60,2,5,60,0,1,{id}\nE\n",
        PrintLanguage.Fingerprint => $"BF ON\nPP 20,150\nFT \"Univers\",10\nPT \"{name}\"\nPP 20,60\nBT \"CODE128\"\nBH 60\nPB \"{id}\"\nPF 1\n",
        PrintLanguage.Ipl => $"\u0002\u001bC\u0003\u0002\u001bP\u0003\u0002E4;F4;\u0003\u0002H0;o20,20;f0;c25;h20;w20;d3,{name};\u0003\u0002B1;o20,70;c6,0;h60;w2;d3,{id};\u0003\u0002R\u0003\u0002\u001bE4\u0003\u0002\u0017\u0003",
        PrintLanguage.Sbpl => $"\u001bA\u001bV0020\u001bH0020\u001bL0202\u001bXM{name}\u001bV0070\u001bH0020\u001bBG02060{id}\u001bQ1\u001bZ",
        PrintLanguage.Tpcl => $"{{D0270,0500,0250|}}\n{{C|}}\n{{PC000;0020,0050,10,10,A,00,B={name}|}}\n{{XB00;0020,0080,9,3,02,0,0060,+0000000000,000,1,00={id}|}}\n{{XS;I,0001,0002C4000|}}\n",
        _ => $"PrintBridge test\n{name}\n{id}\n"
    };

    static Languages() { try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { } }

    /// <summary>Encodings a website or printer setting can ask for. UTF-8 is the default.</summary>
    public static Encoding? FindEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
        var n = name.Trim().ToLowerInvariant().Replace("_", "-");
        if (n is "utf8" or "utf-8") return new UTF8Encoding(false);
        if (n is "latin1" or "latin-1") n = "iso-8859-1";
        if (n.StartsWith("cp") && int.TryParse(n[2..], out var cp)) { try { return Encoding.GetEncoding(cp); } catch { return null; } }
        if (int.TryParse(n, out var cp2)) { try { return Encoding.GetEncoding(cp2); } catch { return null; } }
        try { return Encoding.GetEncoding(n); } catch { return null; }
    }

    public static byte[] Encode(string data, PrintLanguage l, Encoding? enc = null) => (enc ?? new UTF8Encoding(false)).GetBytes(data);
}
