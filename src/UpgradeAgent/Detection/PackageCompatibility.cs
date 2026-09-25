using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace UpgradeAgent.Detection;

internal enum CompatibilityStatus
{
    Compatible,
    Incompatible,
    Unknown,
}

internal sealed record CompatibilityResult(CompatibilityStatus Status, string? Detail = null);

internal interface IPackageCompatibilityChecker
{
    Task<CompatibilityResult> CheckAsync(string id, NuGetVersion version, IReadOnlyCollection<NuGetFramework> projectFrameworks, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a package's lib/ref (or dependency-group) frameworks from the global packages folder, or
/// downloads the nupkg from the repo's configured sources, honouring package source mapping.
/// Failures (for example a private feed that needs credentials) return Unknown; restore is the
/// authoritative check later (NU1202).
/// </summary>
internal sealed class NuGetPackageCompatibilityChecker : IPackageCompatibilityChecker, IDisposable
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettings _settings;
    private readonly SourceCacheContext _cache = new();

    /// <param name="settings">NuGet settings as the repo sees them (its nuget.config, source mapping, packages folder).</param>
    public NuGetPackageCompatibilityChecker(ISettings settings) => _settings = settings;

    /// <summary>Loads NuGet settings the way restore would from <paramref name="repoRoot"/>.</summary>
    public static NuGetPackageCompatibilityChecker ForRepo(string repoRoot) => new(Settings.LoadDefaultSettings(repoRoot));

    public async Task<CompatibilityResult> CheckAsync(
        string id, NuGetVersion version, IReadOnlyCollection<NuGetFramework> projectFrameworks, CancellationToken cancellationToken)
    {
        var resolver = new VersionFolderPathResolver(SettingsUtility.GetGlobalPackagesFolder(_settings));
        if (File.Exists(resolver.GetManifestFilePath(id, version)))
        {
            using var folderReader = new PackageFolderReader(resolver.GetInstallPath(id, version));
            return Evaluate(folderReader, projectFrameworks);
        }

        var failures = new List<string>();
        foreach (var source in GetSources(id))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SourceTimeout);

                var repository = Repository.Factory.GetCoreV3(source);
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(timeout.Token);
                using var stream = new MemoryStream();
                if (resource is not null && await resource.CopyNupkgToStreamAsync(id, version, stream, _cache, NullLogger.Instance, timeout.Token))
                {
                    stream.Position = 0;
                    using var archiveReader = new PackageArchiveReader(stream);
                    return Evaluate(archiveReader, projectFrameworks);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{source.Name}: {ex.GetBaseException().Message}");
            }
        }

        return new CompatibilityResult(
            CompatibilityStatus.Unknown,
            failures.Count == 0 ? "package not found on configured sources" : string.Join("; ", failures));
    }

    public void Dispose() => _cache.Dispose();

    internal static CompatibilityResult Evaluate(PackageReaderBase reader, IReadOnlyCollection<NuGetFramework> projectFrameworks)
    {
        var assetFrameworks = reader.GetLibItems()
            .Concat(reader.GetReferenceItems())
            .Select(g => g.TargetFramework)
            .Distinct()
            .ToList();

        var supported = assetFrameworks.Count > 0
            ? assetFrameworks
            : reader.GetPackageDependencies().Select(g => g.TargetFramework).Distinct().ToList();

        return Evaluate(supported, projectFrameworks);
    }

    internal static CompatibilityResult Evaluate(IReadOnlyCollection<NuGetFramework> supported, IReadOnlyCollection<NuGetFramework> projectFrameworks)
    {
        if (supported.Count == 0 || supported.Any(f => f.IsAny || f.IsAgnostic))
        {
            return new CompatibilityResult(CompatibilityStatus.Compatible);
        }

        var incompatible = projectFrameworks
            .Where(fw => !supported.Any(s => DefaultCompatibilityProvider.Instance.IsCompatible(fw, s)))
            .ToList();

        return incompatible.Count == 0
            ? new CompatibilityResult(CompatibilityStatus.Compatible)
            : new CompatibilityResult(
                CompatibilityStatus.Incompatible,
                $"supports {string.Join(", ", supported.Select(s => s.GetShortFolderName()))}; not {string.Join(", ", incompatible.Select(f => f.GetShortFolderName()))}");
    }

    private IEnumerable<PackageSource> GetSources(string id)
    {
        var sources = new PackageSourceProvider(_settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        var mapping = PackageSourceMapping.GetPackageSourceMapping(_settings);
        if (!mapping.IsEnabled)
        {
            return sources;
        }

        var allowed = mapping.GetConfiguredPackageSources(id);
        return sources.Where(s => allowed.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
    }
}
