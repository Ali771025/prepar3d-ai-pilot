using System;

namespace P3DAIPilot.Core
{
    public sealed class AutopilotEngine
    {
        private readonly AppConfiguration _config;
        private readonly FlightDirector _director;
        private readonly FlightController _controller;
        private readonly SafetyGuard _safety;
        private readonly PilotOverrideDetector _pilotOverride;
        private readonly ControlCommandLimiter _commandLimiter;
        private AircraftProfile _aircraft;
        private RunwayFrame _runway;
        private ControlCommand _lastCommand;
        private bool _autoTuned;
        private DateTime _missionStartedUtc, _stableClimbUtc;
        private string _transitionReason = "Not armed";

        public AutopilotEngine(AppConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _aircraft = config.Aircraft.Clone();
            _director = new FlightDirector(config.Pattern, config.Controllers);
            _controller = new FlightController(config.Controllers);
            _safety = new SafetyGuard(config);
            _pilotOverride = new PilotOverrideDetector(config.Safety);
            _commandLimiter = new ControlCommandLimiter(config.Safety);
            _lastCommand = ControlCommand.Neutral();
            Phase = FlightPhase.Disarmed;
        }

        public FlightPhase Phase { get; private set; }
        public Func<FlightPhase, AircraftState, GuidanceTargets, GuidanceTargets> GuidanceOverride { get; set; }
        public Func<FlightPhase, AircraftState, GuidanceTargets, ControlCommand, AircraftProfile, double, ControlCommand> ControlOverride { get; set; }
        public bool IsArmed { get { return Phase != FlightPhase.Disarmed && Phase != FlightPhase.Completed && Phase != FlightPhase.Failsafe; } }
        public AircraftProfile ActiveAircraftProfile { get { return _aircraft.Clone(); } }
        public RunwayFrame Runway { get { return _runway; } }

        public bool Arm(AircraftState state, RunwayFrame runway, out string reason)
        {
            if (state == null || !state.IsFinite()) return Reject("Telemetry is unavailable.", out reason);
            double telemetryAge = (DateTime.UtcNow - state.UtcTimestamp).TotalSeconds;
            if (state.UtcTimestamp == DateTime.MinValue || telemetryAge < -1.0
                || telemetryAge > _config.TelemetryTimeoutSeconds)
                return Reject("Telemetry is stale.", out reason);
            if (runway == null || !runway.IsValid()) return Reject("Capture or configure a valid runway first.", out reason);
            if (!state.OnGround) return Reject("Aircraft must be on the ground before arming.", out reason);
            if (state.Paused || state.SlewActive) return Reject("Exit pause/slew mode before arming.", out reason);
            if (state.GroundSpeedKts > 5.0) return Reject("Aircraft must be nearly stopped before arming.", out reason);
            if (state.ParkingBrakeSet) return Reject("Release the parking brake before arming.", out reason);
            if (Math.Abs(MathEx.HeadingError(runway.HeadingTrueDeg, state.HeadingTrueDeg)) > 12.0)
                return Reject("Aircraft is not aligned with the captured runway.", out reason);
            if (_config.Safety.ManualOverrideEnabled
                && (Math.Abs(state.YokeX) > 0.20 || Math.Abs(state.YokeY) > 0.20 || Math.Abs(state.RudderPedal) > 0.20))
                return Reject("Center the yoke and rudder before arming.", out reason);

            _runway = runway;
            _aircraft = _config.Aircraft.Clone();
            _autoTuned = false;
            if (_config.AutoTuneFromAircraftDesignSpeeds)
            {
                _aircraft = _config.Aircraft.AutoTuned(state);
                _autoTuned = (state.DesignVs0Kts >= 25.0 && state.DesignVs0Kts <= 150.0)
                             || (state.DesignVs1Kts >= 25.0 && state.DesignVs1Kts <= 150.0)
                             || (state.EstimatedCruiseKts >= 60.0 && state.EstimatedCruiseKts <= 350.0);
            }

            _controller.Reset();
            _missionStartedUtc = DateTime.UtcNow;
            _stableClimbUtc = DateTime.MinValue;
            _lastCommand = new ControlCommand
            {
                Aileron = state.YokeX,
                Elevator = state.YokeY,
                Rudder = state.RudderPedal,
                Throttle01 = state.Throttle01,
                Flaps01 = state.Flaps01,
                GearDown = !_aircraft.RetractableGear
            };
            _commandLimiter.Reset(_lastCommand);
            _pilotOverride.Reset(DateTime.UtcNow);
            SetPhase(FlightPhase.Armed, "Arming checks passed.");
            reason = _autoTuned
                ? string.Format("Armed. Speeds: Vr {0:0.0}, Vy {1:0.0}, Vapp {2:0.0} kt.", _aircraft.RotateSpeedKts, _aircraft.ClimbSpeedKts, _aircraft.ApproachSpeedKts)
                : "Armed with configured aircraft speeds.";
            return true;
        }

