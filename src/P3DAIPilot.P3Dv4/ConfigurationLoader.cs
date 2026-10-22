using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal static class ConfigurationLoader
    {
        public static AppConfiguration Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Configuration file was not found.", path);
            var serializer = new DataContractJsonSerializer(typeof(AppConfiguration));
            // File.ReadAllText detects UTF BOMs; the serializer stream overload does not
            // reliably accept UTF-8 BOMs produced by Windows PowerShell Set-Content.
            byte[] json = Encoding.UTF8.GetBytes(File.ReadAllText(path));
            using (var stream = new MemoryStream(json, false))
            {
                var result = serializer.ReadObject(stream) as AppConfiguration;
                if (result == null) throw new InvalidDataException("Configuration JSON is invalid.");
                return result;
            }
        }

        public static string ResolvePath(string[] args)
        {
            if (args != null)
            {
                for (int index = 0; index < args.Length; index++)
                {
                    if (string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                        return Path.GetFullPath(args[index + 1]);
                    if (!args[index].StartsWith("--", StringComparison.Ordinal) && File.Exists(args[index]))
                        return Path.GetFullPath(args[index]);
                }
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "appsettings.json");
        }

        public static bool HasOption(string[] args, string option)
        {
            if (args == null) return false;
            foreach (string value in args)
                if (string.Equals(value, option, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string GetOptionValue(string[] args, string option)
        {
            if (args == null) return null;
            for (int index = 0; index + 1 < args.Length; index++)
                if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }
    }
}
