using Wss.Ble.Windows.Native;

try
{
    var lifecycleOnly = false;
    var iterations = 1;
    var seconds = 10;
    string? nusDevice = null, writeHex = null, expectedHex = null;
    var addressType = BleAddressType.Unspecified;
    var nusSeconds = 10;
    for (var i = 0; i < args.Length; ++i)
    {
        switch (args[i])
        {
            case "--lifecycle-only": lifecycleOnly = true; break;
            case "--iterations": iterations = int.Parse(args[++i]); break;
            case "--scan-seconds": seconds = int.Parse(args[++i]); break;
            case "--nus-device": nusDevice = args[++i]; break;
            case "--address-type": addressType = Enum.Parse<BleAddressType>(args[++i], true); break;
            case "--nus-seconds": nusSeconds = int.Parse(args[++i]); break;
            case "--write-hex": writeHex = args[++i]; break;
            case "--expect-notification-hex": expectedHex = args[++i]; break;
            default: throw new ArgumentException("Unknown argument: " + args[i]);
        }
    }
    if (iterations < 1 || iterations > 10000 || seconds < 1 || seconds > 3600)
        throw new ArgumentException("Iterations must be 1..10000 and scan seconds 1..3600.");
    if (nusSeconds < 1 || nusSeconds > 3600 || (uint)addressType > 2) throw new ArgumentException("Invalid NUS duration/address type.");
    if (nusDevice == null && (writeHex != null || expectedHex != null || args.Contains("--address-type") || args.Contains("--nus-seconds")))
        throw new ArgumentException("NUS options require --nus-device.");
    if (nusDevice != null && (lifecycleOnly || args.Contains("--scan-seconds"))) throw new ArgumentException("NUS mode cannot be combined with scan/lifecycle mode.");
    var write = writeHex == null ? null : Convert.FromHexString(writeHex);
    var expected = expectedHex == null ? null : Convert.FromHexString(expectedHex);
    if (expected is { Length: 0 }) throw new ArgumentException("Expected notification must not be empty.");
    if (write != null)
    {
        Console.WriteLine($"WRITE SAFETY: target '{nusDevice}', exact bytes '{writeHex}'. This must be a designated NON-ACTUATING test peripheral.");
        Console.Write("Type NON-ACTUATING to explicitly designate this target, or press Enter to abort: ");
        if (!string.Equals(Console.ReadLine(), "NON-ACTUATING", StringComparison.Ordinal)) throw new OperationCanceledException("Safe test peripheral was not designated.");
    }
    Console.WriteLine($"Windows native BLE scanning proof; process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    var actualReception = false;
    var realScans = 0;
    using var cancelled = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };
    if (nusDevice != null)
    {
        var received = 0; var matched = false;
        for (var iteration = 1; iteration <= iterations; ++iteration)
        {
            using var client = new WindowsBleClient();
            await client.InitializeAsync(cancelled.Token);
            var info = await client.ConnectAsync(nusDevice, addressType, cancellationToken: cancelled.Token);
            Console.WriteLine($"NUS ready: connection={info.ConnectionId}, ordinary write budget={info.MaxWriteLength}, receive-only={write == null}");
            try
            {
                if (write != null) { await client.SendAsync(write, cancelled.Token); Console.WriteLine("Write-with-response completed."); }
                var deadline = DateTime.UtcNow.AddSeconds(nusSeconds);
                do
                {
                    cancelled.Token.ThrowIfCancellationRequested();
                    foreach (var bytes in client.DrainNotifications())
                    {
                        ++received; Console.WriteLine("RX " + Convert.ToHexString(bytes));
                        if (expected != null && bytes.SequenceEqual(expected)) matched = true;
                    }
                    if (client.LastConnectionError is { } error) throw error;
                    if (!client.IsConnected) throw new InvalidOperationException("NUS disconnected unexpectedly.");
                    await Task.Delay(50, cancelled.Token);
                } while (DateTime.UtcNow < deadline);
            }
            finally { await client.DisconnectAsync(); }
        }
        if (expected != null && !matched) throw new InvalidOperationException("Expected notification was not received.");
        Console.WriteLine($"PASS: NUS discovery/subscription/disconnect; notifications={received}.");
        if (received == 0) Console.WriteLine("OUTSTANDING: notification reception NOT proved.");
        return 0;
    }
    for (var iteration = 1; iteration <= iterations; ++iteration)
    {
        using var client = new WindowsBleClient();
        await client.InitializeAsync(cancelled.Token);
        var adapter = await client.QueryAdapterAsync(cancelled.Token);
        Console.WriteLine($"Iteration {iteration}/{iterations}: DLL/ABI {client.AbiVersion}, adapter: {adapter}");
        if (lifecycleOnly) continue;
        if (adapter == BleAdapterState.Absent || adapter == BleAdapterState.NoLowEnergySupport)
        {
            Console.WriteLine("SKIP hardware scanning: no usable Bluetooth LE adapter.");
            continue;
        }
        if (adapter == BleAdapterState.Off)
        {
            Console.WriteLine("SKIP hardware scanning: Bluetooth disabled/radio not on.");
            continue;
        }
        if (adapter == BleAdapterState.Unknown)
            Console.WriteLine("Radio state unknown; watcher result will determine operational availability.");
        await client.StartScanAsync(cancelled.Token);
        ++realScans;
        try
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                cancelled.Token.ThrowIfCancellationRequested();
                foreach (var device in client.DrainDiscovery())
                {
                    actualReception = true;
                    Console.WriteLine($"  {device.Id} {device.AddressType} RSSI={device.Rssi?.ToString() ?? "unknown"} name={device.Name ?? "(unnamed)"} UUIDs={string.Join(",", device.ServiceUuids)}");
                }
                if (client.LastScanError is { } error) throw error;
                await Task.Delay(100, cancelled.Token);
            }
            foreach (var device in client.DrainDiscovery())
            {
                actualReception = true;
                Console.WriteLine($"  {device.Id} {device.Name ?? "(unnamed)"}");
            }
        }
        finally { await client.StopScanAsync(); }
        Console.WriteLine($"Scan stopped; unique devices={client.GetDiscoveredDevices().Count}, dropped updates={client.DroppedAdvertisements}");
    }
    Console.WriteLine($"PASS: {iterations} initialization/query/disposal cycles.");
    if (!lifecycleOnly)
        Console.WriteLine(actualReception ? "PASS: actual advertisement reception observed." :
            $"OUTSTANDING: advertisement reception NOT proved ({realScans} scans; an empty scan is not reception evidence).");
    return 0;
}
catch (DllNotFoundException ex) { Console.Error.WriteLine("FAIL DLL loading/native dependency missing: " + ex.Message); return 2; }
catch (BadImageFormatException ex) { Console.Error.WriteLine("FAIL unsupported/mismatched native architecture or invalid DLL: " + ex.Message); return 3; }
catch (EntryPointNotFoundException ex) { Console.Error.WriteLine("FAIL native DLL export/ABI mismatch: " + ex.Message); return 4; }
catch (PlatformNotSupportedException ex) { Console.Error.WriteLine("FAIL unsupported platform/process architecture: " + ex.Message); return 3; }
catch (BleBridgeException ex)
{
    Console.Error.WriteLine($"FAIL native operation: {ex.Status}; HRESULT=0x{ex.NativeHResult:X8}; BluetoothError={ex.BluetoothError}; Stage={ex.GattStage}; GATT={ex.GattStatus}; ATT={ex.ProtocolError}: {ex.Message}");
    if (ex.Status == BleStatus.AccessDenied) Console.Error.WriteLine("Windows access/consent/policy failure; check Bluetooth permissions and policy.");
    else if (ex.Status == BleStatus.AdapterUnavailable) Console.Error.WriteLine("Bluetooth radio/adapter unavailable (removed, disabled, or absent); query result may distinguish it.");
    else if (ex.Status == BleStatus.BluetoothDisabled) Console.Error.WriteLine("Bluetooth disabled.");
    return 5;
}
catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED by user or native operation."); return 6; }
catch (TimeoutException ex) { Console.Error.WriteLine("FAIL operation timeout: " + ex.Message); return 7; }
catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
