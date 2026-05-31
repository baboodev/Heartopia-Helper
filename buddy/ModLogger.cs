using System;
using System.IO;

#if MELONLOADER
using MelonLoader;
#elif BEPINEX
using BepInEx.Logging;
#endif

public static class ModLogger
{
#if BEPINEX
    private static ManualLogSource _log;
    private static StreamWriter _fileLog;

    public static void Init(ManualLogSource log)
    {
        _log = log;
        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "buddy.log");
            _fileLog = new StreamWriter(path, append: true) { AutoFlush = true };
            _fileLog.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === session start ===");
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Could not open UserData/buddy.log: " + ex.Message);
        }
    }

    private static void WriteFile(string level, string message)
    {
        try
        {
            _fileLog?.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }
        catch
        {
        }
    }
#endif

    public static void Msg(string message)
    {
#if MELONLOADER
        MelonLogger.Msg(message);
#elif BEPINEX
        _log?.LogInfo(message);
        WriteFile("INFO", message);
#endif
    }

    public static void Warning(string message)
    {
#if MELONLOADER
        MelonLogger.Warning(message);
#elif BEPINEX
        _log?.LogWarning(message);
        WriteFile("WARN", message);
#endif
    }
}
