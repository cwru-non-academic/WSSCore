using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArtifactBuild;

internal sealed class Manifest
{
    public string PrimaryAssembly { get; set; } = string.Empty;
    public string PrimaryTargetFramework { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
    public List<string> RequiredAssemblies { get; set; } = new();
    public List<string> ForbiddenAssemblies { get; set; } = new();
    public Dictionary<string, ManagedAssemblyManifest> ManagedAssemblies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> NativeFiles { get; set; } = new();
    public List<string> ForbiddenDirectories { get; set; } = new();
    public Dictionary<string, PackageOverride> PackageOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SourceFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore]
    public string ManifestDirectory { get; set; } = string.Empty;
}

internal sealed class PackageOverride
{
    public string AssetsFile { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string ReferenceAssembly { get; set; } = string.Empty;
}

internal sealed class ManagedAssemblyManifest
{
    public string Identity { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Culture { get; set; } = string.Empty;
    public string PublicKeyToken { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
}

internal static partial class Program
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".pdb", ".xml", ".deps.json"
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "build-all")
            {
                BuildUniversal(args.Skip(1).ToArray());
                return 0;
            }

            if (args.Length == 5 && args[0] == "archive" && args[1] == "--package" && args[3] == "--tag")
            {
                var universalManifest = ReadManifest("tools/ArtifactBuild/manifests/universal.json");
                Validate(universalManifest, args[2]);
                ArchiveUniversal(args[2], args[4]);
                return 0;
            }

            if (args.Length == 0 || (args[0] != "stage" && args[0] != "validate" && args[0] != "describe" && args[0] != "references") ||
                (args[0] == "stage" && args.Length != 7) ||
                ((args[0] == "validate" || args[0] == "describe" || args[0] == "references") && args.Length != 5))
            {
                throw new ArgumentException("Usage: ArtifactBuild build-all | stage|validate|describe --manifest <path> --source|--package <path>.");
            }

            var manifest = ReadManifest(args[2]);
            if (args[1] != "--manifest")
            {
                throw new ArgumentException("The manifest must be supplied with --manifest.");
            }

            if (args[0] == "stage")
            {
                if (args[3] != "--source" || args[5] != "--destination")
                {
                    throw new ArgumentException("stage requires --source <directory> --destination <directory>.");
                }

                Stage(manifest, args[4], args[6]);
                Validate(manifest, args[6]);
                return 0;
            }

            if (args[3] != "--package")
            {
                throw new ArgumentException(args[0] + " requires --package <directory>.");
            }

            if (args[0] == "describe")
            {
                Describe(manifest, args[4]);
                return 0;
            }

            if (args[0] == "references")
            {
                Console.WriteLine(JsonSerializer.Serialize(manifest.ManagedAssemblies.Keys.ToDictionary(p => p,
                    p => ReadReferences(Path.Combine(args[4], p))), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            Validate(manifest, args[4]);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ArtifactBuild: " + exception.Message);
            return 1;
        }
    }

    private static void RunBuild(string repositoryRoot, string projectPath, params string[] additionalArguments)
    {
        Console.WriteLine("Building " + projectPath + " (Release)...");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");
        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet build for " + projectPath + ".");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("dotnet build failed for " + projectPath + " with exit code " + process.ExitCode + ".");
        }
    }

    private static Manifest ReadManifest(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Manifest is empty.");
        manifest.ManifestDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return manifest;
    }

