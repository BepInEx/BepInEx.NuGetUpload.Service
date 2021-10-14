using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NuGetUpload.Services
{
    public class GamePackageInfoService
    {
        public const int KEY_LENGTH = 64;
        private readonly ILogger<GamePackageInfoService> logger;
        private readonly PathsOptions pathsOptions;
        private Dictionary<string, GamePackageInfo> gamePackages = new();

        public GamePackageInfoService(ILogger<GamePackageInfoService> logger, IOptions<PathsOptions> pathsOptions)
        {
            this.logger = logger;
            this.pathsOptions = pathsOptions.Value;
            InitializeRepo();
        }

        public GamePackageInfo GetPackage(string key)
        {
            return gamePackages.TryGetValue(key, out var info) ? info : null;
        }

        private void InitializeRepo()
        {
            logger.LogInformation("Repo dir: {}", pathsOptions.PackageInfos);
            Directory.CreateDirectory(pathsOptions.PackageInfos);

            var (hasKeyMap, keyMap) = ReadOrCreate<Dictionary<string, string>>("keymap.json");
            if (!hasKeyMap)
            {
                logger.LogWarning("Skipped reading keymap.json, fix or create the file");
                return;
            }

            foreach (var (key, infoFile) in keyMap)
            {
                var (ok, info) = ReadOrCreate<GamePackageInfo>($"{infoFile}.json");
                if (ok)
                    gamePackages[key] = info;
            }
        }

        private (bool, T) ReadOrCreate<T>(string fileName) where T : new()
        {
            try
            {
                var txt = File.ReadAllText(Path.Combine(pathsOptions.PackageInfos, fileName));
                return (true, JsonSerializer.Deserialize<T>(txt
                    , new()
                    {
                        PropertyNameCaseInsensitive = true
                    }));
            }
            catch (Exception e)
            {
                logger.LogWarning("Failed to read package info JSON file {} because: {}", fileName, e.Message);
                return (false, new());
            }
        }
    }

    public class GamePackageInfo
    {
        public string PackageId { get; set; }
        public string Description { get; set; }
        public string[] Authors { get; set; }
        public IDictionary<string, IDictionary<string, string>> FrameworkTargets { get; set; }
        public string[] AllowedAssemblies { get; set; }
        public bool SkipStripping { get; set; }
        public bool SkipDuplicateMitigation { get; set; }
    }
}
