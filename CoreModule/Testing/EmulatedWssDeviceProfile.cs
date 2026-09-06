namespace Wss.Testing
{
    /// <summary>
    /// Defines the fixed capability values reported by the deterministic WSS emulator.
    /// </summary>
    public sealed class EmulatedWssDeviceProfile
    {
        private EmulatedWssDeviceProfile()
        {
        }

        /// <summary>Gets the shared deterministic emulator profile.</summary>
        public static EmulatedWssDeviceProfile Default { get; } = new EmulatedWssDeviceProfile();

        /// <summary>Gets the target address expected by initialization conformance validation.</summary>
        public byte InitializationTarget => 0x81;

        /// <summary>Gets the module type value reported in the first ModuleQuery settings byte.</summary>
        public byte ModuleType => 0x01;

        /// <summary>Gets the inter-phase delay in microseconds.</summary>
        public byte IpdUs => 50;

        /// <summary>Gets the pulse-amplitude step size.</summary>
        public byte PaStep => 1;

        /// <summary>Gets whether the profile reports 10 mA pulse-amplitude capability.</summary>
        public bool SupportsTenMilliAmp => true;

        /// <summary>Gets whether the profile reports pulse-guard capability.</summary>
        public bool SupportsPulseGuard => true;

        /// <summary>Gets the reported pulse-amplitude limit.</summary>
        public byte PaLimit => 10;

        /// <summary>Gets the reported pulse-width limit.</summary>
        public byte PwLimit => 255;
    }
}
