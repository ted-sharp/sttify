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

public class TextOutputFailedException : Exception
{
    public TextOutputFailedException(string message) : base(message) { }
    public TextOutputFailedException(string message, Exception innerException) : base(message, innerException) { }
}