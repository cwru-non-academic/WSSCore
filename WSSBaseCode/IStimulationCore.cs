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
    void Stream_change(int targetWSS, int[] PA, int[] PW, int[] IPI);
    void StimulateAnalog(string finger, bool rawValues, int PW, int amp = 3);
    void Zero_out_stim();
    void StartStim(int targetWSS = 0);
    void StopStim(int targetWSS = 0);
    void StimWithMode(string finger, float magnitude);
    void UpdateChannelParams(string finger, int max, int min, int amp);
    void UpdateIPD(int targetWSS, int IPD);
    void UpdateIPD(int IPD);
    void UpdateFrequency(int targetWSS, int FR);
    void UpdateFrequency(int FR);
    void UpdateWaveform(int[] waveform, int eventID);
    void UpdateWaveform(int targetWSS, int[] waveform, int eventID);
    void UpdateWaveform(int cathodicWaveform, int anodicWaveform, int eventID);
    void UpdateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID);
    void UpdateWaveform(WaveformBuilder waveform, int eventID);
    void UpdateWaveform(int targetWSS, WaveformBuilder waveform, int eventID);
    void LoadWaveform(string fileName, int eventID);
    void WaveformSetup(WaveformBuilder wave, int eventID);
    void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID);


    // setup & edits 
    void Save(int targetWSS);
    void Load(int targetWSS);
    void Load();
    void Request_Configs(int targetWSS, int command, int id);
    void LoadConfigFile();
}

