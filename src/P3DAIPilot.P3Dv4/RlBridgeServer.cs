using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    [DataContract]
    internal sealed class RlActionRequest
    {
        [DataMember(Name = "action")] public double[] Action { get; set; }
        [DataMember(Name = "stage")] public string Stage { get; set; }
        [DataMember(Name = "step_id")] public long StepId { get; set; }
        [DataMember(Name = "reset_id")] public int ResetId { get; set; }
        [DataMember(Name = "uncertainty")] public double Uncertainty { get; set; }
    }

    [DataContract]
    internal sealed class RlObservation
    {
        [DataMember(Name = "aircraft_id")] public uint AircraftObjectId { get; set; }
        [DataMember(Name = "aircraft_title")] public string AircraftTitle { get; set; }
        [DataMember(Name = "control_role")] public string ControlRole { get; set; }
        [DataMember(Name = "sequence")] public long Sequence { get; set; }
        [DataMember(Name = "reset_id")] public int ResetId { get; set; }
        [DataMember(Name = "reset_status")] public string ResetStatus { get; set; }
        [DataMember(Name = "utc")] public string Utc { get; set; }
        [DataMember(Name = "phase")] public string Phase { get; set; }
        [DataMember(Name = "phase_id")] public int PhaseId { get; set; }
        [DataMember(Name = "ias_kts")] public double IasKts { get; set; }
        [DataMember(Name = "tas_kts")] public double TrueAirspeedKts { get; set; }
        [DataMember(Name = "gs_kts")] public double GroundSpeedKts { get; set; }
        [DataMember(Name = "alt_msl_ft")] public double AltitudeMslFt { get; set; }
        [DataMember(Name = "alt_agl_ft")] public double AltitudeAglFt { get; set; }
        [DataMember(Name = "vs_fpm")] public double VerticalSpeedFpm { get; set; }
        [DataMember(Name = "pitch_deg")] public double PitchDeg { get; set; }
        [DataMember(Name = "bank_deg")] public double BankDeg { get; set; }
        [DataMember(Name = "heading_deg")] public double HeadingDeg { get; set; }
        [DataMember(Name = "aoa_deg")] public double AngleOfAttackDeg { get; set; }
        [DataMember(Name = "beta_deg")] public double SideSlipDeg { get; set; }
        [DataMember(Name = "body_rate_p_dps")] public double BodyRatePDegPerSecond { get; set; }
        [DataMember(Name = "body_rate_q_dps")] public double BodyRateQDegPerSecond { get; set; }
        [DataMember(Name = "body_rate_r_dps")] public double BodyRateRDegPerSecond { get; set; }
        [DataMember(Name = "accel_x_g")] public double AccelerationXG { get; set; }
        [DataMember(Name = "accel_y_g")] public double AccelerationYG { get; set; }
        [DataMember(Name = "accel_z_g")] public double AccelerationZG { get; set; }
        [DataMember(Name = "wind_speed_kts")] public double WindSpeedKts { get; set; }
        [DataMember(Name = "wind_cross_kts")] public double WindCrossKts { get; set; }
        [DataMember(Name = "rpm")] public double EngineRpm { get; set; }
        [DataMember(Name = "fuel_fraction")] public double FuelFraction { get; set; }
        [DataMember(Name = "weight_fraction")] public double WeightFraction { get; set; }
        [DataMember(Name = "cg_fraction")] public double CgFraction { get; set; }
        [DataMember(Name = "pilot_yoke_x")] public double PilotYokeX { get; set; }
        [DataMember(Name = "pilot_yoke_y")] public double PilotYokeY { get; set; }
        [DataMember(Name = "pilot_rudder")] public double PilotRudder { get; set; }
        [DataMember(Name = "pilot_throttle")] public double PilotThrottle { get; set; }
        [DataMember(Name = "along_m")] public double AlongM { get; set; }
        [DataMember(Name = "cross_m")] public double CrossM { get; set; }
        [DataMember(Name = "distance_threshold_m")] public double DistanceThresholdM { get; set; }
        [DataMember(Name = "glide_error_ft")] public double GlideErrorFt { get; set; }
        [DataMember(Name = "on_ground")] public bool OnGround { get; set; }
        [DataMember(Name = "stall")] public bool Stall { get; set; }
        [DataMember(Name = "overspeed")] public bool Overspeed { get; set; }
        [DataMember(Name = "paused")] public bool Paused { get; set; }
        [DataMember(Name = "slew")] public bool Slew { get; set; }
        [DataMember(Name = "target_heading_deg")] public double TargetHeadingDeg { get; set; }
        [DataMember(Name = "target_ias_kts")] public double TargetIasKts { get; set; }
        [DataMember(Name = "target_alt_msl_ft")] public double TargetAltitudeMslFt { get; set; }
        [DataMember(Name = "target_vs_fpm")] public double TargetVerticalSpeedFpm { get; set; }
        [DataMember(Name = "target_bank_deg")] public double TargetBankDeg { get; set; }
        [DataMember(Name = "baseline")] public double[] Baseline { get; set; }
        [DataMember(Name = "command")] public double[] Command { get; set; }
        [DataMember(Name = "requested_residual")] public double[] RequestedResidual { get; set; }
        [DataMember(Name = "applied_residual")] public double[] AppliedResidual { get; set; }
        [DataMember(Name = "safety_scale")] public double SafetyScale { get; set; }
        [DataMember(Name = "uncertainty")] public double Uncertainty { get; set; }
        [DataMember(Name = "shield_intervened")] public bool ShieldIntervened { get; set; }
        [DataMember(Name = "residual_rejection")] public string ResidualRejection { get; set; }
        [DataMember(Name = "terminal")] public bool Terminal { get; set; }
        [DataMember(Name = "success")] public bool Success { get; set; }
        [DataMember(Name = "terminal_reason")] public string TerminalReason { get; set; }
    }

    [DataContract]
    internal sealed class RlApiResult
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "reset_id")] public int ResetId { get; set; }
    }

    internal sealed class RlBridgeServer : IDisposable
    {
        private readonly object _sync = new object();
        private readonly RlProfile _profile;
        private readonly ResidualSafetyShield _shield;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim _requestSlots;
        private HttpListener _listener;
        private Task _serverTask;
        private ResidualAction _latestAction = ResidualAction.Zero();
        private RlObservation _latestObservation;
        private ControlCommand _lastBaseline = ControlCommand.Neutral();
        private bool _resetRequested;
        private int _resetId;
        private string _resetStatus = "ready";
        private long _sequence;
        private long _lastAcceptedStepId = -1;

        public RlBridgeServer(RlProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _shield = new ResidualSafetyShield(profile);
            _requestSlots = new SemaphoreSlim(Math.Max(1, profile.MaxConcurrentRequests), Math.Max(1, profile.MaxConcurrentRequests));
        }

        public ResidualTrace LastTrace { get { return _shield.LastTrace; } }

        public void Start()
        {
            if (!_profile.BridgeEnabled || _listener != null) return;
            _listener = new HttpListener();
            _listener.Prefixes.Add(_profile.ListenPrefix);
            _listener.Start();
            _serverTask = Task.Run(() => AcceptLoop(_cancellation.Token));
        }

        public ControlCommand ApplyResidual(
            FlightPhase phase,
            AircraftState state,
            GuidanceTargets targets,
            ControlCommand baseline,
            AircraftProfile aircraft,
            double dtSeconds)
        {
            ResidualAction action;
            lock (_sync)
            {
                action = _latestAction;
                _lastBaseline = CopyCommand(baseline);
            }
            return _shield.Apply(phase, state, baseline, action, aircraft, dtSeconds, DateTime.UtcNow);
        }

        public void Publish(AircraftState state, AutopilotOutput output)
        {
            if (state == null || output == null) return;
            GuidanceTargets target = output.Targets ?? new GuidanceTargets();
            RunwayPosition position = output.RunwayPosition ?? new RunwayPosition();
            ControlCommand command = output.Command ?? ControlCommand.Neutral();
            ResidualTrace trace = _shield.LastTrace ?? ResidualTrace.Rejected(null, "No trace.");
            ResidualAction requested = trace.Requested ?? ResidualAction.Zero();
            ResidualAction applied = trace.Applied ?? ResidualAction.Zero();
            ControlCommand baseline;
            lock (_sync) baseline = CopyCommand(_lastBaseline);

            int resetId;
            string resetStatus;
            lock (_sync)
            {
                resetId = _resetId;
                resetStatus = _resetStatus;
            }
            var observation = new RlObservation
            {
                AircraftObjectId = state.AircraftObjectId, AircraftTitle = state.AircraftTitle, ControlRole = state.ControlRole ?? "unspecified",
                Sequence = Interlocked.Increment(ref _sequence),
                ResetId = resetId,
                ResetStatus = resetStatus,
                Utc = state.UtcTimestamp.ToString("O"),
                Phase = output.Phase.ToString(),
                PhaseId = (int)output.Phase,
                IasKts = state.IndicatedAirspeedKts,
                TrueAirspeedKts = state.TrueAirspeedKts,
                GroundSpeedKts = state.GroundSpeedKts,
                AltitudeMslFt = state.AltitudeMslFt,
                AltitudeAglFt = state.AltitudeAglFt,
                VerticalSpeedFpm = state.VerticalSpeedFpm,
                PitchDeg = state.PitchDeg,
                BankDeg = state.BankDeg,
                HeadingDeg = state.HeadingTrueDeg,
                AngleOfAttackDeg = state.AngleOfAttackDeg,
                SideSlipDeg = state.SideSlipDeg,
                BodyRatePDegPerSecond = state.BodyRatePDegPerSecond,
                BodyRateQDegPerSecond = state.BodyRateQDegPerSecond,
                BodyRateRDegPerSecond = state.BodyRateRDegPerSecond,
                AccelerationXG = state.AccelerationXG,
                AccelerationYG = state.AccelerationYG,
                AccelerationZG = state.AccelerationZG,
                WindSpeedKts = state.WindSpeedKts,
                WindCrossKts = state.CrosswindKts,
                EngineRpm = state.EngineRpm,
                FuelFraction = state.FuelFraction,
                WeightFraction = state.WeightFraction,
                CgFraction = state.CgPercent / 100.0,
                PilotYokeX = state.YokeX,
                PilotYokeY = state.YokeY,
                PilotRudder = state.RudderPedal,
                PilotThrottle = state.Throttle01,
                AlongM = position.AlongTrackM,
                CrossM = position.CrossTrackM,
                DistanceThresholdM = position.DistanceToThresholdM,
                GlideErrorFt = target.AltitudeMslFt - state.AltitudeMslFt,
                OnGround = state.OnGround,
                Stall = state.StallWarning,
                Overspeed = state.OverspeedWarning,
                Paused = state.Paused,
                Slew = state.SlewActive,
                TargetHeadingDeg = target.HeadingTrueDeg,
                TargetIasKts = target.AirspeedKts,
                TargetAltitudeMslFt = target.AltitudeMslFt,
                TargetVerticalSpeedFpm = target.VerticalSpeedFpm,
                TargetBankDeg = MathEx.Clamp(
                    MathEx.HeadingError(target.HeadingTrueDeg, state.HeadingTrueDeg) * 0.45,
                    -(output.Phase == FlightPhase.Final || output.Phase == FlightPhase.Flare ? 12.0 : 22.0),
                    output.Phase == FlightPhase.Final || output.Phase == FlightPhase.Flare ? 12.0 : 22.0),
                Baseline = CommandArray(baseline),
                Command = CommandArray(command),
                RequestedResidual = ActionArray(requested),
                AppliedResidual = ActionArray(applied),
                SafetyScale = trace.SafetyScale,
                Uncertainty = trace.Uncertainty,
                ShieldIntervened = trace.Intervened,
                ResidualRejection = trace.RejectionReason ?? "",
                Terminal = output.Phase == FlightPhase.Completed || output.Phase == FlightPhase.Failsafe,
                Success = output.Phase == FlightPhase.Completed,
                TerminalReason = output.TransitionReason ?? ""
            };
            lock (_sync) _latestObservation = observation;
        }

        public bool ConsumeResetRequest()
        {
            lock (_sync)
            {
                if (!_resetRequested) return false;
                _resetRequested = false;
                _resetStatus = "resetting";
                _latestAction = ResidualAction.Zero();
                _lastAcceptedStepId = -1;
                _shield.Reset();
                return true;
            }
        }

        public void CompleteReset(bool success, string message)
        {
            lock (_sync)
            {
                _resetId++;
                _resetStatus = success ? "ready" : "failed:" + message;
                _latestAction = ResidualAction.Zero();
                _lastAcceptedStepId = -1;
                _shield.Reset();
            }
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                if (!_requestSlots.Wait(0))
                {
                    WriteJson(context.Response, 429, new RlApiResult { Ok = false, Message = "Too many concurrent requests." });
                    continue;
                }
                _ = Task.Run(() =>
                {
                    try { Handle(context); }
                    finally { _requestSlots.Release(); }
                });
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                if (!IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
                {
                    WriteJson(context.Response, 403, new RlApiResult { Ok = false, Message = "Loopback clients only." });
                    return;
                }

                if (context.Request.HttpMethod == "POST"
                    && (context.Request.ContentLength64 < 0 || context.Request.ContentLength64 > _profile.MaxRequestBodyBytes))
                {
                    WriteJson(context.Response, 413, new RlApiResult { Ok = false, Message = "Request body is missing or too large." });
                    return;
                }

                string path = context.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (context.Request.HttpMethod == "GET" && path == "/health")
                {
                    int resetId;
                    lock (_sync) resetId = _resetId;
                    WriteJson(context.Response, 200, new RlApiResult { Ok = true, Message = "P3D RL bridge", ResetId = resetId });
                }
                else if (context.Request.HttpMethod == "GET" && path == "/api/v1/observation")
                {
                    RlObservation observation;
                    lock (_sync) observation = _latestObservation;
                    if (observation == null) WriteJson(context.Response, 503, new RlApiResult { Ok = false, Message = "No telemetry." });
                    else WriteJson(context.Response, 200, observation);
                }
                else if (context.Request.HttpMethod == "POST" && path == "/api/v1/action")
                {
                    var request = ReadJson<RlActionRequest>(context.Request);
                    if (request?.Action == null || request.Action.Length != 4 || !MathEx.AreFinite(request.Action)
                        || Array.Exists(request.Action, value => value < -1.0 || value > 1.0) || request.StepId < 0
                        || !MathEx.AreFinite(request.Uncertainty) || request.Uncertainty < 0.0 || request.Uncertainty > 1.0)
                    {
                        WriteJson(context.Response, 400, new RlApiResult { Ok = false, Message = "Action and uncertainty are invalid." });
                        return;
                    }
                    int statusCode = 200;
                    string message = "Action accepted.";
                    int currentResetId;
                    lock (_sync)
                    {
                        currentResetId = _resetId;
                        if (!string.Equals(_resetStatus, "ready", StringComparison.Ordinal))
                        {
                            statusCode = 409;
                            message = "Episode is not ready for actions.";
                        }
                        else if (request.ResetId != _resetId)
                        {
                            statusCode = 409;
                            message = "Stale reset_id.";
                        }
                        else if (request.StepId <= _lastAcceptedStepId)
                        {
                            statusCode = 409;
                            message = "Out-of-order or replayed step_id.";
                        }
                        else if (!IsKnownStage(request.Stage))
                        {
                            statusCode = 400;
                            message = "Unknown curriculum stage.";
                        }
                        else
                        {
                            _lastAcceptedStepId = request.StepId;
                            _latestAction = new ResidualAction
                            {
                                Aileron = request.Action[0], Elevator = request.Action[1], Rudder = request.Action[2], Throttle = request.Action[3],
                                Stage = request.Stage, StepId = request.StepId, ResetId = request.ResetId,
                                Uncertainty = request.Uncertainty, ReceivedUtc = DateTime.UtcNow
                            };
                        }
                    }
                    WriteJson(context.Response, statusCode, new RlApiResult
                    {
                        Ok = statusCode == 200, Message = message, ResetId = currentResetId
                    });
                }
                else if (context.Request.HttpMethod == "POST" && path == "/api/v1/reset")
                {
                    bool accepted;
                    int resetId;
                    lock (_sync)
                    {
                        resetId = _resetId;
                        accepted = string.Equals(_resetStatus, "ready", StringComparison.Ordinal)
                                   || _resetStatus.StartsWith("failed:", StringComparison.Ordinal);
                        if (accepted)
                        {
                            _resetRequested = true;
                            _resetStatus = "requested";
                            _latestAction = ResidualAction.Zero();
                            _lastAcceptedStepId = -1;
                        }
                    }
                    WriteJson(context.Response, accepted ? 202 : 409, new RlApiResult
                    {
                        Ok = accepted, Message = accepted ? "Reset requested." : "A reset is already pending.", ResetId = resetId
                    });
                }
                else
                {
                    WriteJson(context.Response, 404, new RlApiResult { Ok = false, Message = "Unknown endpoint." });
                }
            }
            catch (Exception ex)
            {
                try { WriteJson(context.Response, 500, new RlApiResult { Ok = false, Message = ex.Message }); }
                catch
                {
                    try { context.Response.Close(); }
                    catch { }
                }
            }
        }

        private static T ReadJson<T>(HttpListenerRequest request)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            return (T)serializer.ReadObject(request.InputStream);
        }

        private static void WriteJson(HttpListenerResponse response, int statusCode, object value)
        {
            var serializer = new DataContractJsonSerializer(value.GetType());
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                byte[] bytes = stream.ToArray();
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            response.Close();
        }

        private static double[] CommandArray(ControlCommand value)
        {
            return new[] { value.Aileron, value.Elevator, value.Rudder, value.Throttle01 };
        }

        private static double[] ActionArray(ResidualAction value)
        {
            return new[] { value.Aileron, value.Elevator, value.Rudder, value.Throttle };
        }

        private static ControlCommand CopyCommand(ControlCommand value)
        {
            return new ControlCommand
            {
                Aileron = value.Aileron, Elevator = value.Elevator, Rudder = value.Rudder,
                Throttle01 = value.Throttle01, Flaps01 = value.Flaps01,
                GearDown = value.GearDown, Brakes01 = value.Brakes01
            };
        }

        private static bool IsKnownStage(string stage)
        {
            string value = (stage ?? "").Trim().ToLowerInvariant();
            return value == "takeoff" || value == "airwork" || value == "pattern" || value == "approach" || value == "full";
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            if (_listener != null)
            {
                _listener.Stop();
                _listener.Close();
                _listener = null;
            }
            try { _serverTask?.Wait(1000); } catch (AggregateException) { }
            _cancellation.Dispose();
        }
    }
}
