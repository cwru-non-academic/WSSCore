using System;
using System.Collections.Generic;
using System.Data;
using System.IO.Ports;
using System.Reflection;
using System.Threading.Tasks;

public class SerialToWSS
{
    #region COM_Port_vars
    private string comPort = "COM5";
    private const int BAUD_RATE = 115200;                   // bps that the UART is setup for
    private const StopBits STOP_BIT_COUNT = StopBits.One;   // 1 stop bit
    private const int BITS_PER_DATA_CHAR = 8;               // 8-bit data character length (i.e., we're using bytes as characters)
    private const Parity PARITY = Parity.None;              // parity disabled
    private const int timeOut = 10;
    private SerialPort stream;
    #endregion

    #region message_vars
    //each messesage consists of a preable, data , and ending
    //data changes contents and size so created on each msg
    private static byte sender_address = 0x00;
    private static byte wss_address1 = 0x81;
    private static byte wss_address2 = 0x82;
    private static byte wss_address3 = 0x83;
    private static byte wss_broadcast = 0x8F;
    //preable size is constant but its content change at contructions so created and modify once
    private byte[] preamble = new byte[] { sender_address, wss_address1 }; //sender address, wss address, command type
    //ending size is constant but its content change so created onece but modified for each msg
    private byte[] ending = new byte[] { 0x00, 0xC0 }; //check sum, ending byte
    private const byte END_BYTE = 0xC0;          // indicates end of message
    private const byte ESC_BYTE = 0xDB;          // indicates to receiver that a special byte follows
    private const byte END_SUBST_BYTE = 0xDC;    // is interpreted as 0xC0 when following 0xDB as data
    private const byte ESC_SUBST_BYTE = 0xDD;    // follows 0xDB; indicates to receiver that 0xDB is intended as data
    public List<string> msgs;
    Dictionary<byte, string> errorMsgs;
    #endregion

    #region queue vars
    private List<QueueID> queue = new List<QueueID>(); // each queue position will hold a unique identifier and a message type identifier 
    private int queueCount = 0;
    private int timeOutQueue = 2000; // in ms
    private bool started = false;
    #endregion

    #region constructors_and_overloads
    //Base Constructor
    public SerialToWSS()
    {
        errorMsgStartUp();
        comPort = getCOMPort();
        stream = new SerialPort(comPort, BAUD_RATE, PARITY, BITS_PER_DATA_CHAR, STOP_BIT_COUNT);
        stream.ReadTimeout = timeOut;
        stream.Open();
    }
    //overload to specify sender address and wss address
    public SerialToWSS(byte sender, byte wss)
    {
        errorMsgStartUp();
        comPort = getCOMPort();
        stream = new SerialPort(comPort, BAUD_RATE, PARITY, BITS_PER_DATA_CHAR, STOP_BIT_COUNT);
        stream.ReadTimeout = timeOut;
        stream.Open();
        preamble[0]=sender; 
        preamble[1]=wss;
        
    }
    //overload to specify port
    public SerialToWSS(string port)
    {
        errorMsgStartUp();
        comPort = port;
        stream = new SerialPort(comPort, BAUD_RATE, PARITY, BITS_PER_DATA_CHAR, STOP_BIT_COUNT);
        stream.ReadTimeout = timeOut;
        stream.Open();
    }
    //overload to specify port and addresses
    public SerialToWSS(string port, byte sender, byte wss)
    {
        errorMsgStartUp();
        comPort = port;
        stream = new SerialPort(comPort, BAUD_RATE, PARITY, BITS_PER_DATA_CHAR, STOP_BIT_COUNT);
        stream.ReadTimeout = timeOut;
        stream.Open();
        preamble[0] = sender;
        preamble[1] = wss;
    }

    //destructor
    ~SerialToWSS()
    {
        //close the serial port once the class gets garbage collected
        //(avoid the port being still open when the program is not running anymore,
        //so other programs or another instance of this program can use the serial port)
        if (stream != null)
        {
            if (stream.IsOpen)
            {
                stream.Close(); 
            }
        }
    }

