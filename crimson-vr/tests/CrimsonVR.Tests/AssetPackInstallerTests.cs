using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

public sealed class AssetPackInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cvr-assets-{Guid.NewGuid():N}");

    public AssetPackInstallerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void InstallsCompletePack()
    {
        string pack = MakeValidPack();

        AssetPackInstaller.Result result = AssetPackInstaller.Install(pack, _root);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(_root, "assets", "crimson-assets.json")));
        Assert.True(File.Exists(Path.Combine(_root, "assets", "sprites", "sprite_manifest.json")));
    }

    [Fact]
    public void RejectsTraversalWithoutWritingOutsideStaging()
    {
        string pack = MakePack(("../escaped.txt", "bad"));

        AssetPackInstaller.Result result = AssetPackInstaller.Install(pack, _root);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void InvalidPackPreservesExistingInstallation()
    {
        string installed = Path.Combine(_root, "assets");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "keep.txt"), "working");
        string pack = MakePack(("crimson-assets.json", "{\"schema_version\":99}"));

        AssetPackInstaller.Result result = AssetPackInstaller.Install(pack, _root);

        Assert.False(result.Success);
        Assert.Equal("working", File.ReadAllText(Path.Combine(installed, "keep.txt")));
    }

    [Fact]
    public void RejectsPayloadWhoseHashDoesNotMatchManifest()
    {
        string marker = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema_version = 1,
            files = new Dictionary<string, string>
            {
                ["sprites/sprite_manifest.json"] = new string('0', 64),
                ["audio/audio_manifest.json"] = new string('0', 64),
            },
        });
        string pack = MakePack(("crimson-assets.json", marker),
            ("sprites/sprite_manifest.json", "tampered"),
            ("audio/audio_manifest.json", "tampered"));

        AssetPackInstaller.Result result = AssetPackInstaller.Install(pack, _root);

        Assert.False(result.Success);
        Assert.Contains("integrity", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "assets")));
    }

    [Fact]
    public void RejectsPayloadNotDeclaredByManifest()
    {
        string pack = MakeValidPack(("surprise.bin", "undeclared"));

        AssetPackInstaller.Result result = AssetPackInstaller.Install(pack, _root);

        Assert.False(result.Success);
        Assert.Contains("undeclared", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsDuplicateArchivePath()
    {
        string pack = MakePack(("same.txt", "one"), ("same.txt", "two"));

        AssetPackInstaller.Result result = AssetPackInstaller.Install(pack, _root);

        Assert.False(result.Success);
        Assert.Contains("duplicate", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 70_000_000, true)]
    [InlineData(-1, 70_000_000, true)]
    [InlineData(60_000_000, 70_000_000, false)]
    [InlineData(80_000_000, 70_000_000, true)]
    public void UnknownFreeSpaceDoesNotProduceFalseFailure(
        long available, long required, bool expected)
    {
        Assert.Equal(expected, AssetPackInstaller.HasEnoughReportedSpace(available, required));
    }

    private string MakePack(params (string Name, string Contents)[] entries)
    {
        string path = Path.Combine(_root, $"{Guid.NewGuid():N}.pack");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string contents) in entries)
        {
            using StreamWriter writer = new(archive.CreateEntry(name).Open());
            writer.Write(contents);
        }
        return path;
    }

    private string MakeValidPack(params (string Name, string Contents)[] extras)
    {
        const string sprites = "{}";
        const string audio = "{}";
        static string Hash(string value) => Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        string marker = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema_version = 1,
            files = new Dictionary<string, string>
            {
                ["sprites/sprite_manifest.json"] = Hash(sprites),
                ["audio/audio_manifest.json"] = Hash(audio),
            },
        });
        string contentHash = Hash(
            $"audio/audio_manifest.json\0{Hash(audio)}\n" +
            $"sprites/sprite_manifest.json\0{Hash(sprites)}\n");
        marker = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema_version = 1,
            content_sha256 = contentHash,
            files = new Dictionary<string, string>
            {
                ["sprites/sprite_manifest.json"] = Hash(sprites),
                ["audio/audio_manifest.json"] = Hash(audio),
            },
        });
        return MakePack(new[] { ("crimson-assets.json", marker),
            ("sprites/sprite_manifest.json", sprites), ("audio/audio_manifest.json", audio) }
            .Concat(extras).ToArray());
    }
}
