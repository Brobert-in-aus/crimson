using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CrimsonVR;

/// <summary>
/// A zero-copy view over one packed snapshot payload (see crimson_host.h and
/// Sim.cs). Entity spans alias the underlying byte buffer, which stays valid
/// until the owning <see cref="SimSession"/> reuses that buffer slot.
/// </summary>
public readonly ref struct SnapshotView
{
    public readonly Sim.SnapshotHeader Header;
    private readonly ReadOnlySpan<byte> _buf;
    private readonly int _playersOff;
    private readonly int _creaturesOff;
    private readonly int _projectilesOff;
    private readonly int _secondariesOff;
    private readonly int _bonusesOff;
    private readonly int _particlesOff;
    private readonly int _glowsOff;
    private readonly int _spriteFxOff;

    public SnapshotView(ReadOnlySpan<byte> buf)
    {
        Header = MemoryMarshal.Read<Sim.SnapshotHeader>(buf);
        if (Header.Magic != Sim.SnapshotMagic)
        {
            throw new InvalidOperationException($"bad snapshot magic 0x{Header.Magic:X8}");
        }
        _buf = buf;

        int off = Unsafe.SizeOf<Sim.SnapshotHeader>();
        _playersOff = off;
        off += (int)Header.PlayerCount * Unsafe.SizeOf<Sim.PlayerSnap>();
        _creaturesOff = off;
        off += (int)Header.CreatureCount * Unsafe.SizeOf<Sim.CreatureSnap>();
        _projectilesOff = off;
        off += (int)Header.ProjectileCount * Unsafe.SizeOf<Sim.ProjectileSnap>();
        _secondariesOff = off;
        off += (int)Header.SecondaryCount * Unsafe.SizeOf<Sim.SecondarySnap>();
        _bonusesOff = off;
        off += (int)Header.BonusCount * Unsafe.SizeOf<Sim.BonusSnap>();
        _particlesOff = off;
        off += (int)Header.ParticleCount * Unsafe.SizeOf<Sim.ParticleSnap>();
        _glowsOff = off;
        off += (int)Header.GlowCount * Unsafe.SizeOf<Sim.ParticleGlowSnap>();
        _spriteFxOff = off;
    }

    public ReadOnlySpan<Sim.PlayerSnap> Players
        => Cast<Sim.PlayerSnap>(_playersOff, Header.PlayerCount);
    public ReadOnlySpan<Sim.CreatureSnap> Creatures
        => Cast<Sim.CreatureSnap>(_creaturesOff, Header.CreatureCount);
    public ReadOnlySpan<Sim.ProjectileSnap> Projectiles
        => Cast<Sim.ProjectileSnap>(_projectilesOff, Header.ProjectileCount);
    public ReadOnlySpan<Sim.SecondarySnap> Secondaries
        => Cast<Sim.SecondarySnap>(_secondariesOff, Header.SecondaryCount);
    public ReadOnlySpan<Sim.BonusSnap> Bonuses
        => Cast<Sim.BonusSnap>(_bonusesOff, Header.BonusCount);
    public ReadOnlySpan<Sim.ParticleSnap> Particles
        => Cast<Sim.ParticleSnap>(_particlesOff, Header.ParticleCount);
    public ReadOnlySpan<Sim.ParticleGlowSnap> Glows
        => Cast<Sim.ParticleGlowSnap>(_glowsOff, Header.GlowCount);
    public ReadOnlySpan<Sim.SpriteEffectSnap> SpriteEffects
        => Cast<Sim.SpriteEffectSnap>(_spriteFxOff, Header.SpriteEffectCount);

    public ulong Tick => ((ulong)Header.TickHi << 32) | Header.TickLo;

    private ReadOnlySpan<T> Cast<T>(int offset, uint count) where T : struct
        => MemoryMarshal.Cast<byte, T>(_buf.Slice(offset, (int)count * Unsafe.SizeOf<T>()));
}

