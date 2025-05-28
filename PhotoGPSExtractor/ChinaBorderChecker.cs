using GeoJSON.Net.Feature;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace PhotoGPSExtractor {
    public class ChinaBorderChecker {
        private Geometry _chinaBoundary;
        private bool _isInitialized;

        /// <summary>
        /// Initializes a new instance of the ChinaBorderChecker class.
        /// </summary>
        /// <param name="geojsonFilePath">Path to the China border GeoJSON file</param>
        public ChinaBorderChecker(string geojsonFilePath) {
            Initialize(geojsonFilePath);
        }

        /// <summary>
        /// Loads and initializes the China border geometry from a GeoJSON file.
        /// </summary>
        /// <param name="geojsonFilePath">Path to the China border GeoJSON file</param>
        private void Initialize(string geojsonFilePath) {
            try {
                if (!File.Exists(geojsonFilePath)) {
                    throw new FileNotFoundException($"GeoJSON file not found: {geojsonFilePath}");
                }

                var geojsonContent = File.ReadAllText(geojsonFilePath);

                // Parse the GeoJSON file
                var serializer = GeoJsonSerializer.Create();
                using var stringReader = new StringReader(geojsonContent);
                using var jsonReader = new JsonTextReader(stringReader);
                var featureCollection = serializer.Deserialize<FeatureCollection>(jsonReader);

                if (featureCollection == null || featureCollection.Features.Count == 0) {
                    throw new InvalidDataException("Invalid GeoJSON: No features found");
                }

                // For a country border, we expect a FeatureCollection with one or more Polygon/MultiPolygon features
                // We'll combine them all into a single geometry
                var geometryFactory = new GeometryFactory();
                var geometries = new Geometry[featureCollection.Features.Count];

                for (var i = 0; i < featureCollection.Features.Count; i++) {
                    geometries[i] = featureCollection.Features[i].Geometry as Geometry;
                }

                // Create a unified geometry representing all of China's borders
                _chinaBoundary = geometryFactory.CreateGeometryCollection(geometries).Union();
                _isInitialized = true;
            } catch (Exception ex) {
                _isInitialized = false;
                throw new Exception($"Failed to initialize China border geometry: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Checks if a point with the given latitude and longitude is within China's borders.
        /// </summary>
        /// <param name="latitude">The latitude of the point to check</param>
        /// <param name="longitude">The longitude of the point to check</param>
        /// <returns>True if the point is within China, false otherwise</returns>
        public bool IsPointInChina(double latitude, double longitude) {
            if (!_isInitialized) {
                throw new InvalidOperationException("ChinaBorderChecker is not properly initialized");
            }

            // Create a point with the given coordinates
            var point = new Point(longitude, latitude);

            // Check if the point is contained in China's boundary
            return _chinaBoundary.Contains(point);
        }

        /// <summary>
        /// Checks if a point with the given coordinates is within China's borders.
        /// </summary>
        /// <param name="point">A Coordinate object with Longitude (X) and Latitude (Y) properties</param>
        /// <returns>True if the point is within China, false otherwise</returns>
        public bool IsPointInChina(Coordinate point) {
            if (!_isInitialized) {
                throw new InvalidOperationException("ChinaBorderChecker is not properly initialized");
            }

            var pointGeometry = new Point(point);
            return _chinaBoundary.Contains(pointGeometry);
        }
    }
}