        public void Disarm(string reason)
        {
            _controller.Reset();
            _pilotOverride.Reset(DateTime.UtcNow);
            SetPhase(FlightPhase.Disarmed, string.IsNullOrWhiteSpace(reason) ? "Manual disarm." : reason);
        }

        public void ApplySupervisorIntent(string intent)
        {
            string normalized = (intent ?? "").Trim().ToUpperInvariant();
            if (normalized == "RETURN_CONTROL_TO_PILOT")
            {
                Disarm("Supervisor requested return of control to pilot.");
            }
            else if (normalized == "GO_AROUND"
                     && (Phase == FlightPhase.Base || Phase == FlightPhase.Final || Phase == FlightPhase.Flare))
            {
                SetPhase(FlightPhase.GoAround, "Validated supervisor go-around intent.");
            }
        }

        public AutopilotOutput Update(AircraftState state, double dtSeconds, DateTime utcNow)
        {
            if (_runway == null || state == null)
            {
                return InactiveOutput(state, "Runway or telemetry unavailable.");
            }

            var position = MathEx.ToRunwayFrame(state, _runway);
            if (Phase == FlightPhase.Disarmed || Phase == FlightPhase.Completed || Phase == FlightPhase.Failsafe)
            {
                return InactiveOutput(state, _transitionReason, position);
            }

            string pilotOverrideReason;
            if (_pilotOverride.IsOverride(state, _lastCommand, utcNow, out pilotOverrideReason))
            {
                Disarm(pilotOverrideReason);
                return InactiveOutput(state, pilotOverrideReason, position);
            }

            var safety = _safety.Evaluate(Phase, state, _aircraft, _runway, position, utcNow);
            if (safety.MustDisarm)
            {
                SetPhase(FlightPhase.Failsafe, safety.Reason);
                return new AutopilotOutput
                {
                    Phase = Phase,
                    Command = ControlCommand.Neutral(),
                    RunwayPosition = position,
                    Safety = safety,
                    TransitionReason = _transitionReason,
                    ApplyControls = false
                };
            }

            if (safety.MustGoAround && Phase != FlightPhase.GoAround)
            {
                SetPhase(FlightPhase.GoAround, safety.Reason);
            }

            AdvanceState(state, position);
            if (_config.TakeoffMission != null && _config.TakeoffMission.Enabled)
            {
                var mission = _config.TakeoffMission;
                if ((utcNow - _missionStartedUtc).TotalSeconds > mission.MaximumSeconds)
                    SetPhase(FlightPhase.Failsafe, "Takeoff mission timed out.");
                bool stable = Phase == FlightPhase.InitialClimb && !state.OnGround
                    && state.AltitudeAglFt >= mission.CompletionAglFt
                    && state.VerticalSpeedFpm >= mission.MinimumClimbFpm
                    && Math.Abs(state.BankDeg) <= 15 && !state.StallWarning && !state.OverspeedWarning
                    && state.IndicatedAirspeedKts >= _aircraft.StallSpeedCleanKts * 1.2;
                if (!stable) _stableClimbUtc = DateTime.MinValue;
                else if (_stableClimbUtc == DateTime.MinValue) _stableClimbUtc = utcNow;
                else if ((utcNow - _stableClimbUtc).TotalSeconds >= mission.StableClimbSeconds)
                    SetPhase(FlightPhase.Completed, "Takeoff mission completed: stable positive climb.");
                if (Phase == FlightPhase.Completed || Phase == FlightPhase.Failsafe)
                    return InactiveOutput(state, _transitionReason, position);
            }
            GuidanceTargets targets = _director.GetTargets(Phase, state, _runway, position, _aircraft);
            if (GuidanceOverride != null)
            {
                try
                {
                    GuidanceTargets overridden = GuidanceOverride(Phase, state, targets.Clone());
                    if (overridden != null) targets = overridden;
                }
                catch
                {
                    // An optional supervisor must never break the deterministic controller.
                }
            }
            ControlCommand command = _controller.Update(Phase, state, targets, _aircraft, dtSeconds);
            if (ControlOverride != null)
            {
                try
                {
                    ControlCommand overridden = ControlOverride(Phase, state, targets, command, _aircraft, dtSeconds);
                    if (overridden != null) command = overridden;
                }
                catch
                {
                    // Residual learning is optional; deterministic control remains authoritative.
                }
            }

            bool emergencyEnvelopeRecovery = state.StallWarning || state.OverspeedWarning;
            if (state.StallWarning)
            {
                command.Throttle01 = 1.0;
                command.Elevator = Math.Min(command.Elevator, -0.12);
                command.Flaps01 = Math.Min(command.Flaps01, 0.5);
            }
            else if (state.OverspeedWarning)
            {
                command.Throttle01 = 0.0;
                command.Elevator = Math.Max(command.Elevator, 0.08);
            }

            command.Clamp();
            command = _commandLimiter.Apply(command, dtSeconds);
            // Envelope recovery must not be delayed by the normal command-slew limiter.
            if (emergencyEnvelopeRecovery)
            {
                if (state.StallWarning)
                {
                    command.Throttle01 = 1.0;
                    command.Elevator = Math.Min(command.Elevator, -0.12);
                    command.Flaps01 = Math.Min(command.Flaps01, 0.5);
                }
                else
                {
                    command.Throttle01 = 0.0;
                    command.Elevator = Math.Max(command.Elevator, 0.08);
                }
                command.Clamp();
                _commandLimiter.Reset(command);
            }
            _lastCommand = command.Clone();
            return new AutopilotOutput
            {
                Phase = Phase,
                Targets = targets,
                Command = command,
                RunwayPosition = position,
                Safety = safety,
                TransitionReason = _transitionReason,
                ApplyControls = safety.MayControl && IsArmed
            };
        }

