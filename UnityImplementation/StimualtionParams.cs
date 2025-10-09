using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// Unity wrapper that composes a params layer (<c>StimParamsLayer</c>) over a core WSS implementation.
/// Extends the basic interface with per-finger calibration parameters, normalized drive, and JSON persistence.
/// All public stimulation APIs accept a finger string and map to a channel via <see cref="FingerToChannel"/>.
/// </summary>
public class StimulationParams : MonoBehaviour
{
    #region ==== Serialized Fields ====

    /// <summary>
    /// Forces connection to a specific COM port instead of auto-detecting.
    /// </summary>
    [SerializeField] public bool forcePort = false;

    /// <summary>
    /// Enables simulated test mode without real hardware communication.
    /// </summary>
    [SerializeField] private bool testMode = true;

    /// <summary>
    /// Maximum number of setup retries before failing initialization.
    /// </summary>
    [SerializeField] private int maxSetupTries = 5;

    /// <summary>
    /// Target COM port to use when <see cref="forcePort"/> is enabled.
    /// </summary>
    [SerializeField] public string comPort = "COM7";

    #endregion

    private IStimParamsCore WSS;
    private IBasicStimulation basicWSS;
    /// <summary>True after <see cref="StartStimulation"/> succeeds.</summary>
    public bool started = false;
    /// <summary>True if the underlying core exposes basic-stimulation APIs.</summary>
    private bool basicSupported = false;

    #region ==== Unity Lifecycle ====

