
[System.Serializable]
public class Waveform
{
    public int[] catShape;
    public int[] anShape;
    public float area;
    public Waveform(int[] catWaveform, int[] anodicWaveform, float area) 
    {
        catShape = catWaveform;
        anShape = anodicWaveform;
        this.area = area;
    }
}
