using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal sealed class RuntimeHealthRecorder : IDisposable
    {
        private readonly StreamWriter _writer;

        public RuntimeHealthRecorder(string directory)
        {
            string resolved = Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, directory ?? "logs");
            Directory.CreateDirectory(resolved);
            FilePath = Path.Combine(resolved, "runtime_health_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl");
            _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false), 1024 * 16) { AutoFlush = true };
        }

        public string FilePath { get; private set; }

        public void Append(AircraftState state, AutopilotOutput output, SimulatorGatewayHealth gateway,
            bool recording, LlmDecision llm, LlmRuntimeHealth llmHealth, ResidualTrace residual)
        {
            string transports = gateway == null || gateway.Transports == null ? "[]" : "[" + string.Join(",",
                gateway.Transports.Select(t => "{" +
                    "\"name\":" + Q(t.Name) + "," +
                    "\"connected\":" + B(t.IsConnected) + "," +
                    "\"fresh_age_s\":" + F(t.LastTelemetryUtc == DateTime.MinValue ? -1.0 : (DateTime.UtcNow - t.LastTelemetryUtc).TotalSeconds) + "," +
                    "\"samples\":" + t.TelemetrySamples.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"rejected_samples\":" + t.RejectedTelemetrySamples.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"commands\":" + t.CommandsSent.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"connection_attempts\":" + t.ReconnectAttempts.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"consecutive_failures\":" + t.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"version\":" + Q(t.Version) + "," +
                    "\"last_error\":" + Q(t.LastError) + "}")) + "]";
            string json = "{" +
                "\"schema\":\"runtime-health-v1\"," +
                "\"utc\":" + Q(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) + "," +
                "\"active_transport\":" + Q(gateway == null ? "None" : gateway.ActiveTransport) + "," +
                "\"telemetry_source\":" + Q(state == null ? "" : state.TelemetrySource) + "," +
                "\"aircraft_title\":" + Q(state == null ? "" : state.AircraftTitle) + "," +
                "\"flight_phase\":" + Q(output == null ? "Unavailable" : output.Phase.ToString()) + "," +
                "\"control_applied\":" + B(output != null && output.ApplyControls) + "," +
                "\"safety\":" + Q(output == null || output.Safety == null ? "" : output.Safety.Reason) + "," +
                "\"recording\":" + B(recording) + "," +
                "\"llm_intent\":" + Q(llm == null ? "" : llm.Intent) + "," +
                "\"llm_requests\":" + (llmHealth == null ? "0" : llmHealth.Requests.ToString(CultureInfo.InvariantCulture)) + "," +
                "\"llm_successes\":" + (llmHealth == null ? "0" : llmHealth.Successes.ToString(CultureInfo.InvariantCulture)) + "," +
                "\"llm_consecutive_failures\":" + (llmHealth == null ? "0" : llmHealth.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture)) + "," +
                "\"llm_last_error\":" + Q(llmHealth == null ? "" : llmHealth.LastError) + "," +
                "\"rl_intervened\":" + B(residual != null && residual.Intervened) + "," +
                "\"telemetry_divergent\":" + B(gateway != null && gateway.DivergenceDetected) + "," +
                "\"divergence_duration_s\":" + F(gateway == null ? 0.0 : gateway.DivergenceDurationSeconds) + "," +
                "\"control_inhibited\":" + B(gateway != null && gateway.ControlInhibited) + "," +
                "\"transports\":" + transports + "}";
            _writer.WriteLine(json);
        }

        private static string F(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string B(bool value) { return value ? "true" : "false"; }
        private static string Q(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }
        public void Dispose() { _writer.Dispose(); }
    }
}
