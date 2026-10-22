using System;

namespace P3DAIPilot.Core
{
    public sealed class ControlCommandLimiter
    {
        private readonly SafetyProfile _profile;
        private ControlCommand _previous = ControlCommand.Neutral();
        private bool _initialized;

        public ControlCommandLimiter(SafetyProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public void Reset(ControlCommand initial)
        {
            _previous = (initial ?? ControlCommand.Neutral()).Clone();
            _previous.Clamp();
            _initialized = true;
        }

        public ControlCommand Apply(ControlCommand desired, double dtSeconds)
        {
            if (desired == null) throw new ArgumentNullException(nameof(desired));
            if (!desired.IsFinite()) throw new ArgumentException("Desired command contains a non-finite value.", nameof(desired));
            desired = desired.Clone();
            desired.Clamp();
            if (!_profile.CommandRateLimitEnabled)
            {
                _previous = desired.Clone();
                _initialized = true;
                return desired;
            }
            if (!_initialized) Reset(desired);
            double dt = MathEx.Clamp(dtSeconds, 0.001, 0.2);
            double axisDelta = _profile.MaximumPrimaryAxisRatePerSecond * dt;
            double throttleDelta = _profile.MaximumThrottleRatePerSecond * dt;
            double brakeDelta = _profile.MaximumBrakeRatePerSecond * dt;
            var limited = new ControlCommand
            {
                Aileron = Rate(desired.Aileron, _previous.Aileron, axisDelta),
                Elevator = Rate(desired.Elevator, _previous.Elevator, axisDelta),
                Rudder = Rate(desired.Rudder, _previous.Rudder, axisDelta),
                Throttle01 = Rate(desired.Throttle01, _previous.Throttle01, throttleDelta),
                Flaps01 = desired.Flaps01,
                GearDown = desired.GearDown,
                Brakes01 = Rate(desired.Brakes01, _previous.Brakes01, brakeDelta)
            };
            limited.Clamp();
            _previous = limited.Clone();
            return limited;
        }

        private static double Rate(double desired, double previous, double maximumDelta)
        {
            return previous + MathEx.Clamp(desired - previous, -maximumDelta, maximumDelta);
        }
    }
}
