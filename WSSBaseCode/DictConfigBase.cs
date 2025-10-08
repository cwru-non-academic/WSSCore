using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public abstract class DictConfigBase
{
    protected readonly object _sync = new();
    protected JObject _root;
    public string Path { get; }

    protected DictConfigBase(string path, JObject defaults)
    {
        Path = path;
        _root = JsonReader.LoadJObject(path, defaults);
    }

    public void Save()
    { lock (_sync) JsonReader.SaveJObject(Path, _root); }

    public bool TryGetFloat(string key, out float v)
    { lock (_sync) { v = 0f; var t = _root.SelectToken(key); if (t==null) return false; v = t.Value<float>(); return true; } }

    public bool TryGetInt(string key, out int v)
    { lock (_sync) { v = 0; var t = _root.SelectToken(key); if (t==null) return false; v = t.Value<int>(); return true; } }

    public bool TryGetString(string key, out string s)
    { lock (_sync) { s = null; var t = _root.SelectToken(key); if (t==null) return false; s = t.Type==JTokenType.String ? (string)t : t.ToString(); return true; } }

    public bool TryGetFloatArray(string key, out float[] arr)
    {
        lock (_sync)
        {
            arr = null;
            var t = _root.SelectToken(key);
            if (t is not JArray a) return false;
            var list = new List<float>();
            foreach (var x in a) list.Add(x.Value<float>());
            arr = list.ToArray(); return true;
        }
    }

    public float GetFloat(string key, float dflt = 0f) => TryGetFloat(key, out var v) ? v : dflt;
    public int   GetInt  (string key, int dflt = 0)   => TryGetInt  (key, out var v) ? v : dflt;
    public string GetString(string key, string dflt="") => TryGetString(key, out var s) ? s : dflt;

    public void Set(string dottedKey, JToken value)
    {
        lock (_sync)
        {
            var parts = dottedKey.Split('.');
            JObject cur = _root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var p = parts[i];
                if (cur[p] is not JObject next) { next = new JObject(); cur[p] = next; }
                cur = next;
            }
            cur[parts[^1]] = value;
        }
    }
}
