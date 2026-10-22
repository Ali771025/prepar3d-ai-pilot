using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace P3DAIPilot.P3Dv4
{
    internal sealed class SimulatorInstallationInfo
    {
        public string Prepar3DPath { get; set; }
        public string Prepar3DVersion { get; set; }
        public string FsuipcPath { get; set; }
        public string FsuipcVersion { get; set; }
        public string FsuipcDiscoverySource { get; set; }
        public IList<string> RegistryLocationsChecked { get; set; }
        public IList<string> Notes { get; set; }
    }

    internal static class SimulatorInstallationDetector
    {
        private static readonly string[] P3DRegistryPaths =
        {
            @"SOFTWARE\Lockheed Martin\Prepar3D v4",
            @"SOFTWARE\WOW6432Node\Lockheed Martin\Prepar3D v4"
        };

        public static SimulatorInstallationInfo Detect()
        {
            var checkedLocations = new List<string>();
            var notes = new List<string>();
            string p3dPath = FindP3DPath(checkedLocations, notes);
            string fsuipcPath;
            string source;
            FindFsuipc(checkedLocations, notes, out fsuipcPath, out source);

            return new SimulatorInstallationInfo
            {
                Prepar3DPath = p3dPath,
                Prepar3DVersion = p3dPath == null ? null : FileVersion(Path.Combine(p3dPath, "Prepar3D.exe")),
                FsuipcPath = fsuipcPath,
                FsuipcVersion = fsuipcPath == null ? null : FileVersion(FindFsuipcDll(fsuipcPath)),
                FsuipcDiscoverySource = source,
                RegistryLocationsChecked = checkedLocations,
                Notes = notes
            };
        }

        private static string FindP3DPath(ICollection<string> checkedLocations, ICollection<string> notes)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    foreach (string path in P3DRegistryPaths)
                    {
                        string label = hive + "[" + view + "]\\" + path;
                        checkedLocations.Add(label);
                        try
                        {
                            using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                            using (RegistryKey key = root.OpenSubKey(path))
                            {
                                string candidate = key == null ? null : (key.GetValue("SetupPath") ?? key.GetValue("AppPath")) as string;
                                if (HasFile(candidate, "Prepar3D.exe")) return Path.GetFullPath(candidate);
                            }
                        }
                        catch (Exception ex)
                        {
                            notes.Add("Could not inspect Registry location " + label + ": " + ex.Message);
                        }
                    }
                }
            }
            return null;
        }

        private static void FindFsuipc(ICollection<string> checkedLocations, ICollection<string> notes,
            out string path, out string source)
        {
            path = null;
            source = null;
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
                    checkedLocations.Add(hive + "[" + view + "]\\" + uninstall + "\\*");
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey parent = root.OpenSubKey(uninstall))
                        {
                            if (parent == null) continue;
                            foreach (string name in parent.GetSubKeyNames())
                            {
                                using (RegistryKey key = parent.OpenSubKey(name))
                                {
                                    string display = key == null ? null : key.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(display)
                                        || (display.IndexOf("FSUIPC6", StringComparison.OrdinalIgnoreCase) < 0
                                            && display.IndexOf("FSUIPC5", StringComparison.OrdinalIgnoreCase) < 0))
                                        continue;
                                    string candidate = key.GetValue("InstallLocation") as string;
                                    if (FindFsuipcDll(candidate) == null)
                                        candidate = DirectoryFromCommand(key.GetValue("UninstallString") as string);
                                    if (FindFsuipcDll(candidate) != null)
                                    {
                                        path = Path.GetFullPath(candidate);
                                        source = "Windows Registry uninstall entry";
                                        return;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add("Could not inspect Registry uninstall entries for " + hive + "[" + view + "]: " + ex.Message);
                    }
                }
            }

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string addOnRoot = Path.Combine(documents, "Prepar3D v4 Add-ons");
            if (Directory.Exists(addOnRoot))
            {
                foreach (string xmlPath in SafeEnumerateAddOnXml(addOnRoot, notes))
                {
                    string candidate = FindFsuipcPathInAddOnXml(xmlPath, notes);
                    if (FindFsuipcDll(candidate) != null)
                    {
                        path = Path.GetFullPath(candidate);
                        source = xmlPath;
                        return;
                    }
                }
            }
            notes.Add("FSUIPC5/FSUIPC6 was not located in uninstall Registry entries or Prepar3D v4 Add-ons.");
        }

        private static IEnumerable<string> SafeEnumerateAddOnXml(string root, ICollection<string> notes)
        {
            var results = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                try
                {
                    results.AddRange(Directory.EnumerateFiles(directory, "add-on.xml", SearchOption.TopDirectoryOnly));
                    foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        if ((new DirectoryInfo(child).Attributes & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                }
                catch (Exception ex)
                {
                    notes.Add("Could not enumerate add-on directory " + directory + ": " + ex.Message);
                }
            }
            return results;
        }

        private static string FindFsuipcPathInAddOnXml(string xmlPath, ICollection<string> notes)
        {
            try
            {
                var document = new XmlDocument { XmlResolver = null };
                document.Load(xmlPath);
                foreach (XmlNode node in document.SelectNodes("//*[local-name()='Path']"))
                {
                    string value = Environment.ExpandEnvironmentVariables((node.InnerText ?? "").Trim());
                    if (!Path.IsPathRooted(value)) value = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(xmlPath), value));
                    if (File.Exists(value)
                        && (string.Equals(Path.GetFileName(value), "FSUIPC6.dll", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Path.GetFileName(value), "FSUIPC5.dll", StringComparison.OrdinalIgnoreCase)))
                        return Path.GetDirectoryName(value);
                    if (FindFsuipcDll(value) != null) return value;
                }
            }
            catch (Exception ex)
            {
                notes.Add("Could not inspect " + xmlPath + ": " + ex.Message);
            }
            return null;
        }

        private static string DirectoryFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string text = command.Trim();
            string executable;
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = text.IndexOf('"', 1);
                executable = end > 1 ? text.Substring(1, end - 1) : text.Trim('"');
            }
            else
            {
                int end = text.IndexOf(' ');
                executable = end > 0 ? text.Substring(0, end) : text;
            }
            return Path.GetDirectoryName(executable);
        }

        private static bool HasFile(string directory, string fileName)
        {
            return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, fileName));
        }

        private static string FindFsuipcDll(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;
            string fsuipc6 = Path.Combine(directory, "FSUIPC6.dll");
            if (File.Exists(fsuipc6)) return fsuipc6;
            string fsuipc5 = Path.Combine(directory, "FSUIPC5.dll");
            return File.Exists(fsuipc5) ? fsuipc5 : null;
        }

        private static string FileVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
    }
}