    private void errorMsgStartUp()
    {
        msgs = new List<string>();
        errorMsgs = new Dictionary<byte, string>()
        {
            {0x00,"No Error"},
            {0x01,"Comms Error"},
            {0x02,"Wrong Reciever Error"},
            {0x03,"Checksum Error"},
            {0x04,"Command Error"},
            {0x05,"Parameters Error"},
            {0x06,"No Setup Error"},
            {0x07,"Incompatible Error"},
            {0x0B,"No Schedule Error"},
            {0x0C,"No Event Error"},
            {0x0D,"No Memory Error"},
            {0x0E,"Not Event Error"},
            {0x0F,"Delay Too Long Error"},
            {0x10,"Wrong Schedule Error"},
            {0x11,"Duration Too Short Error"},
            {0x12,"Fault Error"},
            {0x15,"Delat Too Short Error"},
            {0x16,"Event Exists Error"},
            {0x17,"Schedule Exists Error"},
            {0x18,"No Config Error"},
            {0x19,"Bad State Error"},
            {0x1A,"Not Shape Error"},
            {0x20,"Wrong Address Error"},
            {0x30,"Stream Parameters Error"},
            {0x31,"Stream Address Error"},
            {0x81,"Output Invalid Error"},
        };
    }

    #endregion

    #region com_port_methods
    private void WriteToWSS(byte[] message, int lenght)
    {
        //msgs.Add(ByteToString(message, lenght)); //debug to see byte output
        stream.Write(message, 0, lenght); 
        stream.BaseStream.Flush();
    }

    public void releaseCOM_port()
    {
        if (stream != null)
        {
            if (stream.IsOpen)
            {
                stream.Close();
            }
        }
    }

    //pick first com port in list so if multiple com ports use overide (set desired port in contructor in the form "COMX")
    private string getCOMPort()
    {
        string[] ports = SerialPort.GetPortNames();


        if (ports.Length > 1)
        {
            msgs.Add("Multiple Ports Beware");
            // Display each port name to the console.
            msgs.Add("The following serial ports were found:");
            foreach (string port in ports)
            {
                msgs.Add("Port: " + port.ToString());
            }
            msgs.Add("Using port: " + ports[0].ToString());
        }
        return ports[0].ToString();
    }

    private string ByteToString(byte[] data, int lenght)
    {
        string str ="";
        for(int i = 0; i<lenght; i++)
        {
            str += data[i].ToString("x")+" ";
        }
        return str;
    }

    private string processInput(byte[] data, int length)
    {
        //error msg struture wss sender 05 02 errorCode commandWithError Checksum endByte
        string error = "";
        // Return was an error
        if (data[2] == 0x05)
        {
            //remove message from queue that caused error 

            //transform codes in to plain text error message
            if (errorMsgs.TryGetValue(data[4], out error))
            {
                removeFromQueue(data[5]);
                return "Error: "+error + " in Command: " + data[5].ToString("x");
            }
            return "Error: Error Not Found";
        }
        //not an error. Handle reponses, response identifier comes after data lenght byte or data[4]
        removeFromQueue(data[2]);
        if (data[2] == 0x0B)
        {
            if (data[4]==0x01)//start acknowlege
            {
                started = true;
                return "Log: Start Acknowleged by WSS " + data[0].ToString("x");
            } else if (data[4] == 0x00) //stop acknowlege
            {
                started= false;
                return "Log: Stop Acknowleged by WSS " + data[0].ToString("x");
            }
        }
        return ByteToString(data, length);
    }

    //TODO remove while to improve FPS and handle responses
    public void checkForErrors()
    {
        while (stream.BytesToRead > 5)
        {
            byte[] incoming = new byte[25];
            stream.Read(incoming, 0, incoming.Length);
            int realLenght= (int)incoming[3]+6;
            if (realLenght > incoming.Length)
            {
                realLenght = incoming.Length;
            }
            //msgs.Add("In " + ByteToString(incoming, realLenght)); //debug to see byte input
            msgs.Add(processInput(incoming, realLenght));
        }
    }
    #endregion

