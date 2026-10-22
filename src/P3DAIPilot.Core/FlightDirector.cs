using System;

namespace P3DAIPilot.Core
{
    public sealed class FlightDirector
    {
        private readonly PatternProfile _pattern;
        private readonly ControllerProfile _controllers;

        public FlightDirector(PatternProfile pattern, ControllerProfile controllers)
        {
            _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            _controllers = controllers ?? throw new ArgumentNullException(nameof(controllers));
        }

        public GuidanceTargets GetTargets(
            FlightPhase phase,
            AircraftState state,
            RunwayFrame runway,
            RunwayPosition position,
            AircraftProfile aircraft)
        {
            double direction = (double)_pattern.ParsedDirection;
            double patternAltitudeMsl = runway.ThresholdElevationFt + _pattern.PatternAltitudeAglFt;
            var target = new GuidanceTargets
            {
                HeadingTrueDeg = runway.HeadingTrueDeg,
                AirspeedKts = aircraft.PatternSpeedKts,
                AltitudeMslFt = patternAltitudeMsl,
                VerticalSpeedFpm = 0.0,
                PitchDeg = 0.0,
                BaseThrottle01 = 0.58,
                Flaps01 = 0.0,
                GearDown = !aircraft.RetractableGear,
                VerticalMode = VerticalMode.Altitude,
                Intent = phase.ToString().ToUpperInvariant()
            };

            switch (phase)
            {
                case FlightPhase.Armed:
                    target.AirspeedKts = 0.0;
                    target.BaseThrottle01 = 0.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = 0.0;
                    target.Flaps01 = aircraft.TakeoffFlaps;
                    break;

                case FlightPhase.TakeoffRoll:
                    target.AirspeedKts = aircraft.RotateSpeedKts;
                    target.BaseThrottle01 = 1.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = state.IndicatedAirspeedKts > aircraft.RotateSpeedKts * 0.80 ? 2.0 : 0.0;
                    target.Flaps01 = aircraft.TakeoffFlaps;
                    break;

                case FlightPhase.Rotate:
                    target.AirspeedKts = aircraft.ClimbSpeedKts;
                    target.BaseThrottle01 = 1.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = aircraft.TakeoffPitchDeg;
                    target.Flaps01 = aircraft.TakeoffFlaps;
                    break;

                case FlightPhase.InitialClimb:
                    target.AirspeedKts = aircraft.ClimbSpeedKts;
                    target.BaseThrottle01 = 1.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = aircraft.ClimbPitchDeg;
                    target.Flaps01 = state.AltitudeAglFt > 300.0 ? 0.0 : aircraft.TakeoffFlaps;
                    target.GearDown = !aircraft.RetractableGear;
                    break;

                case FlightPhase.Crosswind:
                    target.HeadingTrueDeg = MathEx.NormalizeHeading(runway.HeadingTrueDeg + direction * 90.0);
                    target.AirspeedKts = aircraft.ClimbSpeedKts;
                    target.BaseThrottle01 = 0.90;
                    target.VerticalMode = VerticalMode.Altitude;
                    break;

                case FlightPhase.Downwind:
                    target.HeadingTrueDeg = MathEx.NormalizeHeading(runway.HeadingTrueDeg + 180.0);
                    target.AirspeedKts = aircraft.PatternSpeedKts;
                    target.BaseThrottle01 = 0.58;
                    target.VerticalMode = VerticalMode.Altitude;
                    break;

                case FlightPhase.Base:
                    target.HeadingTrueDeg = MathEx.NormalizeHeading(runway.HeadingTrueDeg - direction * 90.0);
                    target.AirspeedKts = Math.Max(aircraft.ApproachSpeedKts + 8.0, aircraft.StallSpeedLandingKts * 1.45);
                    target.BaseThrottle01 = 0.40;
                    target.VerticalMode = VerticalMode.VerticalSpeed;
                    target.VerticalSpeedFpm = -450.0;
                    target.Flaps01 = aircraft.BaseFlaps;
                    target.GearDown = true;
                    break;

                case FlightPhase.Final:
                    target.HeadingTrueDeg = FinalHeading(runway, position);
                    target.AirspeedKts = aircraft.ApproachSpeedKts;
                    target.BaseThrottle01 = 0.34;
                    target.VerticalMode = VerticalMode.GlidePath;
                    target.AltitudeMslFt = GlidePathAltitudeMsl(runway, position);
                    target.VerticalSpeedFpm = -target.AirspeedKts * 101.2686
                                              * Math.Tan(MathEx.DegreesToRadians(_pattern.GlideSlopeDeg));
                    target.Flaps01 = aircraft.LandingFlaps;
                    target.GearDown = true;
                    break;

                case FlightPhase.Flare:
                    target.HeadingTrueDeg = FinalHeading(runway, position);
                    target.AirspeedKts = aircraft.StallSpeedLandingKts;
                    target.BaseThrottle01 = 0.0;
                    target.VerticalMode = VerticalMode.Flare;
                    target.PitchDeg = aircraft.FlarePitchDeg;
                    target.Flaps01 = aircraft.LandingFlaps;
                    target.GearDown = true;
                    break;

                case FlightPhase.Rollout:
                    target.HeadingTrueDeg = runway.HeadingTrueDeg;
                    target.AirspeedKts = 0.0;
                    target.BaseThrottle01 = 0.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = 0.0;
                    target.Flaps01 = aircraft.LandingFlaps;
                    target.GearDown = true;
                    target.Brakes01Enabled = true;
                    target.Brakes01 = MathEx.Clamp((45.0 - state.GroundSpeedKts) / 35.0, 0.10, 0.75);
                    break;

                case FlightPhase.GoAround:
                    target.HeadingTrueDeg = runway.HeadingTrueDeg;
                    target.AirspeedKts = aircraft.ClimbSpeedKts;
                    target.BaseThrottle01 = 1.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = aircraft.ClimbPitchDeg;
                    target.Flaps01 = Math.Min(aircraft.BaseFlaps, 0.5);
                    target.GearDown = state.AltitudeAglFt < 100.0 || !aircraft.RetractableGear;
                    break;

                case FlightPhase.Completed:
                case FlightPhase.Disarmed:
                case FlightPhase.Failsafe:
                    target.AirspeedKts = 0.0;
                    target.BaseThrottle01 = 0.0;
                    target.VerticalMode = VerticalMode.Pitch;
                    target.PitchDeg = 0.0;
                    break;
            }

            return target;
        }

