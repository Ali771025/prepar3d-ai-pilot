using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    [DataContract]
    internal sealed class LlmDecision
    {
        [DataMember(Name = "intent")] public string Intent { get; set; }
        [DataMember(Name = "heading_delta_deg")] public double HeadingDeltaDeg { get; set; }
        [DataMember(Name = "airspeed_delta_kts")] public double AirspeedDeltaKts { get; set; }
        [DataMember(Name = "confidence")] public double Confidence { get; set; }
        [DataMember(Name = "reason")] public string Reason { get; set; }
        public DateTime ReceivedUtc { get; set; }
    }

    internal sealed class LlmRuntimeHealth
    {
        public long Requests { get; set; }
        public long Successes { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string LastError { get; set; }
        public DateTime CircuitOpenUntilUtc { get; set; }
    }

    internal sealed class LocalLlmSupervisor : IDisposable
    {
        [DataContract]
        private sealed class ChatRequest
        {
            [DataMember(Name = "model")] public string Model { get; set; }
            [DataMember(Name = "messages")] public List<ChatMessage> Messages { get; set; }
            [DataMember(Name = "temperature")] public double Temperature { get; set; }
            [DataMember(Name = "max_tokens")] public int MaxTokens { get; set; }
        }

        [DataContract]
        private sealed class ChatMessage
        {
            [DataMember(Name = "role")] public string Role { get; set; }
            [DataMember(Name = "content")] public string Content { get; set; }
        }

        [DataContract]
        private sealed class ChatResponse
        {
            [DataMember(Name = "choices")] public List<ChatChoice> Choices { get; set; }
        }

        [DataContract]
        private sealed class ChatChoice
        {
            [DataMember(Name = "message")] public ChatMessage Message { get; set; }
        }

        private sealed class Observation
        {
            public FlightPhase Phase;
            public AircraftState State;
            public GuidanceTargets Baseline;
        }

        private readonly object _sync = new object();
        private readonly LlmProfile _profile;
        private readonly HttpClient _http;
        private readonly Timer _timer;
        private Observation _observation;
        private LlmDecision _lastDecision;
        private int _requestRunning;
        private long _requests;
        private long _successes;
        private int _consecutiveFailures;
        private string _lastError = "";
        private DateTime _circuitOpenUntilUtc;

        public LocalLlmSupervisor(LlmProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(0.5, profile.TimeoutSeconds)) };
            if (profile.Enabled)
            {
                int periodMs = (int)Math.Max(250.0, profile.IntervalSeconds * 1000.0);
                _timer = new Timer(OnTimer, null, periodMs, periodMs);
            }
        }

        public event Action<LlmDecision> DecisionUpdated;
        public event Action<string> StatusChanged;

        public LlmDecision LastDecision
        {
            get { lock (_sync) return _lastDecision; }
        }

        public LlmRuntimeHealth GetHealth()
        {
            lock (_sync)
            {
                return new LlmRuntimeHealth
                {
                    Requests = _requests, Successes = _successes,
                    ConsecutiveFailures = _consecutiveFailures, LastError = _lastError,
                    CircuitOpenUntilUtc = _circuitOpenUntilUtc
                };
            }
        }

        public void Observe(FlightPhase phase, AircraftState state, GuidanceTargets baseline)
        {
            if (!_profile.Enabled || state == null || baseline == null) return;
            lock (_sync) _observation = new Observation { Phase = phase, State = state, Baseline = baseline.Clone() };
        }

        public GuidanceTargets ApplyOverride(FlightPhase phase, AircraftState state, GuidanceTargets baseline)
        {
            if (!_profile.Enabled || !_profile.IsAssist || baseline == null) return baseline;
            LlmDecision decision = GetValidatedDecision(phase);
            if (decision == null) return baseline;
            if (phase != FlightPhase.Crosswind && phase != FlightPhase.Downwind && phase != FlightPhase.Base && phase != FlightPhase.Final)
                return baseline;
            if (string.Equals(decision.Intent, "GO_AROUND", StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision.Intent, "RETURN_CONTROL_TO_PILOT", StringComparison.OrdinalIgnoreCase))
                return baseline;

            baseline.HeadingTrueDeg = MathEx.NormalizeHeading(baseline.HeadingTrueDeg
                + MathEx.Clamp(decision.HeadingDeltaDeg, -_profile.MaximumHeadingChangeDeg, _profile.MaximumHeadingChangeDeg));
            baseline.AirspeedKts += MathEx.Clamp(decision.AirspeedDeltaKts,
                -_profile.MaximumSpeedChangeKts, _profile.MaximumSpeedChangeKts);
            baseline.Intent = "LLM_" + (decision.Intent ?? "ADJUST");
            return baseline;
        }

        public string GetValidatedIntent(FlightPhase phase)
        {
            LlmDecision decision = GetValidatedDecision(phase);
            return decision == null ? "" : decision.Intent;
        }

        private LlmDecision GetValidatedDecision(FlightPhase phase)
        {
            if (!_profile.Enabled || !_profile.IsAssist) return null;
            LlmDecision decision;
            lock (_sync) decision = _lastDecision;
            if (decision == null || decision.Confidence < _profile.MinimumConfidence) return null;
            if ((DateTime.UtcNow - decision.ReceivedUtc).TotalSeconds > _profile.IntervalSeconds * 2.5) return null;
            string intent = (decision.Intent ?? "").Trim().ToUpperInvariant();
            if (!IsAllowedIntent(intent)) return null;
            if (intent == "GO_AROUND" && phase != FlightPhase.Base && phase != FlightPhase.Final && phase != FlightPhase.Flare) return null;
            if (intent == "EXTEND_DOWNWIND" && phase != FlightPhase.Downwind) return null;
            return decision;
        }

        private void OnTimer(object ignored)
        {
            lock (_sync)
                if (DateTime.UtcNow < _circuitOpenUntilUtc) return;
            if (Interlocked.CompareExchange(ref _requestRunning, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    Interlocked.Increment(ref _requests);
                    bool produced = await RequestDecisionAsync().ConfigureAwait(false);
                    if (produced) lock (_sync)
                    {
                        _successes++;
                        _consecutiveFailures = 0;
                        _lastError = "";
                    }
                }
                catch (Exception ex)
                {
                    bool opened = false;
                    lock (_sync)
                    {
                        _consecutiveFailures++;
                        _lastError = ex.Message;
                        if (_consecutiveFailures >= _profile.MaximumConsecutiveFailures)
                        {
                            _circuitOpenUntilUtc = DateTime.UtcNow.AddSeconds(_profile.FailureCooldownSeconds);
                            opened = true;
                        }
                    }
                    StatusChanged?.Invoke(opened
                        ? "LLM circuit breaker opened; deterministic guidance remains active. " + ex.Message
                        : "LLM request failed; deterministic guidance remains active. " + ex.Message);
                }
                finally { Interlocked.Exchange(ref _requestRunning, 0); }
            });
        }

        private async Task<bool> RequestDecisionAsync()
        {
            Observation snapshot;
            lock (_sync) snapshot = _observation;
            if (snapshot == null) return false;

            string prompt = BuildPrompt(snapshot);
            var request = new ChatRequest
            {
                Model = _profile.Model,
                Temperature = 0.0,
                MaxTokens = 140,
                Messages = new List<ChatMessage>
                {
                    new ChatMessage
                    {
                        Role = "system",
                        Content = "You are a simulator flight supervisor. Return exactly one JSON object and no markdown. " +
                                  "Never output direct control-surface commands. Allowed intent: CONTINUE_PATTERN, EXTEND_DOWNWIND, " +
                                  "STABILIZE, GO_AROUND, HOLD_HEADING, or RETURN_CONTROL_TO_PILOT."
                    },
                    new ChatMessage { Role = "user", Content = prompt }
                }
            };

            string json = Serialize(request, typeof(ChatRequest));
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await _http.PostAsync(_profile.Endpoint, content).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue
                    && response.Content.Headers.ContentLength.Value > 65536)
                    throw new InvalidDataException("LLM response exceeded 64 KiB.");
                string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (responseJson.Length > 65536) throw new InvalidDataException("LLM response exceeded 64 KiB.");
                var parsed = (ChatResponse)Deserialize(responseJson, typeof(ChatResponse));
                if (parsed?.Choices == null || parsed.Choices.Count == 0 || parsed.Choices[0].Message == null)
                    throw new InvalidDataException("LLM response did not contain a choice.");
                string decisionJson = StripFence(parsed.Choices[0].Message.Content);
                var decision = (LlmDecision)Deserialize(decisionJson, typeof(LlmDecision));
                if (decision == null || !MathEx.AreFinite(decision.HeadingDeltaDeg, decision.AirspeedDeltaKts, decision.Confidence))
                    throw new InvalidDataException("LLM decision contained invalid numeric values.");
                decision.Intent = (decision.Intent ?? "").Trim().ToUpperInvariant();
                if (!IsAllowedIntent(decision.Intent)) throw new InvalidDataException("LLM returned an unsupported intent.");
                decision.Confidence = MathEx.Clamp(decision.Confidence, 0.0, 1.0);
                decision.Reason = (decision.Reason ?? "").Trim();
                if (decision.Reason.Length > 240) decision.Reason = decision.Reason.Substring(0, 240);
                decision.ReceivedUtc = DateTime.UtcNow;
                lock (_sync) _lastDecision = decision;
                DecisionUpdated?.Invoke(decision);
                return true;
            }
        }

        private static string BuildPrompt(Observation item)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "aircraft=" + item.State.AircraftTitle + "; role=" + item.State.ControlRole + "; phase={0}; IAS={1:0.0}kt; AGL={2:0}ft; VS={3:0}fpm; pitch={4:0.0}deg; bank={5:0.0}deg; heading={6:0.0}deg; " +
                "pilot_yoke_x={7:0.00}; pilot_yoke_y={8:0.00}; pilot_rudder={9:0.00}; pilot_throttle={10:0.00}; " +
                "baseline_heading={11:0.0}deg; baseline_speed={12:0.0}kt. Return " +
                "{{\"intent\":\"CONTINUE_PATTERN\",\"heading_delta_deg\":0,\"airspeed_delta_kts\":0,\"confidence\":0.0,\"reason\":\"short reason\"}}.",
                item.Phase, item.State.IndicatedAirspeedKts, item.State.AltitudeAglFt,
                item.State.VerticalSpeedFpm, item.State.PitchDeg, item.State.BankDeg,
                item.State.HeadingTrueDeg, item.State.YokeX, item.State.YokeY, item.State.RudderPedal,
                item.State.Throttle01, item.Baseline.HeadingTrueDeg, item.Baseline.AirspeedKts);
        }

        private static string Serialize(object value, Type type)
        {
            var serializer = new DataContractJsonSerializer(type);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static object Deserialize(string json, Type type)
        {
            var serializer = new DataContractJsonSerializer(type);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return serializer.ReadObject(stream);
        }

        private static string StripFence(string value)
        {
            string text = (value ?? "").Trim();
            if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
            int firstNewLine = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            return firstNewLine >= 0 && lastFence > firstNewLine
                ? text.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim()
                : text;
        }

        private static bool IsAllowedIntent(string intent)
        {
            return intent == "CONTINUE_PATTERN" || intent == "EXTEND_DOWNWIND" || intent == "STABILIZE"
                   || intent == "GO_AROUND" || intent == "HOLD_HEADING" || intent == "RETURN_CONTROL_TO_PILOT";
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _http.Dispose();
        }
    }
}
