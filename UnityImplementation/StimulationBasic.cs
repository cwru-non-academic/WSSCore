using System;
using UnityEngine;

public class Stimulationbasic : MonoBehaviour
{
    [SerializeField] public bool forcePort = false;
    [SerializeField] private bool testMode = true;
    [SerializeField] private int maxSetupTries = 5;
    [SerializeField] public string comPort = "COM7";

    private IStimulationCore WSS;
    private IBasicStimulation basicWSS;
    public bool started = false;
    // Start is called before the first frame update
    public void Awake()
    {
        if (forcePort)
        {
            //WSS = new LegacyStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries);
            WSS = new WssStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries);
        }
        else
        {
            //WSS = new LegacyStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);
            WSS = new WssStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);
        }
        basicWSS = (IBasicStimulation)WSS;
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

    #region "Stimulation methods"
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
        basicWSS.Save(IntToWssTarget(targetWSS));
    }

    public void Save()
    {
        basicWSS.Save(WssTarget.Broadcast);
    }

    public void load(int targetWSS)
    {
        basicWSS.Load(IntToWssTarget(targetWSS));
    }

    public void load()
    {
        basicWSS.Load(WssTarget.Broadcast);
    }


    public void request_Configs(int targetWSS, int command, int id)
    {
        basicWSS.Request_Configs(command, id, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(int[] waveform, int eventID)
    {
        basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, int[] waveform, int eventID)
    {
        basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        basicWSS.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, IntToWssTarget(targetWSS));
    }

    //overload for loading from json functionality
    public void updateWaveform(WaveformBuilder waveform, int eventID)
    {
        basicWSS.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
    {
        basicWSS.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    public void loadWaveform(string fileName, int eventID)
    {
        basicWSS.LoadWaveform(fileName, eventID);
    }


    public void WaveformSetup(WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        basicWSS.WaveformSetup(wave, eventID, WssTarget.Broadcast);
    }

    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        basicWSS.WaveformSetup(wave, eventID, IntToWssTarget(targetWSS));
    }
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
