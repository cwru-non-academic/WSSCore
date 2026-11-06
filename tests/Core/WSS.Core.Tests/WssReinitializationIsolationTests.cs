using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using Wss.Testing;
using Wss.Transports;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssReinitializationIsolationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReinitializeMustFenceWorkFromThePreviousConnection(bool holdStream)
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-reset-isolation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string config = Path.Combine(directory, "stimConfig.json");
            File.WriteAllText(config, "{\"maxWSS\":1,\"firmware\":\"J03\"}");
            var transport = new DelayedSetupTransport(holdStream);
            using var core = new WssStimulationCore(transport, new WssStimulationCoreOptions
            {
                ConfigPath = config,
                MaxSetupTries = 1
            });
            try
            {
                core.Initialize();
                for (int i = 0; i < 3000 && !transport.SetupHeld.Task.IsCompleted; i++)
                {
                    core.Tick();
                    await Task.Delay(1);
                }
                Assert.That(await Task.WhenAny(transport.SetupHeld.Task, Task.Delay(3000)),
                    Is.SameAs(transport.SetupHeld.Task), "Setup did not reach the controlled emulator boundary.");

                // These are the same underlying stack calls used by resetRadio().
                // No new-generation Tick is allowed before the observation below.
                var resetting = Task.Run(() => { core.Shutdown(); core.Initialize(); });
                Task connection = await Task.WhenAny(transport.Reconnected.Task, Task.Delay(100));
                bool reconnectedBeforeSendCompleted = connection == transport.Reconnected.Task;
                transport.ReleaseSetup.TrySetResult(true);
                Assert.That(await Task.WhenAny(resetting, Task.Delay(3000)), Is.SameAs(resetting),
                    "Reset failed to complete after the held transport operation completed.");
                await resetting;
                transport.ObserveNewGeneration = true;

                Task observed = await Task.WhenAny(transport.PreviousSetupContinued.Task, Task.Delay(500));
                Assert.That(observed, Is.Not.SameAs(transport.PreviousSetupContinued.Task),
                    "Old setup submitted a command to the new connection before its first Tick. " +
                    "Shutdown/Initialize did not fence the old setup worker.");
                Assert.That(reconnectedBeforeSendCompleted, Is.False,
                    "Reset reconnected while an old setup/stream send was still in flight.");
            }
            finally
            {
                transport.ReleaseSetup.TrySetResult(true);
                core.Shutdown();
                Directory.Delete(directory, true);
            }
        }

        // The emulator replies normally, then the first send remains in flight.
        // This models a real asynchronous transport whose send completion can lag
        // its receive callback. No physical transport or private-field injection.
        private sealed class DelayedSetupTransport : ITransport
        {
            private readonly EmulatedWssTransport inner = new EmulatedWssTransport();
            private int firstSend;
            private readonly bool holdStream;
            internal DelayedSetupTransport(bool holdStream) { this.holdStream = holdStream; }
            private int connections;
            public readonly TaskCompletionSource<bool> SetupHeld = Signal();
            public readonly TaskCompletionSource<bool> ReleaseSetup = Signal();
            public readonly TaskCompletionSource<bool> PreviousSetupContinued = Signal();
            public readonly TaskCompletionSource<bool> Reconnected = Signal();
            public volatile bool ObserveNewGeneration;
            public bool IsConnected => inner.IsConnected;
            public event Action<byte[]> BytesReceived
            {
                add => inner.BytesReceived += value;
                remove => inner.BytesReceived -= value;
            }
            public Task ConnectAsync(CancellationToken ct = default)
            {
                if (Interlocked.Increment(ref connections) > 1) Reconnected.TrySetResult(true);
                return inner.ConnectAsync(ct);
            }
            public Task DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
            public async Task SendAsync(byte[] data, CancellationToken ct = default)
            {
                if (ObserveNewGeneration) PreviousSetupContinued.TrySetResult(true);
                await inner.SendAsync(data, ct);
                byte message = new WssFrameCodec().Deframe(data).Single()[2];
                bool stream = message >= (byte)WSSMessageIDs.StreamChangeAll && message <= (byte)WSSMessageIDs.StreamChangeNoPA;
                if (stream == holdStream && Interlocked.Increment(ref firstSend) == 1)
                {
                    SetupHeld.TrySetResult(true);
                    await ReleaseSetup.Task;
                }
            }
            public void Dispose() => inner.Dispose();
            private static TaskCompletionSource<bool> Signal() =>
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
