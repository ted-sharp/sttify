namespace Sttify.Corelib.Wake;

public class WakeWordDetector
{
    private const string DefaultWakeWord = "スティファイ";
    private readonly int _maxHistorySize = 5;
    private readonly Queue<string> _recentRecognitions = new();
    private readonly string _wakeWord;

    public WakeWordDetector(string? wakeWord = null)
    {
        _wakeWord = wakeWord ?? DefaultWakeWord;
    }

    public event EventHandler<WakeWordDetectedEventArgs>? OnWakeWordDetected;

    public void ProcessRecognition(string recognizedText, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(recognizedText))
            return;

        if (isFinal)
        {
            _recentRecognitions.Enqueue(recognizedText);

            if (_recentRecognitions.Count > _maxHistorySize)
            {
                _recentRecognitions.Dequeue();
            }
        }

        if (ContainsWakeWord(recognizedText))
        {
            OnWakeWordDetected?.Invoke(this, new WakeWordDetectedEventArgs(_wakeWord, recognizedText));
        }
    }

    private bool ContainsWakeWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains(_wakeWord, StringComparison.OrdinalIgnoreCase) ||
               IsPhoneticMatch(text, _wakeWord);
    }

    private bool IsPhoneticMatch(string text, string _)
    {
        var phoneticVariations = new[]
        {
            "すてぃふぁい",
            "ステファイ",
            "すてふぁい"
        };

        foreach (var variation in phoneticVariations)
        {
            if (text.Contains(variation, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Reset()
    {
        _recentRecognitions.Clear();
    }
}

public class WakeWordDetectedEventArgs : EventArgs
{
    public WakeWordDetectedEventArgs(string wakeWord, string recognizedText)
    {
        WakeWord = wakeWord;
        RecognizedText = recognizedText;
        Timestamp = DateTime.UtcNow;
    }

    public string WakeWord { get; }
    public string RecognizedText { get; }
    public DateTime Timestamp { get; }
}
