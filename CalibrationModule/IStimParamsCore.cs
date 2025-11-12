using System.Collections.Generic;
using Wss.CoreModule;

/// <summary>
/// Core + parameters surface layered over <see cref="IStimulationCore"/>.
/// Provides normalized-drive stimulation, per-channel parameter access,
/// and JSON persistence for stimulation parameters. BASIC is optional and
/// can be exposed via <see cref="TryGetBasic(out IBasicStimulation)"/>.
/// </summary>

namespace Wss.CalibrationModule
{
    public interface IStimParamsCore : IStimulationCore
    {
        // ---- Normalized stimulation ----

        /// <summary>
        /// Computes a pulse width from a normalized value in [0,1] using per-channel
        /// parameters (minPW, maxPW, amp, IPI) and forwards the result to the core.
        /// Implementations clamp the input to [0,1] and cache the last PW sent.
        /// </summary>
        /// <param name="channel">1-based logical channel.</param>
        /// <param name="normalizedValue">Normalized drive in [0,1].</param>
        void StimulateNormalized(int channel, float normalizedValue);

        /// <summary>
        /// Returns the most recently computed stimulation intensity for the channel.
        /// For PW-driven systems, this is the last pulse width (µs) sent by
        /// <see cref="StimulateNormalized(int,float)"/>.
        /// </summary>
        /// <param name="channel">1-based logical channel.</param>
        /// <returns>Most recent intensity value (µs or mA depending on mode).</returns>
        float GetStimIntensity(int channel);

        // ---- Params persistence ----

        /// <summary>
        /// Saves the current stimulation-parameters JSON to disk.
        /// </summary>
        void SaveParamsJson();

        /// <summary>
        /// Loads the stimulation-parameters JSON from the default location.
        /// </summary>
        void LoadParamsJson();

        /// <summary>
        /// Loads the stimulation-parameters JSON from a specific file path or directory.
        /// </summary>
        /// <param name="path">File path or directory.</param>
        void LoadParamsJson(string path);

        // ---- Dotted-key API ("stim.ch.{N}.{leaf}") ----

        /// <summary>
        /// Adds or updates a parameter by dotted key. Examples:
        /// <c>stim.ch.1.defaultPA</c>, <c>stim.ch.2.minPW</c>, <c>stim.ch.3.maxPW</c>, <c>stim.ch.4.IPI</c>.
        /// </summary>
        /// <param name="key">Dotted parameter key.</param>
        /// <param name="value">Value to set.</param>
        void AddOrUpdateStimParam(string key, float value);

        /// <summary>
        /// Reads a parameter by dotted key. Throws if the key is missing.
        /// </summary>
        /// <param name="key">Dotted parameter key.</param>
        /// <returns>Parameter value as float.</returns>
        float GetStimParam(string key);

        /// <summary>
        /// Tries to read a parameter by dotted key.
        /// </summary>
        /// <param name="key">Dotted parameter key.</param>
        /// <param name="value">Out value if the key exists.</param>
        /// <returns><c>true</c> if found, otherwise <c>false</c>.</returns>
        bool TryGetStimParam(string key, out float value);

        /// <summary>
        /// Returns a copy of all current stimulation parameters as a dotted-key map.
        /// </summary>
        /// <returns>Dictionary mapping dotted keys to parameter values.</returns>
        Dictionary<string, float> GetAllStimParams();

        /// <summary>
        /// Returns the parameters/configuration controller for stimulation params.
        /// </summary>
        /// <returns>The active <see cref="StimParamsConfigController"/>.</returns>
        StimParamsConfigController GetStimParamsConfigController();

        // ---- Channel helpers ----

        /// <summary>Sets the per-channel default amplitude (PA) in mA.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="mA">Amplitude in milliamps.</param>
        void SetChannelAmp(int ch, float mA);

        /// <summary>Sets per-channel minimum PA in mA.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="mA">Minimum amplitude in milliamps.</param>
        void SetChannelPAMin(int ch, float mA);

        /// <summary>Sets per-channel maximum PA in mA.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="mA">Maximum amplitude in milliamps.</param>
        void SetChannelPAMax(int ch, float mA);

