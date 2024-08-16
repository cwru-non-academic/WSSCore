using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO.Ports;
using System;
using System.Threading;
using UnityEngine.PlayerLoop;
using UnityEditor.PackageManager;

public class Stimulation : MonoBehaviour
{
    [SerializeField] private bool forcePort = false;
    [SerializeField] private bool testMode = true;
    [SerializeField] private string comPort = "COM7";

    private const float delay = 0.1f;// delay between mesages to the WSS to avoid congestion on the radio

    public bool started = false;
    private bool ready = false;
    private bool editor = false;
    private bool running = false;
    private SerialToWSS WSS;

    #region "Channels vars"
    private int ch1Amp = 0;
    private int ch2Amp = 0;
    private int ch3Amp = 0;
    private int ch4Amp = 0;
    private int ch5Amp = 0;
    private int ch6Amp = 0;
    private int ch1PW = 0;
    private int ch2PW = 0;
    private int ch3PW = 0;
    private int ch4PW = 0;
    private int ch5PW = 0;
    private int ch6PW = 0;
    private int current_IPD = 50; //in us
    #endregion

    // Start is called before the first frame update
    public void Start()
    {
        
    }

    void OnEnable()
    {
        editor = Application.isEditor;
        if ((editor || Application.platform == RuntimePlatform.WindowsPlayer) && !testMode) //runs USB mode only on editor mode or windows mode 
        {
            if (!forcePort) //overide useful when multiple ports
            {
                WSS = new SerialToWSS();
            }
            else
            {
                WSS = new SerialToWSS(comPort);
            }
            NormalSetup();
            running = true;
        }
        else if (testMode)
        {
            running = false;
        }
    }

    // Update is called once per frame
    void Update()
    {
        if(!testMode)
        {
            WSS.checkForErrors();
            if (WSS.msgs.Count>0)
            {
                for (int i = 0; i < WSS.msgs.Count; i++)
                {
                    Debug.Log(WSS.msgs[i]);
                    WSS.msgs.RemoveAt(i);
                }
            }
        }
    }

    void OnDestroy()
    {
        
    }

    void OnDisable()
    {
        running = false;
        WSS.stream_change(new int[] { AmpTo255Convention(ch1Amp), AmpTo255Convention(ch2Amp), AmpTo255Convention(ch3Amp) },
            new int[] { 0, 0, 0 }, null);
        WSS.releaseCOM_port();
        ready = false;
    }

    #region "Stimulation methods"
    //current functions are design to be called by a discrete sensor  
    // like the bubble. For an analog sensor I suggets you only use the 
    //function below and disregard the rest
    public void StimulateAnalog(string finger, bool right, int PW, int amp)
    {
        //finger name of the finger (shown in if statements below)
        //right bool true for right hand false for left (not implemented yet)
        //PW is pulse width of the stimualtion in the range 4 to 255 us or 0us for no stim
        //Amp is amplitude of the stimualtion in the rnage of 0 to 83mA (non linear or exact).
        if (finger == "Index")
        {
            ch2Amp = amp;
            ch2PW = PW; 
        }
        else if (finger == "Middle")
        {
            ch3Amp = amp;
            ch3PW = PW;
        }
        else if (finger == "Ring")
        {
            ch4Amp = amp;
            ch4PW = PW;
        }
        else if (finger == "Pinky")
        {
            ch5Amp = amp;
            ch5PW = PW;
        }
        else if (finger == "Thumb")
        {
            ch1Amp = amp;
            ch1PW = PW;
        }
        else if (finger == "Palm")
        {
            ch6Amp = amp;
            ch6PW = PW;
        }
    }

    public void updateBuffer()
    {
        WSS.stream_change(new int[] {AmpTo255Convention(ch1Amp), AmpTo255Convention(ch2Amp), AmpTo255Convention(ch3Amp) }, 
            new int[] { ch1PW, ch2PW, ch3PW}, null);
    }

    IEnumerator UpdateCoroutine()
    {
        while(running)
        {
            while (started && ready)
            {
                updateBuffer();
                yield return new WaitForSeconds(0.02f);
            }
            yield return new WaitForSeconds(0.02f);
        }
    }

    public void StartStimulation()
    {
        if (ready)
        {
            StartCoroutine(StartCoroutine());
        }
    }

