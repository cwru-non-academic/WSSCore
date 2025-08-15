using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

// - File location is configurable via constructor (defaults to "./stimConfig.json")

public sealed class StimConfigController
{
    public StimConfig _config;
    private readonly string _configPath;
    private bool jsonLoaded = false;

    /// <summary>
    /// Create a controller that reads/writes stimConfig.json in the current directory.
    /// </summary>
    public StimConfigController() : this(Path.Combine(Environment.CurrentDirectory, "stimConfig.json"))
    {
    }

    /// <summary>
    /// Create a controller pointing to a custom JSON path (absolute or relative).
    /// Pass a directory path to place "stimConfig.json" inside that directory.
    /// </summary>
    public StimConfigController(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Invalid config path", nameof(path));

        // If a directory was provided, append the default filename
        if (Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
        {
            _configPath = Path.Combine(path, "stimConfig.json");
        }
        else
        {
            _configPath = path;
        }
    }

    private bool verify_json()
    {
        return _config != null && _config.constants != null && _config.stimParams != null;
    }

    /// <summary>
    /// Load config from disk. If file doesn't exist or is malformed, a new config is created and saved.
    /// </summary>
    public void LoadJSON()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                _config = JsonConvert.DeserializeObject<StimConfig>(File.ReadAllText(_configPath));
                if (!verify_json())
                {
                    // fall back to new default if structure isn't right
                    generateInitialJSON();
                    SaveJSON();
                }
                jsonLoaded = true;
            }
            else
            {
                generateInitialJSON();
                SaveJSON();
                jsonLoaded = true;
            }
        }
        catch (Exception ex)
        {
            // On any error, generate a fresh default so the system can proceed
            Console.Error.WriteLine($"[StimConfigController] JSON loading error: {ex.Message}. Generating defaults.");
            generateInitialJSON();
            SaveJSON();
            jsonLoaded = true;
        }
    }

    private void SaveJSON()
    {
        var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(_configPath, json);
    }

    public void addConstant(string name, float val)
    {
        _config.constants[name] = val;
        SaveJSON();
    }

    public void addStimParam(string name, float val)
    {
        _config.stimParams[name] = val;
        SaveJSON();
    }

    public float getStimParam(string name) => _config.stimParams[name];
    public float getConstant(string name) => _config.constants[name];
    public void modifyStimParam(string name, float value)
    {
        _config.stimParams[name] = value;
        SaveJSON();
    }

    private void generateInitialJSON()
    {
        _config = new StimConfig();
        if (_config.constants == null) _config.constants = new Dictionary<string, float>();
        if (_config.stimParams == null) _config.stimParams = new Dictionary<string, float>();

        addConstant("PModeProportional", 1.0f);
        addConstant("PModeOffsset", 0.0f);

        addConstant("PDModeProportional", 0.5f);
        addConstant("PDModeDerivative", 0.2f);
        addConstant("PDModeOffsset", 0.0f);

        addStimParam("Ch1Max", 0);
        addStimParam("Ch1Min", 0);
        addStimParam("Ch1Amp", 3.0f);

        addStimParam("Ch2Max", 0);
        addStimParam("Ch2Min", 0);
        addStimParam("Ch2Amp", 3.0f);

        addStimParam("Ch3Max", 0);
        addStimParam("Ch3Min", 0);
        addStimParam("Ch3Amp", 3.0f);

        addStimParam("Ch4Max", 0);
        addStimParam("Ch4Min", 0);
        addStimParam("Ch4Amp", 3.0f);

        addStimParam("Ch5Max", 0);
        addStimParam("Ch5Min", 0);
        addStimParam("Ch5Amp", 3.0f);

        addStimParam("Ch6Max", 0);
        addStimParam("Ch6Min", 0);
        addStimParam("Ch6Amp", 3.0f);

        addStimParam("Ch7Max", 0);
        addStimParam("Ch7Min", 0);
        addStimParam("Ch7Amp", 3.0f);

        addStimParam("Ch8Max", 0);
        addStimParam("Ch8Min", 0);
        addStimParam("Ch8Amp", 3.0f);

        addStimParam("Ch9Max", 0);
        addStimParam("Ch9Min", 0);
        addStimParam("Ch9Amp", 3.0f);
    }
}
