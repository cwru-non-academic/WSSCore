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
}

internal sealed class ManagedAssemblyManifest
{
    public string Identity { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Culture { get; set; } = string.Empty;
    public string PublicKeyToken { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
}

internal static class Program
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
            if (args.Length == 0 || (args[0] != "stage" && args[0] != "validate" && args[0] != "describe") ||
                (args[0] == "stage" && args.Length != 7) ||
                ((args[0] == "validate" || args[0] == "describe") && args.Length != 5))
            {
                throw new ArgumentException("Usage: ArtifactBuild stage|validate|describe --manifest <path> --source|--package <path>.");
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

            Validate(manifest, args[4]);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ArtifactBuild: " + exception.Message);
            return 1;
        }
    }

    private static Manifest ReadManifest(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Manifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Manifest is empty.");
    }

    private static void Validate(Manifest manifest, string packageDirectory)
    {
        var expected = new HashSet<string>(manifest.Files.Select(Normalize), PathComparer);
        var declaredManagedPaths = new HashSet<string>(manifest.ManagedAssemblies.Keys.Select(Normalize), PathComparer);
        var declaredNativePaths = new HashSet<string>(manifest.NativeFiles.Select(Normalize), PathComparer);
        if (!declaredManagedPaths.Concat(declaredNativePaths).ToHashSet(PathComparer).SetEquals(expected))
        {
            throw new InvalidDataException("Manifest must classify every staged file as a managed assembly or native file.");
        }

        if (declaredNativePaths.Any(path => !path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Native manifest entries must be .so files.");
        }
        var actual = new HashSet<string>(Directory.GetFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Normalize(Path.GetRelativePath(packageDirectory, path))), PathComparer);
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
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        foreach (var relativePath in manifest.Files)
        {
            var source = Path.Combine(sourceDirectory, relativePath);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Manifest file was not produced by the build.", source);
            }

            var destination = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
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

    private sealed record AssemblyInfo(string Name, string Version, string Culture, string PublicKeyToken,
        string TargetFramework, HashSet<string> References);
}
