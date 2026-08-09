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
    private static string AudioDir => AssetStore.AudioDir;
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
    private string? _musicTrack;    // track currently in the player
    private string? _musicDesired;  // track that SHOULD be playing (survives volume 0)
    private string? _musicPending;  // next track, starts once the fade-out lands
    private bool _musicStopping;    // fade to silence with nothing after
    private float _musicFade;       // 0..1 crossfade factor over the user volume
    // Base exclusive-channel behaviour (music.py:229-256,293-344): the old
    // track fades OUT at 0.5/s, the new one fades IN at 1.0/s only after
    // silence; music volume 0 hard-stops playback instead of playing at -80dB.
    private const float MusicFadeOutPerSec = 0.5f;
    private const float MusicFadeInPerSec = 1.0f;
    private int _musicLevel = 10;
    private float _sfxVolumeDb;
    private float _musicVolumeDb;
    private AudioStream? _levelUpStream; // one-shot UI cue on level up
    // UI poke cues (button click / keyboard type / enter), loaded by name.
    private AudioStream? _buttonClickStream;
    private readonly List<AudioStream> _typeClickStreams = new();
    private AudioStream? _typeEnterStream;
    private AudioStream? _panelClickStream;
    private int _typeClickNext;

    // Trooper death VO (player_damage.py _PLAYER_DEATH_SFX: trooper_die_01/02).
    // The sim emits these only for DAMAGE deaths; Main plays one for perk-kill
    // deaths (Grim Deal / lost Fatal Lottery, natively silent) at the death
    // cinematic, using the timestamp to avoid doubling a damage death's VO.
    private readonly List<int> _trooperDieIds = new();
    private ulong _lastTrooperDieMs;

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

        // UI level-up cue, loaded by name (robust to sfx-id ordering).
        string levelUpPath = AudioDir + "ui_levelUp.ogg";
        _levelUpStream = AssetStore.LoadAudio(levelUpPath);

        // UI poke cues, loaded by name (mirrors the desktop menu/keyboard sfx).
        _buttonClickStream = LoadUi("ui_buttonClick.ogg");
        _typeEnterStream = LoadUi("ui_typeEnter.ogg");
        _panelClickStream = LoadUi("ui_panelClick.ogg"); // panel-open cue (UI_PANELCLICK)
        foreach (string name in new[] { "ui_typeClick_01.ogg", "ui_typeClick_02.ogg" })
        {
            if (LoadUi(name) is AudioStream s)
            {
                _typeClickStreams.Add(s);
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
            // Only the 01/02 pair — the player-death VO set. die_03 belongs to
            // the trooper CREATURE's pool (creatures/damage.py) and must not
            // trip the recent-VO suppression when a trooper enemy dies nearby.
            if (name is "trooper_die_01.ogg" or "trooper_die_02.ogg")
            {
                _trooperDieIds.Add(i);
            }
            if (!byName.TryGetValue(name, out AudioStream? stream))
            {
                string path = AudioDir + name;
                stream = AssetStore.LoadAudio(path);
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
                // First creature hit of the run: start the randomly-rolled game
                // tune (no hit sfx for this event, matching the router).
                PlayGameTune(hit.GameTuneRoll);
                continue;
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
            if (_trooperDieIds.Contains(sfxId))
            {
                _lastTrooperDieMs = Time.GetTicksMsec();
            }
            Play(sfxId, centre);
        }
    }

    /// <summary>True when a player-death VO played within the window — the sim
    /// emits one for damage deaths, so the frontend's perk-death VO must not
    /// stack a second cry on top of it.</summary>
    public bool TrooperDieRecent(ulong withinMs)
        => _lastTrooperDieMs != 0 && Time.GetTicksMsec() - _lastTrooperDieMs <= withinMs;

    /// <summary>Play the trooper death VO at the player (random of the 01/02
    /// pair, like player_damage.py's rand &amp; 1). VR adaptation for perk-kill
    /// deaths, which are natively silent.</summary>
    public void PlayTrooperDie(Vector2 playerGame)
    {
        if (!_ready || _trooperDieIds.Count == 0)
        {
            return;
        }
        _lastTrooperDieMs = Time.GetTicksMsec();
        int pick = _trooperDieIds[(int)(GD.Randi() % (uint)_trooperDieIds.Count)];
        Play(pick, Mapper.GameToArenaLocal(playerGame, _arenaSideMeters, _worldSize));
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
        _musicLevel = level;
        _musicVolumeDb = VolumeToDb(level);
        if (_music == null)
        {
            return;
        }
        if (level <= 0)
        {
            // Base semantics: volume 0 STOPS playback (music.py:303-363).
            _music.Stop();
            _musicTrack = null;
        }
        else if (!_music.Playing && _musicDesired != null)
        {
            // Volume raised from 0: resume the desired track, fading in.
            StartTrack(_musicDesired, fadeFrom: 0.0f);
        }
        ApplyMusicVolume();
    }

    private void ApplyMusicVolume()
        => _music.VolumeDb = _musicVolumeDb + Mathf.LinearToDb(Mathf.Max(_musicFade, 0.0001f));

    /// <summary>Play a named music track (looping), e.g. "crimson_theme" (menu) or
    /// "gt1_ingame" (gameplay), crossfading from whatever plays now (fade out
    /// 0.5/s, fade in 1.0/s after silence — music.py). No-ops if the track/asset
    /// is absent. Re-selecting the current track keeps it playing.</summary>
    public void PlayMusic(string track)
    {
        if (_music == null || !_musicTracks.ContainsKey(track))
        {
            return;
        }
        _musicDesired = track;
        _musicStopping = false;
        if (_musicLevel <= 0)
        {
            return; // volume 0: remember the track, play nothing
        }
        if (_musicTrack == track && _music.Playing)
        {
            _musicPending = null;
            return;
        }
        if (_music.Playing)
        {
            _musicPending = track; // fade the current one out first
        }
        else
        {
            StartTrack(track, fadeFrom: 0.0f);
        }
    }

    /// <summary>Fade the music out to silence (base fades at 0.5/s rather than
    /// cutting; entering a run stops the menu theme this way).</summary>
    public void StopMusic()
    {
        _musicDesired = null;
        _musicPending = null;
        _musicStopping = true;
    }

    /// <summary>First projectile hit on a creature: the sim raises
    /// trigger_game_tune with a resolved roll; the base picks randomly from the
    /// game-tune queue (music/game_tunes.txt: gt1_ingame + gt2_harppen) and
    /// crossfades it in (audio_router.py:100-119, music.py:269-290).</summary>
    private static readonly string[] GameTuneQueue = { "gt1_ingame", "gt2_harppen" };

    private void PlayGameTune(int roll)
    {
        var available = new List<string>();
        foreach (string t in GameTuneQueue)
        {
            if (_musicTracks.ContainsKey(t))
            {
                available.Add(t);
            }
        }
        if (available.Count == 0)
        {
            return;
        }
        int idx = roll >= 0 ? roll % available.Count : 0;
        PlayMusic(available[idx]);
    }

    private void StartTrack(string track, float fadeFrom)
    {
        if (!_musicTracks.TryGetValue(track, out string? path)
            || AssetStore.LoadAudio(AudioDir + path) is not AudioStream stream)
        {
            return;
        }
        _musicTrack = track;
        _musicPending = null;
        _musicFade = fadeFrom;
        _music.Stream = stream;
        ApplyMusicVolume();
        _music.Play();
    }

    /// <summary>Drive the music crossfade (out 0.5/s; in 1.0/s once silent).</summary>
    public override void _Process(double delta)
    {
        if (_music == null || _musicLevel <= 0)
        {
            return;
        }
        bool fadingOut = _musicStopping || _musicPending != null;
        if (fadingOut && _music.Playing)
        {
            _musicFade -= (float)delta * MusicFadeOutPerSec;
            if (_musicFade <= 0.0f)
            {
                _musicFade = 0.0f;
                _music.Stop();
                _musicTrack = null;
                if (_musicPending is { } next)
                {
                    StartTrack(next, fadeFrom: 0.0f);
                }
                else
                {
                    _musicStopping = false;
                }
            }
            ApplyMusicVolume();
        }
        else if (_music.Playing && _musicFade < 1.0f)
        {
            _musicFade = Mathf.Min(1.0f, _musicFade + (float)delta * MusicFadeInPerSec);
            ApplyMusicVolume();
        }
    }

    /// <summary>Play the level-up UI cue (non-positional-ish, at the arena centre).</summary>
    public void PlayLevelUp()
    {
        if (_levelUpStream == null || _pool.Length == 0)
        {
            return;
        }
        AudioStreamPlayer3D p = _pool[_next];
        _next = (_next + 1) % _pool.Length;
        p.Stream = _levelUpStream;
        p.Position = Vector3.Zero;
        // +6 dB (double amplitude): the cue is easy to miss at pool volume.
        // Pool players are shared round-robin, so every play path stamps its
        // own VolumeDb rather than inheriting the previous cue's.
        p.VolumeDb = _sfxVolumeDb + 6.0f;
        p.Play();
    }

    private static AudioStream? LoadUi(string name)
    {
        string path = AudioDir + name;
        return AssetStore.LoadAudio(path);
    }

    /// <summary>UI poke cue kinds (matches VrButton.ClickSound).</summary>
    public const int UiButton = 0;
    public const int UiType = 1;
    public const int UiEnter = 2;
    public const int UiPanel = 3; // panel-open cue (base UI_PANELCLICK)

    /// <summary>Play a UI poke cue (button click / keyboard type / enter) at the
    /// arena centre. Driven by VrButton.OnAnyPress so every poke button clicks.</summary>
    public void PlayUi(int kind)
    {
        AudioStream? stream = kind switch
        {
            UiType => _typeClickStreams.Count > 0
                ? _typeClickStreams[_typeClickNext++ % _typeClickStreams.Count]
                : null,
            UiEnter => _typeEnterStream,
            UiPanel => _panelClickStream,
            _ => _buttonClickStream,
        };
        if (stream == null || _pool.Length == 0)
        {
            return;
        }
        AudioStreamPlayer3D p = _pool[_next];
        _next = (_next + 1) % _pool.Length;
        p.Stream = stream;
        p.Position = Vector3.Zero;
        p.VolumeDb = _sfxVolumeDb;
        p.Play();
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
        player.VolumeDb = _sfxVolumeDb;
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
