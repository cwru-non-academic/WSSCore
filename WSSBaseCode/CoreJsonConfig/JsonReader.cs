using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class JsonReader
{
    public static T LoadObject<T>(string path, T defaults) where T : class
    {
        if (File.Exists(path))
        {
            var txt = File.ReadAllText(path);
            var obj = JsonConvert.DeserializeObject<T>(txt);
            if (obj != null) return obj;
        }
        SaveObject(path, defaults);
        return defaults;
    }

    public static void SaveObject<T>(string path, T obj)
    {
        EnsureDir(path);
        File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
    }

    public static JObject LoadJObject(string path, JObject defaults)
    {
        if (File.Exists(path))
        {
            var txt = File.ReadAllText(path);
            return JObject.Parse(txt);
        }
        SaveJObject(path, defaults);
        return defaults;
    }

    public static void SaveJObject(string path, JObject obj)
    {
        EnsureDir(path);
        File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
    }

    private static void EnsureDir(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
