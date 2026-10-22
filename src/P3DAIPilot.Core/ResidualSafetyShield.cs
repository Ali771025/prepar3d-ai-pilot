using System;

namespace P3DAIPilot.Core
{
    public sealed class ResidualSafetyShield
    {
        private readonly RlProfile _profile;
        private ResidualAction _previousApplied = ResidualAction.Zero();

        public ResidualSafetyShield(RlProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            LastTrace = ResidualTrace.Rejected(null, "No action received.");
        }

        public ResidualTrace LastTrace { get; private set; }

        public ControlCommand Apply(
            FlightPhase phase,
            AircraftState state,
            ControlCommand baseline,
            ResidualAction requested,
            AircraftProfile aircraft,
            double dtSeconds,
            DateTime utcNow)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (!_profile.AppliesActions) return Reject(baseline, requested, "RL is disabled or in Shadow mode.");
            if (state == null || requested == null) return Reject(baseline, requested, "No state or action.");
            if ((utcNow - requested.ReceivedUtc).TotalSeconds > _profile.ActionTimeoutSeconds)
                return Reject(baseline, requested, "RL action watchdog timeout.");
            if (!IsStageAllowed(requested.Stage, phase))
                return Reject(baseline, requested, "Curriculum stage does not allow this phase.");
            if (state.OnGround || state.StallWarning || state.OverspeedWarning)
                return Reject(baseline, requested, "Hard flight-envelope gate is active.");
            if (!MathEx.AreFinite(requested.Uncertainty) || requested.Uncertainty < 0.0
                || requested.Uncertainty > _profile.MaximumPolicyUncertainty)
                return Reject(baseline, requested, "Policy uncertainty exceeds the runtime limit.");

            requested.ClampNormalized();
            double safetyScale = ComputeSafetyScale(state, aircraft);
            safetyScale *= MathEx.Clamp(1.0 - requested.Uncertainty / _profile.MaximumPolicyUncertainty, 0.0, 1.0);
            if (safetyScale <= 0.0) return Reject(baseline, requested, "No remaining soft-envelope margin.");

            var desired = new ResidualAction
            {
                Aileron = requested.Aileron * _profile.MaxResidualAileron * safetyScale,
                Elevator = requested.Elevator * _profile.MaxResidualElevator * safetyScale,
                Rudder = requested.Rudder * _profile.MaxResidualRudder * safetyScale,
                Throttle = requested.Throttle * _profile.MaxResidualThrottle * safetyScale,
                Stage = requested.Stage,
                StepId = requested.StepId,
                ResetId = requested.ResetId,
                Uncertainty = requested.Uncertainty,
                ReceivedUtc = requested.ReceivedUtc
            };

            double maxDelta = Math.Max(0.001, _profile.MaxResidualRatePerSecond * MathEx.Clamp(dtSeconds, 0.001, 0.2));
            var applied = new ResidualAction
            {
                Aileron = RateLimit(desired.Aileron, _previousApplied.Aileron, maxDelta),
                Elevator = RateLimit(desired.Elevator, _previousApplied.Elevator, maxDelta),
                Rudder = RateLimit(desired.Rudder, _previousApplied.Rudder, maxDelta),
                Throttle = RateLimit(desired.Throttle, _previousApplied.Throttle, maxDelta),
                Stage = desired.Stage,
                StepId = desired.StepId,
                ResetId = desired.ResetId,
                Uncertainty = desired.Uncertainty,
                ReceivedUtc = desired.ReceivedUtc
            };
            _previousApplied = applied;

            var result = new ControlCommand
            {
                Aileron = baseline.Aileron + applied.Aileron,
                Elevator = baseline.Elevator + applied.Elevator,
                Rudder = baseline.Rudder + applied.Rudder,
                Throttle01 = baseline.Throttle01 + applied.Throttle,
                Flaps01 = baseline.Flaps01,
                GearDown = baseline.GearDown,
                Brakes01 = baseline.Brakes01
            };
            result.Clamp();
            LastTrace = new ResidualTrace
            {
                Requested = requested,
                Applied = applied,
                SafetyScale = safetyScale,
                Uncertainty = requested.Uncertainty,
                Intervened = safetyScale < 0.999 || Math.Abs(applied.Aileron - desired.Aileron) > 0.0001
                             || Math.Abs(applied.Elevator - desired.Elevator) > 0.0001
                             || Math.Abs(applied.Rudder - desired.Rudder) > 0.0001
                             || Math.Abs(applied.Throttle - desired.Throttle) > 0.0001,
                RejectionReason = ""
            };
            return result;
        }

        public void Reset()
        {
            _previousApplied = ResidualAction.Zero();
            LastTrace = ResidualTrace.Rejected(null, "Shield reset.");
        }

        private ControlCommand Reject(ControlCommand baseline, ResidualAction requested, string reason)
        {
            _previousApplied = ResidualAction.Zero(requested == null ? "shadow" : requested.Stage);
            LastTrace = ResidualTrace.Rejected(requested, reason);
            return baseline;
        }

        private double ComputeSafetyScale(AircraftState state, AircraftProfile aircraft)
        {
            double bankMargin = aircraft.MaximumBankDeg - Math.Abs(state.BankDeg);
            double pitchMargin = Math.Min(
                aircraft.MaximumPitchUpDeg - state.PitchDeg,
                state.PitchDeg - aircraft.MaximumPitchDownDeg);
            double lowSpeedMargin = state.IndicatedAirspeedKts - aircraft.StallSpeedLandingKts * 1.08;
            double highSpeedMargin = aircraft.MaximumSafeSpeedKts - state.IndicatedAirspeedKts;
            double speedMargin = Math.Min(lowSpeedMargin, highSpeedMargin);

            double bankScale = MathEx.Clamp(bankMargin / Math.Max(1.0, _profile.EnvelopeSoftMarginBankDeg), 0.0, 1.0);
            double pitchScale = MathEx.Clamp(pitchMargin / Math.Max(1.0, _profile.EnvelopeSoftMarginPitchDeg), 0.0, 1.0);
            double speedScale = MathEx.Clamp(speedMargin / Math.Max(1.0, _profile.EnvelopeSoftMarginSpeedKts), 0.0, 1.0);
            return Math.Min(bankScale, Math.Min(pitchScale, speedScale));
        }

        private static bool IsStageAllowed(string stage, FlightPhase phase)
        {
            string normalized = (stage ?? "shadow").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "takeoff":
                    return phase == FlightPhase.InitialClimb;
                case "airwork":
                    return phase == FlightPhase.InitialClimb || phase == FlightPhase.Crosswind || phase == FlightPhase.Downwind;
                case "pattern":
                    return phase == FlightPhase.InitialClimb || phase == FlightPhase.Crosswind
                           || phase == FlightPhase.Downwind || phase == FlightPhase.Base;
                case "approach":
                    return phase == FlightPhase.Base || phase == FlightPhase.Final;
                case "full":
                    return phase == FlightPhase.InitialClimb || phase == FlightPhase.Crosswind
                           || phase == FlightPhase.Downwind || phase == FlightPhase.Base || phase == FlightPhase.Final;
                default:
                    return false;
            }
        }

        private static double RateLimit(double desired, double previous, double maxDelta)
        {
            return previous + MathEx.Clamp(desired - previous, -maxDelta, maxDelta);
        }
    }
}
