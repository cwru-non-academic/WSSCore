namespace Wss.Testing
{
    /// <summary>
    /// Identifies why an emulator input frame was rejected.
    /// </summary>
    public enum WssProtocolErrorKind
    {
        /// <summary>The frame checksum did not match its decoded contents.</summary>
        InvalidChecksum,

        /// <summary>The frame did not contain a complete WSS request structure.</summary>
        MalformedFrame
    }
}
