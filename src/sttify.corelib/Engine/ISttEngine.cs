using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Engine;

[ExcludeFromCodeCoverage] // Interface definition only
public interface ISttEngine : IDisposable
{
    event EventHandler<PartialRecognitionEventArgs>? OnPartial;
    event EventHandler<FinalRecognitionEventArgs>? OnFinal;
    event EventHandler<SttErrorEventArgs>? OnError;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    void PushAudio(ReadOnlySpan<byte> audioData);
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class PartialRecognitionEventArgs : EventArgs
{
    public string Text { get; }
    public double Confidence { get; }

    public PartialRecognitionEventArgs(string text, double confidence)
    {
        Text = text;
        Confidence = confidence;
    }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class FinalRecognitionEventArgs : EventArgs
{
    public string Text { get; }
    public double Confidence { get; }
    public TimeSpan Duration { get; }

    public FinalRecognitionEventArgs(string text, double confidence, TimeSpan duration)
    {
        Text = text;
        Confidence = confidence;
        Duration = duration;
    }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class SttErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public string Message { get; }

    public SttErrorEventArgs(Exception exception, string message)
    {
        Exception = exception;
        Message = message;
    }
}