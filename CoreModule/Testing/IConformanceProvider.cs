namespace Wss.Testing
{
    /// <summary>
    /// Exposes conformance observations for transports that support protocol inspection.
    /// </summary>
    public interface IConformanceProvider
    {
        /// <summary>
        /// Gets the conformance data associated with this transport.
        /// </summary>
        IWssConformance Conformance { get; }
    }
}
