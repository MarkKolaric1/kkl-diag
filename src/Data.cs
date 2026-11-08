// Static data: control modules, fault code names and output parsing helpers.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace KklDiag
{
    public class Module
    {
        public string Address;   // hex, as VCDS shows it
        public string Name;
        public string Group;     // category in the module list
        public string Icon;      // Segoe MDL2 Assets glyph
        public bool Common;      // fitted to most 1996-2005 VAG cars; used by Auto-Scan
        public Module(string address, string name, string group, string icon, bool common)
        { Address = address; Name = name; Group = group; Icon = icon; Common = common; }
        public string Title { get { return Address + " · " + Name; } }
    }

    public class FaultCode
    {
        public string Code;      // 5-digit VAG code
        public string PCode;     // OBD-II equivalent when there is one
        public string Name;
        public int Type;         // fault type number from the module
        public bool Intermittent;
    }

    public static class Data
    {
        public static readonly List<Module> Modules = new List<Module>
        {
            new Module("01", "Engine",                 "Drivetrain",       "\uE945", true),
            new Module("02", "Auto Transmission",      "Drivetrain",       "\uE8AB", true),
            new Module("22", "All-Wheel Drive",        "Drivetrain",       "\uE804", false),
            new Module("03", "ABS Brakes",             "Chassis & Safety", "\uE804", true),
            new Module("15", "Airbags",                "Chassis & Safety", "\uEA18", true),
            new Module("16", "Steering Wheel",         "Chassis & Safety", "\uE804", false),
            new Module("44", "Power Steering",         "Chassis & Safety", "\uE804", false),
            new Module("17", "Instruments",            "Body & Comfort",   "\uEC4A", true),
            new Module("25", "Immobilizer",            "Body & Comfort",   "\uE72E", true),
            new Module("35", "Central Locking",        "Body & Comfort",   "\uE785", true),
            new Module("46", "Central Convenience",    "Body & Comfort",   "\uE713", true),
            new Module("08", "Climate Control",        "Body & Comfort",   "\uE9CA", true),
            new Module("09", "Central Electronics",    "Body & Comfort",   "\uE713", false),
            new Module("45", "Interior Monitoring",    "Body & Comfort",   "\uE7B3", false),
            new Module("55", "Headlight Range",        "Body & Comfort",   "\uE706", false),
            new Module("56", "Radio",                  "Audio & Extras",   "\uE7F5", true),
            new Module("37", "Navigation",             "Audio & Extras",   "\uE707", false),
            new Module("77", "Telephone",              "Audio & Extras",   "\uE717", false),
            new Module("76", "Parking Aid",            "Audio & Extras",   "\uE804", false),
            new Module("11", "Engine II",              "Drivetrain",       "\uE945", false),
            new Module("65", "Tire Pressure",          "Chassis & Safety", "\uE804", false),
            new Module("19", "CAN Gateway",            "Body & Comfort",   "\uE968", false),
            new Module("36", "Seat Memory Driver",     "Body & Comfort",   "\uE7B3", false),
            new Module("47", "Sound System",           "Audio & Extras",   "\uE7F5", false),
            new Module("57", "TV Tuner",               "Audio & Extras",   "\uE7F4", false),
        };

        public static Module FindModule(string address)
        {
            address = (address ?? "").Trim().ToUpperInvariant().TrimStart('0');
            foreach (var m in Modules)
                if (m.Address.TrimStart('0') == address) return m;
            return null;
        }

        // Well-known VAG fault codes. Anything not listed links to the Ross-Tech wiki.
        static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            { 283, "ABS Wheel Speed Sensor Front Left (G47)" },
            { 285, "ABS Wheel Speed Sensor Front Right (G45)" },
            { 287, "ABS Wheel Speed Sensor Rear Right (G44)" },
            { 290, "ABS Wheel Speed Sensor Rear Left (G46)" },
            { 513, "Engine Speed Sensor (G28)" },
            { 515, "Camshaft Position Sensor / Hall Sender (G40)" },
            { 518, "Throttle Position Sensor (G69)" },
            { 522, "Engine Coolant Temperature Sensor (G62)" },
            { 524, "Knock Sensor 1 (G61)" },
            { 525, "Oxygen (Lambda) Sensor (G39)" },
            { 527, "Intake Air Temperature Sensor (G42)" },
            { 532, "Supply Voltage B+" },
            { 533, "Idle Speed Regulation" },
            { 537, "Oxygen (Lambda) Regulation" },
            { 540, "Knock Sensor 2 (G66)" },
            { 553, "Mass Air Flow Sensor (G70)" },
            { 561, "Mixture Adaptation" },
            { 575, "Intake Manifold Pressure" },
            { 588, "Airbag Igniter, Driver Side (N95)" },
            { 589, "Airbag Igniter, Passenger Side (N131)" },
            { 625, "Vehicle Speed Signal" },
            { 668, "Supply Voltage Terminal 30" },
            { 771, "Fuel Level Sender (G)" },
            { 779, "Outside Air Temperature Sensor (G17)" },
            { 1044, "Control Module Incorrectly Coded" },
            { 1128, "Immobilizer Reading Coil (D2)" },
            { 1176, "Key (immobilizer transponder)" },
            { 1177, "Engine Control Module (immobilizer authorisation)" },
            { 1276, "ABS Hydraulic Pump (V64)" },
            { 1314, "Engine Control Module (no communication)" },
            { 1316, "ABS Control Module (no communication)" },
        };

        // Generic OBD-II meanings for the P-codes VAG engine modules report most often.
        static readonly Dictionary<string, string> PNames = new Dictionary<string, string>
        {
            { "P0102", "Mass Air Flow Sensor: Signal Too Low" },
            { "P0103", "Mass Air Flow Sensor: Signal Too High" },
            { "P0117", "Coolant Temperature Sensor: Signal Too Low" },
            { "P0118", "Coolant Temperature Sensor: Signal Too High" },
            { "P0130", "Oxygen Sensor (Bank 1, Sensor 1): Circuit Malfunction" },
            { "P0134", "Oxygen Sensor (Bank 1, Sensor 1): No Activity" },
            { "P0171", "System Too Lean (Bank 1)" },
            { "P0172", "System Too Rich (Bank 1)" },
            { "P0300", "Random / Multiple Cylinder Misfire" },
            { "P0301", "Cylinder 1 Misfire" },
            { "P0302", "Cylinder 2 Misfire" },
            { "P0303", "Cylinder 3 Misfire" },
            { "P0304", "Cylinder 4 Misfire" },
            { "P0420", "Catalytic Converter Efficiency Below Threshold (Bank 1)" },
            { "P0441", "EVAP System: Incorrect Purge Flow" },
            { "P0501", "Vehicle Speed Sensor: Range / Performance" },
            { "P1570", "Engine Start Blocked by Immobilizer" },
            { "P1602", "Power Supply (Terminal 30): Voltage Too Low" },
        };

        public static string ToPCode(int code)
        {
            // VAG engine modules report OBD-II codes as 16384 + P0xxx and 16408 + P1xxx..P3xxx.
            if (code >= 16384 && code <= 17383) return "P" + (code - 16384).ToString("D4");
            if (code >= 17408 && code <= 19999) return "P" + (code - 16408).ToString("D4");
            return null;
        }

        public static FaultCode MakeFault(int code, int type, int flag)
        {
            var f = new FaultCode();
            f.Code = code.ToString("D5");
            f.PCode = ToPCode(code);
            f.Type = type;
            f.Intermittent = flag == 10;
            string name;
            if (Names.TryGetValue(code, out name)) f.Name = name;
            else if (f.PCode != null && PNames.TryGetValue(f.PCode, out name)) f.Name = name;
            else if (f.PCode != null) f.Name = "OBD-II code " + f.PCode;
            else f.Name = "Unknown component";
            return f;
        }

        public static string WikiUrl(FaultCode f)
        {
            return "https://wiki.ross-tech.com/wiki/index.php/" + (f.PCode ?? f.Code);
        }

        // ---------- Parsing kw1281test output ----------

        public enum Outcome { Ok, Cancelled, NoCable, PortBusy, NoAnswer, WrongSpeed, NotSupported, Rejected, Error }

        public static Outcome Classify(string text)
        {
            if (text.Contains("Could not find file 'COM") || text.Contains("does not exist"))
                return Outcome.NoCable;
            if (text.Contains("Access to the path") || text.Contains("Unable to open port") || text.Contains("UnauthorizedAccess"))
                return Outcome.PortBusy;
            if (text.Contains("Controller did not wake up"))
                return text.Contains("Unexpected sync byte") ? Outcome.WrongSpeed : Outcome.NoAnswer;
            if (text.Contains("Expected KWP1281 protocol"))
                return Outcome.NotSupported;
            if (text.Contains("Received a NAK"))
                return Outcome.Rejected;
            if (text.Contains("Unhandled exception") || text.Contains("Expected an Adaptation response") || text.Contains("Failed to clear"))
                return Outcome.Error;
            return Outcome.Ok;
        }

        public static string Explain(Outcome o)
        {
            switch (o)
            {
                case Outcome.NoCable: return "Cable not found. Plug the KKL cable into the laptop and pick its COM port in the top bar.";
                case Outcome.PortBusy: return "The COM port is in use by another program (VCDS-Lite, Commander, WBH-Diag...). Close it and try again.";
                case Outcome.NoAnswer: return "The module didn't answer. Check the ignition is ON, the cable is pushed fully into the car, and that this module is fitted to your car.";
                case Outcome.WrongSpeed: return "The module answered at a different speed. Set Speed to Auto (or try 9600) in the top bar.";
                case Outcome.NotSupported: return "This module uses the newer KWP2000 protocol. Fault codes and identification work when the \"ABS & KWP2000 modules\" add-on is on (Settings). Live data, adaptation and coding aren't available for KWP2000 modules.";
                case Outcome.Rejected: return "The module refused the request. Usually a wrong login/PIN, or the value isn't allowed on this channel.";
                case Outcome.Cancelled: return "Stopped.";
                case Outcome.Error: return "Something went wrong talking to the module. See the Log page for details.";
                default: return "";
            }
        }

        static readonly Regex FaultLine = new Regex(@"^\s+(\d{5}) - (\d{2})-(\d{2})\s*$", RegexOptions.Multiline);

        public static List<FaultCode> ParseFaults(string text)
        {
            var list = new List<FaultCode>();
            int start = text.LastIndexOf("Fault codes:", StringComparison.Ordinal);
            if (start < 0) return list;
            foreach (Match m in FaultLine.Matches(text.Substring(start)))
                list.Add(MakeFault(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
            return list;
        }

        public static bool HasFaultList(string text) { return text.Contains("Fault codes:"); }

        public static List<string> ParseLines(string text, string prefix)
        {
            return text.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
                .Select(l => l.Substring(prefix.Length).Trim()).ToList();
        }

        public static string ParseValue(string text, string prefix)
        {
            var all = ParseLines(text, prefix);
            return all.Count > 0 ? all[all.Count - 1] : null;
        }

        static readonly Regex GroupLine = new Regex(@"Group (\d{3}): ([^\n]*)");

        // Latest "Group NNN: a | b | c | d" line from a live reading.
        public static string[] ParseGroup(string text, out int group)
        {
            group = -1;
            var ms = GroupLine.Matches(text);
            if (ms.Count == 0) return null;
            var last = ms[ms.Count - 1];
            group = int.Parse(last.Groups[1].Value);
            var body = last.Groups[2].Value.Trim();
            if (body.StartsWith("Not Available", StringComparison.Ordinal)) return new string[0];
            return body.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        public static string ParseActuator(string text)
        {
            if (text.Contains("End of test.")) return "End of test";
            return ParseValue(text, "Actuator Test:");
        }
    }
}
