using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArtifactBuild;

internal static partial class Program
{
    private static void BuildUniversal(string[] args)
    {
        string destination = Path.GetFullPath("artifacts/universal"), tag = "local";
        bool managedAlreadyBuilt = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--destination" when i + 1 < args.Length: destination = Path.GetFullPath(args[++i]); break;
                case "--tag" when i + 1 < args.Length: tag = args[++i]; break;
                case "--no-build": managedAlreadyBuilt = true; break;
                default: throw new ArgumentException("build-all [--destination <new directory>] [--tag <tag>] [--no-build]");
            }
        }
        ValidateTag(tag);
        // Never replace historical artifacts or an arbitrary user directory.
        if (Directory.Exists(destination)) throw new IOException("Universal destination must be new: " + destination);
        var root = Directory.GetCurrentDirectory();
        if (!managedAlreadyBuilt)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Production Windows native builds require Windows. Validate the same completed universal payload on Linux.");
            RunBuild(root, "WSS.Transport.BLE.Windows/WSS.Transport.BLE.Windows.csproj", "-p:RestoreLockedMode=true");
            RunBuild(root, "WSS.Transport.BLE.Linux/WSS.Transport.BLE.Linux.csproj", "--no-dependencies", "-p:RestoreLockedMode=true");
            // Native source is built, never sourced from a candidate or a prior distribution.
            RunProcess("powershell", root, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools/WindowsBleBridge/build.ps1", "-ProductionOnly");
        }
        var manifest = ReadManifest(Path.Combine(root, "tools/ArtifactBuild/manifests/universal.json"));
        var common = Path.Combine(root, "WSS.Transport.BLE/bin/Release/netstandard2.0");
        VerifyRestoredClosure(manifest, common);
        Stage(manifest, common, destination);
        Validate(manifest, destination);
        ArchiveUniversal(destination, tag);
    }

    private static void RunProcess(string tool, string root, params string[] args)
    {
        var start = new ProcessStartInfo(tool) { WorkingDirectory = root, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + tool);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(tool + " failed: " + process.ExitCode);
    }

    private static void VerifyRestoredClosure(Manifest manifest, string common)
    {
        foreach (var project in new[] { "WSS.Transport.BLE", "WSS.Transport.BLE.Windows", "WSS.Transport.BLE.Linux" })
        {
            var projectRoot = Path.GetFullPath(Path.Combine(manifest.ManifestDirectory, "../../..", project));
            var output = Path.Combine(projectRoot, "bin/Release/netstandard2.0");
            // Reject an added or incompatible dependency, rather than silently omit it.
            foreach (var dll in Directory.GetFiles(output, "*.dll"))
            {
                var file = Path.GetFileName(dll);
                var paths = manifest.Files.Where(p => Path.GetFileName(p) == file && !p.StartsWith("runtimes/")).ToArray();
                if (paths.Length != 1) throw new InvalidDataException("Missing/duplicate managed closure entry: " + project + "/" + file);
                var relative = paths[0];
                if (file == "System.IO.Ports.dll") continue; // approved compile/reference-compatible runtime override
                var selected = manifest.SourceFiles.TryGetValue(relative, out var source)
                    ? Path.GetFullPath(Path.Combine(manifest.ManifestDirectory, source)) : Path.Combine(common, relative);
                if (!File.ReadAllBytes(dll).SequenceEqual(File.ReadAllBytes(selected)))
                    throw new InvalidDataException("Conflicting shared dependency bytes: " + project + "/" + file);
            }
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "obj/project.assets.json")));
            var target = GetRestoredTarget(assets.RootElement.GetProperty("targets"), "netstandard2.0");
            foreach (var package in target.EnumerateObject())
            {
                if (package.Value.GetProperty("type").GetString() != "package" || !package.Value.TryGetProperty("runtime", out var runtime)) continue;
                foreach (var asset in runtime.EnumerateObject().Where(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    var file = Path.GetFileName(asset.Name);
                    if (!File.Exists(Path.Combine(output, file))) throw new FileNotFoundException("Restored runtime asset not built: " + asset.Name);
                    if (file == "System.IO.Ports.dll" && package.Name != "System.IO.Ports/8.0.0")
                        throw new InvalidDataException("Ports must remain 8.0.0.");
                }
            }
        }
    }

    private static void ValidateUniversal(Manifest manifest, string package)
    {
        var managed = manifest.ManagedAssemblies.Keys.Select(path => (Path: path, Info: ReadAssembly(Path.Combine(package, path))!)).ToArray();
        var frameworkReferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "netstandard", "mscorlib", "System", "System.Runtime", "System.Runtime.InteropServices", "System.Threading.Overlapped",
            "System.ComponentModel.Primitives", "Microsoft.Win32.Registry", "System.Threading", "System.Threading.ThreadPool",
            "Microsoft.Win32.Primitives", "System.Text.Encoding.Extensions", "System.Threading.Thread", "System.Net.Sockets", "System.Collections"
        };
        foreach (var group in managed.Where(a => !a.Path.StartsWith("runtimes/")).GroupBy(a => a.Info.Name))
            if (group.Count() != 1) throw new InvalidDataException("Duplicate common/backend assembly identity: " + group.Key);
        foreach (var assembly in managed)
        {
            if (assembly.Info.Name.StartsWith("WSS") && assembly.Info.TargetFramework != ".NETStandard,Version=v2.0")
                throw new InvalidDataException("Nonportable WSS assembly: " + assembly.Path);
            foreach (var reference in ReadReferences(Path.Combine(package, assembly.Path)))
            {
                // .NET8 RID Ports uses the host BCL, not the portable compatibility DLL.
                if (frameworkReferences.Contains(reference.Name) || (assembly.Path.StartsWith("runtimes/") && reference.Name == "System.Memory")) continue;
                var selected = managed.FirstOrDefault(a => a.Info.Name == reference.Name);
                if (selected.Info == null) throw new InvalidDataException("Missing packaged reference: " + assembly.Path + " -> " + reference.Name);
                // These are the existing Core package's deliberately pinned NuGet unifications.
                bool approvedUpgrade = (reference.Name, reference.Version, selected.Info.Version) is
                    ("System.Numerics.Vectors", "4.1.3.0", "4.1.4.0") or
                    ("System.Runtime.CompilerServices.Unsafe", "4.0.4.1", "6.0.0.0") or
                    ("System.Buffers", "4.0.2.0", "4.0.3.0");
                if (reference.Culture != selected.Info.Culture || reference.PublicKeyToken != selected.Info.PublicKeyToken ||
                    (reference.Version != selected.Info.Version && !approvedUpgrade))
                    throw new InvalidDataException("Incompatible packaged reference: " + assembly.Path + " -> " + reference);
            }
        }
        var native = Path.Combine(package, "backends/windows/WssBleWindows.dll");
        using var stream = File.OpenRead(native);
        using var pe = new PEReader(stream);
        if (pe.HasMetadata || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus ||
            (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
            throw new InvalidDataException("Windows native payload must be an unmanaged AMD64 PE32+ DLL.");
        var reader = pe.GetSectionData(pe.PEHeaders.PEHeader.ExportTableDirectory.RelativeVirtualAddress).GetReader();
        reader.Offset = 24;
        int count = checked((int)reader.ReadUInt32());
        reader.ReadUInt32(); // AddressOfFunctions
        int namesRva = checked((int)reader.ReadUInt32());
        var names = pe.GetSectionData(namesRva).GetReader();
        var exports = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; ++i)
        {
            var name = pe.GetSectionData(checked((int)names.ReadUInt32())).GetReader();
            var text = new StringBuilder();
            byte ch;
            while ((ch = name.ReadByte()) != 0) text.Append((char)ch);
            exports.Add(text.ToString());
        }
        var expected = new[] { "wss_ble_cancel", "wss_ble_create", "wss_ble_destroy", "wss_ble_get_abi_info", "wss_ble_initialize",
            "wss_ble_nus_connect", "wss_ble_nus_disconnect", "wss_ble_nus_write", "wss_ble_query_adapter", "wss_ble_scan_start", "wss_ble_scan_stop" };
        if (!exports.SetEquals(expected)) throw new InvalidDataException("Native ABI export set differs from production ABI 1.1 (or includes test exports).");
        var elf = File.ReadAllBytes(Path.Combine(package, "runtimes/linux-x64/native/libSystem.IO.Ports.Native.so"));
        if (elf.Length < 20 || !elf.Take(5).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46, 2 }) || elf[5] != 1 || BitConverter.ToUInt16(elf, 18) != 62)
            throw new InvalidDataException("Linux serial native payload must be ELF64 little-endian x86-64.");
        Console.WriteLine("Universal shared identities, compatible references, AMD64 ABI exports, ELF64: PASS");
    }

    private static JsonElement GetRestoredTarget(JsonElement targets, string target)
    {
        if (targets.TryGetProperty(target, out var value)) return value;
        // VS MSBuild/NuGet emits the long TFM; dotnet restore emits its short alias.
        if (target == "netstandard2.0" && targets.TryGetProperty(".NETStandard,Version=v2.0", out value)) return value;
        throw new InvalidDataException("Restored assets do not contain the required framework target: " + target);
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_'))
            throw new ArgumentException("Tag must contain only ASCII letters, digits, dot, dash or underscore.");
    }

    private static void ArchiveUniversal(string package, string tag)
    {
        ValidateTag(tag);
        var archive = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(package))!, "WSS-Universal-" + tag + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var file in Directory.GetFiles(package, "*", SearchOption.AllDirectories).OrderBy(p => Normalize(Path.GetRelativePath(package, p)), StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry("WSS-Universal/" + Normalize(Path.GetRelativePath(package, file)), CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        using var content = File.OpenRead(archive);
        var checksum = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(archive)!, "SHA256SUMS.txt"), checksum + "  " + Path.GetFileName(archive) + "\n");
        Console.WriteLine("Universal release: " + archive + " SHA256=" + checksum);
    }
}
