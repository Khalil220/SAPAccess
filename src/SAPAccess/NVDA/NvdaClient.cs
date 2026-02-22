using System;
using System.Runtime.InteropServices;

namespace SAPAccess.NVDA;

/// <summary>
/// Thin P/Invoke wrapper around nvdaControllerClient64.dll.
/// All methods are safe to call even when NVDA is not running.
/// </summary>
public static class NvdaClient
{
    private const string DllName = "nvdaControllerClient64.dll";

    private static bool _available = true;

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int nvdaController_testIfRunning();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode)]
    private static extern int nvdaController_speakText(
        [MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int nvdaController_cancelSpeech();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode)]
    private static extern int nvdaController_brailleMessage(
        [MarshalAs(UnmanagedType.LPWStr)] string message);

    /// <summary>Whether the NVDA client DLL was found and loaded.</summary>
    public static bool IsAvailable => _available;

    /// <summary>Returns true if NVDA is currently running.</summary>
    public static bool IsRunning()
    {
        if (!_available) return false;
        try
        {
            return nvdaController_testIfRunning() == 0;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            return false;
        }
    }

    /// <summary>Speaks text via NVDA. Returns true on success.</summary>
    public static bool Speak(string text)
    {
        if (!_available || string.IsNullOrEmpty(text)) return false;
        try
        {
            return nvdaController_speakText(text) == 0;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            return false;
        }
    }

    /// <summary>Cancels any current NVDA speech.</summary>
    public static bool CancelSpeech()
    {
        if (!_available) return false;
        try
        {
            return nvdaController_cancelSpeech() == 0;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            return false;
        }
    }

    /// <summary>Displays a message on the braille display.</summary>
    public static bool Braille(string message)
    {
        if (!_available || string.IsNullOrEmpty(message)) return false;
        try
        {
            return nvdaController_brailleMessage(message) == 0;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            return false;
        }
    }
}
