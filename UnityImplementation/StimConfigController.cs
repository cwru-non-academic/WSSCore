using UnityEngine;
using Newtonsoft.Json;

public class StimConfigController : MonoBehaviour
{
    public StimConfig _config;
    private bool jsonLoaded = false;

    // Start is called before the first frame update
    private void Awake()
    {
        LoadJSON();
        if (!jsonLoaded)
        {
            generateInitialJSON();
            LoadJSON();
        }
    }

    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            LoadJSON();
        }
    }

    private bool verify_json()
    {
        return !(_config.constants == null);
    }

    public void LoadJSON()
    {
        try
        {
            _config = JsonConvert.DeserializeObject<StimConfig>(System.IO.File.ReadAllText(Application.streamingAssetsPath + "/stimConfig.json"));
            //_config = JsonUtility.FromJson<StimConfig>(System.IO.File.ReadAllText(Application.streamingAssetsPath + "/stimConfig.json"));
            if (verify_json())
            {
                jsonLoaded = true;
                Debug.LogError("New JSON loaded");
            } else
            {
                Debug.LogError("JSON not in correct format");
            }
        } catch(System.Exception ex)
        {
            Debug.LogError("JSON loading error: "+ex.Message);
        }
    }

    private void SaveJSON()
    {
        string toJSON = JsonConvert.SerializeObject(_config, Formatting.Indented);
        //string toJSON = JsonUtility.ToJson(_config, true);
        System.IO.File.WriteAllText(Application.streamingAssetsPath + "/stimConfig.json", toJSON);
    }

    public void addConstant(string name, float val)
    {
        _config.constants.Add(name, val);
        SaveJSON();
    }

    public void addStimParam(string name, float val)
    {
        _config.stimParams.Add(name, val);
        SaveJSON();
    }

    public float getStimParam(string name)
    {
        return _config.stimParams[name];
    }

    public float getConstant(string name)
    {
        return _config.constants[name];
    }

    public void modifyStimParam(string name, float value)
    {
        _config.stimParams[name] = value;
        SaveJSON();
    }

    private void generateInitialJSON()
    {
        //create a empty stim config instance
        _config = new StimConfig();
        //add basic values to dictionary
        addConstant("PModeProportional", 1.0f);
        addConstant("PModeOffsset", 0.0f);

        addConstant("PDModeProportional", 0.5f);
        addConstant("PDModeDerivative", 0.2f);
        addConstant("PDModeOffsset", 0.0f);

        addStimParam("Ch1Max", 0);
        addStimParam("Ch1Min", 0);
        addStimParam("Ch1Amp", 3.0f);

        addStimParam("Ch2Max", 0);
        addStimParam("Ch2Min", 0);
        addStimParam("Ch2Amp", 3.0f);

        addStimParam("Ch3Max", 0);
        addStimParam("Ch3Min", 0);
        addStimParam("Ch3Amp", 3.0f);

        addStimParam("Ch4Max", 0);
        addStimParam("Ch4Min", 0);
        addStimParam("Ch4Amp", 3.0f);

        addStimParam("Ch5Max", 0);
        addStimParam("Ch5Min", 0);
        addStimParam("Ch5Amp", 3.0f);

        addStimParam("Ch6Max", 0);
        addStimParam("Ch6Min", 0);
        addStimParam("Ch6Amp", 3.0f);

        addStimParam("Ch7Max", 0);
        addStimParam("Ch7Min", 0);
        addStimParam("Ch7Amp", 3.0f);

        addStimParam("Ch8Max", 0);
        addStimParam("Ch8Min", 0);
        addStimParam("Ch8Amp", 3.0f);

        addStimParam("Ch9Max", 0);
        addStimParam("Ch9Min", 0);
        addStimParam("Ch9Amp", 3.0f);
    }

    



}
