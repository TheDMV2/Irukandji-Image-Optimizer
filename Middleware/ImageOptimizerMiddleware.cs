using System;
using System.IO;
using System.Threading.Tasks;
using Irukandji.ImageOptimizer.Logging;
using Irukandji.ImageOptimizer.Services;
using Microsoft.AspNetCore.Http;
using SkiaSharp;

namespace Irukandji.ImageOptimizer.Middleware
{
    public class ImageOptimizerMiddleware
    {
        private readonly RequestDelegate _next;

        public ImageOptimizerMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            // 1. Intercept Avatar Uploads
            if ((method == "POST" || method == "PUT") &&
                path.Contains("/Users/", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("/Images", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await InterceptUploadStream(context);
                }
                catch (Exception ex)
                {
                    PluginLogger.LogError("Failed optimizing incoming avatar upload", ex);
                }
                await _next(context);
                return;
            }

            // Detect GET targets
            bool isClientImageGet = method == "GET" &&
                                   (path.Contains("/Items/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Users/", StringComparison.OrdinalIgnoreCase)) &&
                                   path.Contains("/Images", StringComparison.OrdinalIgnoreCase);

            bool isTrickplayGet = method == "GET" && path.Contains("/Trickplay/", StringComparison.OrdinalIgnoreCase);

            if (isClientImageGet || isTrickplayGet)
            {
                var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
                
                // Device negotiation: Detect WebP/AVIF capabilities via HTTP Accept Header (highly standard & robust)
                bool acceptsWebp = false;
                bool acceptsAvif = false;
                if (context.Request.Headers.TryGetValue("Accept", out var acceptValue))
                {
                    string accept = acceptValue.ToString();
                    acceptsWebp = accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase);
                    acceptsAvif = accept.Contains("image/avif", StringComparison.OrdinalIgnoreCase);
                }

                // Device negotiation: Detect mobile clients via User-Agent Header
                bool isMobile = false;
                if (config.EnableClientProfiling && context.Request.Headers.TryGetValue("User-Agent", out var uaValue))
                {
                    string ua = uaValue.ToString();
                    isMobile = ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
                               ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                               ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
                               ua.Contains("iPad", StringComparison.OrdinalIgnoreCase);
                }

                // Optimization: Skip buffering stream wrapper if target request quality surpasses 96 (Skip mobile profile checks in bypass)
                if (isClientImageGet && !isMobile)
                {
                    int quality = 96;
                    if (context.Request.Query.TryGetValue("quality", out var qVal) && int.TryParse(qVal, out int parsedQuality))
                    {
                        quality = parsedQuality;
                    }
                    if (quality > 96)
                    {
                        await _next(context);
                        return;
                    }
                }

                var originalBodyStream = context.Response.Body;
                
                // Optimization: Pre-allocate MemoryStream buffer size if response Content-Length header is set
                long? contentLength = context.Response.ContentLength;
                using var responseBody = contentLength.HasValue 
                    ? new MemoryStream((int)contentLength.Value) 
                    : new MemoryStream();
                    
                context.Response.Body = responseBody;

                try
                {
                    await _next(context);
                }
                catch
                {
                    // Clean stream recovery on failures
                    responseBody.Position = 0;
                    await responseBody.CopyToAsync(originalBodyStream);
                    throw;
                }

                if (context.Response.StatusCode == 200)
                {
                    var contentType = context.Response.ContentType;
                    if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] originalBytes = responseBody.ToArray();
                        var optimizer = new ImageOptimizerService();

                        if (isTrickplayGet)
                        {
                            string newContentType;
                            using SKData optimizedData = optimizer.OptimizeTrickplayImage(originalBytes, contentType, acceptsWebp, acceptsAvif, out newContentType);
                            if (optimizedData != null)
                            {
                                context.Response.ContentType = newContentType;
                                context.Response.ContentLength = optimizedData.Size;
                                
                                // Optimization: Stream unmanaged memory directly to socket (Zero managed byte-array copies)
                                using var stream = optimizedData.AsStream();
                                await stream.CopyToAsync(originalBodyStream);
                                return;
                            }
                        }
                        else if (isClientImageGet)
                        {
                            string newContentTypeScope; // Correction: Declared out-variable before block scope
                            using SKData optimizedData = isMobile 
                                ? optimizer.OptimizeMobileFastPath(originalBytes, contentType, acceptsWebp, acceptsAvif, out newContentTypeScope)
                                : optimizer.OptimizeClientImage(originalBytes, contentType, acceptsWebp, acceptsAvif, out newContentTypeScope);

                            if (optimizedData != null)
                            {
                                context.Response.ContentType = newContentTypeScope;
                                context.Response.ContentLength = optimizedData.Size;
                                
                                // Optimization: Stream unmanaged memory directly to socket (Zero managed byte-array copies)
                                using var stream = optimizedData.AsStream();
                                await stream.CopyToAsync(originalBodyStream);
                                return;
                            }
                        }
                    }
                }

                responseBody.Position = 0;
                await responseBody.CopyToAsync(originalBodyStream);
                return;
            }

            await _next(context);
        }

        private async Task InterceptUploadStream(HttpContext context)
        {
            context.Request.EnableBuffering();
            
            // Optimization: Replaced manual loop with native CopyToAsync stream copy (Uses internal ASP.NET Core native transport memory pools)
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            
            byte[] originalBytes = ms.ToArray();
            string contentType = context.Request.ContentType ?? "image/jpeg";
            var optimizer = new ImageOptimizerService();
            string newContentType;
            
            using SKData optimizedData = optimizer.OptimizeAvatarImage(originalBytes, contentType, out newContentType);

            if (optimizedData != null) {
                context.Request.Body = new MemoryStream(optimizedData.ToArray());
                context.Request.ContentType = newContentType;
                context.Request.ContentLength = optimizedData.Size;
            } else {
                context.Request.Body.Position = 0;
            }
        }
    }
}