using System;
using System.Diagnostics;
using System.Threading;
using FSUIPC;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal sealed class FsuipcSimulatorClient : ISimulatorTransport, ISimulatorControlRelease
    {
        private const double FeetPerMetre = 3.28083989501312;
        private const double KnotsPerMetrePerSecond = 1.9438444924406;
        private const double RadiansToDegrees = 180.0 / Math.PI;
        private readonly Stopwatch _commandAge = new Stopwatch();
        private bool _controlsApplied;
        private readonly object _sync = new object();
        private readonly FsuipcProfile _profile;
        private readonly string _group;

        private readonly Offset<double> _simTime;
        private readonly Offset<FsLatitude> _latitude;
        private readonly Offset<FsLongitude> _longitude;
        private readonly Offset<long> _altitude;
        private readonly Offset<int> _radioAltitude;
        private readonly Offset<int> _groundSpeed;
        private readonly Offset<int> _trueAirspeed;
        private readonly Offset<int> _indicatedAirspeed;
        private readonly Offset<int> _verticalSpeed;
        private readonly Offset<int> _pitch;
        private readonly Offset<int> _bank;
        private readonly Offset<uint> _heading;
        private readonly Offset<double> _alpha;
        private readonly Offset<double> _beta;
        private readonly Offset<double> _pitchRate;
        private readonly Offset<double> _rollRate;
        private readonly Offset<double> _yawRate;
        private readonly Offset<double> _accelerationLateral;
        private readonly Offset<double> _accelerationVertical;
        private readonly Offset<double> _accelerationLongitudinal;
        private readonly Offset<ushort> _windSpeed;
        private readonly Offset<ushort> _windDirection;
        private readonly Offset<short> _engineN1OrRpm;
        private readonly Offset<ushort> _engineRpmScaler;
        private readonly Offset<double> _weight;
        private readonly Offset<double> _cgFraction;
        private readonly Offset<short> _yokeY;
        private readonly Offset<short> _yokeX;
        private readonly Offset<short> _rudder;
        private readonly Offset<short> _throttle;
        private readonly Offset<int> _flaps;
        private readonly Offset<double> _designVs0FeetPerSecond;
        private readonly Offset<double> _designVs1FeetPerSecond;
        private readonly Offset<double> _designCruiseFeetPerSecond;
        private readonly Offset<byte> _engineType;
        private readonly Offset<ushort> _engineCount;
        private readonly Offset<short> _onGround;
        private readonly Offset<short> _parkingBrake;
        private readonly Offset<byte> _stallWarning;
        private readonly Offset<byte> _overspeedWarning;
        private readonly Offset<short> _slewActive;
        private readonly Offset<short> _paused;
        private readonly Offset<string> _aircraftTitle;
        private readonly Offset<string> _scenarioPath;
        private readonly Offset<ushort> _scenarioLoadCount;
        private readonly Offset<ushort> _upstreamReconnectCount;
        private readonly Offset<byte> _loading;
        private readonly Offset<byte> _inMenu;

        private AircraftState _latestState;
        private volatile bool _connected;
        private volatile bool _disposed;
        private DateTime _connectedUtc;
        private DateTime _lastTelemetryUtc;
        private DateTime _lastCommandUtc;
        private DateTime _lastPayloadRefreshUtc;
        private DateTime _lastPayloadWarningUtc;
        private long _telemetrySamples;
        private long _rejectedTelemetrySamples;
        private long _commandsSent;
        private long _reconnectAttempts;
        private int _consecutiveFailures;
        private string _version = "unknown";
        private string _lastError = "";
        private double _fuelFraction;
        private double _maximumGrossWeightLb;
        private double _lastFlaps = double.NaN;
        private bool? _lastGear;

        public FsuipcSimulatorClient(FsuipcProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _group = profile.OffsetGroup + "-" + Guid.NewGuid().ToString("N");
            FSUIPCConnection.GroupsIsolatedToThread = false;
            _simTime = new Offset<double>(_group, 0x0310);
            _latitude = new Offset<FsLatitude>(_group, 0x0560);
            _longitude = new Offset<FsLongitude>(_group, 0x0568);
            _altitude = new Offset<long>(_group, 0x0570);
            _radioAltitude = new Offset<int>(_group, 0x31E4);
            _groundSpeed = new Offset<int>(_group, 0x02B4);
            _trueAirspeed = new Offset<int>(_group, 0x02B8);
            _indicatedAirspeed = new Offset<int>(_group, 0x02BC);
            _verticalSpeed = new Offset<int>(_group, 0x02C8);
            _pitch = new Offset<int>(_group, 0x0578);
            _bank = new Offset<int>(_group, 0x057C);
            _heading = new Offset<uint>(_group, 0x0580);
            _alpha = new Offset<double>(_group, 0x2ED0);
            _beta = new Offset<double>(_group, 0x2ED8);
            _pitchRate = new Offset<double>(_group, 0x30A8);
            _rollRate = new Offset<double>(_group, 0x30B0);
            _yawRate = new Offset<double>(_group, 0x30B8);
            _accelerationLateral = new Offset<double>(_group, 0x3060);
            _accelerationVertical = new Offset<double>(_group, 0x3068);
            _accelerationLongitudinal = new Offset<double>(_group, 0x3070);
            _windSpeed = new Offset<ushort>(_group, 0x0E90);
            _windDirection = new Offset<ushort>(_group, 0x0E92);
            _engineN1OrRpm = new Offset<short>(_group, 0x0898);
            _engineRpmScaler = new Offset<ushort>(_group, 0x08C8);
            _weight = new Offset<double>(_group, 0x30C0);
            _cgFraction = new Offset<double>(_group, 0x2EF8);
            _yokeY = new Offset<short>(_group, 0x0BB2);
            _yokeX = new Offset<short>(_group, 0x0BB6);
            _rudder = new Offset<short>(_group, 0x0BBA);
            _throttle = new Offset<short>(_group, 0x088C);
            _flaps = new Offset<int>(_group, 0x0BDC);
            _designVs0FeetPerSecond = new Offset<double>(_group, 0x0538);
            _designVs1FeetPerSecond = new Offset<double>(_group, 0x0540);
            _designCruiseFeetPerSecond = new Offset<double>(_group, 0x0548);
            _engineType = new Offset<byte>(_group, 0x0609);
            _engineCount = new Offset<ushort>(_group, 0x0AEC);
            _onGround = new Offset<short>(_group, 0x0366);
            _parkingBrake = new Offset<short>(_group, 0x0BC8);
            _stallWarning = new Offset<byte>(_group, 0x036C);
            _overspeedWarning = new Offset<byte>(_group, 0x036D);
            _slewActive = new Offset<short>(_group, 0x05DC);
            _paused = new Offset<short>(_group, 0x0264);
            _aircraftTitle = new Offset<string>(_group, 0x3D00, 256);
            _scenarioPath = new Offset<string>(_group, 0x3F04, 252);
            _scenarioLoadCount = new Offset<ushort>(_group, 0x3F02);
            _upstreamReconnectCount = new Offset<ushort>(_group, 0x3BF6);
            _loading = new Offset<byte>(_group, 0x3364);
            _inMenu = new Offset<byte>(_group, 0x3365);
        }

        public string Name { get { return "FSUIPC"; } }
        public event Action<string> StatusChanged;

        public void Connect()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_connected) return;
                _reconnectAttempts++;
                try
                {
                    if (FSUIPCConnection.IsOpen) FSUIPCConnection.Close();
                    // Open without a version filter first so an installed but
                    // incompatible FSUIPC/P3D combination produces a precise
                    // version diagnosis instead of the generic open failure.
                    FSUIPCConnection.Open(FlightSim.Any);
                    FsVersion connectedSimulator = FSUIPCConnection.FlightSimVersionConnected;
                    if (_profile.RequireP3Dx64 && connectedSimulator.Simulator != FlightSim.Prepar3dx64)
                        throw new InvalidOperationException("FSUIPC is connected to " + connectedSimulator
                            + ", but this profile requires 64-bit Prepar3D v4 (Prepar3dx64)."
                            + " Use FSUIPC5 or FSUIPC6 matching the installed P3D v4 release.");
                    _connectedUtc = DateTime.UtcNow;
                    _lastTelemetryUtc = DateTime.MinValue;
                    _latestState = null;
                    _lastFlaps = double.NaN;
                    _lastGear = null;
                    _lastPayloadRefreshUtc = DateTime.MinValue;
                    _fuelFraction = 0;
                    _maximumGrossWeightLb = 0;
                    _consecutiveFailures = 0;
                    _lastError = "";

                    // Open() only establishes the IPC handle.  Process one
                    // complete group before advertising a healthy connection,
                    // so architecture/version/group errors are reported as
                    // connection failures instead of a misleading
                    // "connected; waiting for telemetry" state.
                    FSUIPCConnection.Process(_group);
                    RefreshPayloadIfDue();
                    AircraftState initial = CreateState();
                    if (!IsPlausible(initial))
                        throw new InvalidOperationException("FSUIPC opened but returned an invalid initial telemetry sample.");

                    _latestState = initial;
                    _lastTelemetryUtc = initial.UtcTimestamp;
                    _connected = true;
                    _version = FSUIPCConnection.FSUIPCVersion.ToString() + " / client " + FSUIPCConnection.DLLVersion
                               + " / sim " + connectedSimulator;
                    RaiseStatus("Connected through FSUIPC " + _version + ".");
                }
                catch (Exception ex)
                {
                    var fsuipcError = ex as FSUIPCException;
                    string detail = fsuipcError == null ? ex.Message
                        : fsuipcError.FSUIPCErrorCode + ": " + fsuipcError.Message;
                    _connected = false;
                    _consecutiveFailures++;
                    _lastError = detail;
                    if (FSUIPCConnection.IsOpen)
                    {
                        try { FSUIPCConnection.Close(); } catch (Exception cleanupError) { RaiseStatus("FSUIPC failed-open cleanup: " + cleanupError.Message); }
                    }
                    throw new InvalidOperationException("FSUIPC connection/handshake failed: " + detail
                        + " Check that P3D has a fully loaded scenario, FSUIPC is shown in Add-ons,"
                        + " and both processes run at the same Windows elevation level.", ex);
                }
            }
        }

        public void Pump(CancellationToken token)
        {
            int delayMs = Math.Max(10, 1000 / Math.Max(1, _profile.PollHz));
            while (!token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    lock (_sync)
                    {
                        if (!_connected) break;
                        if (_controlsApplied && _commandAge.ElapsedMilliseconds >= 500)
                            ReleaseControls("command timeout (500 ms)");
                        ushort oldLoadCount = _scenarioLoadCount.Value;
                        FSUIPCConnection.Process(_group);
                        if (_scenarioLoadCount.Value != oldLoadCount)
                        {
                            ReleaseControls("scenario loaded or reloaded");
                            _lastGear = null;
                            _lastFlaps = double.NaN;
                            _lastPayloadRefreshUtc = DateTime.MinValue;
                            RaiseStatus("FSUIPC scenario changed: " + (_scenarioPath.Value ?? "").TrimEnd('\0', ' ')
                                + " loadCount=" + _scenarioLoadCount.Value
                                + " upstreamReconnectCount=" + _upstreamReconnectCount.Value);
                        }
                        RefreshPayloadIfDue();
                        AircraftState candidate = CreateState();
                        if (candidate.Paused || candidate.SlewActive) ReleaseControls("simulator paused or in slew");
                        if (_latestState != null && _latestState.AircraftTitle != candidate.AircraftTitle)
                        {
                            ReleaseControls("aircraft changed");
                            _lastFlaps = double.NaN;
                            _lastGear = null;
                            _lastPayloadRefreshUtc = DateTime.MinValue;
                            _fuelFraction = 0;
                            _maximumGrossWeightLb = 0;
                        }
                        if (!IsPlausible(candidate))
                        {
                            _rejectedTelemetrySamples++;
                            _consecutiveFailures++;
                            _lastError = "Rejected invalid FSUIPC telemetry sample.";
                            if (_consecutiveFailures >= 5)
                                throw new InvalidOperationException("Five consecutive invalid FSUIPC telemetry samples.");
                        }
                        else
                        {
                            _latestState = candidate;
                            _lastTelemetryUtc = candidate.UtcTimestamp;
                            _lastError = "";
                            _consecutiveFailures = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        ReleaseControls("polling failure");
                        _lastError = ex.Message;
                        _connected = false;
                    }
                    RaiseStatus("FSUIPC polling stopped: " + ex.Message);
                    break;
                }
                if (token.WaitHandle.WaitOne(delayMs)) break;
            }
        }

        public void Disconnect(string reason)
        {
            lock (_sync)
            {
                ReleaseControls(reason);
                _connected = false;
                _latestState = null;
                if (!string.IsNullOrWhiteSpace(reason)) _lastError = reason;
                if (FSUIPCConnection.IsOpen) FSUIPCConnection.Close();
            }
        }

        public AircraftState GetLatestState()
        {
            lock (_sync) return _latestState;
        }

        public SimulatorTransportHealth GetHealth()
        {
            lock (_sync)
            {
                return new SimulatorTransportHealth
                {
                    Name = Name,
                    IsConnected = _connected && !_disposed,
                    HasTelemetry = _latestState != null,
                    ConnectedUtc = _connectedUtc,
                    LastTelemetryUtc = _lastTelemetryUtc,
                    LastCommandUtc = _lastCommandUtc,
                    TelemetrySamples = _telemetrySamples,
                    RejectedTelemetrySamples = _rejectedTelemetrySamples,
                    CommandsSent = _commandsSent,
                    ReconnectAttempts = _reconnectAttempts,
                    ConsecutiveFailures = _consecutiveFailures,
                    Version = _version,
                    LastError = _lastError,
                    SimulationPaused = _paused.Value != 0 || _loading.Value != 0 || _inMenu.Value != 0,
                    ScenarioPath = (_scenarioPath.Value ?? "").TrimEnd('\0', ' '),
                    ScenarioLoadCount = _scenarioLoadCount.Value,
                    UpstreamReconnectCount = _upstreamReconnectCount.Value,
                    Capabilities = SimulatorCapabilities.Telemetry | SimulatorCapabilities.Controls
                                   | SimulatorCapabilities.ScenarioReset | SimulatorCapabilities.Payload
                };
            }
        }

        public void Send(ControlCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!command.IsFinite()) throw new ArgumentException("Control command contains a non-finite value.", nameof(command));
            command.Clamp();
            lock (_sync)
            {
                try
                {
                    EnsureConnected();
                    if (_latestState == null || _latestState.Paused || _latestState.SlewActive
                        || (DateTime.UtcNow - _lastTelemetryUtc).TotalSeconds > 1)
                        throw new InvalidOperationException("FSUIPC controls require fresh, unpaused telemetry.");
                    _controlsApplied = true;
                    _commandAge.Restart();
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_AILERONS_SET, SignedAxis(command.Aileron));
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_ELEVATOR_SET, SignedAxis(command.Elevator));
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_RUDDER_SET, SignedAxis(command.Rudder));
                    FSUIPCConnection.SendControlToFS(FsControl.THROTTLE_SET, UnsignedAxis(command.Throttle01));
                    if (double.IsNaN(_lastFlaps) || Math.Abs(command.Flaps01 - _lastFlaps) >= 0.02)
                    {
                        FSUIPCConnection.SendControlToFS(FsControl.AXIS_FLAPS_SET, AxisRange01(command.Flaps01));
                        _lastFlaps = command.Flaps01;
                    }
                    if (!_lastGear.HasValue || _lastGear.Value != command.GearDown)
                    {
                        FSUIPCConnection.SendControlToFS(FsControl.GEAR_SET, command.GearDown ? 1 : 0);
                        _lastGear = command.GearDown;
                    }
                    int brake = AxisRange01(command.Brakes01);
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_LEFT_BRAKE_SET, brake);
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_RIGHT_BRAKE_SET, brake);
                    _lastCommandUtc = DateTime.UtcNow;
                    _commandsSent++;
                }
                catch (Exception ex)
                {
                    ReleaseControls("control write failure");
                    _connected = false;
                    _consecutiveFailures++;
                    _lastError = ex.Message;
                    throw;
                }
            }
        }

        public void ReleaseControls(string reason)
        {
            lock (_sync)
            {
                if (!_controlsApplied) return;
                _controlsApplied = false;
                _commandAge.Stop();
                if (!FSUIPCConnection.IsOpen) return;
                try
                {
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_AILERONS_SET, 0);
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_ELEVATOR_SET, 0);
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_RUDDER_SET, 0);
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_LEFT_BRAKE_SET, AxisRange01(0));
                    FSUIPCConnection.SendControlToFS(FsControl.AXIS_RIGHT_BRAKE_SET, AxisRange01(0));
                    RaiseStatus("FSUIPC AI axes released: " + reason);
                }
                catch (Exception ex) { RaiseStatus("FSUIPC AI release unavailable: " + ex.Message); }
            }
        }

        public void ResetScenario()
        {
            lock (_sync)
            {
                EnsureConnected();
                FSUIPCConnection.SendControlToFS(FsControl.SITUATION_RESET, 0);
            }
        }

        private AircraftState CreateState()
        {
            long sequence = ++_telemetrySamples;
            double altitudeM = _altitude.Value / 4294967296.0;
            double pitch = -SignedAngle(_pitch.Value);
            double bank = -SignedAngle(_bank.Value);
            double rpm = _engineN1OrRpm.Value * _engineRpmScaler.Value / 65536.0;
            return new AircraftState
            {
                TelemetrySource = Name,
                AircraftTitle = (_aircraftTitle.Value ?? "").TrimEnd('\0', ' '),
                TelemetrySequence = sequence,
                UtcTimestamp = DateTime.UtcNow,
                SimTimeSeconds = _simTime.Value,
                LatitudeDeg = _latitude.Value.DecimalDegrees,
                LongitudeDeg = _longitude.Value.DecimalDegrees,
                AltitudeMslFt = altitudeM * FeetPerMetre,
                AltitudeAglFt = Math.Max(0.0, _radioAltitude.Value / 65536.0 * FeetPerMetre),
                IndicatedAirspeedKts = _indicatedAirspeed.Value / 128.0,
                TrueAirspeedKts = _trueAirspeed.Value / 128.0,
                GroundSpeedKts = _groundSpeed.Value / 65536.0 * KnotsPerMetrePerSecond,
                VerticalSpeedFpm = _verticalSpeed.Value * 60.0 * FeetPerMetre / 256.0,
                PitchDeg = pitch,
                BankDeg = bank,
                HeadingTrueDeg = MathEx.NormalizeHeading(_heading.Value * 360.0 / 4294967296.0),
                AngleOfAttackDeg = OptionalValue(_alpha.Value, "alpha") * RadiansToDegrees,
                SideSlipDeg = OptionalValue(_beta.Value, "beta") * RadiansToDegrees,
                BodyRatePDegPerSecond = OptionalValue(_rollRate.Value, "rollRate") * RadiansToDegrees,
                BodyRateQDegPerSecond = OptionalValue(_pitchRate.Value, "pitchRate") * RadiansToDegrees,
                BodyRateRDegPerSecond = OptionalValue(_yawRate.Value, "yawRate") * RadiansToDegrees,
                AccelerationXG = OptionalValue(_accelerationLateral.Value, "accelerationLateral") / 32.174,
                AccelerationYG = OptionalValue(_accelerationVertical.Value, "accelerationVertical") / 32.174,
                AccelerationZG = OptionalValue(_accelerationLongitudinal.Value, "accelerationLongitudinal") / 32.174,
                WindSpeedKts = _windSpeed.Value,
                WindDirectionTrueDeg = MathEx.NormalizeHeading(_windDirection.Value * 360.0 / 65536.0),
                EngineRpm = rpm,
                FuelFraction = MathEx.Clamp(_fuelFraction, 0.0, 1.0),
                WeightLb = OptionalValue(_weight.Value, "weight"),
                MaximumGrossWeightLb = _maximumGrossWeightLb,
                CgPercent = OptionalValue(_cgFraction.Value, "cgFraction") * 100.0,
                YokeX = NormalizeSigned(_yokeX.Value),
                YokeY = NormalizeSigned(_yokeY.Value),
                RudderPedal = NormalizeSigned(_rudder.Value),
                Throttle01 = MathEx.Clamp(_throttle.Value / 16384.0, 0.0, 1.0),
                Flaps01 = MathEx.Clamp(_flaps.Value / 16383.0, 0.0, 1.0),
                DesignVs0Kts = OptionalValue(_designVs0FeetPerSecond.Value, "designVs0FeetPerSecond") * 0.592483801296,
                DesignVs1Kts = OptionalValue(_designVs1FeetPerSecond.Value, "designVs1FeetPerSecond") * 0.592483801296,
                EstimatedCruiseKts = OptionalValue(_designCruiseFeetPerSecond.Value, "designCruiseFeetPerSecond") * 0.592483801296,
                EngineType = _engineType.Value,
                EngineCount = _engineCount.Value,
                OnGround = _onGround.Value != 0,
                ParkingBrakeSet = _parkingBrake.Value != 0,
                StallWarning = _stallWarning.Value != 0,
                OverspeedWarning = _overspeedWarning.Value != 0,
                SlewActive = _slewActive.Value != 0,
                Paused = _paused.Value != 0 || _loading.Value != 0 || _inMenu.Value != 0
            };
        }

        private void RefreshPayloadIfDue()
        {
            if (!_profile.RefreshPayloadServices) return;
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPayloadRefreshUtc).TotalSeconds < _profile.PayloadRefreshSeconds) return;
            _lastPayloadRefreshUtc = now;
            try
            {
                FSUIPCConnection.PayloadServices.RefreshData();
                _fuelFraction = OptionalValue(FSUIPCConnection.PayloadServices.FuelPercentage / 100.0, "fuelFraction");
                _maximumGrossWeightLb = OptionalValue(FSUIPCConnection.PayloadServices.MaxGrossWeightLbs, "maximumGrossWeight");
            }
            catch (Exception ex)
            {
                if ((now - _lastPayloadWarningUtc).TotalSeconds >= 30.0)
                {
                    _lastPayloadWarningUtc = now;
                    RaiseStatus("FSUIPC payload refresh skipped: " + ex.Message);
                }
            }
        }

        private double OptionalValue(double value, string name)
        {
            if (MathEx.AreFinite(value)) return value;
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPayloadWarningUtc).TotalSeconds >= 30)
            {
                _lastPayloadWarningUtc = now;
                RaiseStatus("FSUIPC optional property unavailable/non-finite: " + name + "; using zero for this optional value.");
            }
            return 0;
        }

        private static bool IsPlausible(AircraftState state)
        {
            return state != null && !string.IsNullOrWhiteSpace(state.AircraftTitle)
                   && state.EngineCount > 0 && state.IsFinite()
                   && state.LatitudeDeg >= -90.0 && state.LatitudeDeg <= 90.0
                   && state.LongitudeDeg >= -180.0 && state.LongitudeDeg <= 180.0
                   && state.IndicatedAirspeedKts >= -1.0 && state.AltitudeAglFt >= -100.0;
        }

        private static double SignedAngle(int value) { return value * 360.0 / 4294967296.0; }
        private static double NormalizeSigned(short value) { return MathEx.Clamp(value / 16383.0, -1.0, 1.0); }
        private static int SignedAxis(double normalized) { return (int)Math.Round(MathEx.Clamp(normalized, -1.0, 1.0) * 16383.0); }
        private static int UnsignedAxis(double normalized) { return (int)Math.Round(MathEx.Clamp(normalized, 0.0, 1.0) * 16383.0); }
        private static int AxisRange01(double normalized) { return (int)Math.Round(-16383.0 + MathEx.Clamp(normalized, 0.0, 1.0) * 32766.0); }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!_connected || !FSUIPCConnection.IsOpen) throw new InvalidOperationException("FSUIPC is not connected.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FsuipcSimulatorClient));
        }

        private void RaiseStatus(string message) { StatusChanged?.Invoke(message); }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    Disconnect("disposed");
                }
                catch (Exception ex) { RaiseStatus("FSUIPC cleanup failed: " + ex.Message); }
                FSUIPCConnection.DeleteGroup(_group);
            }
        }
    }
}