        public bool IsStableFinal(AircraftState state, RunwayPosition position, AircraftProfile aircraft)
        {
            return Math.Abs(position.CrossTrackM) <= _pattern.MaximumFinalCrossTrackM
                   && Math.Abs(state.IndicatedAirspeedKts - aircraft.ApproachSpeedKts) <= _pattern.MaximumFinalSpeedErrorKts
                   && state.VerticalSpeedFpm >= -_pattern.MaximumFinalSinkRateFpm
                   && Math.Abs(state.BankDeg) <= aircraft.MaximumFinalBankDeg + 3.0;
        }

        private double FinalHeading(RunwayFrame runway, RunwayPosition position)
        {
            double correction = -position.CrossTrackM * _controllers.FinalCrossTrackHeadingGain;
            return MathEx.NormalizeHeading(runway.HeadingTrueDeg + MathEx.Clamp(correction, -18.0, 18.0));
        }

        private double GlidePathAltitudeMsl(RunwayFrame runway, RunwayPosition position)
        {
            double distanceBeforeThresholdM = Math.Max(0.0, -position.AlongTrackM);
            double heightM = distanceBeforeThresholdM * Math.Tan(MathEx.DegreesToRadians(_pattern.GlideSlopeDeg));
            return runway.ThresholdElevationFt + heightM * 3.280839895;
        }
    }
}
