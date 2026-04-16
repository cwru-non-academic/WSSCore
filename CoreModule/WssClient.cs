using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
namespace Wss.CoreModule
{
    /// <summary>
    /// Low-level protocol client. Frames commands using an <see cref="IFrameCodec"/>,
    /// sends them over an <see cref="ITransport"/>, and correlates responses by
    /// <c>(target,msgId)</c>.
    /// </summary>
    public sealed class WssClient : IDisposable
    {
        private readonly ITransport _transport;
        private readonly IFrameCodec _codec;
        private readonly byte _sender;
        private readonly WSSVersionHandler _versionHandler;
        private readonly bool _ownsTransport;
        private readonly TimeSpan _responseTimeout;
        private readonly byte _broadcastTarget;
        private readonly byte[] _wssTargets;

        private readonly ConcurrentDictionary<(byte target, byte msgId), ConcurrentQueue<TaskCompletionSource<byte[]>>> _pending
            = new ConcurrentDictionary<(byte target, byte msgId), ConcurrentQueue<TaskCompletionSource<byte[]>>>();

        // Stores last ModuleQuery data-only bytes per device target
        private readonly ConcurrentDictionary<WssTarget, byte[]> _moduleQueryData
            = new ConcurrentDictionary<WssTarget, byte[]>();

        /// <summary>
        /// Indicates whether the WSS client connection has been started.
        /// </summary>
        public bool Started { get; private set; }

        /// <summary>
        /// Creates a low-level protocol client over a caller-provided transport and frame codec.
        /// </summary>
        /// <param name="transport">Underlying byte transport (for example, serial or BLE).</param>
        /// <param name="codec">Frame codec used for escaping/deframing and checksum validation.</param>
        /// <param name="versionHandler">Firmware version handler used for version-gated commands.</param>
        /// <param name="options">Client options including sender address, transport ownership, reply timeout, and logical target address mapping.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/>, <paramref name="codec"/>, <paramref name="versionHandler"/>, or <paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the configured response timeout is not positive.</exception>
        /// <remarks>
        /// <see cref="ITransport.BytesReceived"/> may be raised on a background thread depending on the transport implementation.
        /// Reply correlation primarily matches responses by <c>(target,msgId)</c>, with additional handling for
        /// protocol error replies and firmware paths that answer <see cref="WSSMessageIDs.ModuleQuery"/> using
        /// <see cref="WSSMessageIDs.RequestAnalog"/>.
        /// </remarks>
        public WssClient(ITransport transport, IFrameCodec codec, WSSVersionHandler versionHandler, WssClientOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _versionHandler = versionHandler ?? throw new ArgumentNullException(nameof(versionHandler));
            if (options.ResponseTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(WssClientOptions.ResponseTimeout), "Response timeout must be positive.");

            _sender = options.Sender;
            _ownsTransport = options.OwnsTransport;
            _responseTimeout = options.ResponseTimeout;
            _broadcastTarget = options.BroadcastTarget;
            _wssTargets = NormalizeWssTargets(options.WssTargets);
            _transport.BytesReceived += OnBytes;
        }

        /// <summary>
        /// Establishes a connection to the specified WSS device.
        /// </summary>
        /// <param name="ct">The cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous connection operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if already connected to the target.</exception>
        /// <exception cref="System.IO.IOException">Thrown if the connection attempt fails.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        public Task ConnectAsync(CancellationToken ct = default) => _transport.ConnectAsync(ct);

        /// <summary>
        /// Closes the active connection to the specified WSS device.
        /// </summary>
        /// <param name="ct">The cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous disconnection operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if not connected to the target.</exception>
        /// <exception cref="System.IO.IOException">Thrown if the disconnection fails due to a transport error.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        public Task DisconnectAsync(CancellationToken ct = default) => _transport.DisconnectAsync(ct);

        /// <summary>
        /// Builds payload [cmd][len][data...], frames it, sends it, and awaits one response
        /// for the (target, msgId) key.
        /// </summary>
        /// <param name="msgId">The WSS message ID to send.</param>
        /// <param name="target">Target device to send to.</param>
        /// <param name="ct">Cancellation token to cancel the send/wait.</param>
        /// <param name="dataBytes">Payload data bytes (excluding cmd/len).</param>
        /// <returns>Response as a string, after processing.</returns>
        private Task<string> SendCmdAsync(WSSMessageIDs msgId, WssTarget target, CancellationToken ct, params byte[] dataBytes)
        {
            if (dataBytes == null) dataBytes = Array.Empty<byte>();
            if (dataBytes.Length > 255)
                throw new ArgumentException("Payload too long. Max 255 bytes for length field.", nameof(dataBytes));

            // Construct [cmd][len][data...]
            var payload = new byte[2 + dataBytes.Length];
            payload[0] = (byte)msgId;
            payload[1] = (byte)dataBytes.Length; // length = bytes after cmd+len
            if (dataBytes.Length > 0)
                Buffer.BlockCopy(dataBytes, 0, payload, 2, dataBytes.Length);

            var wireTarget = ResolveWireTarget(target);
            var framed = _codec.Frame(_sender, wireTarget, payload);
            var key = (wireTarget, (byte)msgId);

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            // FIFO queue for multiple pending requests with same key
            var queue = _pending.GetOrAdd(key, _ => new ConcurrentQueue<TaskCompletionSource<byte[]>>());
            queue.Enqueue(tcs);

            return SendAwaitOneAsync(tcs, framed, key, ct);
        }

