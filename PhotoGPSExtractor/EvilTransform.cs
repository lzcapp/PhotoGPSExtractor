//
// Copyright (C) 1000 - 9999 Somebody Anonymous
// NO WARRANTY OR GUARANTEE
//

namespace PhotoGPSExtractor {
    internal class EvilTransform {
        const double Pi = 3.14159265358979324;

        //
        // Krasovsky 1940
        //
        // a = 6378245.0, 1/f = 298.3
        // b = a * (1 - f)
        // ee = (a^2 - b^2) / a^2;
        const double A = 6378245.0;
        const double Ee = 0.00669342162296594323;

        //
        // World Geodetic System ==> Mars Geodetic System
        public static void Transform(double wgLat, double wgLon, out double mgLat, out double mgLon) {
            if (OutOfChina(wgLat, wgLon)) {
                mgLat = wgLat;
                mgLon = wgLon;
                return;
            }
            double dLat = TransformLat(wgLon - 105.0, wgLat - 35.0);
            double dLon = TransformLon(wgLon - 105.0, wgLat - 35.0);
            double radLat = wgLat / 180.0 * Pi;
            double magic = Math.Sin(radLat);
            magic = 1 - Ee * magic * magic;
            double sqrtMagic = Math.Sqrt(magic);
            dLat = (dLat * 180.0) / ((A * (1 - Ee)) / (magic * sqrtMagic) * Pi);
            dLon = (dLon * 180.0) / (A / sqrtMagic * Math.Cos(radLat) * Pi);
            mgLat = wgLat + dLat;
            mgLon = wgLon + dLon;
        }

        static bool OutOfChina(double lat, double lon) {
            if (lon < 72.004 || lon > 137.8347)
                return true;
            if (lat < 0.8293 || lat > 55.8271)
                return true;
            return false;
        }

        static double TransformLat(double x, double y) {
            double ret = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
            ret += (20.0 * Math.Sin(6.0 * x * Pi) + 20.0 * Math.Sin(2.0 * x * Pi)) * 2.0 / 3.0;
            ret += (20.0 * Math.Sin(y * Pi) + 40.0 * Math.Sin(y / 3.0 * Pi)) * 2.0 / 3.0;
            ret += (160.0 * Math.Sin(y / 12.0 * Pi) + 320 * Math.Sin(y * Pi / 30.0)) * 2.0 / 3.0;
            return ret;
        }

        static double TransformLon(double x, double y) {
            double ret = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
            ret += (20.0 * Math.Sin(6.0 * x * Pi) + 20.0 * Math.Sin(2.0 * x * Pi)) * 2.0 / 3.0;
            ret += (20.0 * Math.Sin(x * Pi) + 40.0 * Math.Sin(x / 3.0 * Pi)) * 2.0 / 3.0;
            ret += (150.0 * Math.Sin(x / 12.0 * Pi) + 300.0 * Math.Sin(x / 30.0 * Pi)) * 2.0 / 3.0;
            return ret;
        }
    }
}