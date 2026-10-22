using System;

namespace P3DAIPilot.Core
{
    public static class MathEx
    {
        private const double EarthRadiusM = 6371008.8;
        public const double MetersPerNauticalMile = 1852.0;

        public static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        public static bool AreFinite(params double[] values)
        {
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            }

            return true;
        }

        public static double NormalizeHeading(double degrees)
        {
            double value = degrees % 360.0;
            return value < 0.0 ? value + 360.0 : value;
        }

        public static double HeadingError(double targetDeg, double actualDeg)
        {
            double error = NormalizeHeading(targetDeg) - NormalizeHeading(actualDeg);
            if (error > 180.0) error -= 360.0;
            if (error < -180.0) error += 360.0;
            return error;
        }

        public static RunwayPosition ToRunwayFrame(AircraftState state, RunwayFrame runway)
        {
            double lat0 = DegreesToRadians(runway.ThresholdLatitudeDeg);
            double dLat = DegreesToRadians(state.LatitudeDeg - runway.ThresholdLatitudeDeg);
            double dLon = DegreesToRadians(state.LongitudeDeg - runway.ThresholdLongitudeDeg);
            double northM = dLat * EarthRadiusM;
            double eastM = dLon * EarthRadiusM * Math.Cos(lat0);

            double heading = DegreesToRadians(runway.HeadingTrueDeg);
            double along = northM * Math.Cos(heading) + eastM * Math.Sin(heading);
            double cross = eastM * Math.Cos(heading) - northM * Math.Sin(heading);
            return new RunwayPosition
            {
                AlongTrackM = along,
                CrossTrackM = cross,
                DistanceToThresholdM = Math.Sqrt(along * along + cross * cross)
            };
        }

        public static double BearingTo(double fromLatDeg, double fromLonDeg, double toLatDeg, double toLonDeg)
        {
            double lat1 = DegreesToRadians(fromLatDeg);
            double lat2 = DegreesToRadians(toLatDeg);
            double dLon = DegreesToRadians(toLonDeg - fromLonDeg);
            double y = Math.Sin(dLon) * Math.Cos(lat2);
            double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
            return NormalizeHeading(RadiansToDegrees(Math.Atan2(y, x)));
        }

        public static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        public static double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }
    }
}