        private void AdvanceState(AircraftState state, RunwayPosition position)
        {
            double direction = (double)_config.Pattern.ParsedDirection;
            switch (Phase)
            {
                case FlightPhase.Armed:
                    SetPhase(FlightPhase.TakeoffRoll, "Takeoff command started.");
                    break;
                case FlightPhase.TakeoffRoll:
                    if (state.IndicatedAirspeedKts >= _aircraft.RotateSpeedKts)
                        SetPhase(FlightPhase.Rotate, "Rotate speed reached.");
                    break;
                case FlightPhase.Rotate:
                    if (!state.OnGround && state.AltitudeAglFt >= 35.0)
                        SetPhase(FlightPhase.InitialClimb, "Positive climb established.");
                    break;
                case FlightPhase.InitialClimb:
                    if (!(_config.TakeoffMission != null && _config.TakeoffMission.Enabled)
                        && state.AltitudeAglFt >= _config.Pattern.CrosswindTurnAltitudeAglFt)
                        SetPhase(FlightPhase.Crosswind, "Crosswind turn altitude reached.");
                    break;
                case FlightPhase.Crosswind:
                    double offsetM = _config.Pattern.DownwindOffsetNm * MathEx.MetersPerNauticalMile;
                    if (direction * position.CrossTrackM >= offsetM)
                        SetPhase(FlightPhase.Downwind, "Configured downwind offset reached.");
                    break;
                case FlightPhase.Downwind:
                    double basePastM = _config.Pattern.BaseTurnPastThresholdNm * MathEx.MetersPerNauticalMile;
                    if (position.AlongTrackM <= -basePastM)
                        SetPhase(FlightPhase.Base, "Base turn point reached.");
                    break;
                case FlightPhase.Base:
                    double leadM = _config.Pattern.FinalTurnLeadNm * MathEx.MetersPerNauticalMile;
                    if (direction * position.CrossTrackM <= leadM)
                        SetPhase(FlightPhase.Final, "Final turn lead reached.");
                    break;
                case FlightPhase.Final:
                    if (state.AltitudeAglFt <= _config.Pattern.StableApproachMinimumAglFt
                        && state.AltitudeAglFt > _config.Pattern.GoAroundMinimumAglFt
                        && !_director.IsStableFinal(state, position, _aircraft))
                    {
                        SetPhase(FlightPhase.GoAround, "Unstable approach.");
                    }
                    else if (state.AltitudeAglFt <= _config.Pattern.FlareHeightAglFt)
                    {
                        SetPhase(FlightPhase.Flare, "Flare height reached.");
                    }
                    break;
                case FlightPhase.Flare:
                    if (state.OnGround)
                        SetPhase(FlightPhase.Rollout, "Touchdown detected.");
                    break;
                case FlightPhase.Rollout:
                    if (state.GroundSpeedKts <= 4.0)
                        SetPhase(FlightPhase.Completed, "Aircraft stopped on runway.");
                    break;
                case FlightPhase.GoAround:
                    if (state.AltitudeAglFt >= _config.Pattern.CrosswindTurnAltitudeAglFt
                        && state.VerticalSpeedFpm > 100.0)
                        SetPhase(FlightPhase.Crosswind, "Go-around climb established.");
                    break;
            }
        }

        private AutopilotOutput InactiveOutput(AircraftState state, string reason, RunwayPosition position = null)
        {
            return new AutopilotOutput
            {
                Phase = Phase,
                Command = ControlCommand.Neutral(),
                RunwayPosition = position,
                Safety = new SafetyResult { MayControl = false, Reason = reason },
                TransitionReason = reason,
                ApplyControls = false
            };
        }

        private void SetPhase(FlightPhase phase, string reason)
        {
            if (Phase != phase) _controller.Reset();
            Phase = phase;
            _transitionReason = reason;
        }

        private static bool Reject(string message, out string reason)
        {
            reason = message;
            return false;
        }
    }
}
