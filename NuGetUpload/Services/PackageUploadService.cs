using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using dnlib.DotNet;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGetUpload.Utils;

namespace NuGetUpload.Services
{
    public class PackageUploadService
    {
        private static readonly Regex _validNamePattern = new(@"^[A-Za-z0-9.\-_]+$", RegexOptions.Compiled);

        private static readonly HashSet<string> _zipContentTypes = new()
        {
            "application/zip",
            "application/x-zip-compressed",
            "multipart/x-zip"
        };

        private readonly ILogger<PackageUploadService> _logger;
        private readonly NugetOptions _nugetOptions;
        private readonly PathsOptions _pathsOptions;

        public PackageUploadService(ILogger<PackageUploadService> logger,
            IOptions<UploadOptions> options,
            IOptions<PathsOptions> paths,
            IOptions<NugetOptions> nuget)
        {
            _logger = logger;
            _pathsOptions = paths.Value;
            _nugetOptions = nuget.Value;
            Options = options.Value;
        }

        public UploadOptions Options { get; }
        public int MaxFileSize => (int)Options.MaxFileSizeMB * 1024 * 1024;

        public bool IsValidName(string s)
        {
            return _validNamePattern.IsMatch(s);
        }

        private static async IAsyncEnumerable<int> SaveToFile(Stream s, string filePath)
        {
            await using var fs = new FileStream(filePath, FileMode.Create);
            var buffer = new byte[81920];
            int read;
            while ((read = await s.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                yield return read;
            }
        }

        private static async IAsyncEnumerable<string> ExtractZip(string zipFilePath, string basePath, List<string> uploads)
        {
            using var zipFile = ZipFile.OpenRead(zipFilePath);
            foreach (var zipArchiveEntry in zipFile.Entries)
            {
                yield return $"Extracting {zipArchiveEntry.Name}";
                var filePath = Path.Combine(basePath, zipArchiveEntry.Name);
                await using var entryStream = zipArchiveEntry.Open();
                await using var fs = new FileStream(filePath, FileMode.Create);
                await entryStream.CopyToAsync(fs);
                uploads.Add(filePath);
            }
        }

        public record PackageInfo(string Id, string Version);

        public async Task<PackageInfo> UploadPackage(GamePackageInfo info, IList<IInputFile> files, NuGetVersion version, string unityVersion, IProgress<(double, string)> progress = null)
        {
            var totalSteps =
                files.Count + // Upload 
                (info.SkipStripping ? 0 : files.Count) + // Strip
                1 + // Metadata fetch
                1 + // Package create
                1; // Package push

            var stepSize = 100.0 / totalSteps;
            var progressValue = 0.0;

            void Step(string msg, double? size = null)
            {
                progressValue += size ?? stepSize;
                progress?.Report((progressValue, msg));
            }

            using var temporaryFolder = new TemporaryFolder(_pathsOptions.TempUploads);
            _logger.LogDebug("Upload path: {Path}", temporaryFolder.Path);

            var filePaths = new List<string>();
            foreach (var file in files)
            {
                var uploadMessage = $"Uploading {file.Name}";
                Step(uploadMessage, 0);

                var filePath = Path.Combine(temporaryFolder.Path, file.Name);
                var totalSize = (double)file.Size;
                await using var stream = file.OpenReadStream(MaxFileSize);
                await foreach (var read in SaveToFile(stream, filePath))
                    Step(uploadMessage, read / totalSize * stepSize);

                if (_zipContentTypes.Contains(file.ContentType))
                {
                    Step($"Extracting ZIP {file.Name}", 0);
                    await foreach (var s in ExtractZip(filePath, temporaryFolder.Path, filePaths))
                        Step(s, 0);
                }
                else
                {
                    filePaths.Add(filePath);
                }
            }

            var allowedAssemblies = info.AllowedAssemblies == null ? null : new HashSet<string>(info.AllowedAssemblies, StringComparer.InvariantCultureIgnoreCase);

            foreach (var filePath in filePaths)
            {
                var fileName = Path.GetFileName(filePath);
                try
                {
                    using var module = ModuleDefMD.Load(await File.ReadAllBytesAsync(filePath));
                    if (allowedAssemblies != null && !allowedAssemblies.Remove(module.Assembly.Name))
                        throw new PackageUploadException($"Assembly {module.Assembly.Name} is not in allowed list");

                    if (!info.SkipStripping)
                    {
                        Step($"Stripping and publicising {fileName}");
                        AssemblyStripper.StripAssembly(module);
                        module.Write(filePath);
                    }
                }
                catch (Exception e)
                {
                    throw new PackageUploadException($"Could not process {fileName}: {e.Message}", e);
                }
            }

            if (allowedAssemblies != null && allowedAssemblies.Count > 0)
                throw new PackageUploadException(
                    $"The following assemblies are missing: {string.Join(", ", allowedAssemblies)}. Please include all assemblies.");

            Step("Querying existing package metadata");

            var cache = new SourceCacheContext { NoCache = true };
            var repo = Repository.Factory.GetCoreV3(_nugetOptions.SourceUrl);
            var resource = await repo.GetResourceAsync<FindPackageByIdResource>();

            var versions =
                await resource.GetAllVersionsAsync(info.PackageId, cache, NullLogger.Instance, CancellationToken.None);

            if (!info.SkipDuplicateMitigation)
            {
                var number = 0;

                var highest = versions.Where(v => v.Version == version.Version).Max();
                if (highest != null)
                {
                    var releaseLabels = highest.ReleaseLabels.ToList();
                    var index = releaseLabels.LastIndexOf("r");

                    if (index != -1)
                    {
                        number = int.Parse(releaseLabels[index + 1]) + 1;
                    }
                }

                version = new NuGetVersion(version.Version, version.ReleaseLabels.Append("r").Append(number.ToString()), version.Metadata, version.OriginalVersion);
            }

            Step("Generating package");

            var meta = new ManifestMetadata
            {
                Id = info.PackageId,
                Authors = info.Authors,
                Version = version,
                Description = info.Description,
                DependencyGroups = info.FrameworkTargets.Select(kv =>
                    new PackageDependencyGroup(
                        NuGetFramework.Parse(kv.Key),
                        kv.Value.Select(d => new PackageDependency(d.Key, VersionRange.Parse(d.Value)))))
            };

            var builder = new PackageBuilder();

            if (info.IsIl2Cpp)
            {
                var propsPath = Path.Combine(temporaryFolder.Path, info.PackageId + ".props");
                await File.WriteAllTextAsync(propsPath, $@"
<Project>
    <ItemGroup>
        <Unhollow Include=""{info.PackageId}"" Version=""{version}"" DummyDirectory=""$(MSBuildThisFileDirectory)"" UnityVersion=""{unityVersion}"" />
    </ItemGroup>
</Project>
");

                builder.PopulateFiles(temporaryFolder.Path, new[]
                {
                    new ManifestFile
                    {
                        Source = "*.dll",
                        Target = "build"
                    },
                    new ManifestFile
                    {
                        Source = Path.GetFileName(propsPath),
                        Target = "build"
                    }
                });
            }
            else
            {
                builder.PopulateFiles(temporaryFolder.Path, info.FrameworkTargets.Select(kv => new ManifestFile
                {
                    Source = "*.dll",
                    Target = $"lib/{kv.Key}"
                }));
            }

            builder.Populate(meta);

            var packagePath = Path.Combine(temporaryFolder.Path, "package.nupkg");
            await using (var packageFs = File.Create(packagePath))
            {
                builder.Save(packageFs);
            }

            var updateResource = await repo.GetResourceAsync<PackageUpdateResource>();

            Step("Pushing package");

            await updateResource.Push(new[] { packagePath }.ToList(),
                null,
                2 * 60,
                false,
                s => _nugetOptions.ApiKey,
                s => null,
                false,
                false,
                null,
                NullLogger.Instance);

            Step("Cleaning up");

            return new PackageInfo(info.PackageId, version.ToFullString());
        }
    }

    public class PackageUploadException : Exception
    {
        public PackageUploadException(string message) : base(message)
        {
        }

        public PackageUploadException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    public interface IInputFile
    {
        string Name { get; }

        long Size { get; }

        string ContentType { get; }

        Stream OpenReadStream(long maxAllowedSize);
    }

    public class InputBrowserFile : IInputFile
    {
        private readonly IBrowserFile _file;

        public InputBrowserFile(IBrowserFile file)
        {
            _file = file;
        }

        public string Name => _file.Name;

        public long Size => _file.Size;

        public string ContentType => _file.ContentType;

        public Stream OpenReadStream(long maxAllowedSize)
        {
            return _file.OpenReadStream(maxAllowedSize);
        }
    }

    public class InputFormFile : IInputFile
    {
        private readonly IFormFile _file;

        public InputFormFile(IFormFile file)
        {
            _file = file;
        }

        public string Name => _file.FileName;

        public long Size => _file.Length;

        public string ContentType => _file.ContentType;

        public Stream OpenReadStream(long maxAllowedSize)
        {
            return _file.OpenReadStream();
        }
    }
}
