using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using NuGetUpload.Services;

namespace NuGetUpload.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PackageController : ControllerBase
    {
        private readonly ILogger<PackageController> _logger;
        private readonly PackageUploadService _packageUploadService;
        private readonly GamePackageInfoService _gamePackageInfoService;

        public PackageController(ILogger<PackageController> logger, PackageUploadService packageUploadService, GamePackageInfoService gamePackageInfoService)
        {
            _logger = logger;
            _packageUploadService = packageUploadService;
            _gamePackageInfoService = gamePackageInfoService;
        }

        [HttpPost("upload")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<GamePackageInfo>> UploadAsync(
            [FromForm] [Required] [MaxLength(64)] [MinLength(64)] string key,
            [FromForm] [Required] string version,
            [FromForm] string unityVersion,
            [FromForm(Name = "inputFile")] [Required] IFormFile[] files
        )
        {
            if (!NuGetVersion.TryParse(version, out var parsedVersion))
            {
                return BadRequest("Failed to parse version");
            }

            if (files.Length > _packageUploadService.Options.MaxFiles)
            {
                return BadRequest($"Too many files");
            }

            foreach (var file in files)
            {
                if (!_packageUploadService.IsValidName(file.FileName))
                {
                    return BadRequest($"File {file.FileName} has an invalid name");
                }

                if (file.Length > _packageUploadService.MaxFileSize)
                {
                    return BadRequest($"File {file.FileName} is too large");
                }
            }

            var info = _gamePackageInfoService.GetPackage(key);

            if (info == null)
            {
                return Unauthorized("Invalid key");
            }

            if (info.IsIl2Cpp && (unityVersion == null || !Version.TryParse(unityVersion, out _)))
            {
                return BadRequest("Bad unityVersion");
            }

            var packageInfo = await _packageUploadService.UploadPackage(info, files.Select(file => new InputFormFile(file)).Cast<IInputFile>().ToList(), parsedVersion, unityVersion, new Progress<(double Progress, string Status)>(x =>
            {
                _logger.LogDebug("Upload status: {Status}", x.Status);
            }));

            return Ok(packageInfo);
        }
    }
}
