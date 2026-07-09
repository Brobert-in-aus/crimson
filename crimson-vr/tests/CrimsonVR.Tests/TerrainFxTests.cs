using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

/// <summary>
/// Locks the packed layout contract for the ABI v3 terrain-fx drain
/// (crimson_host_terrain_fx). If the Zig extern structs (host_abi/exports.zig)
/// and the C# structs (Sim.cs) ever drift, TerrainFxView would silently
/// mis-decode (the header carries no per-field magic), so pin it with a
/// hand-built buffer round-trip.
/// </summary>
public class TerrainFxTests
{
    private static void Write<T>(byte[] buf, ref int off, T value) where T : struct
    {
        MemoryMarshal.Write(buf.AsSpan(off), in value);
        off += Unsafe.SizeOf<T>();
    }

    [Fact]
    public void DecodesHeaderDecalsAndCorpses()
    {
        var header = new Sim.TerrainFxHeader { Version = 3, DecalCount = 2, CorpseCount = 1 };
        var d0 = new Sim.TerrainDecalSnap
        {
            EffectId = 5, X = 10f, Y = 20f, Width = 30f, Height = 31f,
            Rotation = 1.5f, R = 0.84f, G = 0.85f, B = 0.86f, A = 0.78f,
        };
        var d1 = new Sim.TerrainDecalSnap
        {
            EffectId = 7, X = -40f, Y = 1064f, Width = 12f, Height = 12f,
            Rotation = -0.25f, R = 0.9f, G = 0.9f, B = 0.9f, A = 0.5f,
        };
        var c0 = new Sim.TerrainCorpseSnap
        {
            CreatureTypeId = 2, X = 100f, Y = 200f, Rotation = 3.0f, Scale = 48f,
            R = 0.6f, G = 0.2f, B = 0.2f, A = 1.0f,
        };

        int size = Unsafe.SizeOf<Sim.TerrainFxHeader>()
            + 2 * Unsafe.SizeOf<Sim.TerrainDecalSnap>()
            + Unsafe.SizeOf<Sim.TerrainCorpseSnap>();
        var buf = new byte[size];
        int off = 0;
        Write(buf, ref off, header);
        Write(buf, ref off, d0);
        Write(buf, ref off, d1);
        Write(buf, ref off, c0);
        Assert.Equal(size, off);

        var view = new TerrainFxView(buf);
        Assert.Equal(3u, view.Header.Version);
        Assert.Equal(2u, view.Header.DecalCount);
        Assert.Equal(1u, view.Header.CorpseCount);

        Assert.Equal(2, view.Decals.Length);
        Assert.Equal(5, view.Decals[0].EffectId);
        Assert.Equal(30f, view.Decals[0].Width);
        Assert.Equal(0.78f, view.Decals[0].A);
        Assert.Equal(7, view.Decals[1].EffectId);
        Assert.Equal(1064f, view.Decals[1].Y);

        Assert.Equal(1, view.Corpses.Length);
        Assert.Equal(2, view.Corpses[0].CreatureTypeId);
        Assert.Equal(48f, view.Corpses[0].Scale);
        Assert.Equal(3.0f, view.Corpses[0].Rotation);
        Assert.Equal(1.0f, view.Corpses[0].A);
    }

    [Fact]
    public void DecodesEmptyBatch()
    {
        var header = new Sim.TerrainFxHeader { Version = 3, DecalCount = 0, CorpseCount = 0 };
        var buf = new byte[Unsafe.SizeOf<Sim.TerrainFxHeader>()];
        int off = 0;
        Write(buf, ref off, header);

        var view = new TerrainFxView(buf);
        Assert.Equal(0u, view.Header.DecalCount);
        Assert.Equal(0, view.Decals.Length);
        Assert.Equal(0, view.Corpses.Length);
    }

    [Fact]
    public void StructSizesMatchPackedAbi()
    {
        // 4-byte fields, no padding (crimson_host.h contract).
        Assert.Equal(12, Unsafe.SizeOf<Sim.TerrainFxHeader>());
        Assert.Equal(40, Unsafe.SizeOf<Sim.TerrainDecalSnap>());
        Assert.Equal(36, Unsafe.SizeOf<Sim.TerrainCorpseSnap>());
        Assert.Equal(24, Unsafe.SizeOf<Sim.TerrainInfo>());
    }
}
