using FlashCap;

namespace LtzPrintBridge.Core.Scanning;

/// <summary>Opens a camera and hands each frame to <see cref="Frame"/> as an encoded image. Uses Media Foundation / DirectShow on Windows and AVFoundation on macOS.</summary>
public sealed class CameraSource : IAsyncDisposable
{
    CaptureDevice? _device;

    /// <summary>Raised on a camera thread for every frame.</summary>
    public event Action<byte[]>? Frame;

    public string? Name { get; private set; }

    public static IReadOnlyList<CaptureDeviceDescriptor> List()
    {
        try
        {
            return new CaptureDevices().EnumerateDescriptors()
                .Where(d => d.Characteristics.Any(c => c.PixelFormat != PixelFormats.Unknown))
                .ToList();
        }
        catch (Exception e)
        {
            Log.Error("Camera list failed", e);
            return Array.Empty<CaptureDeviceDescriptor>();
        }
    }

    public async Task StartAsync(CaptureDeviceDescriptor descriptor, CancellationToken ct = default)
    {
        await StopAsync();
        var ch = Pick(descriptor.Characteristics);
        Name = descriptor.Name;
        Log.Info($"Camera: {descriptor.Name} {ch}");
        _device = await descriptor.OpenAsync(ch, TranscodeFormats.Auto, (PixelBufferArrivedDelegate)(scope =>
        {
            var image = scope.Buffer.CopyImage();
            Frame?.Invoke(image);
        }), ct);
        await _device.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        var d = _device;
        _device = null;
        if (d == null) return;
        try { await d.StopAsync(); } catch (Exception e) { Log.Warn("Camera stop: " + e.Message); }
        try { await d.DisposeAsync(); } catch { }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Close to 1280×720 is sharp enough for small medicine codes without slowing the decoder down.</summary>
    static VideoCharacteristics Pick(VideoCharacteristics[] all)
    {
        var usable = all.Where(c => c.PixelFormat != PixelFormats.Unknown).ToList();
        return usable
            .OrderBy(c => Math.Abs(c.Width * c.Height - 1280 * 720))
            .ThenBy(c => c.PixelFormat == PixelFormats.JPEG ? 1 : 0)
            .FirstOrDefault() ?? all[0];
    }
}
