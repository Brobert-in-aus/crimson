using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CrimsonVR;

/// <summary>
/// M3 slice 3: positional SFX. Consumes the host-ABI audio events drained each
/// tick (SimSession.CaptureAudio) and plays the matching Crimsonland samples
/// through a pool of spatialized <see cref="AudioStreamPlayer3D"/> anchored to
/// the arena, so shots/hits/deaths sound like they come from the tabletop
/// diorama (PLAN §6).
///
/// The routing mirrors the desktop's <c>audio_router.py</c>: shot events map a
/// weapon id -&gt; fire sound (or Fire-Bullets + Plasma-Minigun when active);
/// reload events map a weapon id -&gt; reload sound; hit events play the shock or
/// bullet-hit sample (the ABI supplies the resolved roll); loose sfx events are
/// native sfx ids straight into the sample table. All mappings come from
/// assets/audio/audio_manifest.json (baked, gitignored); if it or the Oggs are
/// absent the bank is silent (no assets = no sound, project still runs).
///
/// The original game's SFX are not positional, and the ABI carries no per-event
/// position, so only player-emitted sounds (shots/reload) are placed at the
/// player; everything else plays at the arena centre. Across a ~0.4 m table the
/// spatialization is subtle — its real value is that sound comes from the table,
/// not from inside the player's head.
/// </summary>
public sealed partial class AudioBank : Node3D
{
    private const string AudioDir = "res://assets/audio/";
    private const int PoolSize = 24;

    private float _arenaSideMeters;
    private float _worldSize;

    private AudioStream?[] _streams = System.Array.Empty<AudioStream?>();
    private readonly Dictionary<int, int> _weaponFire = new();
    private readonly Dictionary<int, int> _weaponReload = new();
    private int[] _bulletHit = System.Array.Empty<int>();
    private int _shockHit = -1;
    private int _fireBulletsWeaponId = -1;
    private int _plasmaMinigunWeaponId = -1;

    private AudioStreamPlayer3D[] _pool = System.Array.Empty<AudioStreamPlayer3D>();
    private int _next;
    private bool _ready;

    // Non-positional music (menu theme / in-game), + segmented 0-10 volumes that
    // mirror the original Options screen. SFX volume rides on the pool players'
    // VolumeDb; music on the music player's.
    private AudioStreamPlayer _music = null!;
    private readonly Dictionary<string, string> _musicTracks = new();
    private string? _musicTrack;
    private float _sfxVolumeDb;
    private float _musicVolumeDb;

    public void Configure(float arenaSideMeters, float worldSize)
    {
        _arenaSideMeters = arenaSideMeters;
        _worldSize = worldSize;

        AudioManifest? manifest = LoadManifest();

        // Music player (non-positional), set up regardless of SFX so the menu
        // theme plays even if the sfx manifest is empty. Loops by replaying on
        // finish. Tracks are resolved by name in PlayMusic.
        _music = new AudioStreamPlayer();
        _music.Finished += () => { if (_music.Stream != null) { _music.Play(); } };
        AddChild(_music);
        if (manifest?.music is { } tracks)
        {
            foreach (KeyValuePair<string, string> kv in tracks)
            {
                _musicTracks[kv.Key] = kv.Value;
            }
        }

        if (manifest?.sfx is not { Length: > 0 } files)
        {
            return; // no sfx assets -> silent SFX (music may still play)
        }

        // Load each distinct sample once, indexed by native sfx id.
        var byName = new Dictionary<string, AudioStream?>();
        _streams = new AudioStream?[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            string name = files[i];
            if (!byName.TryGetValue(name, out AudioStream? stream))
            {
                string path = AudioDir + name;
                stream = ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
                if (stream == null)
                {
                    GD.PushWarning($"CrimsonVR: audio sample missing ({path})");
                }
                byName[name] = stream;
            }
            _streams[i] = stream;
        }

        CopyIntMap(manifest.weapon_fire, _weaponFire);
        CopyIntMap(manifest.weapon_reload, _weaponReload);
        _bulletHit = manifest.bullet_hit ?? System.Array.Empty<int>();
        _shockHit = manifest.shock_hit;
        _fireBulletsWeaponId = manifest.fire_bullets_weapon_id;
        _plasmaMinigunWeaponId = manifest.plasma_minigun_weapon_id;

        _pool = new AudioStreamPlayer3D[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            var p = new AudioStreamPlayer3D
            {
                // Constant volume + panning across the tiny table (distance
                // attenuation would make near sounds boom and far ones vanish).
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                MaxPolyphony = 1,
                VolumeDb = _sfxVolumeDb,
            };
            AddChild(p);
            _pool[i] = p;
        }
        _ready = true;
    }

