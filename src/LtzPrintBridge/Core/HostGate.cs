namespace LtzPrintBridge.Core;

public enum HostDecision { Block, AllowOnce, AllowAlways, NotNow }

/// <summary>Decides which websites may use the bridge. Unknown sites trigger a prompt; the request waits for the answer.</summary>
public sealed class HostGate
{
    readonly SettingsStore _store;
    readonly HashSet<string> _session = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();
    public Func<string, Task<HostDecision>>? Prompt { get; set; }
    /// <summary>Raised when nobody answered a prompt in time, so the window can be closed.</summary>
    public event Action<string>? PromptExpired;
    /// <summary>Most permission windows open at once. Further unknown websites are refused until one is answered.</summary>
    public const int MaxPrompts = 3;
    public static TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public event Action? Changed;
    public HostGate(SettingsStore store) { _store = store; }

    public static bool Match(IEnumerable<string> list, string host) => list.Any(e =>
        string.Equals(e, host, StringComparison.OrdinalIgnoreCase) ||
        (e.StartsWith("*.") && host.EndsWith(e[1..], StringComparison.OrdinalIgnoreCase)));

    public Task<bool> IsAllowedAsync(string host)
    {
        host = host.Trim().ToLowerInvariant();
        var s = _store.Value;
        lock (_lock)
        {
            if (Match(s.BlockedHosts, host)) return Task.FromResult(false);
            if (Match(s.AcceptedHosts, host) || _session.Contains(host)) return Task.FromResult(true);
            if (_pending.TryGetValue(host, out var t)) return t;
            if (_pending.Count >= MaxPrompts) return Task.FromResult(false);
            var ask = Ask(host);
            if (!ask.IsCompleted) _pending[host] = ask; // an answer given at once must not be remembered as "still waiting"
            return ask;
        }
    }

    async Task<bool> Ask(string host)
    {
        try
        {
            if (Prompt == null) return false;
            var task = Prompt(host);
            if (await Task.WhenAny(task, Task.Delay(PromptTimeout)) != task) { PromptExpired?.Invoke(host); return false; }
            switch (await task)
            {
                case HostDecision.AllowAlways: Accept(host); return true;
                case HostDecision.AllowOnce: lock (_lock) _session.Add(host); return true;
                case HostDecision.Block: Block(host); return false;
                default: return false; // window closed without a choice: ask again next time
            }
        }
        finally { lock (_lock) _pending.Remove(host); }
    }

    public void Accept(string host)
    {
        host = host.Trim().ToLowerInvariant(); if (host.Length == 0) return;
        lock (_lock)
        {
            var s = _store.Value; s.BlockedHosts.RemoveAll(h => h == host);
            if (!s.AcceptedHosts.Contains(host)) s.AcceptedHosts.Add(host);
        }
        _store.Save(); Changed?.Invoke();
    }
    public void Block(string host)
    {
        host = host.Trim().ToLowerInvariant();
        lock (_lock)
        {
            var s = _store.Value;
            s.AcceptedHosts.RemoveAll(h => h == host);
            if (!s.BlockedHosts.Contains(host)) s.BlockedHosts.Add(host);
        }
        _store.Save(); Changed?.Invoke();
    }
    /// <summary>Forgets every decision: allowed, blocked and "allow once" sites. localhost and policy.json sites stay.</summary>
    public void ResetAll()
    {
        lock (_lock)
        {
            _session.Clear();
            var s = _store.Value;
            s.AcceptedHosts = new() { "localhost", "127.0.0.1" }; s.BlockedHosts = new();
            Policy.Apply(_store);
        }
        _store.Save(); Changed?.Invoke();
    }

    public void Remove(string host)
    {
        lock (_lock) { var s = _store.Value; s.AcceptedHosts.RemoveAll(h => h == host); s.BlockedHosts.RemoveAll(h => h == host); }
        _store.Save(); Changed?.Invoke();
    }
}
