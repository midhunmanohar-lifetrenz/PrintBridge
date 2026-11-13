using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtzPrintBridge.Core;

public static class AppPaths
{
    public static string Dir { get; } = Environment.GetEnvironmentVariable("LTZ_DATA_DIR") is { Length: > 0 } d ? d
        : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "PrintBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LtzPrintBridge");
    public static string Settings => Path.Combine(Dir, "settings.json");
    public static string Jobs => Path.Combine(Dir, "jobs.jsonl");
    public static string Certs => Path.Combine(Dir, "certs");
    public static string Log => Path.Combine(Dir, "bridge.log");
    static AppPaths() { Directory.CreateDirectory(Dir); Directory.CreateDirectory(Certs); }
}

public static class Log
{
    static readonly object L = new();
    public static event Action<string>? Line;
    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m, Exception? e = null) => Write("ERROR", e == null ? m : $"{m}: {e.Message}");
    static void Write(string lvl, string m)
    {
        var s = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lvl} {m}";
        try
        {
            lock (L)
            {
                var fi = new FileInfo(AppPaths.Log);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(AppPaths.Log, AppPaths.Log + ".1", true);
                File.AppendAllText(AppPaths.Log, s + Environment.NewLine);
            }
        }
        catch { }
        Line?.Invoke(s);
    }
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    readonly object _lock = new();
    readonly string _path;
    /// <param name="path">Where settings.json lives. The normal data folder when omitted.</param>
    public SettingsStore(string? path = null) { _path = path ?? AppPaths.Settings; }
    public AppSettings Value { get; private set; } = new();

    public void Load()
    {
        // settings.json first, then the copy kept from the last good save. A damaged file is set aside, never lost.
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opt) ?? throw new InvalidDataException("empty file");
                if (path != _path) Log.Warn("Settings restored from the backup copy");
                return;
            }
            catch (Exception e)
            {
                Log.Error($"{Path.GetFileName(path)} unreadable", e);
                try { File.Copy(path, path + ".damaged", true); } catch { }
            }
        }
        Value = new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                // Write beside the real file and swap, so a crash or power cut can never leave half a file.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Opt));
                if (File.Exists(_path)) File.Copy(_path, _path + ".bak", true);
                File.Move(tmp, _path, true);
            }
            catch (Exception e) { Log.Error("Could not save settings", e); }
        }
    }
}
