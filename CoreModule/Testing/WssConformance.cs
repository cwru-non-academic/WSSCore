using System;
using System.Collections.Generic;

namespace Wss.Testing
{
    /// <summary>
    /// Provides read-only access to observations recorded by one emulated WSS device.
    /// </summary>
    public sealed class WssConformance : IWssConformance
    {
        private readonly EmulatedWssDevice _device;

        /// <summary>
        /// Creates a conformance view associated with an emulated device.
        /// </summary>
        /// <param name="device">Device whose observations are exposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="device"/> is null.</exception>
        public WssConformance(EmulatedWssDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <inheritdoc/>
        public IReadOnlyList<WssMessageObservation> MessageHistory => _device.GetMessageHistorySnapshot();

        /// <inheritdoc/>
        public IReadOnlyList<WssProtocolError> ProtocolErrors => _device.GetProtocolErrorsSnapshot();
    }
}
