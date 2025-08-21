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
    void StreamChange(int[] PA, int[] PW, int[] IPI, WssTarget wssTarget);
    void StimulateAnalog(string finger, bool rawValues, int PW, int amp = 3);
    void ZeroOutStim(WssTarget wssTarget);
    void StartStim(WssTarget wssTarget);
    void StopStim(WssTarget wssTarget);
    void StimWithMode(string finger, float magnitude);
    void UpdateChannelParams(string finger, int max, int min, int amp);
    void UpdateIPD(int IPD, WssTarget wssTarget);
    void UpdateFrequency(int FR, WssTarget wssTarget);
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
}

