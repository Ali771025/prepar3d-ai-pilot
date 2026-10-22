using System;
using System.Collections.Generic;

namespace P3DAIPilot.Core
{
    public enum FlightPhase
    {
        Disarmed,
        Armed,
        TakeoffRoll,
        Rotate,
        InitialClimb,
        Crosswind,
        Downwind,
        Base,
        Final,
        Flare,
        Rollout,
        GoAround,
        Completed,
        Failsafe
    }

    public enum PatternDirection
    {
        Left = -1,
        Right = 1
    }

    public enum VerticalMode
    {
        Pitch,
        Altitude,
        VerticalSpeed,
        GlidePath,
        Flare
    }

    public sealed class AircraftState
    {
        public string TelemetrySource { get; set; }
        public string AircraftTitle { get; set; }
        public uint AircraftObjectId { get; set; }
        public string ControlRole { get; set; }
        public long TelemetrySequence { get; set; }
        public DateTime UtcTimestamp { get; set; }
        public double SimTimeSeconds { get; set; }
        public double LatitudeDeg { get; set; }
        public double LongitudeDeg { get; set; }
        public double AltitudeMslFt { get; set; }
        public double AltitudeAglFt { get; set; }
        public double IndicatedAirspeedKts { get; set; }
        public double TrueAirspeedKts { get; set; }
        public double GroundSpeedKts { get; set; }
        public double VerticalSpeedFpm { get; set; }
        public double PitchDeg { get; set; }
        public double BankDeg { get; set; }
        public double HeadingTrueDeg { get; set; }
        public double AngleOfAttackDeg { get; set; }
        public double SideSlipDeg { get; set; }
        public double BodyRatePDegPerSecond { get; set; }
        public double BodyRateQDegPerSecond { get; set; }
        public double BodyRateRDegPerSecond { get; set; }
        public double AccelerationXG { get; set; }
        public double AccelerationYG { get; set; }
        public double AccelerationZG { get; set; }
        public double WindSpeedKts { get; set; }
        public double WindDirectionTrueDeg { get; set; }
        public double EngineRpm { get; set; }
        public double FuelFraction { get; set; }
        public double WeightLb { get; set; }
        public double MaximumGrossWeightLb { get; set; }
        public double CgPercent { get; set; }
        public double YokeX { get; set; }
        public double YokeY { get; set; }
        public double RudderPedal { get; set; }
        public double Throttle01 { get; set; }
        public double Flaps01 { get; set; }
        public double DesignVs0Kts { get; set; }
        public double DesignVs1Kts { get; set; }
        public double EstimatedCruiseKts { get; set; }
        public int EngineType { get; set; }
        public int EngineCount { get; set; }
        public bool OnGround { get; set; }
        public bool ParkingBrakeSet { get; set; }
        public bool StallWarning { get; set; }
        public bool OverspeedWarning { get; set; }
        public bool SlewActive { get; set; }
        public bool Paused { get; set; }

        public bool IsFinite()
        {
            return MathEx.AreFinite(
                LatitudeDeg, LongitudeDeg, AltitudeMslFt, AltitudeAglFt,
                IndicatedAirspeedKts, TrueAirspeedKts, GroundSpeedKts,
                VerticalSpeedFpm, PitchDeg, BankDeg, HeadingTrueDeg,
                AngleOfAttackDeg, SideSlipDeg, BodyRatePDegPerSecond,
                BodyRateQDegPerSecond, BodyRateRDegPerSecond,
                AccelerationXG, AccelerationYG, AccelerationZG,
                WindSpeedKts, WindDirectionTrueDeg, EngineRpm,
                FuelFraction, WeightLb, MaximumGrossWeightLb, CgPercent,
                YokeX, YokeY, RudderPedal, Throttle01, Flaps01,
                DesignVs0Kts, DesignVs1Kts, EstimatedCruiseKts,
                SimTimeSeconds);
        }

