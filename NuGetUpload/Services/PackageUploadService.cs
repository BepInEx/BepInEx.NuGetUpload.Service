using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
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
        private readonly IWebHostEnvironment env;
        private readonly ILogger<PackageUploadService> logger;
        private readonly NugetOptions nuget;
        private readonly PathsOptions paths;
        private readonly Regex validNamePattern = new(@"^[A-Za-z0-9.\-_]+$");

        public PackageUploadService(ILogger<PackageUploadService> logger,
                                    IWebHostEnvironment env,
                                    IOptions<UploadOptions> options,
                                    IOptions<PathsOptions> paths,
                                    IOptions<NugetOptions> nuget)
        {
            this.logger = logger;
            this.env = env;
            this.paths = paths.Value;
            this.nuget = nuget.Value;
            Options = options.Value;
        }

        public UploadOptions Options { get; }
        public int MaxFileSize => (int)Options.MaxFileSizeMB * 1024 * 1024;

        public bool IsValidName(string s)
        {
            return validNamePattern.IsMatch(s);
        }

        private async IAsyncEnumerable<int> Upload(IBrowserFile file, string filePath)
        {
            await using var fs = new FileStream(filePath, FileMode.Create);
            await using var s = file.OpenReadStream(MaxFileSize);
            var buffer = new byte[81920];
            int read;
            while ((read = await s.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                yield return read;
            }
        }
        
        public async IAsyncEnumerable<(double, string)> UploadPackage(GamePackageInfo info, IList<IBrowserFile> files)
        {
            var totalSteps = files.Count + // Upload 
                             files.Count + // String
                             1 +           // Metadata fetch
                             1 +           // Package create
                             1;            // Package push


            var stepSize = 100.0 / totalSteps;
            var progress = 0.0;

            (double, string) Step(string msg, double size = -1)
            {
                progress += size >= 0 ? size : stepSize;
                return (progress, msg);
            }

            using var randomFolder = new TemporaryFolder(paths.TempUploads);
            logger.LogInformation("Upload path: {}", randomFolder.Path);
            var uploads = new List<string>();
            foreach (var browserFile in files)
            {
                var uploadMsg = $"Uploading {browserFile.Name}";
                yield return Step(uploadMsg, 0);
                var filePath = Path.Combine(randomFolder.Path, browserFile.Name);
                var totalSize = (double)browserFile.Size;
                await foreach (var read in Upload(browserFile, filePath))
                    yield return Step(uploadMsg, read / totalSize * stepSize);
                uploads.Add(filePath);
            }

            var allowedAssemblies = new HashSet<string>(info.AllowedAssemblies, StringComparer.InvariantCultureIgnoreCase);
            foreach (var filePath in uploads)
            {
                var fileName = Path.GetFileName(filePath);
                yield return Step($"Stripping and publicising {fileName}");
                try
                {
                    AssemblyStripper.StripAssembly(filePath, allowedAssemblies);
                }
                catch (Exception e)
                {
                    throw new Exception($"Could not process {fileName}: {e.Message}", e);
                }
            }

            if (allowedAssemblies.Count > 0)
                throw new Exception($"The following assemblies are missing: {string.Join(", " ,allowedAssemblies)}. Please include all assemblies.");
                    
            yield return Step("Querying existing package metadata");

            var now = DateTime.Now;
            var packageVersion = new NuGetVersion(now.Year, now.Month, now.Day);

            var cache = new SourceCacheContext { NoCache = true };
            var repo = Repository.Factory.GetCoreV3(nuget.SourceUrl);
            var resource = await repo.GetResourceAsync<FindPackageByIdResource>();

            var versions =
                await resource.GetAllVersionsAsync(info.PackageId, cache, NullLogger.Instance, CancellationToken.None);
            var highest = versions.Max();

            if (highest is not null &&
                (highest.Major, highest.Minor, highest.Patch) == (packageVersion.Major, packageVersion.Minor, packageVersion.Patch))
                packageVersion = new NuGetVersion(highest.Major, highest.Minor, highest.Patch, highest.Revision + 1);

            yield return Step("Generating package");

            var meta = new ManifestMetadata
            {
                Id = info.PackageId,
                Authors = info.Authors,
                Version = packageVersion,
                Description = info.Description,
                DependencyGroups = info.FrameworkTargets.Select(kv =>
                    new PackageDependencyGroup(
                        NuGetFramework.Parse(kv.Key),
                        kv.Value.Select(d => new PackageDependency(d.Key, VersionRange.Parse(d.Value)))))
            };

            var builder = new PackageBuilder();
            builder.PopulateFiles(randomFolder.Path, info.FrameworkTargets.Select(kv => new ManifestFile
            {
                Source = "*.dll",
                Target = $"lib/{kv.Key}"
            }));
            builder.Populate(meta);

            var packagePath = Path.Combine(randomFolder.Path, "package.nupkg");
            await using (var packageFs = File.Create(packagePath))
            {
                builder.Save(packageFs);
            }

            var updateResource = await repo.GetResourceAsync<PackageUpdateResource>();

            yield return Step("Pushing package");
            
            await updateResource.Push(new[] { packagePath }.ToList(),
                null,
                2 * 60,
                false,
                s => nuget.ApiKey,
                s => null,
                false,
                false,
                null,
                NullLogger.Instance);

            yield return Step("Cleaning up");
        }
    }
}
