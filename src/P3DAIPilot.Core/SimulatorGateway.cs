using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace P3DAIPilot.Core
{
    public interface ISimulatorTransport : IDisposable
    {
        string Name { get; }
        event Action<string> StatusChanged;
        void Connect();
        void Disconnect(string reason);
        void Pump(CancellationToken token);
        AircraftState GetLatestState();
        SimulatorTransportHealth GetHealth();
        void Send(ControlCommand command);
        void ResetScenario();
    }

    public interface ISimulatorControlRelease
    {
        void ReleaseControls(string reason);
    }

    public sealed class SimulatorGateway : IDisposable
    {
        private readonly object _sync = new object();
        private readonly SimulatorProfile _profile;
        private readonly List<ISimulatorTransport> _transports;
        private readonly List<Task> _pumpTasks = new List<Task>();
        private readonly Dictionary<ISimulatorTransport, Task> _currentPumps = new Dictionary<ISimulatorTransport, Task>();
        private ISimulatorTransport _active;
        private DateTime _preferredFreshSince;
        private DateTime _lastDivergenceWarningUtc;
        private DateTime _divergenceSinceUtc;
        private DateTime _lastDivergenceEvaluationUtc;
        private readonly Dictionary<ISimulatorTransport, DateTime> _nextReconnectUtc = new Dictionary<ISimulatorTransport, DateTime>();
        private readonly Dictionary<ISimulatorTransport, int> _retryFailures = new Dictionary<ISimulatorTransport, int>();
        private CancellationToken _pumpToken;
        private bool _pumpsStarted;
        private bool _disposed;
        private SimulatorGatewayHealth _lastHealth;

        public SimulatorGateway(SimulatorProfile profile, IEnumerable<ISimulatorTransport> transports)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _transports = (transports ?? throw new ArgumentNullException(nameof(transports)))
                .Where(t => t != null && profile.Allows(t.Name)).ToList();
            if (_transports.Count == 0) throw new ArgumentException("No simulator transport is enabled.", nameof(transports));
            foreach (ISimulatorTransport transport in _transports)
                transport.StatusChanged += OnTransportStatusChanged;
        }

        public event Action<string> StatusChanged;
        public event Action<string, string> ActiveTransportChanged;

        public string ActiveTransportName
        {
            get { lock (_sync) return _active == null ? "None" : _active.Name; }
        }

        public void ConnectAll()
        {
            IList<string> failures;
            if (!TryConnectAll(out failures))
                throw new InvalidOperationException("No simulator connection could be opened. " + string.Join(" | ", failures));
        }

        public bool TryConnectAll(out IList<string> failures)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SimulatorGateway));
                var connectionFailures = new List<string>();
                foreach (ISimulatorTransport transport in OrderedTransports())
                {
                    if (transport.GetHealth().IsConnected) continue;
                    try
                    {
                        transport.Connect();
                        RaiseStatus(transport.Name + " connection opened; waiting for telemetry.");
                    }
                    catch (Exception ex)
                    {
                        connectionFailures.Add(transport.Name + ": " + ex.Message);
                        RaiseStatus(transport.Name + " unavailable: " + ex.Message);
                    }
                }

                failures = connectionFailures;
                return _transports.Any(t => t.GetHealth().IsConnected);
            }
        }

        public void StartPumps(CancellationToken token)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SimulatorGateway));
                _pumpToken = token;
                _pumpsStarted = true;
                foreach (ISimulatorTransport transport in _transports.Where(t => t.GetHealth().IsConnected))
                    StartPump(transport);
            }
        }

        public AircraftState GetLatestState(DateTime utcNow)
        {
            lock (_sync)
            {
                TryReconnect(utcNow);
                SelectActiveTransport(utcNow);
                UpdateDivergence(utcNow);
                return _active == null ? null : _active.GetLatestState();
            }
        }

        public void Send(ControlCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!command.IsFinite()) throw new ArgumentException("Control command contains a non-finite value.", nameof(command));
            ISimulatorTransport selected;
            lock (_sync) selected = _active;
            if (selected == null) throw new InvalidOperationException("No healthy simulator transport owns control authority.");
            try
            {
                selected.Send(command);
            }
            catch (Exception ex)
            {
                RaiseStatus(selected.Name + " control write failed: " + ex.Message);
                if (!_profile.CommandFailoverEnabled) throw;
                Deactivate(selected, "control write failure");
            }
        }

        public void ResetScenario()
        {
            ISimulatorTransport selected;
            lock (_sync) selected = _active;
            if (selected == null) throw new InvalidOperationException("No healthy simulator transport is active for reset.");
            SimulatorTransportHealth health = selected.GetHealth();
            if ((health.Capabilities & SimulatorCapabilities.ScenarioReset) == 0)
            {
                selected = _transports.FirstOrDefault(t => t.GetHealth().IsFresh(DateTime.UtcNow, _profile.TelemetryStaleSeconds)
                    && (t.GetHealth().Capabilities & SimulatorCapabilities.ScenarioReset) != 0);
            }
            if (selected == null) throw new NotSupportedException("No connected transport supports scenario reset.");
            selected.ResetScenario();
        }

        public SimulatorGatewayHealth GetHealth(DateTime utcNow)
        {
            lock (_sync)
            {
                UpdateDivergence(utcNow);
                SimulatorGatewayHealth snapshot = _lastHealth ?? BuildHealth(false, 0.0, 0.0, 0.0);
                return new SimulatorGatewayHealth
                {
                    ActiveTransport = snapshot.ActiveTransport,
                    PreferredTransport = snapshot.PreferredTransport,
                    DivergenceDetected = snapshot.DivergenceDetected,
                    DivergenceDurationSeconds = snapshot.DivergenceDurationSeconds,
                    ControlInhibited = snapshot.ControlInhibited,
                    AltitudeDivergenceFt = snapshot.AltitudeDivergenceFt,
                    AirspeedDivergenceKts = snapshot.AirspeedDivergenceKts,
                    HeadingDivergenceDeg = snapshot.HeadingDivergenceDeg,
                    Transports = snapshot.Transports.Select(x => x.Clone()).ToList()
                };
            }
        }

        public void WaitForPumps(int millisecondsTimeout)
        {
            Task[] pumps;
            lock (_sync) pumps = _pumpTasks.ToArray();
            if (pumps.Length == 0) return;
            try { Task.WaitAll(pumps, millisecondsTimeout); }
            catch (AggregateException ex) { RaiseStatus("Simulator pump failure: " + ex.Flatten().Message); }
        }

        private void SelectActiveTransport(DateTime utcNow)
        {
            ISimulatorTransport preferred = FindByName(_profile.PreferredTransport);
            bool preferredFresh = preferred != null && preferred.GetHealth().IsFresh(utcNow, _profile.TelemetryStaleSeconds);
            bool activeFresh = _active != null && _active.GetHealth().IsFresh(utcNow, _profile.TelemetryStaleSeconds);

            if (preferredFresh)
            {
                if (_preferredFreshSince == DateTime.MinValue) _preferredFreshSince = utcNow;
            }
            else _preferredFreshSince = DateTime.MinValue;

            ISimulatorTransport next = _active;
            if (!activeFresh)
                next = preferredFresh ? preferred : _transports.FirstOrDefault(t => t.GetHealth().IsFresh(utcNow, _profile.TelemetryStaleSeconds));
            else if (!object.ReferenceEquals(_active, preferred) && preferredFresh
                     && (utcNow - _preferredFreshSince).TotalSeconds >= _profile.FailbackStableSeconds)
                next = preferred;

            if (!object.ReferenceEquals(next, _active))
            {
                string previous = _active == null ? "None" : _active.Name;
                ReleaseTransport(_active, "active transport changed");
                _active = next;
                string current = _active == null ? "None" : _active.Name;
                RaiseStatus("Control authority changed: " + previous + " -> " + current + ".");
                ActiveTransportChanged?.Invoke(previous, current);
            }
        }

        private void TryReconnect(DateTime utcNow)
        {
            if (_disposed || (_pumpsStarted && _pumpToken.IsCancellationRequested)) return;
            var recycled = new HashSet<ISimulatorTransport>();
            foreach (ISimulatorTransport transport in _transports.Where(t => t.GetHealth().IsConnected))
            {
                SimulatorTransportHealth health = transport.GetHealth();
                double age = health.HasTelemetry && health.LastTelemetryUtc != DateTime.MinValue
                    ? (utcNow - health.LastTelemetryUtc).TotalSeconds
                    : (utcNow - health.ConnectedUtc).TotalSeconds;
                if (health.IsFresh(utcNow, _profile.TelemetryStaleSeconds)
                    && (utcNow - health.ConnectedUtc).TotalSeconds >= 10)
                {
                    _retryFailures.Remove(transport);
                    _nextReconnectUtc.Remove(transport);
                }
                AircraftState currentState = transport.GetLatestState();
                // SIM_FRAME telemetry stops while paused; do not recycle a valid paused session.
                if (health.SimulationPaused == true || (currentState != null && currentState.Paused)) continue;
                if (age < _profile.StaleConnectionRecycleSeconds) continue;
                try
                {
                    transport.Disconnect("telemetry watchdog recycle");
                    recycled.Add(transport);
                    RaiseStatus(transport.Name + " connection recycled after " + age.ToString("0.0") + " seconds without telemetry.");
                    if (object.ReferenceEquals(_active, transport))
                        Deactivate(transport, "telemetry watchdog recycle");
                }
                catch (Exception ex)
                {
                    RaiseStatus(transport.Name + " recycle failed: " + ex.Message);
                }
            }
            foreach (ISimulatorTransport transport in _transports.Where(t => !t.GetHealth().IsConnected && !recycled.Contains(t)))
            {
                DateTime due;
                if (_nextReconnectUtc.TryGetValue(transport, out due) && utcNow < due) continue;
                Task previousPump;
                if (_currentPumps.TryGetValue(transport, out previousPump) && !previousPump.IsCompleted)
                {
                    // Reopening before the old loop exits can leave the new connection without a pump.
                    continue;
                }
                var attemptClock = Stopwatch.StartNew();
                try
                {
                    transport.Connect();
                    ScheduleRetry(transport, utcNow.Add(attemptClock.Elapsed));
                    RaiseStatus(transport.Name + " reconnected; waiting for fresh telemetry.");
                    if (_pumpsStarted) StartPump(transport);
                }
                catch (Exception ex)
                {
                    double delay = ScheduleRetry(transport, utcNow.Add(attemptClock.Elapsed));
                    RaiseStatus(transport.Name + " reconnect failed; retry in " + delay.ToString("0") + " seconds: " + ex.Message);
                }
            }
        }

        private double ScheduleRetry(ISimulatorTransport transport, DateTime utcNow)
        {
            int failures;
            _retryFailures.TryGetValue(transport, out failures);
            failures = Math.Min(16, failures + 1);
            _retryFailures[transport] = failures;
            double delay = Math.Min(Math.Max(30, _profile.ReconnectIntervalSeconds),
                _profile.ReconnectIntervalSeconds * Math.Pow(2, failures - 1));
            _nextReconnectUtc[transport] = utcNow.AddSeconds(delay);
            return delay;
        }

        private void StartPump(ISimulatorTransport transport)
        {
            _pumpTasks.RemoveAll(completedPump => completedPump.IsCompleted);
            Task current;
            if (_currentPumps.TryGetValue(transport, out current) && !current.IsCompleted) return;
            ISimulatorTransport captured = transport;
            Task pumpTask = Task.Run(() => captured.Pump(_pumpToken), _pumpToken);
            _currentPumps[transport] = pumpTask;
            _pumpTasks.Add(pumpTask);
        }

        private void UpdateDivergence(DateTime utcNow)
        {
            _lastDivergenceEvaluationUtc = utcNow;
            var fresh = _transports.Where(t => t.GetHealth().IsFresh(utcNow, _profile.TelemetryStaleSeconds))
                .Select(t => t.GetLatestState()).Where(s => s != null).ToList();
            if (fresh.Count < 2)
            {
                _divergenceSinceUtc = DateTime.MinValue;
                _lastHealth = BuildHealth(false, 0.0, 0.0, 0.0);
                return;
            }

            AircraftState a = fresh[0];
            AircraftState b = fresh[1];
            double altitude = Math.Abs(a.AltitudeMslFt - b.AltitudeMslFt);
            double airspeed = Math.Abs(a.IndicatedAirspeedKts - b.IndicatedAirspeedKts);
            double heading = Math.Abs(MathEx.HeadingError(a.HeadingTrueDeg, b.HeadingTrueDeg));
            bool divergent = altitude > _profile.MaximumAltitudeDivergenceFt
                             || airspeed > _profile.MaximumAirspeedDivergenceKts
                             || heading > _profile.MaximumHeadingDivergenceDeg;
            if (divergent)
            {
                if (_divergenceSinceUtc == DateTime.MinValue) _divergenceSinceUtc = utcNow;
            }
            else _divergenceSinceUtc = DateTime.MinValue;
            _lastHealth = BuildHealth(divergent, altitude, airspeed, heading);
            if (divergent && (utcNow - _lastDivergenceWarningUtc).TotalSeconds >= 5.0)
            {
                _lastDivergenceWarningUtc = utcNow;
                RaiseStatus(string.Format("Telemetry divergence: altitude={0:0.0}ft IAS={1:0.0}kt heading={2:0.0}deg.",
                    altitude, airspeed, heading));
            }
        }

        private SimulatorGatewayHealth BuildHealth(bool divergent, double altitude, double airspeed, double heading)
        {
            double divergenceDuration = divergent && _divergenceSinceUtc != DateTime.MinValue
                ? Math.Max(0.0, (_lastDivergenceEvaluationUtc - _divergenceSinceUtc).TotalSeconds)
                : 0.0;
            return new SimulatorGatewayHealth
            {
                ActiveTransport = _active == null ? "None" : _active.Name,
                PreferredTransport = _profile.PreferredTransport,
                DivergenceDetected = divergent,
                DivergenceDurationSeconds = divergenceDuration,
                ControlInhibited = divergent && _profile.DisarmOnTelemetryDivergence
                                   && divergenceDuration >= _profile.TelemetryDivergenceHoldSeconds,
                AltitudeDivergenceFt = altitude,
                AirspeedDivergenceKts = airspeed,
                HeadingDivergenceDeg = heading,
                Transports = _transports.Select(t => t.GetHealth()).ToList()
            };
        }

        private IEnumerable<ISimulatorTransport> OrderedTransports()
        {
            return _transports.OrderBy(t => string.Equals(t.Name, _profile.PreferredTransport, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        private ISimulatorTransport FindByName(string name)
        {
            return _transports.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private void Deactivate(ISimulatorTransport expected, string reason)
        {
            string previous = "None";
            bool changed = false;
            lock (_sync)
            {
                if (object.ReferenceEquals(_active, expected))
                {
                    previous = _active.Name;
                    ReleaseTransport(_active, reason);
                    _active = null;
                    changed = true;
                }
            }
            if (!changed) return;
            RaiseStatus("Control authority removed from " + previous + " after " + reason + ".");
            ActiveTransportChanged?.Invoke(previous, "None");
        }

        public void ReleaseControls(string reason)
        {
            lock (_sync) ReleaseTransport(_active, reason);
        }

        private void ReleaseTransport(ISimulatorTransport transport, string reason)
        {
            var releasable = transport as ISimulatorControlRelease;
            if (releasable == null) return;
            try { releasable.ReleaseControls(reason); }
            catch (Exception ex) { RaiseStatus(transport.Name + " control release failed: " + ex.Message); }
        }

        private void OnTransportStatusChanged(string message) { RaiseStatus(message); }
        private void RaiseStatus(string message) { StatusChanged?.Invoke(message); }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (ISimulatorTransport transport in _transports)
                {
                    ReleaseTransport(transport, "gateway shutdown");
                    transport.StatusChanged -= OnTransportStatusChanged;
                    try { transport.Dispose(); }
                    catch (Exception ex) { RaiseStatus(transport.Name + " disposal failed: " + ex.Message); }
                }
                _active = null;
            }
        }
    }
}