        public double WeightFraction
        {
            get { return MaximumGrossWeightLb > 1.0 ? MathEx.Clamp(WeightLb / MaximumGrossWeightLb, 0.0, 2.0) : 1.0; }
        }

        public double CrosswindKts
        {
            get
            {
                double relative = MathEx.DegreesToRadians(WindDirectionTrueDeg - HeadingTrueDeg);
                return WindSpeedKts * Math.Sin(relative);
            }
        }
    }

    [Flags]
    public enum SimulatorCapabilities
    {
        None = 0,
        Telemetry = 1,
        Controls = 2,
        ScenarioReset = 4,
        Payload = 8
    }

    public sealed class SimulatorTransportHealth
    {
        public string Name { get; set; }
        public bool IsConnected { get; set; }
        public bool HasTelemetry { get; set; }
        public DateTime ConnectedUtc { get; set; }
        public DateTime LastTelemetryUtc { get; set; }
        public DateTime LastCommandUtc { get; set; }
        public long TelemetrySamples { get; set; }
        public long RejectedTelemetrySamples { get; set; }
        public long CommandsSent { get; set; }
        public long ReconnectAttempts { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string Version { get; set; }
        public string LastError { get; set; }
        public bool? SimulationPaused { get; set; }
        public string ScenarioPath { get; set; }
        public long? ScenarioLoadCount { get; set; }
        public long? UpstreamReconnectCount { get; set; }
        public SimulatorCapabilities Capabilities { get; set; }

        public bool IsFresh(DateTime utcNow, double staleSeconds)
        {
            double age = (utcNow - LastTelemetryUtc).TotalSeconds;
            return IsConnected && HasTelemetry && LastTelemetryUtc != DateTime.MinValue
                   && age >= -1.0 && age <= staleSeconds;
        }

        public SimulatorTransportHealth Clone()
        {
            return (SimulatorTransportHealth)MemberwiseClone();
        }
    }

    public sealed class SimulatorGatewayHealth
    {
        public string ActiveTransport { get; set; }
        public string PreferredTransport { get; set; }
        public bool DivergenceDetected { get; set; }
        public double DivergenceDurationSeconds { get; set; }
        public bool ControlInhibited { get; set; }
        public double AltitudeDivergenceFt { get; set; }
        public double AirspeedDivergenceKts { get; set; }
        public double HeadingDivergenceDeg { get; set; }
        public IList<SimulatorTransportHealth> Transports { get; set; }
    }

    public sealed class RunwayFrame
    {
        public double ThresholdLatitudeDeg { get; set; }
        public double ThresholdLongitudeDeg { get; set; }
        public double ThresholdElevationFt { get; set; }
        public double HeadingTrueDeg { get; set; }
        public double LengthM { get; set; }
        public double WidthM { get; set; }

        public bool IsValid()
        {
            return MathEx.AreFinite(ThresholdLatitudeDeg, ThresholdLongitudeDeg,
                       ThresholdElevationFt, HeadingTrueDeg, LengthM, WidthM)
                   && ThresholdLatitudeDeg >= -90.0 && ThresholdLatitudeDeg <= 90.0
                   && ThresholdLongitudeDeg >= -180.0 && ThresholdLongitudeDeg <= 180.0
                   && LengthM >= 300.0 && WidthM >= 10.0;
        }
    }

    public sealed class RunwayPosition
    {
        public double AlongTrackM { get; set; }
        public double CrossTrackM { get; set; }
        public double DistanceToThresholdM { get; set; }
    }

    public sealed class GuidanceTargets
    {
        public double HeadingTrueDeg { get; set; }
        public double AirspeedKts { get; set; }
        public double AltitudeMslFt { get; set; }
        public double VerticalSpeedFpm { get; set; }
        public double PitchDeg { get; set; }
        public double BaseThrottle01 { get; set; }
        public double Flaps01 { get; set; }
        public bool GearDown { get; set; }
        public bool Brakes01Enabled { get; set; }
        public double Brakes01 { get; set; }
        public VerticalMode VerticalMode { get; set; }
        public string Intent { get; set; }

