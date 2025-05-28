using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Newtonsoft.Json;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Collections.Concurrent;
using System.Diagnostics;
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
                await ExportResultsAsync(locations, folderPath);

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

        private static async Task ExportResultsAsync(List<LocationData> locations, string folderPath) {
            var tasks = new List<Task> {
                Task.Run(() => {
                    ExportToExcel(locations, folderPath);
                    ExportToGeoJson(DeduplicateLocations(locations, 4), folderPath);
                })
            };
            await Task.WhenAll(tasks);
        }

        /*
        private static void ExportToCsv(List<LocationData> locations, bool isWgs84 = true) {
            const string csvPath = "data.csv";
            using var writer = new StreamWriter(csvPath);

            writer.WriteLine("Latitude,Longitude,Altitude,Timestamp,FileName,FilePath");
            foreach (var location in locations) {
                var latitude = location.Latitude;
                var longitude = location.Longitude;
                if (isWgs84) {
                    EvilTransform.Transform(location.Latitude, location.Longitude, out latitude, out longitude);
                }

                var altitude = location.Altitude != null
                    ? location.Altitude.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                var timestamp = location.Timestamp != null
                    ? location.Timestamp.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                writer.WriteLine($"{latitude},{longitude},{altitude},{timestamp}");
            }

            Console.WriteLine($"\nCSV exported to {csvPath}");
        }
        */

        private static void ExportToExcel(List<LocationData> locations, string filePath) {
            ExcelPackage.License.SetNonCommercialPersonal("Seeleo");

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Data");

            // 设置表头样式
            using (var headerRange = worksheet.Cells[1, 1, 1, 6]) {
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                headerRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
            }

            // 写入表头
            worksheet.Cells[1, 1].Value = "Latitude";
            worksheet.Cells[1, 2].Value = "Longitude";
            worksheet.Cells[1, 3].Value = "Altitude";
            worksheet.Cells[1, 4].Value = "Timestamp";

            // 写入数据
            for (var i = 0; i < locations.Count; i++) {
                var row = i + 2;
                worksheet.Cells[row, 1].Value = locations[i].Latitude;
                worksheet.Cells[row, 2].Value = locations[i].Longitude;
                worksheet.Cells[row, 3].Value = locations[i].Altitude;
                worksheet.Cells[row, 4].Value = locations[i].Timestamp;
            }

            // 自动调整列宽
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // 保存文件
            filePath = Path.Combine(filePath, "data.xlsx");
            package.SaveAs(new FileInfo(filePath));
            
            Console.WriteLine($"\nExcel exported to {filePath}");
        }

        private static void ExportToGeoJson(List<LocationData> locations, string filePath, bool isWgs84 = true) {
            var featureCollection = new List<Feature>();

            foreach (var location in locations) {
                // Create the geometry point
                var latitude = location.Latitude;
                var longitude = location.Longitude;
                if (isWgs84) {
                    EvilTransform.Transform(latitude, longitude, out latitude, out longitude);
                }

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
            filePath = Path.Combine(filePath, "data.geojson");
            File.WriteAllText(filePath, json);

            Console.WriteLine($"\nGeoJSON exported to {filePath}");
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