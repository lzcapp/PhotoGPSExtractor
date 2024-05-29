using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = System.IO.Directory;

Console.Write("Enter the folder path: ");
var folderPath = Console.ReadLine();

if (Directory.Exists(folderPath)) {
    try {
        var photoFiles = Directory.GetFiles(folderPath, "*.jpg", SearchOption.AllDirectories);

        var locationList = new List<LocationData>();

        foreach (var file in photoFiles) {
            try {
                var directories = ImageMetadataReader.ReadMetadata(file);
                GpsDirectory? gps = directories.OfType<GpsDirectory>().FirstOrDefault();
                ExifSubIfdDirectory? exifSub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

                if (gps != null) {
                    GeoLocation? location = gps.GetGeoLocation();
                    var altitude = gps.GetDouble(GpsDirectory.TagAltitude);
                    long timestamp = 0;
                    if (exifSub != null) {
                        exifSub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime timestampX);
                        timestamp = new DateTimeOffset(timestampX).ToUnixTimeMilliseconds();
                    }

                    if (location != null) {
                        locationList.Add(new LocationData(location.Latitude, location.Longitude, altitude, timestamp));
                    }
                }
            } catch {
                // ignored
            }
        }

        var sortedLocationList = locationList.OrderBy(ld => ld.Timestamp).ToList();

        const string csvFilePath = "gps_data.csv";

        using var writer = new StreamWriter(csvFilePath);

        const string jsonFilePath = "gps_data.geojson";

        using var geoWriter = new StreamWriter(jsonFilePath);

        writer.WriteLine("Latitude,Longitude,Altitude,Unix Timestamp");

        geoWriter.WriteLine("{\r\n\t\"type\": \"Feature\",\r\n\t\"geometry\": {\r\n        \"type\": \"LineString\",\r\n        \"coordinates\": [");

        for (var index = 0; index < sortedLocationList.Count; index++) {
            LocationData locationData = sortedLocationList[index];
            var gpsData = new List<string> {
                $"{locationData.Latitude}", $"{locationData.Longitude}", $"{locationData.Altitude}", $"{locationData.Timestamp}"
            };
            var strGpsData = string.Join(",", gpsData);
            writer.WriteLine(strGpsData);
            Console.WriteLine(strGpsData);
            geoWriter.Write("            [" + $"{locationData.Longitude}" + "," + $"{locationData.Latitude}" + "]");
            if (index < sortedLocationList.Count - 1) {
                geoWriter.WriteLine(",");
            }
        }

        geoWriter.WriteLine("]\r\n    }\r\n}");

        writer.Flush();
        writer.Close();
        geoWriter.Flush();
        geoWriter.Close();
    } catch (Exception e) {
        Console.WriteLine(e);
    }
}

public class LocationData {
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public long Timestamp { get; set; }

    public LocationData(double latitude, double longitude, double altitude, long timestamp) {
        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
        Timestamp = timestamp;
    }
}