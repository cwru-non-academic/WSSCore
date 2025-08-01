using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using TMPro;
using UnityEngine;

public class portScanner : MonoBehaviour
{
    // Usage Instructions:
    // - Place this script on a separate empty GameObject (do not make it a child of the object with the stimulation script).
    // - Attach the stimulation script and the dropdown used for selecting the port.
    // - Connect the selectPort() method to a button or transition trigger in the UI.
    // -  Make sure the stimulation script's ForcePort boolean is set to true. The COM Port variable is irrelevant and will be overridden by the dropdown selection.

    [SerializeField] private Stimulation stim;
    [SerializeField] private TMP_Dropdown serialList;

    private string selectedPort = "";
    // Start is called before the first frame update
    void Start()
    {
        
    }

    void OnEnable()
    {
        stim.gameObject.SetActive(false);
        generatePortList();
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    private void generatePortList()
    {
        serialList.ClearOptions();
        string[] ports = SerialPort.GetPortNames();
        if(ports.Length > 0)
        {
            List<string> portNames = new List<string>();
            foreach (string port in ports)
            {
                portNames.Add(port);
            }
            serialList.AddOptions(portNames);
        }else
        {
            serialList.options = new List<TMP_Dropdown.OptionData> { new TMP_Dropdown.OptionData("empty") };
        }
        serialList.SetValueWithoutNotify(0);
        serialList.RefreshShownValue();
    }

    void OnDisable()
    {
        
    }

    public void selectPort()
    {
        selectedPort = serialList.options[serialList.value].text;
        if(selectedPort != "empty")
        {
           stim.gameObject.SetActive(false);
           stim.comPort = selectedPort;
           stim.gameObject.SetActive(true);
        }
        
    }
}
