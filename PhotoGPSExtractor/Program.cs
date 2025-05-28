using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Directory = System.IO.Directory;

namespace PhotoGPSExtractor {
    public static class Program {
        private readonly static EnumerationOptions FastEnumerationOptions = new() {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
        };

        public static async Task Main() {
            Console.WriteLine("Photo GPS Metadata Extractor");
            Console.WriteLine("----------------------------");

            var folderPath = GetFolderPath();
            if (folderPath == null) return;

            var stopwatch = Stopwatch.StartNew();

            try {
                // Phase 1: Parallel file discovery
                var (files, discoveryTime) = await FindPhotoFilesAsync(folderPath);
                if (files.Count == 0) {
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
            } catch (Exception ex) {
                Console.WriteLine($"\nFatal error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
            }
        }

        private static string? GetFolderPath() {
            Console.Write("Folder path (or drag folder here): ");
            var input = Console.ReadLine()?.Trim('"').Trim();

            if (string.IsNullOrWhiteSpace(input)) {
                Console.WriteLine("No path provided");
                return null;
            }

            if (Directory.Exists(input)) {
                return input;
            }

            Console.WriteLine("Directory does not exist!");
            return null;

        }

        private static async Task<(List<string> Files, TimeSpan DiscoveryTime)> FindPhotoFilesAsync(string folderPath) {
            Console.WriteLine("\nDiscovering photo files...");
            var stopwatch = Stopwatch.StartNew();

            var files = new ConcurrentBag<string>();
            var lastReport = Stopwatch.GetTimestamp();
            var totalFiles = 0;

            await Task.Run(() => {
                Parallel.ForEach(Directory.EnumerateFiles(folderPath, "*.*", FastEnumerationOptions), file => {
                    files.Add(file);
                    totalFiles = files.Count;

                    // Throttled progress reporting
                    if (Stopwatch.GetTimestamp() - lastReport > Stopwatch.Frequency / 4) {
                        Console.Write($"\rFound {totalFiles} files...");
                        lastReport = Stopwatch.GetTimestamp();
                    }
                });
            });

            Console.Write($"\rFound {totalFiles} files in {stopwatch.Elapsed.TotalSeconds:0.00}s");
            return (files.ToList(), stopwatch.Elapsed);
        }

        private static async Task<(List<LocationData> Locations, TimeSpan ProcessingTime)> ProcessFilesAsync(
            List<string> files) {
            Console.WriteLine("\n\nProcessing files...");
            var stopwatch = Stopwatch.StartNew();

            var locations = new ConcurrentBag<LocationData>();
            var progress = new ProgressReporter(files.Count);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            await Task.Run(() => {
                Parallel.ForEach(files, options, file => {
                    try {
                        var location = ProcessSingleFile(file);
                        if (location != null) {
                            locations.Add(location);
                        }
                    } catch (Exception) {
                        //Debug.WriteLine($"Error processing {file}: {ex.Message}");
                    } finally {
                        progress.ReportProgress();
                    }
                });
            });

            return (locations.OrderBy(l => l.Timestamp).ToList(), stopwatch.Elapsed);
        }

        private static LocationData? ProcessSingleFile(string filePath) {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

            var location = gps?.GetGeoLocation();
            if (location == null) return null;

            // Extract altitude
            decimal? altitude = null;
            try {
                if (gps != null && gps.ContainsTag(GpsDirectory.TagAltitude)) {
                    altitude = Math.Abs((decimal)gps.GetDouble(GpsDirectory.TagAltitude));
                    var intAltitudeRef = gps.GetInt32(GpsDirectory.TagAltitudeRef);
                    var strAltitudeRef = gps.GetDescription(GpsDirectory.TagAltitudeRef) ?? string.Empty;
                    if (intAltitudeRef == 1 ||
                        strAltitudeRef.Equals("Below sea level", StringComparison.OrdinalIgnoreCase)) {
                        altitude *= -1;
                    }
                }
            } catch (Exception) {
                // ignored
            }

            // Extract timestamp
            long timestamp = 0;
            try {
                var exifSub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (exifSub?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) == true ||
                    exifSub?.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out dt) == true ||
                    exifSub?.TryGetDateTime(ExifDirectoryBase.TagDateTime, out dt) == true) {
                    timestamp = new DateTimeOffset(dt).ToUnixTimeSeconds();
                }
            } catch (Exception) {
                // ignored
            }

            return new LocationData(location.Latitude, location.Longitude, altitude, timestamp);
        }

