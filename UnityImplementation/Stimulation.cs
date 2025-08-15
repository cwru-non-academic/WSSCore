using System.Collections;
using UnityEngine;
using System;
using System.Diagnostics;
using TMPro;

public class Stimulation : MonoBehaviour
{
    [SerializeField] public bool forcePort = false;
    [SerializeField] private bool testMode = true;
    [SerializeField] private int maxSetupTries = 5;
    [SerializeField] public string comPort = "COM7";
    [SerializeField] private StimConfigController config;

    private IStimulationCore WSS;
    public bool started = false;
    // Start is called before the first frame update
    public void Start()
    {
        if (forcePort)
        {
            WSS = new LegacyStimulationCore(comPort, Application.streamingAssetsPath, testMode, maxSetupTries);
        } else
        {
            WSS = new LegacyStimulationCore(Application.streamingAssetsPath, testMode, maxSetupTries);
        }
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
    public void StimulateAnalog(string finger, bool rawValues, int PW, int amp = 3)
    {
        WSS.StimulateAnalog(finger, rawValues, PW, amp);
    }


    public void StartStimulation()
    {
        WSS.StartStim();
    }

    public void StopStimulation()
    {
        WSS.StopStim();
    }

    public void StimWithMode(string finger, float magnitude)
    {
        WSS.StimWithMode(finger, magnitude);
    }

    public void UpdateChannelParams(string finger, int max, int min, int amp)
    {
        WSS.UpdateChannelParams(finger, max, min, amp);
    }

    public void Save(int targetWSS)
    {
        WSS.Save(targetWSS);
    }

    public void Save()
    {
        WSS.Save(0);
    }

    public void load(int targetWSS)
    {
        WSS.Load(targetWSS);
    }

    public void load()
    {
        WSS.Load();
    }


    public void request_Configs(int targetWSS, int command, int id)
    {
        WSS.Request_Configs(targetWSS, command, id);
    }

    public void UpdateIPD(int targetWSS, int IPD) // in us (0 to 1000us)
    {
        WSS.UpdateIPD(targetWSS, IPD);
    }

    public void UpdateIPD(int IPD) // in us (0 to 1000us)
    {
        WSS.UpdateIPD(IPD);
    }

    public void UpdateFrequency(int targetWSS, int FR) //in Hz (1-1000Hz) might be further limited by PW duration
    {
        WSS.UpdateFrequency(targetWSS, FR);
    } //max 1000ms for pw IPD

    public void UpdateFrequency(int FR) //in Hz (1-1000Hz) might be further limited by PW duration
    {
        WSS.UpdateFrequency(FR);
    } //max 1000ms for pw IPD

    public void updateWaveform(int[] waveform, int eventID) 
    {
       WSS.UpdateWaveform(waveform, eventID);
    }

    public void updateWaveform(int targetWSS, int[] waveform, int eventID)
    {
        WSS.UpdateWaveform(targetWSS, waveform, eventID);
    }

    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        WSS.UpdateWaveform(cathodicWaveform, anodicWaveform, eventID);
    }

    public void updateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        WSS.UpdateWaveform(targetWSS, cathodicWaveform, anodicWaveform, eventID);
    }

    //overload for loading from json functionality
    public void updateWaveform(WaveformBuilder waveform, int eventID) 
    {
        WSS.UpdateWaveform(waveform, eventID);
    }

    public void updateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
    {
        WSS.UpdateWaveform(targetWSS, waveform, eventID);
    }

    public void loadWaveform(string fileName, int eventID)
    {
        WSS.LoadWaveform(fileName, eventID);
    }


    public void WaveformSetup(WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        WSS.WaveformSetup(wave, eventID);
    }

    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        WSS.WaveformSetup(targetWSS, wave, eventID);
    }
    #endregion

    #region getSets
    public bool Ready()
    {
        return WSS.Ready();
    }

    public bool isModeValid()
    {
        return WSS.IsModeValid();
    }
    #endregion
}
