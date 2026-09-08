using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssInitializationConformanceTests
    {
        [Test]
        public async Task RealCoreInitializationReachesReadyAndPassesConformance()
        {
            var scenario = WssBehaviorScenarios.Initialization;
            string configPath = Path.Combine(Path.GetTempPath(), $"wss-core-{Guid.NewGuid():N}.json");
            File.WriteAllText(configPath,
                "{\"maxWSS\":1,\"firmware\":\"J03\",\"broadcastTarget\":\"0x8F\",\"wssTargets\":[\"0x81\",\"0x82\",\"0x83\"]}");

            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = new WssStimulationCore(
                    transport,
                    new WssStimulationCoreOptions
                    {
                        ConfigPath = configPath,
                        MaxSetupTries = 1
                    });

                core.Initialize();
                bool reachedReady = false;
                for (int i = 0; i < 2000; i++)
                {
                    core.Tick();
                    if (core.Ready())
                    {
                        reachedReady = true;
                        break;
                    }

                    await Task.Delay(1);
                }

                for (int i = 0; i < 1000 && !transport.Conformance.StimulationHistory.Any(
                    item => item.Target == scenario.Target); i++)
                {
                    await Task.Delay(1);
                }

                bool streamObserved = transport.Conformance.StimulationHistory.Any(
                    item => item.Target == scenario.Target);
                var result = transport.Conformance.ValidateInitialization();
                Assert.Multiple(() =>
                {
                    Assert.That(!scenario.RequiresOperationalState || reachedReady, Is.True,
                        "Core did not reach Ready within the finite tick limit.");
                    Assert.That(!scenario.RequiresOperationalState || core.Ready(), Is.True,
                        "Core left Ready before conformance validation.");
                    Assert.That(!scenario.RequiresStreamObservation || streamObserved, Is.True,
                        $"Core did not emit a supported startup stream for target 0x{scenario.Target:X2}.");
                    Assert.That(!scenario.RequiresSuccessfulConformance || result.Passed, Is.True,
                        string.Join(Environment.NewLine, result.Failures));
                });
            }
            finally
            {
                core?.Dispose();
                if (core == null)
                    transport.Dispose();
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        [Test]
        public async Task RealCoreRuntimeEventEditPausesAndResumesStreaming()
        {
            var scenario = WssBehaviorScenarios.RuntimeEventEdit;
            string configPath = CreateCoreConfigPath();
            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = CreateCore(transport, configPath);
                var initialStream = await InitializeUntilStreamingAsync(core, transport);
                int syncCountBefore = transport.Conformance.MessageHistory.Count(item =>
                    item.MessageId == (byte)WSSMessageIDs.SyncGroup);

                core.UpdateEventRatio(scenario.Ratio, scenario.EventId, (WssTarget)scenario.Target);

                WssMessageObservation edit = null;
                WssMessageObservation resumedStream = null;
                for (int i = 0; i < 2000; i++)
                {
                    core.Tick();
                    var history = transport.Conformance.MessageHistory;
                    edit = history.FirstOrDefault(item =>
                        item.SequenceNumber > initialStream.SequenceNumber &&
                        item.Target == scenario.Target &&
                        item.MessageId == scenario.ExpectedMessageId &&
                        item.Payload.Length >= 5 &&
                        item.Payload[2] == scenario.EventId &&
                        item.Payload[3] == scenario.ExpectedSubcommand &&
                        item.Payload[4] == scenario.ExpectedValue);
                    if (edit != null)
                    {
                        resumedStream = history.FirstOrDefault(item =>
                            item.SequenceNumber > edit.SequenceNumber && IsStream(item));
                    }
                    if (resumedStream != null)
                        break;
                    await Task.Delay(1);
                }

                var result = transport.Conformance.ValidateInitialization();
                int syncCountAfter = transport.Conformance.MessageHistory.Count(item =>
                    item.MessageId == (byte)WSSMessageIDs.SyncGroup);
                int expectedAdditionalSyncGroups = scenario.AdditionalSyncGroupExpected ? 1 : 0;
                if (edit != null && resumedStream != null)
                {
                    TestContext.WriteLine(
                        $"Observed ordering: Stream #{initialStream.SequenceNumber}, EditEventConfig #{edit.SequenceNumber}, Stream #{resumedStream.SequenceNumber}.");
                }
                Assert.Multiple(() =>
                {
                    Assert.That(edit, Is.Not.Null, "Core did not transmit the requested EditEventConfig ratio command.");
                    Assert.That(resumedStream != null, Is.EqualTo(scenario.ResumeStreamingExpected),
                        "Core streaming state after the Event edit did not match the shared scenario.");
                    Assert.That(edit?.SequenceNumber, Is.GreaterThan(initialStream.SequenceNumber));
                    Assert.That(resumedStream?.SequenceNumber, Is.GreaterThan(edit?.SequenceNumber));
                    Assert.That(syncCountAfter - syncCountBefore, Is.EqualTo(expectedAdditionalSyncGroups),
                        "Runtime Event edit SyncGroup behavior did not match the shared scenario.");
                    Assert.That(!scenario.RequiresSuccessfulConformance || result.Passed, Is.True,
                        string.Join(Environment.NewLine, result.Failures));
                });
            }
            finally
            {
                core?.Dispose();
                if (core == null)
                    transport.Dispose();
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        [Test]
        public async Task RealCoreRapidRuntimeEventEditsExecuteExactlyOnceInOrderAndResumeStreaming()
        {
            string configPath = CreateCoreConfigPath();
            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = CreateCore(transport, configPath);
                await InitializeUntilStreamingAsync(core, transport);

                var initialValidation = transport.Conformance.ValidateInitialization();
                var baselineStream = transport.Conformance.MessageHistory.Last(IsStream);
                long baselineSequence = baselineStream.SequenceNumber;
                int syncCountBefore = transport.Conformance.MessageHistory.Count(item =>
                    item.MessageId == (byte)WSSMessageIDs.SyncGroup);

                Assert.That(initialValidation.Passed, Is.True,
                    "Core initialization was not conformant before the runtime edit burst: " +
                    string.Join(Environment.NewLine, initialValidation.Failures));

                core.UpdateEventRatio(4, 1, WssTarget.Wss1);
                core.UpdateEventDelay(3, 1, WssTarget.Wss1);
                core.UpdateEventPulseWidths(1, 20, 20, 50, WssTarget.Wss1);

                bool pausedAfterScheduling = !core.Started();
                bool burstCompleted = false;
                WssMessageObservation resumedStream = null;
                for (int i = 0; i < 2000; i++)
                {
                    core.Tick();
                    var history = transport.Conformance.MessageHistory;
                    var edits = history.Where(item =>
                        item.SequenceNumber > baselineSequence &&
                        item.Target == (byte)WssTarget.Wss1 &&
                        item.MessageId == (byte)WSSMessageIDs.EditEventConfig).ToArray();

                    var observedPulseWidthEdit = edits.FirstOrDefault(item =>
                        PayloadEquals(item, 0x49, 0x05, 0x01, 0x02, 0x14, 0x32, 0x14));
                    if (observedPulseWidthEdit != null)
                    {
                        resumedStream = history.FirstOrDefault(item =>
                            item.SequenceNumber > observedPulseWidthEdit.SequenceNumber && IsStream(item));
                    }

                    if (edits.Length >= 3 && resumedStream != null && core.Started())
                    {
                        burstCompleted = true;
                        break;
                    }

                    await Task.Delay(1);
                }

                var postBurstHistory = transport.Conformance.MessageHistory
                    .Where(item => item.SequenceNumber > baselineSequence)
                    .ToArray();
                var eventEdits = postBurstHistory.Where(item =>
                    item.MessageId == (byte)WSSMessageIDs.EditEventConfig).ToArray();
                var ratioEdits = eventEdits.Where(item =>
                    PayloadEquals(item, 0x49, 0x03, 0x01, 0x07, 0x04)).ToArray();
                var delayEdits = eventEdits.Where(item =>
                    PayloadEquals(item, 0x49, 0x03, 0x01, 0x06, 0x03)).ToArray();
                var pulseWidthEdits = eventEdits.Where(item =>
                    PayloadEquals(item, 0x49, 0x05, 0x01, 0x02, 0x14, 0x32, 0x14)).ToArray();
                int syncCountAfter = transport.Conformance.MessageHistory.Count(item =>
                    item.MessageId == (byte)WSSMessageIDs.SyncGroup);
                var finalValidation = transport.Conformance.ValidateInitialization();

                var ratioEdit = ratioEdits.FirstOrDefault();
                var delayEdit = delayEdits.FirstOrDefault();
                var pulseWidthEdit = pulseWidthEdits.FirstOrDefault();
                if (ratioEdit != null && delayEdit != null && pulseWidthEdit != null && resumedStream != null)
                {
                    TestContext.WriteLine(
                        $"Observed ordering: Stream #{baselineSequence}, Ratio #{ratioEdit.SequenceNumber}, " +
                        $"Delay #{delayEdit.SequenceNumber}, PulseWidths #{pulseWidthEdit.SequenceNumber}, " +
                        $"Stream #{resumedStream.SequenceNumber}.");
                    TestContext.WriteLine(
                        $"Observed counts: ratio={ratioEdits.Length}, delay={delayEdits.Length}, " +
                        $"pulseWidths={pulseWidthEdits.Length}, SyncGroup before={syncCountBefore}, after={syncCountAfter}.");
                }

                Assert.Multiple(() =>
                {
                    Assert.That(pausedAfterScheduling, Is.True,
                        "Core did not leave Streaming while the runtime edit burst was queued.");
                    Assert.That(burstCompleted, Is.True,
                        "Runtime edit processing did not complete within the finite tick limit.");
                    Assert.That(ratioEdits, Has.Length.EqualTo(1), "Ratio edit was lost or duplicated.");
                    Assert.That(delayEdits, Has.Length.EqualTo(1), "Delay edit was lost or duplicated.");
                    Assert.That(pulseWidthEdits, Has.Length.EqualTo(1), "Pulse-width edit was lost or duplicated.");
                    Assert.That(eventEdits, Has.Length.EqualTo(3),
                        "Unexpected additional Event edits were transmitted after the burst baseline.");
                    Assert.That(ratioEdit?.Target, Is.EqualTo((byte)WssTarget.Wss1));
                    Assert.That(delayEdit?.Target, Is.EqualTo((byte)WssTarget.Wss1));
                    Assert.That(pulseWidthEdit?.Target, Is.EqualTo((byte)WssTarget.Wss1));
                    Assert.That(ratioEdit?.SequenceNumber, Is.GreaterThan(baselineSequence));
                    Assert.That(delayEdit?.SequenceNumber, Is.GreaterThan(ratioEdit?.SequenceNumber));
                    Assert.That(pulseWidthEdit?.SequenceNumber, Is.GreaterThan(delayEdit?.SequenceNumber));
                    Assert.That(resumedStream, Is.Not.Null, "Core did not resume streaming after all Event edits.");
                    Assert.That(resumedStream?.SequenceNumber, Is.GreaterThan(pulseWidthEdit?.SequenceNumber));
                    Assert.That(syncCountAfter, Is.EqualTo(syncCountBefore),
                        "Runtime Event edits caused a redundant SyncGroup command.");
                    Assert.That(finalValidation.Passed, Is.True,
                        string.Join(Environment.NewLine, finalValidation.Failures));
                });
            }
            finally
            {
                core?.Dispose();
                if (core == null)
                    transport.Dispose();
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        [Test]
        public async Task RealCoreRepeatedRapidRuntimeEventEditBurstsRemainOrderedAndResumeStreaming()
        {
            const int BurstCount = 5;
            string configPath = CreateCoreConfigPath();
            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = CreateCore(transport, configPath);
                await InitializeUntilStreamingAsync(core, transport);

                var initialValidation = transport.Conformance.ValidateInitialization();
                long testBaseline = transport.Conformance.MessageHistory.Last(IsStream).SequenceNumber;
                int syncCountBefore = transport.Conformance.MessageHistory.Count(item =>
                    item.MessageId == (byte)WSSMessageIDs.SyncGroup);
                Assert.That(initialValidation.Passed, Is.True,
                    "Core initialization was not conformant before repeated runtime edit bursts: " +
                    string.Join(Environment.NewLine, initialValidation.Failures));

                for (int burst = 1; burst <= BurstCount; burst++)
                {
                    long burstBaseline = transport.Conformance.MessageHistory.Last(IsStream).SequenceNumber;

                    core.UpdateEventRatio(4, 1, WssTarget.Wss1);
                    core.UpdateEventDelay(3, 1, WssTarget.Wss1);
                    core.UpdateEventPulseWidths(1, 20, 20, 50, WssTarget.Wss1);

                    bool burstCompleted = false;
                    WssMessageObservation resumedStream = null;
                    for (int i = 0; i < 2000; i++)
                    {
                        core.Tick();
                        var history = transport.Conformance.MessageHistory;
                        var observedPulseWidthEdit = history.FirstOrDefault(item =>
                            item.SequenceNumber > burstBaseline &&
                            item.Target == (byte)WssTarget.Wss1 &&
                            item.MessageId == (byte)WSSMessageIDs.EditEventConfig &&
                            PayloadEquals(item, 0x49, 0x05, 0x01, 0x02, 0x14, 0x32, 0x14));
                        if (observedPulseWidthEdit != null)
                        {
                            resumedStream = history.FirstOrDefault(item =>
                                item.SequenceNumber > observedPulseWidthEdit.SequenceNumber && IsStream(item));
                        }

                        if (resumedStream != null && core.Started())
                        {
                            burstCompleted = true;
                            break;
                        }

                        await Task.Delay(1);
                    }

                    var burstEdits = transport.Conformance.MessageHistory.Where(item =>
                        item.SequenceNumber > burstBaseline &&
                        item.MessageId == (byte)WSSMessageIDs.EditEventConfig).ToArray();
                    var ratioEdits = burstEdits.Where(item =>
                        PayloadEquals(item, 0x49, 0x03, 0x01, 0x07, 0x04)).ToArray();
                    var delayEdits = burstEdits.Where(item =>
                        PayloadEquals(item, 0x49, 0x03, 0x01, 0x06, 0x03)).ToArray();
                    var pulseWidthEdits = burstEdits.Where(item =>
                        PayloadEquals(item, 0x49, 0x05, 0x01, 0x02, 0x14, 0x32, 0x14)).ToArray();

                    Assert.Multiple(() =>
                    {
                        Assert.That(burstCompleted, Is.True,
                            $"Burst {burst} did not complete within the finite tick limit.");
                        Assert.That(ratioEdits, Has.Length.EqualTo(1), $"Burst {burst} ratio edit was lost or duplicated.");
                        Assert.That(delayEdits, Has.Length.EqualTo(1), $"Burst {burst} delay edit was lost or duplicated.");
                        Assert.That(pulseWidthEdits, Has.Length.EqualTo(1), $"Burst {burst} pulse-width edit was lost or duplicated.");
                        Assert.That(burstEdits, Has.Length.EqualTo(3), $"Burst {burst} transmitted unexpected Event edits.");
                        Assert.That(ratioEdits.FirstOrDefault()?.Target, Is.EqualTo((byte)WssTarget.Wss1));
                        Assert.That(delayEdits.FirstOrDefault()?.Target, Is.EqualTo((byte)WssTarget.Wss1));
                        Assert.That(pulseWidthEdits.FirstOrDefault()?.Target, Is.EqualTo((byte)WssTarget.Wss1));
                        Assert.That(delayEdits.FirstOrDefault()?.SequenceNumber,
                            Is.GreaterThan(ratioEdits.FirstOrDefault()?.SequenceNumber),
                            $"Burst {burst} did not preserve ratio-before-delay ordering.");
                        Assert.That(pulseWidthEdits.FirstOrDefault()?.SequenceNumber,
                            Is.GreaterThan(delayEdits.FirstOrDefault()?.SequenceNumber),
                            $"Burst {burst} did not preserve delay-before-pulse-width ordering.");
                        Assert.That(resumedStream, Is.Not.Null, $"Streaming did not resume after burst {burst}.");
                        Assert.That(resumedStream?.SequenceNumber,
                            Is.GreaterThan(pulseWidthEdits.FirstOrDefault()?.SequenceNumber));
                    });

                    if (ratioEdits.Length == 1 && delayEdits.Length == 1 &&
                        pulseWidthEdits.Length == 1 && resumedStream != null)
                    {
                        TestContext.WriteLine(
                            $"Burst {burst}: Stream #{burstBaseline}, Ratio #{ratioEdits[0].SequenceNumber}, " +
                            $"Delay #{delayEdits[0].SequenceNumber}, PulseWidths #{pulseWidthEdits[0].SequenceNumber}, " +
                            $"Stream #{resumedStream.SequenceNumber}.");
                    }
                }

                var allEdits = transport.Conformance.MessageHistory.Where(item =>
                    item.SequenceNumber > testBaseline &&
                    item.MessageId == (byte)WSSMessageIDs.EditEventConfig).ToArray();
                int ratioCount = allEdits.Count(item =>
                    PayloadEquals(item, 0x49, 0x03, 0x01, 0x07, 0x04));
                int delayCount = allEdits.Count(item =>
                    PayloadEquals(item, 0x49, 0x03, 0x01, 0x06, 0x03));
                int pulseWidthCount = allEdits.Count(item =>
                    PayloadEquals(item, 0x49, 0x05, 0x01, 0x02, 0x14, 0x32, 0x14));
                int syncCountAfter = transport.Conformance.MessageHistory.Count(item =>
                    item.MessageId == (byte)WSSMessageIDs.SyncGroup);
                var finalValidation = transport.Conformance.ValidateInitialization();

                TestContext.WriteLine(
                    $"Repeated burst totals: edits={allEdits.Length}, ratio={ratioCount}, delay={delayCount}, " +
                    $"pulseWidths={pulseWidthCount}, SyncGroup before={syncCountBefore}, after={syncCountAfter}.");
                Assert.Multiple(() =>
                {
                    Assert.That(allEdits, Has.Length.EqualTo(BurstCount * 3));
                    Assert.That(ratioCount, Is.EqualTo(BurstCount));
                    Assert.That(delayCount, Is.EqualTo(BurstCount));
                    Assert.That(pulseWidthCount, Is.EqualTo(BurstCount));
                    Assert.That(syncCountAfter, Is.EqualTo(syncCountBefore));
                    Assert.That(finalValidation.Passed, Is.True,
                        string.Join(Environment.NewLine, finalValidation.Failures));
                });
            }
            finally
            {
                core?.Dispose();
                if (core == null)
                    transport.Dispose();
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        [Test]
        public async Task RealCoreStopStimLeavesStreamingStopped()
        {
            var scenario = WssBehaviorScenarios.StopStimulation;
            string configPath = CreateCoreConfigPath();
            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = CreateCore(transport, configPath);
                var initialStream = await InitializeUntilStreamingAsync(core, transport);

                core.StopStim((WssTarget)scenario.Target);

                WssMessageObservation stop = null;
                bool queueDrained = false;
                for (int i = 0; i < 2000; i++)
                {
                    core.Tick();
                    stop = transport.Conformance.MessageHistory.FirstOrDefault(item =>
                        item.SequenceNumber > initialStream.SequenceNumber &&
                        item.Target == scenario.Target &&
                        item.MessageId == scenario.ExpectedMessageId &&
                        item.Payload.Length == 3 &&
                        item.Payload[2] == scenario.ExpectedOperationValue);
                    queueDrained = stop != null && core.Ready();
                    if (queueDrained)
                        break;
                    await Task.Delay(1);
                }

                for (int i = 0; i < 100; i++)
                {
                    core.Tick();
                    await Task.Delay(1);
                }

                bool streamAfterStop = stop != null && transport.Conformance.MessageHistory.Any(item =>
                    item.SequenceNumber > stop.SequenceNumber && IsStream(item));
                if (stop != null)
                    TestContext.WriteLine($"Observed StopStim #{stop.SequenceNumber}; post-stop stream observed: {streamAfterStop}.");
                var result = transport.Conformance.ValidateInitialization();
                Assert.Multiple(() =>
                {
                    Assert.That(stop, Is.Not.Null, "Core did not transmit StimulationSwitch STOP.");
                    Assert.That(queueDrained, Is.True, "StopStim setup processing did not drain within the finite tick limit.");
                    Assert.That(streamAfterStop, Is.EqualTo(scenario.ResumeStreamingExpected),
                        "Core streaming state after StopStim did not match the shared scenario.");
                    Assert.That(core.Ready(), Is.True, "Core did not remain Ready after stopping stimulation.");
                    Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
                });
            }
            finally
            {
                core?.Dispose();
                if (core == null)
                    transport.Dispose();
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        [Test]
        public async Task CreateEventBeforeModuleQueryFailsForKnownFreshCapableDevice()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var codec = new WssFrameCodec();
            await SendAsync(transport, codec, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateContactConfig, 0x01, 0x00, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateEvent, 0x01, 0x00, 0x01);

            var result = transport.Conformance.ValidateInitialization();
            var check = result.Errors.Single(item => item.Name == "ModuleQueryBeforeEventCreation");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(check.Severity, Is.EqualTo(WssConformanceSeverity.Error));
                Assert.That(check.Details, Does.Contain("CreateEvent 1 requires ModuleQuery(settings) first"));
                Assert.That(check.Target, Is.EqualTo(0x81));
            });
        }

        [Test]
        public async Task ModuleQueryAfterEventDoesNotRepairInvalidOrdering()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var codec = new WssFrameCodec();
            await SendAsync(transport, codec, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateContactConfig, 0x01, 0x00, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateEvent, 0x01, 0x00, 0x01);
            await SendAsync(transport, codec, WSSMessageIDs.ModuleQuery, 0x01);

            var result = transport.Conformance.ValidateInitialization();
            var check = result.Errors.Single(item => item.Name == "ModuleQueryBeforeEventCreation");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(check.Passed, Is.False);
                Assert.That(check.SequenceNumber, Is.EqualTo(3));
                Assert.That(check.Details, Does.Contain("Observed sequence: #3"));
            });
        }

        private static Task SendAsync(
            EmulatedWssTransport transport,
            WssFrameCodec codec,
            WSSMessageIDs messageId,
            params byte[] data)
        {
            var payload = new byte[data.Length + 2];
            payload[0] = (byte)messageId;
            payload[1] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            return transport.SendAsync(codec.Frame(0x00, 0x81, payload));
        }

        private static string CreateCoreConfigPath()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"wss-core-{Guid.NewGuid():N}.json");
            File.WriteAllText(configPath,
                "{\"maxWSS\":1,\"firmware\":\"J03\",\"broadcastTarget\":\"0x8F\",\"wssTargets\":[\"0x81\",\"0x82\",\"0x83\"]}");
            return configPath;
        }

        private static WssStimulationCore CreateCore(EmulatedWssTransport transport, string configPath)
            => new WssStimulationCore(
                transport,
                new WssStimulationCoreOptions
                {
                    ConfigPath = configPath,
                    MaxSetupTries = 1
                });

        private static async Task<WssMessageObservation> InitializeUntilStreamingAsync(
            WssStimulationCore core,
            EmulatedWssTransport transport)
        {
            core.Initialize();
            WssMessageObservation stream = null;
            for (int i = 0; i < 2000; i++)
            {
                core.Tick();
                stream = transport.Conformance.MessageHistory.LastOrDefault(IsStream);
                if (core.Started() && stream != null)
                {
                    core.Tick();
                    return stream;
                }
                await Task.Delay(1);
            }

            Assert.Fail("Core did not reach Streaming within the finite tick limit.");
            return null;
        }

        private static bool IsStream(WssMessageObservation observation)
            => observation.MessageId >= (byte)WSSMessageIDs.StreamChangeAll &&
               observation.MessageId <= (byte)WSSMessageIDs.StreamChangeNoPA;

        private static bool PayloadEquals(WssMessageObservation observation, params byte[] expected)
            => observation.Payload.SequenceEqual(expected);
    }
}
