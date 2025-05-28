using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json;
using OfficeOpenXml;

namespace Excel2GeoJson {
    // Required NuGet packages:
// - EPPlus
// - Newtonsoft.Json
// - GeoJSON.Net

    public static class Program {
        public static void Main() {
            ExcelPackage.License.SetNonCommercialPersonal("Seeleo");

            var excelFilePath = "data.xlsx";
            var outputJsonPath = "data.json";

            var points = ReadPointsFromExcel(excelFilePath);
            var featureCollection = CreateGeoJsonFeatureCollection(points);

            // Serialize to JSON with proper converters
            var json = JsonConvert.SerializeObject(featureCollection, Formatting.Indented);
            File.WriteAllText(outputJsonPath, json);

            Console.WriteLine($"GeoJSON file created at: {outputJsonPath}");
        }

        private static List<GeoPoint> ReadPointsFromExcel(string filePath) {
            var points = new List<GeoPoint>();

            using var package = new ExcelPackage(new FileInfo(filePath));
            var worksheet = package.Workbook.Worksheets[0];
            var rowCount = worksheet.Dimension.Rows;

            // Assuming first row has headers
            for (var row = 2; row <= rowCount; row++) {
                var latitude = Convert.ToDouble(worksheet.Cells[row, 1].Value);
                var longitude = Convert.ToDouble(worksheet.Cells[row, 2].Value);
                var altitude = Convert.ToDouble(worksheet.Cells[row, 3].Value);
                var timestamp = Convert.ToDouble(worksheet.Cells[row, 4].Value);
                var item = new GeoPoint {
                    Latitude = latitude,
                    Longitude = longitude,
                    Altitude = altitude,
                    Timestamp = timestamp
                };
                points.Add(item);
            }

            return points;
        }

        private static FeatureCollection CreateGeoJsonFeatureCollection(List<GeoPoint> points) {
            var features = new List<Feature>();

            foreach (var point in points) {
                // Create the geometry point
                var position = new Position(point.Latitude, point.Longitude, point.Altitude);
                var geoPoint = new Point(position);

                // Create properties dictionary
                var properties = new Dictionary<string, object> {
                    { "timestamp", point.Timestamp }
                };

                // Create feature with geometry and properties
                var feature = new Feature(geoPoint, properties);
                features.Add(feature);
            }

            return new FeatureCollection(features);
        }
    }

    public class GeoPoint {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public double Timestamp { get; set; }
    }
}