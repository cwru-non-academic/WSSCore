#if UNITY_2019_1_OR_NEWER
using Debug = UnityEngine.Debug;
#endif
using System;

public static class Log
{
    public static void Info(string msg)
    {
#if UNITY_2019_1_OR_NEWER
        Debug.Log(msg);
#else
        Console.WriteLine(msg);
#endif
    }

    public static void Warn(string msg)
    {
#if UNITY_2019_1_OR_NEWER
        Debug.LogWarning(msg);
#else
        Console.WriteLine("[WARN] " + msg);
#endif
    }

    public static void Error(string msg, Exception ex = null)
    {
#if UNITY_2019_1_OR_NEWER
        Debug.LogError(ex == null ? msg : $"{msg}\n{ex}");
#else
        Console.Error.WriteLine(ex == null ? msg : $"{msg}\n{ex}");
#endif
    }
}
