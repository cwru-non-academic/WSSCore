using System;

/// <summary>
/// Strongly-typed snapshot of settings returned by the ModuleQuery command.
/// Includes a safe decoder for the data-only payload and helpers for selecting
/// amplitude mapping behavior (e.g., 10 mA vs 72 mA based on HW flags).
/// </summary>
public sealed class ModuleSettings
{
    [Flags]
    /// <summary>
    /// Hardware configuration flags reported by the device. Each bit defines an
    /// independent setting; the on-wire value is the sum of set bits. In particular,
    /// bit 1 (value 2) indicates 10 mA PA mode and bit 2 (value 4) indicates 10 ms pulse guard.
    /// </summary>
    public enum HwConfigFlags : byte
    {
        Default         = 0,          // no special flags
        TenMilliAmpPA   = 1 << 1,     // bit1 => 10 mA PA mode (value 2) otheriwise 72mA PA
        TenMsPulseGuard = 1 << 2,     // bit2 => 10 ms pulse guard (value 4)
        Future8         = 1 << 3,     // future use (value 8)
        Future16        = 1 << 4,     // future use (value 16)
        Future32        = 1 << 5,     // future use (value 32)
        Future64        = 1 << 6      // future use (value 64)
    }

    /// <summary>
    /// Fingerswitch operating mode, when present.
    /// </summary>
    public enum FingerswitchConfig : byte
    {
        None  = 0,
        FSw   = 1,
        Burst = 2,
        Future = 3 // 3..255 reserved/future
    }

    /// <summary>Which parameter the front-panel step affects (PA or PW).</summary>
    public enum ParameterStepKind : byte { PA, PW }

    /// <summary>Device serial number (one byte in this query).</summary>
    public byte SerialNumber { get; private set; }
    /// <summary>Start pointer for device log data (24-bit big-endian value).</summary>
    public uint DataStart24 { get; private set; }
    /// <summary>Low-battery threshold (raw units).</summary>
    public ushort BatteryThreshold { get; private set; }
    /// <summary>Battery check timer period (raw units).</summary>
    public ushort BatteryCheckPeriod { get; private set; }
    /// <summary>High-impedance threshold (raw units).</summary>
    public ushort ImpedanceThreshold { get; private set; }
    /// <summary>Hardware configuration flags (bitfield).</summary>
    public HwConfigFlags HwConfig { get; private set; }
    /// <summary>Fingerswitch configuration value.</summary>
    public FingerswitchConfig FswConfig { get; private set; }
    /// <summary>Inter-phase delay in microseconds (one byte in this query).</summary>
    public int IpdUs { get; private set; }
    /// <summary>Whether the step size applies to PA or PW.</summary>
    public ParameterStepKind StepKind { get; private set; }
    /// <summary>Step size magnitude (lower 7 bits of the Step byte).</summary>
    public int StepSize { get; private set; }
    /// <summary>PA limit (raw device units; typically correlates with max mA capability).</summary>
    public int PaLimit { get; private set; }
    /// <summary>PW limit (raw device units).</summary>
    public int PwLimit { get; private set; }
    /// <summary>True when the payload was shorter than expected; some fields may be defaulted.</summary>
    public bool IsPartial { get; private set; }
    /// <summary>True when the values originated from a real device probe, not defaults.</summary>
    public bool ProbeSupported { get; private set; }

    /// <summary>
    /// Convenience selector for amplitude curves. Returns "10mA" when the
    /// <see cref="HwConfigFlags.TenMilliAmpPA"/> bit is set; otherwise "72mA".
    /// </summary>
    public string AmpCurveKey => (HwConfig & HwConfigFlags.TenMilliAmpPA) != 0 ? "10mA" : "72mA";

    /// <summary>
    /// Builds a human-readable one-line summary of key fields for logs and diagnostics.
    /// </summary>
    /// <returns>Summary string including HW flags, FSW config, IPD, limits, thresholds, and serial.</returns>
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

    /// <summary>
    /// Attempts to decode a ModuleQuery settings payload (data-only slice) into a <see cref="ModuleSettings"/> instance.
    /// </summary>
    /// <param name="data">Data-only bytes in the expected order (length ≥ 16 recommended).</param>
    /// <param name="settings">Decoded settings snapshot (always set; may have <see cref="IsPartial"/>=true).</param>
    /// <returns>
    /// True when all expected fields were present (not partial). False indicates a short payload, but a
    /// best-effort decode is still returned in <paramref name="settings"/>.
    /// </returns>
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
    /// Creates a default (synthetic) 72 mA profile for cases where ModuleQuery is not supported or
    /// data is unavailable. Fields are set to conservative defaults and <see cref="IsPartial"/> is true.
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
