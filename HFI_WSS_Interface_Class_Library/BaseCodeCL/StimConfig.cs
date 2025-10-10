using System.Collections.Generic;

[System.Serializable]

public class StimConfig
{
    public int maxWSS = 1;
    public string sensationController = "P";
    public Dictionary<string, float> constants = new Dictionary<string, float>();
    public Dictionary<string, float> stimParams = new Dictionary<string, float>();

    public StimConfig(int maxWSS, string sensationController, Dictionary<string, float> constants, Dictionary<string, float> stimParams)
    {
        this.maxWSS = maxWSS;
        this.sensationController = sensationController;
        this.constants = constants;
        this.stimParams = stimParams;
    }

    public StimConfig() 
    {
        this.constants = new Dictionary<string, float>();
        this.stimParams = new Dictionary<string, float>();
    }

    public StimConfig(int maxWSS, string sensationController)
    {
        this.maxWSS = maxWSS;
        this.sensationController = sensationController;
        this.constants = new Dictionary<string, float>();
        this.stimParams = new Dictionary<string, float>();
    }
}
