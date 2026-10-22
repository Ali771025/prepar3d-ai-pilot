using System;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal sealed class ScenarioFingerprint
    {
        private const double EarthRadiusM = 6371008.8;

        public double LatitudeDeg { get; private set; }
        public double LongitudeDeg { get; private set; }
        public double HeadingTrueDeg { get; private set; }
        public double FuelFraction { get; private set; }
        public double WeightFraction { get; private set; }

        public static ScenarioFingerprint Capture(AircraftState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return new ScenarioFingerprint
            {
                LatitudeDeg = state.LatitudeDeg,
                LongitudeDeg = state.LongitudeDeg,
                HeadingTrueDeg = state.HeadingTrueDeg,
                FuelFraction = state.FuelFraction,
                WeightFraction = state.WeightFraction
            };
        }

        public bool Validate(AircraftState state, out string reason)
        {
            if (state == null || !state.IsFinite()) return Reject("Telemetry is invalid after reset.", out reason);
            if (!state.OnGround || state.GroundSpeedKts >= 5.0) return Reject("Aircraft is not settled on the ground.", out reason);
            if (DistanceM(state.LatitudeDeg, state.LongitudeDeg) > 35.0) return Reject("Reset position differs by more than 35 m.", out reason);
            if (Math.Abs(MathEx.HeadingError(HeadingTrueDeg, state.HeadingTrueDeg)) > 5.0) return Reject("Reset heading differs by more than 5 degrees.", out reason);
            if (Math.Abs(FuelFraction - state.FuelFraction) > 0.05) return Reject("Reset fuel differs by more than 5 percent.", out reason);
            if (Math.Abs(WeightFraction - state.WeightFraction) > 0.05) return Reject("Reset weight differs by more than 5 percent.", out reason);
            reason = "Scenario fingerprint validated.";
            return true;
        }

        private double DistanceM(double latitudeDeg, double longitudeDeg)
        {
            double latitude = MathEx.DegreesToRadians((LatitudeDeg + latitudeDeg) * 0.5);
            double north = MathEx.DegreesToRadians(latitudeDeg - LatitudeDeg) * EarthRadiusM;
            double east = MathEx.DegreesToRadians(longitudeDeg - LongitudeDeg) * EarthRadiusM * Math.Cos(latitude);
            return Math.Sqrt(north * north + east * east);
        }

        private static bool Reject(string message, out string reason)
        {
            reason = message;
            return false;
        }
    }
}
