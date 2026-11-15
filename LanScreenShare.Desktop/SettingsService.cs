using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public static class SettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanScreenShare");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static readonly Lock FileLock = new();

        public static AppSettings Load(string? filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? SettingsFilePath : filePath;
            lock (FileLock)
            {
                try
                {
                    if (File.Exists(targetPath))
                    {
                        string json = File.ReadAllText(targetPath);
                        var settings = JsonSerializer.Deserialize<AppSettings>(json);
                        if (settings != null)
                            return settings;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] Error cargando configuración: {ex.Message}");
                }

                return new AppSettings();
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

