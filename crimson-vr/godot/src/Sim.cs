using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;

namespace CrimsonVR;

/// <summary>
/// P/Invoke bindings for libcrimson (crimson_host). The contract is
/// crimson-vr/abi/crimson_host.h; struct layouts are packed 4-byte fields.
/// Native binaries are resolved from res://native/<rid>/ (populated by
/// crimson-vr/tools/build_libcrimson.ps1, gitignored).
/// </summary>
public static partial class Sim
{
    private const string LibName = "crimson_host";

    public const uint SnapshotMagic = 0x31525643;
    public const int Ok = 0;

    // Layout version this frontend was built against (crimson_host.h
    // CRIMSON_HOST_ABI_VERSION). The snapshot magic is unchanged across layout
    // revisions, so a stale native lib would be silently mis-decoded; the session
    // driver checks this against crimson_host_abi_version() at startup.
    public const uint ExpectedAbiVersion = 18;

    // Save-status weapon usage table size (Zig state.weapon_count_size):
    // index = weapon id, slot 0 unused. The session-create JSON array must be
    // exactly this long (std.json fixed-array parse).
    public const int WeaponUsageSlots = 54;

    [StructLayout(LayoutKind.Sequential)]
    public struct HostInput
    {
        public float MoveX;
        public float MoveY;
        public float AimX;
        public float AimY;
        public uint Flags;
        public int MoveMode;
        public int AimScheme;
        public int PerkChoiceIndex;
        public uint PerkMenuActive;

        public const uint FlagFireDown = 1u << 0;
        public const uint FlagFirePressed = 1u << 1;
        public const uint FlagReloadPressed = 1u << 2;
        public const uint FlagReloadDown = 1u << 3;
        public const uint FlagMoveToCursor = 1u << 4;

