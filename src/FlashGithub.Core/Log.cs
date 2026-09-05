namespace FlashGithub.Core;

/// <summary>极简的进程内日志，供引擎与界面共享。</summary>
public static class Log
{
    public static event Action<string>? OnMessage;

    public static void Info(string message) => Emit("信息", message);
    public static void Warn(string message) => Emit("警告", message);
    public static void Error(string message) => Emit("错误", message);

    private static void Emit(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        try { Console.WriteLine(line); } catch { }
        OnMessage?.Invoke(line);
    }
}