        private static List<LocationData> DeduplicateLocations(List<LocationData> locations, int precision = 6) {
            var uniqueKeys = new HashSet<Tuple<double, double>>();
            var result = new List<LocationData>();

            foreach (var loc in locations) {
                // 精度缩减
                var roundedLat = Math.Round(loc.Latitude, precision);
                var roundedLon = Math.Round(loc.Longitude, precision);
                var key = Tuple.Create(roundedLat, roundedLon);

                // 去重判断
                if (uniqueKeys.Add(key)) {
                    result.Add(loc with { Latitude = roundedLat, Longitude = roundedLon });
                }
            }

            return result;
        }

        private static async Task ExportResultsAsync(List<LocationData> locations) {
            var tasks = new List<Task> {
                Task.Run(() => {
                    ExportToCsv(locations);
                    ExportToGeoJson(DeduplicateLocations(locations, 4));
                })
            };
            await Task.WhenAll(tasks);
        }

        private static void ExportToCsv(List<LocationData> locations) {
            const string csvPath = "data.csv";
            using var writer = new StreamWriter(csvPath);

            writer.WriteLine("Latitude,Longitude,Altitude,Timestamp,FileName,FilePath");
            foreach (var loc in locations) {
                var altitude = loc.Altitude != null ? loc.Altitude.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
                var timestamp = loc.Timestamp != null ? loc.Timestamp.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
                writer.WriteLine($"{loc.Latitude},{loc.Longitude},{altitude},{timestamp}");
            }

            Console.WriteLine($"\nCSV exported to {csvPath}");
        }

        private static void ExportToGeoJson(List<LocationData> locations) {
            const string jsonPath = "data.json";
            var featureCollection = new List<Feature>();

            foreach (var location in locations) {
                // Create the geometry point
                double? altitude = location.Altitude == null ? null : Convert.ToDouble(location.Altitude);
                var position = new Position(location.Latitude, location.Longitude, altitude);
                var geoPoint = new Point(position);

                // Create properties dictionary
                Dictionary<string, object>? properties = null;
                if (location.Timestamp != null) {
                    properties = new Dictionary<string, object> {
                        { "timestamp", location.Timestamp }
                    };
                }
                var feature = new Feature(geoPoint, properties);
                featureCollection.Add(feature);
            }

            var json = JsonConvert.SerializeObject(new FeatureCollection(featureCollection), Formatting.Indented);
            File.WriteAllText(jsonPath, json);

            Console.WriteLine($"\nGeoJSON exported to {jsonPath}");
        }
    }

    internal record LocationData(
        double Latitude,
        double Longitude,
        decimal? Altitude,
        long? Timestamp
    );

    internal class ProgressReporter {
        private readonly object _lock = new();
        private readonly int _total;
        private long _lastReportTime;
        private int _processed;

        public ProgressReporter(int total) => _total = total;

        public void ReportProgress() {
            lock (_lock) {
                _processed++;
                var now = Stopwatch.GetTimestamp();

                // Throttle to 2 updates/sec
                if (now - _lastReportTime <= Stopwatch.Frequency / 2) {
                    return;
                }

                Console.Write($"\rProcessed {_processed} of {_total} ({_processed * 100 / _total}%)...");
                _lastReportTime = now;
            }
        }
    }
}