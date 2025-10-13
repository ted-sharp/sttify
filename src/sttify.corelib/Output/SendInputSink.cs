using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Sttify.Corelib.Diagnostics;
using Sttify.Corelib.Ime;
using Vanara.PInvoke;
using static Vanara.PInvoke.Kernel32;
using static Vanara.PInvoke.User32;
using CsINPUT = Windows.Win32.UI.Input.KeyboardAndMouse.INPUT;
using CsINPUT_TYPE = Windows.Win32.UI.Input.KeyboardAndMouse.INPUT_TYPE;
using CsKEYBD_EVENT_FLAGS = Windows.Win32.UI.Input.KeyboardAndMouse.KEYBD_EVENT_FLAGS;
using CsKEYBDINPUT = Windows.Win32.UI.Input.KeyboardAndMouse.KEYBDINPUT;
using CsVIRTUAL_KEY = Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY;
// Reserved for future CsWin32 migrations
using Win32PInvoke = Windows.Win32.PInvoke;

namespace Sttify.Corelib.Output;

[ExcludeFromCodeCoverage] // Win32 SendInput API integration, difficult to mock effectively
public class SendInputSink : ITextOutputSink
{
    private const uint CF_UNICODETEXT = 13;
    private readonly ImeController _imeController;

    private readonly SendInputSettings _settings;

    public SendInputSink(SendInputSettings? settings = null)
    {
        _settings = settings ?? new SendInputSettings();
        _imeController = new ImeController(_settings.Ime);
    }

    public string Id => "sendinput";
    public string Name => "SendInput";
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [SupportedOSPlatform("windows")]
    public Task<bool> CanSendAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Task.FromResult(false);

        // Check if IME is currently composing and we should skip
        if (_settings.Ime.SkipWhenImeComposing && _imeController.IsImeComposing())
        {
            Telemetry.LogEvent("SendInputSkippedDueToImeComposition");
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    [SupportedOSPlatform("windows")]
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return;

        System.Diagnostics.Debug.WriteLine($"*** SendInputSink.SendAsync - Text: '{text}', Length: {text.Length} ***");

        // Check if we can send (this also checks IME composition status)
        if (!await CanSendAsync(cancellationToken))
        {
            System.Diagnostics.Debug.WriteLine("*** SendInputSink: Cannot send - skipping ***");
            return;
        }

        // Debug: Check structure sizes
        int inputSize = Marshal.SizeOf<CsINPUT>();
        int expectedSize = IntPtr.Size == 8 ? 40 : 28; // x64 vs x86
        System.Diagnostics.Debug.WriteLine($"*** INPUT structure size: {inputSize} bytes (expected: {expectedSize}) ***");

        // Check if we're running elevated and target app privileges
        // This code path runs only on Windows. Mark as Windows-specific to satisfy CA1416 when cross-targeting.
        bool isElevated = OperatingSystem.IsWindows() && IsProcessElevated();
        IntPtr targetWindow = OperatingSystem.IsWindows() ? (IntPtr)GetForegroundWindow() : IntPtr.Zero;
        bool targetElevated = IsWindowElevated(targetWindow);
        System.Diagnostics.Debug.WriteLine($"*** Sttify elevated: {isElevated}, Target window elevated: {targetElevated} ***");

        // Get target window information
        if (targetWindow != IntPtr.Zero)
        {
            var windowTitle = new StringBuilder(256);
            var className = new StringBuilder(256);
            GetWindowText(new HWND(targetWindow), windowTitle, windowTitle.Capacity);
            GetClassName(new HWND(targetWindow), className, className.Capacity);
            System.Diagnostics.Debug.WriteLine($"*** Target window: '{windowTitle}' (class: '{className}', handle: {targetWindow}) ***");
        }

        // UIPI Issue Detection
        if (OperatingSystem.IsWindows() && isElevated && !targetElevated)
        {
            System.Diagnostics.Debug.WriteLine("*** UIPI BLOCKING: Elevated process cannot send input to non-elevated process ***");
            System.Diagnostics.Debug.WriteLine("*** Trying ChangeWindowMessageFilter to bypass UIPI ***");

            // Try to bypass UIPI by allowing specific messages
            ChangeWindowMessageFilter(0x0100, MessageFilterFlag.MSGFLT_ADD); // WM_KEYDOWN
            ChangeWindowMessageFilter(0x0101, MessageFilterFlag.MSGFLT_ADD); // WM_KEYUP
            ChangeWindowMessageFilter(0x0102, MessageFilterFlag.MSGFLT_ADD); // WM_CHAR
            ChangeWindowMessageFilter(0x0103, MessageFilterFlag.MSGFLT_ADD); // WM_DEADCHAR
            ChangeWindowMessageFilter(0x0104, MessageFilterFlag.MSGFLT_ADD); // WM_SYSKEYDOWN
            ChangeWindowMessageFilter(0x0105, MessageFilterFlag.MSGFLT_ADD); // WM_SYSKEYUP
            ChangeWindowMessageFilter(0x0106, MessageFilterFlag.MSGFLT_ADD); // WM_SYSCHAR
            ChangeWindowMessageFilter(0x0302, MessageFilterFlag.MSGFLT_ADD); // WM_PASTE
        }

        // Suppress IME before sending text to prevent conflicts
        IDisposable? imeRestorer = null;
        try
        {
            if (_settings.Ime.EnableImeControl)
            {
                imeRestorer = _imeController.SuppressImeTemporarily();
                System.Diagnostics.Debug.WriteLine("*** IME suppression activated ***");

                // Add small delay to ensure IME state change takes effect
                if (_settings.Ime.RestoreDelayMs > 0)
                {
                    await Task.Delay(Math.Min(_settings.Ime.RestoreDelayMs / 2, 50), cancellationToken);
                }
            }

            // Try direct SendInput first (handles surrogate pairs)
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
                        // Don't return here - we still need to restore IME
                        success = true;
                    }
                }

