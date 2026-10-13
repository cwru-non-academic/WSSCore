using System.Reflection;
using System.Runtime.Versioning;
using NUnit.Framework;
using Wss.Transports;
using Wss.Transports.Backends.Linux;

namespace WSS.BLE.Tests;

[TestFixture]
public sealed class LinuxCompatibilityTests
{
    [Test]
    public void BackendAndBluetoothPackageUseStandardAssets()
    {
        foreach (var assembly in new[] { typeof(LinuxBleBackend).Assembly, typeof(Linux.Bluetooth.BlueZManager).Assembly })
            Assert.That(assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName, Is.EqualTo(".NETStandard,Version=v2.0"));
    }

    [Test]
    public async Task CancellationOnlyStopsManagedWaitAndDoesNotCancelDbusTask()
    {
        var type = typeof(LinuxBleBackend).Assembly.GetType("Wss.Transports.Backends.Linux.TaskCancellation", true)!;
        var method = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => !m.IsGenericMethod);
        var underlying = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource();
        var wait = (Task)method.Invoke(null, new object[] { underlying.Task, cts.Token })!;
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(underlying.Task.IsCompleted, Is.False);
        underlying.SetResult(true);
        await underlying.Task;
    }
}
