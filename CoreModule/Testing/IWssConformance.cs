using System.Collections.Generic;

namespace Wss.Testing
{
    /// <summary>
    /// Provides read-only protocol observations collected by an emulated WSS device.
    /// </summary>
    public interface IWssConformance
    {
        /// <summary>
        /// Gets a snapshot of valid decoded request messages in receive order.
        /// </summary>
        IReadOnlyList<WssMessageObservation> MessageHistory { get; }

        /// <summary>
        /// Gets a snapshot of rejected protocol input in receive order.
        /// </summary>
        IReadOnlyList<WssProtocolError> ProtocolErrors { get; }
    }
}
