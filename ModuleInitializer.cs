using System;
using System.Runtime.CompilerServices;

namespace Irukandji.ImageOptimizer
{
    public static class ModuleInitializer
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var requestedName = new System.Reflection.AssemblyName(args.Name).Name;
                    if (string.IsNullOrEmpty(requestedName)) return null;

                    if (requestedName.Equals("SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
                        requestedName.Equals("Jellyfin.Controller", StringComparison.OrdinalIgnoreCase) ||
                        requestedName.Equals("Jellyfin.Model", StringComparison.OrdinalIgnoreCase))
                    {
                        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                        foreach (var assembly in assemblies)
                        {
                            if (assembly.GetName().Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                            {
                                return assembly;
                            }
                        }
                    }
                }
                catch
                {
                    // Fail-safe to avoid crash loops during assembly loading
                }
                return null;
            };
        }
    }
}