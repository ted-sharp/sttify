using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Output;

public class TsfTipSink : ITextOutputSink
{
    private const string PipeName = "sttify_tip_ipc";
    private readonly TsfTipSettings _settings;
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;
    private bool _lastAvailabilityResult;
    
    public string Name => "TSF TIP";
    public bool IsAvailable => CheckTipAvailability();

    public TsfTipSink(TsfTipSettings? settings = null)
    {
        _settings = settings ?? new TsfTipSettings();
    }

    public async Task<bool> CanSendAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(IsAvailable);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var startTime = DateTime.UtcNow;
        
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            
            var connectTimeout = _settings.ConnectionTimeoutMs > 0 ? _settings.ConnectionTimeoutMs : 5000;
            await client.ConnectAsync(connectTimeout, cancellationToken);

            var ipcCommand = new TipIpcCommand
            {
                Command = "SendText",
                Data = text,
                Mode = _settings.InsertionMode.ToString(),
                SuppressWhenImeComposing = _settings.SuppressWhenImeComposing,
                Timestamp = startTime
            };

            var message = JsonSerializer.Serialize(ipcCommand);
            var buffer = Encoding.UTF8.GetBytes(message);
            
            // Write message length first (4 bytes)
            var lengthBytes = BitConverter.GetBytes(buffer.Length);
            await client.WriteAsync(lengthBytes, cancellationToken);
            
            // Write message content
            await client.WriteAsync(buffer, cancellationToken);
            await client.FlushAsync(cancellationToken);
            
            var duration = DateTime.UtcNow - startTime;
            
            Telemetry.LogEvent("TsfTipSendCompleted", new 
            { 
                TextLength = text.Length,
                DurationMs = duration.TotalMilliseconds,
                InsertionMode = _settings.InsertionMode.ToString()
            });
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            Telemetry.LogError("TsfTipSendFailed", ex, new 
            { 
                TextLength = text.Length,
                DurationMs = duration.TotalMilliseconds,
                PipeName = PipeName
            });
            
            throw new TextOutputFailedException($"Failed to send text via TSF TIP: {ex.Message}", ex);
        }
    }

    private bool CheckTipAvailability()
    {
        // Cache availability check for a short time to avoid frequent pipe connection attempts
        var now = DateTime.UtcNow;
        if (now - _lastAvailabilityCheck < TimeSpan.FromSeconds(5))
        {
            return _lastAvailabilityResult;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500); // Increased timeout slightly
            
            // Send a ping command to verify TIP is responsive
            var pingCommand = new TipIpcCommand
            {
                Command = "Ping",
                Data = "",
                Timestamp = now
            };
            
            var message = JsonSerializer.Serialize(pingCommand);
            var buffer = Encoding.UTF8.GetBytes(message);
            var lengthBytes = BitConverter.GetBytes(buffer.Length);
            
            client.Write(lengthBytes);
            client.Write(buffer);
            client.Flush();
            
            _lastAvailabilityResult = true;
            Telemetry.LogEvent("TsfTipAvailabilityChecked", new { Available = true });
        }
        catch (Exception ex)
        {
            _lastAvailabilityResult = false;
            Telemetry.LogEvent("TsfTipAvailabilityChecked", new { Available = false, Error = ex.Message });
        }
        
        _lastAvailabilityCheck = now;
        return _lastAvailabilityResult;
    }

    // Additional helper methods for TSF TIP communication
    public async Task<bool> RequestCompositionAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(_settings.ConnectionTimeoutMs, cancellationToken);

            var ipcCommand = new TipIpcCommand
            {
                Command = "StartComposition",
                Data = text,
                Mode = _settings.InsertionMode.ToString(),
                Timestamp = DateTime.UtcNow
            };

            var message = JsonSerializer.Serialize(ipcCommand);
            var buffer = Encoding.UTF8.GetBytes(message);
            var lengthBytes = BitConverter.GetBytes(buffer.Length);
            
            await client.WriteAsync(lengthBytes, cancellationToken);
            await client.WriteAsync(buffer, cancellationToken);
            await client.FlushAsync(cancellationToken);
            
            return true;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("TsfTipCompositionFailed", ex, new { TextLength = text.Length });
            return false;
        }
    }

    public async Task<bool> FinalizeCompositionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(_settings.ConnectionTimeoutMs, cancellationToken);

            var ipcCommand = new TipIpcCommand
            {
                Command = "FinalizeComposition",
                Data = "",
                Timestamp = DateTime.UtcNow
            };

            var message = JsonSerializer.Serialize(ipcCommand);
            var buffer = Encoding.UTF8.GetBytes(message);
            var lengthBytes = BitConverter.GetBytes(buffer.Length);
            
            await client.WriteAsync(lengthBytes, cancellationToken);
            await client.WriteAsync(buffer, cancellationToken);
            await client.FlushAsync(cancellationToken);
            
            return true;
        }
        catch (Exception ex)
        {
            Telemetry.LogError("TsfTipFinalizationFailed", ex);
            return false;
        }
    }
}

public class TipIpcCommand
{
    public string Command { get; set; } = "";
    public string Data { get; set; } = "";
    public string Mode { get; set; } = "FinalOnly";
    public bool SuppressWhenImeComposing { get; set; } = true;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class TsfTipSettings
{
    public TextInsertionMode InsertionMode { get; set; } = TextInsertionMode.FinalOnly;
    public bool SuppressWhenImeComposing { get; set; } = true;
    public int ConnectionTimeoutMs { get; set; } = 5000;
    public bool EnableComposition { get; set; } = false; // Whether to show composition preview
    public string CompositionFontName { get; set; } = ""; // Empty = use system default
    public int CompositionFontSize { get; set; } = 0; // 0 = use system default
}