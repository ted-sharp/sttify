using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.Kernel32;

namespace Sttify.Corelib.Ime;

/// <summary>
/// Win32 IME API declarations for IME control functionality
/// </summary>
[ExcludeFromCodeCoverage] // Win32 API wrapper, system integration
internal static class ImeNativeMethods
{
    // IME Constants
    public const int IMC_GETCANDIDATEPOS = 0x0007;
    public const int IMC_SETCANDIDATEPOS = 0x0008;
    public const int IMC_GETCOMPOSITIONFONT = 0x0009;
    public const int IMC_SETCOMPOSITIONFONT = 0x000A;
    public const int IMC_GETCOMPOSITIONWINDOW = 0x000B;
    public const int IMC_SETCOMPOSITIONWINDOW = 0x000C;
    public const int IMC_GETSTATUSWINDOWPOS = 0x000F;
    public const int IMC_SETSTATUSWINDOWPOS = 0x0010;
    public const int IMC_CLOSESTATUSWINDOW = 0x0021;
    public const int IMC_OPENSTATUSWINDOW = 0x0022;

    // Conversion modes
    public const int IME_CMODE_ALPHANUMERIC = 0x0000;
    public const int IME_CMODE_NATIVE = 0x0001;
    public const int IME_CMODE_CHINESE = IME_CMODE_NATIVE;
    public const int IME_CMODE_HANGUL = IME_CMODE_NATIVE;
    public const int IME_CMODE_JAPANESE = IME_CMODE_NATIVE;
    public const int IME_CMODE_KATAKANA = 0x0002;
    public const int IME_CMODE_LANGUAGE = 0x0003;
    public const int IME_CMODE_FULLSHAPE = 0x0008;
    public const int IME_CMODE_ROMAN = 0x0010;
    public const int IME_CMODE_CHARCODE = 0x0020;
    public const int IME_CMODE_HANJACONVERT = 0x0040;
    public const int IME_CMODE_SOFTKBD = 0x0080;
    public const int IME_CMODE_NOCONVERSION = 0x0100;
    public const int IME_CMODE_EUDC = 0x0200;
    public const int IME_CMODE_SYMBOL = 0x0400;
    public const int IME_CMODE_FIXED = 0x0800;

    // Sentence modes
    public const int IME_SMODE_NONE = 0x0000;
    public const int IME_SMODE_PLAURALCLAUSE = 0x0001;
    public const int IME_SMODE_SINGLECONVERT = 0x0002;
    public const int IME_SMODE_AUTOMATIC = 0x0004;
    public const int IME_SMODE_PHRASEPREDICT = 0x0008;
    public const int IME_SMODE_CONVERSATION = 0x0010;

    // Notify IME flags
    public const int NI_OPENCANDIDATE = 0x0010;
    public const int NI_CLOSECANDIDATE = 0x0011;
    public const int NI_SELECTCANDIDATESTR = 0x0012;
    public const int NI_CHANGECANDIDATELIST = 0x0013;
    public const int NI_FINALIZECONVERSIONRESULT = 0x0014;
    public const int NI_COMPOSITIONSTR = 0x0015;
    public const int NI_SETCANDIDATE_PAGESTART = 0x0016;
    public const int NI_SETCANDIDATE_PAGESIZE = 0x0017;
    public const int NI_IMEMENUSELECTED = 0x0018;

    // Composition string flags
    public const int GCS_COMPREADSTR = 0x0001;
    public const int GCS_COMPREADATTR = 0x0002;
    public const int GCS_COMPREADCLAUSE = 0x0004;
    public const int GCS_COMPSTR = 0x0008;
    public const int GCS_COMPATTR = 0x0010;
    public const int GCS_COMPCLAUSE = 0x0020;
    public const int GCS_CURSORPOS = 0x0080;
    public const int GCS_DELTASTART = 0x0100;
    public const int GCS_RESULTREADSTR = 0x0200;
    public const int GCS_RESULTREADCLAUSE = 0x0400;
    public const int GCS_RESULTSTR = 0x0800;
    public const int GCS_RESULTCLAUSE = 0x1000;

    // Set composition string types
    public const int SCS_SETSTR = (GCS_COMPREADSTR | GCS_COMPSTR);
    public const int SCS_CHANGEATTR = (GCS_COMPREADATTR | GCS_COMPATTR);
    public const int SCS_CHANGECLAUSE = (GCS_COMPREADCLAUSE | GCS_COMPCLAUSE);
    public const int SCS_SETRECONVERTSTRING = 0x00010000;
    public const int SCS_QUERYRECONVERTSTRING = 0x00020000;

    /// <summary>
    /// Retrieves the input context associated with the specified window
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetContext(IntPtr hWnd);

    /// <summary>
    /// Releases the input context
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    /// <summary>
    /// Opens or closes the IME
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

    /// <summary>
    /// Retrieves the open status of the IME
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmGetOpenStatus(IntPtr hIMC);

    /// <summary>
    /// Sets the current conversion status
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmSetConversionStatus(IntPtr hIMC, int fdwConversion, int fdwSentence);

    /// <summary>
    /// Retrieves the current conversion status
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmGetConversionStatus(IntPtr hIMC, out int fdwConversion, out int fdwSentence);

    /// <summary>
    /// Sets the composition string
    /// </summary>
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ImmSetCompositionString(IntPtr hIMC, int dwIndex, string lpComp, int dwCompLen, string lpRead, int dwReadLen);

    /// <summary>
    /// Retrieves information about the composition string
    /// </summary>
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);

    /// <summary>
    /// Notifies the IME about changes to the status of the input context
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern bool ImmNotifyIME(IntPtr hIMC, int dwAction, int dwIndex, int dwValue);

    // GetForegroundWindow, GetWindowThreadProcessId, GetCurrentThreadId, AttachThreadInput now provided by Vanara.PInvoke
}