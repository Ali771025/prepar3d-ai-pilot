using System;

namespace P3DAIPilot.Core
{
    public sealed class SafetyGuard
    {
        private readonly AppConfiguration _config;

        public SafetyGuard(AppConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public SafetyResult Evaluate(FlightPhase phase, AircraftState state, AircraftProfile aircraft, RunwayFrame runway, RunwayPosition position, DateTime utcNow)
        {
            if (state == null || !state.IsFinite()) return Disarm("Invalid or non-finite telemetry.");
            double telemetryAge = (utcNow - state.UtcTimestamp).TotalSeconds;
            if (state.UtcTimestamp == DateTime.MinValue || telemetryAge < -1.0
                || telemetryAge > _config.TelemetryTimeoutSeconds)
                return Disarm("Telemetry watchdog timeout.");
            if (state.Paused) return Disarm("Simulator is paused.");
            if (state.SlewActive) return Disarm("Slew mode is active.");
            if (!runway.IsValid()) return Disarm("Runway frame is invalid.");

            if (Math.Abs(state.BankDeg) > aircraft.MaximumBankDeg + 20.0) return Disarm("Bank angle exceeded the hard envelope.");
            if (state.PitchDeg > aircraft.MaximumPitchUpDeg + 12.0 || state.PitchDeg < aircraft.MaximumPitchDownDeg - 12.0)
                return Disarm("Pitch angle exceeded the hard envelope.");

            if (!state.OnGround && (state.StallWarning || state.IndicatedAirspeedKts < aircraft.StallSpeedLandingKts * 1.03))
                return GoAround("Stall protection activated.");

            if (state.OverspeedWarning || state.IndicatedAirspeedKts > aircraft.MaximumSafeSpeedKts)
                return GoAround("Overspeed protection activated.");

            if ((phase == FlightPhase.TakeoffRoll || phase == FlightPhase.Rollout)
                && Math.Abs(position.CrossTrackM) > Math.Max(runway.WidthM * 0.8, 30.0))
                return Disarm("Runway lateral deviation is excessive.");

            return SafetyResult.Safe();
        }

        private static SafetyResult Disarm(string reason)
        {
            return new SafetyResult { MayControl = false, MustDisarm = true, Reason = reason };
        }

        private static SafetyResult GoAround(string reason)
        {
            return new SafetyResult { MayControl = true, MustGoAround = true, Reason = reason };
        }
    }
}
