using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public sealed record InstallDefaultsConfig
    {
        public string DefaultDownloadDirectory { get; init; } = string.Empty;
    }

    public static class SettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanScreenShare");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        private static readonly string DefaultInstallDefaultsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LanScreenShare",
            "install-defaults.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static readonly Lock FileLock = new();

        public static AppSettings Load(string? filePath = null) =>
            Load(filePath, DefaultInstallDefaultsFilePath);

        internal static AppSettings Load(string? filePath, string? installDefaultsPath)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? SettingsFilePath : filePath;
            lock (FileLock)
            {
                var settings = TryDeserialize<AppSettings>(targetPath, "configuración") ?? new AppSettings();
                ApplyInstallDefaults(settings, installDefaultsPath);
                return settings;
            }
        }

        private static void ApplyInstallDefaults(AppSettings settings, string? installDefaultsPath)
        {
            if (!string.IsNullOrWhiteSpace(settings.DownloadDirectory) || string.IsNullOrWhiteSpace(installDefaultsPath))
                return;

            var defaults = TryDeserialize<InstallDefaultsConfig>(installDefaultsPath, "defaults de instalación");
            if (!string.IsNullOrWhiteSpace(defaults?.DefaultDownloadDirectory))
            {
                settings.DownloadDirectory = defaults.DefaultDownloadDirectory.Trim();
            }
        }

        private static T? TryDeserialize<T>(string path, string label) where T : class
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Error cargando {label}: {ex.Message}");
                return null;
            }
        }

        public static void Save(AppSettings settings, string? filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? SettingsFilePath : filePath;
            lock (FileLock)
            {
                try
                {
                    string? targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    string json = JsonSerializer.Serialize(settings, JsonOptions);
                    File.WriteAllText(targetPath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] Error guardando configuración: {ex.Message}");
                }
            }
        }
    }
}