    /// <summary>
    /// Builds the core WSS and wraps it with <c>StimParamsLayer</c>.
    /// Tries to expose <see cref="IBasicStimulation"/> if available.
    /// </summary>
    public void Awake()
    {
        IStimulationCore WSScore =
            forcePort
            ? new WssStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries)
            : new WssStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);

        WSS = new StimParamsLayer(WSScore, Application.streamingAssetsPath);
        WSS.TryGetBasic(out basicWSS);
        basicSupported = (basicWSS != null);
    }

    /// <summary>
    /// Initializes the WSS device connection when the component becomes active.
    /// </summary>
    void OnEnable() => WSS.Initialize();

    /// <summary>
    /// Performs periodic updates and tick cycles for communication and task execution.
    /// Press <c>A</c> to manually reload the core configuration file for testing.
    /// </summary>
    void Update()
    {
        WSS.Tick();
        if (Input.GetKeyDown(KeyCode.A))
            WSS.LoadConfigFile();
    }

    /// <summary>
    /// Ensures proper device shutdown when the component is disabled.
    /// </summary>
    void OnDisable() => WSS.Shutdown();

    #endregion

    #region ==== Connection Management ====

    /// <summary>
    /// Explicitly releases the radio connection.
    /// </summary>
    public void releaseRadio() => WSS.Shutdown();

    /// <summary>
    /// Performs a radio reset by shutting down and re-initializing the connection.
    /// </summary>
    public void resetRadio()
    {
        WSS.Shutdown();
        WSS.Initialize();
    }

    #endregion

    #region ==== Stimulation methods: basic and core ====

    /// <summary>
    /// Direct analog stimulation using raw parameters.
    /// Prefer <see cref="StimulateNormalized(string,float)"/> for analog sensors.
    /// </summary>
    /// <param name="finger">Finger name or channel alias (e.g., "index" or "ch2").</param>
    /// <param name="PW">Pulse width in microseconds.</param>
    /// <param name="amp">Amplitude in milliamps (default = 3).</param>
    /// <param name="IPI">Inter-pulse interval in milliseconds (default = 10).</param>
    public void StimulateAnalog(string finger, int PW, int amp = 3, int IPI = 10)
    {
        int channel = FingerToChannel(finger);
        WSS.StimulateAnalog(channel, PW, amp, IPI);
    }

    /// <summary>Broadcasts start stimulation command to all connected WSS units.</summary>
    public void StartStimulation() => WSS.StartStim(WssTarget.Broadcast);

    /// <summary>Broadcasts stop stimulation command to all connected WSS units.</summary>
    public void StopStimulation() => WSS.StopStim(WssTarget.Broadcast);

    /// <summary>Saves current settings to a specific WSS unit.</summary>
    /// <param name="targetWSS">0=broadcast, 1..3=unit index.</param>
    public void Save(int targetWSS)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.Save(IntToWssTarget(targetWSS));
    }

    /// <summary>Saves current settings to all WSS units.</summary>
    public void Save()
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.Save(WssTarget.Broadcast);
    }

    /// <summary>Loads saved configuration on a specific WSS unit.</summary>
    public void load(int targetWSS)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.Load(IntToWssTarget(targetWSS));
    }

    /// <summary>Loads saved configuration on all WSS units.</summary>
    public void load()
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.Load(WssTarget.Broadcast);
    }

    /// <summary>
    /// Requests configuration data from a specific WSS unit.
    /// </summary>
    /// <param name="targetWSS">0=broadcast, 1..3=unit index.</param>
    /// <param name="command">Configuration block selector.</param>
    /// <param name="id">Optional block id.</param>
    public void request_Configs(int targetWSS, int command, int id)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.Request_Configs(command, id, IntToWssTarget(targetWSS));
    }

    /// <summary>
    /// Updates the waveform parameters for a specific event across all units.
    /// </summary>
    /// <param name="waveform">Serialized device waveform.</param>
    /// <param name="eventID">Target event slot.</param>
    public void updateWaveform(int[] waveform, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    /// <summary>
    /// Updates the waveform parameters for a specific event on a target unit.
    /// </summary>
    public void updateWaveform(int targetWSS, int[] waveform, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    /// <summary>
    /// Selects a predefined or custom waveform shape from device memory.
    /// Slots 0–10 are predefined. Slots 11–13 are custom.
    /// </summary>
    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, WssTarget.Broadcast);
    }

    /// <summary>
    /// Shape selection on a target unit.
    /// </summary>
    public void updateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, IntToWssTarget(targetWSS));
    }

    /// <summary>
    /// Updates waveform using JSON-loaded builder definition for all units.
    /// </summary>
    public void updateWaveform(WaveformBuilder waveform, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    /// <summary>
    /// Updates waveform using JSON-loaded builder definition for a target unit.
    /// </summary>
    public void updateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    /// <summary>Loads a waveform definition from external file.</summary>
    public void loadWaveform(string fileName, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.LoadWaveform(fileName, eventID);
    }

    /// <summary>Defines a custom waveform for the specified event slot on all units.</summary>
    public void WaveformSetup(WaveformBuilder wave, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.WaveformSetup(wave, eventID, WssTarget.Broadcast);
    }

    /// <summary>Defines a custom waveform for the specified event slot on a target unit.</summary>
    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)
    {
        if (!basicSupported) { Log.Error("Basic stimulation not supported on this Stimulator."); return; }
        basicWSS.WaveformSetup(wave, eventID, IntToWssTarget(targetWSS));
    }

    #endregion

    #region ==== Stimulation methods: params layer (finger APIs) ====

    /// <summary>
    /// Normalized drive [0..1]. Computes PW via per-finger min/max and stimulates.
    /// </summary>
    /// <param name="finger">Finger label or alias (e.g., "index", "ring", "ch2").</param>
    /// <param name="value01">Normalized drive in [0,1]. Values are clamped.</param>
    public void StimulateNormalized(string finger, float value01)
    {
        int ch = FingerToChannel(finger);
        WSS.StimulateNormalized(ch, value01);
    }

    /// <summary>
    /// Returns the last computed intensity for a finger. For PW-driven systems this is PW (µs).
    /// </summary>
    /// <param name="finger">Finger label or alias.</param>
    public int GetLastPulseWidth(string finger)
    {
        int ch = FingerToChannel(finger);
        return (int)WSS.GetStimIntensity(ch);
    }

    /// <summary>Sets per-finger amplitude in mA.</summary>
    public void SetChannelAmp(string finger, float mA)
    {
        int ch = FingerToChannel(finger);
        WSS.SetChannelAmp(ch, mA);
    }

    /// <summary>Sets per-finger minimum PW in µs.</summary>
    public void SetChannelPWMin(string finger, int us)
    {
        int ch = FingerToChannel(finger);
        WSS.SetChannelPWMin(ch, us);
    }

    /// <summary>Sets per-finger maximum PW in µs.</summary>
    public void SetChannelPWMax(string finger, int us)
    {
        int ch = FingerToChannel(finger);
        WSS.SetChannelPWMax(ch, us);
    }

    /// <summary>Sets per-finger IPI in ms.</summary>
    public void SetChannelIPI(string finger, int ms)
    {
        int ch = FingerToChannel(finger);
        WSS.SetChannelIPI(ch, ms);
    }

    /// <summary>Gets per-finger amplitude in mA.</summary>
    public float GetChannelAmp(string finger)
    {
        int ch = FingerToChannel(finger);
        return WSS.GetChannelAmp(ch);
    }

    /// <summary>Gets per-finger minimum PW in µs.</summary>
    public int GetChannelPWMin(string finger)
    {
        int ch = FingerToChannel(finger);
        return WSS.GetChannelPWMin(ch);
    }

    /// <summary>Gets per-finger maximum PW in µs.</summary>
    public int GetChannelPWMax(string finger)
    {
        int ch = FingerToChannel(finger);
        return WSS.GetChannelPWMax(ch);
    }

    /// <summary>Gets per-finger IPI in ms.</summary>
    public int GetChannelIPI(string finger)
    {
        int ch = FingerToChannel(finger);
        return WSS.GetChannelIPI(ch);
    }

    /// <summary>Returns true if the finger maps to a valid channel for the current configuration.</summary>
    public bool IsFingerValid(string finger)
    {
        int ch = FingerToChannel(finger);
        return WSS.IsChannelInRange(ch);
    }

    #endregion

    #region ==== Params JSON and calibration ====

    /// <summary>Saves current params JSON to disk.</summary>
    public void SaveParamsJson() => WSS.SaveParamsJson();

    /// <summary>Loads params JSON from default location.</summary>
    public void LoadParamsJson() => WSS.LoadParamsJson();

    /// <summary>Loads params JSON from a given file or directory.</summary>
    /// <param name="pathOrDir">File path or directory with default name.</param>
    public void LoadParamsJson(string pathOrDir) => WSS.LoadParamsJson(pathOrDir);

    /// <summary>
    /// Sets a parameter by dotted key. Examples: "stim.ch.0.amp", "stim.ch.1.minPW".
    /// </summary>
    public void AddOrUpdateStimParam(string key, float value) => WSS.AddOrUpdateStimParam(key, value);

    /// <summary>Gets a parameter by dotted key.</summary>
    public float GetStimParam(string key) => WSS.GetStimParam(key);

    /// <summary>Tries to read a parameter by dotted key.</summary>
    public bool TryGetStimParam(string key, out float v) => WSS.TryGetStimParam(key, out v);

    /// <summary>Returns a copy of all current params as dotted-key map.</summary>
    public Dictionary<string, float> GetAllStimParams() => WSS.GetAllStimParams();

    /// <summary>
    /// Updates per-finger calibration params.
    /// </summary>
    /// <param name="finger">e.g., "index", "ring", or "ch2".</param>
    /// <param name="max">Max PW (µs).</param>
    /// <param name="min">Min PW (µs).</param>
    /// <param name="amp">Amplitude (mA).</param>
    /// <exception cref="ArgumentOutOfRangeException">If finger maps to an invalid channel.</exception>
    public void UpdateChannelParams(string finger, int max, int min, int amp)
    {
        int ch = FingerToChannel(finger);
        if (!WSS.IsChannelInRange(ch))
            throw new ArgumentOutOfRangeException(nameof(finger), $"Channel {ch} is not valid for current config.");

        string baseKey = $"stim.ch.{ch}";
        WSS.AddOrUpdateStimParam($"{baseKey}.maxPW", max);
        WSS.AddOrUpdateStimParam($"{baseKey}.minPW", min);
        WSS.AddOrUpdateStimParam($"{baseKey}.amp", amp);
    }

    #endregion

    #region ==== Getters ====

    /// <summary>Returns <c>true</c> if the device has completed setup and is ready.</summary>
    public bool Ready() => WSS.Ready();

    /// <summary>Returns <c>true</c> if stimulation is currently active.</summary>
    public bool Started() => WSS.Started();

    /// <summary>Provides access to the core configuration controller.</summary>
    public CoreConfigController GetCoreConfigCTRL() => WSS.GetCoreConfigController();

    #endregion

    #region ==== Utility ====

    private WssTarget IntToWssTarget(int i) =>
        i switch
        {
            0 => WssTarget.Broadcast,
            1 => WssTarget.Wss1,
            2 => WssTarget.Wss2,
            3 => WssTarget.Wss3,
            _ => WssTarget.Wss1
        };

    /// <summary>
    /// Maps a finger label or alias to a device channel.
    /// Supports "thumb/index/middle/ring/pinky" and "chN" aliases.
    /// Returns 0 on invalid or empty input.
    /// </summary>
    private int FingerToChannel(string fingerOrAlias)
    {
        if (string.IsNullOrWhiteSpace(fingerOrAlias)) return 0;

        if (fingerOrAlias.StartsWith("ch", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(fingerOrAlias.AsSpan(2), out var n))
            return n;

        return fingerOrAlias.ToLowerInvariant() switch
        {
            "thumb" => 1,
            "index" => 2,
            "middle" => 3,
            "ring" => 4,
            "pinky" or "little" => 5,
            _ => 0
        };
    }

    #endregion
}
