using System;

namespace P3DAIPilot.Core
{
    public sealed class PilotOverrideDetector
    {
        private readonly SafetyProfile _profile;
        private DateTime _armedUtc;
        private DateTime _mismatchSinceUtc;

        public PilotOverrideDetector(SafetyProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public void Reset(DateTime utcNow)
        {
            _armedUtc = utcNow;
            _mismatchSinceUtc = DateTime.MinValue;
        }

        public bool IsOverride(AircraftState state, ControlCommand lastCommand, DateTime utcNow, out string reason)
        {
            reason = "";
            if (!_profile.ManualOverrideEnabled || state == null || lastCommand == null)
            {
                _mismatchSinceUtc = DateTime.MinValue;
                return false;
            }
            if ((utcNow - _armedUtc).TotalSeconds < _profile.ManualOverrideGraceSeconds)
            {
                _mismatchSinceUtc = DateTime.MinValue;
                return false;
            }

            double aileronDelta = Math.Abs(state.YokeX - lastCommand.Aileron);
            double elevatorDelta = Math.Abs(state.YokeY - lastCommand.Elevator);
            double rudderDelta = Math.Abs(state.RudderPedal - lastCommand.Rudder);
            bool mismatched = aileronDelta >= _profile.ManualOverrideAxisDelta
                              || elevatorDelta >= _profile.ManualOverrideAxisDelta
                              || rudderDelta >= _profile.ManualOverrideAxisDelta;
            if (!mismatched)
            {
                _mismatchSinceUtc = DateTime.MinValue;
                return false;
            }
            if (_mismatchSinceUtc == DateTime.MinValue) _mismatchSinceUtc = utcNow;
            if ((utcNow - _mismatchSinceUtc).TotalSeconds < _profile.ManualOverrideHoldSeconds) return false;

            reason = string.Format("Pilot override detected (aileron Δ={0:0.00}, elevator Δ={1:0.00}, rudder Δ={2:0.00}).",
                aileronDelta, elevatorDelta, rudderDelta);
            return true;
        }
    }
}