/// <summary>
/// A zero-copy view over one packed audio-events payload (crimson_host_audio_events,
/// see Sim.AudioHeader). Spans alias the owning session's audio buffer, valid
/// until the next <see cref="SimSession.CaptureAudio"/> call.
/// </summary>
public readonly ref struct AudioEventsView
{
    public readonly Sim.AudioHeader Header;
    private readonly ReadOnlySpan<byte> _buf;
    private readonly int _shotsOff;
    private readonly int _reloadsOff;
    private readonly int _hitsOff;
    private readonly int _sfxOff;

    public AudioEventsView(ReadOnlySpan<byte> buf)
    {
        Header = MemoryMarshal.Read<Sim.AudioHeader>(buf);
        _buf = buf;
        int off = Unsafe.SizeOf<Sim.AudioHeader>();
        _shotsOff = off;
        off += (int)Header.ShotCount * Unsafe.SizeOf<Sim.ShotAudioSnap>();
        _reloadsOff = off;
        off += (int)Header.ReloadCount * sizeof(int);
        _hitsOff = off;
        off += (int)Header.HitCount * Unsafe.SizeOf<Sim.HitAudioSnap>();
        _sfxOff = off;
    }

    public ReadOnlySpan<Sim.ShotAudioSnap> Shots => Cast<Sim.ShotAudioSnap>(_shotsOff, Header.ShotCount);
    public ReadOnlySpan<int> Reloads => Cast<int>(_reloadsOff, Header.ReloadCount);
    public ReadOnlySpan<Sim.HitAudioSnap> Hits => Cast<Sim.HitAudioSnap>(_hitsOff, Header.HitCount);
    public ReadOnlySpan<int> Sfx => Cast<int>(_sfxOff, Header.SfxCount);

    private ReadOnlySpan<T> Cast<T>(int offset, uint count) where T : struct
        => MemoryMarshal.Cast<byte, T>(_buf.Slice(offset, (int)count * Unsafe.SizeOf<T>()));
}

/// <summary>
/// A zero-copy view over one packed terrain-fx payload (crimson_host_terrain_fx,
/// see Sim.TerrainFxHeader). Spans alias the owning session's terrain-fx buffer,
/// valid until the next <see cref="SimSession.CaptureTerrainFx"/> call.
/// </summary>
public readonly ref struct TerrainFxView
{
    public readonly Sim.TerrainFxHeader Header;
    private readonly ReadOnlySpan<byte> _buf;
    private readonly int _decalsOff;
    private readonly int _corpsesOff;

    public TerrainFxView(ReadOnlySpan<byte> buf)
    {
        Header = MemoryMarshal.Read<Sim.TerrainFxHeader>(buf);
        _buf = buf;
        int off = Unsafe.SizeOf<Sim.TerrainFxHeader>();
        _decalsOff = off;
        off += (int)Header.DecalCount * Unsafe.SizeOf<Sim.TerrainDecalSnap>();
        _corpsesOff = off;
    }

    public ReadOnlySpan<Sim.TerrainDecalSnap> Decals
        => Cast<Sim.TerrainDecalSnap>(_decalsOff, Header.DecalCount);
    public ReadOnlySpan<Sim.TerrainCorpseSnap> Corpses
        => Cast<Sim.TerrainCorpseSnap>(_corpsesOff, Header.CorpseCount);

    private ReadOnlySpan<T> Cast<T>(int offset, uint count) where T : struct
        => MemoryMarshal.Cast<byte, T>(_buf.Slice(offset, (int)count * Unsafe.SizeOf<T>()));
}

/// <summary>
/// Owns one live simulation handle and drives it at the sim tick rate. Wraps
/// the raw P/Invoke surface in Sim.cs with buffer management: ticks take a
/// single-player <see cref="Sim.HostInput"/>, and <see cref="CaptureSnapshot"/>
/// ping-pongs between two reusable buffers so the caller can hold last-tick and
/// this-tick snapshots at once (needed for render interpolation, PLAN.md §4).
/// </summary>
public sealed class SimSession : IDisposable
{
    private string _configJson;
    private readonly byte[][] _bufs = new byte[2][];
    private int _curSlot = -1;
    private byte[] _audioBuf = new byte[4096];
    private byte[] _terrainFxBuf = new byte[8192];

    public ulong Handle { get; private set; }
    public Sim.TickResult LastResult { get; private set; }
    public bool GameOver => LastResult.AllPlayersDead != 0;

    public SimSession(string configJson)
    {
        // Fail loudly on a native lib whose snapshot/struct layout differs from
        // what this build decodes (a mixed win-x64 DLL / arm64 .so), rather than
        // silently mis-reading packed structs (the magic doesn't catch this).
        uint abi = Sim.AbiVersion();
        if (abi != Sim.ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"crimson_host ABI mismatch: lib is v{abi}, frontend expects v{Sim.ExpectedAbiVersion}. Rebuild the native lib (tools/build_libcrimson.ps1).");
        }

