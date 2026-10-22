using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using LockheedMartin.Prepar3D.SimConnect;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal sealed class P3DSimConnectClient : ISimulatorTransport, ISimulatorControlRelease
    {
        private enum Definitions { EssentialTelemetry, ExtendedTelemetry, AircraftIdentity }
        private enum Requests { EssentialTelemetry, ExtendedTelemetry, AircraftIdentity, ScenarioPath }
        private enum SystemEvents { Pause, FlightLoaded }
        private enum Events
        {
            Aileron,
            Elevator,
            Rudder,
            Throttle,
            Flaps,
            Gear,
            LeftBrake,
            RightBrake,
            SituationReset
        }
        // SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST is 1. The managed API
        // accepts an Enum in the GroupID slot, so a typed priority is used here.
        private enum Priorities { Highest = 1 }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct EssentialTelemetryData
        {
            public double SimTimeSeconds;
            public double LatitudeDeg;
            public double LongitudeDeg;
            public double AltitudeMslFt;
            public double AltitudeAglFt;
            public double IndicatedAirspeedKts;
            public double TrueAirspeedKts;
            public double GroundSpeedKts;
            public double VerticalSpeedFpm;
            public double PitchDeg;
            public double BankDeg;
            public double HeadingTrueDeg;
            public double OnGround;
            public double ParkingBrake;
            public double SlewActive;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ExtendedTelemetryData
        {
            public double AngleOfAttackDeg;
            public double SideSlipDeg;
            public double BodyRatePDegPerSecond;
            public double BodyRateQDegPerSecond;
            public double BodyRateRDegPerSecond;
            public double AccelerationXFeetPerSecond2;
            public double AccelerationYFeetPerSecond2;
            public double AccelerationZFeetPerSecond2;
            public double WindSpeedKts;
            public double WindDirectionTrueDeg;
            public double EngineRpm;
            public double FuelTotalQuantityGallons;
            public double FuelTotalCapacityGallons;
            public double WeightLb;
            public double MaximumGrossWeightLb;
            public double CgPercent;
            public double YokeX;
            public double YokeY;
            public double RudderPedal;
            public double ThrottlePercent;
            public double FlapsPercent;
            public double DesignVs0Kts;
            public double DesignVs1Kts;
            public double EstimatedCruiseKts;
            public double EngineType;
            public double EngineCount;
            public double StallWarning;
            public double OverspeedWarning;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        private struct AircraftIdentityData
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Title;
        }

        private readonly object _ioLock = new object();
        private readonly Stopwatch _commandAge = new Stopwatch();
        private bool _controlsApplied;
        private bool _openReceived;
        private bool _quitReceived;
        private string _scenarioPath = "";
        private long _scenarioLoadCount;
        private DateTime _lastOptionalWarningUtc;
        private readonly object _stateLock = new object();
        private readonly AutoResetEvent _messageEvent = new AutoResetEvent(false);
        private SimConnect _simConnect;
        private AircraftState _latestState;
        private volatile bool _disposed;
        private volatile bool _connected;
        private DateTime _connectedUtc;
        private DateTime _lastTelemetryUtc;
        private DateTime _lastCommandUtc;
        private long _telemetrySamples;
        private long _rejectedTelemetrySamples;
        private long _commandsSent;
        private long _reconnectAttempts;
        private int _consecutiveFailures;
        private string _version = "unknown";
        private string _lastError = "";
        private double _lastFlaps = double.NaN;
        private bool? _lastGear;
        private bool _paused;
        private ExtendedTelemetryData _extendedTelemetry;
        private string _aircraftTitle = "";

        public event Action<string> StatusChanged;
        public string Name { get { return "SimConnect"; } }

        public void Connect()
        {
            lock (_ioLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(P3DSimConnectClient));
                if (_connected && _simConnect != null) return;
                _reconnectAttempts++;
                if (_simConnect != null)
                {
                    ReleaseControls("reconnect");
                    CloseConnection(_simConnect);
                    _simConnect = null;
                }
                try
                {
                    _messageEvent.Reset();
                    _openReceived = false;
                    _quitReceived = false;
                    _lastError = "";
                    _paused = false;
                    _extendedTelemetry = default(ExtendedTelemetryData);
                    _aircraftTitle = "";
                    _scenarioPath = "";
                    _lastFlaps = double.NaN;
                    _lastGear = null;
                    _simConnect = new SimConnect("P3D AI Pilot", IntPtr.Zero, 0, _messageEvent, 0);
                    _simConnect.OnRecvOpen += OnRecvOpen;
                    _simConnect.OnRecvQuit += OnRecvQuit;
                    _simConnect.OnRecvException += OnRecvException;
                    _simConnect.OnRecvEvent += OnRecvEvent;
                    _simConnect.OnRecvSimobjectData += OnRecvSimobjectData;
                    _simConnect.OnRecvSystemState += OnRecvSystemState;
                    _simConnect.OnRecvEventFilename += OnRecvEventFilename;
                    var handshake = Stopwatch.StartNew();
                    while (!_openReceived && !_quitReceived && handshake.ElapsedMilliseconds < 2000)
                    {
                        if (_messageEvent.WaitOne(50)) _simConnect.ReceiveMessage();
                    }
                    if (_quitReceived) throw new InvalidOperationException("Simulator quit during the SimConnect handshake.");
                    if (!_openReceived) throw new TimeoutException("SimConnect handle opened but no OnRecvOpen arrived within 2 seconds. " + _lastError);
                    DefineEssentialTelemetry();
                    _simConnect.RequestDataOnSimObject(Requests.EssentialTelemetry, Definitions.EssentialTelemetry,
                        SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME,
                        SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    try
                    {
                        DefineAircraftIdentity();
                        _simConnect.RequestDataOnSimObject(Requests.AircraftIdentity, Definitions.AircraftIdentity,
                            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    }
                    catch (Exception optionalIdentityError)
                    {
                        RaiseStatus("SimConnect aircraft TITLE channel unavailable; essential telemetry remains active: "
                            + optionalIdentityError.Message);
                    }
                    try
                    {
                        DefineExtendedTelemetry();
                        _simConnect.RequestDataOnSimObject(Requests.ExtendedTelemetry, Definitions.ExtendedTelemetry,
                            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    }
                    catch (Exception optionalTelemetryError)
                    {
                        RaiseStatus("SimConnect extended telemetry unavailable; essential telemetry remains active: "
                            + optionalTelemetryError.Message);
                    }
                    DefineControlEvents();
                    _simConnect.SubscribeToSystemEvent(SystemEvents.Pause, "Pause");
                    _simConnect.SubscribeToSystemEvent(SystemEvents.FlightLoaded, "FlightLoaded");
                    _simConnect.RequestSystemState(Requests.ScenarioPath, "FlightLoaded");
                    _connected = true;
                    _connectedUtc = DateTime.UtcNow;
                    _lastTelemetryUtc = DateTime.MinValue;
                    _latestState = null;
                    _consecutiveFailures = 0;
                    _lastError = "";
                }
                catch (Exception ex)
                {
                    _connected = false;
                    _consecutiveFailures++;
                    _lastError = ErrorDetail(ex);
                    if (_simConnect != null) { CloseConnection(_simConnect); _simConnect = null; }
                    throw;
                }
            }
        }

        public void Pump(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !_disposed && _connected)
            {
                if (token.WaitHandle.WaitOne(20)) break;
                bool notified;
                lock (_ioLock)
                {
                    if (_disposed || !_connected) break;
                    if (_controlsApplied && _commandAge.ElapsedMilliseconds >= 500)
                        ReleaseControls("command timeout (500 ms)");
                    notified = _messageEvent.WaitOne(0);
                }
                if (notified)
                {
                    try
                    {
                        lock (_ioLock)
                        {
                            if (!_connected || _disposed) break;
                            _simConnect?.ReceiveMessage();
                        }
                    }
                    catch (COMException ex)
                    {
                        lock (_stateLock)
                        {
                            _lastError = ErrorDetail(ex);
                            _connected = false;
                            _consecutiveFailures++;
                        }
                        ReleaseControls("receive failure");
                        RaiseStatus("SimConnect receive error: " + ErrorDetail(ex));
                        break;
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        lock (_stateLock)
                        {
                            _lastError = ErrorDetail(ex);
                            _connected = false;
                            _consecutiveFailures++;
                        }
                        ReleaseControls("pump failure");
                        RaiseStatus("SimConnect pump stopped: " + ErrorDetail(ex));
                        break;
                    }
                }
            }
        }

        public void Disconnect(string reason)
        {
            lock (_ioLock)
            {
                ReleaseControls(reason);
                SimConnect connection;
                lock (_stateLock)
                {
                    _connected = false;
                    _latestState = null;
                    connection = _simConnect;
                    _simConnect = null;
                    if (!string.IsNullOrWhiteSpace(reason)) _lastError = reason;
                }
                _messageEvent.Set();
                if (connection != null) CloseConnection(connection);
            }
        }

        private void CloseConnection(SimConnect connection)
        {
            connection.OnRecvOpen -= OnRecvOpen;
            connection.OnRecvQuit -= OnRecvQuit;
            connection.OnRecvException -= OnRecvException;
            connection.OnRecvEvent -= OnRecvEvent;
            connection.OnRecvSimobjectData -= OnRecvSimobjectData;
            connection.OnRecvSystemState -= OnRecvSystemState;
            connection.OnRecvEventFilename -= OnRecvEventFilename;
            try { connection.Dispose(); }
            catch (Exception ex) { RaiseStatus("SimConnect cleanup failed: " + ErrorDetail(ex)); }
        }

        private static string ErrorDetail(Exception ex)
        {
            return ex.GetType().Name + " HRESULT=0x" + ex.HResult.ToString("X8") + ": " + ex.Message;
        }

        public AircraftState GetLatestState()
        {
            lock (_stateLock) return _latestState;
        }

        public SimulatorTransportHealth GetHealth()
        {
            lock (_stateLock)
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
                    SimulationPaused = _paused,
                    ScenarioPath = _scenarioPath,
                    ScenarioLoadCount = _scenarioLoadCount,
                    Capabilities = SimulatorCapabilities.Telemetry | SimulatorCapabilities.Controls | SimulatorCapabilities.ScenarioReset
                };
            }
        }

        public void Send(ControlCommand command)
        {
            lock (_ioLock)
            {
                if (command == null) throw new ArgumentNullException(nameof(command));
                if (!command.IsFinite()) throw new ArgumentException("Control command contains a non-finite value.", nameof(command));
                if (_simConnect == null || !_connected || _disposed) throw new InvalidOperationException("SimConnect is not connected.");
                lock (_stateLock)
                {
                    if (_latestState == null || _paused || _latestState.Paused || _latestState.SlewActive
                        || (DateTime.UtcNow - _lastTelemetryUtc).TotalSeconds > 1)
                        throw new InvalidOperationException("SimConnect controls require fresh, unpaused telemetry.");
                }
                command.Clamp();
                try
                {
                    _controlsApplied = true;
                    _commandAge.Restart();
                    SendEvent(Events.Aileron, SignedAxis(command.Aileron));
                    SendEvent(Events.Elevator, SignedAxis(command.Elevator));
                    SendEvent(Events.Rudder, SignedAxis(command.Rudder));
                    SendEvent(Events.Throttle, UnsignedAxis(command.Throttle01));

                    if (double.IsNaN(_lastFlaps) || Math.Abs(command.Flaps01 - _lastFlaps) >= 0.02)
                    {
                        SendEvent(Events.Flaps, UnsignedAxis(command.Flaps01));
                        _lastFlaps = command.Flaps01;
                    }

                    if (!_lastGear.HasValue || _lastGear.Value != command.GearDown)
                    {
                        SendEvent(Events.Gear, command.GearDown ? 1u : 0u);
                        _lastGear = command.GearDown;
                    }

                    uint brake = AxisRange01(command.Brakes01);
                    SendEvent(Events.LeftBrake, brake);
                    SendEvent(Events.RightBrake, brake);
                    lock (_stateLock)
                    {
                        _lastCommandUtc = DateTime.UtcNow;
                        _commandsSent++;
                    }
                }
                catch (Exception ex)
                {
                    lock (_stateLock)
                    {
                        ReleaseControls("control write failure");
                        _connected = false;
                        _consecutiveFailures++;
                        _lastError = ErrorDetail(ex);
                    }
                    throw;
                }
            }
        }

        public void ReleaseControls(string reason)
        {
            lock (_ioLock)
            {
                if (!_controlsApplied) return;
                _controlsApplied = false;
                _commandAge.Stop();
                if (_simConnect == null) return;
                try
                {
                    SendEvent(Events.Aileron, 0);
                    SendEvent(Events.Elevator, 0);
                    SendEvent(Events.Rudder, 0);
                    SendEvent(Events.LeftBrake, AxisRange01(0));
                    SendEvent(Events.RightBrake, AxisRange01(0));
                    RaiseStatus("SimConnect AI axes released: " + reason);
                }
                catch (Exception ex) { RaiseStatus("SimConnect AI release unavailable: " + ErrorDetail(ex)); }
            }
        }

        private void DefineEssentialTelemetry()
        {
            Add(Definitions.EssentialTelemetry, "SIM TIME", "seconds");
            Add(Definitions.EssentialTelemetry, "PLANE LATITUDE", "degrees");
            Add(Definitions.EssentialTelemetry, "PLANE LONGITUDE", "degrees");
            Add(Definitions.EssentialTelemetry, "PLANE ALTITUDE", "feet");
            Add(Definitions.EssentialTelemetry, "PLANE ALT ABOVE GROUND", "feet");
            Add(Definitions.EssentialTelemetry, "AIRSPEED INDICATED", "knots");
            Add(Definitions.EssentialTelemetry, "AIRSPEED TRUE", "knots");
            Add(Definitions.EssentialTelemetry, "GROUND VELOCITY", "knots");
            Add(Definitions.EssentialTelemetry, "VERTICAL SPEED", "feet per minute");
            Add(Definitions.EssentialTelemetry, "PLANE PITCH DEGREES", "degrees");
            Add(Definitions.EssentialTelemetry, "PLANE BANK DEGREES", "degrees");
            Add(Definitions.EssentialTelemetry, "PLANE HEADING DEGREES TRUE", "degrees");
            Add(Definitions.EssentialTelemetry, "SIM ON GROUND", "bool");
            Add(Definitions.EssentialTelemetry, "BRAKE PARKING POSITION", "bool");
            Add(Definitions.EssentialTelemetry, "IS SLEW ACTIVE", "bool");
            _simConnect.RegisterDataDefineStruct<EssentialTelemetryData>(Definitions.EssentialTelemetry);
        }

        private void DefineExtendedTelemetry()
        {
            Add(Definitions.ExtendedTelemetry, "INCIDENCE ALPHA", "degrees");
            Add(Definitions.ExtendedTelemetry, "INCIDENCE BETA", "degrees");
            Add(Definitions.ExtendedTelemetry, "ROTATION VELOCITY BODY X", "degrees per second");
            Add(Definitions.ExtendedTelemetry, "ROTATION VELOCITY BODY Y", "degrees per second");
            Add(Definitions.ExtendedTelemetry, "ROTATION VELOCITY BODY Z", "degrees per second");
            Add(Definitions.ExtendedTelemetry, "ACCELERATION BODY X", "feet per second squared");
            Add(Definitions.ExtendedTelemetry, "ACCELERATION BODY Y", "feet per second squared");
            Add(Definitions.ExtendedTelemetry, "ACCELERATION BODY Z", "feet per second squared");
            Add(Definitions.ExtendedTelemetry, "AMBIENT WIND VELOCITY", "knots");
            Add(Definitions.ExtendedTelemetry, "AMBIENT WIND DIRECTION", "degrees");
            Add(Definitions.ExtendedTelemetry, "GENERAL ENG RPM:1", "rpm");
            // P3D v4 has no "FUEL TOTAL QUANTITY PERCENT" SimVar.  A bad
            // datum name causes an asynchronous DATA_ERROR and can prevent the
            // whole telemetry definition from producing samples.  Request the
            // two documented gallon values and derive the fraction locally.
            Add(Definitions.ExtendedTelemetry, "FUEL TOTAL QUANTITY", "gallons");
            Add(Definitions.ExtendedTelemetry, "FUEL TOTAL CAPACITY", "gallons");
            Add(Definitions.ExtendedTelemetry, "TOTAL WEIGHT", "pounds");
            Add(Definitions.ExtendedTelemetry, "MAX GROSS WEIGHT", "pounds");
            Add(Definitions.ExtendedTelemetry, "CG PERCENT", "percent");
            Add(Definitions.ExtendedTelemetry, "YOKE X POSITION", "position");
            Add(Definitions.ExtendedTelemetry, "YOKE Y POSITION", "position");
            Add(Definitions.ExtendedTelemetry, "RUDDER PEDAL POSITION", "position");
            Add(Definitions.ExtendedTelemetry, "GENERAL ENG THROTTLE LEVER POSITION:1", "percent");
            Add(Definitions.ExtendedTelemetry, "FLAPS HANDLE PERCENT", "percent");
            Add(Definitions.ExtendedTelemetry, "DESIGN SPEED VS0", "knots");
            Add(Definitions.ExtendedTelemetry, "DESIGN SPEED VS1", "knots");
            Add(Definitions.ExtendedTelemetry, "ESTIMATED CRUISE SPEED", "knots");
            Add(Definitions.ExtendedTelemetry, "ENGINE TYPE", "number");
            Add(Definitions.ExtendedTelemetry, "NUMBER OF ENGINES", "number");
            Add(Definitions.ExtendedTelemetry, "STALL WARNING", "bool");
            Add(Definitions.ExtendedTelemetry, "OVERSPEED WARNING", "bool");
            _simConnect.RegisterDataDefineStruct<ExtendedTelemetryData>(Definitions.ExtendedTelemetry);
        }

        private void DefineAircraftIdentity()
        {
            _simConnect.AddToDataDefinition(Definitions.AircraftIdentity, "TITLE", null,
                SIMCONNECT_DATATYPE.STRING256, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            _simConnect.RegisterDataDefineStruct<AircraftIdentityData>(Definitions.AircraftIdentity);
        }

        private void Add(Definitions definition, string name, string unit)
        {
            _simConnect.AddToDataDefinition(definition, name, unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
        }

        private void DefineControlEvents()
        {
            _simConnect.MapClientEventToSimEvent(Events.Aileron, "AXIS_AILERONS_SET");
            _simConnect.MapClientEventToSimEvent(Events.Elevator, "AXIS_ELEVATOR_SET");
            _simConnect.MapClientEventToSimEvent(Events.Rudder, "AXIS_RUDDER_SET");
            _simConnect.MapClientEventToSimEvent(Events.Throttle, "THROTTLE_SET");
            _simConnect.MapClientEventToSimEvent(Events.Flaps, "FLAPS_SET");
            _simConnect.MapClientEventToSimEvent(Events.Gear, "GEAR_SET");
            _simConnect.MapClientEventToSimEvent(Events.LeftBrake, "AXIS_LEFT_BRAKE_SET");
            _simConnect.MapClientEventToSimEvent(Events.RightBrake, "AXIS_RIGHT_BRAKE_SET");
            _simConnect.MapClientEventToSimEvent(Events.SituationReset, "SITUATION_RESET");
        }

        private void SendEvent(Events eventId, uint value)
        {
            _simConnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, eventId, value,
                Priorities.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }

        public void ResetScenario()
        {
            lock (_ioLock)
            {
                if (_simConnect == null || !_connected || _disposed) throw new InvalidOperationException("SimConnect is not connected.");
                SendEvent(Events.SituationReset, 0u);
            }
        }

        private static uint SignedAxis(double normalized)
        {
            int value = (int)Math.Round(MathEx.Clamp(normalized, -1.0, 1.0) * 16383.0);
            return unchecked((uint)value);
        }

        private static uint UnsignedAxis(double normalized)
        {
            return (uint)Math.Round(MathEx.Clamp(normalized, 0.0, 1.0) * 16383.0);
        }

        private static uint AxisRange01(double normalized)
        {
            int value = (int)Math.Round(-16383.0 + MathEx.Clamp(normalized, 0.0, 1.0) * 32766.0);
            return unchecked((uint)value);
        }

        private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            lock (_stateLock)
            {
                _openReceived = true;
                _consecutiveFailures = 0;
                _version = data.szApplicationName + " " + data.dwApplicationVersionMajor + "." + data.dwApplicationVersionMinor
                           + "." + data.dwApplicationBuildMajor + "." + data.dwApplicationBuildMinor;
            }
            RaiseStatus("Connected to Prepar3D: " + data.szApplicationName);
        }

        private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            lock (_stateLock)
            {
                _connected = false;
                _quitReceived = true;
                _lastError = "Prepar3D closed the SimConnect session.";
                _consecutiveFailures++;
            }
            RaiseStatus("Prepar3D closed the SimConnect session.");
        }

        private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            lock (_stateLock)
            {
                _lastError = ((SIMCONNECT_EXCEPTION)data.dwException) + " (" + data.dwException + ") sendId=" + data.dwSendID + " datumIndex=" + data.dwIndex;
                _consecutiveFailures++;
            }
            RaiseStatus("SimConnect exception: " + _lastError);
        }

        private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            if ((SystemEvents)data.uEventID != SystemEvents.Pause) return;
            lock (_stateLock)
            {
                _paused = data.dwData != 0;
                if (_latestState != null) _latestState.Paused = _paused;
            }
            if (_paused) ReleaseControls("simulator paused");
            RaiseStatus(_paused ? "Prepar3D paused." : "Prepar3D resumed.");
        }

        private void OnRecvSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
        {
            if ((Requests)data.dwRequestID != Requests.ScenarioPath) return;
            lock (_stateLock) _scenarioPath = data.szString ?? "";
            RaiseStatus("SimConnect loaded scenario: " + (string.IsNullOrWhiteSpace(_scenarioPath) ? "not available" : _scenarioPath));
        }

        private void OnRecvEventFilename(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
        {
            if ((SystemEvents)data.uEventID != SystemEvents.FlightLoaded) return;
            ReleaseControls("scenario changed");
            lock (_stateLock)
            {
                _scenarioLoadCount++;
                _latestState = null;
                _extendedTelemetry = default(ExtendedTelemetryData);
                _aircraftTitle = "";
                _lastTelemetryUtc = DateTime.MinValue;
                _connectedUtc = DateTime.UtcNow;
            }
            _lastGear = null;
            _lastFlaps = double.NaN;
            _simConnect.RequestSystemState(Requests.ScenarioPath, "FlightLoaded");
            RaiseStatus("Prepar3D scenario changed; waiting for fresh aircraft telemetry.");
        }

        private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwData.Length == 0) return;
            Requests request = (Requests)data.dwRequestID;
            if (request == Requests.AircraftIdentity)
            {
                var identity = (AircraftIdentityData)data.dwData[0];
                lock (_stateLock)
                {
                    _aircraftTitle = (identity.Title ?? "").TrimEnd('\0', ' ');
                    if (_latestState != null) _latestState.AircraftTitle = _aircraftTitle;
                }
                return;
            }
            if (request == Requests.ExtendedTelemetry)
            {
                var candidate = (ExtendedTelemetryData)data.dwData[0];
                foreach (var field in typeof(ExtendedTelemetryData).GetFields())
                {
                    if (!MathEx.AreFinite((double)field.GetValue(candidate)))
                    {
                        if ((DateTime.UtcNow - _lastOptionalWarningUtc).TotalSeconds >= 30)
                        {
                            _lastOptionalWarningUtc = DateTime.UtcNow;
                            RaiseStatus("SimConnect optional telemetry rejected: " + field.Name + "; essential channel remains active.");
                        }
                        return;
                    }
                }
                lock (_stateLock) _extendedTelemetry = candidate;
                return;
            }
            if (request != Requests.EssentialTelemetry) return;
            var raw = (EssentialTelemetryData)data.dwData[0];
            ExtendedTelemetryData extended;
            string aircraftTitle;
            lock (_stateLock)
            {
                extended = _extendedTelemetry;
                aircraftTitle = _aircraftTitle;
            }
            var state = new AircraftState
            {
                TelemetrySource = Name,
                AircraftTitle = aircraftTitle,
                TelemetrySequence = Interlocked.Increment(ref _telemetrySamples),
                UtcTimestamp = DateTime.UtcNow,
                SimTimeSeconds = raw.SimTimeSeconds,
                LatitudeDeg = raw.LatitudeDeg,
                LongitudeDeg = raw.LongitudeDeg,
                AltitudeMslFt = raw.AltitudeMslFt,
                AltitudeAglFt = raw.AltitudeAglFt,
                IndicatedAirspeedKts = raw.IndicatedAirspeedKts,
                TrueAirspeedKts = raw.TrueAirspeedKts,
                GroundSpeedKts = raw.GroundSpeedKts,
                VerticalSpeedFpm = raw.VerticalSpeedFpm,
                PitchDeg = raw.PitchDeg,
                BankDeg = raw.BankDeg,
                HeadingTrueDeg = MathEx.NormalizeHeading(raw.HeadingTrueDeg),
                AngleOfAttackDeg = extended.AngleOfAttackDeg,
                SideSlipDeg = extended.SideSlipDeg,
                BodyRatePDegPerSecond = extended.BodyRatePDegPerSecond,
                BodyRateQDegPerSecond = extended.BodyRateQDegPerSecond,
                BodyRateRDegPerSecond = extended.BodyRateRDegPerSecond,
                AccelerationXG = extended.AccelerationXFeetPerSecond2 / 32.174,
                AccelerationYG = extended.AccelerationYFeetPerSecond2 / 32.174,
                AccelerationZG = extended.AccelerationZFeetPerSecond2 / 32.174,
                WindSpeedKts = extended.WindSpeedKts,
                WindDirectionTrueDeg = MathEx.NormalizeHeading(extended.WindDirectionTrueDeg),
                EngineRpm = extended.EngineRpm,
                FuelFraction = extended.FuelTotalCapacityGallons > 0.001
                    ? MathEx.Clamp(extended.FuelTotalQuantityGallons / extended.FuelTotalCapacityGallons, 0.0, 1.0)
                    : 0.0,
                WeightLb = extended.WeightLb,
                MaximumGrossWeightLb = extended.MaximumGrossWeightLb,
                CgPercent = extended.CgPercent,
                YokeX = extended.YokeX,
                YokeY = extended.YokeY,
                RudderPedal = extended.RudderPedal,
                Throttle01 = extended.ThrottlePercent / 100.0,
                Flaps01 = extended.FlapsPercent / 100.0,
                DesignVs0Kts = extended.DesignVs0Kts,
                DesignVs1Kts = extended.DesignVs1Kts,
                EstimatedCruiseKts = extended.EstimatedCruiseKts,
                EngineType = (int)Math.Round(extended.EngineType),
                EngineCount = (int)Math.Round(extended.EngineCount),
                OnGround = raw.OnGround > 0.5,
                ParkingBrakeSet = raw.ParkingBrake > 0.5,
                StallWarning = extended.StallWarning > 0.5,
                OverspeedWarning = extended.OverspeedWarning > 0.5,
                SlewActive = raw.SlewActive > 0.5,
                Paused = _paused
            };
            if (!state.IsFinite() || state.LatitudeDeg < -90.0 || state.LatitudeDeg > 90.0
                || state.LongitudeDeg < -180.0 || state.LongitudeDeg > 180.0
                || state.IndicatedAirspeedKts < -1.0 || state.AltitudeAglFt < -100.0)
            {
                lock (_stateLock)
                {
                    _rejectedTelemetrySamples++;
                    _consecutiveFailures++;
                    _lastError = "Rejected invalid SimConnect telemetry sample.";
                }
                return;
            }
            if (state.SlewActive) ReleaseControls("simulator slew");
            lock (_stateLock)
            {
                _latestState = state;
                _lastTelemetryUtc = state.UtcTimestamp;
                _lastError = "";
                _consecutiveFailures = 0;
            }
        }

        private void RaiseStatus(string message)
        {
            StatusChanged?.Invoke(message);
        }

        public void Dispose()
        {
            lock (_ioLock)
            {
                if (_disposed) return;
                _disposed = true;
                Disconnect("disposed");
                _messageEvent.Dispose();
            }
        }
    }
}
