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

    public ulong Tick => ((ulong)Header.TickHi << 32) | Header.TickLo;

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
    private readonly string _configJson;
    private readonly byte[][] _bufs = new byte[2][];
    private int _curSlot = -1;

    public ulong Handle { get; private set; }
    public Sim.TickResult LastResult { get; private set; }
    public bool GameOver => LastResult.AllPlayersDead != 0;

    public SimSession(string configJson)
    {
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

    /// <summary>Tear down and recreate the session with the same config.</summary>
    public void Restart()
    {
        if (Handle != 0)
        {
            Sim.SessionDestroy(Handle);
        }
        Handle = Sim.SessionCreate(_configJson);
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
