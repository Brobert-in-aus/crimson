using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimsonVR;

public sealed class NetworkLobbySlot
{
    [JsonPropertyName("slot_index")]
    public int SlotIndex { get; set; }
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }
    [JsonPropertyName("is_host")]
    public bool IsHost { get; set; }
    [JsonPropertyName("peer_name")]
    public string PeerName { get; set; } = string.Empty;
}

public sealed class NetworkLobbyStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "connecting";
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
    [JsonPropertyName("netcode")]
    public string Netcode { get; set; } = string.Empty;
    [JsonPropertyName("room_code")]
    public string RoomCode { get; set; } = string.Empty;
    [JsonPropertyName("local_slot")]
    public int LocalSlot { get; set; } = -1;
    [JsonPropertyName("bound_port")]
    public ushort BoundPort { get; set; }
    [JsonPropertyName("expected")]
    public int Expected { get; set; }
    [JsonPropertyName("connected")]
    public int Connected { get; set; }
    [JsonPropertyName("ready")]
    public int Ready { get; set; }
    [JsonPropertyName("started")]
    public bool Started { get; set; }
    [JsonPropertyName("mode_id")]
    public int ModeId { get; set; }
    [JsonPropertyName("failure")]
    public string Failure { get; set; } = string.Empty;
    [JsonPropertyName("slots")]
    public NetworkLobbySlot[] Slots { get; set; } = Array.Empty<NetworkLobbySlot>();
}

/// <summary>Managed presentation adapter for the native network-session ABI.
/// Reliability, lobby state and canonical stepping remain entirely native.</summary>
public sealed class NetworkSimSession : IGameSession
{
    private readonly byte[][] _bufs = new byte[2][];
    private int _curSlot = -1;
    private byte[] _audioBuf = new byte[4096];
    private byte[] _terrainFxBuf = new byte[8192];
    private byte[] _statusBuf = new byte[2048];
    private bool _perkMenuCommandActive;

    public ulong Handle { get; private set; }
    public Sim.TickResult LastResult { get; private set; }
    public bool GameOver => LastResult.AllPlayersDead != 0;
    public bool IsNetwork => true;
    public int LocalPlayerSlot { get; private set; } = -1;
    public Sim.NetworkUpdate LastUpdate { get; private set; }
    public string ConnectionFailure { get; private set; } = string.Empty;