        public const int MoveModeMousePointClick = 4;
        public const int AimSchemeMouse = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TickResult
    {
        public uint TicksAdvanced;
        public uint PausedForPerkPick;
        public uint AllPlayersDead;
        public int PerkPendingCount;
        public float PlayerHealth;
        public int PlayerLevel;
        public int PlayerExperience;
        public int PlayerWeaponId;
        public uint CreatureActiveCount;
        public uint BonusActiveCount;
        public int ShotsFired;
        public int ShotsHit;
        public uint ElapsedMsSimLo;
        public uint ElapsedMsSimHi;
        public int CreatureKillCount;   // ABI v10+: total kills this run (frags)
        public int MostUsedWeaponId;    // ABI v10+: argmax per-weapon shots, current weapon fallback
        public uint QuestCompleted;     // ABI v14+: quest timeline cleared (0 outside quests)

        public readonly long ElapsedMsSim => (long)(((ulong)ElapsedMsSimHi << 32) | ElapsedMsSimLo);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SnapshotHeader
    {
        public uint Magic;
        public uint Version;
        public uint TickLo;
        public uint TickHi;
        public int GameMode;
        public float WorldSize;
        public float ElapsedMsSim;
        public int PerkPendingCount;
        public uint PerkChoiceCount;
        public int PerkChoice0;
        public int PerkChoice1;
        public int PerkChoice2;
        public int PerkChoice3;
        public int PerkChoice4;
        public int PerkChoice5;
        public int PerkChoice6;
        public uint PlayerCount;
        public uint CreatureCount;
        public uint ProjectileCount;
        public uint SecondaryCount;
        public uint BonusCount;
        public uint ParticleCount;
        public float EnergizerTimer; // ABI v3+: global energizer bonus timer
        public float FreezeTimer;    // ABI v5+: global freeze bonus timer
        public uint MonsterVision;   // ABI v7+: 1 => draw yellow aura on all creatures
        public uint GlowCount;       // ABI v7+: flame/bubblegun particle-pool entries
        public uint SpriteEffectCount; // ABI v13+: sprite-effect pool entries (after glows)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlayerSnap
    {
        public float X;
        public float Y;
        public float Heading;
        public float AimX;
        public float AimY;
        public float AimHeading;
        public float Health;
        public float Size;
        public float MuzzleFlashAlpha;
        public int WeaponId;
        public float Ammo;
        public int ClipSize;
        public uint ReloadActive;
        public float ReloadTimer;
        public float ReloadTimerMax;
        public int Experience;
        public int Level;
        public int WeaponIconIndex; // ABI v8+: ui_wicons atlas index
        public int WeaponAmmoClass; // ABI v8+: 0 bullet / 1 fire / 2 rocket / 4 electric
        public float SpreadHeat;    // ABI v11+: aim-spread heat (reticle spread ring)
        public float ShieldTimer;   // ABI v11+: shield bonus timer (> 0 = ring pair)
        public uint PerkFlags;      // ABI v11+: bit0 Doctor / bit1 Radioactive / bit2 Sharpshooter
        public float MovePhase;     // ABI v15+: walk cycle (leg frame = clamp(int(+0.5),0,14))
        public float DeathTimer;    // ABI v17+: 16 -> <0 at 20/s once dead (corpse frames 32..52)

        public const uint PerkFlagDoctor = 1u << 0;
        public const uint PerkFlagRadioactive = 1u << 1;
        public const uint PerkFlagSharpshooter = 1u << 2;
        public const uint PerkFlagIonGunMaster = 1u << 3; // ABI v12: ion chain reach x1.2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreatureSnap
    {
        public float X;
        public float Y;
        public float Heading;
        public float Size;
        public float AnimPhase;
        public float Hp;
        public float MaxHp;
        public float LifecycleStage;
        public int TypeId;
        public uint Flags;
        public float R; // ABI v4+: per-creature tint RGBA + hit-flash timer
        public float G;
        public float B;
        public float A;
        public float HitFlashTimer;
        public int PoolIndex;       // ABI v18+: stable pool slot for render interpolation
        public uint Generation;     // ABI v18+: increments whenever the slot is reused
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProjectileSnap
    {
        public float X;
        public float Y;
        public float Angle;
        public int TypeId;
        public float Vx; // ABI v6: fixed-magnitude direction (cos,sin)*1.5, NOT speed
        public float Vy;
        public float LifeTimer; // ABI v9: < 0.4 => hit & lingering (stopped moving)
        public float OriginX;      // ABI v12: spawn origin (trails/beams/pulse sizing)
        public float OriginY;
        public float SpeedScale;   // ABI v12: plasma tail step scale
        public float TravelBudget; // ABI v12: plasma tail segment budget
        public int PoolIndex;      // ABI v12: stable pool slot (spin/orbit phases)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecondarySnap
    {
        public float X;
        public float Y;
        public float Angle;
        public float DetonationT;
        public float DetonationScale;
        public int TypeId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BonusSnap
    {
        public float X;
        public float Y;
        public float TimeLeft;
        public float TimeMax;
        public int BonusId;
        public int Amount;
    }

    // One live sprite-effect (blood, gibs, explosion, casing, glow). Mirrors the
    // Zig ParticleSnap: effect_id -> particles-atlas frame, size from
    // half_width/half_height*scale, rotation, rgba color, flags/age gate.
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticleSnap
    {
        public float X;
        public float Y;
        public float HalfWidth;
        public float HalfHeight;
        public float Scale;
        public float Rotation;
        public float R;
        public float G;
        public float B;
        public float A;
        public float Age;
        public int EffectId;
        public int Flags;
    }

    // One live flame/bubblegun particle (state.particles), a pool separate from
    // the effect pool, rendered additively (draw_particle_pool). ABI v7+.
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticleGlowSnap
    {
        public float X;
        public float Y;
        public float Intensity;
        public float Spin;
        public float TintR;
        public float TintG;
        public float TintB;
        public float Age; // alpha multiplier (0..1)
        public int StyleId;
    }

    // One live sprite effect (session.sprite_effects), the THIRD effect system:
    // muzzle puffs, rocket exhaust, explosion smoke. Every entry draws the
    // EXPLOSION_PUFF atlas cell (FULL cell, no 2px clamp), plain alpha blend,
    // size = Scale world units, Rotation radians, tinted RGBA. Skipped below
    // fx-detail 2. ABI v13+.
    [StructLayout(LayoutKind.Sequential)]
    public struct SpriteEffectSnap
    {
        public float X;
        public float Y;
        public float Scale;
        public float Rotation;
        public float R;
        public float G;
        public float B;
        public float A;
    }

    // Audio events drained per tick (crimson_host_audio_events). Header then
    // packed arrays: ShotAudioSnap[], reload weapon ids (i32[]), HitAudioSnap[],
    // sfx ids (i32[] = @intFromEnum(SfxId), i.e. native sfx index).
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioHeader
    {
        public uint Version;
        public uint Flags;
        public uint ShotCount;
        public uint ReloadCount;
        public uint HitCount;
        public uint SfxCount;
    }

    // Static terrain generation info (crimson_host_terrain_info, ABI v3). The
    // base ground is stamped once from three atlas slots seeded by TerrainSeed;
    // query once after session create.
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainInfo
    {
        public int Slot0; // base atlas slot (ter/ sheet index)
        public int Slot1; // overlay atlas slot
        public int Slot2; // detail atlas slot
        public uint TerrainSeed;
        public int TerrainSize;
        public float WorldSize;
    }

    // Terrain FX drained per tick (crimson_host_terrain_fx, ABI v3). Header then
    // packed arrays: TerrainDecalSnap[] (blood/scorch splats), then
    // TerrainCorpseSnap[] (rotated corpse stamps on creature death). One-shot
    // events -> paint into a persistent decal layer; do not treat as state.
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainFxHeader
    {
        public uint Version;
        public uint DecalCount;
        public uint CorpseCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainDecalSnap
    {
        public int EffectId; // ter/ decal atlas frame
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public float Rotation;
        public float R;
        public float G;
        public float B;
        public float A;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainCorpseSnap
    {
        public int CreatureTypeId; // bodyset/creature sheet id (7 = ping-pong fallback)
        public float X; // top-left x
        public float Y; // top-left y
        public float Rotation;
        public float Scale;
        public float R;
        public float G;
        public float B;
        public float A;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShotAudioSnap
    {
        public int WeaponId;
        public uint FireBulletsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HitAudioSnap
    {
        public uint ShockHit;
        public int BulletHitRoll;  // -1 = none (this hit triggered the game tune)
        public int GameTuneRoll;   // -1 = none
        public uint TriggerGameTune;
    }

    static Sim()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveNative);
    }

    private static IntPtr ResolveNative(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibName)
        {
            return IntPtr.Zero;
        }
        // Android bundles the .so inside the APK; default resolution works.
        // On desktop, look in res://native/<rid>/ first (dev layout).
        string rid = OS.GetName() switch
        {
            "Windows" => "win-x64",
            "Linux" => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64",
            _ => "",
        };
        if (rid.Length == 0)
        {
            return IntPtr.Zero;
        }
        string fileName = OS.GetName() == "Windows" ? "crimson_host.dll" : "libcrimson_host.so";
        string path = ProjectSettings.GlobalizePath($"res://native/{rid}/{fileName}");
        return NativeLibrary.TryLoad(path, out IntPtr handle) ? handle : IntPtr.Zero;
    }

    [LibraryImport(LibName, EntryPoint = "crimson_host_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(LibName, EntryPoint = "crimson_host_last_error")]
    private static partial int LastErrorNative(Span<byte> buf, uint len);

    [LibraryImport(LibName, EntryPoint = "crimson_host_session_create")]
    private static partial int SessionCreateNative(ReadOnlySpan<byte> configJson, uint configLen, out ulong handle);

    [LibraryImport(LibName, EntryPoint = "crimson_host_session_destroy")]
    public static partial void SessionDestroy(ulong handle);

    // ABI v16: current per-weapon usage counts (seeded config values + this
    // run's pickup assigns). Returns entries written (WeaponUsageSlots) or the
    // negative required count.
    [LibraryImport(LibName, EntryPoint = "crimson_host_status_weapon_usage")]
    public static partial int StatusWeaponUsage(ulong handle, Span<uint> outCounts, uint max);

    [LibraryImport(LibName, EntryPoint = "crimson_host_session_tick")]
    public static partial int SessionTick(ulong handle, ReadOnlySpan<HostInput> inputs, uint inputCount, out TickResult result);

    [LibraryImport(LibName, EntryPoint = "crimson_host_snapshot_max_size")]
    public static partial uint SnapshotMaxSize();

    [LibraryImport(LibName, EntryPoint = "crimson_host_snapshot")]
    public static partial int Snapshot(ulong handle, Span<byte> buf, ref uint len);

    [LibraryImport(LibName, EntryPoint = "crimson_host_audio_events")]
    public static partial int AudioEvents(ulong handle, Span<byte> buf, ref uint len);

    [LibraryImport(LibName, EntryPoint = "crimson_host_terrain_info")]
    public static partial int TerrainInfoNative(ulong handle, out TerrainInfo info);

    [LibraryImport(LibName, EntryPoint = "crimson_host_terrain_fx")]
    public static partial int TerrainFx(ulong handle, Span<byte> buf, ref uint len);

    public static string LastError()
    {
        Span<byte> buf = stackalloc byte[1024];
        int written = LastErrorNative(buf, (uint)buf.Length);
        return written > 0 ? System.Text.Encoding.UTF8.GetString(buf[..written]) : "";
    }

    public static ulong SessionCreate(string configJson)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(configJson);
        int rc = SessionCreateNative(bytes, (uint)bytes.Length, out ulong handle);
        if (rc != Ok)
        {
            throw new InvalidOperationException($"session create failed ({rc}): {LastError()}");
        }
        return handle;
    }
}
