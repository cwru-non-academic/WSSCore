/// <summary>
/// Strongly-typed core configuration model persisted in JSON.
/// Exposes the maximum number of WSS devices and the firmware version string.
/// </summary>

namespace Wss.CoreModule
{
    public sealed class CoreConfig
    {
        /// <summary>Maximum number of WSS devices supported by this app.</summary>
        public int maxWSS { get; set; } = 1;
        /// <summary>Firmware version string (e.g., "H03", "J03").</summary>
        public string firmware { get; set; } = "H03";

        /// <summary>
        /// When true, use per-WSS amplitude curve parameters from this config.
        /// When false, use built-in defaults (72mA) or device-reported mode (10mA) mapping.
        /// </summary>
        public bool useConfigAmpCurves { get; set; } = false;

        /// <summary>
        /// Per-WSS amplitude curve parameters. Index 0 => Wss1, 1 => Wss2, 2 => Wss3.
        /// Defaults to three entries matching the 72mA built-in curve.
        /// </summary>
        public AmpCurveParams[] ampCurves { get; set; } = new AmpCurveParams[]
        {
            AmpCurveParams.Default72mA(),
            AmpCurveParams.Default72mA(),
            AmpCurveParams.Default72mA()
        };
    }

    /// <summary>
    /// Piecewise amplitude curve parameters used to map mA to 0..255.
    /// mA &lt;= LowThreshold: out = pow(mA / LowConst, ExpPower) + 1
    /// mA &gt;  LowThreshold: out = ((mA + LinearOffset) / LinearSlope) + 1
    /// </summary>
    public sealed class AmpCurveParams
    {
        /// <summary>Boundary between the two segments in mA.</summary>
        public double LowThreshold { get; set; }
        /// <summary>Low-range divisor constant (C) for the power segment.</summary>
        public double LowConst { get; set; }
        /// <summary>Exponent used in the low-range power segment (already reciprocal, e.g., 1/1.5466).</summary>
        public double ExpPower { get; set; }
        /// <summary>Offset used in the high-range linear segment.</summary>
        public double LinearOffset { get; set; }
        /// <summary>Slope divisor used in the high-range linear segment.</summary>
        public double LinearSlope { get; set; }

        /// <summary>
        /// Returns the built-in 72 mA piecewise curve parameters that match the historical mapping
        /// used by the core (power segment below 4 mA and linear segment above).
        /// </summary>
        public static AmpCurveParams Default72mA() => new AmpCurveParams
        {
            LowThreshold = 4.0,
            LowConst = 0.0522,
            ExpPower = 1.0 / 1.5466,
            LinearOffset = 1.7045,
            LinearSlope = 0.3396
        };
    }
}
