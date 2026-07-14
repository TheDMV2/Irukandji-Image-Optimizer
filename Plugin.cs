using System;
using System.Collections.Generic;
using Irukandji.ImageOptimizer.Configuration;
using Irukandji.ImageOptimizer.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Irukandji.ImageOptimizer
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            PluginLogger.Initialize(applicationPaths.ConfigurationDirectoryPath);
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "Irukandji Image Optimizer";

        public override Guid Id => Guid.Parse("A8D36D61-D8B4-4B3F-8C5C-BDC3B98D7122");

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var config = Configuration;
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "ImageOptimizer",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = config.EnableInSidebar,
                    MenuSection = "server",
                    MenuIcon = "photo",
                    DisplayName = "Irukandji Image Optimizer"
                }
            };
        }
    }
}