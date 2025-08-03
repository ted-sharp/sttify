using System.Diagnostics.CodeAnalysis;

namespace Sttify.Corelib.Output;

public interface ITextOutputSink
{
    string Name { get; }
    bool IsAvailable { get; }
    
    Task<bool> CanSendAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string text, CancellationToken cancellationToken = default);
}

public enum TextInsertionMode
{
    FinalOnly,
    WithComposition
}

[ExcludeFromCodeCoverage] // Simple exception class with no business logic
public class TextOutputFailedException : Exception
{
    public TextOutputFailedException(string message) : base(message) { }
    public TextOutputFailedException(string message, Exception innerException) : base(message, innerException) { }
}