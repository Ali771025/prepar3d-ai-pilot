using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    [DataContract]
    internal sealed class AircraftConfigurationProfile
    {
        [DataMember(Name = "schema")] public string Schema { get; set; }
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "display_name")] public string DisplayName { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "match_titles")] public string[] MatchTitles { get; set; }
        [DataMember(Name = "auto_tune_from_design_speeds")] public bool AutoTuneFromAircraftDesignSpeeds { get; set; }
        [DataMember(Name = "aircraft")] public AircraftProfile Aircraft { get; set; }
        [DataMember(Name = "pattern")] public PatternProfile Pattern { get; set; }
        [DataMember(Name = "controllers")] public ControllerProfile Controllers { get; set; }
        [DataMember(Name = "safety")] public SafetyProfile Safety { get; set; }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (!string.Equals(Schema, "aircraft-profile-v4", StringComparison.Ordinal))
                errors.Add("schema must be aircraft-profile-v4.");
            if (string.IsNullOrWhiteSpace(Id)) errors.Add("id is required.");
            if (string.IsNullOrWhiteSpace(DisplayName)) errors.Add("display_name is required.");
            if (MatchTitles == null || MatchTitles.All(string.IsNullOrWhiteSpace))
                errors.Add("at least one non-empty match_titles entry is required.");
            if (Aircraft == null) errors.Add("aircraft settings are required.");
            else errors.AddRange(Aircraft.Validate());
            if (Pattern == null) errors.Add("pattern settings are required.");
            else errors.AddRange(Pattern.Validate());
            if (Controllers == null) errors.Add("controllers settings are required.");
            else errors.AddRange(Controllers.Validate());
            if (Safety == null) errors.Add("safety settings are required.");
            else errors.AddRange(Safety.Validate());
            return errors;
        }

        public void ApplyTo(AppConfiguration configuration)
        {
            configuration.AutoTuneFromAircraftDesignSpeeds = AutoTuneFromAircraftDesignSpeeds;
            configuration.Aircraft = Aircraft;
            configuration.Pattern = Pattern;
            configuration.Controllers = Controllers;
            configuration.Safety = Safety;
        }
    }

    internal sealed class AircraftProfileCatalog
    {
        private readonly IList<AircraftConfigurationProfile> _profiles;
        private readonly AircraftProfileSelection _selection;

        private AircraftProfileCatalog(AircraftProfileSelection selection, IList<AircraftConfigurationProfile> profiles)
        {
            _selection = selection;
            _profiles = profiles;
        }

        public bool Enabled { get { return _selection != null && _selection.Enabled; } }
        public bool RequireMatch { get { return Enabled && _selection.RequireMatch; } }

        public static AircraftProfileCatalog Load(AircraftProfileSelection selection, string configurationDirectory)
        {
            if (selection == null || !selection.Enabled)
                return new AircraftProfileCatalog(selection, new List<AircraftConfigurationProfile>());

            string directory = Path.IsPathRooted(selection.Directory)
                ? selection.Directory
                : Path.GetFullPath(Path.Combine(configurationDirectory, selection.Directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("Aircraft profile directory was not found: " + directory);

            var serializer = new DataContractJsonSerializer(typeof(AircraftConfigurationProfile));
            var profiles = new List<AircraftConfigurationProfile>();
            foreach (string path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(x => x))
            {
                AircraftConfigurationProfile profile;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path)), false))
                    profile = serializer.ReadObject(stream) as AircraftConfigurationProfile;
                if (profile == null) throw new InvalidDataException("Aircraft profile JSON is invalid: " + path);
                IList<string> errors = profile.Validate();
                if (errors.Count > 0)
                    throw new InvalidDataException("Aircraft profile " + path + " is invalid: " + string.Join(" | ", errors));
                if (profiles.Any(x => string.Equals(x.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Duplicate aircraft profile id: " + profile.Id);
                profiles.Add(profile);
            }
            if (profiles.Count == 0) throw new InvalidDataException("No aircraft profile JSON files were found in " + directory);
            if (!profiles.Any(x => string.Equals(x.Id, selection.DefaultProfileId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Default aircraft profile was not found: " + selection.DefaultProfileId);
            return new AircraftProfileCatalog(selection, profiles);
        }

        public AircraftConfigurationProfile Resolve(string aircraftTitle, string explicitProfileId, out bool exactMatch)
        {
            exactMatch = false;
            if (!Enabled) return null;
            if (!string.IsNullOrWhiteSpace(explicitProfileId))
            {
                AircraftConfigurationProfile explicitProfile = _profiles.FirstOrDefault(
                    x => string.Equals(x.Id, explicitProfileId, StringComparison.OrdinalIgnoreCase));
                if (explicitProfile == null) throw new InvalidDataException("Requested aircraft profile was not found: " + explicitProfileId);
                exactMatch = true;
                return explicitProfile;
            }

            string title = (aircraftTitle ?? "").Trim();
            AircraftConfigurationProfile matched = _profiles
                .SelectMany(profile => (profile.MatchTitles ?? new string[0])
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => new { Profile = profile, Alias = alias.Trim() }))
                .Where(candidate => title.IndexOf(candidate.Alias, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(candidate => candidate.Alias.Length)
                .Select(candidate => candidate.Profile)
                .FirstOrDefault();
            if (matched != null)
            {
                exactMatch = true;
                return matched;
            }
            if (_selection.RequireMatch && !string.IsNullOrWhiteSpace(title)) return null;
            return _profiles.First(x => string.Equals(x.Id, _selection.DefaultProfileId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
