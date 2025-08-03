using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Sttify.Corelib.Output;

[ExcludeFromCodeCoverage] // Win32 SendInput API integration, difficult to mock effectively
public class SendInputSink : ITextOutputSink
{
    private const int INPUT_KEYBOARD = 1;
    private const int KEYEVENTF_UNICODE = 0x0004;

    public string Name => "SendInput";
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly SendInputSettings _settings;

    public SendInputSink(SendInputSettings? settings = null)
    {
        _settings = settings ?? new SendInputSettings();
    }

    public async Task<bool> CanSendAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(IsAvailable);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;

        await SendTextViaInputAsync(text, cancellationToken);
    }

    private async Task SendTextViaInputAsync(string text, CancellationToken cancellationToken)
    {
        var inputs = new List<INPUT>();
        var delayMs = _settings.RateLimitCps > 0 ? 1000 / _settings.RateLimitCps : 0;

        foreach (char c in text)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });

            // Send character immediately for better responsiveness
            if (inputs.Count == 1)
            {
                SendInput(1, inputs.ToArray(), Marshal.SizeOf<INPUT>());
                inputs.Clear();
                
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
        }

        // Send any remaining inputs
        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }

        // Send commit key if specified
        if (_settings.CommitKey != null)
        {
            var commitInputs = new INPUT[]
            {
                CreateKeyInput(_settings.CommitKey.Value, false),
                CreateKeyInput(_settings.CommitKey.Value, true)
            };
            SendInput(2, commitInputs, Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT CreateKeyInput(int virtualKey, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = 0,
                    dwFlags = keyUp ? 0x0002u : 0u,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SendInputSettings
{
    public int RateLimitCps { get; set; } = 50;
    public int? CommitKey { get; set; } = null;
}