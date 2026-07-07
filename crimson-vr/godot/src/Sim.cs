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
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProjectileSnap
    {
        public float X;
        public float Y;
        public float Angle;
        public int TypeId;
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

    [LibraryImport(LibName, EntryPoint = "crimson_host_session_tick")]
    public static partial int SessionTick(ulong handle, ReadOnlySpan<HostInput> inputs, uint inputCount, out TickResult result);

    [LibraryImport(LibName, EntryPoint = "crimson_host_snapshot_max_size")]
    public static partial uint SnapshotMaxSize();

    [LibraryImport(LibName, EntryPoint = "crimson_host_snapshot")]
    public static partial int Snapshot(ulong handle, Span<byte> buf, ref uint len);

    [LibraryImport(LibName, EntryPoint = "crimson_host_audio_events")]
    public static partial int AudioEvents(ulong handle, Span<byte> buf, ref uint len);

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
