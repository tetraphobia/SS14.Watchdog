using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Mono.Unix;
using SS14.Watchdog.Configuration.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.Updates
{
    /// <summary>
    ///     Update providers that pulls the latest build of a Jenkins job as updates.
    /// </summary>
    public sealed class UpdateProviderJenkins : UpdateProvider
    {
        private readonly HttpClient _httpClient = new HttpClient();

        private readonly ILogger<UpdateProviderJenkins> _logger;
        private readonly string _baseUrl;
        private readonly string _jobName;

        public UpdateProviderJenkins(UpdateProviderJenkinsConfiguration configuration,
            ILogger<UpdateProviderJenkins> logger)
        {
            _logger = logger;
            _baseUrl = configuration.BaseUrl;
            _jobName = Uri.EscapeUriString(configuration.JobName);
        }

        public override async Task<bool> CheckForUpdateAsync(string? currentVersion, CancellationToken cancel = default)
        {
            try
            {
                var lastSuccessfulBuild = await GetLastSuccessfulBuildAsync(cancel);

                if (lastSuccessfulBuild == null)
                {
                    // No succeeded builds?
                    return false;
                }

                return lastSuccessfulBuild.Number.ToString(CultureInfo.InvariantCulture) != currentVersion;
            }
            catch (HttpRequestException e)
            {
                _logger.LogWarning("Failed to check for updates due to exception:\n{0}", e);
                // Update server probably down? No updates then.
            }

            return false;
        }

        public override async Task<string?> RunUpdateAsync(
            string? currentVersion,
            string binPath,
            CancellationToken cancel = default)
        {
            try
            {
                _logger.LogTrace("Updating...");

                var buildRef = await GetLastSuccessfulBuildAsync(cancel);

                if (buildRef == null)
                {
                    _logger.LogTrace("No last build?");
                    return null;
                }

                if (buildRef.Number.ToString(CultureInfo.InvariantCulture) == currentVersion)
                {
                    _logger.LogTrace("Update not necessary!");
                    return null;
                }

                _logger.LogTrace("New version is {newVersion} from {oldVersion}", buildRef.Number,
                    currentVersion ?? "<none>");


                var downloadRootUri = new Uri($"{_baseUrl}/job/{_jobName}/{buildRef.Number}/artifact/release/");

                // Create temporary file to download binary into (not doing this in memory).
                await using var tempFile = File.Open(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite);
                // Download URI for server binary.
                var serverDownload = new Uri(downloadRootUri, $"SS14.Server_{GetHostPlatformName()}_x64.zip");

                _logger.LogTrace("Downloading server binary from {download} to {tempFile}", serverDownload,
                    tempFile.Name);

                // Download to file...
                var resp = await _httpClient.GetAsync(serverDownload, cancel);
                await resp.Content.CopyToAsync(tempFile, cancel);

                _logger.LogTrace("Deleting old bin directory ({binPath})", binPath);
                if (Directory.Exists(binPath))
                {
                    Directory.Delete(binPath, true);
                }

                Directory.CreateDirectory(binPath);

                _logger.LogTrace("Extracting zip file");
                // Reset file position so we can extract.
                tempFile.Seek(0, SeekOrigin.Begin);

                // Actually extract.
                using var archive = new ZipArchive(tempFile, ZipArchiveMode.Read);
                archive.ExtractToDirectory(binPath);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // chmod +x Robust.Server

                    var rsPath = Path.Combine(binPath, "Robust.Server");
                    if (File.Exists(rsPath))
                    {
                        var f = new UnixFileInfo(rsPath);
                        f.FileAccessPermissions |=
                            FileAccessPermissions.UserExecute | FileAccessPermissions.GroupExecute |
                            FileAccessPermissions.OtherExecute;
                    }
                }

                return buildRef.Number.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to run update!");

                return null;
            }
        }

        private async Task<JenkinsBuildRef?> GetLastSuccessfulBuildAsync(CancellationToken cancel = default)
        {
            var jobUri = new Uri($"{_baseUrl}/job/{_jobName}/api/json");

            using var jobDataResponse = await _httpClient.GetAsync(jobUri, cancel);

            jobDataResponse.EnsureSuccessStatusCode();

            var jobInfo = JsonSerializer.Deserialize<JenkinsJobInfo>(await jobDataResponse.Content.ReadAsStringAsync(cancel));

            return jobInfo!.LastSuccessfulBuild;
        }

        [Pure]
        private static string GetHostPlatformName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return PlatformNameWindows;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return PlatformNameLinux;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return PlatformNameMacOS;
            }

            throw new PlatformNotSupportedException();
        }
    }
}