using System;
using System.IO;
using Godot;

namespace CrimsonVR;

/// <summary>
/// Single entry point for user-supplied game assets. A complete installation in
/// <c>user://assets</c> wins; developer assets under <c>res://assets</c> remain
/// as a fallback so local editor/export workflows do not change.
/// </summary>
public static class AssetStore
{
    public const int PackSchemaVersion = 1;
    public const string InstalledMarker = "crimson-assets.json";

    private const string UserRoot = "user://assets";
    private const string DevelopmentRoot = "res://assets";

    public static string Root { get; private set; } = DevelopmentRoot;
    public static bool UsesUserAssets => Root == UserRoot;
    public static bool HasCompleteAssets { get; private set; }

    public static string SpriteDir => Root + "/sprites/";
    public static string AudioDir => Root + "/audio/";

    /// <summary>
    /// Import a pack delivered by ADB to this app's external-files inbox. This
    /// location needs no Android storage permission and is writable by ADB.
    /// The helper always uses the well-known pending filename; after success we
    /// archive it so startup does not re-hash/reinstall it on every launch.
    /// </summary>
    public static AssetPackInstaller.Result? ImportPendingPack()
    {
        string inbox;
        if (OS.GetName() == "Android")
        {
            string external = OS.GetEnvironment("EXTERNAL_STORAGE");
            if (string.IsNullOrWhiteSpace(external))
            {
                external = "/sdcard";
            }
            inbox = Path.Combine(external, "Android", "data", "xyz.crimsonvr.app",
                "files", "crimson-assets.pack");
        }
        else
        {
            inbox = ProjectSettings.GlobalizePath("user://crimson-assets.pack");
        }
        if (!File.Exists(inbox))
        {
            return null;
        }

        AssetPackInstaller.Result result = AssetPackInstaller.Install(
            inbox, ProjectSettings.GlobalizePath("user://"));
        if (result.Success)
        {
            string archive = Path.Combine(Path.GetDirectoryName(inbox)!, "crimson-assets.imported.pack");
            try
            {
                File.Move(inbox, archive, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                GD.PushWarning($"CrimsonVR: imported assets but could not archive inbox pack: {e.Message}");
            }
        }
        return result;
    }

    public static void Initialize()
    {
        string userMarker = UserRoot + "/" + InstalledMarker;
        bool userComplete = Godot.FileAccess.FileExists(userMarker)
            && Godot.FileAccess.FileExists(UserRoot + "/sprites/sprite_manifest.json")
            && Godot.FileAccess.FileExists(UserRoot + "/audio/audio_manifest.json");

        Root = userComplete ? UserRoot : DevelopmentRoot;
        HasCompleteAssets = userComplete
            || (Godot.FileAccess.FileExists(DevelopmentRoot + "/sprites/sprite_manifest.json")
                && Godot.FileAccess.FileExists(DevelopmentRoot + "/audio/audio_manifest.json"));

        GD.Print($"CrimsonVR: asset root = {Root} ({(HasCompleteAssets ? "ready" : "incomplete")})");
    }

    public static string SpritePath(string name) => SpriteDir + name;
    public static string AudioPath(string name) => AudioDir + name;

    public static Texture2D? LoadTexture(string path)
    {
        if (path.StartsWith("res://", StringComparison.Ordinal))
        {
            return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
        }
        if (!Godot.FileAccess.FileExists(path))
        {
            return null;
        }

        var image = new Image();
        Error error = image.Load(ProjectSettings.GlobalizePath(path));
        if (error != Error.Ok)
        {
            GD.PushWarning($"CrimsonVR: could not load texture {path}: {error}");
            return null;
        }
        return ImageTexture.CreateFromImage(image);
    }

    public static AudioStream? LoadAudio(string path)
    {
        if (path.StartsWith("res://", StringComparison.Ordinal))
        {
            return ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
        }
        if (!Godot.FileAccess.FileExists(path))
        {
            return null;
        }

        string absolute = ProjectSettings.GlobalizePath(path);
        return Path.GetExtension(absolute).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? AudioStreamOggVorbis.LoadFromFile(absolute)
            : null;
    }
}
