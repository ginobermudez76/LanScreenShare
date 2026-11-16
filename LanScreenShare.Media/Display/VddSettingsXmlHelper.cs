using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Media.Display
{
    public static class VddSettingsXmlHelper
    {
        public static int ReadMonitorCount(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
                return 0;

            try
            {
                var doc = XDocument.Parse(xmlContent);
                string? countText = doc.Root?.Element("monitors")?.Element("count")?.Value;
                return int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                    ? Math.Max(0, count)
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static string UpdateMonitorCountAndMode(string? existingXml, int monitorCount, VirtualDisplayMode mode)
        {
            var doc = ParseOrCreateRoot(existingXml);
            var root = doc.Root!;
            SetMonitorCount(root, Math.Max(0, monitorCount));
            EnsureResolutionEntry(root, mode);
            return doc.ToString();
        }

        public static string BuildSetDisplayCountCommand(int count) =>
            $"SETDISPLAYCOUNT {Math.Max(0, count)}";

        public static string BuildReloadDriverCommand() => "RELOAD_DRIVER";

        private static XDocument ParseOrCreateRoot(string? existingXml)
        {
            if (!string.IsNullOrWhiteSpace(existingXml))
            {
                try
                {
                    var parsed = XDocument.Parse(existingXml);
                    if (parsed.Root != null)
                        return parsed;
                }
                catch
                {
                    // Fallback to creating a fresh settings document
                }
            }

            return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("vdd_settings"));
        }

        private static void SetMonitorCount(XElement root, int count)
        {
            var monitors = GetOrCreateElement(root, "monitors");
            var countEl = GetOrCreateElement(monitors, "count");
            countEl.Value = count.ToString(CultureInfo.InvariantCulture);
        }

        private static void EnsureResolutionEntry(XElement root, VirtualDisplayMode mode)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in mode))
                return;

            var resolutions = GetOrCreateElement(root, "resolutions");
            bool alreadyPresent = resolutions.Elements("resolution").Any(el => MatchesMode(el, mode));
            if (alreadyPresent)
                return;

            resolutions.Add(new XElement("resolution",
                new XElement("width", mode.Width.ToString(CultureInfo.InvariantCulture)),
                new XElement("height", mode.Height.ToString(CultureInfo.InvariantCulture)),
                new XElement("refresh_rate", mode.RefreshRateHz.ToString(CultureInfo.InvariantCulture))));
        }

        private static bool MatchesMode(XElement resolutionEl, in VirtualDisplayMode mode)
        {
            bool widthMatch = TryGetInt(resolutionEl, "width", out int w) && w == mode.Width;
            bool heightMatch = TryGetInt(resolutionEl, "height", out int h) && h == mode.Height;
            bool hzMatch = TryGetInt(resolutionEl, "refresh_rate", out int hz) && hz == mode.RefreshRateHz;
            return widthMatch && heightMatch && hzMatch;
        }

        private static bool TryGetInt(XElement parent, string name, out int value)
        {
            string? text = parent.Element(name)?.Value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static XElement GetOrCreateElement(XElement parent, string name)
        {
            var el = parent.Element(name);
            if (el != null)
                return el;

            el = new XElement(name);
            parent.Add(el);
            return el;
        }
    }
}