    private static void Validate(Manifest manifest, string packageDirectory)
    {
        foreach (var directory in Directory.GetDirectories(packageDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Normalize(Path.GetRelativePath(packageDirectory, directory));
            if (manifest.ForbiddenDirectories.Any(forbidden =>
                relative.Equals(Normalize(forbidden).TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith("/" + Normalize(forbidden).TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(Normalize(forbidden).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Forbidden package directory is present: " + relative);
            }
        }
        var expected = new HashSet<string>(manifest.Files.Select(Normalize), PathComparer);
        if (expected.Count != manifest.Files.Count) throw new InvalidDataException("Duplicate manifest path.");
        var declaredManagedPaths = new HashSet<string>(manifest.ManagedAssemblies.Keys.Select(Normalize), PathComparer);
        var declaredNativePaths = new HashSet<string>(manifest.NativeFiles.Select(Normalize), PathComparer);
        if (!declaredManagedPaths.Concat(declaredNativePaths).ToHashSet(PathComparer).SetEquals(expected))
        {
            throw new InvalidDataException("Manifest must classify every staged file as a managed assembly or native file.");
        }

        if (declaredNativePaths.Any(path => !path.EndsWith(".so", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("backends/windows/WssBleWindows.dll", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Unrecognized native payload.");
        }
        var physicalFiles = Directory.GetFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Normalize(Path.GetRelativePath(packageDirectory, path))).ToArray();
        var actual = new HashSet<string>(physicalFiles, PathComparer);
        if (actual.Count != physicalFiles.Length) throw new InvalidDataException("Case-colliding duplicate physical package path.");
        if (!expected.SetEquals(actual))
        {
            throw new InvalidDataException("Package file set differs from its exact manifest. Missing: " +
                string.Join(", ", expected.Except(actual)) + "; unexpected: " + string.Join(", ", actual.Except(expected)));
        }

        if (actual.Any(path => ForbiddenExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ||
                               path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                               path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Package contains a forbidden source or build file.");
        }

        var assemblies = new List<AssemblyInfo>();
        foreach (var relativePath in declaredManagedPaths)
        {
            if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(relativePath + " is declared as managed but is not a DLL.");
            }

            var info = ReadAssembly(Path.Combine(packageDirectory, relativePath));
            if (info == null)
            {
                throw new InvalidDataException(relativePath + " is not a managed assembly.");
            }

            var expectedAssembly = manifest.ManagedAssemblies[relativePath];
            if (!string.Equals(expectedAssembly.Identity, info.Name, StringComparison.Ordinal) ||
                !string.Equals(expectedAssembly.Version, info.Version, StringComparison.Ordinal) ||
                !string.Equals(NormalizeCulture(expectedAssembly.Culture), info.Culture, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NormalizePublicKeyToken(expectedAssembly.PublicKeyToken), info.PublicKeyToken, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(expectedAssembly.TargetFramework, info.TargetFramework, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(relativePath + " does not match its declared assembly identity or target framework.");
            }

            assemblies.Add(info);
        }

        var primary = assemblies.SingleOrDefault(assembly => string.Equals(assembly.Name, Path.GetFileNameWithoutExtension(manifest.PrimaryAssembly), StringComparison.OrdinalIgnoreCase));
        if (primary == null ||
            !string.Equals(primary.TargetFramework, manifest.PrimaryTargetFramework, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Primary assembly identity or TargetFrameworkAttribute does not match the manifest.");
        }

        foreach (var dependency in manifest.RequiredAssemblies)
        {
            if (!assemblies.Any(assembly => string.Equals(assembly.Name, Path.GetFileNameWithoutExtension(dependency), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Required dependency is missing: " + dependency);
            }
        }

        foreach (var forbidden in manifest.ForbiddenAssemblies)
        {
            if (assemblies.Any(assembly => string.Equals(assembly.Name, Path.GetFileNameWithoutExtension(forbidden), StringComparison.OrdinalIgnoreCase)) ||
                assemblies.Any(assembly => assembly.References.Contains(Path.GetFileNameWithoutExtension(forbidden))))
            {
                throw new InvalidDataException("Forbidden dependency is present: " + forbidden);
            }
        }

        if (manifest.SourceFiles.Count != 0) ValidateUniversal(manifest, packageDirectory);
        Console.WriteLine("Validated " + packageDirectory + " against exact manifest.");
    }

    private static void Describe(Manifest manifest, string packageDirectory)
    {
        var identities = new Dictionary<string, ManagedAssemblyManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in manifest.ManagedAssemblies.Keys.Select(Normalize).OrderBy(path => path, PathComparer))
        {
            var info = ReadAssembly(Path.Combine(packageDirectory, relativePath))
                ?? throw new InvalidDataException(relativePath + " is not a managed assembly.");
            identities.Add(relativePath, new ManagedAssemblyManifest
            {
                Identity = info.Name,
                Version = info.Version,
                Culture = info.Culture,
                PublicKeyToken = info.PublicKeyToken,
                TargetFramework = info.TargetFramework
            });
        }

        Console.WriteLine(JsonSerializer.Serialize(identities, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Stage(Manifest manifest, string sourceDirectory, string destinationDirectory)
    {
        var overrides = manifest.PackageOverrides.ToDictionary(entry => entry.Key,
            entry => ResolvePackageOverride(manifest, entry.Key, entry.Value, sourceDirectory), PathComparer);
        if (overrides.Keys.Any(path => !manifest.Files.Contains(path, PathComparer)))
        {
            throw new InvalidDataException("Package overrides must name manifest files.");
        }
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        foreach (var relativePath in manifest.Files)
        {
            var source = overrides.TryGetValue(relativePath, out var approvedSource)
                ? approvedSource : manifest.SourceFiles.TryGetValue(relativePath, out var projectSource)
                    ? Path.GetFullPath(Path.Combine(manifest.ManifestDirectory, projectSource))
                    : Path.Combine(sourceDirectory, relativePath);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Manifest file was not produced by the build.", source);
            }

            var destination = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            if (approvedSource != null)
            {
                using var sourceStream = File.OpenRead(source);
                using var destinationStream = File.OpenRead(destination);
                if (!SHA256.HashData(sourceStream).SequenceEqual(SHA256.HashData(destinationStream)))
                {
                    throw new InvalidDataException("Staged package override differs from approved source: " + relativePath);
                }
                Console.WriteLine("Verified package override: " + relativePath + " <- " + source);
            }
        }
    }

    private static string ResolvePackageOverride(Manifest manifest, string file, PackageOverride entry, string sourceDirectory)
    {
        if (Path.IsPathRooted(entry.AssetsFile) || Path.IsPathRooted(entry.Asset) ||
            Normalize(entry.Asset).Split('/').Any(part => part == ".."))
        {
            throw new InvalidDataException("Package override paths must be relative; assets must remain inside the package.");
        }
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifest.ManifestDirectory, entry.AssetsFile)));
        var library = assets.RootElement.GetProperty("libraries").GetProperty(entry.Package);
        if (library.GetProperty("type").GetString() != "package" ||
            !library.GetProperty("files").EnumerateArray().Any(asset => asset.GetString() == entry.Asset))
        {
            throw new InvalidDataException("Override asset is not declared by the restored package: " + entry.Asset);
        }
        var packagePath = library.GetProperty("path").GetString()!;
        string ResolveAsset(string asset) => assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => Path.Combine(folder.Name, packagePath, asset)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Restored package asset was not found: " + asset);
        var runtimePath = ResolveAsset(entry.Asset);
        if (manifest.NativeFiles.Contains(file, PathComparer)) return runtimePath;
        var compileAsset = GetRestoredTarget(assets.RootElement.GetProperty("targets"), entry.Target)
            .GetProperty(entry.Package).GetProperty("compile").EnumerateObject()
            .Single(asset => Path.GetFileName(asset.Name).Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)).Name;
        var runtime = ReadAssembly(runtimePath) ?? throw new InvalidDataException("Override is not managed.");
        var compile = ReadAssembly(ResolveAsset(compileAsset)) ?? throw new InvalidDataException("Compile asset is not managed.");
        var reference = ReadReferences(Path.Combine(sourceDirectory, entry.ReferenceAssembly))
            .Single(identity => identity.Name == runtime.Name);
        var identity = new AssemblyIdentity(runtime.Name, runtime.Version, runtime.Culture, runtime.PublicKeyToken);
        if (identity != new AssemblyIdentity(compile.Name, compile.Version, compile.Culture, compile.PublicKeyToken) ||
            identity != reference)
        {
            throw new InvalidDataException("Package override identity differs from compile asset or emitted reference: " + file);
        }
        Console.WriteLine("Compatible compile/reference/runtime identity: " + identity);
        return runtimePath;
    }

    private static List<AssemblyIdentity> ReadReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(handle =>
        {
            var reference = metadata.GetAssemblyReference(handle);
            var key = reference.PublicKeyOrToken.IsNil ? Array.Empty<byte>() : metadata.GetBlobBytes(reference.PublicKeyOrToken);
            var token = (reference.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0
                ? GetPublicKeyToken(key) : key.Length == 0 ? "null" : Convert.ToHexString(key).ToLowerInvariant();
            return new AssemblyIdentity(metadata.GetString(reference.Name), reference.Version.ToString(),
                NormalizeCulture(reference.Culture.IsNil ? string.Empty : metadata.GetString(reference.Culture)), token);
        }).ToList();
    }

    private static AssemblyInfo? ReadAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return null;
        }

        var metadata = peReader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var targetFramework = string.Empty;
        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (GetAttributeTypeName(metadata, attribute.Constructor) != "System.Runtime.Versioning.TargetFrameworkAttribute")
            {
                continue;
            }

            var reader = metadata.GetBlobReader(attribute.Value);
            if (reader.ReadUInt16() == 1)
            {
                targetFramework = reader.ReadSerializedString() ?? string.Empty;
            }
        }

        var publicKey = definition.PublicKey.IsNil ? Array.Empty<byte>() : metadata.GetBlobBytes(definition.PublicKey);
        return new AssemblyInfo(metadata.GetString(definition.Name), definition.Version.ToString(),
            NormalizeCulture(definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture)),
            GetPublicKeyToken(publicKey), targetFramework,
            metadata.AssemblyReferences.Select(reference => metadata.GetString(metadata.GetAssemblyReference(reference).Name)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeCulture(string culture) =>
        string.IsNullOrWhiteSpace(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase)
            ? "neutral"
            : culture;

    private static string NormalizePublicKeyToken(string publicKeyToken) =>
        string.IsNullOrWhiteSpace(publicKeyToken) || string.Equals(publicKeyToken, "null", StringComparison.OrdinalIgnoreCase)
            ? "null"
            : publicKeyToken;

    private static string GetPublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return "null";
        }

        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);
        return string.Concat(hash.Reverse().Take(8).Select(value => value.ToString("x2")));
    }

    private static string GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        if (constructor.Kind != HandleKind.MemberReference)
        {
            return string.Empty;
        }

        var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference)
        {
            return string.Empty;
        }

        var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
        return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record AssemblyIdentity(string Name, string Version, string Culture, string PublicKeyToken);

    private sealed record AssemblyInfo(string Name, string Version, string Culture, string PublicKeyToken,
        string TargetFramework, HashSet<string> References);
}