                if (!success)
                {
                    System.Diagnostics.Debug.WriteLine("*** SendInput failed, trying Win32 clipboard fallback ***");
                    await SendTextViaWin32ClipboardAsync(text, cancellationToken);
                }
            }
        }
        finally
        {
            // Restore IME state after sending text
            if (imeRestorer != null)
            {
                if (_settings.Ime.RestoreDelayMs > 0)
                {
                    await Task.Delay(_settings.Ime.RestoreDelayMs, cancellationToken);
                }

                imeRestorer.Dispose();
                System.Diagnostics.Debug.WriteLine("*** IME state restored ***");
            }
        }

        System.Diagnostics.Debug.WriteLine($"*** SendInputSink.SendAsync - Completed sending '{text}' ***");
    }

    /// <summary>
    /// Check if the current Windows version supports SendInput API (Windows 5.0+)
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsSendInputSupported()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            // Check Windows version - SendInput is supported on Windows 5.0+
            var version = Environment.OSVersion;
            return version.Version.Major >= 10; // Windows 10+ (which is Windows 5.0+ internally)
        }
        catch
        {
            // Fallback: assume supported if we can't determine version
            return true;
        }
    }

    /// <summary>
    /// Safely call SendInput with Windows 5.0+ compatibility check
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe uint SafeSendInput(uint nInputs, CsINPUT* pInputs, int cbSize)
    {
        if (!IsSendInputSupported())
        {
            System.Diagnostics.Debug.WriteLine("*** SendInput not supported on this Windows version ***");
            return 0;
        }

        // Windows 5.0+ compatibility check for SendInput API
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            System.Diagnostics.Debug.WriteLine("*** SendInput requires Windows 10+ (Windows 5.0+) ***");
            return 0;
        }

        return Win32PInvoke.SendInput(nInputs, pInputs, cbSize);
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> SendTextViaInputAsync(string text, CancellationToken cancellationToken)
    {
        var delayMs = _settings.RateLimitCps > 0 ? 1000 / _settings.RateLimitCps : 0;
        bool anySuccess = false;

        // Pre-allocate input arrays to avoid stackalloc in loops
        var singleInput = new CsINPUT[1];
        var doubleInput = new CsINPUT[2];

        // Iterate over text by Unicode scalar values to handle surrogate pairs
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            if (cancellationToken.IsCancellationRequested)
                return anySuccess;

            var element = enumerator.GetTextElement();
            foreach (var ch in element)
            {
                // Send UNICODE key down
                var downInput = new CsINPUT
                {
                    type = CsINPUT_TYPE.INPUT_KEYBOARD,
                    Anonymous = new()
                    {
                        ki = new CsKEYBDINPUT
                        {
                            wVk = default,
                            wScan = ch,
                            dwFlags = CsKEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                };

                uint resultDown;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = downInput;
                        resultDown = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                if (resultDown == 0)
                {
                    uint error = (uint)GetLastError();
                    string errorMsgDown = error switch
                    {
                        87 => "ERROR_INVALID_PARAMETER - Invalid input structure or blocked by target",
                        5 => "ERROR_ACCESS_DENIED - Target app elevated or blocking input",
                        0 => "No error code - Likely UIPI blocking",
                        _ => $"Windows error {error}"
                    };
                    System.Diagnostics.Debug.WriteLine($"*** SendInput (down) FAILED for char '{ch}': {errorMsgDown} ***");
                }
                else
                {
                    anySuccess = true;
                }

                // Send UNICODE key up (required to avoid stuck keys)
                var upInput = new CsINPUT
                {
                    type = CsINPUT_TYPE.INPUT_KEYBOARD,
                    Anonymous = new()
                    {
                        ki = new CsKEYBDINPUT
                        {
                            wVk = default,
                            wScan = ch,
                            dwFlags = CsKEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | CsKEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                };

                uint resultUp;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = upInput;
                        resultUp = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                if (resultUp == 0)
                {
                    uint error = (uint)GetLastError();
                    string errorMsgUp = error switch
                    {
                        87 => "ERROR_INVALID_PARAMETER - Invalid input structure or blocked by target",
                        5 => "ERROR_ACCESS_DENIED - Target app elevated or blocking input",
                        0 => "No error code - Likely UIPI blocking",
                        _ => $"Windows error {error}"
                    };
                    System.Diagnostics.Debug.WriteLine($"*** SendInput (up) FAILED for char '{ch}': {errorMsgUp} ***");
                }
            }

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        // Send commit key if specified
        if (_settings.CommitKey != null)
        {
            var commitDown = CreateKeyInput(_settings.CommitKey.Value, false);
            var commitUp = CreateKeyInput(_settings.CommitKey.Value, true);
            uint result;
            unsafe
            {
                fixed (CsINPUT* inputs = doubleInput)
                {
                    inputs[0] = commitDown;
                    inputs[1] = commitUp;
                    result = SafeSendInput(2, inputs, Marshal.SizeOf<CsINPUT>());
                }
            }
            if (result > 0)
                anySuccess = true;
        }

        return anySuccess;
    }

    [SupportedOSPlatform("windows")]
    private async Task SendTextViaWin32ClipboardAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            // Backup clipboard text (best effort) - use Win32 only to avoid WPF dependency
            string? original = null;
            try
            {
                original = GetClipboardText();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"*** Failed to backup clipboard: {ex.Message} ***");
            }

            // Use Win32 clipboard APIs directly
            bool clipboardSet = SetClipboardText(text);

            if (clipboardSet)
            {
                System.Diagnostics.Debug.WriteLine("*** Win32 clipboard set successfully ***");

                // Check active window before sending Ctrl+V
                IntPtr activeWindow = (IntPtr)GetForegroundWindow();
                System.Diagnostics.Debug.WriteLine($"*** Active window handle: {activeWindow} ***");

                // Pre-allocate input arrays to avoid stackalloc in loops
                var singleInput = new CsINPUT[1];

                // Try alternative: Send VK_INSERT with Shift (Shift+Insert = Paste)
                System.Diagnostics.Debug.WriteLine("*** Trying Shift+Insert (alternative paste) ***");
                var shiftDown = CreateKeyInput(0x10, false);  // Shift down
                var insertDown = CreateKeyInput(0x2D, false); // Insert down
                var insertUp = CreateKeyInput(0x2D, true);    // Insert up
                var shiftUp = CreateKeyInput(0x10, true);     // Shift up

                uint sr1;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = shiftDown;
                        sr1 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                await Task.Delay(10, cancellationToken);
                uint sr2;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = insertDown;
                        sr2 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                await Task.Delay(10, cancellationToken);
                uint sr3;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = insertUp;
                        sr3 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                await Task.Delay(10, cancellationToken);
                uint sr4;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = shiftUp;
                        sr4 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }

                System.Diagnostics.Debug.WriteLine($"*** Shift+Insert results: Shift={sr1}, Insert_down={sr2}, Insert_up={sr3}, Shift_up={sr4} ***");

                await Task.Delay(100, cancellationToken);

                // Also try Ctrl+V as backup
                System.Diagnostics.Debug.WriteLine("*** Also trying Ctrl+V ***");
                var ctrlDown = CreateKeyInput(0x11, false); // Ctrl down
                var vDown = CreateKeyInput(0x56, false);     // V down
                var vUp = CreateKeyInput(0x56, true);        // V up
                var ctrlUp = CreateKeyInput(0x11, true);     // Ctrl up

                uint result1;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = ctrlDown;
                        result1 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                await Task.Delay(10, cancellationToken);
                uint result2;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = vDown;
                        result2 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                await Task.Delay(10, cancellationToken);
                uint result3;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = vUp;
                        result3 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }
                await Task.Delay(10, cancellationToken);
                uint result4;
                unsafe
                {
                    fixed (CsINPUT* inputs = singleInput)
                    {
                        singleInput[0] = ctrlUp;
                        result4 = SafeSendInput(1, inputs, Marshal.SizeOf<CsINPUT>());
                    }
                }

                System.Diagnostics.Debug.WriteLine($"*** Ctrl+V results: Ctrl={result1}, V_down={result2}, V_up={result3}, Ctrl_up={result4} ***");

                await Task.Delay(100, cancellationToken);

                // Final attempt: Send WM_PASTE message directly to active window
                System.Diagnostics.Debug.WriteLine("*** Trying direct WM_PASTE message ***");
                IntPtr currentWindow = (IntPtr)GetForegroundWindow();
                if (currentWindow != IntPtr.Zero)
                {
                    const uint WM_PASTE = 0x0302;
                    var lres = SendMessage(
                        new HWND(currentWindow),
                        WM_PASTE,
                        new IntPtr(0),
                        new IntPtr(0));
                    System.Diagnostics.Debug.WriteLine($"*** WM_PASTE result: {lres} to window {currentWindow} ***");
                }

                await Task.Delay(50, cancellationToken); // Small delay for clipboard operation
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("*** Failed to set Win32 clipboard ***");
            }
            // Restore clipboard (best effort)
            try
            {
                if (original != null)
                {
                    SetClipboardText(original);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"*** Failed to restore clipboard: {ex.Message} ***");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"*** Win32 clipboard fallback failed: {ex.Message} ***");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetClipboardText()
    {
        try
        {
            if (!OpenClipboard(HWND.NULL))
                return null;

            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                CloseClipboard();
                return null;
            }

            IntPtr ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                CloseClipboard();
                return null;
            }

            try
            {
                string text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                return text;
            }
            finally
            {
                GlobalUnlock(handle);
                CloseClipboard();
            }
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SetClipboardText(string text)
    {
        try
        {
            if (!OpenClipboard(HWND.NULL))
                return false;

            EmptyClipboard();

            IntPtr hGlobal = Marshal.StringToHGlobalUni(text);
            if (hGlobal == IntPtr.Zero)
            {
                CloseClipboard();
                return false;
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
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

    [SupportedOSPlatform("windows")]
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
                IntPtr result = SendMessage(new HWND(targetWindow), WM_CHAR, new IntPtr(c), IntPtr.Zero);

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
            if (!OperatingSystem.IsWindows())
                return false;
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
            if (!OperatingSystem.IsWindows())
                return false;
            GetWindowThreadProcessId(new HWND(windowHandle), out uint processId);
            using var processHandle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (processHandle == IntPtr.Zero)
                return false;

            bool result = OpenProcessToken(processHandle, 0x0008, out IntPtr tokenHandle); // TOKEN_QUERY
            if (!result)
            {
                return false; // processHandle will be disposed automatically
            }

            // Query TokenElevation (20) to determine if the target process is elevated
            const int TokenElevation = 20;
            int elevationSize = Marshal.SizeOf<TOKEN_ELEVATION>();
            IntPtr elevationPtr = Marshal.AllocHGlobal(elevationSize);
            try
            {
                bool gotInfo = GetTokenInformation(tokenHandle, TokenElevation, elevationPtr, (uint)elevationSize, out uint _);
                var elevation = gotInfo ? Marshal.PtrToStructure<TOKEN_ELEVATION>(elevationPtr) : default;

                CloseHandle(tokenHandle);

                return gotInfo && elevation.TokenIsElevated != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(elevationPtr);
            }
        }
        catch
        {
            return false;
        }
    }

    private static CsINPUT CreateKeyInput(int virtualKey, bool keyUp)
    {
        return new CsINPUT
        {
            type = CsINPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new()
            {
                ki = new CsKEYBDINPUT
                {
                    wVk = (CsVIRTUAL_KEY)virtualKey,
                    wScan = 0,
                    dwFlags = keyUp ? CsKEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    // GetLastError: use CsWin32 PInvoke.GetLastError()

    // OpenClipboard, CloseClipboard, EmptyClipboard, SetClipboardData, GetClipboardData now provided by Vanara.PInvoke

    // GetForegroundWindow, SendMessage now provided by Vanara.PInvoke

    // GetWindowThreadProcessId now provided by Vanara.PInvoke

    // ChangeWindowMessageFilter, GlobalLock, GlobalUnlock, GetWindowText, GetClassName now provided by Vanara.PInvoke

    // OpenProcess now provided by Vanara.PInvoke.Kernel32
    // OpenProcessToken and GetTokenInformation require AdvApi32 which is not available in Vanara

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeHandle ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }
}

[ExcludeFromCodeCoverage] // Simple configuration class with no business logic
public class SendInputSettings
{
    public int RateLimitCps { get; set; } = 50;
    public int? CommitKey { get; set; }
    public ImeSettings Ime { get; set; } = new ImeSettings();
}