    /// <summary>Play every event in one drained audio frame. <paramref name="playerGame"/>
    /// is the player's game-space position (shots/reload emit from there).</summary>
    public void Route(in AudioEventsView view, Vector2 playerGame)
    {
        if (!_ready)
        {
            return;
        }
        Vector3 playerPos = Mapper.GameToArenaLocal(playerGame, _arenaSideMeters, _worldSize);
        Vector3 centre = Mapper.GameToArenaLocal(
            new Vector2(_worldSize * 0.5f, _worldSize * 0.5f), _arenaSideMeters, _worldSize);

        foreach (Sim.ShotAudioSnap shot in view.Shots)
        {
            if (shot.FireBulletsActive != 0)
            {
                // Fire-Bullets replaces the per-weapon shot sfx with these two.
                PlayWeapon(_weaponFire, _fireBulletsWeaponId, playerPos);
                PlayWeapon(_weaponFire, _plasmaMinigunWeaponId, playerPos);
            }
            else
            {
                PlayWeapon(_weaponFire, shot.WeaponId, playerPos);
            }
        }

        foreach (int weaponId in view.Reloads)
        {
            PlayWeapon(_weaponReload, weaponId, playerPos);
        }

        foreach (Sim.HitAudioSnap hit in view.Hits)
        {
            if (hit.TriggerGameTune != 0)
            {
                continue; // game-tune trigger plays no hit sfx (music: later slice)
            }
            if (hit.ShockHit != 0)
            {
                Play(_shockHit, centre);
            }
            else if (hit.BulletHitRoll >= 0 && hit.BulletHitRoll < _bulletHit.Length)
            {
                Play(_bulletHit[hit.BulletHitRoll], centre);
            }
        }

        foreach (int sfxId in view.Sfx)
        {
            Play(sfxId, centre);
        }
    }

    private void PlayWeapon(Dictionary<int, int> map, int weaponId, Vector3 pos)
    {
        if (map.TryGetValue(weaponId, out int sfxId))
        {
            Play(sfxId, pos);
        }
    }

    // ---- Volume + music (Options screen) ----

    /// <summary>Segmented 0-10 volume (mirrors the original Options scale). 0 = mute.</summary>
    private static float VolumeToDb(int level) => level <= 0 ? -80.0f : Mathf.LinearToDb(Mathf.Clamp(level, 0, 10) / 10.0f);

    public void SetSfxVolume(int level)
    {
        _sfxVolumeDb = VolumeToDb(level);
        foreach (AudioStreamPlayer3D p in _pool)
        {
            p.VolumeDb = _sfxVolumeDb;
        }
    }

    public void SetMusicVolume(int level)
    {
        _musicVolumeDb = VolumeToDb(level);
        if (_music != null)
        {
            _music.VolumeDb = _musicVolumeDb;
        }
    }

    /// <summary>Play a named music track (looping), e.g. "crimson_theme" (menu) or
    /// "gt1_ingame" (gameplay). No-ops if the track/asset is absent. Re-selecting
    /// the current track keeps it playing.</summary>
    public void PlayMusic(string track)
    {
        if (_music == null || _musicTrack == track)
        {
            return;
        }
        if (!_musicTracks.TryGetValue(track, out string? path) || ResourceLoader.Load<AudioStream>(AudioDir + path) is not AudioStream stream)
        {
            return;
        }
        _musicTrack = track;
        _music.Stream = stream;
        _music.VolumeDb = _musicVolumeDb;
        _music.Play();
    }

    public void StopMusic()
    {
        _musicTrack = null;
        _music?.Stop();
    }

    private void Play(int sfxId, Vector3 localPos)
    {
        if (sfxId < 0 || sfxId >= _streams.Length || _streams[sfxId] is not AudioStream stream)
        {
            return;
        }
        // Round-robin the pool; steal the oldest if all are busy (short SFX).
        AudioStreamPlayer3D player = _pool[_next];
        _next = (_next + 1) % _pool.Length;
        player.Stream = stream;
        player.Position = localPos;
        player.Play();
    }

    private static void CopyIntMap(Dictionary<string, int>? src, Dictionary<int, int> dst)
    {
        if (src == null)
        {
            return;
        }
        foreach (KeyValuePair<string, int> kv in src)
        {
            if (int.TryParse(kv.Key, out int key))
            {
                dst[key] = kv.Value;
            }
        }
    }

    // ---- Manifest ----

    private sealed class AudioManifest
    {
        public string[]? sfx { get; set; }
        public Dictionary<string, int>? weapon_fire { get; set; }
        public Dictionary<string, int>? weapon_reload { get; set; }
        public int[]? bullet_hit { get; set; }
        public int shock_hit { get; set; } = -1;

        [JsonPropertyName("fire_bullets_weapon_id")]
        public int fire_bullets_weapon_id { get; set; } = -1;

        [JsonPropertyName("plasma_minigun_weapon_id")]
        public int plasma_minigun_weapon_id { get; set; } = -1;

        // Music track name -> ogg path (relative to AudioDir).
        public Dictionary<string, string>? music { get; set; }
    }

    private static AudioManifest? LoadManifest()
    {
        string path = AudioDir + "audio_manifest.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.PushWarning($"CrimsonVR: {path} missing; no SFX. Run tools/bake_assets.py.");
            return null;
        }
        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        try
        {
            return JsonSerializer.Deserialize<AudioManifest>(file.GetAsText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException e)
        {
            GD.PushError($"CrimsonVR: bad audio manifest: {e.Message}");
            return null;
        }
    }
}
