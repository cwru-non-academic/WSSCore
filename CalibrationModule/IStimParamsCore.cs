// IStimParamsCore.cs
using System.Collections.Generic;

/// <summary>
/// Core + params surface. BASIC is optional and exposed via TryGetBasic.
/// </summary>
public interface IStimParamsCore : IStimulationCore
{
    //methods to do simple stimualtion based ona normalized input value
    void StimulateNormalized(int channel, float normalizedValue);
    float GetStimIntensity(int channel);

    // Params persistence (delegates to StimParamsConfig)
    void SaveParamsJson();
    void LoadParamsJson();
    void LoadParamsJson(string path);

    // Dotted-key API ("stim.ch.{N}.{leaf}")
    void AddOrUpdateStimParam(string key, float value);
    float GetStimParam(string key);
    bool TryGetStimParam(string key, out float value);
    Dictionary<string, float> GetAllStimParams();
    StimParamsConfigController GetStimParamsConfigController();

    // Channel helpers
    void SetChannelAmp(int ch, float mA);
    void SetChannelPWMin(int ch, int us);
    void SetChannelPWMax(int ch, int us);
    void SetChannelIPI(int ch, int ms);
    float GetChannelAmp(int ch);
    int   GetChannelPWMin(int ch);
    int   GetChannelPWMax(int ch);
    int   GetChannelIPI(int ch);
    bool  IsChannelInRange(int ch);

    /// <summary>
    /// Optional BASIC capability. Returns true and an instance if available.
    /// </summary>
    bool TryGetBasic(out IBasicStimulation basic);
}
