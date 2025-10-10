using System;

public class WaveformBuilder
{
    private const int shapeSize = 32;
    private const float maxAmp = 2000.0f;
    private int[] catShape = new int[shapeSize];
    private int[] anShape = new int[shapeSize];
    private Waveform wave;
    private float area = 0;
    public WaveformBuilder(int[] catWaveform)
    {
        catShape = catWaveform;
        anShape = anodicWaveMaker(areaCalculation(catWaveform));
        wave = new Waveform(catShape, anShape, area);
    }
    public WaveformBuilder(Waveform wave)
    {
        this.wave = wave;
        wave.catShape = catShape;
        wave.anShape = anShape;
        wave.area = area;
    }

    private float areaCalculation(int[] catWaveform)
    {
        int prevY = 0;
        for (int i = 0; i < catWaveform.Length; i++)
        {
            //calculate triangle difference area 
            area += (1.0f / (shapeSize * 2)) * ((catWaveform[i] - prevY) / maxAmp); //base/2 * height
            //calculate square area based on previus y
            area += (1.0f / shapeSize) * (prevY / maxAmp); //base * height
            prevY = catWaveform[i];
        }
        return area;
    }

    private int[] anodicWaveMaker(float area)
    {
        float rechargeHeight = area *maxAmp;
        int[] anodicWaveform = new int[shapeSize];
        Array.Fill(anodicWaveform, (int)rechargeHeight);
        return anodicWaveform;
    }

    public int[] getAnodicShapeArray()
    {
        return anShape;
    }

    public int[] getCatShapeArray()
    {
        return catShape;
    }
    public float getArea()
    {
        return area;
    }
    public Waveform getWave()
    {
        return wave;
    }
}

