using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines deterministic configuration values used by the shared stimulation scenarios.
    /// </summary>
    public sealed class WssStimulationFixtureProfile
    {
        /// <summary>
        /// Creates a deterministic stimulation fixture profile.
        /// </summary>
        /// <param name="amplitudeMode">Configured normalized amplitude mode.</param>
        /// <param name="minimumPulseWidth">Minimum nonzero pulse width in microseconds.</param>
        /// <param name="maximumPulseWidth">Maximum pulse width in microseconds.</param>
        /// <param name="defaultAmplitudeMa">Default pulse amplitude in milliamperes.</param>
        /// <param name="interPulseInterval">Inter-pulse interval in milliseconds.</param>
        /// <param name="curveLowThreshold">Low-segment calibration threshold.</param>
        /// <param name="curveLowConstant">Low-segment calibration constant.</param>
        /// <param name="curveExponent">Exponential calibration power.</param>
        /// <param name="curveLinearOffset">Linear calibration offset.</param>
        /// <param name="curveLinearSlope">Linear calibration slope.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="amplitudeMode"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a fixture value is invalid or not finite.</exception>
        public WssStimulationFixtureProfile(
            string amplitudeMode,
            int minimumPulseWidth,
            int maximumPulseWidth,
            float defaultAmplitudeMa,
            int interPulseInterval,
            float curveLowThreshold,
            float curveLowConstant,
            float curveExponent,
            float curveLinearOffset,
            float curveLinearSlope)
        {
            if (string.IsNullOrWhiteSpace(amplitudeMode))
                throw new ArgumentException("An amplitude mode is required.", nameof(amplitudeMode));
            ValidateByte(minimumPulseWidth, nameof(minimumPulseWidth));
            ValidateByte(maximumPulseWidth, nameof(maximumPulseWidth));
            ValidateByte(interPulseInterval, nameof(interPulseInterval));
            if (minimumPulseWidth > maximumPulseWidth)
                throw new ArgumentOutOfRangeException(nameof(minimumPulseWidth), "Minimum pulse width cannot exceed maximum pulse width.");
            ValidateFinite(defaultAmplitudeMa, nameof(defaultAmplitudeMa));
            ValidateFinite(curveLowThreshold, nameof(curveLowThreshold));
            ValidateFinite(curveLowConstant, nameof(curveLowConstant));
            ValidateFinite(curveExponent, nameof(curveExponent));
            ValidateFinite(curveLinearOffset, nameof(curveLinearOffset));
            ValidateFinite(curveLinearSlope, nameof(curveLinearSlope));

            AmplitudeMode = amplitudeMode;
            MinimumPulseWidth = minimumPulseWidth;
            MaximumPulseWidth = maximumPulseWidth;
            DefaultAmplitudeMa = defaultAmplitudeMa;
            InterPulseInterval = interPulseInterval;
            CurveLowThreshold = curveLowThreshold;
            CurveLowConstant = curveLowConstant;
            CurveExponent = curveExponent;
            CurveLinearOffset = curveLinearOffset;
            CurveLinearSlope = curveLinearSlope;
        }

        /// <summary>Gets the configured normalized amplitude mode.</summary>
        public string AmplitudeMode { get; }

        /// <summary>Gets the minimum nonzero pulse width in microseconds.</summary>
        public int MinimumPulseWidth { get; }

        /// <summary>Gets the maximum pulse width in microseconds.</summary>
        public int MaximumPulseWidth { get; }

        /// <summary>Gets the default pulse amplitude in milliamperes.</summary>
        public float DefaultAmplitudeMa { get; }

        /// <summary>Gets the inter-pulse interval in milliseconds.</summary>
        public int InterPulseInterval { get; }

        /// <summary>Gets the low-segment calibration threshold.</summary>
        public float CurveLowThreshold { get; }

        /// <summary>Gets the low-segment calibration constant.</summary>
        public float CurveLowConstant { get; }

        /// <summary>Gets the exponential calibration power.</summary>
        public float CurveExponent { get; }

        /// <summary>Gets the linear calibration offset.</summary>
        public float CurveLinearOffset { get; }

        /// <summary>Gets the linear calibration slope.</summary>
        public float CurveLinearSlope { get; }

        private static void ValidateByte(int value, string parameterName)
        {
            if (value < byte.MinValue || value > byte.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Value must be from 0 through 255.");
        }

        private static void ValidateFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName, "Value must be finite.");
        }
    }
}