        public GuidanceTargets Clone()
        {
            return (GuidanceTargets)MemberwiseClone();
        }
    }

    public sealed class ControlCommand
    {
        public double Aileron { get; set; }
        public double Elevator { get; set; }
        public double Rudder { get; set; }
        public double Throttle01 { get; set; }
        public double Flaps01 { get; set; }
        public bool GearDown { get; set; }
        public double Brakes01 { get; set; }

        public static ControlCommand Neutral()
        {
            return new ControlCommand();
        }

        public ControlCommand Clone()
        {
            return (ControlCommand)MemberwiseClone();
        }

        public bool IsFinite()
        {
            return MathEx.AreFinite(Aileron, Elevator, Rudder, Throttle01, Flaps01, Brakes01);
        }

        public void Clamp()
        {
            Aileron = MathEx.Clamp(Aileron, -1.0, 1.0);
            Elevator = MathEx.Clamp(Elevator, -1.0, 1.0);
            Rudder = MathEx.Clamp(Rudder, -1.0, 1.0);
            Throttle01 = MathEx.Clamp(Throttle01, 0.0, 1.0);
            Flaps01 = MathEx.Clamp(Flaps01, 0.0, 1.0);
            Brakes01 = MathEx.Clamp(Brakes01, 0.0, 1.0);
        }
    }

    public sealed class SafetyResult
    {
        public bool MayControl { get; set; }
        public bool MustGoAround { get; set; }
        public bool MustDisarm { get; set; }
        public string Reason { get; set; }

        public static SafetyResult Safe()
        {
            return new SafetyResult { MayControl = true, Reason = "OK" };
        }
    }

    public sealed class AutopilotOutput
    {
        public FlightPhase Phase { get; set; }
        public GuidanceTargets Targets { get; set; }
        public ControlCommand Command { get; set; }
        public RunwayPosition RunwayPosition { get; set; }
        public SafetyResult Safety { get; set; }
        public string TransitionReason { get; set; }
        public bool ApplyControls { get; set; }
    }

    public sealed class ResidualAction
    {
        public double Aileron { get; set; }
        public double Elevator { get; set; }
        public double Rudder { get; set; }
        public double Throttle { get; set; }
        public string Stage { get; set; }
        public long StepId { get; set; }
        public int ResetId { get; set; }
        public double Uncertainty { get; set; }
        public DateTime ReceivedUtc { get; set; }

        public static ResidualAction Zero(string stage = "shadow")
        {
            return new ResidualAction { Stage = stage, ReceivedUtc = DateTime.UtcNow };
        }

        public void ClampNormalized()
        {
            Aileron = MathEx.Clamp(Aileron, -1.0, 1.0);
            Elevator = MathEx.Clamp(Elevator, -1.0, 1.0);
            Rudder = MathEx.Clamp(Rudder, -1.0, 1.0);
            Throttle = MathEx.Clamp(Throttle, -1.0, 1.0);
        }
    }

    public sealed class ResidualTrace
    {
        public ResidualAction Requested { get; set; }
        public ResidualAction Applied { get; set; }
        public double SafetyScale { get; set; }
        public double Uncertainty { get; set; }
        public bool Intervened { get; set; }
        public string RejectionReason { get; set; }

        public static ResidualTrace Rejected(ResidualAction requested, string reason)
        {
            return new ResidualTrace
            {
                Requested = requested ?? ResidualAction.Zero(),
                Applied = ResidualAction.Zero(requested == null ? "shadow" : requested.Stage),
                SafetyScale = 0.0,
                Uncertainty = requested == null ? 0.0 : requested.Uncertainty,
                Intervened = true,
                RejectionReason = reason
            };
        }
    }

    public sealed class EnergyCommand
    {
        public double PitchTargetDeg { get; set; }
        public double Throttle01 { get; set; }
        public double TotalEnergyError { get; set; }
        public double EnergyBalanceError { get; set; }
    }
}
