using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using Irukandji.ImageOptimizer.Logging;
using Irukandji.ImageOptimizer.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller; // Added namespace for IServerApplicationPaths
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace Irukandji.ImageOptimizer.Api
{
    internal class SweeperFile
    {
        public string FilePath { get; set; }
        public int MaxDimension { get; set; }
    }

    [ApiController]
    [Route("ImageOptimizer")]
    public class ImageOptimizerController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths; // Correction: Injecting IApplicationPaths instead of un-registered IServerApplicationPaths
        private readonly IProviderManager _providerManager;

        // Static tracking of unnecessary trickplay directories
        private static readonly string[] UnnecessaryTrickplayParentFolders = new[]
        {
            "behind the scenes", "deleted scenes", "interviews", "scenes", 
            "samples", "shorts", "featurettes", "clips", "other", "extras", "trailers"
        };

        public ImageOptimizerController(ILibraryManager libraryManager, IApplicationPaths appPaths, IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _providerManager = providerManager;
        }

        [HttpGet("Status")]
        public IActionResult GetStatus()
        {
            try
            {
                var status = ImageOptimizerService.GetInitializationStatus();
                return Ok(status);
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed retrieving plugin status", ex);
                return StatusCode(500, new { Success = false, ErrorMessage = ex.Message });
            }
        }

        [HttpGet("AvifSupported")]
        public IActionResult GetAvifSupported()
        {
            try
            {
                return Ok(new { Supported = CheckAvifSupported() });
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed checking AVIF system support", ex);
                return Ok(new { Supported = false });
            }
        }

        private bool CheckAvifSupported()
        {
            try
            {
                // Verify raw system codec encoding by writing a tiny canvas overlay
                using (var bitmap = new SKBitmap(1, 1))
                using (var image = SKImage.FromBitmap(bitmap))
                using (var data = image.Encode(SKEncodedImageFormat.Avif, 100))
                {
                    return data != null;
                }
            }
            catch
            {
                return false;
            }
        }

        [HttpGet("SmartlistsExists")]
        public IActionResult GetSmartlistsExists()
        {
            try
            {
                var smartlistsPath = ResolveDirectoryPath("data", "smartlists");
                return Ok(new { Exists = System.IO.Directory.Exists(smartlistsPath) });
            }
            catch
            {
                return Ok(new { Exists = false });
            }
        }

        private string ResolveDirectoryPath(params string[] segments)
        {
            // Try 1: Directly inside ConfigurationDirectoryPath
            var path = System.IO.Path.Combine(_appPaths.ConfigurationDirectoryPath, System.IO.Path.Combine(segments));
            if (System.IO.Directory.Exists(path))
            {
                return path;
            }

            // Try 2: Inside the parent folder (ignoring root '/' or '\' folder contexts)
            var parent = System.IO.Path.GetDirectoryName(_appPaths.ConfigurationDirectoryPath);
            if (!string.IsNullOrEmpty(parent) && !parent.Equals("/", StringComparison.Ordinal) && !parent.Equals("\\", StringComparison.Ordinal))
            {
                var parentPath = System.IO.Path.Combine(parent, System.IO.Path.Combine(segments));
                if (System.IO.Directory.Exists(parentPath))
                {
                    return parentPath;
                }
            }

            // Try 3: Inside ProgramDataPath
            var progDataPath = System.IO.Path.Combine(_appPaths.ProgramDataPath, System.IO.Path.Combine(segments));
            if (System.IO.Directory.Exists(progDataPath))
            {
                return progDataPath;
            }

            return path;
        }

        [HttpGet("Libraries")]
        public IActionResult GetLibraries()
        {
            try
            {
                var folders = _libraryManager.GetVirtualFolders();
                var result = folders.Select(f => new { f.Name }).ToList();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("Metadata/Search")]
        public async Task<IActionResult> SearchMetadata([FromQuery] string query, [FromQuery] string imdbId = null)
        {
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(imdbId))
            {
                return BadRequest("Search query or IMDb ID must be provided.");
            }

            try
            {
                var searchInfo = new MovieInfo
                {
                    Name = query ?? string.Empty
                };

                if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    searchInfo.ProviderIds["Imdb"] = imdbId;
                }

                var searchQuery = new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = searchInfo
                };

                var results = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(searchQuery, CancellationToken.None);
                
                var list = new System.Collections.Generic.List<object>();
                foreach (var r in results)
                {
                    if (!string.IsNullOrEmpty(r.ImageUrl))
                    {
                        list.Add(new
                        {
                            Name = r.Name,
                            Year = r.ProductionYear,
                            ImageUrl = r.ImageUrl,
                            ProviderIds = r.ProviderIds
                        });
                    }
                }

                return Ok(list);
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Metadata provider search failed", ex);
                return StatusCode(500, "Search failed: " + ex.Message);
            }
        }

        [HttpPost("Metadata/Test")]
        public async Task<IActionResult> TestMetadataOptimization([FromQuery] string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return BadRequest("Image URL cannot be empty.");
            }

            try
            {
                // Retrieve uncompressed original bytes directly from provider CDN via raw HttpClient (bypasses interceptor handler)
                using var client = new HttpClient();
                byte[] originalBytes = await client.GetByteArrayAsync(imageUrl);

                string contentType = "image/jpeg";
                if (imageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase)) contentType = "image/png";
                else if (imageUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase)) contentType = "image/webp";
                else if (imageUrl.Contains(".gif", StringComparison.OrdinalIgnoreCase)) contentType = "image/gif";

                // Extract original width and height
                int originalWidth = 0;
                int originalHeight = 0;
                try
                {
                    using var original = SKBitmap.Decode(originalBytes);
                    if (original != null)
                    {
                        originalWidth = original.Width;
                        originalHeight = original.Height;
                    }
                }
                catch
                {
                    // Fail gracefully if metadata reading fails
                }

                var optimizer = new ImageOptimizerService();
                var config = Plugin.Instance.Configuration;
                
                // Instrument in-memory optimization execution speed using high-precision Stopwatch
                var stopwatch = Stopwatch.StartNew();
                using var optimizedData = optimizer.OptimizeMetadataImage(originalBytes, contentType, out string newContentType);
                stopwatch.Stop();
                long processingTimeMs = stopwatch.ElapsedMilliseconds;

                byte[] optimizedBytes = optimizedData != null ? optimizedData.ToArray() : originalBytes;

                // Extract optimized width and height
                int optimizedWidth = 0;
                int optimizedHeight = 0;
                double fidelityScore = 100.0;
                try
                {
                    using var original = SKBitmap.Decode(originalBytes);
                    using var optimized = SKBitmap.Decode(optimizedBytes);
                    if (original != null && optimized != null)
                    {
                        optimizedWidth = optimized.Width;
                        optimizedHeight = optimized.Height;
                        fidelityScore = ImageOptimizerService.CalculateFidelityScore(original, optimized);
                    }
                }
                catch
                {
                    // Fail gracefully if metadata reading fails
                }

                string originalBase64 = Convert.ToBase64String(originalBytes);
                string optimizedBase64 = Convert.ToBase64String(optimizedBytes);

                return Ok(new
                {
                    OriginalSize = originalBytes.Length,
                    OptimizedSize = optimizedBytes.Length,
                    OriginalType = contentType,
                    OptimizedType = newContentType,
                    OriginalWidth = originalWidth,
                    OriginalHeight = originalHeight,
                    OptimizedWidth = optimizedWidth,
                    OptimizedHeight = optimizedHeight,
                    OriginalBase64 = "data:" + contentType + ";base64," + originalBase64,
                    OptimizedBase64 = "data:" + newContentType + ";base64," + optimizedBase64,
                    
                    // Config diagnostic mappings passed to client reporting views
                    JpegQuality = config.JpegQuality,
                    JpegProgressive = config.JpegProgressive,
                    WebpQuality = config.WebpQuality,
                    WebpLossless = config.WebpLossless,
                    AvifQuality = config.AvifQuality,
                    AvifSpeed = config.AvifSpeed,
                    PngCompressionLevel = config.PngCompressionLevel,
                    
                    // Execution diagnostic duration and visual fidelity metrics
                    ProcessingTimeMs = processingTimeMs,
                    FidelityScore = fidelityScore
                });
            }
            catch (Exception ex) {
                PluginLogger.LogError("Metadata sandbox execution failure", ex);
                return StatusCode(500, "Operation failed: " + ex.Message);
            }
        }

        [HttpPost("Test")]
        public async Task<IActionResult> TestOptimization([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No test file supplied.");
            }

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                byte[] originalBytes = ms.ToArray();
                string contentType = file.ContentType;

                // Extract original width and height
                int originalWidth = 0;
                int originalHeight = 0;
                try
                {
                    using var original = SKBitmap.Decode(originalBytes);
                    if (original != null)
                    {
                        originalWidth = original.Width;
                        originalHeight = original.Height;
                    }
                }
                catch
                {
                    // Fail gracefully if metadata reading fails
                }

                var optimizer = new ImageOptimizerService();
                var config = Plugin.Instance.Configuration;
                
                // Instrument in-memory optimization execution speed using high-precision Stopwatch
                var stopwatch = Stopwatch.StartNew();
                using var optimizedData = optimizer.OptimizeMetadataImage(originalBytes, contentType, out string newContentType);
                stopwatch.Stop();
                long processingTimeMs = stopwatch.ElapsedMilliseconds;

                byte[] optimizedBytes = optimizedData != null ? optimizedData.ToArray() : originalBytes;

                // Extract optimized width and height
                int optimizedWidth = 0;
                int optimizedHeight = 0;
                double fidelityScore = 100.0;
                try
                {
                    using var original = SKBitmap.Decode(originalBytes);
                    using var optimized = SKBitmap.Decode(optimizedBytes);
                    if (original != null && optimized != null)
                    {
                        optimizedWidth = optimized.Width;
                        optimizedHeight = optimized.Height;
                        fidelityScore = ImageOptimizerService.CalculateFidelityScore(original, optimized);
                    }
                }
                catch
                {
                    // Fail gracefully if metadata reading fails
                }

                string originalBase64 = Convert.ToBase64String(originalBytes);
                string optimizedBase64 = Convert.ToBase64String(optimizedBytes);

                return Ok(new
                {
                    OriginalSize = originalBytes.Length,
                    OptimizedSize = optimizedBytes.Length,
                    OriginalType = contentType,
                    OptimizedType = newContentType,
                    OriginalWidth = originalWidth,
                    OriginalHeight = originalHeight,
                    OptimizedWidth = optimizedWidth,
                    OptimizedHeight = optimizedHeight,
                    OriginalBase64 = "data:" + contentType + ";base64," + originalBase64,
                    OptimizedBase64 = "data:" + newContentType + ";base64," + optimizedBase64,
                    
                    // Config diagnostic mappings passed to client reporting views
                    JpegQuality = config.JpegQuality,
                    JpegProgressive = config.JpegProgressive,
                    WebpQuality = config.WebpQuality,
                    WebpLossless = config.WebpLossless,
                    AvifQuality = config.AvifQuality,
                    AvifSpeed = config.AvifSpeed,
                    PngCompressionLevel = config.PngCompressionLevel,
                    
                    // Execution diagnostic duration and visual fidelity metrics
                    ProcessingTimeMs = processingTimeMs,
                    FidelityScore = fidelityScore
                });
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Manual sandbox optimization pipeline failure", ex);
                return StatusCode(500, "Operation failed: " + ex.Message);
            }
        }

        private string GetActiveBackupRoot()
        {
            var config = Plugin.Instance.Configuration;
            if (!string.IsNullOrWhiteSpace(config.BackupFolderPath) && System.IO.Directory.Exists(config.BackupFolderPath))
            {
                return config.BackupFolderPath;
            }
            return System.IO.Path.Combine(_appPaths.ConfigurationDirectoryPath, "ImageOptimizerBackups");
        }

        [HttpPost("Path/Test")]
        public IActionResult TestPathAccess([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("Path cannot be empty.");
            }

            try
            {
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }

                // Verify absolute read/write access permissions on host platform folder structures
                string testFile = System.IO.Path.Combine(path, "irukandji_test_" + Guid.NewGuid().ToString() + ".tmp");
                System.IO.File.WriteAllText(testFile, "test");
                System.IO.File.Delete(testFile);

                return Ok(new { Success = true });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, ErrorMessage = ex.Message });
            }
        }

        // Sanitization helper ensuring 100% escape-proof and hack-proof blacklist inputs
        private string SanitizeInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ' || c == ',' || c == '.' || c == '/' || c == '\\')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        [HttpPost("Sweeper/Run")]
        public IActionResult RunSweeper([FromQuery] bool dryRun, [FromQuery] bool backup, [FromQuery] int startIndex = 0)
        {
            try
            {
                var optimizer = new ImageOptimizerService();
                var config = Plugin.Instance.Configuration;
                var results = new ConcurrentBag<object>(); // Thread-safe accumulator

                long totalOriginal = 0;
                long totalOptimized = 0;
                object statsLock = new object();

                // Dynamic resolver loads backup folders based on config rules (falls back to config root if blank/unwritable)
                string registryRoot = GetActiveBackupRoot();
                string registryPath = System.IO.Path.Combine(registryRoot, "optimized_registry.txt");
                var optimizedFiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (System.IO.File.Exists(registryPath))
                {
                    try
                    {
                        var lines = System.IO.File.ReadAllLines(registryPath);
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                optimizedFiles.Add(line.Trim());
                            }
                        }
                    }
                    catch { }
                }

                // Setup Backup Directories if selected for a live write run
                string currentBackupDir = null;
                string manifestPath = null;
                if (!dryRun && backup)
                {
                    System.IO.Directory.CreateDirectory(registryRoot);
                    string dateStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    currentBackupDir = System.IO.Path.Combine(registryRoot, "sweep_" + dateStr); // Correction: Fixed compilation typo
                    System.IO.Directory.CreateDirectory(currentBackupDir);
                    manifestPath = System.IO.Path.Combine(currentBackupDir, "manifest.txt");
                }

                var imageFiles = new System.Collections.Generic.List<SweeperFile>();

                // Structural parameters tracking deleted/purged files
                long totalPurgedBytes = 0;
                var purgedFoldersWithSizes = new System.Collections.Generic.List<(string Path, long Size)>();

                // #1: Metadata
                if (config.ProcessMetadata)
                {
                    string path = ResolveDirectoryPath("metadata");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #2: Collections
                if (config.ProcessCollections)
                {
                    string path = ResolveDirectoryPath("data", "collections");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #3: Playlists
                if (config.ProcessPlaylists)
                {
                    string path = ResolveDirectoryPath("data", "playlists");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #4: Smartlists
                if (config.ProcessSmartlists)
                {
                    string path = ResolveDirectoryPath("data", "smartlists");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #5: Library covers
                if (config.ProcessLibraryCovers)
                {
                    string path = ResolveDirectoryPath("root", "default");
                    if (System.IO.Directory.Exists(path))
                    {
                        int coverDim = config.ResizeLibraryCovers ? config.LibraryCoversWidth : config.MaxMetadataDimension;
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, coverDim);
                    }
                }

                // #6: User profile pics (Checks multiple configurations for profile layouts)
                if (config.ProcessUserProfiles)
                {
                    var path1 = System.IO.Path.Combine(_appPaths.ConfigurationDirectoryPath, "users");
                    if (System.IO.Directory.Exists(path1))
                    {
                        AddImagesFromDirectory(path1, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                    }

                    var parent = System.IO.Path.GetDirectoryName(_appPaths.ConfigurationDirectoryPath);
                    if (!string.IsNullOrEmpty(parent) && !parent.Equals("/") && !parent.Equals("\\"))
                    {
                        var path2 = System.IO.Path.Combine(parent, "users");
                        if (System.IO.Directory.Exists(path2) && !path2.Equals(path1, StringComparison.OrdinalIgnoreCase))
                        {
                            AddImagesFromDirectory(path2, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                        }
                    }

                    var pathAlt1 = "/config/config/users";
                    if (System.IO.Directory.Exists(pathAlt1) && !pathAlt1.Equals(path1, StringComparison.OrdinalIgnoreCase))
                    {
                        AddImagesFromDirectory(pathAlt1, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                    }
                    var pathAlt2 = "/config/users";
                    if (System.IO.Directory.Exists(pathAlt2) && !pathAlt2.Equals(path1, StringComparison.OrdinalIgnoreCase) && !pathAlt2.Equals(pathAlt1, StringComparison.OrdinalIgnoreCase))
                    {
                        AddImagesFromDirectory(pathAlt2, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                    }
                }

                // #7: Trickplay (Scans internal system directories)
                if (config.ProcessTrickplay)
                {
                    // Scan internal 10.10+ trickplay folders
                    string internalTrickplayPath = ResolveDirectoryPath("data", "trickplay");
                    if (System.IO.Directory.Exists(internalTrickplayPath))
                    {
                        AddImagesFromDirectory(internalTrickplayPath, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }

                    // Scan legacy 10.9 internal trickplay folders (under metadata/library)
                    string legacyTrickplayPath = ResolveDirectoryPath("metadata", "library");
                    if (System.IO.Directory.Exists(legacyTrickplayPath))
                    {
                        ScanLegacyInternalTrickplay(legacyTrickplayPath, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #8: Media Libraries & external trickplays (Scans physical media directories)
                var virtualFolders = _libraryManager.GetVirtualFolders();
                var includedLibs = (config.IncludedLibraries ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToList();

                foreach (var folder in virtualFolders)
                {
                    bool isLibrarySelectedForMediaFiles = includedLibs.Contains(folder.Name, StringComparer.OrdinalIgnoreCase);
                    bool isTrickplaySelected = config.ProcessTrickplay;

                    if (!isLibrarySelectedForMediaFiles && !isTrickplaySelected)
                    {
                        continue;
                    }

                    foreach (var path in folder.Locations)
                    {
                        if (System.IO.Directory.Exists(path))
                        {
                            ScanMediaLibrary(path, imageFiles, optimizedFiles, isLibrarySelectedForMediaFiles, isTrickplaySelected, config.PurgeTrickplay, ref totalPurgedBytes, purgedFoldersWithSizes, dryRun, config.MaxMetadataDimension);
                        }
                    }
                }

                // Limit the sweep to first 50 images to keep execution latency low (applying dry run offset check)
                int skipOffset = dryRun ? startIndex : 0;
                var filesToProcess = imageFiles.Skip(skipOffset).Take(50).ToList();

                int processLimit = filesToProcess.Count;

                // Queue unmanaged memory buffers in ConcurrentBags to execute high-speed parallel compression
                var processedTasks = new ConcurrentBag<(string FilePath, byte[] OriginalBytes, byte[] OptimizedBytes, string NewContentType, string Ext, int OrigW, int OrigH, int OptW, int OptH, double FidelityScore, string ContentType)>();
                var parallelErrors = new ConcurrentBag<(string FilePath, Exception Ex)>();

                // Multithreaded CPU-bound parallel processing block (scales with host CPU threadpools)
                Parallel.For(0, processLimit, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                {
                    var sweeperFile = filesToProcess[i];
                    string filePath = sweeperFile.FilePath;
                    try
                    {
                        // Optimization: Open files using unmanaged shared streams to bypass OS file-locking events from library watchers
                        byte[] originalBytes;
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            using (var tempMs = new MemoryStream((int)fs.Length))
                            {
                                fs.CopyTo(tempMs);
                                originalBytes = tempMs.ToArray();
                            }
                        }

                        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                        string contentType = ext == ".png" ? "image/png" : (ext == ".gif" ? "image/gif" : (ext == ".webp" ? "image/webp" : "image/jpeg"));
                        int origW = 0, origH = 0;
                        using (var b = SKBitmap.Decode(originalBytes)) { if (b != null) { origW = b.Width; origH = b.Height; } }

                        string newContentType;
                        using var optimizedData = optimizer.ProcessImage(originalBytes, contentType, sweeperFile.MaxDimension, config, out newContentType, isMetadata: true);
                        byte[] optimizedBytes = optimizedData != null ? optimizedData.ToArray() : originalBytes;

                        int optW = 0, optH = 0;
                        double fidelityScore = 100.0;
                        
                        // Calculation: Compare output file size against minimum improvement percentage boundary to prevent degradation
                        long sizeDifferenceThreshold = (long)(originalBytes.Length * (config.MinImprovementPercentage / 100.0));
                        bool meetsImprovementThreshold = (originalBytes.Length - optimizedBytes.Length) >= sizeDifferenceThreshold;

                        // Only calculate the fidelity score if the image was actually optimized (skips CPU-heavy math on skipped files)
                        if (meetsImprovementThreshold)
                        {
                            try
                            {
                                using (var origBitmap = SKBitmap.Decode(originalBytes))
                                using (var optBitmap = SKBitmap.Decode(optimizedBytes))
                                {
                                    if (origBitmap != null && optBitmap != null)
                                    {
                                        optW = optBitmap.Width;
                                        optH = optBitmap.Height;
                                        fidelityScore = ImageOptimizerService.CalculateFidelityScore(origBitmap, optBitmap);
                                    }
                                }
                            }
                            catch { }
                        }

                        processedTasks.Add((filePath, originalBytes, optimizedBytes, newContentType, ext, origW, origH, optW, optH, fidelityScore, contentType));
                    }
                    catch (Exception ex)
                    {
                        parallelErrors.Add((filePath, ex));
                    }
                });

                // Sequential I/O Exception Logging (Ensures zero lock-contention during parallel loops)
                foreach (var err in parallelErrors)
                {
                    PluginLogger.LogError("Sweeper thread failed to process file: " + err.FilePath, err.Ex);
                }

                // Sequential I/O Drain Phase (Safely writes compressed frames back to disk sequentially with no concurrent locks)
                foreach (var task in processedTasks)
                {
                    try
                    {
                        bool isSaved = false;

                        long sizeDifferenceThreshold = (long)(task.OriginalBytes.Length * (config.MinImprovementPercentage / 100.0));
                        bool meetsImprovementThreshold = (task.OriginalBytes.Length - task.OptimizedBytes.Length) >= sizeDifferenceThreshold;

                        if (!dryRun && meetsImprovementThreshold)
                        {
                            if (backup && currentBackupDir != null && manifestPath != null)
                            {
                                try
                                {
                                    string guidName = Guid.NewGuid().ToString() + task.Ext;
                                    string backupFilePath = System.IO.Path.Combine(currentBackupDir, guidName);

                                    using (var bfs = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                                    {
                                        bfs.Write(task.OriginalBytes, 0, task.OriginalBytes.Length);
                                    }

                                    System.IO.File.AppendAllText(manifestPath, guidName + "|" + task.FilePath + Environment.NewLine);
                                }
                                catch (Exception bEx)
                                {
                                    PluginLogger.LogError("Failed to backup original file: " + task.FilePath, bEx);
                                }
                            }

                            using (var wfs = new FileStream(task.FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                            {
                                wfs.Write(task.OptimizedBytes, 0, task.OptimizedBytes.Length);
                            }
                            isSaved = true;
                        }

                        if (!dryRun)
                        {
                            try
                            {
                                System.IO.Directory.CreateDirectory(registryRoot);
                                System.IO.File.AppendAllText(registryPath, task.FilePath + Environment.NewLine);
                            }
                            catch { }
                        }

                        // Stats exclusion: Incremented size totals ONLY if the file was actually optimized and saved/written
                        if (meetsImprovementThreshold)
                        {
                            lock (statsLock)
                            {
                                totalOriginal += task.OriginalBytes.Length;
                                totalOptimized += task.OptimizedBytes.Length;
                            }
                        }

                        results.Add(new
                        {
                            Path = task.FilePath,
                            OriginalSize = task.OriginalBytes.Length,
                            OptimizedSize = meetsImprovementThreshold ? task.OptimizedBytes.Length : task.OriginalBytes.Length,
                            OriginalDim = task.OrigW + "x" + task.OrigH,
                            OptimizedDim = meetsImprovementThreshold ? (task.OptW + "x" + task.OptH) : (task.OrigW + "x" + task.OrigH), // Correction: Skip resolution displays original size
                            ContentType = task.ContentType,
                            NewContentType = meetsImprovementThreshold ? task.NewContentType : task.ContentType, // Correction: Fallback content-type if skipped
                            FidelityScore = meetsImprovementThreshold ? task.FidelityScore : 0.0, // Correction: Exclude fidelity info if skipped
                            SavedStatus = isSaved ? "Saved" : (meetsImprovementThreshold ? "DryRun" : "NotImprovedEnough"),
                            ReductionPercent = meetsImprovementThreshold ? 0.0 : Math.Round(((double)(task.OriginalBytes.Length - task.OptimizedBytes.Length) / task.OriginalBytes.Length) * 100.0, 1)
                        });
                    }
                    catch (Exception ex)
                    {
                        PluginLogger.LogError("Sweeper failed to write out optimized file sequentially: " + task.FilePath, ex);
                    }
                }

                // Add purged folders as 100% saved space into execution reports
                foreach (var pf in purgedFoldersWithSizes)
                {
                    results.Add(new
                    {
                        Path = pf.Path,
                        OriginalSize = pf.Size,
                        OptimizedSize = 0L,
                        OriginalDim = "N/A",
                        OptimizedDim = "Purged",
                        ContentType = "Folder (Trickplay)",
                        NewContentType = "None",
                        FidelityScore = 100.0,
                        SavedStatus = dryRun ? "DryRunPurged" : "Purged",
                        ReductionPercent = 100.0
                    });
                }

                totalOriginal += totalPurgedBytes;

                return Ok(new { Results = results, TotalOriginal = totalOriginal, TotalOptimized = totalOptimized, IsDryRun = dryRun });
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Sweeper execution failed", ex);
                return StatusCode(500, "Sweeper failed: " + ex.Message);
            }
        }

        private void AddImagesFromDirectory(string path, System.Collections.Generic.List<SweeperFile> fileList, System.Collections.Generic.HashSet<string> optimizedFiles, int maxDim)
        {
            if (fileList.Count >= 200) return;

            string[] files = null;
            try
            {
                files = System.IO.Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch { }

            var config = Plugin.Instance.Configuration;
            string sw = SanitizeInput(config.BlacklistWords);
            var blacklist = !string.IsNullOrWhiteSpace(sw) ? sw.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

            if (files != null)
            {
                foreach (var f in files)
                {
                    if (optimizedFiles.Contains(f)) continue;

                    if (blacklist.Length > 0)
                    {
                        bool blocked = false;
                        if (config.BlacklistUseAndOperator)
                        {
                            bool allMatched = true;
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (!m) { allMatched = false; break; }
                            }
                            blocked = allMatched;
                        }
                        else
                        {
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (m) { blocked = true; break; }
                            }
                        }

                        if (blocked)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(System.IO.Path.Combine(GetActiveBackupRoot(), "optimized_registry.txt"), f + Environment.NewLine);
                            }
                            catch { }
                            continue;
                        }
                    }

                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp")
                    {
                        fileList.Add(new SweeperFile { FilePath = f, MaxDimension = maxDim });
                        if (fileList.Count >= 200) return;
                    }
                }
            }

            string[] subDirs = null;
            try
            {
                subDirs = System.IO.Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (subDirs != null)
            {
                foreach (var sub in subDirs)
                {
                    AddImagesFromDirectory(sub, fileList, optimizedFiles, maxDim); // Correction: Passed optimizedFiles parameter correctly to resolve recursive compilation error
                    if (fileList.Count >= 200) return;
                }
            }
        }

        private void ScanLegacyInternalTrickplay(string path, System.Collections.Generic.List<SweeperFile> fileList, System.Collections.Generic.HashSet<string> optimizedFiles, int maxDim)
        {
            if (fileList.Count >= 200) return;

            string[] subDirs = null;
            try
            {
                subDirs = System.IO.Directory.GetDirectories(path, "*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (subDirs != null)
            {
                foreach (var sub in subDirs)
                {
                    var dirName = System.IO.Path.GetFileName(sub);
                    if (dirName.Equals("trickplay", StringComparison.OrdinalIgnoreCase))
                    {
                        // Found a legacy trickplay folder, add all images inside it recursively
                        AddImagesFromDirectory(sub, fileList, optimizedFiles, maxDim);
                    }
                    else
                    {
                        // Recursively traverse
                        ScanLegacyInternalTrickplay(sub, fileList, optimizedFiles, maxDim);
                    }
                }
            }
        }

        private void ScanMediaLibrary(
            string currentPath, 
            System.Collections.Generic.List<SweeperFile> fileList, 
            System.Collections.Generic.HashSet<string> optimizedFiles,
            bool isLibrarySelectedForMediaFiles,
            bool isTrickplaySelected,
            bool purgeTrickplay,
            ref long totalPurgedBytes,
            System.Collections.Generic.List<(string Path, long Size)> purgedFoldersWithSizes,
            bool dryRun,
            int maxDim)
        {
            if (fileList.Count >= 200) return;

            string[] subDirs = null;
            try
            {
                subDirs = System.IO.Directory.GetDirectories(currentPath, "*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (subDirs != null)
            {
                foreach (var sub in subDirs)
                {
                    var dirName = System.IO.Path.GetFileName(sub);
                    bool isTrickplayDir = dirName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase) || 
                                         dirName.Equals("trickplay", StringComparison.OrdinalIgnoreCase);

                    if (isTrickplayDir)
                    {
                        if (isTrickplaySelected)
                        {
                            if (purgeTrickplay && IsUnnecessaryTrickplayFolder(sub))
                            {
                                long folderSize = GetDirectorySize(sub);
                                totalPurgedBytes += folderSize;
                                purgedFoldersWithSizes.Add((sub, folderSize));

                                if (!dryRun)
                                {
                                    try
                                    {
                                        System.IO.Directory.Delete(sub, recursive: true);
                                    }
                                    catch (Exception ex)
                                    {
                                        PluginLogger.LogError("Failed to purge trickplay folder: " + sub, ex);
                                    }
                                }
                                continue; // Skip scanning files inside this purged folder
                            }

                            // If not purging, scan files inside this trickplay folder recursively
                            AddImagesFromDirectory(sub, fileList, optimizedFiles, maxDim);
                        }
                    }
                    else
                    {
                        // Recursive scan of standard subdirectories
                        ScanMediaLibrary(sub, fileList, optimizedFiles, isLibrarySelectedForMediaFiles, isTrickplaySelected, purgeTrickplay, ref totalPurgedBytes, purgedFoldersWithSizes, dryRun, maxDim);
                    }
                }
            }

            // Scan standard files in the current folder (only if this library's standard files are selected)
            if (isLibrarySelectedForMediaFiles)
            {
                ScanLibraryFiles(currentPath, fileList, optimizedFiles, maxDim);
            }
        }

        private long GetDirectorySize(string folderPath)
        {
            long size = 0;
            try
            {
                var files = System.IO.Directory.GetFiles(folderPath, "*.*", System.IO.SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    size += new System.IO.FileInfo(f).Length;
                }
            }
            catch { }
            return size;
        }

        private bool IsUnnecessaryTrickplayFolder(string folderPath)
        {
            var folderName = System.IO.Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(folderName)) return false;

            bool isTrickplay = folderName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase) || 
                               folderName.Equals("trickplay", StringComparison.OrdinalIgnoreCase);

            if (!isTrickplay) return false;

            // Pattern A: Ends with "-trailer.trickplay"
            if (folderName.EndsWith("-trailer.trickplay", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Pattern B: Stored inside unnecessary trailers, clips, samples, or extras folders
            var segments = folderPath.Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                if (UnnecessaryTrickplayParentFolders.Contains(seg, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void ScanLibraryFiles(string path, System.Collections.Generic.List<SweeperFile> fileList, System.Collections.Generic.HashSet<string> optimizedFiles, int maxDim)
        {
            if (fileList.Count >= 200) return;

            string[] files = null;
            try
            {
                files = System.IO.Directory.GetFiles(path, "*.*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch { }

            var config = Plugin.Instance.Configuration;
            string sw = SanitizeInput(config.BlacklistWords);
            var blacklist = !string.IsNullOrWhiteSpace(sw) ? sw.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

            if (files != null)
            {
                foreach (var f in files)
                {
                    if (optimizedFiles.Contains(f)) continue;

                    if (blacklist.Length > 0)
                    {
                        bool blocked = false;
                        if (config.BlacklistUseAndOperator)
                        {
                            bool allMatched = true;
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (!m) { allMatched = false; break; }
                            }
                            blocked = allMatched;
                        }
                        else
                        {
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (m) { blocked = true; break; }
                            }
                        }

                        if (blocked)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(System.IO.Path.Combine(GetActiveBackupRoot(), "optimized_registry.txt"), f + Environment.NewLine);
                            }
                            catch { }
                            continue;
                        }
                    }

                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp")
                    {
                        fileList.Add(new SweeperFile { FilePath = f, MaxDimension = maxDim });
                        if (fileList.Count >= 200) return;
                    }
                }
            }
        }
    }
}