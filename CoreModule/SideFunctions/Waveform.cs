namespace Wss.CoreModule
{
    /// <summary>
    /// Waveform container holding cathodic and anodic shape arrays and a computed total area.
    /// </summary>
    [System.Serializable]
    public class Waveform
    {
        /// <summary>
        /// Cathodic (primary) phase samples.
        /// </summary>
        public int[] catShape;
        /// <summary>
        /// Anodic (recharge) phase samples.
        /// </summary>
        public int[] anShape;
        /// <summary>
        /// Total area under the waveform used for computing recharge amplitude.
        /// </summary>
        public float area;
        /// <summary>Creates a new <see cref="Waveform"/> from cathodic/anodic arrays and an area value.</summary>
        /// <param name="catWaveform">Cathodic shape samples.</param>
        /// <param name="anodicWaveform">Anodic shape samples.</param>
        /// <param name="area">Computed total area.</param>
        public Waveform(int[] catWaveform, int[] anodicWaveform, float area) 
        {
            catShape = catWaveform;
            anShape = anodicWaveform;
            this.area = area;
        }
    }
}
