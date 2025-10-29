using System;

/// <summary>
/// Typed snapshot of ModuleQuery settings with a safe decoder from raw data-only bytes.
/// Data layout (data-only slice):
///   [Serial (1)]
///   [DataStart 24-bit: High (1), Mid (1), Low (1)]
///   [BatteryThreshold (2, BE)]
///   [BatteryCheckPeriod (2, BE)]
///   [ImpedanceThreshold (2, BE)]
///   [HwConfig (1, flags)]
///   [FingerswitchConfig (1)]
///   [IPD (1, microseconds, device-specific granularity)]
///   [ParameterStep (1): bit7=1 => PW, 0 => PA; bits 0..6 = step size]
///   [PaLimit (1)]
///   [PwLimit (1)]
/// Missing/short fields are tolerated; the decoder sets IsPartial = true.
/// </summary>
public sealed class ModuleSettings
{
    [Flags]
    public enum HwConfigFlags : byte
    {
        Default         = 0, // 72 mA PA mode
        TenMilliAmpPA   = 1 << 0, // 10 mA PA mode
        TenMsPulseGuard = 1 << 1, // 10 ms pulse guard
        Future4         = 1 << 2,
        Future8         = 1 << 3,
        Future16        = 1 << 4,
        Future32        = 1 << 5,
        Future64        = 1 << 6
    }

    public enum FingerswitchConfig : byte
    {
        None  = 0,
        FSw   = 1,
        Burst = 2,
        Future = 3 // 3..255 reserved/future
    }

    public enum ParameterStepKind : byte { PA, PW }

    public byte SerialNumber { get; private set; }
    public uint DataStart24 { get; private set; }
    public ushort BatteryThreshold { get; private set; }
    public ushort BatteryCheckPeriod { get; private set; }
    public ushort ImpedanceThreshold { get; private set; }
    public HwConfigFlags HwConfig { get; private set; }
    public FingerswitchConfig FswConfig { get; private set; }
    public int IpdUs { get; private set; }
    public ParameterStepKind StepKind { get; private set; }
    public int StepSize { get; private set; }
    public int PaLimit { get; private set; }
    public int PwLimit { get; private set; }
    public bool IsPartial { get; private set; }
    public bool ProbeSupported { get; private set; }

    // Convenience: map HW config to a curve key for config lookup (e.g., "10mA" vs "72mA")
    public string AmpCurveKey => (HwConfig & HwConfigFlags.TenMilliAmpPA) != 0 ? "10mA" : "72mA";

    public string ToSummaryString()
    {
        string hw = HwConfig.ToString();
        string fsw = FswConfig.ToString();
        string step = $"{StepKind} (+/- {StepSize})";
        string partial = IsPartial ? " (partial)" : "";
        return $"HW={hw}, FSW={fsw}, IPD={IpdUs}us, PA Limit={PaLimit}, PW Limit={PwLimit}, " +
               $"BattThresh={BatteryThreshold}, BattCheck={BatteryCheckPeriod}, ImpThresh={ImpedanceThreshold}, " +
               $"DataStart=0x{DataStart24:X6}, Serial={SerialNumber}{partial}";
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out ModuleSettings settings)
    {
        // helpers cannot capture ref-like values; make them static and pass args
        static byte ReadU8(ReadOnlySpan<byte> d, ref int i, ref bool partial)
        {
            if ((uint)i < (uint)d.Length) return d[i++];
            partial = true; return 0;
        }
        static ushort ReadU16BE(ReadOnlySpan<byte> d, ref int i, ref bool partial)
        {
            byte hi = ReadU8(d, ref i, ref partial);
            byte lo = ReadU8(d, ref i, ref partial);
            return (ushort)((hi << 8) | lo);
        }
        static uint ReadU24BE(ReadOnlySpan<byte> d, ref int i, ref bool partial)
        {
            byte b2 = ReadU8(d, ref i, ref partial); // high
            byte b1 = ReadU8(d, ref i, ref partial); // mid
            byte b0 = ReadU8(d, ref i, ref partial); // low
            return (uint)((b2 << 16) | (b1 << 8) | b0);
        }

        var s = new ModuleSettings();
        bool partial = false;
        int i = 0;

        s.SerialNumber        = ReadU8(data, ref i, ref partial);
        s.DataStart24         = ReadU24BE(data, ref i, ref partial);
        s.BatteryThreshold    = ReadU16BE(data, ref i, ref partial);
        s.BatteryCheckPeriod  = ReadU16BE(data, ref i, ref partial);
        s.ImpedanceThreshold  = ReadU16BE(data, ref i, ref partial);
        s.HwConfig            = (HwConfigFlags)ReadU8(data, ref i, ref partial);
        s.FswConfig           = (FingerswitchConfig)ReadU8(data, ref i, ref partial);
        s.IpdUs               = ReadU8(data, ref i, ref partial);

        byte step             = ReadU8(data, ref i, ref partial);
        s.StepKind            = (step & 0x80) != 0 ? ParameterStepKind.PW : ParameterStepKind.PA;
        s.StepSize            = step & 0x7F;

        s.PaLimit             = ReadU8(data, ref i, ref partial);
        s.PwLimit             = ReadU8(data, ref i, ref partial);

        s.IsPartial           = partial;
        s.ProbeSupported      = true;
        settings = s;
        return !partial;
    }


    /// <summary>
    /// Creates a default 72mA profile when ModuleQuery is not supported or data is unavailable.
    /// Fields are set to safe defaults and IsPartial=true.
    /// </summary>
    public static ModuleSettings CreateDefault72mA(bool probeSupported)
    {
        var s = new ModuleSettings
        {
            SerialNumber = 0,
            DataStart24 = 0,
            BatteryThreshold = 0,
            BatteryCheckPeriod = 0,
            ImpedanceThreshold = 0,
            HwConfig = HwConfigFlags.Default,
            FswConfig = FingerswitchConfig.None,
            IpdUs = 0,
            StepKind = ParameterStepKind.PA,
            StepSize = 0,
            PaLimit = 0,
            PwLimit = 0,
            IsPartial = true,
            ProbeSupported = probeSupported
        };
        return s;
    }
}
