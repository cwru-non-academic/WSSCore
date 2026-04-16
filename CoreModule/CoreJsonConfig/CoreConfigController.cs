using System;
using System.Globalization;
using System.IO;
using System.Threading;
namespace Wss.CoreModule
{
    /// <summary>
    /// Controls loading, validating, reading, and writing a configuration JSON file.
    /// Thread-safe. Ensures a valid default config exists on disk.
    /// </summary>
    public sealed class CoreConfigController : ICoreConfig
    {
        private readonly object _sync = new object();
        private CoreConfig _config;
        private bool _jsonLoaded;

        /// <summary>Resolved file path to the configuration JSON.</summary>
        public string _configPath { get; private set; }

        /// <summary>
        /// Initializes a controller that reads/writes "stimConfig.json" in the current directory.
        /// </summary>
        public CoreConfigController() : this(Path.Combine(Environment.CurrentDirectory, "stimConfig.json")) { }

        /// <summary>
        /// Initializes a controller pointing to a custom path.
        /// If a directory path is provided, "stimConfig.json" is created inside it.
        /// </summary>
        /// <param name="path">Absolute or relative file path, or a directory path.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null, empty, or whitespace.</exception>
        public CoreConfigController(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Invalid config path.", nameof(path));

            // If a directory was provided, append the default filename.
            if (Directory.Exists(path)
                || path.EndsWith(Path.DirectorySeparatorChar.ToString())
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                _configPath = Path.Combine(path, "stimConfig.json");
            }
            else
            {
                _configPath = path;
            }
            _config = JsonReader.LoadObject(_configPath, new CoreConfig());
            Volatile.Write(ref _jsonLoaded, true);
        }

        /// <summary>
        /// Indicates whether the JSON configuration has been loaded into memory.
        /// </summary>
        public bool IsLoaded => Volatile.Read(ref _jsonLoaded);

        /// <summary>
        /// Loads the configuration from disk. Creates and saves a default config if missing or invalid.
        /// Safe to call multiple times.
        /// </summary>
        public void LoadJson() {
            lock (_sync)
            {
                _config = JsonReader.LoadObject(_configPath, new CoreConfig());
                Volatile.Write(ref _jsonLoaded, true);
            }
        } 

        /// <summary>
        /// Loads the core configuration from disk at the specified <paramref name="path"/>.
        /// If <paramref name="path"/> is a directory, the file name <c>stimConfig.json</c> is appended.
        /// If the file does not exist or is invalid, a default configuration is created and saved.
        /// Safe to call multiple times; the operation is thread-safe.
        /// </summary>
        /// <param name="path">
        /// Absolute or relative file path, or a directory path ending with a path separator.
        /// </param>
        public void LoadJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Invalid config path.", nameof(path));

            lock (_sync)
            {
                // Treat trailing separator or existing directory as a directory input
                bool isDir = Directory.Exists(path)
                            || path.EndsWith(Path.DirectorySeparatorChar.ToString())
                            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString());

                _configPath = isDir ? Path.Combine(path, "stimConfig.json") : path;

