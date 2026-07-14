using System;
using System.IO;
using Irukandji.ImageOptimizer.Logging;
using SkiaSharp;

namespace Irukandji.ImageOptimizer.Services
{
    public class ImageOptimizerService
    {
        private static string _initError;
        private static bool _initSuccess = false;

        public static void SetInitializationError(string error) => _initError = error;
        public static void SetInitializationSuccess() => _initSuccess = true;

        public static object GetInitializationStatus() => new { Success = _initSuccess, ErrorMessage = _initError };

        // Runtime dynamic check for SkiaSharp version 4.150.0+ capability mapping
        private static readonly bool IsSkiaSharpV4 = CheckSkiaSharpV4();

        private static bool CheckSkiaSharpV4()
        {
            try
            {
                var version = typeof(SKBitmap).Assembly.GetName().Version;
                return version != null && version.Major >= 4;
            }
            catch
            {
                return false;
            }
        }

        private Configuration.PluginConfiguration GetConfig() =>
            Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        public SKData OptimizeMetadataImage(byte[] inputBytes, string contentType, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessImage(inputBytes, contentType, config.MaxMetadataDimension, config, out finalContentType, isMetadata: true);
        }

        public SKData OptimizeAvatarImage(byte[] inputBytes, string contentType, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessImage(inputBytes, contentType, config.MaxAvatarDimension, config, out finalContentType, isMetadata: true);
        }

        public SKData OptimizeClientImage(byte[] inputBytes, string contentType, bool acceptsWebp, bool acceptsAvif, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessFastPath(inputBytes, contentType, config.JpegQuality, config.WebpQuality, config.AvifQuality, 0, acceptsWebp, acceptsAvif, config, out finalContentType);
        }

        public SKData OptimizeMobileFastPath(byte[] inputBytes, string contentType, bool acceptsWebp, bool acceptsAvif, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessFastPath(inputBytes, contentType, config.MobileJpegQuality, config.MobileWebpQuality, config.AvifQuality, config.MobileMaxDimension, acceptsWebp, acceptsAvif, config, out finalContentType);
        }

        public SKData OptimizeTrickplayImage(byte[] inputBytes, string contentType, bool acceptsWebp, bool acceptsAvif, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessFastPath(inputBytes, contentType, config.JpegQuality, config.WebpQuality, config.AvifQuality, 0, acceptsWebp, acceptsAvif, config, out finalContentType);
        }

