using System;

public interface IStimulationCore : IDisposable
{
    // lifecycle
    void Initialize();
    void Tick();
    void Shutdown();

    // status
    bool Started();
    bool Ready();

    // streaming / control
    void StimulateAnalog(int channel, int PW, float amp, int IPI);
    void ZeroOutStim(WssTarget wssTarget);
    void StartStim(WssTarget wssTarget);
    void StopStim(WssTarget wssTarget);
    void LoadConfigFile();
    CoreConfigController GetCoreConfigController();
}