        _configJson = configJson;
        Handle = Sim.SessionCreate(configJson);
        uint max = Sim.SnapshotMaxSize();
        _bufs[0] = new byte[max];
        _bufs[1] = new byte[max];
    }

    /// <summary>Advance exactly one fixed sim tick with one player's input.</summary>
    public Sim.TickResult Tick(in Sim.HostInput input)
    {
        ReadOnlySpan<Sim.HostInput> one = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in input), 1);
        int rc = Sim.SessionTick(Handle, one, 1, out Sim.TickResult result);
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"session tick failed ({rc}): {Sim.LastError()}");
        }
        LastResult = result;
        return result;
    }

    /// <summary>
    /// Read the current entity state into the next buffer slot and return a
    /// view over it. The previous call's view remains valid until the call
    /// after this one (two-deep buffer), which is what interpolation needs.
    /// </summary>
    public SnapshotView CaptureSnapshot()
    {
        int slot = _curSlot < 0 ? 0 : _curSlot ^ 1;
        byte[] buf = _bufs[slot];
        uint len = (uint)buf.Length;
        int rc = Sim.Snapshot(Handle, buf, ref len);
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"snapshot failed ({rc}): {Sim.LastError()}");
        }
        _curSlot = slot;
        return new SnapshotView(buf.AsSpan(0, (int)len));
    }

    /// <summary>Drain the audio events accumulated during the last tick. The
    /// returned view is valid until the next CaptureAudio call. Grows the
    /// backing buffer on demand (the count varies with on-screen activity).</summary>
    public AudioEventsView CaptureAudio()
    {
        uint len = (uint)_audioBuf.Length;
        int rc = Sim.AudioEvents(Handle, _audioBuf, ref len);
        if (rc != Sim.Ok)
        {
            // Buffer too small: len now holds the required size. Grow and retry.
            _audioBuf = new byte[len];
            len = (uint)_audioBuf.Length;
            rc = Sim.AudioEvents(Handle, _audioBuf, ref len);
            if (rc != Sim.Ok)
            {
                throw new InvalidOperationException($"audio events failed ({rc}): {Sim.LastError()}");
            }
        }
        return new AudioEventsView(_audioBuf.AsSpan(0, (int)len));
    }

    /// <summary>Drain the terrain FX (blood/scorch splats + corpse stamps)
    /// emitted during the last tick. The returned view is valid until the next
    /// CaptureTerrainFx call. Grows the backing buffer on demand.</summary>
    public TerrainFxView CaptureTerrainFx()
    {
        uint len = (uint)_terrainFxBuf.Length;
        int rc = Sim.TerrainFx(Handle, _terrainFxBuf, ref len);
        if (rc != Sim.Ok)
        {
            // Buffer too small: len now holds the required size. Grow and retry.
            _terrainFxBuf = new byte[len];
            len = (uint)_terrainFxBuf.Length;
            rc = Sim.TerrainFx(Handle, _terrainFxBuf, ref len);
            if (rc != Sim.Ok)
            {
                throw new InvalidOperationException($"terrain fx failed ({rc}): {Sim.LastError()}");
            }
        }
        return new TerrainFxView(_terrainFxBuf.AsSpan(0, (int)len));
    }

    /// <summary>Query the static terrain generation info (slots + seed). Stable
    /// for the life of the session; call once after construction.</summary>
    public Sim.TerrainInfo TerrainInfo()
    {
        int rc = Sim.TerrainInfoNative(Handle, out Sim.TerrainInfo info);
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"terrain info failed ({rc}): {Sim.LastError()}");
        }
        return info;
    }

    /// <summary>Tear down and recreate the session with the same config.</summary>
    public void Restart() => Restart(_configJson);

    /// <summary>Tear down and recreate the session with a NEW config (mode
    /// select: game_mode / quest_level_key / unlock index change per run).</summary>
    public void Restart(string configJson)
    {
        if (Handle != 0)
        {
            Sim.SessionDestroy(Handle);
        }
        _configJson = configJson;
        Handle = Sim.SessionCreate(configJson);
        LastResult = default;
        _curSlot = -1;
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Sim.SessionDestroy(Handle);
            Handle = 0;
        }
    }
}
