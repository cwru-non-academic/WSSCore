namespace Wss.CoreModule
{
    /// <summary>
    /// Read-only view of core configuration values loaded from a JSON config file.
    /// </summary>
    public interface ICoreConfig
    {
        /// <summary>
        /// Maximum number of WSS devices supported by this configuration.
        /// </summary>
        int MaxWss { get; }
        /// <summary>
        /// Firmware version string (e.g., "H03", "J03").
        /// </summary>
        string Firmware { get; }
    }
}
