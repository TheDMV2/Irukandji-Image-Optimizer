using System;
using Irukandji.ImageOptimizer.Logging;
using Irukandji.ImageOptimizer.Middleware;
using Irukandji.ImageOptimizer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Irukandji.ImageOptimizer
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            try
            {
                serviceCollection.AddSingleton<IStartupFilter, ImageOptimizerStartupFilter>();
                serviceCollection.AddTransient<ImageOptimizationHandler>();

                serviceCollection.ConfigureAll<HttpClientFactoryOptions>(options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(builder =>
                    {
                        try
                        {
                            var handler = builder.Services.GetRequiredService<ImageOptimizationHandler>();
                            builder.AdditionalHandlers.Add(handler);
                        }
                        catch (Exception ex)
                        {
                            PluginLogger.LogError("Failed targeting HttpClientFactory builder configuration", ex);
                        }
                    });
                });

                ImageOptimizerService.SetInitializationSuccess();
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Initialization registration failed", ex);
                ImageOptimizerService.SetInitializationError(ex.Message);
            }
        }
    }
}