        // Sub-sampling Decoder: Decodes the image directly to a smaller resolution to minimize RAM and CPU overhead
        private SKBitmap DecodeAndScale(byte[] inputBytes, int maxDim, out int targetWidth, out int targetHeight)
        {
            targetWidth = 0;
            targetHeight = 0;
            try
            {
                using var stream = new MemoryStream(inputBytes);
                using var codec = SKCodec.Create(stream); // Read metadata only without decoding actual pixels
                if (codec == null) return null;

                int width = codec.Info.Width;
                int height = codec.Info.Height;
                targetWidth = width;
                targetHeight = height;

                float scale = 1.0f;
                if (maxDim > 0 && (width > maxDim || height > maxDim))
                {
                    float ratio = (float)width / height;
                    if (ratio > 1f)
                    {
                        targetWidth = maxDim;
                        targetHeight = (int)(maxDim / ratio);
                    }
                    else
                    {
                        targetHeight = maxDim;
                        targetWidth = (int)(maxDim * ratio);
                    }
                    scale = (float)targetWidth / width;
                }

                SKBitmap decoded;
                if (scale < 1.0f)
                {
                    // Codec-level scaling downscaling cuts memory footprint up to 75%
                    var scaledDims = codec.GetScaledDimensions(scale);
                    var scaledInfo = new SKImageInfo(scaledDims.Width, scaledDims.Height, codec.Info.ColorType, codec.Info.AlphaType);
                    
                    decoded = new SKBitmap(scaledInfo);
                    var result = codec.GetPixels(scaledInfo, decoded.GetPixels());
                    if (result != SKCodecResult.Success)
                    {
                        // Fallback to full resolution decode if scaled dimension fails
                        decoded.Dispose();
                        decoded = SKBitmap.Decode(codec);
                    }
                }
                else
                {
                    decoded = SKBitmap.Decode(codec);
                }

                if (decoded == null) return null;

                // Apply a final precise scale if the native codec scale is slightly larger than the target
                if (scale < 1.0f && (decoded.Width != targetWidth || decoded.Height != targetHeight))
                {
                    var finalInfo = new SKImageInfo(targetWidth, targetHeight, decoded.ColorType, decoded.AlphaType);
                    var finalBitmap = new SKBitmap(finalInfo);
                    decoded.ScalePixels(finalBitmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    decoded.Dispose();
                    decoded = finalBitmap;
                }

                return decoded;
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed decoding and scaling image dynamically", ex);
                return null;
            }
        }

        // Fast-Path: Specifically optimized for on-the-fly execution (Returns native unmanaged SKData)
        private SKData ProcessFastPath(
            byte[] inputBytes, 
            string contentType, 
            int jpegQual, 
            int webpQual, 
            int avifQual,
            int maxDim, 
            bool acceptsWebp, 
            bool acceptsAvif, 
            Configuration.PluginConfiguration config, 
            out string finalContentType)
        {
            finalContentType = contentType;
            try
            {
                int targetWidth, targetHeight;
                using var original = DecodeAndScale(inputBytes, maxDim, out targetWidth, out targetHeight);
                if (original == null) return null;

                bool isPngOrGif = contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                                  contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase);

                bool hasTransparency = isPngOrGif && HasTransparency(original);

                using var image = SKImage.FromBitmap(original);
                SKEncodedImageFormat outputFormat;
                int quality = 100;

                // Format Negotiation: dynamically convert to WebP or AVIF if the client accepts it natively
                if (acceptsAvif && config.ConvertToAvif && EncodeAvif(image, config, out SKData avifBytes))
                {
                    finalContentType = "image/avif";
                    return avifBytes;
                }
                else if (acceptsWebp && (hasTransparency || config.ConvertToWebpForTransparent || config.ConvertPngToJpg))
                {
                    outputFormat = SKEncodedImageFormat.Webp;
                    quality = webpQual;
                    finalContentType = "image/webp";
                }
                else if (isPngOrGif)
                {
                    if (hasTransparency)
                    {
                        if (config.ConvertToWebpForTransparent && acceptsWebp)
                        {
                            outputFormat = SKEncodedImageFormat.Webp;
                            quality = webpQual;
                            finalContentType = "image/webp";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                    else
                    {
                        if (config.ConvertPngToJpg)
                        {
                            outputFormat = SKEncodedImageFormat.Jpeg;
                            quality = jpegQual;
                            finalContentType = "image/jpeg";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                }
                else if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
                {
                    outputFormat = SKEncodedImageFormat.Jpeg;
                    quality = jpegQual;
                    finalContentType = "image/jpeg";
                }
                else if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                {
                    outputFormat = SKEncodedImageFormat.Webp;
                    quality = webpQual;
                    finalContentType = "image/webp";
                }
                else
                {
                    return null;
                }

                SKData encoded = image.Encode(outputFormat, quality);
                if (encoded != null)
                {
                    return encoded;
                }

                return null;
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Fast path on-the-fly execution crashed", ex);
                return null;
            }
        }

        // High-Effort Metadata Path (Returns native unmanaged SKData)
        public SKData ProcessImage(byte[] inputBytes, string contentType, int maxDimension, Configuration.PluginConfiguration config, out string finalContentType, bool isMetadata)
        {
            finalContentType = contentType;
            try
            {
                int targetWidth, targetHeight;
                using var original = DecodeAndScale(inputBytes, maxDimension, out targetWidth, out targetHeight);
                if (original == null) return null;

                bool isPngOrGif = contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                                  contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase);
                bool hasTransparency = isPngOrGif && HasTransparency(original);

                using var image = SKImage.FromBitmap(original);
                SKEncodedImageFormat outputFormat;
                int quality = 100;

                if (isPngOrGif)
                {
                    if (hasTransparency)
                    {
                        if (config.ConvertToWebpForTransparent)
                        {
                            outputFormat = SKEncodedImageFormat.Webp;
                            quality = config.WebpQuality;
                            finalContentType = "image/webp";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                    else
                    {
                        if (config.ConvertToAvif && EncodeAvif(image, config, out SKData avifBytes))
                        {
                            finalContentType = "image/avif";
                            return avifBytes;
                        }
                        if (config.ConvertPngToJpg)
                        {
                            outputFormat = SKEncodedImageFormat.Jpeg;
                            quality = config.JpegQuality;
                            finalContentType = "image/jpeg";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                }
                else if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
                {
                    if (config.ConvertToAvif && EncodeAvif(image, config, out SKData avifBytes))
                    {
                        finalContentType = "image/avif";
                        return avifBytes;
                    }

                    outputFormat = SKEncodedImageFormat.Jpeg;
                    quality = config.JpegQuality;
                    finalContentType = "image/jpeg";
                }
                else if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                {
                    if (config.ConvertToAvif && EncodeAvif(image, config, out SKData avifBytes))
                    {
                        finalContentType = "image/avif";
                        return avifBytes;
                    }

                    outputFormat = SKEncodedImageFormat.Webp;
                    quality = config.WebpQuality;
                    finalContentType = "image/webp";
                }
                else
                {
                    return null;
                }

                SKData encoded = image.Encode(outputFormat, quality);
                if (encoded != null)
                {
                    return encoded;
                }

                return null;
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Metadata path image optimization failed", ex);
                return null;
            }
        }

        private bool EncodeAvif(SKImage image, Configuration.PluginConfiguration config, out SKData avifBytes)
        {
            avifBytes = null;
            try
            {
                // Verify if AVIF is compile-bound on the target system (otherwise fails and returns null)
                var avifEncoded = image.Encode(SKEncodedImageFormat.Avif, config.AvifQuality);
                if (avifEncoded != null)
                {
                    avifBytes = avifEncoded;
                    return true;
                }

                // If native library is missing AVIF codecs, dynamically fall back
                if (config.LogCodecWarnings)
                {
                    PluginLogger.LogError("AVIF encoding is unsupported by the native Skia build compiled into this server host. Falling back.");
                }
            }
            catch (Exception ex) { if (config.LogCodecWarnings) PluginLogger.LogError("AVIF codec failed or is missing", ex); }
            return false;
        }

        private bool HasTransparency(SKBitmap bitmap)
        {
            if (bitmap.AlphaType == SKAlphaType.Opaque) return false;

            // Safely retrieve the byte span and scan standard 32-bit (4 bytes per pixel) alpha offsets
            var pixels = bitmap.GetPixelSpan();
            int bytesPerPixel = bitmap.BytesPerPixel;

            if (bytesPerPixel == 4)
            {
                // Scan only the alpha channel byte of every pixel (index offset 3, step 4)
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] < 255)
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Fallback for non-standard 16-bit or index color models
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++) { if (bitmap.GetPixel(x, y).Alpha < 255) return true; }
                }
            }

            return false;
        }

        // Highly-optimized PSNR-to-100 Visual Fidelity Scorer (Uses Zero-Allocation Pixel Scan overlays)
        public static double CalculateFidelityScore(SKBitmap bmp1, SKBitmap bmp2)
        {
            SKBitmap tempBmp = null;
            SKBitmap b1 = bmp1;
            SKBitmap b2 = bmp2;

            if (bmp1.Width != bmp2.Width || bmp1.Height != bmp2.Height)
            {
                // Auto-scale original canvas to matching optimized size boundary for pixel-level delta scans
                tempBmp = new SKBitmap(new SKImageInfo(bmp2.Width, bmp2.Height, bmp1.ColorType, bmp1.AlphaType));
                bmp1.ScalePixels(tempBmp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                b1 = tempBmp;
            }

            try
            {
                var span1 = b1.GetPixelSpan();
                var span2 = b2.GetPixelSpan();

                if (span1.Length != span2.Length) return 1.0;

                double sumSquaredError = 0;
                long totalCheckedBytes = 0;

                // High-speed CPU cache line scan
                for (int i = 0; i < span1.Length; i++) {
                    int diff = span1[i] - span2[i]; sumSquaredError += diff * diff; totalCheckedBytes++;
                }

                if (totalCheckedBytes == 0) return 100.0;

                double mse = sumSquaredError / totalCheckedBytes;
                if (mse == 0) return 100.0;

                // Map logarithmic Peak Signal-to-Noise Ratio (PSNR) to 1-100 score matrix (similar to SSIM/VMAF mappings)
                double psnr = 10.0 * Math.Log10((255.0 * 255.0) / mse);
                double score = (psnr - 20.0) / (50.0 - 20.0) * 100.0; // Map range 20dB (poor) to 50dB (excellent)
                score = Math.Clamp(score, 1.0, 100.0);

                return Math.Round(score, 1);
            }
            finally
            {
                if (tempBmp != null) tempBmp.Dispose();
            }
        }
    }
}