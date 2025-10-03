using System;
using System.Collections.Generic;

public interface IStimulationCore : IDisposable
{
    // lifecycle
    void Initialize();
    void Tick();
    void Shutdown();

    // status
    bool Started();
    bool Ready();
    bool IsModeValid();

    // streaming / control
    void StimulateAnalog(string finger, int PW, float amp, int IPI);
    void ZeroOutStim(WssTarget wssTarget);
    void StartStim(WssTarget wssTarget);
    void StopStim(WssTarget wssTarget);
    void StimWithMode(string finger, float magnitude);
    void UpdateChannelParams(string finger, int max, int min, float amp);
    void UpdateIPD(int IPD, WssTarget wssTarget);
    void UpdateFrequency(string finger, int FR);
    void UpdatePeriod(string finger, int periodMs);
    void UpdateWaveform(int[] waveform, int eventID, WssTarget wssTarget);
    void UpdateEventShape(int cathodicWaveform, int anodicWaveform, int eventID, WssTarget wssTarget);
    void UpdateWaveform(WaveformBuilder waveform, int eventID, WssTarget wssTarget);
    void LoadWaveform(string fileName, int eventID);
    void WaveformSetup(WaveformBuilder wave, int eventID, WssTarget wssTarget);

    // setup & edits 
    void Save(WssTarget wssTarget);
    void Load(WssTarget wssTarget);
    void Request_Configs(int command, int id, WssTarget wssTarget);
    void LoadConfigFile();
    StimConfigController GetStimConfigController();
}

