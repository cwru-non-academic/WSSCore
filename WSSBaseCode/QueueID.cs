using System.Collections;
using System.Collections.Generic;

public class QueueID
{
    public int id;
    public byte msgID;

    public QueueID(int id, byte msgID)
    {
        this.id = id;
        this.msgID = msgID;
    }

    public override bool Equals(object obj)
    {
        var item = obj as QueueID;
        if (item == null)
        {
            return false;
        }
        if(item.id == id && item.msgID == msgID) 
        { 
            return true;
        }
        return false;
    }
}