    #region queue methods
    public bool isQueueEmpty()
    {
        if (queue.Count == 0)
        {
            return true;
        }
        return false;
    }

    public void clearQueue()
    {
        queue.Clear();
    }

    private void queueWriteToWSS(byte msgID, byte[] message, int lenght)
    {
        if(msgID >0x2F && msgID<0x34) //stream msges do not go into queue because they have no response
        {
            WriteToWSS(message, lenght);
        } else
        {
            QueueID q = new QueueID(queueCount, msgID);
            queue.Add(q);
            increaseCount();
            Task task = Task.Run(() =>
            {
                int timeElapse = 0;
                while (queue[0] != q && queue.Count > 0)
                {
                    Task.Delay(10).Wait();
                    timeElapse += 10;
                    if (timeElapse > timeOutQueue)
                    {
                        queue.Clear();
                        msgs.Add("Error: Queue timed out");
                    }
                }
                if (queue.Count > 0)
                {
                    WriteToWSS(message, lenght);
                }

            });
        }
    }

    private void increaseCount()
    {
        queueCount++;
        if (queueCount > 1000)
        {
            queueCount = 0;
        }
    }

    private bool removeFromQueue(byte msgID)
    {
        for(int i=0; i<queue.Count; i++)
        {
            if (queue[i].msgID == msgID)
            {
                queue.RemoveAt(i);
                return true;
            }
        }
        queue.Clear();
        msgs.Add("Error: No such msgID on queue. MsgID: "+ msgID.ToString("x"));
        return false;
    }

    public bool Started()
    {
        return started;
    }

    public void Stoped()
    {
        started = false;
    }
    #endregion

    #region stimulation_communication_methods
    private void msg_builder(byte[] data, int target)
    {
        setTarget(target);
        byte[] preMsg = new byte[preamble.Length + data.Length + ending.Length];
        Buffer.BlockCopy(preamble, 0, preMsg, 0, preamble.Length);
        Buffer.BlockCopy(data, 0, preMsg, preamble.Length, data.Length);
        Buffer.BlockCopy(ending, 0, preMsg, preamble.Length + data.Length, ending.Length);
        byte sum= check_Sum(preMsg);
        preMsg[^2]= sum;
        checkSpecial(preMsg);
        
    }

    private void setTarget(int target)
    {
        switch (target)
        {
            case 0:
                preamble = new byte[] { sender_address, wss_broadcast };
                break;
            case 1:
                preamble = new byte[] { sender_address, wss_address1 };
                break;
            case 2:
                preamble = new byte[] { sender_address, wss_address2 };
                break;
            case 3:
                preamble = new byte[] { sender_address, wss_address3 };
                break;
            default:
                preamble = new byte[] { sender_address, wss_address1 };
                break;
        }
    }

    private void checkSpecial(byte[] preMsg)
    {
        byte[] msg = new byte[2 * preMsg.Length];
        int lenght = 0;
        for (int i = 0; i < preMsg.Length-1; i++)
        {
            // Check for and convert special bytes. Append output to buffer array
            if (preMsg[i] == END_BYTE)
            {
                msg[lenght] = ESC_BYTE;
                lenght++;
                msg[lenght] = END_SUBST_BYTE;
            } else if (preMsg[i] == ESC_BYTE)
            {
                msg[lenght] = ESC_BYTE;
                lenght++;
                msg[lenght] = ESC_SUBST_BYTE;
            }
            else
            {
                msg[lenght] = preMsg[i];
            }
            lenght++;
        }
        msg[lenght] = END_BYTE;
        lenght++;
        queueWriteToWSS(preMsg[2] ,msg, lenght);
    }

    private byte check_Sum(byte[] msg)
    {
        int sum = 0x00;
        for (int i = 0; i < msg.Length-2; i++)//do not take into account checksum and end byte
        {
            sum += msg[i];
        }
        sum = ((0x00FF & sum) + (sum >> 8)) ^ 0xFF;
        return (byte)sum;
    }
    #endregion

