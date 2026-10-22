using System;

namespace P3DAIPilot.Core
{
    public sealed class FlightController
    {
        private readonly ControllerProfile _profile;
        private readonly PidController _bankPid;
        private readonly PidController _pitchPid;
        private readonly PidController _throttlePid;
        private readonly TotalEnergyController _tecs;

        public FlightController(ControllerProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _bankPid = new PidController(profile.BankKp, profile.BankKi, profile.BankKd, -1.0, 1.0);
            _pitchPid = new PidController(profile.PitchKp, profile.PitchKi, profile.PitchKd, -1.0, 1.0);
            _throttlePid = new PidController(profile.SpeedThrottleKp, profile.SpeedThrottleKi, 0.0, -0.45, 0.45);
            _tecs = new TotalEnergyController(profile);
        }

        public ControlCommand Update(
            FlightPhase phase,
            AircraftState state,
            GuidanceTargets target,
            AircraftProfile aircraft,
            double dtSeconds)
        {
            var command = new ControlCommand
            {
                Flaps01 = target.Flaps01,
                GearDown = target.GearDown,
                Brakes01 = target.Brakes01Enabled ? target.Brakes01 : 0.0
            };

            double headingError = MathEx.HeadingError(target.HeadingTrueDeg, state.HeadingTrueDeg);
            if (phase == FlightPhase.TakeoffRoll || phase == FlightPhase.Rollout)
            {
                command.Aileron = 0.0;
                command.Rudder = MathEx.Clamp(headingError * _profile.GroundHeadingKp, -0.65, 0.65);
            }
            else
            {
                double bankLimit = phase == FlightPhase.Final || phase == FlightPhase.Flare
                    ? aircraft.MaximumFinalBankDeg
                    : aircraft.MaximumBankDeg;
                double desiredBank = MathEx.Clamp(headingError * _profile.HeadingToBankKp, -bankLimit, bankLimit);
                command.Aileron = _bankPid.Update(desiredBank - state.BankDeg, dtSeconds)
                                  * GainSchedule(state.IndicatedAirspeedKts);
                command.Rudder = MathEx.Clamp(-state.SideSlipDeg * 0.08, -0.25, 0.25);
            }

            bool tecsAllowed = _profile.UseTecs && phase != FlightPhase.TakeoffRoll && phase != FlightPhase.Rotate
                               && phase != FlightPhase.InitialClimb && phase != FlightPhase.Flare
                               && phase != FlightPhase.Rollout && phase != FlightPhase.GoAround;
            EnergyCommand energy = tecsAllowed ? _tecs.Update(state, target, aircraft, dtSeconds) : null;
            double targetPitch = energy == null ? ResolveTargetPitch(state, target, aircraft) : energy.PitchTargetDeg;
            command.Elevator = _pitchPid.Update(targetPitch - state.PitchDeg, dtSeconds)
                               * GainSchedule(state.IndicatedAirspeedKts);

            bool fixedFullThrottle = phase == FlightPhase.TakeoffRoll || phase == FlightPhase.Rotate
                                     || phase == FlightPhase.InitialClimb || phase == FlightPhase.GoAround;
            bool fixedIdle = phase == FlightPhase.Flare || phase == FlightPhase.Rollout;
            if (fixedFullThrottle) command.Throttle01 = 1.0;
            else if (fixedIdle) command.Throttle01 = 0.0;
            else if (energy != null) command.Throttle01 = energy.Throttle01;
            else command.Throttle01 = target.BaseThrottle01 + _throttlePid.Update(target.AirspeedKts - state.IndicatedAirspeedKts, dtSeconds);

            command.Clamp();
            return command;
        }

        public void Reset()
        {
            _bankPid.Reset();
            _pitchPid.Reset();
            _throttlePid.Reset();
            _tecs.Reset();
        }

        private double GainSchedule(double indicatedAirspeedKts)
        {
            double low = _profile.GainScheduleLowSpeedKts;
            double high = _profile.GainScheduleHighSpeedKts;
            if (low <= 0.0 || high <= low) return 1.0;
            double fraction = MathEx.Clamp((indicatedAirspeedKts - low) / (high - low), 0.0, 1.0);
            return 1.15 - 0.30 * fraction;
        }

        private static double ResolveTargetPitch(AircraftState state, GuidanceTargets target, AircraftProfile aircraft)
        {
            double pitch;
            switch (target.VerticalMode)
            {
                case VerticalMode.Pitch:
                case VerticalMode.Flare:
                    pitch = target.PitchDeg;
                    if (target.VerticalMode == VerticalMode.Pitch && target.AirspeedKts > 1.0)
                    {
                        pitch += (state.IndicatedAirspeedKts - target.AirspeedKts) * 0.08;
                    }
                    break;

                case VerticalMode.VerticalSpeed:
                    pitch = state.PitchDeg + (target.VerticalSpeedFpm - state.VerticalSpeedFpm) * 0.004;
                    break;

                case VerticalMode.GlidePath:
                    double nominalVs = target.VerticalSpeedFpm < -1.0
                        ? target.VerticalSpeedFpm
                        : -target.AirspeedKts * 101.2686 * Math.Tan(MathEx.DegreesToRadians(3.0));
                    double altitudeError = target.AltitudeMslFt - state.AltitudeMslFt;
                    double desiredVs = MathEx.Clamp(nominalVs + altitudeError * 2.0, -850.0, 250.0);
                    pitch = state.PitchDeg + (desiredVs - state.VerticalSpeedFpm) * 0.004;
                    break;

                default:
                    double errorFt = target.AltitudeMslFt - state.AltitudeMslFt;
                    double requestedVs = MathEx.Clamp(errorFt * 1.8, -650.0, 650.0);
                    pitch = state.PitchDeg + (requestedVs - state.VerticalSpeedFpm) * 0.004;
                    break;
            }

            return MathEx.Clamp(pitch, aircraft.MaximumPitchDownDeg, aircraft.MaximumPitchUpDeg);
        }
    }
}
