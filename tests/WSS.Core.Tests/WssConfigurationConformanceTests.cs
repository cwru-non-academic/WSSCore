using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssConfigurationConformanceTests
    {
        [Test]
        public async Task MinimalConfigurationWithArbitraryIdsPasses()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, true);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors, Is.Empty);
                Assert.That(result.Checks.Any(item => item.Name == "RunnableConfigurationBeforeStart"), Is.True);
            });
        }

        [Test]
        public async Task EventRatioEditIsOptional()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 19, 17, 14, 42, false);

            var result = transport.Conformance.ValidateInitialization();

            Assert.That(result.Passed, Is.True, JoinFailures(result));
            Assert.That(result.Checks.Any(item => item.Name.Contains("Ratio")), Is.False);
        }

        [Test]
        public async Task UnusedResourcesProduceWarningsWithoutFailing()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);
            await SendAsync(transport, WSSMessageIDs.CreateSchedule, 4, 0, 13, 170);
            await SendAsync(transport, WSSMessageIDs.AddEventToSchedule, 7, 4);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 10, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 8, 0, 9);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 11, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateSchedule, 12, 0, 13, 170);
            await SendAsync(transport, WSSMessageIDs.SyncGroup, 170);
            await SendAsync(transport, WSSMessageIDs.StimulationSwitch, 0x03);
            await SendStreamAsync(transport);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Warnings.Any(item => item.Name == "UnassignedEvent" && item.Details.Contains("Event 8")), Is.True);
                Assert.That(result.Warnings.Any(item => item.Name == "UnusedContactConfig" && item.Details.Contains("ContactConfig 10")), Is.True);
                Assert.That(result.Warnings.Any(item => item.Name == "UnusedContactConfig" && item.Details.Contains("ContactConfig 11")), Is.True);
                Assert.That(result.Warnings.Any(item => item.Name == "EmptySchedule" && item.Details.Contains("Schedule 12")), Is.True);
            });
        }

        [Test]
        public async Task KnownCleanCreateEventWithMissingContactFails()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);

            var result = transport.Conformance.ValidateInitialization();

            AssertError(result, "EventContactExists");
        }

        [TestCase(true, false, "AssignmentScheduleExists")]
        [TestCase(false, true, "AssignmentEventExists")]
        public async Task AssignmentToKnownMissingResourceFails(
            bool createEvent,
            bool createSchedule,
            string expectedFinding)
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            if (createEvent)
                await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);
            if (createSchedule)
                await SendAsync(transport, WSSMessageIDs.CreateSchedule, 4, 0, 13, 170);
            await SendAsync(transport, WSSMessageIDs.AddEventToSchedule, 7, 4);

            AssertError(transport.Conformance.ValidateInitialization(), expectedFinding);
        }

        [Test]
        public async Task SynchronizationWithoutMatchingKnownScheduleFails()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);
            await SendAsync(transport, WSSMessageIDs.CreateSchedule, 4, 0, 13, 170);
            await SendAsync(transport, WSSMessageIDs.AddEventToSchedule, 7, 4);
            await SendAsync(transport, WSSMessageIDs.SyncGroup, 42);
            await SendAsync(transport, WSSMessageIDs.StimulationSwitch, 0x03);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                AssertError(result, "SynchronizationMatchesSchedule");
                AssertError(result, "RunnableConfigurationBeforeStart");
            });
        }

        [Test]
        public async Task StartWithoutRunnableScheduleFails()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);
            await SendAsync(transport, WSSMessageIDs.CreateSchedule, 4, 0, 13, 170);
            await SendAsync(transport, WSSMessageIDs.SyncGroup, 170);
            await SendAsync(transport, WSSMessageIDs.StimulationSwitch, 0x03);

            AssertError(transport.Conformance.ValidateInitialization(), "RunnableConfigurationBeforeStart");
        }

        [TestCase(WSSMessageIDs.StreamChangeAll)]
        [TestCase(WSSMessageIDs.StreamChangeNoIPI)]
        [TestCase(WSSMessageIDs.StreamChangeNoPW)]
        [TestCase(WSSMessageIDs.StreamChangeNoPA)]
        public async Task StreamingBeforeStartFails(WSSMessageIDs messageId)
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendStreamAsync(transport, messageId);

            AssertError(transport.Conformance.ValidateInitialization(), "StimulationStartedBeforeStreaming");
        }

        [Test]
        public async Task StreamingAfterStopFails()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.StimulationSwitch, 0x04);
            await SendStreamAsync(transport);

            AssertError(transport.Conformance.ValidateInitialization(), "StimulationStartedBeforeStreaming");
        }

        [Test]
        public async Task EditOfUnobservedEventWithoutCleanBaselineIsNotVerifiable()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.EditEventConfig, 7, 0x02, 1, 1, 1);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "EditedEventExists"), Is.False);
                Assert.That(result.NotVerifiable.Any(item =>
                    item.Name == "EditedEventExists" && item.Details.Contains("cannot be verified")), Is.True);
            });
        }

        [TestCase(0x01)]
        [TestCase(0x02)]
        [TestCase(0x04)]
        [TestCase(0x05)]
        [TestCase(0x06)]
        [TestCase(0x07)]
        [TestCase(0x08)]
        public async Task EditOfKnownMissingEventFails(byte subcommand)
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendEventEditAsync(transport, 7, subcommand);

            AssertError(transport.Conformance.ValidateInitialization(), "EditedEventExists");
        }

        [Test]
        public async Task CreateEventWithoutCleanBaselineDoesNotRequireModuleQuery()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "ModuleQueryBeforeEventCreation"), Is.False);
                Assert.That(result.Warnings.Any(item => item.Name == "UnknownBaseline"), Is.True);
            });
        }

        [Test]
        public async Task ClearAllPreservesEarlierModuleQuery()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "ModuleQueryBeforeEventCreation"), Is.False);
                Assert.That(result.Checks.Any(item => item.Name == "KnownCleanBaseline"), Is.True);
            });
        }

        [Test]
        public async Task LegacyProfileDoesNotRequireUnavailableModuleQuery()
        {
            using var transport = new EmulatedWssTransport(EmulatedWssDeviceProfile.Legacy);
            await transport.ConnectAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "ModuleQueryBeforeEventCreation"), Is.False);
            });
        }

        [Test]
        public async Task SynchronizationAgainstUnknownPreExistingSchedulesIsNotVerifiable()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.SyncGroup, 42);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.NotVerifiable.Any(item => item.Name == "SynchronizationMatchesSchedule"), Is.True);
            });
        }

        [Test]
        public async Task DeletingReferencedContactFails()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);
            await SendAsync(transport, WSSMessageIDs.DeleteContactConfig, 9);

            AssertError(transport.Conformance.ValidateInitialization(), "DeleteReferencedContact");
        }

        [Test]
        public async Task PreResetStartCannotAuthorizePostResetStreaming()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.Reset);
            await SendStreamAsync(transport);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                AssertError(result, "StimulationStartedBeforeStreaming");
                Assert.That(result.Errors.Any(item => item.Name == "ResetDuringConfiguration"), Is.False);
                Assert.That(result.Errors.Single(item => item.Name == "StimulationStartedBeforeStreaming").Details,
                    Does.Contain("Reset invalidated the previous lifecycle state"));
            });
        }

        [Test]
        public async Task ResetInvalidatesEarlierModuleQuery()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.Reset);
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 19, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 17, 0, 19);

            AssertError(transport.Conformance.ValidateInitialization(), "ModuleQueryBeforeEventCreation");
        }

        [Test]
        public async Task ResetMakesPriorConfigurationExistenceNotVerifiable()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.Reset);
            await SendEventEditAsync(transport, 7, 0x02);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "EditedEventExists"), Is.False);
                Assert.That(result.NotVerifiable.Any(item =>
                    item.Name == "EditedEventExists" && item.Details.Contains("cannot be verified")), Is.True);
                Assert.That(result.Warnings.Any(item => item.Name == "UnknownBaseline"), Is.True);
            });
        }

        [Test]
        public async Task ResetAfterCompletedStartupDoesNotInvalidateHistoricalInitialization()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.Reset);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "ResetDuringConfiguration"), Is.False);
                Assert.That(result.Warnings.Any(item => item.Name == "UnknownBaseline"), Is.True);
            });
        }

        [Test]
        public async Task CompleteSetupAfterResetFormsNewValidEpoch()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.Reset);
            await SendRunnableConfigurationAsync(transport, 19, 17, 14, 42, false);

            var result = transport.Conformance.ValidateInitialization();

            Assert.That(result.Passed, Is.True, JoinFailures(result));
            Assert.That(result.Errors, Is.Empty);
        }

        [Test]
        public async Task ResetAllowsRecoveryFromInterruptedInitializationAttempt()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 9, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, 7, 0, 9);
            await SendAsync(transport, WSSMessageIDs.Reset);
            await SendRunnableConfigurationAsync(transport, 19, 17, 14, 42, false);

            var result = transport.Conformance.ValidateInitialization();

            Assert.That(result.Passed, Is.True, JoinFailures(result));
            Assert.That(result.Errors, Is.Empty);
        }

        [Test]
        public async Task ResetDoesNotEraseCompletedEpochFailures()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.DeleteContactConfig, 9);
            await SendAsync(transport, WSSMessageIDs.Reset);

            AssertError(transport.Conformance.ValidateInitialization(), "DeleteReferencedContact");
        }

        [Test]
        public async Task ValidPostStreamEventEditIsEvaluatedAndPasses()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            long firstStreamSequence = transport.Conformance.MessageHistory.Last(IsStream).SequenceNumber;

            await SendEventEditAsync(transport, 7, 0x07);
            var edit = transport.Conformance.MessageHistory.Last(item =>
                item.MessageId == (byte)WSSMessageIDs.EditEventConfig);
            await SendStreamAsync(transport);
            var secondStream = transport.Conformance.MessageHistory.Last(IsStream);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(edit.Payload[2], Is.EqualTo(7));
                Assert.That(edit.Payload[3], Is.EqualTo(0x07));
                Assert.That(edit.SequenceNumber, Is.GreaterThan(firstStreamSequence));
                Assert.That(secondStream.SequenceNumber, Is.GreaterThan(edit.SequenceNumber));
            });
        }

        [Test]
        public async Task InvalidPostStreamTransitionFails()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            long firstStreamSequence = transport.Conformance.MessageHistory.Last(IsStream).SequenceNumber;

            await SendAsync(transport, WSSMessageIDs.DeleteContactConfig, 9);

            var result = transport.Conformance.ValidateInitialization();
            var error = result.Errors.Single(item => item.Name == "DeleteReferencedContact");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(error.SequenceNumber, Is.GreaterThan(firstStreamSequence));
                Assert.That(error.Details, Does.Contain("Event 7"));
            });
        }

        [Test]
        public async Task EventEditPreservesScheduleSynchronizationWithoutResync()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            long firstStreamSequence = transport.Conformance.MessageHistory.Last(IsStream).SequenceNumber;

            await SendEventEditAsync(transport, 7, 0x02);
            var edit = transport.Conformance.MessageHistory.Last(item =>
                item.MessageId == (byte)WSSMessageIDs.EditEventConfig);
            await SendStreamAsync(transport);
            var secondStream = transport.Conformance.MessageHistory.Last(IsStream);

            var result = transport.Conformance.ValidateInitialization();
            bool redundantSync = transport.Conformance.MessageHistory.Any(item =>
                item.SequenceNumber > edit.SequenceNumber &&
                item.SequenceNumber < secondStream.SequenceNumber &&
                item.MessageId == (byte)WSSMessageIDs.SyncGroup);

            Assert.Multiple(() =>
            {
                Assert.That(edit.SequenceNumber, Is.GreaterThan(firstStreamSequence));
                Assert.That(secondStream.SequenceNumber, Is.GreaterThan(edit.SequenceNumber));
                Assert.That(redundantSync, Is.False);
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(result.Errors.Any(item => item.Name == "RunnableConfigurationBeforeStreaming"), Is.False);
            });
        }

        [Test]
        public async Task PostStreamConfigurationContributesFinalStateWarnings()
        {
            using var transport = await CreateConnectedTransportAsync();
            await SendRunnableConfigurationAsync(transport, 9, 7, 4, 170, false);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, 99, 0, 0);

            var result = transport.Conformance.ValidateInitialization();

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.True, JoinFailures(result));
                Assert.That(transport.Conformance.MessageHistory.Any(item =>
                    item.MessageId == (byte)WSSMessageIDs.CreateContactConfig && item.Payload[2] == 99), Is.True);
                Assert.That(result.Warnings.Any(item =>
                    item.Name == "UnusedContactConfig" && item.Details.Contains("ContactConfig 99")), Is.True);
            });
        }

        private static async Task<EmulatedWssTransport> CreateConnectedTransportAsync()
        {
            var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            return transport;
        }

        private static async Task SendRunnableConfigurationAsync(
            EmulatedWssTransport transport,
            byte contactId,
            byte eventId,
            byte scheduleId,
            byte syncSignal,
            bool includeRatio)
        {
            await SendAsync(transport, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, WSSMessageIDs.ModuleQuery, 0x01);
            await SendAsync(transport, WSSMessageIDs.CreateContactConfig, contactId, 0, 0);
            await SendAsync(transport, WSSMessageIDs.CreateEvent, eventId, 0, contactId);
            if (includeRatio)
                await SendAsync(transport, WSSMessageIDs.EditEventConfig, eventId, 0x07, 1);
            await SendAsync(transport, WSSMessageIDs.CreateSchedule, scheduleId, 0, 13, syncSignal);
            await SendAsync(transport, WSSMessageIDs.AddEventToSchedule, eventId, scheduleId);
            await SendAsync(transport, WSSMessageIDs.SyncGroup, syncSignal);
            await SendAsync(transport, WSSMessageIDs.StimulationSwitch, 0x03);
            await SendStreamAsync(transport);
        }

        private static Task SendStreamAsync(
            EmulatedWssTransport transport,
            WSSMessageIDs messageId = WSSMessageIDs.StreamChangeAll)
            => SendAsync(transport, messageId, 1, 2, 3, 4, 5, 6, 13, 13, 13);

        private static Task SendEventEditAsync(
            EmulatedWssTransport transport,
            byte eventId,
            byte subcommand)
        {
            switch (subcommand)
            {
                case 0x01:
                    return SendAsync(transport, WSSMessageIDs.EditEventConfig, eventId, subcommand, 9);
                case 0x02:
                    return SendAsync(transport, WSSMessageIDs.EditEventConfig, eventId, subcommand, 1, 1, 1);
                case 0x04:
                    return SendAsync(transport, WSSMessageIDs.EditEventConfig, eventId, subcommand, 1, 1, 1, 1, 1, 1, 1, 1);
                case 0x05:
                    return SendAsync(transport, WSSMessageIDs.EditEventConfig, eventId, subcommand, 0, 0);
                default:
                    return SendAsync(transport, WSSMessageIDs.EditEventConfig, eventId, subcommand, 1);
            }
        }

        private static Task SendAsync(
            EmulatedWssTransport transport,
            WSSMessageIDs messageId,
            params byte[] data)
        {
            var payload = new byte[data.Length + 2];
            payload[0] = (byte)messageId;
            payload[1] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            return transport.SendAsync(new WssFrameCodec().Frame(0x00, 0x81, payload));
        }

        private static bool IsStream(WssMessageObservation observation)
            => observation.MessageId >= (byte)WSSMessageIDs.StreamChangeAll &&
               observation.MessageId <= (byte)WSSMessageIDs.StreamChangeNoPA;

        private static void AssertError(InitializationConformanceResult result, string name)
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.Errors.Any(item =>
                item.Name == name && item.Severity == WssConformanceSeverity.Error), Is.True,
                $"Expected error {name}.{Environment.NewLine}{JoinFailures(result)}");
        }

        private static string JoinFailures(InitializationConformanceResult result)
            => string.Join(Environment.NewLine, result.Failures);
    }
}
