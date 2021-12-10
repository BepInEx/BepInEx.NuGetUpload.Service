using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NuGetUpload;

public static class ConfigurationExtension
{
    public static IServiceCollection AddAppConfiguration(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<UploadOptions>(config.GetSection(UploadOptions.Section));
        services.Configure<NugetOptions>(config.GetSection(NugetOptions.Section));
        services.Configure<PathsOptions>(config.GetSection(PathsOptions.Section));
        return services;
    }
}

public class UploadOptions
{
    public const string Section = "Upload";

    public double MaxFileSizeMB { get; set; }
    public int MaxFiles { get; set; }
}

public class NugetOptions
{
    public const string Section = "NuGet";

    public string SourceUrl { get; set; }
    public string ApiKey { get; set; }
    public string PublicHost { get; set; }
}

public class PathsOptions
{
    public const string Section = "Paths";

    public string TempUploads { get; set; }
    public string PackageInfos { get; set; }
}