        /// <summary>Sets per-channel minimum pulse width in µs.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="us">Minimum pulse width in microseconds.</param>
        void SetChannelPWMin(int ch, int us);

        /// <summary>Sets per-channel maximum pulse width in µs.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="us">Maximum pulse width in microseconds.</param>
        void SetChannelPWMax(int ch, int us);

        /// <summary>Sets the per-channel default pulse width in µs.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="us">Default pulse width in microseconds.</param>
        void SetChannelDefaultPW(int ch, int us);

        /// <summary>Sets per-channel IPI in ms.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="ms">Inter-pulse interval in milliseconds.</param>
        void SetChannelIPI(int ch, int ms);

        /// <summary>Sets the amplitude-control mode ("PW"/"PA") for a channel.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="mode">Target amplitude mode string.</param>
        void SetChannelAmpMode(int ch, string mode);

        /// <summary>Sets the same amplitude-control mode for all channels.</summary>
        /// <param name="mode">Target amplitude mode string.</param>
        void SetAllChannelsAmpMode(string mode);

        /// <summary>
        /// Sets the active-control minimum for a channel (µs when PW mode, mA when PA mode).
        /// </summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="value">Minimum value in the active axis units (µs for PW mode).</param>
        void SetChannelMin(int ch, float value);

        /// <summary>
        /// Sets the active-control maximum for a channel (µs when PW mode, mA when PA mode).
        /// </summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="value">Maximum value in the active axis units (µs for PW mode).</param>
        void SetChannelMax(int ch, float value);

        /// <summary>
        /// Sets the non-controlled axis default (PA when PW mode, PW when PA mode).
        /// </summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <param name="value">Default value expressed in the non-controlled axis units (µs for PA mode).</param>
        void SetChannelDefault(int ch, float value);

        /// <summary>Gets the per-channel default amplitude (PA) in mA.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Default amplitude in milliamps.</returns>
        float GetChannelAmp(int ch);

        /// <summary>Gets per-channel minimum PA in mA.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Minimum amplitude in milliamps.</returns>
        float GetChannelPAMin(int ch);

        /// <summary>Gets per-channel maximum PA in mA.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Maximum amplitude in milliamps.</returns>
        float GetChannelPAMax(int ch);

        /// <summary>Gets per-channel minimum pulse width in µs.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Minimum pulse width in microseconds.</returns>
        int GetChannelPWMin(int ch);

        /// <summary>Gets per-channel maximum pulse width in µs.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Maximum pulse width in microseconds.</returns>
        int GetChannelPWMax(int ch);

        /// <summary>Gets the per-channel default pulse width in µs.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Default pulse width in microseconds.</returns>
        int GetChannelDefaultPW(int ch);

        /// <summary>Gets per-channel IPI in ms.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Inter-pulse interval in milliseconds.</returns>
        int GetChannelIPI(int ch);

        /// <summary>Gets the current amplitude-control mode for a channel.</summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>"PW" or "PA".</returns>
        string GetChannelAmpMode(int ch);

        /// <summary>
        /// Gets the active-control minimum (µs when PW mode, mA when PA mode).
        /// </summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Minimum value in the active axis units (µs for PW mode).</returns>
        float GetChannelMin(int ch);

        /// <summary>
        /// Gets the active-control maximum (µs when PW mode, mA when PA mode).
        /// </summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Maximum value in the active axis units (µs for PW mode).</returns>
        float GetChannelMax(int ch);

        /// <summary>
        /// Gets the non-controlled axis default (PA when PW mode, PW when PA mode).
        /// </summary>
        /// <param name="ch">1-based logical channel.</param>
        /// <returns>Default value expressed in the non-controlled axis units (µs for PA mode).</returns>
        float GetChannelDefault(int ch);

        // ---- Optional capability ----

        /// <summary>
        /// Exposes the optional BASIC capability if available from the wrapped core.
        /// Returns <c>true</c> and sets <paramref name="basic"/> if supported.
        /// </summary>
        /// <param name="basic">Out parameter for the BASIC interface.</param>
        /// <returns><c>true</c> when BASIC is available, otherwise <c>false</c>.</returns>
        bool TryGetBasic(out IBasicStimulation basic);
    }
}
