using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal static class Program
    {
        private static readonly object ConsoleSync = new object();

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                WriteStatus("Fatal error: " + ex);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            string configPath = ConfigurationLoader.ResolvePath(args);
            AppConfiguration config = ConfigurationLoader.Load(configPath);
            string requestedTransport = ConfigurationLoader.GetOptionValue(args, "--transport");
            if (!string.IsNullOrWhiteSpace(requestedTransport))
            {
                if (string.Equals(requestedTransport, "SimConnect", StringComparison.OrdinalIgnoreCase))
                {
                    config.Simulator.Mode = "SimConnectOnly";
                    config.Simulator.PreferredTransport = "SimConnect";
                }
                else if (string.Equals(requestedTransport, "FSUIPC", StringComparison.OrdinalIgnoreCase))
                {
                    config.Simulator.Mode = "FsuipcOnly";
                    config.Simulator.PreferredTransport = "FSUIPC";
                }
                else throw new InvalidDataException("--transport must be SimConnect or FSUIPC.");
            }
            AircraftProfileCatalog aircraftProfiles = AircraftProfileCatalog.Load(config.AircraftProfiles,
                Path.GetDirectoryName(configPath));
            string requestedAircraftProfile = ConfigurationLoader.GetOptionValue(args, "--aircraft-profile");
            string activeAircraftProfileId = null;
            if (!string.IsNullOrWhiteSpace(requestedAircraftProfile))
            {
                bool explicitMatch;
                AircraftConfigurationProfile selected = aircraftProfiles.Resolve(null, requestedAircraftProfile, out explicitMatch);
                if (selected == null) throw new InvalidDataException("Aircraft profiles are disabled; --aircraft-profile cannot be used.");
                selected.ApplyTo(config);
                activeAircraftProfileId = selected.Id;
            }
            var errors = config.Validate();
            if (errors.Count > 0)
            {
                foreach (string error in errors) WriteStatus("Configuration: " + error);
                return 2;
            }

            if (ConfigurationLoader.HasOption(args, "--diagnostics"))
                return RunStaticDiagnostics(config);
            if (ConfigurationLoader.HasOption(args, "--connection-test"))
                return RunLiveConnectionDiagnostics(config);

            WriteStatus("P3D AI Pilot v4.1.0-beta.1 — hardened Prepar3D v4 / SimConnect + FSUIPC6 / x64");
            WriteStatus("Config: " + configPath);
            WriteStatus("Connection: " + config.Simulator.Mode + ", preferred=" + config.Simulator.PreferredTransport);
            if (activeAircraftProfileId != null) WriteStatus("Aircraft profile forced: " + activeAircraftProfileId);
            WriteStatus("Keys: C=capture runway, R=record, A=arm/start, D=disarm, Q=quit");

            var cancellation = new CancellationTokenSource();
            FlightCsvRecorder recorder = null;
            RunwayFrame capturedRunway = null;
            var engine = new AutopilotEngine(config);
            DateTime lastRecordedTimestamp = DateTime.MinValue;
            DateTime lastDashboard = DateTime.MinValue;
            DateTime lastHealthLog = DateTime.MinValue;
            bool resumeRecordingAfterTransportSwitch = false;
            bool resumeRecordingAfterReset = false;
            bool firstTelemetryReceived = false;
            bool resetInProgress = false;
            string observedAircraftTitle = null;
            bool aircraftProfileBlocked = aircraftProfiles.RequireMatch && activeAircraftProfileId == null;
            DateTime resetStartedUtc = DateTime.MinValue;
            ScenarioFingerprint scenarioFingerprint = null;
            RunwayFrame fingerprintRunway = null;

            using (var simulator = RuntimeCompositionRoot.CreateSimulatorGateway(config))
            using (var llm = new LocalLlmSupervisor(config.Llm))
            using (var rlBridge = new RlBridgeServer(config.Rl))
            using (var healthRecorder = config.Observability.Enabled ? new RuntimeHealthRecorder(config.LogDirectory) : null)
            {
                simulator.StatusChanged += WriteStatus;
                simulator.ActiveTransportChanged += (previous, current) =>
                {
                    bool initialSelection = string.Equals(previous, "None", StringComparison.OrdinalIgnoreCase)
                                            && !string.Equals(current, "None", StringComparison.OrdinalIgnoreCase)
                                            && !firstTelemetryReceived;
                    if (!initialSelection)
                    {
                        engine.Disarm("Simulator transport authority changed from " + previous + " to " + current + ". Re-arm is required.");
                        WriteStatus("SAFETY DISARM: transport authority changed; inspect health and press A to re-arm.");
                    }
                    if (!initialSelection && recorder != null)
                    {
                        string completedPath = recorder.FilePath;
                        recorder.Dispose();
                        recorder = null;
                        lastRecordedTimestamp = DateTime.MinValue;
                        resumeRecordingAfterTransportSwitch = true;
                        WriteStatus("Dataset segment closed before telemetry-source change: " + completedPath);
                    }
                    if (resumeRecordingAfterTransportSwitch && !string.Equals(current, "None", StringComparison.OrdinalIgnoreCase)
                        && !resetInProgress)
                    {
                        recorder = StartRecorder(config);
                        resumeRecordingAfterTransportSwitch = false;
                    }
                };
                llm.DecisionUpdated += d => WriteStatus(string.Format(
                    "LLM [{0:0.00}] {1}: Δhdg={2:0.0}, Δias={3:0.0} — {4}",
                    d.Confidence, d.Intent, d.HeadingDeltaDeg, d.AirspeedDeltaKts, d.Reason));
                llm.StatusChanged += WriteStatus;
                engine.GuidanceOverride = llm.ApplyOverride;
                engine.ControlOverride = rlBridge.ApplyResidual;
                if (healthRecorder != null) WriteStatus("Runtime health: " + healthRecorder.FilePath);

                IList<string> initialConnectionFailures;
                if (!simulator.TryConnectAll(out initialConnectionFailures))
                {
                    WriteStatus("No simulator transport is available yet; retrying for "
                                + config.Simulator.FirstTelemetryTimeoutSeconds.ToString("0")
                                + " seconds. Start P3D and load a scenario. "
                                + string.Join(" | ", initialConnectionFailures));
                }

                // StartPumps must be called even when the first connection
                // attempt failed.  The gateway then starts a pump for every
                // transport that succeeds during its reconnect cycle.
                simulator.StartPumps(cancellation.Token);
                try
                {
                    rlBridge.Start();
                    if (config.Rl.BridgeEnabled) WriteStatus("RL bridge: " + config.Rl.ListenPrefix);
                }
                catch (Exception ex)
                {
                    WriteStatus("RL bridge failed to start: " + ex.Message);
                    return 4;
                }
                if (config.RecordOnStart) recorder = StartRecorder(config);
                var clock = Stopwatch.StartNew();
                long previousTicks = clock.ElapsedTicks;
                int delayMs = Math.Max(1, 1000 / config.ControlLoopHz);
                bool quit = false;
                DateTime connectionStartedUtc = DateTime.UtcNow;
                bool startupTimeoutReported = false;
                bool controlsWereApplied = false;
                string observedScenario = null;

                while (!quit)
                {
                    long currentTicks = clock.ElapsedTicks;
                    double dt = (currentTicks - previousTicks) / (double)Stopwatch.Frequency;
                    previousTicks = currentTicks;
                    dt = MathEx.Clamp(dt, 0.005, 0.20);
                    DateTime cycleUtc = DateTime.UtcNow;
                    AircraftState state = simulator.GetLatestState(cycleUtc);
                    SimulatorGatewayHealth cycleHealth = simulator.GetHealth(cycleUtc);
                    SimulatorTransportHealth selectedHealth = null;
                    foreach (var item in cycleHealth.Transports)
                        if (item.Name == cycleHealth.ActiveTransport) selectedHealth = item;
                    if (selectedHealth != null)
                    {
                        string scenario = selectedHealth.Name + "|" + selectedHealth.ScenarioPath + "|" + selectedHealth.ScenarioLoadCount;
                        if (observedScenario != null && observedScenario != scenario && !resetInProgress)
                        {
                            engine.Disarm("Simulator scenario changed.");
                            simulator.ReleaseControls("scenario changed");
                            capturedRunway = null;
                            scenarioFingerprint = null;
                            fingerprintRunway = null;
                            WriteStatus("Scenario changed; capture the runway and explicitly activate again.");
                        }
                        observedScenario = scenario;
                    }
                    if (state != null && aircraftProfiles.Enabled
                        && string.IsNullOrWhiteSpace(requestedAircraftProfile)
                        && !string.IsNullOrWhiteSpace(state.AircraftTitle)
                        && !string.Equals(observedAircraftTitle, state.AircraftTitle, StringComparison.Ordinal))
                    {
                        observedAircraftTitle = state.AircraftTitle;
                        bool titleMatched;
                        AircraftConfigurationProfile selected = aircraftProfiles.Resolve(state.AircraftTitle, null, out titleMatched);
                        if (selected == null)
                        {
                            aircraftProfileBlocked = true;
                            engine.Disarm("No approved profile matches aircraft TITLE " + state.AircraftTitle + ".");
                            WriteStatus("AIRCRAFT PROFILE BLOCKED: no match for TITLE=\"" + state.AircraftTitle + "\".");
                        }
                        else if (!string.Equals(activeAircraftProfileId, selected.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            aircraftProfileBlocked = false;
                            engine.Disarm("Aircraft profile changed.");
                            selected.ApplyTo(config);
                            engine = new AutopilotEngine(config);
                            engine.GuidanceOverride = llm.ApplyOverride;
                            engine.ControlOverride = rlBridge.ApplyResidual;
                            activeAircraftProfileId = selected.Id;
                            capturedRunway = null;
                            scenarioFingerprint = null;
                            fingerprintRunway = null;
                            WriteStatus("Aircraft TITLE=\"" + state.AircraftTitle + "\" -> profile=" + selected.Id
                                + (titleMatched ? "" : " (default fallback)") + ", status=" + selected.Status + ".");
                        }
                        else aircraftProfileBlocked = false;
                    }
                    if (state != null) firstTelemetryReceived = true;
                    if (cycleHealth.ControlInhibited && engine.IsArmed)
                    {
                        engine.Disarm("Persistent SimConnect/FSUIPC telemetry divergence; control inhibited.");
                        WriteStatus("SAFETY DISARM: redundant telemetry exceeded divergence limits.");
                    }
                    if (!firstTelemetryReceived && !startupTimeoutReported
                        && (DateTime.UtcNow - connectionStartedUtc).TotalSeconds > config.Simulator.FirstTelemetryTimeoutSeconds)
                    {
                        string reportPath = WriteAutomaticDiagnostic(config);
                        WriteStatus("No telemetry arrived before timeout. Diagnostic: " + reportPath);
                        startupTimeoutReported = true;
                        engine.Disarm("No initial simulator telemetry; standby recovery.");
                        WriteStatus("Remaining in standby; reconnect continues. Press Q to exit.");
                    }

                    HandleKeys(ref quit, ref capturedRunway, ref recorder, state, engine, config,
                        cycleHealth.ControlInhibited, aircraftProfileBlocked, observedAircraftTitle);
                    if (state != null && capturedRunway != null && !object.ReferenceEquals(capturedRunway, fingerprintRunway))
                    {
                        scenarioFingerprint = ScenarioFingerprint.Capture(state);
                        fingerprintRunway = capturedRunway;
                        WriteStatus("Scenario fingerprint captured for deterministic reset validation.");
                    }
                    if (rlBridge.ConsumeResetRequest())
                    {
                        if (capturedRunway == null || scenarioFingerprint == null)
                        {
                            rlBridge.CompleteReset(false, "Capture a runway with C before RL reset.");
                        }
                        else
                        {
                            engine.Disarm("RL episode reset.");
                            if (recorder != null)
                            {
                                string completedPath = recorder.FilePath;
                                recorder.Dispose();
                                recorder = null;
                                lastRecordedTimestamp = DateTime.MinValue;
                                resumeRecordingAfterReset = true;
                                WriteStatus("Episode recording closed before reset: " + completedPath);
                            }
                            try
                            {
                                simulator.ResetScenario();
                                resetInProgress = true;
                                resetStartedUtc = DateTime.UtcNow;
                                WriteStatus("RL requested scenario reset.");
                            }
                            catch (Exception ex)
                            {
                                rlBridge.CompleteReset(false, "Scenario reset command failed: " + ex.Message);
                                resumeRecordingAfterReset = false;
                                WriteStatus("RL reset failed: " + ex.Message);
                            }
                        }
                    }

                    if (state != null)
                    {
                        if (resetInProgress)
                        {
                            double resetAge = (DateTime.UtcNow - resetStartedUtc).TotalSeconds;
                            if (resetAge >= config.Rl.ResetSettlingSeconds && state.OnGround && state.GroundSpeedKts < 5.0)
                            {
                                string validationReason;
                                bool valid = scenarioFingerprint.Validate(state, out validationReason);
                                string armReason = validationReason;
                                bool redundancySafe = !cycleHealth.ControlInhibited;
                                if (!redundancySafe) armReason = "Redundant telemetry is divergent; control is inhibited.";
                                bool armed = valid && redundancySafe && engine.Arm(state, capturedRunway, out armReason);
                                rlBridge.CompleteReset(armed, valid ? armReason : validationReason);
                                resetInProgress = false;
                                if (armed && (resumeRecordingAfterReset || resumeRecordingAfterTransportSwitch))
                                {
                                    recorder = StartRecorder(config);
                                    lastRecordedTimestamp = DateTime.MinValue;
                                }
                                resumeRecordingAfterReset = false;
                                resumeRecordingAfterTransportSwitch = false;
                                WriteStatus("RL reset: " + (valid ? armReason : validationReason));
                            }
                            else if (resetAge > 30.0)
                            {
                                rlBridge.CompleteReset(false, "Scenario did not settle on the ground within 30 seconds.");
                                resetInProgress = false;
                                resumeRecordingAfterReset = false;
                                resumeRecordingAfterTransportSwitch = false;
                            }
                        }

                        engine.ApplySupervisorIntent(llm.GetValidatedIntent(engine.Phase));
                        AutopilotOutput output = engine.Update(state, dt, DateTime.UtcNow);
                        if (output.Targets != null) llm.Observe(output.Phase, state, output.Targets);
                        if (output.ApplyControls) simulator.Send(output.Command);
                        else if (controlsWereApplied) simulator.ReleaseControls("controller disarmed");
                        controlsWereApplied = output.ApplyControls;
                        rlBridge.Publish(state, output);

                        if (recorder != null && state.UtcTimestamp != lastRecordedTimestamp)
                        {
                            recorder.Append(state, output, llm.LastDecision, rlBridge.LastTrace);
                            lastRecordedTimestamp = state.UtcTimestamp;
                        }

                        SimulatorGatewayHealth gatewayHealth = cycleHealth;
                        if ((DateTime.UtcNow - lastDashboard).TotalSeconds >= config.Observability.ConsoleDashboardIntervalSeconds)
                        {
                            PrintDashboard(state, output, recorder != null, gatewayHealth);
                            lastDashboard = DateTime.UtcNow;
                        }
                        if (healthRecorder != null
                            && (DateTime.UtcNow - lastHealthLog).TotalSeconds >= config.Observability.HealthLogIntervalSeconds)
                        {
                            healthRecorder.Append(state, output, gatewayHealth, recorder != null,
                                llm.LastDecision, llm.GetHealth(), rlBridge.LastTrace);
                            lastHealthLog = DateTime.UtcNow;
                        }
                    }

                    double workMs = (clock.ElapsedTicks - currentTicks) * 1000.0 / Stopwatch.Frequency;
                    int remainingMs = (int)Math.Floor(delayMs - workMs);
                    if (remainingMs > 0) Thread.Sleep(remainingMs);
                }

                engine.Disarm("Program exit.");
                simulator.ReleaseControls("program exit");
                cancellation.Cancel();
                simulator.WaitForPumps(1000);
                recorder?.Dispose();
            }

            cancellation.Dispose();
            WriteStatus("Stopped. Controls returned to the user.");
            return 0;
        }

        private static void HandleKeys(
            ref bool quit,
            ref RunwayFrame runway,
            ref FlightCsvRecorder recorder,
            AircraftState state,
            AutopilotEngine engine,
            AppConfiguration config,
            bool controlInhibited,
            bool aircraftProfileBlocked,
            string aircraftTitle)
        {
            if (Console.IsInputRedirected || !Console.KeyAvailable) return;
            ConsoleKey key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.C:
                    if (state == null) { WriteStatus("No telemetry yet."); break; }
                    if (!state.OnGround) { WriteStatus("Runway capture is allowed only on the ground."); break; }
                    runway = new RunwayFrame
                    {
                        ThresholdLatitudeDeg = state.LatitudeDeg,
                        ThresholdLongitudeDeg = state.LongitudeDeg,
                        ThresholdElevationFt = state.AltitudeMslFt - state.AltitudeAglFt,
                        HeadingTrueDeg = state.HeadingTrueDeg,
                        LengthM = 2000.0,
                        WidthM = 45.0
                    };
                    WriteStatus(string.Format("Runway captured: {0:0.000000}, {1:0.000000}, heading {2:0.0}° true.",
                        runway.ThresholdLatitudeDeg, runway.ThresholdLongitudeDeg, runway.HeadingTrueDeg));
                    break;

                case ConsoleKey.A:
                    if (state == null) { WriteStatus("No telemetry yet."); break; }
                    if (aircraftProfileBlocked)
                    {
                        WriteStatus("Arm rejected: no approved aircraft profile for TITLE=\""
                            + (aircraftTitle ?? "not received") + "\". Add a matching profile or use --aircraft-profile explicitly.");
                        break;
                    }
                    if (controlInhibited)
                    {
                        WriteStatus("Arm rejected: redundant telemetry is divergent and control is inhibited.");
                        break;
                    }
                    string reason;
                    if (engine.Arm(state, runway, out reason)) WriteStatus(reason);
                    else WriteStatus("Arm rejected: " + reason);
                    break;

                case ConsoleKey.D:
                    engine.Disarm("Manual disarm key.");
                    WriteStatus("DISARMED — move the joystick to take control.");
                    break;

                case ConsoleKey.R:
                    if (recorder == null) recorder = StartRecorder(config);
                    else
                    {
                        string path = recorder.FilePath;
                        recorder.Dispose();
                        recorder = null;
                        WriteStatus("Recording stopped: " + path);
                    }
                    break;

                case ConsoleKey.Q:
                    quit = true;
                    break;
            }
        }

        private static FlightCsvRecorder StartRecorder(AppConfiguration config)
        {
            var recorder = new FlightCsvRecorder(config.LogDirectory);
            WriteStatus("Recording: " + recorder.FilePath);
            return recorder;
        }

        private static void PrintDashboard(AircraftState state, AutopilotOutput output, bool recording, SimulatorGatewayHealth health)
        {
            double cross = output.RunwayPosition == null ? 0.0 : output.RunwayPosition.CrossTrackM;
            string line = string.Format(
                "{0,-12} IAS {1,6:0.0}  AGL {2,6:0}  VS {3,6:0}  HDG {4,6:0.0}  BANK {5,6:0.0}  XTK {6,7:0}m  {7}  LINK={8}",
                output.Phase, state.IndicatedAirspeedKts, state.AltitudeAglFt, state.VerticalSpeedFpm,
                state.HeadingTrueDeg, state.BankDeg, cross, recording ? "REC" : "---",
                health == null ? state.TelemetrySource : health.ActiveTransport);
            WriteStatus(line);
        }

        private static int RunStaticDiagnostics(AppConfiguration config)
        {
            ConnectionDiagnosticReport report = ConnectionDiagnostics.Inspect(config);
            string path = ConnectionDiagnostics.WriteJson(report, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics"));
            WriteStatus("x64 process: " + report.Is64BitProcess);
            WriteStatus("Prepar3D: " + (report.Prepar3DPath ?? "NOT FOUND") + " version=" + (report.Prepar3DVersion ?? "unknown"));
            WriteStatus("Prepar3D process running: " + report.Prepar3DProcessRunning);
            WriteStatus("SimConnect managed DLL: " + (report.SimConnectManagedDllExists ? report.SimConnectManagedDll : "NOT FOUND"));
            WriteStatus("FSUIPC5/6: " + (report.FsuipcPath ?? "NOT FOUND") + " version=" + (report.FsuipcVersion ?? "unknown"));
            WriteStatus("FSUIPC module loaded in P3D: " + (report.FsuipcModuleLoadedInPrepar3D.HasValue
                ? report.FsuipcModuleLoadedInPrepar3D.Value.ToString() : "UNKNOWN"));
            foreach (string finding in report.Findings) WriteStatus(finding);
            WriteStatus("Diagnostic report: " + path);
            foreach (string finding in report.Findings)
                if (finding.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return 10;
            return 0;
        }

        private static int RunLiveConnectionDiagnostics(AppConfiguration config)
        {
            double durationSeconds = Math.Min(30.0, Math.Max(5.0, config.Simulator.FirstTelemetryTimeoutSeconds));
            var cancellation = new CancellationTokenSource();
            SimulatorGatewayHealth health;
            AircraftState latestState = null;
            using (var simulator = RuntimeCompositionRoot.CreateSimulatorGateway(config))
            {
                simulator.StatusChanged += WriteStatus;
                try
                {
                    IList<string> initialConnectionFailures;
                    if (!simulator.TryConnectAll(out initialConnectionFailures))
                    {
                        WriteStatus("Initial live connection attempt failed; retrying during the test window. "
                                    + string.Join(" | ", initialConnectionFailures));
                    }
                    simulator.StartPumps(cancellation.Token);
                    DateTime started = DateTime.UtcNow;
                    while ((DateTime.UtcNow - started).TotalSeconds < durationSeconds)
                    {
                        latestState = simulator.GetLatestState(DateTime.UtcNow);
                        health = simulator.GetHealth(DateTime.UtcNow);
                        bool allFresh = health.Transports.Count > 0;
                        foreach (SimulatorTransportHealth item in health.Transports)
                            allFresh &= item.IsFresh(DateTime.UtcNow, config.Simulator.TelemetryStaleSeconds);
                        // Observe the full test window; a single early sample is not stability evidence.
                        if (allFresh && latestState == null) WriteStatus("Transport reports fresh data but no selected state.");
                        Thread.Sleep(100);
                    }
                    health = simulator.GetHealth(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    WriteStatus("Live connection test error: " + ex.Message);
                    health = simulator.GetHealth(DateTime.UtcNow);
                }
                finally
                {
                    cancellation.Cancel();
                    simulator.WaitForPumps(1500);
                }
            }
            cancellation.Dispose();

            bool operational = false;
            bool fullyRedundant = health.Transports.Count > 0;
            var transports = new List<LiveTransportDiagnostic>();
            foreach (SimulatorTransportHealth item in health.Transports)
            {
                bool fresh = item.IsFresh(DateTime.UtcNow, config.Simulator.TelemetryStaleSeconds);
                operational |= fresh;
                fullyRedundant &= fresh;
                transports.Add(new LiveTransportDiagnostic
                {
                    Name = item.Name, Connected = item.IsConnected, Fresh = fresh,
                    Samples = item.TelemetrySamples, RejectedSamples = item.RejectedTelemetrySamples,
                    ConnectionAttempts = item.ReconnectAttempts, ConsecutiveFailures = item.ConsecutiveFailures,
                    Version = item.Version, LastError = item.LastError, ScenarioPath = item.ScenarioPath,
                    ScenarioLoadCount = item.ScenarioLoadCount, UpstreamReconnectCount = item.UpstreamReconnectCount
                });
            }
            var report = new LiveConnectionDiagnosticReport
            {
                Schema = "live-connection-diagnostic-v1", Utc = DateTime.UtcNow,
                TestDurationSeconds = durationSeconds, ActiveTransport = health.ActiveTransport,
                AircraftTitle = latestState == null ? "" : latestState.AircraftTitle,
                Operational = operational, FullyRedundant = fullyRedundant,
                DivergenceDetected = health.DivergenceDetected, Transports = transports
            };
            string path = ConnectionDiagnostics.WriteLiveJson(report,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics"));
            foreach (LiveTransportDiagnostic item in transports)
                WriteStatus(item.Name + ": connected=" + item.Connected + " fresh=" + item.Fresh
                            + " samples=" + item.Samples + " rejected=" + item.RejectedSamples
                            + " error=" + (item.LastError ?? ""));
            WriteStatus("Live diagnostic report: " + path);
            WriteStatus("Detected aircraft TITLE: " + (latestState == null || string.IsNullOrWhiteSpace(latestState.AircraftTitle)
                ? "NOT AVAILABLE" : latestState.AircraftTitle));
            if (fullyRedundant) return 0;
            return operational ? 12 : 13;
        }

        private static string WriteAutomaticDiagnostic(AppConfiguration config)
        {
            ConnectionDiagnosticReport report = ConnectionDiagnostics.Inspect(config);
            return ConnectionDiagnostics.WriteJson(report, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics"));
        }

        private static void WriteStatus(string message)
        {
            lock (ConsoleSync) Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        }
    }
}