        /// <summary>
        /// Sends a previously framed command and awaits a single response for the specified (target,msgId) key.
        /// </summary>
        /// <param name="tcs">Completion source to complete when a matching frame arrives.</param>
        /// <param name="framed">Fully framed bytes to send (already includes cmd/len and escaping).</param>
        /// <param name="key">Key used to correlate the response: (target,msgId).</param>
        /// <param name="ct">Cancellation token. The configured response timeout is also applied.</param>
        /// <returns>Processed response string.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="framed"/> is null.</exception>
        /// <exception cref="System.IO.IOException">Thrown if the underlying connection encounters an error.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        private async Task<string> SendAwaitOneAsync(
            TaskCompletionSource<byte[]> tcs,
            byte[] framed,
            (byte target, byte msgId) key,
            CancellationToken ct)
        {
            await _transport.SendAsync(framed, ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_responseTimeout);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var frame = await tcs.Task.ConfigureAwait(false);
                return ProcessFrame(frame);
            }
        }

        /// <summary>
        /// Fire-and-forget send: builds [cmd][len][data...] and sends without awaiting a reply.
        /// Use for one-way streaming opcodes (e.g., 0x30..0x33).
        /// </summary>
        private Task SendFireAndForgetAsync(WSSMessageIDs msgId, WssTarget target, CancellationToken ct = default, params byte[] dataBytes)
        {
            if (dataBytes == null) dataBytes = Array.Empty<byte>();
            if (dataBytes.Length > 255)
                throw new ArgumentException("Payload too long. Max 255 bytes for length field.", nameof(dataBytes));

            // Construct [cmd][len][data...]
            var payload = new byte[2 + dataBytes.Length];
            payload[0] = (byte)msgId;
            payload[1] = (byte)dataBytes.Length;
            if (dataBytes.Length > 0)
                Buffer.BlockCopy(dataBytes, 0, payload, 2, dataBytes.Length);

            var framed = _codec.Frame(_sender, ResolveWireTarget(target), payload);
            return _transport.SendAsync(framed, ct);
        }

        /// <summary>
        /// Handles incoming byte chunks, deframes them into complete frames, and completes the oldest waiter
        /// in the FIFO queue for the corresponding (target,msgId) key.
        /// </summary>
        /// <param name="chunk">Raw bytes received from the transport.</param>
        private void OnBytes(byte[] chunk)
        {
            foreach (var frame in _codec.Deframe(chunk))
            {
                if (frame == null || frame.Length < 3) continue;

                // 1) Errors first: route by offending command
                if (frame[2] == 0x05 && frame.Length >= 6)
                {
                    var key = (frame[0], frame[5]); // sender, offendingCmd
                    bool handled = TryCompletePending(key, frame);
                    if (!handled && frame[4] == 0x02)
                        handled = TryCompleteWrongReceiverFallback(frame);

                    if (!handled)
                    {
                        Log.Warn($"Unpaired error: {ProcessFrame(frame)} ({FormatFrame(frame)})");
                    }
                    continue;
                }

                // 2) Exceptions to paired replies
                if (frame[2] == 0x02 && frame.Length >= 16)//querry msg
                {
                    //force to serach using querry msg id and the reply is unpaired
                    var key = (frame[0], (byte)WSSMessageIDs.ModuleQuery); // sender, querryCommandID
                    if (!TryCompletePending(key, frame))
                    {
                        Log.Warn("Unpaired reply: " + FormatFrame(frame));
                    }
                    continue;
                }

                // 3) Normal replies: route by msgId
                var normalKey = (frame[0], frame[2]); // sender, msgId
                if (!TryCompletePending(normalKey, frame))
                {
                    Log.Info("Unpaired reply: " + FormatFrame(frame));
                }
            }
        }

        private bool TryCompletePending((byte target, byte msgId) key, byte[] frame)
        {
            if (!_pending.TryGetValue(key, out var queue))
                return false;

            while (queue.TryDequeue(out var waiter))
            {
                if (waiter.TrySetResult(frame))
                {
                    if (queue.IsEmpty)
                        _pending.TryRemove(key, out _);
                    return true;
                }
            }

            if (queue.IsEmpty)
                _pending.TryRemove(key, out _);

            return false;
        }

        private bool TryCompleteWrongReceiverFallback(byte[] frame)
        {
            var offendingCmd = frame[5];
            var candidates = _pending
                .Where(kvp => kvp.Key.msgId == offendingCmd && kvp.Value.Count == 1)
                .Select(kvp => kvp.Key)
                .ToList();

            if (candidates.Count != 1)
                return false;

            return TryCompletePending(candidates[0], frame);
        }

        private static string FormatFrame(byte[] frame)
            => BitConverter.ToString(frame).Replace("-", " ").ToLowerInvariant();


        /// <summary>
        /// Processes a complete frame of data received from the WSS connection.
        /// it will extract information from replies and just foward message if it is only a mirror reply.
        /// </summary>
        /// <param name="frame">The received frame of bytes.</param>
        private string ProcessFrame(byte[] frame)
        {
            if (frame.Length < 6)
            {
                return $"Error: return msg length is too small, size {frame.Length}";
            }
            switch (frame[2])
            {
                case (byte)WSSMessageIDs.Error:
                    var code = frame.ElementAtOrDefault(4);
                    var cmd = frame.ElementAtOrDefault(5);
                    var replySender = frame.ElementAtOrDefault(0);
                    var replyTarget = frame.ElementAtOrDefault(1);
                    var text = code switch
                    {
                        0x00 => "No Error",
                        0x01 => "Comms Error",
                        0x02 => "Wrong Receiver",
                        0x03 => "Checksum Error",
                        0x04 => "Command Error",
                        0x05 => "Parameters Error",
                        0x06 => "No Setup",
                        0x07 => "Incompatible",
                        0x0B => "No Schedule",
                        0x0C => "No Event",
                        0x0D => "No Memory",
                        0x0E => "Not Event",
                        0x0F => "Delay Too Long",
                        0x10 => "Wrong Schedule",
                        0x11 => "Duration Too Short",
                        0x12 => "Fault",
                        0x15 => "Delay Too Short",
                        0x16 => "Event Exists",
                        0x17 => "Schedule Exists",
                        0x18 => "No Config",
                        0x19 => "Bad State",
                        0x1A => "Not Shape",
                        0x20 => "Wrong Address",
                        0x30 => "Stream Params",
                        0x31 => "Stream Address",
                        0x81 => "Output Invalid",
                        _ => "Unknown"
                    };
                    if (code == 0x02)
                        return $"Error: {text} in Command: {cmd:x} (reply sender: {replySender:x2}, reply target: {replyTarget:x2})";

                    return $"Error: {text} in Command: {cmd:x}";
                case (byte)WSSMessageIDs.ModuleQuery:
                case (byte)WSSMessageIDs.RequestAnalog://Module Queery replies with request analog msg id
                    // Cache data-only slice for this sender (device). Payload is [msgId][len][data...]
                    try
                    {
                        var senderAddr = frame[0];
                        var target = ResolveLogicalTarget(senderAddr);
                        int declaredLen = frame[3];
                        int available = Math.Max(0, frame.Length - 4);
                        int dataLen = Math.Min(declaredLen, available);
                        var data = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
                        if (dataLen > 0)
                            Buffer.BlockCopy(frame, 4, data, 0, dataLen);
                        _moduleQueryData[target] = data;
                    }
                    catch
                    {
                        Log.Warn("Could not match queery data to object using defaults" + BitConverter.ToString(frame).Replace("-", " ").ToLowerInvariant());
                    }
                    return BitConverter.ToString(frame).Replace("-", " ").ToLowerInvariant();
                case (byte)WSSMessageIDs.StimulationSwitch:
                    if (frame[4] == 0x01) { Started = true; return "Start Acknowledged"; }
                    else if (frame[4] == 0x00) { Started = false; return "Stop Acknowledged"; }
                    return $"Unexpected data {frame[4]} in reply for cmd {WSSMessageIDs.StimulationSwitch}";
            }
            return BitConverter.ToString(frame).Replace("-", " ").ToLowerInvariant();
        }

        /// <summary>
        /// Gets the last cached ModuleQuery data (data-only slice) for a target, if present.
        /// </summary>
        /// <param name="target">Target device that produced the ModuleQuery reply.</param>
        /// <param name="data">The cached data bytes for the most recent reply from <paramref name="target"/>.</param>
        /// <returns>True if cached data exists; otherwise false.</returns>
        /// <remarks>
        /// The returned array is the cached in-memory instance for the latest reply, not a defensive copy.
        /// </remarks>
        public bool TryGetModuleQueryData(WssTarget target, out byte[] data)
            => _moduleQueryData.TryGetValue(target, out data);

        private byte ResolveWireTarget(WssTarget target)
        {
            return target switch
            {
                WssTarget.Broadcast => _broadcastTarget,
                WssTarget.Wss1 => _wssTargets[0],
                WssTarget.Wss2 => _wssTargets[1],
                WssTarget.Wss3 => _wssTargets[2],
                _ => _wssTargets[0]
            };
        }

        private WssTarget ResolveLogicalTarget(byte wireTarget)
        {
            if (wireTarget == _broadcastTarget) return WssTarget.Broadcast;
            if (wireTarget == _wssTargets[0]) return WssTarget.Wss1;
            if (wireTarget == _wssTargets[1]) return WssTarget.Wss2;
            if (wireTarget == _wssTargets[2]) return WssTarget.Wss3;
            return WssTarget.Wss1;
        }

        private static byte[] NormalizeWssTargets(byte[] values)
        {
            var defaults = new byte[] { 0x81, 0x82, 0x83 };
            if (values == null || values.Length == 0)
                return defaults;

            var normalized = new byte[3];
            for (int i = 0; i < normalized.Length; i++)
                normalized[i] = i < values.Length ? values[i] : defaults[i];

            return normalized;
        }

        /// <summary>
        /// Indicates whether ModuleQuery is available in this firmware (version J or later).
        /// </summary>
        public bool IsModuleQueryAvailable()
        {
            try { return _versionHandler.GetVersion() >= WSSVersionHandler.SupportedVersions.J03; }
            catch { return false; }
        }

        /// <summary>
        /// Unsubscribes from inbound bytes and releases client state.
        /// </summary>
        /// <remarks>
        /// Disposes the underlying transport only when this instance was constructed with
        /// <c>ownsTransport</c> set to <see langword="true"/>. This method does not call
        /// <see cref="DisconnectAsync"/> automatically.
        /// </remarks>
        public void Dispose()
        {
            _transport.BytesReceived -= OnBytes;
            if (_ownsTransport)
            {
                _transport.Dispose();
            }
            _moduleQueryData.Clear();
        }

        // Helper used to extract a byte from an int, ensuring it's within 0-255 range.
        // Throws ArgumentOutOfRangeException if not.
        private static byte ToByteValidated(int v, int maxInclusive, string paramName = "value")
            => ToByteInRange(v, maxInclusive, paramName);

        // Helper used to extract a byte from an int, ensuring itaithin 0-65535 range.
        // Throws ArgumentOutOfRangeException if not.
        private static (byte hi, byte lo) ToU16Validated(int v, int maxInclusive, string paramName = "value")
            => ToU16InRange(v, maxInclusive, paramName);

        // 8-bit path (byte) with max
        private static byte ToByteInRange(int v, int maxInclusive, string paramName = "value")
        {
            if ((uint)v > (uint)Math.Min(maxInclusive, 255))
                throw new ArgumentOutOfRangeException(paramName, $"Value must be 0..{Math.Min(maxInclusive, 255)}.");
            return (byte)v;
        }

        // 16-bit big-endian split with max
        private static (byte hi, byte lo) ToU16InRange(int v, int maxInclusive, string paramName = "value")
        {
            if ((uint)v > (uint)Math.Min(maxInclusive, 65535))
                throw new ArgumentOutOfRangeException(paramName, $"Value must be 0..{Math.Min(maxInclusive, 65535)}.");
            return ((byte)(v >> 8), (byte)(v & 0xFF));
        }

        #region stimulation_base_methods
        /// <summary>
        /// Sends the "start stimulation".
        /// 0x01 = Start Ack expected.
        /// Broadcast is acceptable for a system-wide switch.
        /// </summary>
        /// <param name="target">Target device (often Broadcast).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> StartStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.StimulationSwitch, target, ct, 0x03);

        /// <summary>
        /// Sends the "stop stimulation" switch (opcode 0x0B).
        /// 0x00 = Stop Ack expected.
        /// Broadcast is acceptable for a system-wide switch.
        /// </summary>
        /// <param name="target">Target device (often Broadcast).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> StopStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.StimulationSwitch, target, ct, 0x04);

        /// <summary>Resets the microcontroller (0x04).</summary>
        public Task<string> Reset(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.Reset, target, ct);

        /// <summary>
        /// Echo round-trip (0x07). Two opaque data bytes are echoed back.
        /// Untested, and reading from WSS devices is not yet implemented.
        /// </summary>
        public Task<string> Echo(int echoData1, int echoData2, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            // Validate and convert ints into bytes
            var b1 = ToByteValidated(echoData1, (int)WSSLimits.oneByte, nameof(echoData1));
            var b2 = ToByteValidated(echoData2, (int)WSSLimits.oneByte, nameof(echoData2));

            return SendCmdAsync(WSSMessageIDs.Echo, target, ct, b1, b2);
        }

        /// <summary>
        /// Requests specific battery and impedance data from the WSS device.
        /// TODO Not implemented in firmware, and reading from WSS devices is not yet implemented.
        /// </summary>
        public Task<string> RequestAnalog(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        => SendCmdAsync(WSSMessageIDs.RequestAnalog, target, ct, 0x01);

        /// <summary>
        /// Clears groups of resources (0x40): 0=All, 1=Events, 2=Schedules, 3=Contacts.
        /// </summary>
        /// <param name="configIndex">Clear events(1), schedules(2), contacts(3), all(0).</param>
        /// <param name="target">Target device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> Clear(int configIndex, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            // Validate and convert ints into bytes
            var b1 = ToByteValidated(configIndex, (int)WSSLimits.clearIndex, nameof(configIndex));

            return SendCmdAsync(WSSMessageIDs.Clear, target, ct, b1);
        }

        /// <summary>
        /// Gets settings or information for the target WSS (opcode 0x01).
        /// Version-gated: on firmware that supports ModuleQuery (version J+), this sends the
        /// command and the reply is cached as data-only bytes per target. On older firmware,
        /// no command is sent; for index 1 (settings array) a default 16-byte payload is
        /// seeded in the cache so higher layers can decode default unit settings.
        /// </summary>
        /// <param name="moduleIndex">0 = serial number, 1 = settings array.</param>
        /// <param name="target">Target device.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <remarks>
        /// - When supported (J+): this method transmits the request and, upon reply, ProcessFrame
        ///   caches the data-only slice under the sender's target address. Use
        ///   <see cref="TryGetModuleQueryData(WssTarget, out byte[])"/> to retrieve the last snapshot.
        /// - When not supported: this method does not transmit. For <c>moduleIndex == 1</c>, it seeds
        ///   a default 16-byte zero array into the per-target cache, allowing the core to decode a
        ///   default profile. The returned string indicates that defaults are being used.
        /// </remarks>
        /// <returns>
        /// A human-readable string summary. Cache side-effects are described above.
        /// </returns>
        public Task<string> ModuleQuery(int moduleIndex, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            // Gate by firmware support: if unsupported, avoid sending an invalid command
            if (_versionHandler == null || !_versionHandler.IsModuleQueryAvailable())
            {
                // For settings array (index 1), seed a default 16-byte data-only payload (zeros)
                // so core decoders can construct a default (e.g., 72mA) profile.
                if (moduleIndex == 1)
                {
                    try { _moduleQueryData[target] = new byte[16]; } catch { }
                }
                return Task.FromResult("Querry: module query not supported on this firmware; using defaults");
            }
            // Validate and convert ints into bytes
            var b1 = ToByteValidated(moduleIndex, (int)WSSLimits.moduleIndex, nameof(moduleIndex));

            return SendCmdAsync(WSSMessageIDs.ModuleQuery, target, ct, b1);
        }

        /// <summary>
        /// Sends a request to the WSS target for a specific configuration type.
        /// This is a general-purpose request that can target output configs, events,
        /// schedules, and their various sub-configurations.
        /// Untested, and reading from WSS devices is not yet implemented.
        /// </summary>
        /// <param name="command">
        /// Integer code that selects which configuration to request:
        /// 0 = Output Configuration List
        /// 1 = Output Configuration details
        /// 2 = Event List
        /// 3 = Basic Event configuration
        /// 4 = Event output configuration
        /// 5 = Event stimulation configuration
        /// 6 = Event shape configuration
        /// 7 = Event burst configuration
        /// 8 = Schedule basic configuration
        /// 9 = Schedule listing
        /// </param>
        /// <param name="id">ID associated with the requested configuration (0–255).</param>
        /// <param name="target">Target WSS device to query (default: Wss1).</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>Processed response string from the WSS target.</returns>
        public Task<string> RequestConfigs(int command, int id, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            // Map the command code to the two selector bytes used in the WSS protocol
            var selectors = command switch
            {
                0 => (0x00, 0x00), // Output Configuration List
                1 => (0x00, 0x01), // Output Configuration details
                2 => (0x01, 0x00), // Event List
                3 => (0x01, 0x01), // Basic Event configuration
                4 => (0x01, 0x02), // Event output configuration
                5 => (0x01, 0x03), // Event stimulation configuration
                6 => (0x01, 0x04), // Event shape configuration
                7 => (0x01, 0x05), // Event burst configuration
                8 => (0x02, 0x00), // Schedule basic configuration
                9 => (0x02, 0x01), // Schedule listing
                _ => (0x00, 0x00), // Default: Output Configuration List
            };

            // Convert id safely to a byte with range validation
            byte validatedId = ToByteValidated(id, (int)WSSLimits.oneByte, nameof(id));

            // SendCmdAsync will build [cmd][len][selectors][id] and handle framing/queue/await
            return SendCmdAsync(WSSMessageIDs.RequestConfig, target, ct, (byte)selectors.Item1, (byte)selectors.Item2, validatedId);
        }

        /// <summary>
        /// Creates a contact configuration for the stimulator.
        /// Defines the sources and sinks for stimulation and recharge phases.
        /// </summary>
        /// <param name="definition">
        /// Contact configuration values to encode and send, including the contact id, four-output stimulation setup,
        /// four-output recharge setup, and optional LED bitmask.
        /// </param>
        /// <param name="target">The WSS target device to send the configuration to.</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>Processed response string from the WSS target.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when either setup array in <paramref name="definition"/> is null or does not contain exactly four entries.</exception>
        public Task<string> CreateContactConfig(ContactConfigDefinition definition, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (definition.StimSetup == null || definition.StimSetup.Length != 4)
                throw new ArgumentException("Stim setup must be an array of exactly 4 integers.", nameof(definition.StimSetup));

            if (definition.RechargeSetup == null || definition.RechargeSetup.Length != 4)
                throw new ArgumentException("Recharge setup must be an array of exactly 4 integers.", nameof(definition.RechargeSetup));

            // Convert ID safely
            byte validatedId = ToByteValidated(definition.ContactId, (int)WSSLimits.oneByte, nameof(definition.ContactId));

            // Reverse order so index 0 (closest to switch) becomes index 3 internally
            // made so the order matches other methods in which array index 0 is closest to the switch
            var stimReversed = System.Linq.Enumerable.Reverse(definition.StimSetup).ToArray();
            var rechargeReversed = System.Linq.Enumerable.Reverse(definition.RechargeSetup).ToArray(); 

            // Encode stim and recharge setups into single bytes (bit-packed 2 bits per output)
            byte stimByte = EncodeContactSetup(stimReversed);
            byte rechargeByte = EncodeContactSetup(rechargeReversed);

            if (definition.Leds.HasValue)
            {
                if (_versionHandler.IsLEDSettingsAvailable())
                {
                    // Validate and convert LEDs int into a byte
                    byte ledByte = ToByteValidated(definition.Leds.Value, (int)WSSLimits.LEDs, nameof(definition.Leds));
                    return SendCmdAsync(WSSMessageIDs.CreateContactConfig, target, ct, validatedId, stimByte, rechargeByte, ledByte);
                }
                else
                {
                    Log.Warn($"Firmware version does not support LEDs. Ignoring LED value {definition.Leds.Value}.");
                }
            }

            return SendCmdAsync(WSSMessageIDs.CreateContactConfig, target, ct, validatedId, stimByte, rechargeByte);
        }

        /// <summary>
        /// Encodes a contact configuration array into a single byte.
        /// </summary>
        /// <param name="setup">
        /// Array of 4 integers (0 = not used, 1 = source, 2 = sink),
        /// index 0 = farthest from switch, index 3 = closest to switch.
        /// </param>
        /// <returns>
        /// Encoded byte: 2 bits per contact, 00 = not used, 10 = source, 11 = sink.
        /// </returns>
        private static byte EncodeContactSetup(int[] setup)
        {
            if (setup.Length != 4)
                throw new ArgumentException("Setup must have exactly 4 elements.");

            byte result = 0;
            for (int i = 0; i < 4; i++)
            {
                int value = setup[i] switch
                {
                    0 => 0b00,
                    1 => 0b10,
                    2 => 0b11,
                    _ => throw new ArgumentOutOfRangeException(nameof(setup), "Values must be 0, 1, or 2.")
                };
                result |= (byte)(value << ((3 - i) * 2));
            }
            return result;
        }

        /// <summary>
        /// Deletes an existing contact configuration on the WSS device by its ID.
        /// </summary>
        /// <param name="contactId">The contact configuration ID to delete (0–255).</param>
        /// <param name="target">The WSS target device to send the request to (default: Wss1).</param>
        /// <param name="ct">Optional cancellation token to cancel the operation.</param>
        /// <returns>Processed response string from the WSS target.</returns>
        public Task<string> DeleteContactConfig(int contactId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            // Convert ID safely to a byte (throws if out of range)
            byte validatedId = ToByteValidated(contactId, (int)WSSLimits.oneByte, nameof(contactId));
            return SendCmdAsync(WSSMessageIDs.DeleteContactConfig, target, ct, validatedId);
        }

        /// <summary>
        /// Creates an event on the WSS device from a grouped request model.
        /// The request supports the same payload combinations as the previous overload set:
        /// basic, shape-only, amplitudes plus pulse widths, or shapes plus amplitudes plus pulse widths.
        /// </summary>
        /// <param name="request">Grouped event request to encode and validate.</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>Processed response string from the WSS target.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the request mixes incompatible combinations, such as only one shape ID,
        /// only one amplitude array, or pulse widths without both amplitude arrays.
        /// </exception>
        public Task<string> CreateEvent(CreateEventRequest request, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            bool hasShapes = request.StandardShapeId.HasValue || request.RechargeShapeId.HasValue;
            bool hasAmpPayload = request.StandardAmplitudes != null || request.RechargeAmplitudes != null || request.PulseWidths != null;

            if (request.StandardShapeId.HasValue != request.RechargeShapeId.HasValue)
                throw new ArgumentException("Both shape IDs must be provided together.", nameof(request));

            if ((request.StandardAmplitudes != null) != (request.RechargeAmplitudes != null))
                throw new ArgumentException("Both amplitude arrays must be provided together.", nameof(request));

            if ((request.StandardAmplitudes != null || request.RechargeAmplitudes != null) && request.PulseWidths == null)
                throw new ArgumentException("Pulse widths are required when amplitude arrays are provided.", nameof(request));

            if (request.PulseWidths != null && (request.StandardAmplitudes == null || request.RechargeAmplitudes == null))
                throw new ArgumentException("Amplitude arrays are required when pulse widths are provided.", nameof(request));

            byte ev = ToByteValidated(request.EventId, (int)WSSLimits.oneByte, nameof(request.EventId));
            byte dly = ToByteValidated(request.DelayMs, (int)WSSLimits.oneByte, nameof(request.DelayMs));
            byte oc = ToByteValidated(request.ContactConfigId, (int)WSSLimits.oneByte, nameof(request.ContactConfigId));

            if (!hasShapes && !hasAmpPayload)
            {
                return SendCmdAsync(WSSMessageIDs.CreateEvent, target, ct, ev, dly, oc);
            }

            if (hasShapes && !hasAmpPayload)
            {
                byte std = ToByteValidated(request.StandardShapeId.Value, (int)WSSLimits.shape, nameof(request.StandardShapeId));
                byte rech = ToByteValidated(request.RechargeShapeId.Value, (int)WSSLimits.shape, nameof(request.RechargeShapeId));
                return SendCmdAsync(WSSMessageIDs.CreateEvent, target, ct, ev, dly, oc, std, rech);
            }

            if (request.StandardAmplitudes.Length != 4)
                throw new ArgumentException("Standard amplitudes must have 4 elements.", nameof(request.StandardAmplitudes));

            if (request.RechargeAmplitudes.Length != 4)
                throw new ArgumentException("Recharge amplitudes must have 4 elements.", nameof(request.RechargeAmplitudes));

            var s0 = ToByteValidated(request.StandardAmplitudes[0], (int)WSSLimits.oneByte, "StandardAmplitudes[0]");
            var s1 = ToByteValidated(request.StandardAmplitudes[1], (int)WSSLimits.oneByte, "StandardAmplitudes[1]");
            var s2 = ToByteValidated(request.StandardAmplitudes[2], (int)WSSLimits.oneByte, "StandardAmplitudes[2]");
            var s3 = ToByteValidated(request.StandardAmplitudes[3], (int)WSSLimits.oneByte, "StandardAmplitudes[3]");
            var r0 = ToByteValidated(request.RechargeAmplitudes[0], (int)WSSLimits.oneByte, "RechargeAmplitudes[0]");
            var r1 = ToByteValidated(request.RechargeAmplitudes[1], (int)WSSLimits.oneByte, "RechargeAmplitudes[1]");
            var r2 = ToByteValidated(request.RechargeAmplitudes[2], (int)WSSLimits.oneByte, "RechargeAmplitudes[2]");
            var r3 = ToByteValidated(request.RechargeAmplitudes[3], (int)WSSLimits.oneByte, "RechargeAmplitudes[3]");

            int stdPw = request.PulseWidths.StandardPw;
            int ipd = request.PulseWidths.Ipd;
            int rechPw = request.PulseWidths.RechargePw;
            bool wide = stdPw > (int)WSSLimits.oneByte || ipd > (int)WSSLimits.oneByte || rechPw > (int)WSSLimits.oneByte;

            if (!hasShapes)
            {
                if (wide)
                {
                    (byte sh, byte sl) = ToU16Validated(stdPw, (int)WSSLimits.pulseWidthLong, nameof(request.PulseWidths.StandardPw));
                    (byte ih, byte il) = ToU16Validated(ipd, (int)WSSLimits.pulseWidthLong, nameof(request.PulseWidths.Ipd));
                    (byte rh, byte rl) = ToU16Validated(rechPw, (int)WSSLimits.pulseWidthLong, nameof(request.PulseWidths.RechargePw));
                    return SendCmdAsync(WSSMessageIDs.CreateEvent, target, ct, ev, dly, oc, s0, s1, s2, s3, r0, r1, r2, r3, sh, sl, ih, il, rh, rl);
                }

                byte std8 = ToByteValidated(stdPw, (int)WSSLimits.oneByte, nameof(request.PulseWidths.StandardPw));
                byte ipd8 = ToByteValidated(ipd, (int)WSSLimits.oneByte, nameof(request.PulseWidths.Ipd));
                byte rech8 = ToByteValidated(rechPw, (int)WSSLimits.oneByte, nameof(request.PulseWidths.RechargePw));
                return SendCmdAsync(WSSMessageIDs.CreateEvent, target, ct, ev, dly, oc, s0, s1, s2, s3, r0, r1, r2, r3, std8, ipd8, rech8);
            }

            byte stdS = ToByteValidated(request.StandardShapeId.Value, (int)WSSLimits.shape, nameof(request.StandardShapeId));
            byte recS = ToByteValidated(request.RechargeShapeId.Value, (int)WSSLimits.shape, nameof(request.RechargeShapeId));
            if (wide)
            {
                (byte sh, byte sl) = ToU16Validated(stdPw, (int)WSSLimits.pulseWidthLong, nameof(request.PulseWidths.StandardPw));
                (byte ih, byte il) = ToU16Validated(ipd, (int)WSSLimits.pulseWidthLong, nameof(request.PulseWidths.Ipd));
                (byte rh, byte rl) = ToU16Validated(rechPw, (int)WSSLimits.pulseWidthLong, nameof(request.PulseWidths.RechargePw));
                return SendCmdAsync(WSSMessageIDs.CreateEvent, target, ct, ev, dly, oc, s0, s1, s2, s3, r0, r1, r2, r3, sh, sl, ih, il, rh, rl, stdS, recS);
            }

            byte stdShape8 = ToByteValidated(stdPw, (int)WSSLimits.oneByte, nameof(request.PulseWidths.StandardPw));
            byte ipdShape8 = ToByteValidated(ipd, (int)WSSLimits.oneByte, nameof(request.PulseWidths.Ipd));
            byte rechShape8 = ToByteValidated(rechPw, (int)WSSLimits.oneByte, nameof(request.PulseWidths.RechargePw));
            return SendCmdAsync(WSSMessageIDs.CreateEvent, target, ct, ev, dly, oc, s0, s1, s2, s3, r0, r1, r2, r3, stdShape8, ipdShape8, rechShape8, stdS, recS);
        }

        /// <summary>
        /// Deletes an existing event on the WSS device by its ID.
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> DeleteEvent(int eventId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            return SendCmdAsync(WSSMessageIDs.DeleteEvent, target, ct, ev);
        }

        /// <summary>
        /// Adds an event to a schedule.
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="scheduleId">Schedule ID (0–255).</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> AddEventToSchedule(int eventId, int scheduleId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte sc = ToByteValidated(scheduleId, (int)WSSLimits.oneByte, nameof(scheduleId));
            return SendCmdAsync(WSSMessageIDs.AddEventToSchedule, target, ct, ev, sc);
        }

        /// <summary>
        /// Removes an event from its assigned schedule.
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> DeleteEventFromSchedule(int eventId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            return SendCmdAsync(WSSMessageIDs.RemoveEventFromSchedule, target, ct, ev);
        }

        /// <summary>
        /// Moves an event to a different schedule with a new delay offset.
        /// Fails on-device if <paramref name="delayMs"/> exceeds the schedule period.
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="scheduleId">Destination schedule ID (0–255).</param>
        /// <param name="delayMs">Delay from schedule start in milliseconds (0–255 in this variant).</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> MoveEventToSchedule(int eventId, int scheduleId, int delayMs, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte sc = ToByteValidated(scheduleId, (int)WSSLimits.oneByte, nameof(scheduleId));
            byte dly = ToByteValidated(delayMs, (int)WSSLimits.oneByte, nameof(delayMs));
            return SendCmdAsync(WSSMessageIDs.MoveEventToSchedule, target, ct, ev, sc, dly);
        }

        /// <summary>
        /// Changes an event's output/contact configuration by ID.
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="contactConfigId">Output/contact configuration ID (0–255).</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventContactConfig(int eventId, int contactConfigId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte oc = ToByteValidated(contactConfigId, (int)WSSLimits.oneByte, nameof(contactConfigId));
            // Payload: [eventId][subcmd for contactConfig=0x01][outConfigId]
            return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x01, oc);
        }

        /// <summary>
        /// Changes an event's pulse widths: [standardPW, rechargePW, IPD] (μs).
        /// Uses 8-bit fields if all ≤ oneByte; otherwise encodes all three as 16-bit big-endian.
        /// Wire order matches firmware: [stdPW, IPD, rechPW].
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="pw">Three PW values: [standardPW, rechargePW, IPD] in microseconds.</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventPw(int eventId, int[] pw, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (pw == null || pw.Length != 3) throw new ArgumentException("pw must be [standardPW, rechargePW, IPD].", nameof(pw));

            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));

            bool wide = pw.Any(v => v > (int)WSSLimits.oneByte);

            if (wide)
            {
                // Big-endian pairs in order: stdPW, IPD, rechPW
                (byte sh, byte sl) = ToU16Validated(pw[0], (int)WSSLimits.pulseWidthLong, "standardPW");
                (byte ih, byte il) = ToU16Validated(pw[2], (int)WSSLimits.IPD, "IPD");
                (byte rh, byte rl) = ToU16Validated(pw[1], (int)WSSLimits.pulseWidthLong, "rechargePW");
                // Payload: [eventId][subcmd for pw=0x02][stdH stdL][ipdH ipdL][rechH rechL]
                return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x02, sh, sl, ih, il, rh, rl);
            }
            else
            {
                // 8-bit fields in order: stdPW, IPD, rechPW
                byte std8 = ToByteValidated(pw[0], (int)WSSLimits.oneByte, "standardPW");
                byte ipd8 = ToByteValidated(pw[2], (int)WSSLimits.oneByte, "IPD");
                byte rech8 = ToByteValidated(pw[1], (int)WSSLimits.oneByte, "rechargePW");
                // Payload: [eventId][subcmd for pw=0x02][std][ipd][rech]
                return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x02, std8, ipd8, rech8);
            }
        }

        /// <summary>
        /// Changes an event's amplitude configuration for standard and recharge phases.
        /// </summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="standardAmp">Four amplitudes for the standard (stim) phase, 0–255 each.</param>
        /// <param name="rechargeAmp">Four amplitudes for the recharge phase, 0–255 each.</param>
        /// <param name="target">Target WSS device (default: Wss1).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventAmp(int eventId, int[] standardAmp, int[] rechargeAmp, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (standardAmp == null || standardAmp.Length != 4) throw new ArgumentException("standardAmp must have 4 elements.", nameof(standardAmp));
            if (rechargeAmp == null || rechargeAmp.Length != 4) throw new ArgumentException("rechargeAmp must have 4 elements.", nameof(rechargeAmp));

            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));

            byte s0 = ToByteValidated(standardAmp[0], (int)WSSLimits.oneByte, "standardAmp[0]");
            byte s1 = ToByteValidated(standardAmp[1], (int)WSSLimits.oneByte, "standardAmp[1]");
            byte s2 = ToByteValidated(standardAmp[2], (int)WSSLimits.oneByte, "standardAmp[2]");
            byte s3 = ToByteValidated(standardAmp[3], (int)WSSLimits.oneByte, "standardAmp[3]");
            byte r0 = ToByteValidated(rechargeAmp[0], (int)WSSLimits.oneByte, "rechargeAmp[0]");
            byte r1 = ToByteValidated(rechargeAmp[1], (int)WSSLimits.oneByte, "rechargeAmp[1]");
            byte r2 = ToByteValidated(rechargeAmp[2], (int)WSSLimits.oneByte, "rechargeAmp[2]");
            byte r3 = ToByteValidated(rechargeAmp[3], (int)WSSLimits.oneByte, "rechargeAmp[3]");

            // Payload: [eventId][subcmd for amp=0x04][s0 s1 s2 s3 r0 r1 r2 r3]
            return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x04, s0, s1, s2, s3, r0, r1, r2, r3);
        }

        /// <summary>Changes an event's shape configuration.</summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="standardShapeId">
        /// Shape ID used during the standard (stim) phase:
        /// 0=Rectangular (default), 1=Rectangular (DAC), 2=Sine, 3=Gaussian,
        /// 4=Exponential Increase, 5=Exponential Decrease,
        /// 6=Linear Increase, 7=Linear Decrease,
        /// 10=Trapezoid (setup required),
        /// 11=User Program 1 (setup required),
        /// 12=User Program 2 (setup required),
        /// 13=User Program 3 (setup required).
        /// </param>
        /// <param name="rechargeShapeId">
        /// Shape ID used during the recharge phase (same mapping as <paramref name="standardShapeId"/>).
        /// </param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventShape(int eventId, int standardShapeId, int rechargeShapeId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte stdS = ToByteValidated(standardShapeId, (int)WSSLimits.shape, nameof(standardShapeId));
            byte recS = ToByteValidated(rechargeShapeId, (int)WSSLimits.shape, nameof(rechargeShapeId));
            // Payload: [eventId][subcmd shape=0x05][standardShapeId][rechargeShapeId]
            return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x05, stdS, recS);
        }

        /// <summary>Changes an event's delay (ms from schedule start).</summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="delayMs">Delay in milliseconds (0–255 in this variant).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventDelay(int eventId, int delayMs, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte dly = ToByteValidated(delayMs, (int)WSSLimits.oneByte, nameof(delayMs));
            // Payload: [eventId][subcmd delay=0x06][delay]
            return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x06, dly);
        }

        /// <summary>Changes an event's ratio (allowed: 1, 2, 4, 8).</summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="ratio">Ratio value (must be one of 1, 2, 4, 8).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventRatio(int eventId, int ratio, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (ratio != 1 && ratio != 2 && ratio != 4 && ratio != 8)
                throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be one of {1,2,4,8}.");
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte ra = ToByteValidated(ratio, (int)WSSLimits.oneByte, nameof(ratio));
            // Payload: [eventId][subcmd ratio=0x07][ratio]
            return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x07, ra);
        }

        /// <summary>Changes an event's ratio (allowed: 1, 2, 4, 8).</summary>
        /// <param name="eventId">Event ID (0–255).</param>
        /// <param name="enable">enable bit (0 or 1).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditEventEnableBit(int eventId, int enable, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte ev = ToByteValidated(eventId, (int)WSSLimits.oneByte, nameof(eventId));
            byte en = ToByteValidated(enable, (int)WSSLimits.enable, nameof(enable));
            // Payload: [eventId][subcmd ratio=0x07][ratio]
            return SendCmdAsync(WSSMessageIDs.EditEventConfig, target, ct, ev, 0x08, en);
        }

        /// <summary>Creates a schedule from a grouped definition model.</summary>
        /// <param name="definition">Schedule ID, duration, and sync group values.</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Processed response string from the WSS target.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a value in <paramref name="definition"/> falls outside the protocol-supported range.</exception>
        public Task<string> CreateSchedule(ScheduleDefinition definition, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            byte sc = ToByteValidated(definition.ScheduleId, (int)WSSLimits.oneByte, nameof(definition.ScheduleId));
            (byte dh, byte dl) = ToU16Validated(definition.DurationMs, (int)WSSLimits.Frequency, nameof(definition.DurationMs)); // original used 16-bit BE
            byte sg = ToByteValidated(definition.SyncSignal, (int)WSSLimits.oneByte, nameof(definition.SyncSignal));
            // Payload: [scheduleId][durationHi][durationLo][syncSignal]
            return SendCmdAsync(WSSMessageIDs.CreateSchedule, target, ct, sc, dh, dl, sg);
        }

        /// <summary>Deletes a schedule by ID.</summary>
        /// <param name="scheduleId">Schedule ID (0–255).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> DeleteSchedule(int scheduleId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte sc = ToByteValidated(scheduleId, (int)WSSLimits.oneByte, nameof(scheduleId));
            return SendCmdAsync(WSSMessageIDs.DeleteSchedule, target, ct, sc);
        }

        /// <summary>Sends a sync signal; starts schedules in the group from READY to ACTIVE.</summary>
        /// <param name="syncSignal">Sync group ID (0–255).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> SyncGroup(int syncSignal, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte sg = ToByteValidated(syncSignal, (int)WSSLimits.oneByte, nameof(syncSignal));
            return SendCmdAsync(WSSMessageIDs.SyncGroup, target, ct, sg);
        }

        /// <summary>Changes a sync group's state: READY=1, ACTIVE=0, SUSPEND=2.</summary>
        /// <param name="syncSignal">Sync group ID (0–255).</param>
        /// <param name="state">State (0=ACTIVE, 1=READY, 2=SUSPEND).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> ChangeGroupState(int syncSignal, int state, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte sg = ToByteValidated(syncSignal, (int)WSSLimits.oneByte, nameof(syncSignal));
            byte st = ToByteValidated(state, (int)WSSLimits.state, nameof(state));
            return SendCmdAsync(WSSMessageIDs.ChangeGroupState, target, ct, sg, st);
        }

        /// <summary>Changes a schedule's state.</summary>
        /// <param name="scheduleId">Schedule ID (0–255).</param>
        /// <param name="state">State (0=ACTIVE, 1=READY, 2=SUSPEND).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> ChangeScheduleState(int scheduleId, int state, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte sc = ToByteValidated(scheduleId, (int)WSSLimits.oneByte, nameof(scheduleId));
            byte st = ToByteValidated(state, (int)WSSLimits.state, nameof(state));
            // Command 0x4E subcmd 0x01: [scheduleId][state]
            return SendCmdAsync(WSSMessageIDs.ChangeScheduleConfig, target, ct, 0x01, sc, st);
        }

        /// <summary>Changes a schedule's sync group.</summary>
        /// <param name="scheduleId">Schedule ID (0–255).</param>
        /// <param name="syncSignal">Sync group ID (0–255).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> ChangeScheduleGroup(int scheduleId, int syncSignal, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte sc = ToByteValidated(scheduleId, (int)WSSLimits.oneByte, nameof(scheduleId));
            byte sg = ToByteValidated(syncSignal, (int)WSSLimits.oneByte, nameof(syncSignal));
            // Command 0x4E subcmd 0x02: [scheduleId][syncSignal]
            return SendCmdAsync(WSSMessageIDs.ChangeScheduleConfig, target, ct, 0x02, sc, sg);
        }

        /// <summary>Changes a schedule's duration (8-bit variant). For 16-bit, prefer recreating the schedule.</summary>
        /// <param name="scheduleId">Schedule ID (0–255).</param>
        /// <param name="durationMs">Duration in milliseconds (0–255 in this subcommand TODO expand this command to the full range od=f 0 to 1000).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> ChangeScheduleDuration(int scheduleId, int durationMs, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte sc = ToByteValidated(scheduleId, (int)WSSLimits.oneByte, nameof(scheduleId));
            byte du = ToByteValidated(durationMs, (int)WSSLimits.oneByte, nameof(durationMs));
            // Command 0x4E subcmd 0x03: [scheduleId][duration]
            return SendCmdAsync(WSSMessageIDs.ChangeScheduleConfig, target, ct, 0x03, sc, du);
        }

        /// <summary>Resets all schedules.</summary>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> ResetSchedules(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            return SendCmdAsync(WSSMessageIDs.ResetSchedule, target, ct);
        }

        /// <summary>
        /// Uploads a custom waveform chunk (8 samples) into a slot.
        /// Call this 4 times (msgNumber 0..3) to send all 32 samples.
        /// Each sample is encoded as 16-bit big-endian.
        /// </summary>
        /// <param name="slot">Waveform slot (0–255).</param>
        /// <param name="waveformChunk8">Exactly 8 sample values for this chunk (0..2000 each).</param>
        /// <param name="msgNumber">Chunk index (0..3) — send four chunks to fill 32 samples.</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> SetCustomWaveform(int slot, int[] waveformChunk8, int msgNumber, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            if (waveformChunk8 == null || waveformChunk8.Length != 8)
                throw new ArgumentException("waveformChunk8 must have exactly 8 elements.", nameof(waveformChunk8));

            byte sl = ToByteValidated(slot, (int)WSSLimits.oneByte, nameof(slot));
            byte msg = ToByteValidated(msgNumber, (int)WSSLimits.customWaveformChunks, nameof(msgNumber));

            // Build payload: [slot][msgNumber][s0H s0L][s1H s1L]...[s7H s7L]
            var bytes = new List<byte>(2 + 8 * 2) { sl, msg };
            for (int i = 0; i < 8; i++)
            {
                (byte hi, byte lo) = ToU16Validated(waveformChunk8[i], (int)WSSLimits.customWaveformMaxAmp, $"waveformChunk8[{i}]");
                bytes.Add(hi); bytes.Add(lo);
            }

            return SendCmdAsync(WSSMessageIDs.CustomWaveform, target, ct, bytes.ToArray());
        }

        /// <summary>
        /// Writes a single settings byte at <paramref name="address"/> to <paramref name="value"/>.
        /// </summary>
        /// <param name="address">Settings address (0–255).</param>
        /// <param name="value">Value to write (0–255).</param>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EditSettings(int address, int value, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
        {
            byte addr = ToByteValidated(address, (int)WSSLimits.oneByte, nameof(address));
            byte val = ToByteValidated(value, (int)WSSLimits.oneByte, nameof(value));
            // Command 0x09 subcmd 0x03: [address][value]
            return SendCmdAsync(WSSMessageIDs.BoardCommands, target, ct, 0x03, addr, val);
        }

        /// <summary>
        /// Loads settings from FRAM into the active board settings.
        /// </summary>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> PopulateBoardSettings(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.BoardCommands, target, ct, 0x0A);

        /// <summary>
        /// Saves current board settings into FRAM.
        /// </summary>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> PopulateFramSettings(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.BoardCommands, target, ct, 0x0B);

        /// <summary>
        /// Erases device log data.
        /// </summary>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> EraseLog(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.BoardCommands, target, ct, 0x04);

        /// <summary>
        /// Requests device log data (raw read mode on device side)
        /// Untested TODO.
        /// </summary>
        /// <param name="target">Target WSS device.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task<string> GetLog(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
            => SendCmdAsync(WSSMessageIDs.BoardCommands, target, ct, 0x05);

        /// <summary>
        /// Streams changes to PA (amplitude), PW (pulse width), and IPI (period) for up to three schedules in one shot.
        /// The grouped request may omit at most one of the three data groups.
        /// Uses opcodes:
        ///   0x30 = PA+PW+IPI,  0x31 = PA+PW (no IPI),  0x32 = PA+IPI (no PW),  0x33 = PW+IPI (no PA).
        /// All elements are 0..255 in this streaming variant (one byte each).
        /// </summary>
        /// <param name="request">Grouped PA/PW/IPI request.</param>
        /// <param name="target">Target WSS; streaming commonly uses Broadcast.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the outbound streaming send.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="NotSupportedException">Thrown when more than one data group is omitted.</exception>
        /// <exception cref="ArgumentException">Thrown when a supplied group does not contain exactly 3 elements.</exception>
        public Task StreamChange(StreamChangeRequest request, WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            int[] pa = request.PulseAmplitudes;
            int[] pw = request.PulseWidths;
            int[] ipi = request.InterPulseIntervals;
            int nulls = (pa == null ? 1 : 0) + (pw == null ? 1 : 0) + (ipi == null ? 1 : 0);
            if (nulls > 1) throw new NotSupportedException("Protocol supports omitting at most one of {PA, PW, IPI}.");

            // Validate non-null arrays are exactly 3 elements and 0..255 each
            static (byte a0, byte a1, byte a2) V3(int[] arr, string name)
            {
                if (arr == null) return (0, 0, 0);
                if (arr.Length != 3) throw new ArgumentException($"{name} must have exactly 3 elements.", name);
                return (
                    ToByteValidated(arr[0], (int)WSSLimits.oneByte, $"{name}[0]"),
                    ToByteValidated(arr[1], (int)WSSLimits.oneByte, $"{name}[1]"),
                    ToByteValidated(arr[2], (int)WSSLimits.oneByte, $"{name}[2]")
                );
            }

            var (pa0, pa1, pa2) = V3(pa, nameof(pa));
            var (pw0, pw1, pw2) = V3(pw, nameof(pw));
            var (i0, i1, i2) = V3(ipi, nameof(ipi));

            // Choose opcode and lay out payload exactly like your originals
            if (pa == null)
                // 0x33: [00 00 00][PW0 PW1 PW2][IPI0 IPI1 IPI2]
                return SendFireAndForgetAsync(WSSMessageIDs.StreamChangeNoPA, target, ct, 0x00, 0x00, 0x00, pw0, pw1, pw2, i0, i1, i2);
            else if (pw == null)
                // 0x32: [PA0 PA1 PA2][00 00 00][IPI0 IPI1 IPI2]
                return SendFireAndForgetAsync(WSSMessageIDs.StreamChangeNoPW, target, ct, pa0, pa1, pa2, 0x00, 0x00, 0x00, i0, i1, i2);
            else if (ipi == null)
                // 0x31: [PA0 PA1 PA2][PW0 PW1 PW2][00 00 00]
                return SendFireAndForgetAsync(WSSMessageIDs.StreamChangeNoIPI, target, ct, pa0, pa1, pa2, pw0, pw1, pw2, 0x00, 0x00, 0x00);
            else
                // 0x30: [PA0 PA1 PA2][PW0 PW1 PW2][IPI0 IPI1 IPI2]
                return SendFireAndForgetAsync(WSSMessageIDs.StreamChangeAll, target, ct, pa0, pa1, pa2, pw0, pw1, pw2, i0, i1, i2);
        }

        /// <summary>
        /// Convenience: streams zeros to PA, PW, and IPI (effectively stopping stimulation).
        /// Uses opcode 0x31 (PA+PW, no IPI) with trailing zeros to match the original layout.
        /// </summary>
        /// <param name="target">Target WSS (default Broadcast).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task ZeroOutStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
        {
            // Matches your original: cmd 0x31 and nine zeroes
            return SendFireAndForgetAsync(WSSMessageIDs.StreamChangeNoIPI, target, ct,
                0x00, 0x00, 0x00,   // PA0..2
                0x00, 0x00, 0x00,   // PW0..2
                0x00, 0x00, 0x00);  // IPI0..2 (placeholders)
        }
        //0x9B possibel duplicate of 0x49 with shape cmd
        //TODO 0x9C, 0x9E, 0xE0, 0xE1, 0x64, 0xE65, 0x66, 0x6A, 0x6B, 0x6C, 0x60, 0x61, 0x62, 0x66, 0x6D
        //TODO Missing waiting on explanation: 0x9A, 0x3A, 0x3B, 0x3C, 0xFF
        //not implemented: 0x20, 0x50, 0x51
        #endregion
    }
}
