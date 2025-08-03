using Sttify.Corelib.Config;
using Sttify.Corelib.Rtss;
using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Output;

[ExcludeFromCodeCoverage] // External RTSS application integration, difficult to mock effectively
public class RtssSink : ITextOutputSink, IDisposable
{
    public string Name => "RTSS Overlay";
    public bool IsAvailable => _bridge.Initialize();

    private readonly RtssBridge _bridge;
    private readonly RtssSettings _settings;
    private bool _disposed;

    public RtssSink(RtssSettings? settings = null)
    {
        _settings = settings ?? new RtssSettings();
        _bridge = new RtssBridge(_settings);
    }

    public async Task<bool> CanSendAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_bridge.Initialize());
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RtssSink));

        if (string.IsNullOrEmpty(text))
        {
            _bridge.ClearOsd();
            return;
        }

        await Task.Run(() => _bridge.UpdateOsd(text), cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _bridge?.Dispose();
            _disposed = true;
        }
    }
}