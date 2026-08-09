using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrimsonVR;

/// <summary>Validates and atomically installs a locally-created asset pack.</summary>
public static class AssetPackInstaller
{
    private const int MaxEntries = 4096;
    private const long MaxUncompressedBytes = 1024L * 1024L * 1024L;
    private const long FreeSpaceReserveBytes = 64L * 1024L * 1024L;

    public sealed record Result(bool Success, string Message);

    public static Result Install(string packPath, string userDataDirectory)
    {
        if (!File.Exists(packPath))
        {
            return new(false, "The selected CrimsonVR asset pack does not exist.");
        }

        string staging = Path.Combine(userDataDirectory, $"assets-import-{Guid.NewGuid():N}");
        string target = Path.Combine(userDataDirectory, "assets");
        string backup = Path.Combine(userDataDirectory, "assets-backup");
        try
        {
            Directory.CreateDirectory(staging);
            using (ZipArchive archive = ZipFile.OpenRead(packPath))
            {
                Result preflight = Preflight(archive, staging, userDataDirectory);
                if (!preflight.Success)
                {
                    return preflight;
                }
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    string stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                    if (!destination.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return new(false, "The asset pack contains an unsafe path.");
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            Result validation = Validate(staging);
            if (!validation.Success)
            {
                return validation;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }
                throw;
            }
            if (Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The new installation is already active. A stale backup
                    // is harmless and will be removed before the next import.
                }
            }
            return new(true, "Crimsonland Classic assets imported successfully.");
        }
        catch (InvalidDataException e)
        {
            return new(false, $"This is not a valid CrimsonVR asset pack: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, $"Could not import the asset pack: {e.Message}");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static Result Preflight(ZipArchive archive, string staging, string userDataDirectory)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            return new(false, $"The asset pack has too many entries ({archive.Entries.Count}).");
        }
        long expandedBytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, "The asset pack contains an unsafe path.");
            }
            if (!paths.Add(destination))
            {
                return new(false, $"The asset pack contains a duplicate path: {entry.FullName}");
            }
            if (entry.Length > MaxUncompressedBytes - expandedBytes)
            {
                return new(false, "The expanded asset pack is unreasonably large.");
            }
            expandedBytes += entry.Length;
        }
        // DriveInfo on Android/Quest resolves this app-private path to "/" and
        // reports zero bytes even when /data has ample room. Skip the advisory
        // check there; extraction remains bounded and staged, and a real ENOSPC
        // is caught without replacing the working installation.
        if (!OperatingSystem.IsAndroid())
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(userDataDirectory))!;
                long available = new DriveInfo(root).AvailableFreeSpace;
                long required = expandedBytes + FreeSpaceReserveBytes;
                if (!HasEnoughReportedSpace(available, required))
                {
                    return new(false,
                        $"Not enough free space to import assets ({required / 1024 / 1024} MiB required).");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Some storage providers do not expose DriveInfo. The hard
                // expanded-size cap still protects extraction; write errors
                // remain recoverable because installation is staged.
            }
        }
        return new(true, "Asset pack preflight passed.");
    }

    internal static bool HasEnoughReportedSpace(long available, long required) =>
        available <= 0 || available >= required;

    public static Result Validate(string directory)
    {
        string marker = Path.Combine(directory, AssetStore.InstalledMarker);
        string sprites = Path.Combine(directory, "sprites", "sprite_manifest.json");
        string audio = Path.Combine(directory, "audio", "audio_manifest.json");
        if (!File.Exists(marker) || !File.Exists(sprites) || !File.Exists(audio))
        {
            return new(false, "The pack is incomplete (marker, sprite manifest, or audio manifest is missing).");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(marker));
        if (!document.RootElement.TryGetProperty("schema_version", out JsonElement schema)
            || schema.GetInt32() != AssetStore.PackSchemaVersion)
        {
            return new(false, "The pack was made for an unsupported asset schema version.");
        }
        if (!document.RootElement.TryGetProperty("files", out JsonElement files)
            || files.ValueKind != JsonValueKind.Object)
        {
            return new(false, "The pack has no integrity manifest.");
        }
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentLines = new List<(string Name, string Hash)>();
        foreach (JsonProperty entry in files.EnumerateObject())
        {
            string relative = entry.Name.Replace('\\', '/');
            string extension = Path.GetExtension(relative);
            bool allowed = relative.StartsWith("sprites/", StringComparison.Ordinal)
                ? extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                : relative.StartsWith("audio/", StringComparison.Ordinal)
                    && (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".json", StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                return new(false, $"The pack declares an unsupported asset path: {entry.Name}");
            }
            string path = Path.GetFullPath(Path.Combine(directory, entry.Name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !declared.Add(path) || !File.Exists(path))
            {
                return new(false, $"The pack is missing or has an unsafe asset path: {entry.Name}");
            }
            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, entry.Value.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                return new(false, $"Asset integrity check failed: {entry.Name}");
            }
            contentLines.Add((entry.Name, actual));
        }
        foreach (string payload in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(payload, marker, StringComparison.OrdinalIgnoreCase)
                && !declared.Contains(Path.GetFullPath(payload)))
            {
                return new(false, $"The pack contains an undeclared asset: {Path.GetRelativePath(directory, payload)}");
            }
        }
        if (!document.RootElement.TryGetProperty("content_sha256", out JsonElement expectedContent))
        {
            return new(false, "The pack has no overall content hash.");
        }
        var content = new StringBuilder();
        foreach ((string name, string hash) in contentLines.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            content.Append(name).Append('\0').Append(hash).Append('\n');
        }
        string actualContent = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))).ToLowerInvariant();
        if (!string.Equals(actualContent, expectedContent.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "The pack's overall content hash does not match.");
        }
        return new(true, "Asset pack is valid.");
    }
}
