using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Sttify.Corelib.Diagnostics;

namespace Sttify.Corelib.Ime;

/// <summary>
/// Helper class for testing IME control functionality independently
/// </summary>
[ExcludeFromCodeCoverage] // Test utility class
public static class ImeTestHelper
{
    /// <summary>
    /// Performs a comprehensive test of IME control functionality
    /// </summary>
    /// <param name="testText">Text to use for testing (optional)</param>
    /// <param name="settings">IME settings to use (optional)</param>
    /// <returns>Test results</returns>
    [SupportedOSPlatform("windows")]
    public static async Task<ImeTestResult> TestImeControlAsync(string? testText = null, ImeSettings? settings = null)
    {
        var result = new ImeTestResult();
        testText ??= "IME制御テスト：これはテスト用のテキストです。This is a test.";
        settings ??= new ImeSettings();

        try
        {
            using var controller = new ImeController(settings);
            result.TestText = testText;
            result.Settings = settings;

            // Step 1: Get initial IME status
            result.InitialStatus = controller.GetCurrentImeStatus();
            result.Steps.Add($"Initial IME Status: HasContext={result.InitialStatus.HasImeContext}, IsOpen={result.InitialStatus.IsOpen}, IsComposing={result.InitialStatus.IsComposing}");
            result.Steps.Add($"Initial IME Mode: Native={result.InitialStatus.IsNativeMode}, Alpha={result.InitialStatus.IsAlphanumericMode}, FullShape={result.InitialStatus.IsFullShape}");

            // Step 2: Check if IME is composing
            result.WasComposingInitially = controller.IsImeComposing();
            result.Steps.Add($"IME composition check: {result.WasComposingInitially}");

            // Step 3: Test IME suppression
            result.Steps.Add("Testing IME suppression...");
            using (var suppressor = controller.SuppressImeTemporarily())
            {
                if (suppressor != null)
                {
                    result.SuppressionSucceeded = true;
                    result.Steps.Add("IME suppression activated successfully");
                    
                    await Task.Delay(200); // Allow time for suppression to take effect
                    
                    // Get suppressed status
                    result.SuppressedStatus = controller.GetCurrentImeStatus();
                    result.Steps.Add($"Suppressed IME Status: IsOpen={result.SuppressedStatus?.IsOpen}, Alpha={result.SuppressedStatus?.IsAlphanumericMode}");
                    
                    // Verify suppression worked as expected
                    if (settings.CloseImeWhenSending && result.SuppressedStatus?.IsOpen == false)
                    {
                        result.Steps.Add("✓ IME was successfully closed");
                    }
                    
                    if (settings.SetAlphanumericMode && result.SuppressedStatus?.IsAlphanumericMode == true)
                    {
                        result.Steps.Add("✓ IME was successfully set to alphanumeric mode");
                    }
                    
                    // Test composition clearing (can't directly verify, but log the attempt)
                    if (settings.ClearCompositionString)
                    {
                        result.Steps.Add("✓ Composition string clearing was attempted");
                    }
                }
                else
                {
                    result.SuppressionSucceeded = false;
                    result.Steps.Add("IME suppression not available (IME control disabled or no foreground window)");
                }
            } // IME state restored here

            // Step 4: Wait for restoration and check final status
            if (result.SuppressionSucceeded && settings.RestoreImeStateAfterSending)
            {
                await Task.Delay(Math.Max(settings.RestoreDelayMs, 100));
                result.FinalStatus = controller.GetCurrentImeStatus();
                result.Steps.Add($"Final IME Status: IsOpen={result.FinalStatus.IsOpen}, Alpha={result.FinalStatus.IsAlphanumericMode}");
                
                // Verify restoration
                if (result.InitialStatus.IsOpen == result.FinalStatus.IsOpen)
                {
                    result.Steps.Add("✓ IME open status was restored correctly");
                }
                else
                {
                    result.Steps.Add($"⚠ IME open status restoration: expected {result.InitialStatus.IsOpen}, got {result.FinalStatus.IsOpen}");
                }
            }

            result.Success = true;
            result.Steps.Add("IME control test completed successfully");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Steps.Add($"IME control test failed: {ex.Message}");
            Telemetry.LogError("ImeTestFailed", ex);
        }

        return result;
    }

    /// <summary>
    /// Quick test to check if IME control is working
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool QuickImeTest()
    {
        try
        {
            var settings = new ImeSettings();
            using var controller = new ImeController(settings);
            
            var status = controller.GetCurrentImeStatus();
            return status.HasImeContext; // Basic test: can we get IME context?
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets detailed information about the current IME state
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string GetImeStatusReport()
    {
        try
        {
            var settings = new ImeSettings();
            using var controller = new ImeController(settings);
            
            var status = controller.GetCurrentImeStatus();
            var isComposing = controller.IsImeComposing();
            
            return $"IME Status Report:\n" +
                   $"  Has IME Context: {status.HasImeContext}\n" +
                   $"  Is Open: {status.IsOpen}\n" +
                   $"  Is Composing: {isComposing}\n" +
                   $"  Conversion Mode: 0x{status.ConversionMode:X4}\n" +
                   $"  Sentence Mode: 0x{status.SentenceMode:X4}\n" +
                   $"  Is Native Mode: {status.IsNativeMode}\n" +
                   $"  Is Alphanumeric: {status.IsAlphanumericMode}\n" +
                   $"  Is Full Shape: {status.IsFullShape}\n" +
                   $"  Window Handle: 0x{status.WindowHandle:X8}";
        }
        catch (Exception ex)
        {
            return $"IME Status Report Error: {ex.Message}";
        }
    }
}

/// <summary>
/// Results of IME control testing
/// </summary>
public class ImeTestResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string TestText { get; set; } = "";
    public ImeSettings? Settings { get; set; }
    
    public ImeStatus InitialStatus { get; set; } = new();
    public ImeStatus? SuppressedStatus { get; set; }
    public ImeStatus FinalStatus { get; set; } = new();
    
    public bool WasComposingInitially { get; set; }
    public bool SuppressionSucceeded { get; set; }
    
    public List<string> Steps { get; set; } = new();
    
    public string GetReport()
    {
        var report = $"IME Control Test Report\n";
        report += $"Success: {Success}\n";
        
        if (!Success && !string.IsNullOrEmpty(ErrorMessage))
        {
            report += $"Error: {ErrorMessage}\n";
        }
        
        report += $"Test Text: {TestText}\n";
        report += $"Settings: EnableControl={Settings?.EnableImeControl}, CloseWhenSending={Settings?.CloseImeWhenSending}, SetAlpha={Settings?.SetAlphanumericMode}\n";
        report += $"\nTest Steps:\n";
        
        foreach (var step in Steps)
        {
            report += $"  {step}\n";
        }
        
        return report;
    }
}