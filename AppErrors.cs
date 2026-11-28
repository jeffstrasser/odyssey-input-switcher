namespace OdysseyInputSwitcher;

internal enum ErrorContext { Monitor, Settings, Update, Cleanup }

internal static class AppErrors
{
    internal static string Describe(Exception error, ErrorContext context) => context switch
    {
        ErrorContext.Settings when error is UnauthorizedAccessException =>
            "Windows wouldn't let us save your settings. Check that your account can write to the app's settings folder, then try again.",
        ErrorContext.Settings when error is System.Text.Json.JsonException =>
            "We couldn't read your saved settings. Your files have been kept. Open Diagnostics for details before repairing them.",
        ErrorContext.Settings when error is InvalidOperationException =>
            "We couldn't apply these settings. Check the selected monitor and input names and values, then try again. Open Diagnostics for the specific problem.",
        ErrorContext.Settings =>
            "We couldn't read or save your settings. Your saved files have been kept. Check available disk space and folder permissions, then try again.",
        ErrorContext.Update =>
            "We couldn't complete the update. Your installed version hasn't been changed. Check your internet connection and try Settings > Check for updates again. If it still fails, open Diagnostics.",
        ErrorContext.Cleanup =>
            "The update's temporary files couldn't be removed. You can keep using the app. Open Diagnostics for details.",
        _ =>
            "We couldn't communicate with the monitor. Check that it's awake and connected, enable DDC/CI if supported, then choose Refresh. If you were switching inputs, check the monitor's actual input before retrying."
    };
}
