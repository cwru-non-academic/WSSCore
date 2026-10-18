using System;
using System.Collections.Generic;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>
    /// Provides authoritative behavior scenarios for WSS public API compatibility tests.
    /// </summary>
    public static class WssBehaviorScenarios
    {
        /// <summary>Gets the deterministic configuration profile used by stimulation scenarios.</summary>
        public static WssStimulationFixtureProfile StimulationFixture { get; } =
            new WssStimulationFixtureProfile(
                "PW",
                21,
                221,
                4.0f,
                13,
                0.0f,
                1.0f,
                1.0f,
                -1.0f,
                1.0f);

        /// <summary>Gets the initialization behavior scenario.</summary>
        public static WssInitializationScenario Initialization { get; } =
            new WssInitializationScenario(
                "initialization.default",
                (byte)WssTarget.Wss1,
                requiresOperationalState: true,
                requiresStreamObservation: true,
                requiresSuccessfulConformance: true);

        /// <summary>Gets the direct analog stimulation behavior scenario.</summary>
        public static WssAnalogStimulationScenario DirectAnalog { get; } =
            new WssAnalogStimulationScenario(
                "direct-analog.channel-1",
                (byte)WssTarget.Wss1,
                1,
                4.0f,
                237,
                13,
                new WssStimulationExpectation(
                    (byte)WssTarget.Wss1,
                    1,
                    4,
                    237,
                    13,
                    (byte)WSSMessageIDs.StreamChangeAll));

        /// <summary>Gets all normalized stimulation behavior scenarios in stable catalog order.</summary>
        public static IReadOnlyList<WssNormalizedStimulationScenario> NormalizedStimulation { get; } =
            Array.AsReadOnly(new[]
            {
                new WssNormalizedStimulationScenario(
                    "normalized.minus-0.2",
                    (byte)WssTarget.Wss1,
                    1,
                    -0.2f,
                    new WssStimulationExpectation(
                        (byte)WssTarget.Wss1,
                        1,
                        4,
                        0,
                        13,
                        (byte)WSSMessageIDs.StreamChangeAll)),
                new WssNormalizedStimulationScenario(
                    "normalized.0.0",
                    (byte)WssTarget.Wss1,
                    1,
                    0.0f,
                    new WssStimulationExpectation(
                        (byte)WssTarget.Wss1,
                        1,
                        4,
                        0,
                        13,
                        (byte)WSSMessageIDs.StreamChangeAll)),
                new WssNormalizedStimulationScenario(
                    "normalized.0.37",
                    (byte)WssTarget.Wss1,
                    1,
                    0.37f,
                    new WssStimulationExpectation(
                        (byte)WssTarget.Wss1,
                        1,
                        4,
                        95,
                        13,
                        (byte)WSSMessageIDs.StreamChangeAll)),
                new WssNormalizedStimulationScenario(
                    "normalized.0.5",
                    (byte)WssTarget.Wss1,
                    1,
                    0.5f,
                    new WssStimulationExpectation(
                        (byte)WssTarget.Wss1,
                        1,
                        4,
                        121,
                        13,
                        (byte)WSSMessageIDs.StreamChangeAll)),
                new WssNormalizedStimulationScenario(
                    "normalized.1.0",
                    (byte)WssTarget.Wss1,
                    1,
                    1.0f,
                    new WssStimulationExpectation(
                        (byte)WssTarget.Wss1,
                        1,
                        4,
                        221,
                        13,
                        (byte)WSSMessageIDs.StreamChangeAll)),
                new WssNormalizedStimulationScenario(
                    "normalized.1.2",
                    (byte)WssTarget.Wss1,
                    1,
                    1.2f,
                    new WssStimulationExpectation(
                        (byte)WssTarget.Wss1,
                        1,
                        4,
                        221,
                        13,
                        (byte)WSSMessageIDs.StreamChangeAll))
            });

        /// <summary>Gets the representative runtime Event ratio-edit behavior scenario.</summary>
        public static WssRuntimeEventEditScenario RuntimeEventEdit { get; } =
            new WssRuntimeEventEditScenario(
                "runtime-event-edit.ratio",
                (byte)WssTarget.Wss1,
                1,
                4,
                (byte)WSSMessageIDs.EditEventConfig,
                0x07,
                4,
                resumeStreamingExpected: true,
                additionalSyncGroupExpected: false,
                requiresSuccessfulConformance: true);

        /// <summary>Gets the stop-stimulation behavior scenario.</summary>
        public static WssStopStimulationScenario StopStimulation { get; } =
            new WssStopStimulationScenario(
                "stop-stimulation.default",
                (byte)WssTarget.Wss1,
                (byte)WSSMessageIDs.StimulationSwitch,
                0x04,
                resumeStreamingExpected: false);
    }
}
