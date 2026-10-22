using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    [DataContract]
    internal sealed class ConnectionDiagnosticReport
    {
        [DataMember] public DateTime Utc { get; set; }
        [DataMember] public bool Is64BitProcess { get; set; }
        [DataMember] public string OperatingSystem { get; set; }
        [DataMember] public string Prepar3DPath { get; set; }
        [DataMember] public string Prepar3DVersion { get; set; }
        [DataMember] public bool Prepar3DProcessRunning { get; set; }
        [DataMember] public bool? FsuipcModuleLoadedInPrepar3D { get; set; }
        [DataMember] public string P3DSdkRoot { get; set; }
        [DataMember] public string SimConnectManagedDll { get; set; }
        [DataMember] public bool SimConnectManagedDllExists { get; set; }
        [DataMember] public string FsuipcPath { get; set; }
        [DataMember] public string FsuipcVersion { get; set; }
        [DataMember] public string FsuipcDiscoverySource { get; set; }
        [DataMember] public string ConfiguredMode { get; set; }
        [DataMember] public string PreferredTransport { get; set; }
        [DataMember] public IList<string> Findings { get; set; }
    }

    [DataContract]
    internal sealed class LiveTransportDiagnostic
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public bool Connected { get; set; }
        [DataMember] public bool Fresh { get; set; }
        [DataMember] public long Samples { get; set; }
        [DataMember] public long RejectedSamples { get; set; }
        [DataMember] public long ConnectionAttempts { get; set; }
        [DataMember] public int ConsecutiveFailures { get; set; }
        [DataMember] public string Version { get; set; }
        [DataMember] public string LastError { get; set; }
        [DataMember] public string ScenarioPath { get; set; }
        [DataMember] public long? ScenarioLoadCount { get; set; }
        [DataMember] public long? UpstreamReconnectCount { get; set; }
    }

    [DataContract]
    internal sealed class LiveConnectionDiagnosticReport
    {
        [DataMember] public string Schema { get; set; }
        [DataMember] public DateTime Utc { get; set; }
        [DataMember] public double TestDurationSeconds { get; set; }
        [DataMember] public string ActiveTransport { get; set; }
        [DataMember] public string AircraftTitle { get; set; }
        [DataMember] public bool Operational { get; set; }
        [DataMember] public bool FullyRedundant { get; set; }
        [DataMember] public bool DivergenceDetected { get; set; }
        [DataMember] public IList<LiveTransportDiagnostic> Transports { get; set; }
    }

    internal static class ConnectionDiagnostics
    {
        public static ConnectionDiagnosticReport Inspect(AppConfiguration configuration)
        {
            SimulatorInstallationInfo installation = SimulatorInstallationDetector.Detect();
            string sdkRoot = Environment.GetEnvironmentVariable("P3D_SDK_ROOT");
            string sdkManagedDll = string.IsNullOrWhiteSpace(sdkRoot) ? null
                : Path.Combine(sdkRoot, "lib", "SimConnect", "managed", "LockheedMartin.Prepar3D.SimConnect.dll");
            string bundledManagedDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LockheedMartin.Prepar3D.SimConnect.dll");
            string managedDll = !string.IsNullOrWhiteSpace(sdkManagedDll) && File.Exists(sdkManagedDll)
                ? sdkManagedDll
                : bundledManagedDll;
            var findings = new List<string>();
            Process[] p3dProcesses = Process.GetProcessesByName("Prepar3D");
            bool p3dRunning = p3dProcesses.Length > 0;
            bool? fsuipcLoaded = null;
            if (p3dRunning)
            {
                try
                {
                    fsuipcLoaded = p3dProcesses.Any(process => process.Modules.Cast<ProcessModule>().Any(module =>
                        string.Equals(module.ModuleName, "FSUIPC5.dll", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(module.ModuleName, "FSUIPC6.dll", StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex)
                {
                    findings.Add("NOTE: could not inspect Prepar3D modules; run diagnostics at the same elevation level as P3D: " + ex.Message);
                }
                finally
                {
                    foreach (Process process in p3dProcesses) process.Dispose();
                }
            }
            if (!Environment.Is64BitProcess) findings.Add("ERROR: process is not x64; P3D v4 and FSUIPC6 are 64-bit.");
            if (string.IsNullOrWhiteSpace(installation.Prepar3DPath)) findings.Add("ERROR: Prepar3D v4 was not found in Registry.");
            if (!p3dRunning) findings.Add("WARNING: Prepar3D.exe is not running; start P3D and fully load a scenario before the live test.");
            if (configuration.Simulator.Allows("SimConnect") && !File.Exists(managedDll))
                findings.Add("ERROR: matching managed SimConnect DLL was not found beside the application or under P3D_SDK_ROOT.");
            if (configuration.Simulator.Allows("FSUIPC") && configuration.Simulator.Fsuipc.Enabled
                && string.IsNullOrWhiteSpace(installation.FsuipcPath))
                findings.Add("WARNING: FSUIPC5/FSUIPC6 installation was not found. Use FSUIPC5 for P3D v4, or FSUIPC6 only with P3D v4.5+.");
            if (configuration.Simulator.Allows("FSUIPC") && configuration.Simulator.Fsuipc.Enabled && fsuipcLoaded == false)
                findings.Add("ERROR: Prepar3D is running but neither FSUIPC5.dll nor FSUIPC6.dll is loaded in its process. Check add-on.xml and the P3D Add-ons menu.");
            if (findings.Count == 0) findings.Add("Static installation checks passed. Start P3D and run the live connection test next.");
            foreach (string note in installation.Notes) findings.Add("NOTE: " + note);

            return new ConnectionDiagnosticReport
            {
                Utc = DateTime.UtcNow,
                Is64BitProcess = Environment.Is64BitProcess,
                OperatingSystem = Environment.OSVersion.ToString(),
                Prepar3DPath = installation.Prepar3DPath,
                Prepar3DVersion = installation.Prepar3DVersion,
                Prepar3DProcessRunning = p3dRunning,
                FsuipcModuleLoadedInPrepar3D = fsuipcLoaded,
                P3DSdkRoot = sdkRoot,
                SimConnectManagedDll = managedDll,
                SimConnectManagedDllExists = !string.IsNullOrWhiteSpace(managedDll) && File.Exists(managedDll),
                FsuipcPath = installation.FsuipcPath,
                FsuipcVersion = installation.FsuipcVersion,
                FsuipcDiscoverySource = installation.FsuipcDiscoverySource,
                ConfiguredMode = configuration.Simulator.Mode,
                PreferredTransport = configuration.Simulator.PreferredTransport,
                Findings = findings
            };
        }

        public static string WriteJson(ConnectionDiagnosticReport report, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = UniquePath(directory, "connection-diagnostic-");
            var serializer = new DataContractJsonSerializer(typeof(ConnectionDiagnosticReport));
            using (FileStream stream = File.Create(path)) serializer.WriteObject(stream, report);
            return path;
        }

        public static string WriteLiveJson(LiveConnectionDiagnosticReport report, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = UniquePath(directory, "live-connection-diagnostic-");
            var serializer = new DataContractJsonSerializer(typeof(LiveConnectionDiagnosticReport));
            using (FileStream stream = File.Create(path)) serializer.WriteObject(stream, report);
            return path;
        }

        private static string UniquePath(string directory, string prefix)
        {
            return Path.Combine(directory, prefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
        }
    }
}
