using System;
using UnityEngine;

/// <summary>
/// Basic Unity MonoBehaviour wrapper for the WSS stimulation core.
/// Handles initialization, configuration, and waveform control for connected WSS stimulators.
/// Designed as the simplest interface layer exposing low-level stimulation functionality.
/// </summary>
public class Stimulationbasic : MonoBehaviour
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

    private IStimulationCore WSS;
    private IBasicStimulation basicWSS;
    public bool started = false;

    #region ==== Unity Lifecycle ====

    /// <summary>
    /// Creates and configures the stimulation core controller.
    /// Uses either a forced COM port or default auto-detected connection.
    /// </summary>
    public void Awake()
    {
        if (forcePort)
            WSS = new WssStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries);
        else
            WSS = new WssStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);

        basicWSS = (IBasicStimulation)WSS;
    }

    /// <summary>
    /// Initializes the WSS device connection when the component becomes active.
    /// </summary>
    void OnEnable() => WSS.Initialize();

    /// <summary>
    /// Performs periodic updates and tick cycles for communication and task execution.
    /// Press <c>A</c> key to manually reload the configuration file for testing.
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

    #region ==== Stimulation Methods ====

    /// <summary>
    /// Sends an analog stimulation command to a mapped channel.
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
    public void Save(int targetWSS) => basicWSS.Save(IntToWssTarget(targetWSS));

    /// <summary>Saves current settings to all WSS units.</summary>
    public void Save() => basicWSS.Save(WssTarget.Broadcast);

    /// <summary>Loads saved configuration on a specific WSS unit.</summary>
    public void load(int targetWSS) => basicWSS.Load(IntToWssTarget(targetWSS));

    /// <summary>Loads saved configuration on all WSS units.</summary>
    public void load() => basicWSS.Load(WssTarget.Broadcast);

    /// <summary>
    /// Requests configuration data from a specific WSS unit.
    /// </summary>
    public void request_Configs(int targetWSS, int command, int id)
        => basicWSS.Request_Configs(command, id, IntToWssTarget(targetWSS));

    /// <summary>
    /// Updates the waveform parameters for a specific event across all units.
    /// </summary>
    public void updateWaveform(int[] waveform, int eventID)
        => basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);

    public void updateWaveform(int targetWSS, int[] waveform, int eventID)
        => basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));

    /// <summary>
    /// Selects a predefined or custom waveform shape from device memory.
    /// </summary>
    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID)
        => basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, WssTarget.Broadcast);

    public void updateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID)
        => basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, IntToWssTarget(targetWSS));

    /// <summary>
    /// Updates waveform using JSON-loaded builder definition.
    /// </summary>
    public void updateWaveform(WaveformBuilder waveform, int eventID)
        => basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);

    public void updateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
        => basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));

    /// <summary>Loads waveform from external file.</summary>
    public void loadWaveform(string fileName, int eventID)
        => basicWSS.LoadWaveform(fileName, eventID);

    /// <summary>Defines a custom waveform for the specified event slot.</summary>
    public void WaveformSetup(WaveformBuilder wave, int eventID)
        => basicWSS.WaveformSetup(wave, eventID, WssTarget.Broadcast);

    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)
        => basicWSS.WaveformSetup(wave, eventID, IntToWssTarget(targetWSS));

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
