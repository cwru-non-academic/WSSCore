using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

/// <summary>
/// Controls loading, validating, reading, and writing a stimulation configuration JSON file.
/// Thread-safe. Ensures a valid default config exists on disk.
/// </summary>
public sealed class StimConfigController
{
    private readonly object _sync = new object();
    private StimConfig _config;
    private readonly string _configPath;
    private bool _jsonLoaded;

    /// <summary>
    /// Initializes a controller that reads/writes "stimConfig.json" in the current directory.
    /// </summary>
    public StimConfigController() : this(Path.Combine(Environment.CurrentDirectory, "stimConfig.json")) { }

    /// <summary>
    /// Initializes a controller pointing to a custom path.
    /// If a directory path is provided, "stimConfig.json" is created inside it.
    /// </summary>
    /// <param name="path">Absolute or relative file path, or a directory path.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null, empty, or whitespace.</exception>
    public StimConfigController(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Invalid config path.", nameof(path));

        // If a directory was provided, append the default filename.
        if (Directory.Exists(path)
            || path.EndsWith(Path.DirectorySeparatorChar.ToString())
            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
        {
            _configPath = Path.Combine(path, "stimConfig.json");
        }
        else
        {
            _configPath = path;
        }
    }

    /// <summary>
    /// Gets the resolved configuration file path.
    /// </summary>
    public string ConfigPath => _configPath;

    /// <summary>
    /// Indicates whether the JSON configuration has been loaded into memory.
    /// </summary>
    public bool IsLoaded => Volatile.Read(ref _jsonLoaded);

    /// <summary>
    /// Loads the configuration from disk. Creates and saves a default config if missing or invalid.
    /// Safe to call multiple times.
    /// </summary>
    public void LoadJson()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var text = File.ReadAllText(_configPath);
                    _config = JsonConvert.DeserializeObject<StimConfig>(text) ?? new StimConfig();
                    if (!VerifyJson(_config))
                    {
                        _config = GenerateInitialConfig();
                        SaveJson_NoLock(); // write defaults
                    }
                }
                else
                {
                    _config = GenerateInitialConfig();
                    SaveJson_NoLock();
                }

