using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace P3DAIPilot.Core
{
    [DataContract]
    public sealed class AppConfiguration
    {
        [DataMember] public int ControlLoopHz { get; set; }
        [DataMember] public double TelemetryTimeoutSeconds { get; set; }
        [DataMember] public bool AutoTuneFromAircraftDesignSpeeds { get; set; }
        [DataMember] public bool RecordOnStart { get; set; }
        [DataMember] public string LogDirectory { get; set; }
        [DataMember] public SimulatorProfile Simulator { get; set; }
        [DataMember] public AircraftProfileSelection AircraftProfiles { get; set; }
        [DataMember] public ObservabilityProfile Observability { get; set; }
        [DataMember] public AircraftProfile Aircraft { get; set; }
        [DataMember] public PatternProfile Pattern { get; set; }
        [DataMember] public ControllerProfile Controllers { get; set; }
        [DataMember] public SafetyProfile Safety { get; set; }
        [DataMember] public LlmProfile Llm { get; set; }
        [DataMember] public RlProfile Rl { get; set; }
        [DataMember] public TakeoffMissionProfile TakeoffMission { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (ControlLoopHz < 10 || ControlLoopHz > 100) errors.Add("ControlLoopHz must be between 10 and 100.");
            if (!MathEx.AreFinite(TelemetryTimeoutSeconds) || TelemetryTimeoutSeconds < 0.2 || TelemetryTimeoutSeconds > 10.0)
                errors.Add("TelemetryTimeoutSeconds is outside 0.2..10.");
            if (Aircraft == null) errors.Add("Aircraft configuration is missing.");
            else errors.AddRange(Aircraft.Validate());
            if (Pattern == null) errors.Add("Pattern configuration is missing.");
            else errors.AddRange(Pattern.Validate());
            if (Controllers == null) errors.Add("Controllers configuration is missing.");
            else errors.AddRange(Controllers.Validate());
            if (Safety == null) errors.Add("Safety configuration is missing.");
            else errors.AddRange(Safety.Validate());
            if (Llm == null) errors.Add("Llm configuration is missing.");
            else errors.AddRange(Llm.Validate());
            if (Rl == null) errors.Add("Rl configuration is missing.");
            else errors.AddRange(Rl.Validate());
            if (Simulator == null) errors.Add("Simulator configuration is missing.");
            else errors.AddRange(Simulator.Validate());
            if (AircraftProfiles != null) errors.AddRange(AircraftProfiles.Validate());
            if (Observability == null) errors.Add("Observability configuration is missing.");
            else errors.AddRange(Observability.Validate());
            if (TakeoffMission != null) errors.AddRange(TakeoffMission.Validate());
            return errors;
        }
    }

    [DataContract]
    public sealed class TakeoffMissionProfile
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public double CompletionAglFt { get; set; }
        [DataMember] public double StableClimbSeconds { get; set; }
        [DataMember] public double MinimumClimbFpm { get; set; }
        [DataMember] public double MaximumSeconds { get; set; }
        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!Enabled) return errors;
            if (!MathEx.AreFinite(CompletionAglFt, StableClimbSeconds, MinimumClimbFpm, MaximumSeconds)
                || CompletionAglFt < 300 || CompletionAglFt > 3000 || StableClimbSeconds < 1
                || StableClimbSeconds > 15 || MinimumClimbFpm < 100 || MinimumClimbFpm > 5000
                || MaximumSeconds < 20 || MaximumSeconds > 600)
                errors.Add("TakeoffMission limits are invalid.");
            return errors;
        }
    }

    [DataContract]
    public sealed class AircraftProfileSelection
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public string Directory { get; set; }
        [DataMember] public string DefaultProfileId { get; set; }
        [DataMember] public bool RequireMatch { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!Enabled) return errors;
            if (string.IsNullOrWhiteSpace(Directory))
                errors.Add("AircraftProfiles.Directory is required when aircraft profile selection is enabled.");
            if (string.IsNullOrWhiteSpace(DefaultProfileId))
                errors.Add("AircraftProfiles.DefaultProfileId is required when aircraft profile selection is enabled.");
            return errors;
        }
    }

    [DataContract]
    public sealed class ObservabilityProfile
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public double HealthLogIntervalSeconds { get; set; }
        [DataMember] public double ConsoleDashboardIntervalSeconds { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(HealthLogIntervalSeconds, ConsoleDashboardIntervalSeconds))
                errors.Add("Observability intervals must be finite.");
            else if (HealthLogIntervalSeconds < 0.2 || HealthLogIntervalSeconds > 60.0)
                errors.Add("Observability.HealthLogIntervalSeconds must be 0.2..60.");
            if (MathEx.AreFinite(ConsoleDashboardIntervalSeconds)
                && (ConsoleDashboardIntervalSeconds < 0.2 || ConsoleDashboardIntervalSeconds > 60.0))
                errors.Add("Observability.ConsoleDashboardIntervalSeconds must be 0.2..60.");
            return errors;
        }
    }

    [DataContract]
    public sealed class SimulatorProfile
    {
        [DataMember] public string Mode { get; set; }
        [DataMember] public string PreferredTransport { get; set; }
        [DataMember] public bool EnableParallelTelemetry { get; set; }
        [DataMember] public bool CommandFailoverEnabled { get; set; }
        [DataMember] public double FirstTelemetryTimeoutSeconds { get; set; }
        [DataMember] public double TelemetryStaleSeconds { get; set; }
        [DataMember] public double FailbackStableSeconds { get; set; }
        [DataMember] public double ReconnectIntervalSeconds { get; set; }
        [DataMember] public double StaleConnectionRecycleSeconds { get; set; }
        [DataMember] public double MaximumAltitudeDivergenceFt { get; set; }
        [DataMember] public double MaximumAirspeedDivergenceKts { get; set; }
        [DataMember] public double MaximumHeadingDivergenceDeg { get; set; }
        [DataMember] public bool DisarmOnTelemetryDivergence { get; set; }
        [DataMember] public double TelemetryDivergenceHoldSeconds { get; set; }
        [DataMember] public FsuipcProfile Fsuipc { get; set; }

        public bool Allows(string transportName)
        {
            if (string.Equals(Mode, "SimConnectOnly", StringComparison.OrdinalIgnoreCase))
                return string.Equals(transportName, "SimConnect", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(Mode, "FsuipcOnly", StringComparison.OrdinalIgnoreCase))
                return string.Equals(transportName, "FSUIPC", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(FirstTelemetryTimeoutSeconds, TelemetryStaleSeconds, FailbackStableSeconds,
                    ReconnectIntervalSeconds, StaleConnectionRecycleSeconds, MaximumAltitudeDivergenceFt,
                    MaximumAirspeedDivergenceKts, MaximumHeadingDivergenceDeg, TelemetryDivergenceHoldSeconds))
                errors.Add("Simulator timing and divergence values must be finite.");
            bool validMode = string.Equals(Mode, "AutoFailover", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Mode, "Redundant", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Mode, "SimConnectOnly", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Mode, "FsuipcOnly", StringComparison.OrdinalIgnoreCase);
            if (!validMode) errors.Add("Simulator.Mode must be AutoFailover, Redundant, SimConnectOnly, or FsuipcOnly.");
            if ((string.Equals(Mode, "AutoFailover", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(Mode, "Redundant", StringComparison.OrdinalIgnoreCase)) && !EnableParallelTelemetry)
                errors.Add("Simulator.EnableParallelTelemetry must be true for dual-transport modes.");
            if (!string.Equals(PreferredTransport, "SimConnect", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(PreferredTransport, "FSUIPC", StringComparison.OrdinalIgnoreCase))
                errors.Add("Simulator.PreferredTransport must be SimConnect or FSUIPC.");
            if (TelemetryStaleSeconds < 0.25 || TelemetryStaleSeconds > 10.0)
                errors.Add("Simulator.TelemetryStaleSeconds must be 0.25..10.");
            if (FirstTelemetryTimeoutSeconds < 3.0 || FirstTelemetryTimeoutSeconds > 120.0)
                errors.Add("Simulator.FirstTelemetryTimeoutSeconds must be 3..120.");
            if (FailbackStableSeconds < 1.0 || FailbackStableSeconds > 120.0)
                errors.Add("Simulator.FailbackStableSeconds must be 1..120.");
            if (ReconnectIntervalSeconds < 1.0 || ReconnectIntervalSeconds > 120.0)
                errors.Add("Simulator.ReconnectIntervalSeconds must be 1..120.");
            if (StaleConnectionRecycleSeconds <= TelemetryStaleSeconds || StaleConnectionRecycleSeconds > 120.0)
                errors.Add("Simulator.StaleConnectionRecycleSeconds must exceed TelemetryStaleSeconds and be at most 120.");
            if (MaximumAltitudeDivergenceFt <= 0.0 || MaximumAirspeedDivergenceKts <= 0.0
                || MaximumHeadingDivergenceDeg <= 0.0)
                errors.Add("Simulator divergence limits must be positive.");
            if (TelemetryDivergenceHoldSeconds < 0.2 || TelemetryDivergenceHoldSeconds > 30.0)
                errors.Add("Simulator.TelemetryDivergenceHoldSeconds must be 0.2..30.");
            if (Fsuipc == null) errors.Add("Simulator.Fsuipc configuration is missing.");
            else errors.AddRange(Fsuipc.Validate());
            if (string.Equals(Mode, "FsuipcOnly", StringComparison.OrdinalIgnoreCase) && Fsuipc != null && !Fsuipc.Enabled)
                errors.Add("Simulator.Fsuipc.Enabled must be true in FsuipcOnly mode.");
            return errors;
        }
    }

    [DataContract]
    public sealed class FsuipcProfile
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public int PollHz { get; set; }
        [DataMember] public string OffsetGroup { get; set; }
        [DataMember] public bool RequireP3Dx64 { get; set; }
        [DataMember] public bool RefreshPayloadServices { get; set; }
        [DataMember] public double PayloadRefreshSeconds { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (PollHz < 5 || PollHz > 60) errors.Add("Simulator.Fsuipc.PollHz must be 5..60.");
            if (string.IsNullOrWhiteSpace(OffsetGroup)) errors.Add("Simulator.Fsuipc.OffsetGroup is required.");
            if (!MathEx.AreFinite(PayloadRefreshSeconds) || PayloadRefreshSeconds < 0.5 || PayloadRefreshSeconds > 60.0)
                errors.Add("Simulator.Fsuipc.PayloadRefreshSeconds must be 0.5..60.");
            return errors;
        }
    }

    [DataContract]
    public sealed class AircraftProfile
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public double RotateSpeedKts { get; set; }
        [DataMember] public double ClimbSpeedKts { get; set; }
        [DataMember] public double PatternSpeedKts { get; set; }
        [DataMember] public double ApproachSpeedKts { get; set; }
        [DataMember] public double StallSpeedCleanKts { get; set; }
        [DataMember] public double StallSpeedLandingKts { get; set; }
        [DataMember] public double MaximumSafeSpeedKts { get; set; }
        [DataMember] public double TakeoffPitchDeg { get; set; }
        [DataMember] public double ClimbPitchDeg { get; set; }
        [DataMember] public double FlarePitchDeg { get; set; }
        [DataMember] public double MaximumPitchUpDeg { get; set; }
        [DataMember] public double MaximumPitchDownDeg { get; set; }
        [DataMember] public double MaximumBankDeg { get; set; }
        [DataMember] public double MaximumFinalBankDeg { get; set; }
        [DataMember] public double TakeoffFlaps { get; set; }
        [DataMember] public double BaseFlaps { get; set; }
        [DataMember] public double LandingFlaps { get; set; }
        [DataMember] public bool RetractableGear { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(RotateSpeedKts, ClimbSpeedKts, PatternSpeedKts, ApproachSpeedKts,
                    StallSpeedCleanKts, StallSpeedLandingKts, MaximumSafeSpeedKts, TakeoffPitchDeg,
                    ClimbPitchDeg, FlarePitchDeg, MaximumPitchUpDeg, MaximumPitchDownDeg,
                    MaximumBankDeg, MaximumFinalBankDeg, TakeoffFlaps, BaseFlaps, LandingFlaps))
                errors.Add("Aircraft numeric values must be finite.");
            if (string.IsNullOrWhiteSpace(Name)) errors.Add("Aircraft.Name is required.");
            if (RotateSpeedKts <= StallSpeedCleanKts) errors.Add("RotateSpeedKts must exceed StallSpeedCleanKts.");
            if (ClimbSpeedKts <= StallSpeedCleanKts) errors.Add("ClimbSpeedKts must exceed StallSpeedCleanKts.");
            if (ApproachSpeedKts <= StallSpeedLandingKts) errors.Add("ApproachSpeedKts must exceed StallSpeedLandingKts.");
            if (PatternSpeedKts <= ApproachSpeedKts) errors.Add("PatternSpeedKts must exceed ApproachSpeedKts.");
            if (MaximumSafeSpeedKts <= PatternSpeedKts) errors.Add("MaximumSafeSpeedKts must exceed PatternSpeedKts.");
            if (MaximumPitchDownDeg >= 0.0 || MaximumPitchUpDeg <= 0.0 || MaximumPitchUpDeg > 30.0 || MaximumPitchDownDeg < -25.0)
                errors.Add("Aircraft pitch envelope is invalid.");
            if (MaximumBankDeg < 5.0 || MaximumBankDeg > 35.0) errors.Add("MaximumBankDeg must be 5..35 degrees.");
            if (MaximumFinalBankDeg < 3.0 || MaximumFinalBankDeg > MaximumBankDeg) errors.Add("MaximumFinalBankDeg is invalid.");
            if (!UnitInterval(TakeoffFlaps) || !UnitInterval(BaseFlaps) || !UnitInterval(LandingFlaps)
                || TakeoffFlaps > BaseFlaps || BaseFlaps > LandingFlaps)
                errors.Add("Aircraft flap settings must be ordered values in 0..1.");
            return errors;
        }

        private static bool UnitInterval(double value)
        {
            return MathEx.AreFinite(value) && value >= 0.0 && value <= 1.0;
        }

        public AircraftProfile Clone()
        {
            return (AircraftProfile)MemberwiseClone();
        }

        public AircraftProfile AutoTuned(AircraftState state)
        {
            var tuned = Clone();
            if (state.DesignVs0Kts >= 25.0 && state.DesignVs0Kts <= 150.0)
            {
                tuned.StallSpeedLandingKts = state.DesignVs0Kts;
                tuned.ApproachSpeedKts = Math.Round(state.DesignVs0Kts * 1.30, 1);
            }

            if (state.DesignVs1Kts >= 25.0 && state.DesignVs1Kts <= 150.0)
            {
                tuned.StallSpeedCleanKts = state.DesignVs1Kts;
                tuned.RotateSpeedKts = Math.Round(state.DesignVs1Kts * 1.18, 1);
                tuned.ClimbSpeedKts = Math.Round(state.DesignVs1Kts * 1.40, 1);
            }

            if (state.EstimatedCruiseKts >= 60.0 && state.EstimatedCruiseKts <= 350.0)
            {
                tuned.PatternSpeedKts = MathEx.Clamp(state.EstimatedCruiseKts * 0.65,
                    tuned.ApproachSpeedKts + 8.0, tuned.MaximumSafeSpeedKts - 10.0);
            }

            return tuned;
        }
    }

    [DataContract]
    public sealed class PatternProfile
    {
        [DataMember] public string Direction { get; set; }
        [DataMember] public double PatternAltitudeAglFt { get; set; }
        [DataMember] public double CrosswindTurnAltitudeAglFt { get; set; }
        [DataMember] public double DownwindOffsetNm { get; set; }
        [DataMember] public double BaseTurnPastThresholdNm { get; set; }
        [DataMember] public double FinalTurnLeadNm { get; set; }
        [DataMember] public double GlideSlopeDeg { get; set; }
        [DataMember] public double FlareHeightAglFt { get; set; }
        [DataMember] public double GoAroundMinimumAglFt { get; set; }
        [DataMember] public double StableApproachMinimumAglFt { get; set; }
        [DataMember] public double MaximumFinalCrossTrackM { get; set; }
        [DataMember] public double MaximumFinalSpeedErrorKts { get; set; }
        [DataMember] public double MaximumFinalSinkRateFpm { get; set; }

        public PatternDirection ParsedDirection
        {
            get { return string.Equals(Direction, "Right", StringComparison.OrdinalIgnoreCase) ? PatternDirection.Right : PatternDirection.Left; }
        }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(PatternAltitudeAglFt, CrosswindTurnAltitudeAglFt, DownwindOffsetNm,
                    BaseTurnPastThresholdNm, FinalTurnLeadNm, GlideSlopeDeg, FlareHeightAglFt,
                    GoAroundMinimumAglFt, StableApproachMinimumAglFt, MaximumFinalCrossTrackM,
                    MaximumFinalSpeedErrorKts, MaximumFinalSinkRateFpm))
                errors.Add("Pattern numeric values must be finite.");
            if (!string.Equals(Direction, "Left", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Direction, "Right", StringComparison.OrdinalIgnoreCase))
                errors.Add("Pattern.Direction must be Left or Right.");
            if (PatternAltitudeAglFt < 500.0 || PatternAltitudeAglFt > 2500.0) errors.Add("PatternAltitudeAglFt must be 500..2500.");
            if (CrosswindTurnAltitudeAglFt < 200.0 || CrosswindTurnAltitudeAglFt >= PatternAltitudeAglFt) errors.Add("CrosswindTurnAltitudeAglFt is invalid.");
            if (DownwindOffsetNm < 0.3 || DownwindOffsetNm > 3.0) errors.Add("DownwindOffsetNm must be 0.3..3.0.");
            if (BaseTurnPastThresholdNm < 0.2 || BaseTurnPastThresholdNm > 3.0) errors.Add("BaseTurnPastThresholdNm must be 0.2..3.0.");
            if (FinalTurnLeadNm < 0.05 || FinalTurnLeadNm > DownwindOffsetNm) errors.Add("FinalTurnLeadNm is invalid.");
            if (GlideSlopeDeg < 2.0 || GlideSlopeDeg > 5.0) errors.Add("GlideSlopeDeg must be 2..5.");
            if (FlareHeightAglFt < 5.0 || FlareHeightAglFt > 50.0) errors.Add("FlareHeightAglFt must be 5..50.");
            if (GoAroundMinimumAglFt <= FlareHeightAglFt || StableApproachMinimumAglFt <= GoAroundMinimumAglFt)
                errors.Add("Pattern approach decision heights must satisfy flare < go-around < stable-approach.");
            if (MaximumFinalCrossTrackM <= 0.0 || MaximumFinalSpeedErrorKts <= 0.0 || MaximumFinalSinkRateFpm < 300.0)
                errors.Add("Pattern final-approach limits are invalid.");
            return errors;
        }
    }

    [DataContract]
    public sealed class ControllerProfile
    {
        [DataMember] public double HeadingToBankKp { get; set; }
        [DataMember] public double BankKp { get; set; }
        [DataMember] public double BankKi { get; set; }
        [DataMember] public double BankKd { get; set; }
        [DataMember] public double PitchKp { get; set; }
        [DataMember] public double PitchKi { get; set; }
        [DataMember] public double PitchKd { get; set; }
        [DataMember] public double SpeedThrottleKp { get; set; }
        [DataMember] public double SpeedThrottleKi { get; set; }
        [DataMember] public double GroundHeadingKp { get; set; }
        [DataMember] public double FinalCrossTrackHeadingGain { get; set; }
        [DataMember] public bool UseTecs { get; set; }
        [DataMember] public double TecsTotalEnergyKp { get; set; }
        [DataMember] public double TecsTotalEnergyKi { get; set; }
        [DataMember] public double TecsBalanceKp { get; set; }
        [DataMember] public double TecsBalanceKi { get; set; }
        [DataMember] public double TecsMaximumPitchCorrectionDeg { get; set; }
        [DataMember] public double TecsMaximumThrottleCorrection { get; set; }
        [DataMember] public double GainScheduleLowSpeedKts { get; set; }
        [DataMember] public double GainScheduleHighSpeedKts { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(HeadingToBankKp, BankKp, BankKi, BankKd, PitchKp, PitchKi, PitchKd,
                    SpeedThrottleKp, SpeedThrottleKi, GroundHeadingKp, FinalCrossTrackHeadingGain,
                    TecsTotalEnergyKp, TecsTotalEnergyKi, TecsBalanceKp, TecsBalanceKi,
                    TecsMaximumPitchCorrectionDeg, TecsMaximumThrottleCorrection,
                    GainScheduleLowSpeedKts, GainScheduleHighSpeedKts))
                errors.Add("Controller gains must be finite.");
            if (HeadingToBankKp < 0.0 || BankKp < 0.0 || BankKi < 0.0 || BankKd < 0.0
                || PitchKp < 0.0 || PitchKi < 0.0 || PitchKd < 0.0
                || SpeedThrottleKp < 0.0 || SpeedThrottleKi < 0.0 || GroundHeadingKp < 0.0
                || FinalCrossTrackHeadingGain < 0.0)
                errors.Add("Controller gains cannot be negative.");
            if (UseTecs)
            {
                if (TecsMaximumPitchCorrectionDeg <= 0.0 || TecsMaximumPitchCorrectionDeg > 10.0)
                    errors.Add("Controllers.TecsMaximumPitchCorrectionDeg must be in (0, 10].");
                if (TecsMaximumThrottleCorrection <= 0.0 || TecsMaximumThrottleCorrection > 0.5)
                    errors.Add("Controllers.TecsMaximumThrottleCorrection must be in (0, 0.5].");
                if (GainScheduleLowSpeedKts <= 0.0 || GainScheduleHighSpeedKts <= GainScheduleLowSpeedKts)
                    errors.Add("Controllers gain-schedule speed range is invalid.");
            }
            return errors;
        }
    }

    [DataContract]
    public sealed class SafetyProfile
    {
        [DataMember] public bool ManualOverrideEnabled { get; set; }
        [DataMember] public double ManualOverrideAxisDelta { get; set; }
        [DataMember] public double ManualOverrideHoldSeconds { get; set; }
        [DataMember] public double ManualOverrideGraceSeconds { get; set; }
        [DataMember] public bool CommandRateLimitEnabled { get; set; }
        [DataMember] public double MaximumPrimaryAxisRatePerSecond { get; set; }
        [DataMember] public double MaximumThrottleRatePerSecond { get; set; }
        [DataMember] public double MaximumBrakeRatePerSecond { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(ManualOverrideAxisDelta, ManualOverrideHoldSeconds, ManualOverrideGraceSeconds,
                    MaximumPrimaryAxisRatePerSecond, MaximumThrottleRatePerSecond, MaximumBrakeRatePerSecond))
                errors.Add("Safety numeric values must be finite.");
            if (ManualOverrideAxisDelta < 0.10 || ManualOverrideAxisDelta > 0.80)
                errors.Add("Safety.ManualOverrideAxisDelta must be 0.10..0.80.");
            if (ManualOverrideHoldSeconds < 0.1 || ManualOverrideHoldSeconds > 2.0)
                errors.Add("Safety.ManualOverrideHoldSeconds must be 0.1..2.0.");
            if (ManualOverrideGraceSeconds < 0.0 || ManualOverrideGraceSeconds > 10.0)
                errors.Add("Safety.ManualOverrideGraceSeconds must be 0..10.");
            if (MaximumPrimaryAxisRatePerSecond <= 0.0 || MaximumPrimaryAxisRatePerSecond > 10.0)
                errors.Add("Safety.MaximumPrimaryAxisRatePerSecond must be in (0, 10].");
            if (MaximumThrottleRatePerSecond <= 0.0 || MaximumThrottleRatePerSecond > 10.0)
                errors.Add("Safety.MaximumThrottleRatePerSecond must be in (0, 10].");
            if (MaximumBrakeRatePerSecond <= 0.0 || MaximumBrakeRatePerSecond > 10.0)
                errors.Add("Safety.MaximumBrakeRatePerSecond must be in (0, 10].");
            return errors;
        }
    }

    [DataContract]
    public sealed class LlmProfile
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public string Mode { get; set; }
        [DataMember] public string Endpoint { get; set; }
        [DataMember] public string Model { get; set; }
        [DataMember] public double IntervalSeconds { get; set; }
        [DataMember] public double TimeoutSeconds { get; set; }
        [DataMember] public double MaximumHeadingChangeDeg { get; set; }
        [DataMember] public double MaximumSpeedChangeKts { get; set; }
        [DataMember] public double MinimumConfidence { get; set; }
        [DataMember] public int MaximumConsecutiveFailures { get; set; }
        [DataMember] public double FailureCooldownSeconds { get; set; }

        public bool IsAssist
        {
            get { return string.Equals(Mode, "Assist", StringComparison.OrdinalIgnoreCase); }
        }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(IntervalSeconds, TimeoutSeconds, MaximumHeadingChangeDeg,
                    MaximumSpeedChangeKts, MinimumConfidence, FailureCooldownSeconds))
                errors.Add("Llm numeric values must be finite.");
            if (!string.Equals(Mode, "Shadow", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Mode, "Assist", StringComparison.OrdinalIgnoreCase))
                errors.Add("Llm.Mode must be Shadow or Assist.");
            Uri endpoint;
            if (Enabled && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out endpoint)
                            || !endpoint.IsLoopback || !string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
                errors.Add("Llm.Endpoint must be an absolute loopback HTTP URL.");
            if (Enabled && string.IsNullOrWhiteSpace(Model)) errors.Add("Llm.Model is required when LLM is enabled.");
            if (IntervalSeconds < 0.25 || IntervalSeconds > 60.0) errors.Add("Llm.IntervalSeconds must be 0.25..60.");
            if (TimeoutSeconds < 0.25 || TimeoutSeconds > 30.0) errors.Add("Llm.TimeoutSeconds must be 0.25..30.");
            if (MinimumConfidence < 0.0 || MinimumConfidence > 1.0) errors.Add("Llm.MinimumConfidence must be 0..1.");
            if (MaximumHeadingChangeDeg < 0.0 || MaximumHeadingChangeDeg > 30.0
                || MaximumSpeedChangeKts < 0.0 || MaximumSpeedChangeKts > 20.0)
                errors.Add("Llm guidance adjustment limits are invalid.");
            if (MaximumConsecutiveFailures < 1 || MaximumConsecutiveFailures > 20)
                errors.Add("Llm.MaximumConsecutiveFailures must be 1..20.");
            if (FailureCooldownSeconds < 1.0 || FailureCooldownSeconds > 300.0)
                errors.Add("Llm.FailureCooldownSeconds must be 1..300.");
            return errors;
        }
    }

    [DataContract]
    public sealed class RlProfile
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool BridgeEnabled { get; set; }
        [DataMember] public string Mode { get; set; }
        [DataMember] public string ListenPrefix { get; set; }
        [DataMember] public double ActionTimeoutSeconds { get; set; }
        [DataMember] public double ResetSettlingSeconds { get; set; }
        [DataMember] public double MaxResidualAileron { get; set; }
        [DataMember] public double MaxResidualElevator { get; set; }
        [DataMember] public double MaxResidualRudder { get; set; }
        [DataMember] public double MaxResidualThrottle { get; set; }
        [DataMember] public double MaxResidualRatePerSecond { get; set; }
        [DataMember] public double EnvelopeSoftMarginBankDeg { get; set; }
        [DataMember] public double EnvelopeSoftMarginPitchDeg { get; set; }
        [DataMember] public double EnvelopeSoftMarginSpeedKts { get; set; }
        [DataMember] public double MaximumPolicyUncertainty { get; set; }
        [DataMember] public int MaxRequestBodyBytes { get; set; }
        [DataMember] public int MaxConcurrentRequests { get; set; }

        public bool AppliesActions
        {
            get
            {
                return Enabled && (string.Equals(Mode, "Train", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Mode, "Inference", StringComparison.OrdinalIgnoreCase));
            }
        }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!MathEx.AreFinite(ActionTimeoutSeconds, ResetSettlingSeconds, MaxResidualAileron,
                    MaxResidualElevator, MaxResidualRudder, MaxResidualThrottle, MaxResidualRatePerSecond,
                    EnvelopeSoftMarginBankDeg, EnvelopeSoftMarginPitchDeg, EnvelopeSoftMarginSpeedKts,
                    MaximumPolicyUncertainty))
                errors.Add("Rl numeric values must be finite.");
            if (!string.Equals(Mode, "Shadow", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Mode, "Train", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Mode, "Inference", StringComparison.OrdinalIgnoreCase))
                errors.Add("Rl.Mode must be Shadow, Train, or Inference.");
            if (BridgeEnabled && (string.IsNullOrWhiteSpace(ListenPrefix)
                                  || !ListenPrefix.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                                  || !ListenPrefix.EndsWith("/", StringComparison.Ordinal)))
                errors.Add("Rl.ListenPrefix must be a loopback http://127.0.0.1:PORT/ prefix.");
            if (ActionTimeoutSeconds < 0.1 || ActionTimeoutSeconds > 5.0) errors.Add("Rl.ActionTimeoutSeconds must be 0.1..5.0.");
            if (ResetSettlingSeconds < 0.5 || ResetSettlingSeconds > 30.0) errors.Add("Rl.ResetSettlingSeconds must be 0.5..30.");
            if (MaxResidualAileron < 0.0 || MaxResidualAileron > 0.35) errors.Add("Rl.MaxResidualAileron must be 0..0.35.");
            if (MaxResidualElevator < 0.0 || MaxResidualElevator > 0.30) errors.Add("Rl.MaxResidualElevator must be 0..0.30.");
            if (MaxResidualRudder < 0.0 || MaxResidualRudder > 0.30) errors.Add("Rl.MaxResidualRudder must be 0..0.30.");
            if (MaxResidualThrottle < 0.0 || MaxResidualThrottle > 0.25) errors.Add("Rl.MaxResidualThrottle must be 0..0.25.");
            if (MaxResidualRatePerSecond <= 0.0 || MaxResidualRatePerSecond > 5.0) errors.Add("Rl.MaxResidualRatePerSecond must be 0..5.");
            if (MaximumPolicyUncertainty <= 0.0 || MaximumPolicyUncertainty > 1.0) errors.Add("Rl.MaximumPolicyUncertainty must be in (0, 1].");
            if (EnvelopeSoftMarginBankDeg <= 0.0 || EnvelopeSoftMarginPitchDeg <= 0.0 || EnvelopeSoftMarginSpeedKts <= 0.0)
                errors.Add("Rl soft-envelope margins must be positive.");
            if (MaxRequestBodyBytes < 1024 || MaxRequestBodyBytes > 1048576)
                errors.Add("Rl.MaxRequestBodyBytes must be 1024..1048576.");
            if (MaxConcurrentRequests < 1 || MaxConcurrentRequests > 32)
                errors.Add("Rl.MaxConcurrentRequests must be 1..32.");
            return errors;
        }
    }
}
