using System;
using System.IO;
using System.Linq;
using System.Text;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Configuration;
using OrchardCore.FileStorage;
using OrchardCore.FileStorage.AmazonS3;
using OrchardCore.Media.Core;
using OrchardCore.Media.Core.Events;
using OrchardCore.Media.Events;
using OrchardCore.Modules;

namespace OrchardCore.Media.AmazonS3;

public class Startup : Modules.StartupBase
{
    private readonly ILogger _logger;
    private readonly IShellConfiguration _configuration;

    public Startup(IShellConfiguration configuration,
        ILogger<Startup> logger)
        => (_configuration, _logger)
            = (configuration, logger);

    public override void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IConfigureOptions<AwsStorageOptions>, AwsStorageOptionsConfiguration>();

        var storeOptions = new AwsStorageOptions().BindConfiguration(_configuration, _logger);
        var validationErrors = storeOptions.Validate().ToList();
        var stringBuilder = new StringBuilder();

        if (validationErrors.Any())
        {
            foreach (var error in validationErrors)
            {
                stringBuilder.Append(error.ErrorMessage);
            }

            _logger.LogError("S3 Media configuration validation failed with errors: {Errors} fallback to local file storage.", stringBuilder);
        }
        else
        {
            _logger.LogInformation(
                "Starting with S3 Media Configuration. BucketName: {BucketName}; BasePath: {BasePath}", storeOptions.BucketName, storeOptions.BasePath);

            services.AddSingleton<IMediaFileStoreCacheFileProvider>(serviceProvider =>
            {
                var hostingEnvironment = serviceProvider.GetRequiredService<IWebHostEnvironment>();

                if (String.IsNullOrWhiteSpace(hostingEnvironment.WebRootPath))
                {
                    throw new MediaConfigurationException("The wwwroot folder for serving cache media files is missing.");
                }

                var mediaOptions = serviceProvider.GetRequiredService<IOptions<MediaOptions>>().Value;
                var shellSettings = serviceProvider.GetRequiredService<ShellSettings>();
                var logger = serviceProvider.GetRequiredService<ILogger<DefaultMediaFileStoreCacheFileProvider>>();

                var mediaCachePath = GetMediaCachePath(hostingEnvironment,
                    DefaultMediaFileStoreCacheFileProvider.AssetsCachePath, shellSettings);

                if (!Directory.Exists(mediaCachePath))
                {
                    Directory.CreateDirectory(mediaCachePath);
                }

                return new DefaultMediaFileStoreCacheFileProvider(logger, mediaOptions.AssetsRequestPath,
                    mediaCachePath);
            });

            // Replace the default media file provider with the media cache file provider.
            services.Replace(ServiceDescriptor.Singleton<IMediaFileProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<IMediaFileStoreCacheFileProvider>()));

            // Register the media cache file provider as a file store cache provider.
            services.AddSingleton<IMediaFileStoreCache>(serviceProvider =>
                serviceProvider.GetRequiredService<IMediaFileStoreCacheFileProvider>());

            // Registering IAmazonS3 client using AWS registration factory.
            services.AddAWSService<IAmazonS3>(storeOptions.AwsOptions);

            services.Replace(ServiceDescriptor.Singleton<IMediaFileStore>(serviceProvider =>
            {
                var shellSettings = serviceProvider.GetRequiredService<ShellSettings>();
                var mediaOptions = serviceProvider.GetRequiredService<IOptions<MediaOptions>>().Value;
                var mediaEventHandlers = serviceProvider.GetServices<IMediaEventHandler>();
                var mediaCreatingEventHandlers = serviceProvider.GetServices<IMediaCreatingEventHandler>();
                var clock = serviceProvider.GetRequiredService<IClock>();
                var logger = serviceProvider.GetRequiredService<ILogger<DefaultMediaFileStore>>();
                var amazonS3Client = serviceProvider.GetService<IAmazonS3>();

                var fileStore = new AwsFileStore(clock, storeOptions, amazonS3Client);

                var mediaUrlBase =
                    $"/{fileStore.Combine(shellSettings.RequestUrlPrefix, mediaOptions.AssetsRequestPath)}";

                var originalPathBase = serviceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext
                    ?.Features.Get<ShellContextFeature>()
                    ?.OriginalPathBase;

                if (originalPathBase.HasValue && !String.IsNullOrWhiteSpace(originalPathBase.Value))
                {
                    mediaUrlBase = fileStore.Combine(originalPathBase.Value, mediaUrlBase);
                }

                return new DefaultMediaFileStore(fileStore,
                    mediaUrlBase,
                    mediaOptions.CdnBaseUrl,
                    mediaEventHandlers,
                    mediaCreatingEventHandlers,
                    logger);
            }));

            services.AddSingleton<IMediaEventHandler, DefaultMediaFileStoreCacheEventHandler>();

            services.AddScoped<IModularTenantEvents, CreateMediaS3BucketEvent>();
        }
    }

    private string GetMediaCachePath(IWebHostEnvironment hostingEnvironment,
        string assetsPath, ShellSettings shellSettings)
        => PathExtensions.Combine(hostingEnvironment.WebRootPath,
            assetsPath, shellSettings.Name);
}