                _config = JsonReader.LoadObject(_configPath, new CoreConfig());
                Volatile.Write(ref _jsonLoaded, true);
            }
        }

        /// <summary>
        /// Threads safe, persists the current configuration to disk.
        /// No-op if not loaded yet.
        /// </summary>
        public void SaveJson()
        {
            lock (_sync)
            {
                if (!IsLoaded) return;
                JsonReader.SaveObject(_configPath, _config);
            }
        }

        /// <summary>
        /// Gets max number of WSS supported by this app.
        /// Thread safe get.
        /// </summary>
        public int MaxWss
        {
            get { lock (_sync) return _config.maxWSS; }
        }

        /// <summary>
        /// Gets the firmware version saved on config.
        /// Thread safe get.
        /// </summary>
        public string Firmware
        {
            get { lock (_sync) return _config.firmware; }
        }

        /// <summary>
        /// Gets the configured on-wire broadcast receiver address.
        /// </summary>
        /// <remarks>
        /// The value is read from the persisted hexadecimal string in <see cref="CoreConfig.broadcastTarget"/>.
        /// If the stored value is missing or invalid, the historical default <c>0x8F</c> is returned and
        /// the normalized value is immediately persisted back to disk.
        /// </remarks>
        public byte BroadcastTarget
        {
            get
            {
                lock (_sync)
                {
                    if (TryParseHexByte(_config.broadcastTarget, out var value))
                        return value;

                    _config.broadcastTarget = FormatHexByte(DefaultBroadcastTarget);
                    JsonReader.SaveObject(_configPath, _config);
                    return DefaultBroadcastTarget;
                }
            }
        }

        /// <summary>
        /// Gets the configured on-wire receiver addresses for logical Wss1..Wss3.
        /// A normalized three-entry copy is returned.
        /// </summary>
        /// <remarks>
        /// Missing entries are backfilled with the historical defaults <c>0x81</c>, <c>0x82</c>, and <c>0x83</c>.
        /// When normalization changes the in-memory configuration, the updated values are immediately persisted.
        /// </remarks>
        public byte[] WssTargets
        {
            get
            {
                lock (_sync)
                {
                    var normalized = NormalizeWssTargets(_config.wssTargets, out var normalizedStrings);
                    if (!HaveSameTargets(_config.wssTargets, normalizedStrings))
                    {
                        _config.wssTargets = normalizedStrings;
                        JsonReader.SaveObject(_configPath, _config);
                    }
                    return (byte[])normalized.Clone();
                }
            }
        }

        /// <summary>
        /// When true, the core should use per-WSS amplitude curves from the config file.
        /// </summary>
        public bool UseConfigAmpCurves
        {
            get { lock (_sync) return _config.useConfigAmpCurves; }
        }

        /// <summary>
        /// Returns the configured amplitude curve parameters per WSS (index 0..2 maps to Wss1..Wss3).
        /// </summary>
        /// <remarks>
        /// The returned array is the live in-memory configuration array, not a defensive copy.
        /// Mutating the returned array or its entries changes the loaded configuration until it is reloaded.
        /// Call <see cref="SaveJson"/> to persist those changes.
        /// </remarks>
        public AmpCurveParams[] AmpCurves
        {
            get { lock (_sync) return _config.ampCurves; }
        }

        /// <summary>
        /// Updates the maximum number of supported WSS devices in the loaded configuration
        /// and immediately persists the change to disk. Thread safe.
        /// </summary>
        /// <param name="v">
        /// New maximum WSS count. Must be greater than zero.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="v"/> is less than or equal to zero.</exception>
        public void SetMaxWss(int v)
        {
            if (v <= 0) throw new ArgumentOutOfRangeException(nameof(v));
            lock (_sync) { _config.maxWSS = v; JsonReader.SaveObject(_configPath, _config); }
        }

        /// <summary>
        /// Updates the firmware version recorded in the configuration and immediately saves it to disk.
        /// Thread safe.
        /// </summary>
        /// <param name="v">
        /// Firmware version string to record (for example, "H03").
        /// </param>
        /// <param name="verHandler">
        /// Version handler used to validate whether the supplied firmware string is supported.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="verHandler"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="v"/> is not supported by <paramref name="verHandler"/>.</exception>
        public void SetFirmware(string v, WSSVersionHandler verHandler)
        {
            if (verHandler == null) throw new ArgumentNullException(nameof(verHandler));
            if (!verHandler.isVersionSupported(v))
                throw new ArgumentException($"Firmware version '{v}' is not supported.", nameof(v));
            lock (_sync) { _config.firmware = v; JsonReader.SaveObject(_configPath, _config); }
        }

        private static readonly byte DefaultBroadcastTarget = 0x8F;

        private static byte[] NormalizeWssTargets(string[] values, out string[] normalizedStrings)
        {
            var defaults = new byte[] { 0x81, 0x82, 0x83 };
            var normalized = new byte[3];
            normalizedStrings = new string[3];
            for (int i = 0; i < normalized.Length; i++)
            {
                var fallback = defaults[i];
                if (values != null && i < values.Length && TryParseHexByte(values[i], out var parsed))
                    fallback = parsed;

                normalized[i] = fallback;
                normalizedStrings[i] = FormatHexByte(fallback);
            }

            return normalized;
        }

        private static bool TryParseHexByte(string value, out byte parsed)
        {
            parsed = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            return byte.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed);
        }

        private static string FormatHexByte(byte value)
            => $"0x{value:X2}";

        private static bool HaveSameTargets(string[] current, string[] normalized)
        {
            if (ReferenceEquals(current, normalized))
                return true;
            if (current == null || normalized == null || current.Length != normalized.Length)
                return false;

            for (int i = 0; i < current.Length; i++)
            {
                if (!string.Equals(current[i], normalized[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