                _jsonLoaded = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[StimConfigController] Load error: {ex.Message}. Generating defaults.");
                _config = GenerateInitialConfig();
                SaveJson_NoLock();
                _jsonLoaded = true;
            }
        }
    }

    /// <summary>
    /// Persists the current configuration to disk.
    /// No-op if not loaded yet.
    /// </summary>
    public void SaveJson()
    {
        lock (_sync)
        {
            if (!_jsonLoaded || _config is null) return;
            SaveJson_NoLock();
        }
    }

    /// <summary>
    /// Adds or updates a constant value and saves to disk.
    /// </summary>
    /// <param name="name">Dictionary key. Must be non-empty.</param>
    /// <param name="value">Constant value.</param>
    /// <exception cref="InvalidOperationException">If JSON is not loaded.</exception>
    /// <exception cref="ArgumentException">If <paramref name="name"/> is invalid.</exception>
    public void AddOrUpdateConstant(string name, float value)
    {
        EnsureLoaded();
        ValidateKey(name);
        lock (_sync)
        {
            _config.constants[name] = value;
            SaveJson_NoLock();
        }
    }

    /// <summary>
    /// Adds or updates a stimulation parameter value and saves to disk.
    /// </summary>
    /// <param name="name">Dictionary key. Must be non-empty.</param>
    /// <param name="value">Parameter value.</param>
    /// <exception cref="InvalidOperationException">If JSON is not loaded.</exception>
    /// <exception cref="ArgumentException">If <paramref name="name"/> is invalid.</exception>
    public void AddOrUpdateStimParam(string name, float value)
    {
        EnsureLoaded();
        ValidateKey(name);
        lock (_sync)
        {
            _config.stimParams[name] = value;
            SaveJson_NoLock();
        }
    }

    /// <summary>
    /// Gets a stimulation parameter by name.
    /// </summary>
    /// <param name="name">Dictionary key.</param>
    /// <returns>Parameter value.</returns>
    /// <exception cref="InvalidOperationException">If JSON is not loaded.</exception>
    /// <exception cref="KeyNotFoundException">If the key does not exist.</exception>
    public float GetStimParam(string name)
    {
        EnsureLoaded();
        lock (_sync)
        {
            return _config.stimParams[name];
        }
    }

    /// <summary>
    /// Attempts to get a stimulation parameter by name.
    /// </summary>
    /// <param name="name">Dictionary key.</param>
    /// <param name="value">Out value if found.</param>
    /// <returns>True if found. Otherwise false.</returns>
    public bool TryGetStimParam(string name, out float value)
    {
        EnsureLoaded();
        lock (_sync)
        {
            return _config.stimParams.TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// Gets a constant by name.
    /// </summary>
    /// <param name="name">Dictionary key.</param>
    /// <returns>Constant value.</returns>
    /// <exception cref="InvalidOperationException">If JSON is not loaded.</exception>
    /// <exception cref="KeyNotFoundException">If the key does not exist.</exception>
    public float GetConstant(string name)
    {
        EnsureLoaded();
        lock (_sync)
        {
            return _config.constants[name];
        }
    }

    /// <summary>
    /// Attempts to get a constant by name.
    /// </summary>
    /// <param name="name">Dictionary key.</param>
    /// <param name="value">Out value if found.</param>
    /// <returns>True if found. Otherwise false.</returns>
    public bool TryGetConstant(string name, out float value)
    {
        EnsureLoaded();
        lock (_sync)
        {
            return _config.constants.TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// Returns a shallow copy of all constants.
    /// </summary>
    public Dictionary<string, float> GetAllConstants()
    {
        EnsureLoaded();
        lock (_sync)
        {
            return new Dictionary<string, float>(_config.constants);
        }
    }

    /// <summary>
    /// Returns a shallow copy of all stimulation parameters.
    /// </summary>
    public Dictionary<string, float> GetAllStimParams()
    {
        EnsureLoaded();
        lock (_sync)
        {
            return new Dictionary<string, float>(_config.stimParams);
        }
    }

    /// <summary>
    /// Deletes a key from constants. Saves if a key was removed.
    /// </summary>
    /// <param name="name">Dictionary key.</param>
    /// <returns>True if a key was removed. Otherwise false.</returns>
    public bool RemoveConstant(string name)
    {
        EnsureLoaded();
        lock (_sync)
        {
            var removed = _config.constants.Remove(name);
            if (removed) SaveJson_NoLock();
            return removed;
        }
    }

    /// <summary>
    /// Deletes a key from stimulation parameters. Saves if a key was removed.
    /// </summary>
    /// <param name="name">Dictionary key.</param>
    /// <returns>True if a key was removed. Otherwise false.</returns>
    public bool RemoveStimParam(string name)
    {
        EnsureLoaded();
        lock (_sync)
        {
            var removed = _config.stimParams.Remove(name);
            if (removed) SaveJson_NoLock();
            return removed;
        }
    }

    /// <summary>
    /// Ensures the configuration is loaded or throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">If not loaded.</exception>
    private void EnsureLoaded()
    {
        if (!IsLoaded) throw new InvalidOperationException("Configuration not loaded. Call LoadJson() first.");
    }

    /// <summary>
    /// Validates a dictionary key.
    /// </summary>
    private static void ValidateKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Key must be non-empty.", nameof(name));
    }

    /// <summary>
    /// Validates core JSON structure.
    /// </summary>
    private static bool VerifyJson(StimConfig cfg)
    {
        return cfg != null && cfg.constants != null && cfg.stimParams != null;
    }

    /// <summary>
    /// Writes the in-memory config to disk. Caller must hold _sync.
    /// </summary>
    private void SaveJson_NoLock()
    {
        var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(_configPath, json);
    }

    /// <summary>
    /// Creates a default configuration with sane keys and values.
    /// </summary>
    private StimConfig GenerateInitialConfig()
    {
        var cfg = new StimConfig
        {
            constants = new Dictionary<string, float>(),
            stimParams = new Dictionary<string, float>()
        };

        // PID-like gains
        cfg.constants["PModeProportional"] = 1.0f;
        cfg.constants["PModeOffset"] = 0.0f;

        cfg.constants["PDModeProportional"] = 0.5f;
        cfg.constants["PDModeDerivative"] = 0.2f;
        cfg.constants["PDModeOffset"] = 0.0f;

        // Channel defaults
        for (int ch = 1; ch <= 9; ch++)
        {
            cfg.stimParams[$"Ch{ch}Max"] = 0f;
            cfg.stimParams[$"Ch{ch}Min"] = 0f;
            cfg.stimParams[$"Ch{ch}Amp"] = 3.0f;
            cfg.stimParams[$"Ch{ch}IPI"] = 10.0f;
        }

        return cfg;
    }

    #region getters and setters for core config values

    /// <summary>
    /// Gets or sets the maximum number of WSS devices.
    /// </summary>
    public int MaxWSS
    {
        get { EnsureLoaded(); lock (_sync) return _config.maxWSS; }
        set { EnsureLoaded(); lock (_sync) { _config.maxWSS = value; SaveJson_NoLock(); } }
    }

    /// <summary>
    /// Gets or sets the firmware version string.
    /// </summary>
    public string WSSFirmwareVersion
    {
        get { EnsureLoaded(); lock (_sync) return _config.WSSFirmwareVersion; }
        set { EnsureLoaded(); lock (_sync) { _config.WSSFirmwareVersion = value; SaveJson_NoLock(); } }
    }

    /// <summary>
    /// Gets or sets the sensation controller mode (e.g. "P").
    /// </summary>
    public string SensationController
    {
        get { EnsureLoaded(); lock (_sync) return _config.sensationController; }
        set { EnsureLoaded(); lock (_sync) { _config.sensationController = value; SaveJson_NoLock(); } }
    }


    #endregion
}
