using System.Collections.Generic;
using BepInEx.Logging;

namespace SAPAccess.NVDA;

/// <summary>
/// High-level screen reader API. Manages speech with interrupt/queue semantics.
/// Falls back to BepInEx logging when NVDA is unavailable.
/// </summary>
public class ScreenReader
{
    private static ScreenReader? _instance;
    public static ScreenReader Instance => _instance ??= new ScreenReader();

    private readonly ManualLogSource _log;
    private readonly Queue<string> _queue = new();

    private ScreenReader()
    {
        _log = Logger.CreateLogSource("SAPAccess.Speech");
    }

    /// <summary>
    /// Interrupts current speech and speaks the given text immediately.
    /// </summary>
    public void Say(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        _queue.Clear();

        if (NvdaClient.IsRunning())
        {
            NvdaClient.CancelSpeech();
            NvdaClient.Speak(text);
        }

        _log.LogInfo($"[SAY] {text}");
    }

    /// <summary>
    /// Queues text to be spoken after current speech finishes.
    /// Since NVDA handles its own queue, this just sends to NVDA without canceling.
    /// </summary>
    public void SayQueued(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (NvdaClient.IsRunning())
        {
            NvdaClient.Speak(text);
        }

        _log.LogInfo($"[QUEUE] {text}");
    }

    /// <summary>Stops all speech immediately.</summary>
    public void Stop()
    {
        _queue.Clear();

        if (NvdaClient.IsRunning())
        {
            NvdaClient.CancelSpeech();
        }
    }

    /// <summary>
    /// Speaks text and sends it to the braille display simultaneously.
    /// </summary>
    public void Output(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        Say(text);
        NvdaClient.Braille(text);
    }
}
