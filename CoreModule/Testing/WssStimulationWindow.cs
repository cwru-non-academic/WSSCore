using System;
using System.Collections.Generic;
using System.Linq;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>Evaluates a finite operation transcript without changing IWssConformance.</summary>
    /// <remarks>
    /// Old complete tuples are permitted until convergence. Every observed neighbor and other target
    /// must retain its baseline tuple throughout the window. A changed IPI requires a full stream;
    /// convergence requires three new target frames including a subsequent no-IPI frame, and a frame
    /// from each other configured target after convergence. Hosts must impose a finite polling limit.
    /// </remarks>
    public static class WssStimulationWindow
    {
        /// <summary>Validates the immutable stimulation-history snapshot taken by this call.</summary>
        /// <param name="conformance">Observation source from the real emulator transport.</param>
        /// <param name="baseline">Baseline captured immediately before the public API operation.</param>
        /// <param name="expectation">Requested device-local effective tuple.</param>
        /// <param name="deviceCount">Configured target count, from one through three.</param>
        /// <returns>Settlement state and permanent violations within this window.</returns>
        public static WssStimulationWindowResult Evaluate(IWssConformance conformance,
            WssStimulationBaseline baseline, WssStimulationExpectation expectation, int deviceCount)
        {
            if (conformance == null) throw new ArgumentNullException(nameof(conformance));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (expectation == null) throw new ArgumentNullException(nameof(expectation));
            if (deviceCount < 1 || deviceCount > 3) throw new ArgumentOutOfRangeException(nameof(deviceCount));
            var history = conformance.StimulationHistory;
            long end = history.Count == 0 ? baseline.SequenceNumber : history.Max(item => item.SequenceNumber);
            var failures = new List<string>();
            var afterConvergence = new HashSet<byte>();
            long? converged = null;
            int newFrames = 0;
            bool full = false, partial = false;
            bool baselinePresent = baseline.TryGetState(expectation.Target, expectation.Channel, out var old);
            bool ipiChanged = baselinePresent && old.InterPulseInterval != expectation.InterPulseInterval;
            for (int unit = 0; unit < deviceCount; unit++)
                for (int ch = 1; ch <= 3; ch++)
                    if (!baseline.TryGetState((byte)(0x81 + unit), ch, out _))
                        failures.Add($"Missing baseline target=0x{0x81 + unit:X2} slot={ch}.");
            foreach (var frame in history.Where(item => item.SequenceNumber > baseline.SequenceNumber)
                .GroupBy(item => new { item.SequenceNumber, item.Target }).OrderBy(group => group.Key.SequenceNumber))
            {
                bool configured = frame.Key.Target >= 0x81 && frame.Key.Target < 0x81 + deviceCount;
                if (!configured || frame.Count() != 3 || frame.Select(item => item.Channel).Distinct().Count() != 3)
                {
                    failures.Add($"Unexpected/incomplete frame target=0x{frame.Key.Target:X2} sequence=#{frame.Key.SequenceNumber}.");
                    continue;
                }
                foreach (var item in frame)
                {
                    if (item.MessageId != (byte)WSSMessageIDs.StreamChangeAll &&
                        item.MessageId != (byte)WSSMessageIDs.StreamChangeNoIPI)
                        failures.Add($"Unexpected stream variant 0x{item.MessageId:X2} target=0x{item.Target:X2} sequence=#{item.SequenceNumber}.");
                    if (!baseline.TryGetState(item.Target, item.Channel, out var before)) continue;
                    bool changedSlot = item.Target == expectation.Target && item.Channel == expectation.Channel;
                    bool isOld = Same(item, before.PulseAmplitude, before.PulseWidth, before.InterPulseInterval);
                    bool isNew = Same(item, expectation.PulseAmplitude, expectation.PulseWidth, expectation.InterPulseInterval);
                    if (!changedSlot)
                    {
                        if (!isOld) failures.Add(Describe(item, before.PulseAmplitude, before.PulseWidth, before.InterPulseInterval, "Unrelated channel changed"));
                    }
                    else if (isNew)
                    {
                        if (!converged.HasValue) converged = item.SequenceNumber;
                        newFrames++;
                        full |= item.MessageId == (byte)WSSMessageIDs.StreamChangeAll;
                        partial |= item.SequenceNumber > converged.Value && item.MessageId == (byte)WSSMessageIDs.StreamChangeNoIPI;
                    }
                    else if (!isOld || converged.HasValue)
                        failures.Add(Describe(item, expectation.PulseAmplitude, expectation.PulseWidth, expectation.InterPulseInterval,
                            converged.HasValue ? "Regression after convergence" : "Mixed/unexpected tuple"));
                }
                if (converged.HasValue && frame.Key.SequenceNumber > converged.Value)
                    afterConvergence.Add(frame.Key.Target);
            }
            failures.AddRange(conformance.ProtocolErrors.Where(error => error.SequenceNumber > baseline.ProtocolErrorSequence)
                .Select(error => $"Protocol error #{error.SequenceNumber}: {error.Kind}: {error.Description}"));
            bool settled = baselinePresent && newFrames >= 3 && partial && (!ipiChanged || full) &&
                Enumerable.Range(0, deviceCount).All(unit => afterConvergence.Contains((byte)(0x81 + unit)));
            string progress = $"target=0x{expectation.Target:X2} slot={expectation.Channel} window=({baseline.SequenceNumber},{end}] " +
                $"convergence={converged?.ToString() ?? "pending"} newFrames={newFrames} full={full} noIPI={partial} requiresIPI={ipiChanged}";
            return new WssStimulationWindowResult(settled, failures, progress);
        }

        private static bool Same(WssStimulationObservation item, int amplitude, int width, int ipi)
            => item.PulseAmplitude == amplitude && item.PulseWidth == width && item.InterPulseInterval == ipi;

        private static string Describe(WssStimulationObservation item, int amplitude, int width, int ipi, string reason)
            => $"{reason}: target=0x{item.Target:X2} slot={item.Channel} sequence=#{item.SequenceNumber} " +
               $"expected=({amplitude},{width},{ipi}) observed=({item.PulseAmplitude},{item.PulseWidth},{item.InterPulseInterval}).";
    }

    /// <summary>Reports settlement separately from violations, so polling cannot erase an earlier bad frame.</summary>
    public sealed class WssStimulationWindowResult
    {
        internal WssStimulationWindowResult(bool settled, IEnumerable<string> failures, string progress)
        {
            Settled = settled;
            Failures = Array.AsReadOnly(failures.ToArray());
            Diagnostics = progress + Environment.NewLine + string.Join(Environment.NewLine, Failures);
        }
        /// <summary>Gets whether enough converged full/partial frames and other-target observations were received.</summary>
        public bool Settled { get; }
        /// <summary>Gets whether the window settled without violations.</summary>
        public bool Passed => Settled && Failures.Count == 0;
        /// <summary>Gets permanent violations in the evaluated window.</summary>
        public IReadOnlyList<string> Failures { get; }
        /// <summary>Gets observation-window progress and failure details.</summary>
        public string Diagnostics { get; }
    }
}
