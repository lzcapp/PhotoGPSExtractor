using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = System.IO.Directory;

Console.Write("Folder Path: ");
var folderPath = Console.ReadLine();

if (Directory.Exists(folderPath)) {
    try {
        var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

        var locationList = new List<LocationData>();

        foreach (var file in files) {
            try {
                var directories = ImageMetadataReader.ReadMetadata(file);
                GpsDirectory? gps = directories.OfType<GpsDirectory>().FirstOrDefault();
                ExifSubIfdDirectory? exifSub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

                if (gps != null) {
                    GeoLocation? location = gps.GetGeoLocation();
                    decimal altitude = 0;
                    if (gps.ContainsTag(GpsDirectory.TagAltitude)) {
                        altitude = (decimal)gps.GetDouble(GpsDirectory.TagAltitude);
                        var altRef = gps.GetDescription(GpsDirectory.TagAltitudeRef);
                        if (altRef != null && altRef.Trim().Equals("Below sea level", StringComparison.OrdinalIgnoreCase)) {
                            altitude *= -1;
                        }
                    }
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

        writer.WriteLine("Latitude,Longitude,Altitude,Timestamp");

        foreach (var strGpsData in sortedLocationList.Select(locationData => new List<string> {
            $"{locationData.Latitude}", $"{locationData.Longitude}", $"{locationData.Altitude}", $"{locationData.Timestamp}"
        }).Select(gpsData => string.Join(",", gpsData))) {
            writer.WriteLine(strGpsData);
            Console.WriteLine("> " + strGpsData.Replace(",", "\t"));
        }

        writer.Flush();
        writer.Close();
    } catch (Exception e) {
        Console.WriteLine(e);
    }
}

public class LocationData {
    public double Latitude { get; }
    public double Longitude { get; }
    public decimal Altitude { get; }
    public long Timestamp { get; }

    public LocationData(double latitude, double longitude, decimal altitude, long timestamp) {
        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
        Timestamp = timestamp;
    }
}