    IEnumerator StartCoroutine()
    {
        WSS.startStim();
        Debug.Log("start stim");
        yield return new WaitForSeconds(1.0f);
        started = true;
        StartCoroutine(UpdateCoroutine());
    }


    public void StopStimulation()
    {
        WSS.stopStim();
        Debug.Log("stop stim");
        started = false;
    }


    //TODO units of amplitude or max amplitude, unites of delay for scgeculd and event, max pW
    public void NormalSetup()
    {
        StartCoroutine(SetUpCoroutine());
    }

    IEnumerator SetUpCoroutine()
    {
        //gnd is first electrode 
        // thumb index middle
        WSS.clear(0); //clear everything to make sure setup is correct
        yield return new WaitForSeconds(delay);
        WSS.create_schedule(1, 13, 170); //create a schedule to be sync with the 170 int signal  //13ms is period so freq of about 77Hz
        yield return new WaitForSeconds(delay);
        WSS.creat_contact_config(1, new int[] { 0, 0, 2, 1 }, new int[] { 0, 0, 1, 2 }); //create a 2 cathodes and 1 anode for stim ch1
        yield return new WaitForSeconds(delay);
        WSS.create_event(1, 0, 1, 0, 0, new int[] { 11, 11, 0, 0 }, new int[] { 11, 11, 0, 0 }, new int[] { 0, 0, 50 }); //create a 3mA square wave with 0 PW and 50us IPD for ch1
        yield return new WaitForSeconds(delay);
        WSS.edit_event_ratio(1, 8); //make the wave symetric
        yield return new WaitForSeconds(delay);
        WSS.add_event_to_schedule(1, 1);
        yield return new WaitForSeconds(delay);
        WSS.create_schedule(2, 13, 170); //create a schedule to be sync with the 170 int signal  //13ms is period so freq of about 77Hz
        yield return new WaitForSeconds(delay);
        WSS.creat_contact_config(2, new int[] { 0, 2, 0, 1 }, new int[] { 0, 1, 0, 2}); //create a 1 cathodes and 1 anode for stim ch2
        yield return new WaitForSeconds(delay);
        WSS.create_event(2, 2, 2, 0, 0, new int[] { 11, 0, 11, 0 }, new int[] { 11, 0, 11, 0 }, new int[] { 0, 0, 50 }); //create a 3mA square wave with 0 PW and 50us IPD for ch2
        yield return new WaitForSeconds(delay);
        WSS.edit_event_ratio(2, 8); //make the wave symetric
        yield return new WaitForSeconds(delay);
        WSS.add_event_to_schedule(2, 2);
        yield return new WaitForSeconds(delay);
        WSS.create_schedule(3, 13, 170); //create a schedule to be sync with the 170 int signal  //13ms is period so freq of about 77Hz
        yield return new WaitForSeconds(delay);
        WSS.creat_contact_config(3, new int[] { 2, 0, 0, 1 }, new int[] { 1, 0, 0, 2 }); //create a 1 cathodes and 1 anode for stim ch3
        yield return new WaitForSeconds(delay);
        WSS.create_event(3, 4, 3, 0, 0, new int[] { 11, 0, 0, 11 }, new int[] { 11, 0, 0, 11 }, new int[] { 0, 0, 50 }); //create a 3mA square wave with 0 PW and 50us IPD for ch3
        yield return new WaitForSeconds(delay);
        WSS.edit_event_ratio(3, 8); //make the wave symetric
        yield return new WaitForSeconds(delay);
        WSS.add_event_to_schedule(3, 3);
        yield return new WaitForSeconds(delay);
        WSS.sync_group(170); //sync schedules with 170 sync signal
        yield return new WaitForSeconds(0.5f);
        ready = true;
        Debug.Log("ready");
    } 

    public void Save()
    {
        if(ready)
        {
            ready = false;
            StartCoroutine(DelayReadyCoroutine());
            WSS.populateFRAMSettings();
        } 
    }

    public void load()
    {
        if (ready)
        {
            ready = false;
            StartCoroutine(DelayReadyCoroutine());
            WSS.populateBoardSettings();
        }
    }

    //deprecated as stream command now handles both PW and AMP
    public void UpdateAllPA(int PA) //in mA (0 to 83mA)
    {
        
    }

