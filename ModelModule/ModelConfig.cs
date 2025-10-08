using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public sealed class ModelConfig : DictConfigBase
{
    public ModelConfig(string path)
    : base(path, defaults: new JObject
    {
        ["calib"] = new JObject
        {
            ["mode"] = "P"
        },
        ["constants"] = new JObject
        {
            ["PModeProportional"]  = 1.0,
            ["PModeOffset"]        = 0.0,
            ["PDModeProportional"] = 0.5,
            ["PDModeDerivative"]   = 0.2,
            ["PDModeOffset"]       = 0.0
        }
    })
    { }


    // The only guaranteed key
    public string GetModeRequired() => GetRequired<string>("calib.mode");

    // Generic helpers
    public bool TryGet<T>(string key, out T value) where T : notnull
    {
        lock (_sync)
        {
            value = default!;
            var tok = _root.SelectToken(key);
            if (tok == null) return false;
            try { value = tok.ToObject<T>(); return true; }
            catch { return false; }
        }
    }

    public T GetOrDefault<T>(string key, T dflt = default!)
    {
        return TryGet<T>(key, out var v) ? v : dflt!;
    }

    public T GetRequired<T>(string key)
    {
        if (TryGet<T>(key, out var v)) return v;
        throw new KeyNotFoundException($"Missing required parameter '{key}' in {Path}");
    }
}

