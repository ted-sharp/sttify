using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Sttify.Corelib.Output;

[ExcludeFromCodeCoverage] // Win32 SendInput API integration, difficult to mock effectively
public class SendInputSink : ITextOutputSink
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;

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

        System.Diagnostics.Debug.WriteLine($"*** SendInputSink.SendAsync - Text: '{text}', Length: {text.Length} ***");
        
        // Debug: Check structure sizes
        int inputSize = Marshal.SizeOf<INPUT>();
        int expectedSize = IntPtr.Size == 8 ? 40 : 28; // x64 vs x86
        System.Diagnostics.Debug.WriteLine($"*** INPUT structure size: {inputSize} bytes (expected: {expectedSize}) ***");
        
        // Check if we're running elevated and target app privileges
        bool isElevated = IsProcessElevated();
        IntPtr targetWindow = GetForegroundWindow();
        bool targetElevated = IsWindowElevated(targetWindow);
        System.Diagnostics.Debug.WriteLine($"*** Sttify elevated: {isElevated}, Target window elevated: {targetElevated} ***");
        
        // Get target window information
        if (targetWindow != IntPtr.Zero)
        {
            var windowTitle = new StringBuilder(256);
            var className = new StringBuilder(256);
            GetWindowText(targetWindow, windowTitle, windowTitle.Capacity);
            GetClassName(targetWindow, className, className.Capacity);
            System.Diagnostics.Debug.WriteLine($"*** Target window: '{windowTitle}' (class: '{className}', handle: {targetWindow}) ***");
        }
        
        // UIPI Issue Detection
        if (isElevated && !targetElevated)
        {
            System.Diagnostics.Debug.WriteLine("*** UIPI BLOCKING: Elevated process cannot send input to non-elevated process ***");
            System.Diagnostics.Debug.WriteLine("*** Trying ChangeWindowMessageFilter to bypass UIPI ***");
            
            // Try to bypass UIPI by allowing specific messages
            ChangeWindowMessageFilter(0x0100, 1); // WM_KEYDOWN
            ChangeWindowMessageFilter(0x0101, 1); // WM_KEYUP
            ChangeWindowMessageFilter(0x0102, 1); // WM_CHAR
            ChangeWindowMessageFilter(0x0103, 1); // WM_DEADCHAR
            ChangeWindowMessageFilter(0x0104, 1); // WM_SYSKEYDOWN
            ChangeWindowMessageFilter(0x0105, 1); // WM_SYSKEYUP
            ChangeWindowMessageFilter(0x0106, 1); // WM_SYSCHAR
            ChangeWindowMessageFilter(0x0302, 1); // WM_PASTE
        }
        
        // Try direct SendInput first
        bool success = await SendTextViaInputAsync(text, cancellationToken);
        
        // If SendInput fails, try alternative methods
        if (!success)
        {
            // For UIPI case, try direct WM_CHAR messages first
            if (isElevated && !targetElevated)
            {
                System.Diagnostics.Debug.WriteLine("*** SendInput failed due to UIPI, trying direct WM_CHAR messages ***");
                bool charSuccess = await SendTextViaWmCharAsync(text, targetWindow, cancellationToken);
                if (charSuccess)
                {
                    System.Diagnostics.Debug.WriteLine("*** WM_CHAR method succeeded ***");
                    return;
                }
            }
            
            System.Diagnostics.Debug.WriteLine("*** SendInput failed, trying Win32 clipboard fallback ***");
            await SendTextViaWin32ClipboardAsync(text, cancellationToken);
        }
        
        System.Diagnostics.Debug.WriteLine($"*** SendInputSink.SendAsync - Completed sending '{text}' ***");
    }

    private async Task<bool> SendTextViaInputAsync(string text, CancellationToken cancellationToken)
    {
        var inputs = new List<INPUT>();
        var delayMs = _settings.RateLimitCps > 0 ? 1000 / _settings.RateLimitCps : 0;
        bool anySuccess = false;

        foreach (char c in text)
        {
            if (cancellationToken.IsCancellationRequested)
                return anySuccess;

            // Create UNICODE input
            var unicodeInput = new INPUT
            {
                type = INPUT_KEYBOARD,
                union = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
            
            inputs.Add(unicodeInput);

            // Send character immediately for better responsiveness
            if (inputs.Count == 1)
            {
                uint result = SendInput(1, inputs.ToArray(), Marshal.SizeOf<INPUT>());
                if (result == 0)
                {
                    uint error = GetLastError();
                    string errorMsg = error switch
                    {
                        87 => "ERROR_INVALID_PARAMETER - Invalid input structure or blocked by target",
                        5 => "ERROR_ACCESS_DENIED - Target app elevated or blocking input",
                        0 => "No error code - Likely UIPI blocking",
                        _ => $"Windows error {error}"
                    };
                    System.Diagnostics.Debug.WriteLine($"*** SendInput FAILED for char '{c}': {errorMsg} ***");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"*** SendInput SUCCESS for char '{c}': result={result} ***");
                    anySuccess = true;
                }
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
            uint result = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            if (result > 0) anySuccess = true;
        }

        // Send commit key if specified
        if (_settings.CommitKey != null)
        {
            var commitInputs = new INPUT[]
            {
                CreateKeyInput(_settings.CommitKey.Value, false),
                CreateKeyInput(_settings.CommitKey.Value, true)
            };
            uint result = SendInput(2, commitInputs, Marshal.SizeOf<INPUT>());
            if (result > 0) anySuccess = true;
        }

        return anySuccess;
    }

    private async Task SendTextViaWin32ClipboardAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            // Use Win32 clipboard APIs directly
            bool clipboardSet = SetClipboardText(text);
            
            if (clipboardSet)
            {
                System.Diagnostics.Debug.WriteLine("*** Win32 clipboard set successfully ***");
                
                // Check active window before sending Ctrl+V
                IntPtr activeWindow = GetForegroundWindow();
                System.Diagnostics.Debug.WriteLine($"*** Active window handle: {activeWindow} ***");
                
                // Try alternative: Send VK_INSERT with Shift (Shift+Insert = Paste)
                System.Diagnostics.Debug.WriteLine("*** Trying Shift+Insert (alternative paste) ***");
                var shiftDown = CreateKeyInput(0x10, false);  // Shift down
                var insertDown = CreateKeyInput(0x2D, false); // Insert down
                var insertUp = CreateKeyInput(0x2D, true);    // Insert up  
                var shiftUp = CreateKeyInput(0x10, true);     // Shift up

                uint sr1 = SendInput(1, new[] { shiftDown }, Marshal.SizeOf<INPUT>());
                await Task.Delay(10, cancellationToken);
                uint sr2 = SendInput(1, new[] { insertDown }, Marshal.SizeOf<INPUT>());
                await Task.Delay(10, cancellationToken);
                uint sr3 = SendInput(1, new[] { insertUp }, Marshal.SizeOf<INPUT>());
                await Task.Delay(10, cancellationToken);
                uint sr4 = SendInput(1, new[] { shiftUp }, Marshal.SizeOf<INPUT>());
                
                System.Diagnostics.Debug.WriteLine($"*** Shift+Insert results: Shift={sr1}, Insert_down={sr2}, Insert_up={sr3}, Shift_up={sr4} ***");
                
                await Task.Delay(100, cancellationToken);
                
                // Also try Ctrl+V as backup
                System.Diagnostics.Debug.WriteLine("*** Also trying Ctrl+V ***");
                var ctrlDown = CreateKeyInput(0x11, false); // Ctrl down
                var vDown = CreateKeyInput(0x56, false);     // V down  
                var vUp = CreateKeyInput(0x56, true);        // V up
                var ctrlUp = CreateKeyInput(0x11, true);     // Ctrl up

                uint result1 = SendInput(1, new[] { ctrlDown }, Marshal.SizeOf<INPUT>());
                await Task.Delay(10, cancellationToken);
                uint result2 = SendInput(1, new[] { vDown }, Marshal.SizeOf<INPUT>()); 
                await Task.Delay(10, cancellationToken);
                uint result3 = SendInput(1, new[] { vUp }, Marshal.SizeOf<INPUT>());
                await Task.Delay(10, cancellationToken);
                uint result4 = SendInput(1, new[] { ctrlUp }, Marshal.SizeOf<INPUT>());

                System.Diagnostics.Debug.WriteLine($"*** Ctrl+V results: Ctrl={result1}, V_down={result2}, V_up={result3}, Ctrl_up={result4} ***");

                await Task.Delay(100, cancellationToken);
                
                // Final attempt: Send WM_PASTE message directly to active window
                System.Diagnostics.Debug.WriteLine("*** Trying direct WM_PASTE message ***");
                IntPtr currentWindow = GetForegroundWindow();
                if (currentWindow != IntPtr.Zero)
                {
                    const int WM_PASTE = 0x0302;
                    IntPtr result = SendMessage(currentWindow, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                    System.Diagnostics.Debug.WriteLine($"*** WM_PASTE result: {result} to window {currentWindow} ***");
                }

                await Task.Delay(50, cancellationToken); // Small delay for clipboard operation
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("*** Failed to set Win32 clipboard ***");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Win32 clipboard fallback failed: {ex.Message} ***");
        }
    }

    private static bool SetClipboardText(string text)
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero))
                return false;

            EmptyClipboard();

            IntPtr hGlobal = Marshal.StringToHGlobalUni(text);
            if (hGlobal == IntPtr.Zero)
            {
                CloseClipboard();
                return false;
            }

            if (SetClipboardData(13, hGlobal) == IntPtr.Zero) // CF_UNICODETEXT = 13
            {
                Marshal.FreeHGlobal(hGlobal);
                CloseClipboard();
                return false;
            }

            CloseClipboard();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SendTextViaWmCharAsync(string text, IntPtr targetWindow, CancellationToken cancellationToken)
    {
        try
        {
            if (targetWindow == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("*** WM_CHAR: No target window ***");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"*** Sending WM_CHAR messages to window {targetWindow} ***");
            bool anySuccess = false;

            foreach (char c in text)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                const int WM_CHAR = 0x0102;
                IntPtr result = SendMessage(targetWindow, WM_CHAR, new IntPtr(c), IntPtr.Zero);
                
                if (result != IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"*** WM_CHAR SUCCESS for '{c}': result={result} ***");
                    anySuccess = true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"*** WM_CHAR FAILED for '{c}': result={result} ***");
                }

                // Small delay between characters
                await Task.Delay(5, cancellationToken);
            }

            return anySuccess;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** WM_CHAR method failed: {ex.Message} ***");
            return false;
        }
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindowElevated(IntPtr windowHandle)
    {
        try
        {
            GetWindowThreadProcessId(windowHandle, out uint processId);
            IntPtr processHandle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (processHandle == IntPtr.Zero)
                return false;

            bool result = OpenProcessToken(processHandle, 0x0008, out IntPtr tokenHandle); // TOKEN_QUERY
            if (!result)
            {
                CloseHandle(processHandle);
                return false;
            }

            const int TokenElevationType = 18;
            bool elevated = GetTokenInformation(tokenHandle, TokenElevationType, IntPtr.Zero, 0, out uint returnLength);
            
            CloseHandle(tokenHandle);
            CloseHandle(processHandle);
            
            return elevated;
        }
        catch
        {
            return false;
        }
    }

    private static INPUT CreateKeyInput(int virtualKey, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = 0,
                    dwFlags = keyUp ? 0x0002u : 0u,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll")]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // Microsoft公式仕様に基づく正確な定義
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SendInputSettings
{
    public int RateLimitCps { get; set; } = 50;
    public int? CommitKey { get; set; } = null;
}