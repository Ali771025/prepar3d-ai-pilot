using System;
using System.Globalization;
using System.IO;
using System.Text;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal sealed class FlightCsvRecorder : IDisposable
    {
        private readonly object _sync = new object();
        private readonly StreamWriter _writer;
        private int _pendingRows;
        private bool _disposed;

        public FlightCsvRecorder(string directory)
        {
            string resolved = Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, directory ?? "logs");
            Directory.CreateDirectory(resolved);
            FilePath = Path.Combine(resolved, "flight_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv");
            _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false), 1024 * 64);
            _writer.WriteLine("schema_version,episode_id,telemetry_source,telemetry_sequence,utc,sim_time_s,phase,lat_deg,lon_deg,alt_msl_ft,alt_agl_ft,ias_kts,tas_kts,gs_kts,vs_fpm,pitch_deg,bank_deg,heading_deg,aoa_deg,beta_deg,body_rate_p_dps,body_rate_q_dps,body_rate_r_dps,accel_x_g,accel_y_g,accel_z_g,wind_speed_kts,wind_direction_deg,wind_cross_kts,rpm,fuel_fraction,weight_fraction,cg_fraction,on_ground,stall,overspeed,pilot_yoke_x,pilot_yoke_y,pilot_rudder,pilot_throttle,pilot_flaps,cmd_aileron,cmd_elevator,cmd_rudder,cmd_throttle,cmd_flaps,cmd_gear,cmd_brakes,target_heading,target_ias,target_alt_msl,target_vs,along_m,cross_m,safety,llm_intent,llm_confidence,llm_reason,rl_stage,rl_step_id,rl_req_aileron,rl_req_elevator,rl_req_rudder,rl_req_throttle,rl_applied_aileron,rl_applied_elevator,rl_applied_rudder,rl_applied_throttle,rl_safety_scale,rl_uncertainty,rl_intervened,rl_rejection,aircraft_id,aircraft_title,control_role,controller_output_enabled");
        }

        public string FilePath { get; private set; }

        public void Append(AircraftState state, AutopilotOutput output, LlmDecision llm, ResidualTrace residual)
        {
            if (state == null || output == null) return;
            ControlCommand command = output.Command ?? ControlCommand.Neutral();
            GuidanceTargets target = output.Targets ?? new GuidanceTargets();
            RunwayPosition position = output.RunwayPosition ?? new RunwayPosition();
            SafetyResult safety = output.Safety ?? new SafetyResult { Reason = "" };
            ResidualAction requested = residual?.Requested ?? ResidualAction.Zero();
            ResidualAction applied = residual?.Applied ?? ResidualAction.Zero();
            var columns = new[]
            {
                "episode-v4.2", Q(Path.GetFileNameWithoutExtension(FilePath)), Q(state.TelemetrySource),
                state.TelemetrySequence.ToString(CultureInfo.InvariantCulture),
                state.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture),
                F(state.SimTimeSeconds), output.Phase.ToString(), F(state.LatitudeDeg), F(state.LongitudeDeg),
                F(state.AltitudeMslFt), F(state.AltitudeAglFt), F(state.IndicatedAirspeedKts), F(state.TrueAirspeedKts), F(state.GroundSpeedKts),
                F(state.VerticalSpeedFpm), F(state.PitchDeg), F(state.BankDeg), F(state.HeadingTrueDeg), F(state.AngleOfAttackDeg),
                F(state.SideSlipDeg), F(state.BodyRatePDegPerSecond), F(state.BodyRateQDegPerSecond), F(state.BodyRateRDegPerSecond),
                F(state.AccelerationXG), F(state.AccelerationYG), F(state.AccelerationZG), F(state.WindSpeedKts),
                F(state.WindDirectionTrueDeg), F(state.CrosswindKts), F(state.EngineRpm), F(state.FuelFraction),
                F(state.WeightFraction), F(state.CgPercent / 100.0), B(state.OnGround), B(state.StallWarning), B(state.OverspeedWarning),
                F(state.YokeX), F(state.YokeY), F(state.RudderPedal), F(state.Throttle01), F(state.Flaps01),
                F(command.Aileron), F(command.Elevator), F(command.Rudder), F(command.Throttle01), F(command.Flaps01),
                B(command.GearDown), F(command.Brakes01), F(target.HeadingTrueDeg), F(target.AirspeedKts),
                F(target.AltitudeMslFt), F(target.VerticalSpeedFpm), F(position.AlongTrackM), F(position.CrossTrackM),
                Q(safety.Reason), Q(llm == null ? "" : llm.Intent), F(llm == null ? 0.0 : llm.Confidence),
                Q(llm == null ? "" : llm.Reason), Q(requested.Stage), requested.StepId.ToString(CultureInfo.InvariantCulture),
                F(requested.Aileron), F(requested.Elevator), F(requested.Rudder), F(requested.Throttle),
                F(applied.Aileron), F(applied.Elevator), F(applied.Rudder), F(applied.Throttle),
                F(residual == null ? 0.0 : residual.SafetyScale), F(residual == null ? 0.0 : residual.Uncertainty),
                B(residual != null && residual.Intervened), Q(residual == null ? "" : residual.RejectionReason),
                state.AircraftObjectId.ToString(CultureInfo.InvariantCulture), Q(state.AircraftTitle),
                Q(state.ControlRole ?? "unspecified"), B(output.ApplyControls)
            };

            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FlightCsvRecorder));
                _writer.WriteLine(string.Join(",", columns));
                if (++_pendingRows >= 50)
                {
                    _writer.Flush();
                    _pendingRows = 0;
                }
            }
        }

        private static string F(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }
        private static string B(bool value) { return value ? "1" : "0"; }
        private static string Q(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _writer.Dispose();
            }
        }
    }
}