    //transform mA to 0 to 255 byte convetion
    private int AmpTo255Convention(int amp) {
        if(amp <4)
        {
            return (int) Mathf.Pow(amp /0.0522f, 1/1.5466f)+1;
        } else
        {
            return (int) ((amp + 1.7045f)/ 0.3396f)+1;
        }
    }

    public void request_Configs(int command, int id)
    {
        if (ready)
        {
            ready = false;
            StartCoroutine(DelayReadyCoroutine());
            WSS.request_configs(command, id);
        }
    }

    public void UpdateIPD(int IPD) // in us (0 to 1000us)
    {
        if (ready)
        {
            ready = false;
            if (IPD > 1000)
            {
                IPD = 1000;
            }
            current_IPD = IPD;
            StartCoroutine(UpdateIPDCoroutine());
        }
    }

    IEnumerator UpdateIPDCoroutine()
    {
        WSS.edit_event_PW(1, new int[] { 0, 0, current_IPD }); //edit ch 1
        yield return new WaitForSeconds(delay);
        WSS.edit_event_PW(2, new int[] { 0, 0, current_IPD }); //edit ch 2
        yield return new WaitForSeconds(delay);
        WSS.edit_event_PW(3, new int[] { 0, 0, current_IPD }); //edit ch 3
        yield return new WaitForSeconds(delay);
        ready = true;
    }

    public void UpdateFrequency(int FR) //in Hz (1-1000Hz) might be further limited by PW duration
    {
        if (ready)
        {
            ready = false;
            StartCoroutine(DelayReadyCoroutine());
            float temp = 1000.0f / FR;
            int Freq = (int)temp; //in ms now
                                  //sets PW to 0 too
            WSS.stream_change(null, new int[] { 0, 0, 0 }, new int[] { Freq, Freq, Freq });
        }
    } //max 1000ms for pw IPD

    public void updateWaveform(int[] waveform, int eventID) {
        if (ready)
        {
            ready = false;
            WaveformBuilder stimShape = new WaveformBuilder(waveform);
            StartCoroutine(UpdateWaveformCoroutine(stimShape, eventID));
        }
    }

    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID) //overload to just select from waveforms in memory 
    //slots 0 to 10 are predefined waveforms and slots 11 to 13 are custom defined waveforms
    {
        if (ready)
        {
            ready = false;
            StartCoroutine(DelayReadyCoroutine());
            WSS.edit_event_shape(eventID, cathodicWaveform, anodicWaveform);
        }
    }

    //overload for loading from json functionality
    public void updateWaveform(WaveformBuilder waveform, int eventID) 
    {
        if (ready)
        {
            ready = false;
            WaveformBuilder stimShape = waveform;
            StartCoroutine(UpdateWaveformCoroutine(stimShape, eventID));
        }
    }

    public void loadWaveform(string fileName, int eventID)
    {
        try
        {
            Waveform shape = JsonUtility.FromJson<Waveform>(System.IO.File.ReadAllText(Application.streamingAssetsPath + "/" + fileName + "WF.json"));
            updateWaveform(new WaveformBuilder(shape), eventID);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("JSON loading error: " + ex.Message);
        }
    }


    IEnumerator UpdateWaveformCoroutine(WaveformBuilder wave, int eventID)//custom waveform slots 0 to 2 are attached to shape slots 11 to 13
    {
        WSS.set_costume_waveform(0, wave.getCatShapeArray()[0..^24], 0);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(0, wave.getCatShapeArray()[8..^16], 1);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(0, wave.getCatShapeArray()[16..^8], 2);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(0, wave.getCatShapeArray()[24..^0], 3);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(1, wave.getAnodicShapeArray()[0..^24], 0);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(1, wave.getAnodicShapeArray()[8..^16], 1);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(1, wave.getAnodicShapeArray()[16..^8], 2);
        yield return new WaitForSeconds(delay);
        WSS.set_costume_waveform(1, wave.getAnodicShapeArray()[24..^0], 3);
        yield return new WaitForSeconds(delay);
        WSS.edit_event_shape(eventID, 11, 12);
        yield return new WaitForSeconds(delay);
        ready = true;
    }

    IEnumerator DelayReadyCoroutine()
    {
        yield return new WaitForSeconds(0.1f);
        ready = true;
    }
    #endregion

    #region getSets
    public bool Ready()
    {
        return ready;
    }
    #endregion
}
