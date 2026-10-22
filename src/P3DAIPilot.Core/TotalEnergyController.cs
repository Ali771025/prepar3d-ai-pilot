using System;

namespace P3DAIPilot.Core
{
    public sealed class TotalEnergyController
    {
        private const double GravityMps2 = 9.80665;
        private const double FeetToMeters = 0.3048;
        private const double KnotsToMps = 0.514444;
        private readonly ControllerProfile _profile;
        private double _totalIntegral;
        private double _balanceIntegral;

        public TotalEnergyController(ControllerProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public EnergyCommand Update(AircraftState state, GuidanceTargets target, AircraftProfile aircraft, double dtSeconds)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (aircraft == null) throw new ArgumentNullException(nameof(aircraft));
            double dt = MathEx.Clamp(dtSeconds, 0.001, 0.2);
            double altitudeErrorM = (target.AltitudeMslFt - state.AltitudeMslFt) * FeetToMeters;
            double actualSpeed = Math.Max(10.0, state.IndicatedAirspeedKts) * KnotsToMps;
            double targetSpeed = Math.Max(10.0, target.AirspeedKts) * KnotsToMps;
            double potentialError = GravityMps2 * altitudeErrorM;
            double kineticError = 0.5 * (targetSpeed * targetSpeed - actualSpeed * actualSpeed);
            double totalError = MathEx.Clamp((potentialError + kineticError) / 1000.0, -3.0, 3.0);
            double balanceError = MathEx.Clamp((potentialError - kineticError) / 1000.0, -3.0, 3.0);

            _totalIntegral = MathEx.Clamp(_totalIntegral + totalError * dt, -2.0, 2.0);
            _balanceIntegral = MathEx.Clamp(_balanceIntegral + balanceError * dt, -2.0, 2.0);
            double throttleCorrection = MathEx.Clamp(
                _profile.TecsTotalEnergyKp * totalError + _profile.TecsTotalEnergyKi * _totalIntegral,
                -_profile.TecsMaximumThrottleCorrection, _profile.TecsMaximumThrottleCorrection);
            double pitchCorrection = MathEx.Clamp(
                _profile.TecsBalanceKp * balanceError + _profile.TecsBalanceKi * _balanceIntegral,
                -_profile.TecsMaximumPitchCorrectionDeg, _profile.TecsMaximumPitchCorrectionDeg);
            double basePitch = target.VerticalMode == VerticalMode.GlidePath
                ? MathEx.Clamp(state.PitchDeg + (target.VerticalSpeedFpm - state.VerticalSpeedFpm) * 0.003,
                    aircraft.MaximumPitchDownDeg, aircraft.MaximumPitchUpDeg)
                : target.PitchDeg;
            return new EnergyCommand
            {
                PitchTargetDeg = MathEx.Clamp(basePitch + pitchCorrection,
                    aircraft.MaximumPitchDownDeg, aircraft.MaximumPitchUpDeg),
                Throttle01 = MathEx.Clamp(target.BaseThrottle01 + throttleCorrection, 0.0, 1.0),
                TotalEnergyError = totalError,
                EnergyBalanceError = balanceError
            };
        }

        public void Reset()
        {
            _totalIntegral = 0.0;
            _balanceIntegral = 0.0;
        }
    }
}