    public NetworkSimSession(string configJson)
    {
        uint abi = Sim.AbiVersion();
        if (abi != Sim.ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"crimson_host ABI mismatch: lib is v{abi}, frontend expects v{Sim.ExpectedAbiVersion}.");
        }
        Handle = Sim.NetworkCreate(configJson);
        uint max = Sim.SnapshotMaxSize();
        _bufs[0] = new byte[max];
        _bufs[1] = new byte[max];
    }

    public Sim.TickResult Tick(in Sim.HostInput input)
    {
        SubmitPerkCommands(input);
        int rc = Sim.NetworkUpdateNative(Handle, Environment.TickCount64, input, out Sim.NetworkUpdate update);
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"network update failed ({rc}): {Sim.LastError()}");
        }
        LastUpdate = update;
        LocalPlayerSlot = update.LocalSlot;
        if (update.Phase == Sim.NetPhaseFailed)
        {
            ConnectionFailure = Status().Failure;
            if (string.IsNullOrWhiteSpace(ConnectionFailure)) ConnectionFailure = "connection_lost";
        }
        if (update.Phase == Sim.NetPhaseRunning)
        {
            RefreshResult(update.TicksAdvanced);
        }
        return LastResult;
    }

    public NetworkLobbyStatus Status()
    {
        uint len = (uint)_statusBuf.Length;
        int rc = Sim.NetworkStatus(Handle, _statusBuf, ref len);
        if (rc != Sim.Ok)
        {
            _statusBuf = new byte[len];
            len = (uint)_statusBuf.Length;
            rc = Sim.NetworkStatus(Handle, _statusBuf, ref len);
        }
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"network status failed ({rc}): {Sim.LastError()}");
        }
        return JsonSerializer.Deserialize<NetworkLobbyStatus>(_statusBuf.AsSpan(0, (int)len))
            ?? throw new InvalidOperationException("network status returned invalid JSON");
    }

    public void ResumeAfterSuspend()
    {
        int rc = Sim.NetworkResume(Handle, System.Environment.TickCount64);
        if (rc != Sim.Ok) throw new InvalidOperationException($"network resume failed ({rc}): {Sim.LastError()}");
    }

    public void SetReady(bool ready)
    {
        int rc = Sim.NetworkCommand(Handle, Sim.NetCommandSetReady, Math.Max(0, LocalPlayerSlot), ready ? 1 : 0);
        if (rc != Sim.Ok) throw new InvalidOperationException($"network ready failed ({rc}): {Sim.LastError()}");
    }

    public SnapshotView CaptureSnapshot()
    {
        int slot = _curSlot < 0 ? 0 : _curSlot ^ 1;
        byte[] buf = _bufs[slot];
        uint len = (uint)buf.Length;
        int rc = Sim.NetworkSnapshot(Handle, buf, ref len);
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"network snapshot failed ({rc}): {Sim.LastError()}");
        }
        _curSlot = slot;
        return new SnapshotView(buf.AsSpan(0, (int)len));
    }

    public AudioEventsView CaptureAudio()
    {
        uint len = (uint)_audioBuf.Length;
        int rc = Sim.NetworkAudioEvents(Handle, _audioBuf, ref len);
        if (rc != Sim.Ok)
        {
            _audioBuf = new byte[len];
            len = (uint)_audioBuf.Length;
            rc = Sim.NetworkAudioEvents(Handle, _audioBuf, ref len);
        }
        if (rc != Sim.Ok) throw new InvalidOperationException($"network audio failed ({rc}): {Sim.LastError()}");
        return new AudioEventsView(_audioBuf.AsSpan(0, (int)len));
    }

    public TerrainFxView CaptureTerrainFx()
    {
        uint len = (uint)_terrainFxBuf.Length;
        int rc = Sim.NetworkTerrainFx(Handle, _terrainFxBuf, ref len);
        if (rc != Sim.Ok)
        {
            _terrainFxBuf = new byte[len];
            len = (uint)_terrainFxBuf.Length;
            rc = Sim.NetworkTerrainFx(Handle, _terrainFxBuf, ref len);
        }
        if (rc != Sim.Ok) throw new InvalidOperationException($"network terrain FX failed ({rc}): {Sim.LastError()}");
        return new TerrainFxView(_terrainFxBuf.AsSpan(0, (int)len));
    }

    public Sim.TerrainInfo TerrainInfo()
    {
        int rc = Sim.NetworkTerrainInfo(Handle, out Sim.TerrainInfo info);
        if (rc != Sim.Ok) throw new InvalidOperationException($"network terrain info failed ({rc}): {Sim.LastError()}");
        return info;
    }

    private void RefreshResult(uint ticksAdvanced)
    {
        SnapshotView snapshot = CaptureSnapshot();
        int local = Math.Clamp(LocalPlayerSlot, 0, Math.Max(0, snapshot.Players.Length - 1));
        Sim.PlayerSnap player = snapshot.Players.Length == 0 ? default : snapshot.Players[local];
        bool allDead = snapshot.Players.Length > 0;
        foreach (Sim.PlayerSnap item in snapshot.Players)
        {
            if (item.Health > 0) allDead = false;
        }
        long elapsed = Math.Max(0, (long)snapshot.Header.ElapsedMsSim);
        LastResult = new Sim.TickResult
        {
            TicksAdvanced = ticksAdvanced,
            AllPlayersDead = allDead ? 1u : 0u,
            PerkPendingCount = snapshot.Header.PerkPendingCount,
            PlayerHealth = player.Health,
            PlayerLevel = player.Level,
            PlayerExperience = player.Experience,
            PlayerWeaponId = player.WeaponId,
            CreatureActiveCount = snapshot.Header.CreatureCount,
            BonusActiveCount = snapshot.Header.BonusCount,
            ShotsFired = LastUpdate.LocalShotsFired,
            ShotsHit = LastUpdate.LocalShotsHit,
            ElapsedMsSimLo = (uint)elapsed,
            ElapsedMsSimHi = (uint)((ulong)elapsed >> 32),
            CreatureKillCount = LastUpdate.CreatureKillCount,
            MostUsedWeaponId = LastUpdate.LocalMostUsedWeaponId,
            TutorialStageIndex = -1,
            TutorialHintIndex = -1,
        };
    }

    private void SubmitPerkCommands(in Sim.HostInput input)
    {
        if (LocalPlayerSlot < 0) return;
        bool active = input.PerkMenuActive != 0;
        if (active && !_perkMenuCommandActive)
        {
            CheckCommand(Sim.NetCommandPerkMenuOpen, 0);
        }
        if (input.PerkChoiceIndex >= 0)
        {
            CheckCommand(Sim.NetCommandPerkPick, input.PerkChoiceIndex);
        }
        _perkMenuCommandActive = active;
    }

    private void CheckCommand(int commandType, int value)
    {
        int rc = Sim.NetworkCommand(Handle, commandType, LocalPlayerSlot, value);
        if (rc != Sim.Ok)
        {
            throw new InvalidOperationException($"network command failed ({rc}): {Sim.LastError()}");
        }
    }

    public uint[]? WeaponUsageCounts() => null;
    public void ReplayBegin()
    {
        int rc = Sim.ReplayBegin(Handle);
        if (rc != Sim.Ok) throw new InvalidOperationException($"network replay begin failed ({rc}): {Sim.LastError()}");
    }

    public ulong ReplayDetach()
    {
        int rc = Sim.ReplayDetach(Handle, out ulong recording);
        if (rc != Sim.Ok) throw new InvalidOperationException($"network replay detach failed ({rc}): {Sim.LastError()}");
        return recording;
    }
    public void Restart(string configJson) => throw new NotSupportedException("A network match must return to its lobby.");

    public void Dispose()
    {
        if (Handle == 0) return;
        Sim.NetworkDestroy(Handle);
        Handle = 0;
    }
}
