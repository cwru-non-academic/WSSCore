using System;

public interface IBasicStimulation : IDisposable
{
    void UpdateWaveform(int[] waveform, int eventID, WssTarget wssTarget);
    void UpdateEventShape(int cathodicWaveform, int anodicWaveform, int eventID, WssTarget wssTarget);
    void UpdateWaveform(WaveformBuilder waveform, int eventID, WssTarget wssTarget);
    void LoadWaveform(string fileName, int eventID);
    void WaveformSetup(WaveformBuilder wave, int eventID, WssTarget wssTarget);

    // setup & edits 
    void Save(WssTarget wssTarget);
    void Load(WssTarget wssTarget);
    void Request_Configs(int command, int id, WssTarget wssTarget);
}


