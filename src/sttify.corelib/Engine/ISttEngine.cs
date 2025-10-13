using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Engine;

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
    public PartialRecognitionEventArgs(string text, double confidence)
    {
        Text = text;
        Confidence = confidence;
    }

    public string Text { get; }
    public double Confidence { get; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class FinalRecognitionEventArgs : EventArgs
{
    public FinalRecognitionEventArgs(string text, double confidence, TimeSpan duration)
    {
        Text = text;
        Confidence = confidence;
        Duration = duration;
    }

    public string Text { get; }
    public double Confidence { get; }
    public TimeSpan Duration { get; }
}

[ExcludeFromCodeCoverage] // Simple DTO with no business logic
public class SttErrorEventArgs : EventArgs
{
    public SttErrorEventArgs(Exception exception, string message)
    {
        Exception = exception;
        Message = message;
    }

    public Exception Exception { get; }
    public string Message { get; }
}