    #region stimulation_base_methods
    //reset microcontroller
    public void reset(int targetWSS)
    {
        byte[] data = new byte[] { 0x04, 0x00 };
        data[1] = BitConverter.GetBytes(data.Length-2)[0];
        msg_builder(data, targetWSS);
    }

    //echo msg
    public void echo(int targetWSS, int data1, int data2)
    {
        byte[] data = new byte[] { 0x07, 0x00, BitConverter.GetBytes(data1)[0], BitConverter.GetBytes(data2)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //request battery and impedance, currently not implemented
    public void request_analog(int targetWSS)
    {
        //TODO add reading flag
        byte[] data = new byte[] { 0x02, 0x00, 0x01 };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //clears all:0, events:1, schedules:2, contacts:3
    public void clear(int targetWSS, int command)
    {
        byte[] data = new byte[] { 0x40, 0x00, BitConverter.GetBytes(command)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    public void request_configs(int targetWSS, int command, int id)
    {
        byte[] data = new byte[] { 0x41, 0x00, 0x00, 0x00, BitConverter.GetBytes(id)[0] };
        switch (command)
        {
            case 0: //Request Output Configuration List
                data[2] = 0x00;
                data[3] = 0x00;
                break; 
            case 1: //Request Output Configuration configuration
                data[2] = 0x00;
                data[3] = 0x01;
                break;
            case 2: // Request Event List
                data[2] = 0x01;
                data[3] = 0x00;
                break;
            case 3: // Request basic Event configuration
                data[2] = 0x01;
                data[3] = 0x01;
                break;
            case 4: // Request Event output configuration
                data[2] = 0x01;
                data[3] = 0x02;
                break;
            case 5: // Request Event stim configuration
                data[2] = 0x01;
                data[3] = 0x03;
                break;
            case 6: // Request Event shape configuration
                data[2] = 0x01;
                data[3] = 0x04;
                break;
            case 7: // Request Schedule basic configuration
                data[2] = 0x02;
                data[3] = 0x00;
                break;
            case 8: // Request Schedule listing
                data[2] = 0x02;
                data[3] = 0x01;
                break;
            default: //case 0
                data[2] = 0x00;
                data[3] = 0x00;
                break;
        }
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //id is for future reference, stim setup defines sources and sinks for charge and recharge setup for recharge. 
    //array of 4 int one per output on the stimulator. Use 0 for not used, 1 for source, and 2 for sink
    //So, to set 3 cathodes and 1 anode for stim you would send [2, 2, 2, 1], and for recharge [1, 1, 1, 2].
    //The order fro the board is the forth element in the array is the connector clossest to the switch ir order so the first element is the one farthest from the switch.
    public void creat_contact_config(int targetWSS, int contactID, int[] stimSetup, int[] rechargSetup)
    {
        byte stimByte = processContact(stimSetup);
        byte rechargeByte = processContact(rechargSetup);
        //setup values should be 8 bits, or 4 pairs of bits, that represent the 4 outputs on the system.
        //10 = a source, and 11 = a sink. 00 = not used. 
        //So, to set 3 cathodes and 1 anode for stim you would send 11 11 11 10 or (0xAB), and for recharge 10 10 10 11 (0xFE).
        byte[] data = new byte[] { 0x42, 0x00, BitConverter.GetBytes(contactID)[0], stimByte, rechargeByte};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    private byte processContact(int[] setup)
    {
        int result = 0;
        for (int i = setup.Length - 1; i >= 0; i--)
        {
            if (setup[i] != 0)
            {
                result += (setup[i] + 1) * (int)Math.Pow(2, (setup.Length - i - 1) * 2);
            }
        }
        return BitConverter.GetBytes(result)[0];
    }

    //deletes an contact based on ID
    public void delete_contact_config(int targetWSS, int contactID)
    {
        byte[] data = new byte[] { 0x43, 0x00, BitConverter.GetBytes(contactID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //create event (specified by id)based on inputs (overloads)
    public void create_event(int targetWSS, int eventID, int delay, int outConfigID)
    {
        byte[] data = new byte[] { 0x44, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(delay)[0], BitConverter.GetBytes(outConfigID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    public void create_event(int targetWSS, int eventID, int delay, int outConfigID, int standardShapeID, int rechargeShapeID)
    {
        byte[] data = new byte[] { 0x44, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(delay)[0], 
            BitConverter.GetBytes(outConfigID)[0], BitConverter.GetBytes(standardShapeID)[0] , BitConverter.GetBytes(rechargeShapeID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //4 amplitues per arrays for standard and recharge, 3 PW params: standar PW, recharge PW, and IPD
    public void create_event(int targetWSS, int eventID, int delay, int outConfigID, int[] standardAmp, int[] rechargeAmp, int[] PW)
    {
        byte[] data;
        if (PW[0]>255 || PW[1]>255 || PW[2]>255 || PW[3] > 255) //handle pw greater than 255us 
        {
            data = new byte[] { 0x44, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(delay)[0], BitConverter.GetBytes(outConfigID)[0],
            BitConverter.GetBytes(standardAmp[0])[0], BitConverter.GetBytes(standardAmp[1])[0], BitConverter.GetBytes(standardAmp[2])[0], BitConverter.GetBytes(standardAmp[3])[0],
            BitConverter.GetBytes(rechargeAmp[0])[0], BitConverter.GetBytes(rechargeAmp[1])[0], BitConverter.GetBytes(rechargeAmp[2])[0], BitConverter.GetBytes(rechargeAmp[3])[0],
            BitConverter.GetBytes(PW[0])[1], BitConverter.GetBytes(PW[0])[0],
            BitConverter.GetBytes(PW[2])[1], BitConverter.GetBytes(PW[2])[0],
            BitConverter.GetBytes(PW[1])[1], BitConverter.GetBytes(PW[1])[0]};
        } else
        {
            data = new byte[] { 0x44, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(delay)[0], BitConverter.GetBytes(outConfigID)[0],
            BitConverter.GetBytes(standardAmp[0])[0], BitConverter.GetBytes(standardAmp[1])[0], BitConverter.GetBytes(standardAmp[2])[0], BitConverter.GetBytes(standardAmp[3])[0],
            BitConverter.GetBytes(rechargeAmp[0])[0], BitConverter.GetBytes(rechargeAmp[1])[0], BitConverter.GetBytes(rechargeAmp[2])[0], BitConverter.GetBytes(rechargeAmp[3])[0],
            BitConverter.GetBytes(PW[0])[0], BitConverter.GetBytes(PW[2])[0], BitConverter.GetBytes(PW[1])[0]};
        }
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    public void create_event(int targetWSS, int eventID, int delay, int outConfigID, int standardShapeID, int rechargeShapeID, int[] standardAmp, int[] rechargeAmp, int[] PW)
    {
        //delay from schedule start is in ms make sure different ch are at least 2ms apart on the same schedule
        byte[] data;
        if (PW[0] > 255 || PW[1] > 255 || PW[2] > 255) //handle pw greater than 255us 
        {
            data = new byte[] { 0x44, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(delay)[0], BitConverter.GetBytes(outConfigID)[0],
            BitConverter.GetBytes(standardAmp[0])[0], BitConverter.GetBytes(standardAmp[1])[0], BitConverter.GetBytes(standardAmp[2])[0], BitConverter.GetBytes(standardAmp[3])[0],
            BitConverter.GetBytes(rechargeAmp[0])[0], BitConverter.GetBytes(rechargeAmp[1])[0], BitConverter.GetBytes(rechargeAmp[2])[0], BitConverter.GetBytes(rechargeAmp[3])[0],
            BitConverter.GetBytes(PW[0])[1], BitConverter.GetBytes(PW[0])[0],
            BitConverter.GetBytes(PW[2])[1], BitConverter.GetBytes(PW[2])[0],
            BitConverter.GetBytes(PW[1])[1], BitConverter.GetBytes(PW[1])[0],
            BitConverter.GetBytes(standardShapeID)[0] , BitConverter.GetBytes(rechargeShapeID)[0]};
        }
        else
        {
            data = new byte[] { 0x44, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(delay)[0], BitConverter.GetBytes(outConfigID)[0],
            BitConverter.GetBytes(standardAmp[0])[0], BitConverter.GetBytes(standardAmp[1])[0], BitConverter.GetBytes(standardAmp[2])[0], BitConverter.GetBytes(standardAmp[3])[0],
            BitConverter.GetBytes(rechargeAmp[0])[0], BitConverter.GetBytes(rechargeAmp[1])[0], BitConverter.GetBytes(rechargeAmp[2])[0], BitConverter.GetBytes(rechargeAmp[3])[0],
            BitConverter.GetBytes(PW[0])[0], BitConverter.GetBytes(PW[2])[0], BitConverter.GetBytes(PW[1])[0],
            BitConverter.GetBytes(standardShapeID)[0] , BitConverter.GetBytes(rechargeShapeID)[0]};
        }
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //delete event (specified by id)
    public void delete_event(int targetWSS, int eventID)
    {
        byte[] data = new byte[] { 0x45, 0x00, BitConverter.GetBytes(eventID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    // add event with event id to schedule with schedule id
    public void add_event_to_schedule(int targetWSS, int eventID, int scheduleID)
    {
        byte[] data = new byte[] { 0x46, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(scheduleID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //delete event (specified by id)from its only assign schedule
    public void delete_event_from_schedule(int targetWSS, int eventID)
    {
        byte[] data = new byte[] { 0x47, 0x00, BitConverter.GetBytes(eventID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //moves and event from one schedule to another witha delay. if the delay
    // is longer thna the frequency of the schedule it fails and leave event
    //on its original schedule
    public void move_event_to_schedule(int targetWSS, int eventID, int scheduleID, int delay)
    {
        byte[] data = new byte[] { 0x48, 0x00, BitConverter.GetBytes(eventID)[0], BitConverter.GetBytes(scheduleID)[0], BitConverter.GetBytes(delay)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change event (specified by id) output config based on id
    public void edit_event_OutConfig(int targetWSS, int eventID, int outConfigID)
    {
        byte[] data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x01, BitConverter.GetBytes(outConfigID)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change event (specified by id) pulse widths 3 PW params: standar PW, recharge PW, and IPD
    public void edit_event_PW(int targetWSS, int eventID, int[] PW)
    {
        byte[] data;
        if (PW[0] > 255 || PW[1] > 255 || PW[2] > 255) //handle pw greater than 255us 
        {
            data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x02,
                BitConverter.GetBytes(PW[0])[1], BitConverter.GetBytes(PW[0])[0],
                BitConverter.GetBytes(PW[2])[1], BitConverter.GetBytes(PW[2])[0],
                BitConverter.GetBytes(PW[1])[1], BitConverter.GetBytes(PW[1])[0]};
        }
        else
        {
            data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x02,
                BitConverter.GetBytes(PW[0])[0], BitConverter.GetBytes(PW[2])[0], BitConverter.GetBytes(PW[1])[0]};
        }
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change event (specified by id) amplitude config
    public void edit_event_Amp(int targetWSS, int eventID, int[] standardAmp, int[] rechargeAmp)
    {
        byte[] data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x04,
            BitConverter.GetBytes(standardAmp[0])[0], BitConverter.GetBytes(standardAmp[1])[0], BitConverter.GetBytes(standardAmp[2])[0], BitConverter.GetBytes(standardAmp[3])[0],
            BitConverter.GetBytes(rechargeAmp[0])[0], BitConverter.GetBytes(rechargeAmp[1])[0], BitConverter.GetBytes(rechargeAmp[2])[0], BitConverter.GetBytes(rechargeAmp[3])[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change event (specified by id) shape config
    public void edit_event_shape(int targetWSS, int eventID, int standardShapeID, int rechargeShapeID)
    {
        byte[] data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x05, 
            BitConverter.GetBytes(standardShapeID)[0], BitConverter.GetBytes(rechargeShapeID)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change event (specified by id) delay in ms from start of schedule
    public void edit_event_delay(int targetWSS, int eventID, int delay)
    {
        byte[] data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x06,
            BitConverter.GetBytes(delay)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change event (specified by id) ratio in ratios of 1 to 1, to 2, to 4, or to 8.
    public void edit_event_ratio(int targetWSS, int eventID, int ratio)
    {
        byte[] data = new byte[] { 0x49, 0x00, BitConverter.GetBytes(eventID)[0], 0x07,
            BitConverter.GetBytes(ratio)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //create schedule
    public void create_schedule(int targetWSS, int scheduleID, int duration, int syncSignal)
    {
        byte[] data = new byte[] { 0x4A, 0x00, BitConverter.GetBytes(scheduleID)[0], BitConverter.GetBytes(duration)[1], BitConverter.GetBytes(duration)[0], BitConverter.GetBytes(syncSignal)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //delete schedule
    public void delete_schedule(int targetWSS, int scheduleID)
    {
        byte[] data = new byte[] { 0x4B, 0x00, BitConverter.GetBytes(scheduleID)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //sync group and starts scheculd from ready to active
    public void sync_group(int targetWSS, int syncSignal)
    {
        byte[] data = new byte[] { 0x4C, 0x00, BitConverter.GetBytes(syncSignal)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change state of a sync group from STATE_READY = 1, STATE_ACTIVE = 0, STATE_SUSPEND = 2   
    public void change_group_state(int targetWSS, int syncSignal, int state)
    {
        byte[] data = new byte[] { 0x4D, 0x00, BitConverter.GetBytes(syncSignal)[0], BitConverter.GetBytes(state)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change schedule(by schedule id) state
    public void change_schedule_state(int targetWSS, int scheduleID, int state)
    {
        byte[] data = new byte[] { 0x4E, 0x00, 0x01, BitConverter.GetBytes(scheduleID)[0], BitConverter.GetBytes(state)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change schedule(by schedule id) group
    public void change_schedule_group(int targetWSS, int scheduleID, int syncSignal)
    {
        byte[] data = new byte[] { 0x4E, 0x00, 0x02, BitConverter.GetBytes(scheduleID)[0], BitConverter.GetBytes(syncSignal)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //change schedule(by schedule id) duration
    public void change_schedule_duration(int targetWSS, int scheduleID, int duration)
    {
        byte[] data = new byte[] { 0x4E, 0x00, 0x03, BitConverter.GetBytes(scheduleID)[0], BitConverter.GetBytes(duration)[0] };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //reset all schedules
    public void reset_schedule(int targetWSS)
    {
        byte[] data = new byte[] { 0x4F, 0x00 };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //costumizable waveform upload
    public void set_costume_waveform(int targetWSS, int slot, int[] waveform, int msgNumber)//waveform is an array of 32 bytes 0 to 255 but only 8 can be sent a time call this method 4 times with coresponding delays in between
    {
        int wfIndex = 0;
        byte[] data = new byte[] { 0x9D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
        data[2] = BitConverter.GetBytes(slot)[0];
        data[3] = BitConverter.GetBytes(msgNumber)[0];
        for (int i = 4; i < data.Length; i+=2)
        {
            data[i] = BitConverter.GetBytes(waveform[wfIndex])[1];
            data[i + 1] = BitConverter.GetBytes(waveform[wfIndex])[0];
            wfIndex++;
        }
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //start stim
    public void startStim()
    {
        byte[] data = new byte[] { 0x0B, 0x00, 0x03};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, 0);
    }

    public void startStim(int targetWSS)
    {
        byte[] data = new byte[] { 0x0B, 0x00, 0x03 };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //stop stim
    public void stopStim()
    {
        byte[] data = new byte[] { 0x0B, 0x00, 0x04 };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, 0);
    }

    //edit setting array
    public void editSettings(int targetWSS, int address, int value)
    {
        byte[] data = new byte[] { 0x09, 0x00, 0x03, BitConverter.GetBytes(address)[0], BitConverter.GetBytes(value)[0]};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //move setting from FRAM to board
    public void populateBoardSettings(int targetWSS)
    {
        byte[] data = new byte[] { 0x09, 0x00, 0x0A };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //move setting from board to FRAM
    public void populateFRAMSettings(int targetWSS)
    {
        byte[] data = new byte[] { 0x09, 0x00, 0x0B};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //erase log data
    public void erraseLog(int targetWSS)
    {
        byte[] data = new byte[] { 0x09, 0x00, 0x04 };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    //get log data
    public void GetLog(int targetWSS)
    {
        //TODO add read flag
        byte[] data = new byte[] { 0x09, 0x00, 0x05 };
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }


    //TODO add safe for more than one null
    //change PA, PW, IPI all at the same time or two at a time (use null for not changing param) fro three diferent schedules for one schedule and one event use edit event cmd instead
    //three parameters per array
    //IPI is frequency in ms with 1 ms resolution and each Freq is for one schedule
    public void stream_change(int targetWSS, int[] PA, int[] PW, int[] IPI)
    {
        byte[] data;
        if (PA == null)
        {
            data = new byte[] { 0x33, 0x00, 0x00, 0x00, 0x00,
                BitConverter.GetBytes(PW[0])[0], BitConverter.GetBytes(PW[1])[0], BitConverter.GetBytes(PW[2])[0],
                BitConverter.GetBytes(IPI[0])[0], BitConverter.GetBytes(IPI[1])[0], BitConverter.GetBytes(IPI[2])[0]};
        } else if(PW == null)
        {
            data = new byte[] { 0x32, 0x00, BitConverter.GetBytes(PA[0])[0], BitConverter.GetBytes(PA[1])[0], BitConverter.GetBytes(PA[2])[0],
                0x00, 0x00, 0x00,
                BitConverter.GetBytes(IPI[0])[0], BitConverter.GetBytes(IPI[1])[0], BitConverter.GetBytes(IPI[2])[0]};
        } else if(IPI == null)
        {
            data = new byte[] { 0x31, 0x00, BitConverter.GetBytes(PA[0])[0], BitConverter.GetBytes(PA[1])[0], BitConverter.GetBytes(PA[2])[0],
                BitConverter.GetBytes(PW[0])[0], BitConverter.GetBytes(PW[1])[0], BitConverter.GetBytes(PW[2])[0],
                0x00, 0x00, 0x00};
        } else
        {
            data = new byte[] { 0x30, 0x00, BitConverter.GetBytes(PA[0])[0], BitConverter.GetBytes(PA[1])[0], BitConverter.GetBytes(PA[2])[0],
                BitConverter.GetBytes(PW[0])[0], BitConverter.GetBytes(PW[1])[0], BitConverter.GetBytes(PW[2])[0],
                BitConverter.GetBytes(IPI[0])[0], BitConverter.GetBytes(IPI[1])[0], BitConverter.GetBytes(IPI[2])[0]};
        }
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, targetWSS);
    }

    public void zero_out_stim() 
    {
        byte[]  data = new byte[] { 0x31, 0x00, BitConverter.GetBytes(0)[0], BitConverter.GetBytes(0)[0], BitConverter.GetBytes(0)[0],
                BitConverter.GetBytes(0)[0], BitConverter.GetBytes(0)[0], BitConverter.GetBytes(0)[0],
                0x00, 0x00, 0x00};
        data[1] = BitConverter.GetBytes(data.Length - 2)[0];
        msg_builder(data, 0);
    }

    //missing: 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x3A, 0x3B, 0x3C
    //not implemented: 0x20, 0x50, 0x51
    #endregion

    #region wraper_methods

    #endregion

}
