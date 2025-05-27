using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using System.Collections.Concurrent;
using System.Diagnostics;
using Directory = System.IO.Directory;

namespace PhotoGPSExtractor
{
    public static class Program
    {
        private readonly static HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff",
            ".heic", ".webp", ".arw", ".cr2", ".nef", ".orf", ".raf", ".dng"
        };

        private readonly static EnumerationOptions FastEnumerationOptions = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
        };

        public static async Task Main()
        {
            Console.WriteLine("Photo GPS Metadata Extractor");
            Console.WriteLine("----------------------------");
            
            var folderPath = GetFolderPath();
            if (folderPath == null) return;

            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                // Phase 1: Parallel file discovery
                var (files, discoveryTime) = await FindPhotoFilesAsync(folderPath);
                if (files.Count == 0)
                {
                    Console.WriteLine("No supported photo files found!");
                    return;
                }

                // Phase 2: Parallel metadata processing
                var (locations, processingTime) = await ProcessFilesAsync(files);

                // Phase 3: Export results
                await ExportResultsAsync(locations);

                Console.WriteLine($"\nCompleted in {stopwatch.Elapsed.TotalSeconds:0.00}s");
                Console.WriteLine($"- Discovery: {discoveryTime.TotalSeconds:0.00}s");
                Console.WriteLine($"- Processing: {processingTime.TotalSeconds:0.00}s");
                Console.WriteLine($"- {locations.Count} locations extracted");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nFatal error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
            }
        }

        private static string? GetFolderPath()
        {
            Console.Write("Folder path (or drag folder here): ");
            var input = Console.ReadLine()?.Trim('"').Trim();
            
            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("No path provided");
                return null;
            }

            if (!Directory.Exists(input))
            {
                Console.WriteLine("Directory does not exist!");
                return null;
            }

            return input;
        }

        private static async Task<(List<string> Files, TimeSpan DiscoveryTime)> FindPhotoFilesAsync(string folderPath)
        {
            Console.WriteLine("\nDiscovering photo files...");
            var stopwatch = Stopwatch.StartNew();
            
            var files = new ConcurrentBag<string>();
            var lastReport = Stopwatch.GetTimestamp();
            var totalFiles = 0;

            await Task.Run(() =>
            {
                Parallel.ForEach(Directory.EnumerateFiles(folderPath, "*.*", FastEnumerationOptions), file =>
                {
                    if (PhotoExtensions.Contains(Path.GetExtension(file)))
                    {
                        files.Add(file);
                        totalFiles = files.Count;

                        // Throttled progress reporting
                        if (Stopwatch.GetTimestamp() - lastReport > Stopwatch.Frequency / 4)
                        {
                            Console.Write($"\rFound {totalFiles} files...");
                            lastReport = Stopwatch.GetTimestamp();
                        }
                    }
                });
            });

            Console.Write($"\rFound {totalFiles} files in {stopwatch.Elapsed.TotalSeconds:0.00}s");
            return (files.ToList(), stopwatch.Elapsed);
        }

        private static async Task<(List<LocationData> Locations, TimeSpan ProcessingTime)> ProcessFilesAsync(List<string> files)
        {
            Console.WriteLine("\n\nProcessing files...");
            var stopwatch = Stopwatch.StartNew();
            
            var locations = new ConcurrentBag<LocationData>();
            var progress = new ProgressReporter(files.Count);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            await Task.Run(() =>
            {
                Parallel.ForEach(files, options, file =>
                {
                    try
                    {
                        var location = ProcessSingleFile(file);
                        if (location != null)
                        {
                            locations.Add(location);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing {file}: {ex.Message}");
                    }
                    finally
                    {
                        progress.ReportProgress();
                    }
                });
            });

            return (locations.OrderBy(l => l.Timestamp).ToList(), stopwatch.Elapsed);
        }

        private static LocationData? ProcessSingleFile(string filePath)
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            if (gps == null) return null;

            var location = gps.GetGeoLocation();
            if (location == null) return null;

            // Extract altitude
            decimal altitude = 0;
            if (gps.ContainsTag(GpsDirectory.TagAltitude))
            {
                altitude = Math.Abs((decimal)gps.GetDouble(GpsDirectory.TagAltitude));
                if (gps.GetDescription(GpsDirectory.TagAltitudeRef)?
                    .Trim().Equals("Below sea level", StringComparison.OrdinalIgnoreCase) == true)
                {
                    altitude *= -1;
                }
            }

            // Extract timestamp
            long timestamp = 0;
            var exifSub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exifSub?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) == true)
            {
                timestamp = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
            }

            return new LocationData(
                location.Latitude,
                location.Longitude,
                altitude,
                timestamp,
                Path.GetFileName(filePath),
                filePath
            );
        }

        private static async Task ExportResultsAsync(List<LocationData> locations)
        {
            var tasks = new List<Task> { Task.Run(() => ExportToCsv(locations)) };
            await Task.WhenAll(tasks);
        }

        private static void ExportToCsv(List<LocationData> locations)
        {
            var csvPath = "photo_gps_data.csv";
            using var writer = new StreamWriter(csvPath);
            
            writer.WriteLine("Latitude,Longitude,Altitude,Timestamp,FileName,FilePath");
            foreach (var loc in locations)
            {
                writer.WriteLine($"{loc.Latitude},{loc.Longitude},{loc.Altitude},{loc.Timestamp},\"{loc.FileName}\",\"{loc.FilePath}\"");
            }
            
            Console.WriteLine($"\nCSV exported to {csvPath}");
        }
    }

    internal record LocationData(
        double Latitude,
        double Longitude,
        decimal Altitude,
        long Timestamp,
        string FileName,
        string FilePath
    );

    internal class ProgressReporter
    {
        private readonly int _total;
        private int _processed;
        private readonly object _lock = new();
        private long _lastReportTime;

        public ProgressReporter(int total) => _total = total;

        public void ReportProgress()
        {
            lock (_lock)
            {
                _processed++;
                var now = Stopwatch.GetTimestamp();
                
                if (now - _lastReportTime > Stopwatch.Frequency / 2) // Throttle to 2 updates/sec
                {
                    Console.Write($"\rProcessed {_processed} of {_total} ({_processed * 100 / _total}%)...");
                    _lastReportTime = now;
                }
            }
        }
    }
}