using UnityEngine;
using System.Collections.Generic;
using System;

public class StimulationParams : MonoBehaviour
{
    [SerializeField] public bool forcePort = false;
    [SerializeField] private bool testMode = true;
    [SerializeField] private int maxSetupTries = 5;
    [SerializeField] public string comPort = "COM7";

    private IStimParamsCore WSS;
    private IBasicStimulation basicWSS;
    public bool started = false;
    private bool basicSupported = false;
    // Start is called before the first frame update
    public void Awake()
    {
        IStimulationCore WSScore;
        if (forcePort)
        {
            //WSS = new LegacyStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries);
            WSScore = new WssStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries);
        }
        else
        {
            //WSS = new LegacyStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);
            WSScore = new WssStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);
        }
        WSS = new StimParamsLayer(WSScore, Application.streamingAssetsPath);
        WSS.TryGetBasic(out basicWSS);
        basicSupported = (basicWSS != null);
    }


    void OnEnable()
    {
        WSS.Initialize();
    }

    // Update is called once per frame
    void Update()
    {
        WSS.Tick();
        //manual load config file trigger for testing
        if (Input.GetKeyDown(KeyCode.A))
        {
            WSS.LoadConfigFile();
        }
    }

    void OnDestroy()
    {

    }

    void OnDisable()
    {
        WSS.Shutdown();
    }

    public void releaseRadio()
    {
        WSS.Shutdown();
    }

    public void resetRadio()
    {
        WSS.Shutdown();
        WSS.Initialize();
    }

    #region "Stimulation methods basic and core"
    //current functions are design to be called by a discrete sensor  
    // like the bubble. For an analog sensor I suggets you only use the 
    //function below and disregard the rest
    public void StimulateAnalog(string finger, int PW, int amp = 3, int IPI = 10)
    {
        int channel = FingerToChannel(finger);
        WSS.StimulateAnalog(channel, PW, amp, IPI);
    }


    public void StartStimulation()
    {
        WSS.StartStim(WssTarget.Broadcast);
    }

    public void StopStimulation()
    {
        WSS.StopStim(WssTarget.Broadcast);
    }

    public void Save(int targetWSS)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.Save(IntToWssTarget(targetWSS));
    }

    public void Save()
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.Save(WssTarget.Broadcast);
    }

    public void load(int targetWSS)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.Load(IntToWssTarget(targetWSS));
    }

    public void load()
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.Load(WssTarget.Broadcast);
    }


    public void request_Configs(int targetWSS, int command, int id)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.Request_Configs(command, id, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(int[] waveform, int eventID)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, int[] waveform, int eventID)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, IntToWssTarget(targetWSS));
    }

    //overload for loading from json functionality
    public void updateWaveform(WaveformBuilder waveform, int eventID)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    public void loadWaveform(string fileName, int eventID)
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.LoadWaveform(fileName, eventID);
    }


    public void WaveformSetup(WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.WaveformSetup(wave, eventID, WssTarget.Broadcast);
    }

    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        if (!basicSupported)
        {
            Log.Error("Basic stimulation not supported on this Stimulator.");
            return;
        }
        basicWSS.WaveformSetup(wave, eventID, IntToWssTarget(targetWSS));
    }
    #endregion

    #region "Stimulation methods params"
    // Normalized drive [0..1] → computes PW from minPW..maxPW and stimulates
    public void StimulateNormalized(int channel, float value01)
    {
        WSS.StimulateNormalized(channel, value01);
    }

    // Read last computed PW for a channel
    public int GetLastPulseWidth(int channel)
    {
        return (int)WSS.GetStimIntensity(channel);
    }

    public void UpdateChannelParams(string finger, int max, int min, int amp)
    {
        int ch = FingerToChannel(finger);
        if (!WSS.IsChannelInRange(ch))
            throw new ArgumentOutOfRangeException(nameof(finger), $"Channel {ch} is not valid for current config.");

        // build dotted keys and update values
        string baseKey = $"stim.ch.{ch}";
        WSS.AddOrUpdateStimParam($"{baseKey}.maxPW", max);
        WSS.AddOrUpdateStimParam($"{baseKey}.minPW", min);
        WSS.AddOrUpdateStimParam($"{baseKey}.amp", amp);
    }

    // Persist/Load the params JSON
    public void SaveParamsJson() => WSS.SaveParamsJson();
    public void LoadParamsJson() => WSS.LoadParamsJson();
    public void LoadParamsJson(string pathOrDir) => WSS.LoadParamsJson(pathOrDir);

    // Dotted-key access: "stim.ch.{N}.amp|minPW|maxPW|IPI"
    public void AddOrUpdateStimParam(string key, float value) => WSS.AddOrUpdateStimParam(key, value);
    public float GetStimParam(string key) => WSS.GetStimParam(key);
    public bool TryGetStimParam(string key, out float v) => WSS.TryGetStimParam(key, out v);
    public Dictionary<string, float> GetAllStimParams() => WSS.GetAllStimParams();

    // Channel helpers
    public void SetChannelAmp(int ch, float mA) => WSS.SetChannelAmp(ch, mA);
    public void SetChannelPWMin(int ch, int us) => WSS.SetChannelPWMin(ch, us);
    public void SetChannelPWMax(int ch, int us) => WSS.SetChannelPWMax(ch, us);
    public void SetChannelIPI(int ch, int ms) => WSS.SetChannelIPI(ch, ms);

    public float GetChannelAmp(int ch) => WSS.GetChannelAmp(ch);
    public int GetChannelPWMin(int ch) => WSS.GetChannelPWMin(ch);
    public int GetChannelPWMax(int ch) => WSS.GetChannelPWMax(ch);
    public int GetChannelIPI(int ch) => WSS.GetChannelIPI(ch);

    public bool IsChannelInRange(int ch) => WSS.IsChannelInRange(ch);


    #endregion

    #region getSets
    public bool Ready()
    {
        return WSS.Ready();
    }

    public bool Started()
    {
        return WSS.Started();
    }

    public CoreConfigController GetCoreConfigCTRL()
    {
        return WSS.GetCoreConfigController();
    }
    #endregion

    private WssTarget IntToWssTarget(int i)
    {
        switch (i)
        {
            case 0: return WssTarget.Broadcast;
            case 1: return WssTarget.Wss1;
            case 2: return WssTarget.Wss2;
            case 3: return WssTarget.Wss3;
            default: return WssTarget.Wss1;
        }
    }
    
    private int FingerToChannel(string fingerOrAlias)
    {
        if (string.IsNullOrWhiteSpace(fingerOrAlias)) return 0;
        // chN alias
        if (fingerOrAlias.StartsWith("ch", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(fingerOrAlias.AsSpan(2), out var n)) return n;

        // simple name map (adjust if your mapping differs)
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
}
