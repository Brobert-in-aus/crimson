using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;

namespace CrimsonVR;

/// <summary>Room-code rollback flow plus advanced direct-LAN lockstep. All
/// interaction is physical poke; neither path depends on a laser keyboard.</summary>
public sealed partial class MultiplayerLanMenu : Node3D
{
    private const string RoomCodeAlphabet = "acdefhjkmnprtvwxy347";
    private enum Page { Choice, RelayConfigure, LanChoice, Configure, Lobby }

    private readonly List<VrButton> _choiceButtons = new();
    private readonly List<VrButton> _configButtons = new();
    private readonly List<VrButton> _relayButtons = new();
    private readonly List<VrButton> _lanChoiceButtons = new();
    private readonly List<VrButton> _keypadButtons = new();
    private readonly List<VrButton> _roomCodeKeyButtons = new();
    private readonly List<VrButton> _lobbyButtons = new();
    private Node3D _choice = null!;
    private Node3D _config = null!;
    private Node3D _relayConfig = null!;
    private Node3D _lanChoice = null!;
    private Node3D _lobby = null!;
    private Label3D _configTitle = null!;
    private Label3D _addressLabel = null!;
    private Label3D _lobbyLabel = null!;
    private Label3D _roomCodeLabel = null!;
    private VrButton _playersButton = null!;
    private VrButton _modeButton = null!;
    private VrButton _readyButton = null!;
    private VrButton _relayPlayersButton = null!;
    private VrButton _relayModeButton = null!;
    private VrButton _roomCodeInputButton = null!;
    private VrButton _relaySubmitButton = null!;
    private VrButton _editButton = null!;
    private VrButton _cancelButton = null!;
    private float _side;
    private bool _joining;
    private int _players = 2;
    private int _modeId = 1;
    private string _address = "";
    private string _roomCode = "";
    private bool _relay;
    private bool _localReady;
    private bool _errorState;

    public bool IsOpen { get; private set; }
    public bool IsLobby => IsOpen && _lobby.Visible && !_errorState;

    public event Action<int, int>? OnHost;
    public event Action<string, int, int>? OnJoin;
    public event Action<int, int>? OnHostRoom;
    public event Action<string>? OnJoinRoom;
    public event Action<bool>? OnReady;
    public event Action? OnCancel;
    public event Action? OnBack;

    public void Build(float arenaSideMeters)
    {
        float s = arenaSideMeters;
        _side = s;
        Position = SpatialMenuPlacement.PlayerFacing(s);
        RotationDegrees = new Vector3(-12, 180, 0);
        ClassicPanel.Build(this, s * 1.18f, s * 1.35f, z: -0.012f);

        _choice = new Node3D(); AddChild(_choice);
        AddLabel(_choice, "MULTIPLAYER", 0, s * 0.50f, 30);
        AddButton(_choice, _choiceButtons, "Host Room", 0, s * 0.25f, s * 0.62f, s * 0.12f,
            () => OpenRelayConfigure(joining: false));
        AddButton(_choice, _choiceButtons, "Join Room", 0, s * 0.08f, s * 0.62f, s * 0.12f,
            () => OpenRelayConfigure(joining: true));
        AddButton(_choice, _choiceButtons, "Advanced / Direct LAN", 0, -s * 0.09f, s * 0.62f, s * 0.12f,
            () => ShowPage(Page.LanChoice));
        AddButton(_choice, _choiceButtons, "Back", 0, -s * 0.31f, s * 0.34f, s * 0.11f,
            () => { Close(); OnBack?.Invoke(); });

        _relayConfig = new Node3D { Visible = false }; AddChild(_relayConfig);
        AddLabel(_relayConfig, "FRIENDS-ONLY ROOM", 0, s * 0.53f, 27);
        _roomCodeLabel = AddLabel(_relayConfig, "", 0, s * 0.39f, 25);
        _relayPlayersButton = AddButton(_relayConfig, _relayButtons, "Players", -s * 0.27f, s * 0.28f,
            s * 0.42f, s * 0.09f, CyclePlayers);
        _relayModeButton = AddButton(_relayConfig, _relayButtons, "Mode", s * 0.27f, s * 0.28f,
            s * 0.42f, s * 0.09f, CycleMode);
        for (int i = 0; i < RoomCodeAlphabet.Length; i++)
        {
            string key = RoomCodeAlphabet[i].ToString();
            int row = i / 6;
            int col = i % 6;
            VrButton keyButton = AddButton(_relayConfig, _relayButtons, key,
                (col - 2.5f) * s * 0.13f, s * 0.16f - row * s * 0.085f,
                s * 0.105f, s * 0.065f, () => RoomCodeKey(key));
            _roomCodeKeyButtons.Add(keyButton);
        }
        VrButton deleteButton = AddButton(_relayConfig, _relayButtons, "Delete", -s * 0.30f, -s * 0.39f, s * 0.30f, s * 0.09f,
            () => RoomCodeKey("Del"));
        _roomCodeKeyButtons.Add(deleteButton);
        _roomCodeInputButton = AddButton(_relayConfig, _relayButtons, "Open Keyboard", 0, s * 0.08f, s * 0.58f, s * 0.12f,
            OpenNativeRoomCodeKeyboard);
        _relaySubmitButton = AddButton(_relayConfig, _relayButtons, "Create / Join", s * 0.20f, -s * 0.39f, s * 0.48f, s * 0.09f,
            StartRelayRequested);
        AddButton(_relayConfig, _relayButtons, "Back", 0, -s * 0.51f, s * 0.28f, s * 0.08f,
            () => ShowPage(Page.Choice));

        _lanChoice = new Node3D { Visible = false }; AddChild(_lanChoice);
        AddLabel(_lanChoice, "ADVANCED - DIRECT LAN", 0, s * 0.50f, 27);
        AddButton(_lanChoice, _lanChoiceButtons, "Host LAN", 0, s * 0.20f, s * 0.62f, s * 0.13f,
            () => OpenConfigure(joining: false));
        AddButton(_lanChoice, _lanChoiceButtons, "Join LAN", 0, 0, s * 0.62f, s * 0.13f,
            () => OpenConfigure(joining: true));
        AddButton(_lanChoice, _lanChoiceButtons, "Back", 0, -s * 0.27f, s * 0.34f, s * 0.12f,
            () => ShowPage(Page.Choice));

        _config = new Node3D { Visible = false }; AddChild(_config);
        _configTitle = AddLabel(_config, "", 0, s * 0.52f, 28);
        _addressLabel = AddLabel(_config, "", 0, s * 0.36f, 25);
        _playersButton = AddButton(_config, _configButtons, "Players: 2", -s * 0.27f, s * 0.24f,
            s * 0.42f, s * 0.10f, CyclePlayers);
        _modeButton = AddButton(_config, _configButtons, "Mode: Survival", s * 0.27f, s * 0.24f,
            s * 0.42f, s * 0.10f, CycleMode);

        string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "Del" };
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            int row = i / 3;
            int col = i % 3;
            VrButton keypadButton = AddButton(_config, _configButtons, key,
                (col - 1) * s * 0.17f, s * 0.08f - row * s * 0.13f,
                s * 0.13f, s * 0.09f, () => AddressKey(key));
            _keypadButtons.Add(keypadButton);
        }
        AddButton(_config, _configButtons, "Start", s * 0.31f, -s * 0.49f, s * 0.34f, s * 0.11f, StartRequested);
        AddButton(_config, _configButtons, "Back", -s * 0.31f, -s * 0.49f, s * 0.34f, s * 0.11f,
            () => ShowPage(Page.Choice));

        _lobby = new Node3D { Visible = false }; AddChild(_lobby);
        AddLabel(_lobby, "MULTIPLAYER LOBBY", 0, s * 0.50f, 30);
        _lobbyLabel = AddLabel(_lobby, "Connecting...", 0, s * 0.12f, 23);
        _lobbyLabel.Width = s * 1.02f / _lobbyLabel.PixelSize;
        _lobbyLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _readyButton = AddButton(_lobby, _lobbyButtons, "Ready", s * 0.24f, -s * 0.34f, s * 0.36f, s * 0.12f,
            ReadyOrRetry);
        _cancelButton = AddButton(_lobby, _lobbyButtons, "Cancel", -s * 0.24f, -s * 0.34f, s * 0.36f, s * 0.12f,
            () => OnCancel?.Invoke());
        _editButton = AddButton(_lobby, _lobbyButtons, "Edit", 0.0f, -s * 0.34f, s * 0.30f, s * 0.12f,
            EditAfterError);
        _editButton.Visible = false;
        Visible = false;
    }

    public void Open()
    {
        _localReady = false;
        SetLobbyErrorActions(false);
        IsOpen = true;
        Visible = true;
        ShowPage(Page.Choice);
    }

    public void Close()
    {
        CloseNativeRoomCodeKeyboard();
        IsOpen = false;
        Visible = false;
    }

    public void ReturnFromLobby()
    {
        CloseNativeRoomCodeKeyboard();
        _localReady = false;
        SetLobbyErrorActions(false);
        IsOpen = true;
        Visible = true;
        if (_relay && _joining)
        {
            _roomCode = "";
            UpdateRelayLabels();
            ShowPage(Page.RelayConfigure);
            if (OS.GetName() == "Android")
                Callable.From(OpenNativeRoomCodeKeyboard).CallDeferred();
            return;
        }

        ShowPage(Page.Choice);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (!IsOpen || !_relayConfig.Visible || !_joining || !QuestTextInput.IsAvailable) return;
        if (!QuestTextInput.Poll(out string text, out bool submitted)) return;
        NativeRoomCodeChanged(text);
        if (submitted) NativeRoomCodeSubmitted(text);
    }

    public void ShowLobby(NetworkLobbyStatus status)
    {
        IsOpen = true;
        Visible = true;
        if (!IsLobby) ShowPage(Page.Lobby);
        SetLobbyErrorActions(false);
        bool relay = status.Netcode == "rollback";
        string endpoint = relay
            ? (string.IsNullOrWhiteSpace(status.RoomCode) ? "Creating room..." : $"Code: {status.RoomCode.ToUpperInvariant()}")
            : status.Role == "host" ? $"Share {LocalIpv4()}:{status.BoundPort}" : $"Joining {_address}:31993";
        string slots = string.Join("\n", status.Slots.Select(FormatLobbySlot));
        string failure = string.IsNullOrWhiteSpace(status.Failure) ? "" : $"\nERROR: {status.Failure}";
        string mode = status.ModeId == 2 ? "Rush" : "Survival";
        string recovery = status.Phase == "reconnecting" ? "\nReconnecting / resyncing..." : "";
        _lobbyLabel.Text = $"{endpoint}  -  {mode}\n{status.Connected}/{status.Expected} connected\n{slots}{recovery}{failure}";
        NetworkLobbySlot? local = status.Slots.FirstOrDefault(slot => slot.SlotIndex == status.LocalSlot);
        if (local != null) _localReady = local.Ready;
        // Relay room state arrives before RoomStart assigns the local slot. In
        // that interval the native phase is still "connecting", but the peer is
        // already a valid room member and the relay accepts its ready command.
        bool relayRoomReadyable = relay && !status.Started && status.Slots.Any(slot => slot.Connected);
        _readyButton.Visible = (status.Phase == "lobby" && local?.Connected == true) || relayRoomReadyable;
        _readyButton.SetText(_localReady ? "Unready" : "Ready");
    }

    public void ShowError(string message)
    {
        IsOpen = true;
        Visible = true;
        ShowPage(Page.Lobby);
        _lobbyLabel.Text = $"Multiplayer session failed\n{message}\nYour room code or address was preserved.";
        SetLobbyErrorActions(true);
    }

    public void PollPoke(ReadOnlySpan<HandProbe> probes)
    {
        if (!IsOpen) return;
        List<VrButton> buttons = _choice.Visible ? _choiceButtons
            : _relayConfig.Visible ? _relayButtons
            : _lanChoice.Visible ? _lanChoiceButtons
            : _config.Visible ? _configButtons : _lobbyButtons;
        foreach (VrButton button in buttons)
        {
            if (button.Visible) button.PollPoke(probes);
        }
    }

    private void OpenConfigure(bool joining)
    {
        _relay = false;
        _joining = joining;
        _configTitle.Text = joining ? "JOIN LAN - HOST IPv4" : "HOST LAN";
        foreach (VrButton button in _keypadButtons) button.Visible = joining;
        UpdateConfigLabels();
        ShowPage(Page.Configure);
    }

    private void OpenRelayConfigure(bool joining)
    {
        _relay = true;
        _joining = joining;
        _roomCode = "";
        UpdateRelayLabels();
        ShowPage(Page.RelayConfigure);
        if (joining && OS.GetName() == "Android")
            Callable.From(OpenNativeRoomCodeKeyboard).CallDeferred();
    }

    private void RoomCodeKey(string key)
    {
        if (!_joining) return;
        if (key == "Del")
        {
            if (_roomCode.Length > 0) _roomCode = _roomCode[..^1];
        }
        else if (_roomCode.Length < 4) _roomCode += key;
        UpdateRelayLabels();
    }

    private void ReadyOrRetry()
    {
        if (_errorState)
        {
            SetLobbyErrorActions(false);
            _lobbyLabel.Text = "Retrying connection...";
            if (_relay)
            {
                if (_joining) OnJoinRoom?.Invoke(_roomCode);
                else OnHostRoom?.Invoke(_players, _modeId);
            }
            else
            {
                if (_joining) OnJoin?.Invoke(_address, _players, _modeId);
                else OnHost?.Invoke(_players, _modeId);
            }
            return;
        }
        ToggleReady();
    }

    private void ToggleReady()
    {
        _localReady = !_localReady;
        _readyButton.SetText(_localReady ? "Unready" : "Ready");
        OnReady?.Invoke(_localReady);
    }

    private void EditAfterError()
    {
        SetLobbyErrorActions(false);
        ShowPage(_relay ? Page.RelayConfigure : Page.Configure);
    }

    private void SetLobbyErrorActions(bool error)
    {
        _errorState = error;
        _editButton.Visible = error;
        _readyButton.Visible = error || _readyButton.Visible;
        _readyButton.SetText(error ? "Retry" : (_localReady ? "Unready" : "Ready"));
        _cancelButton.SetText(error ? "Back" : "Cancel");
        _readyButton.Position = new Vector3(error ? -_side * 0.36f : _side * 0.24f, -_side * 0.34f, 0.0f);
        _editButton.Position = new Vector3(0.0f, -_side * 0.34f, 0.0f);
        _cancelButton.Position = new Vector3(error ? _side * 0.36f : -_side * 0.24f, -_side * 0.34f, 0.0f);
        _readyButton.ResetPress();
        _editButton.ResetPress();
        _cancelButton.ResetPress();
    }

    private void UpdateRelayLabels()
    {
        _roomCodeLabel.Text = _joining ? $"Room code: {_roomCode.ToUpperInvariant()}" : $"Create {_players}-player {(_modeId == 2 ? "Rush" : "Survival")} room";
        _relayPlayersButton.Visible = !_joining;
        _relayModeButton.Visible = !_joining;
        bool nativeKeyboard = QuestTextInput.IsAvailable;
        foreach (VrButton button in _roomCodeKeyButtons) button.Visible = _joining && !nativeKeyboard;
        _roomCodeInputButton.Visible = _joining && nativeKeyboard;
        _relaySubmitButton.SetText(_joining ? "Join Room" : "Create Room");
    }

    private void OpenNativeRoomCodeKeyboard()
    {
        if (!_joining) return;
        QuestTextInput.Show(_roomCode, 4, roomCode: true);
    }

    private static void CloseNativeRoomCodeKeyboard() => QuestTextInput.Hide();

    private void NativeRoomCodeChanged(string text)
    {
        string filtered = new(text.ToLowerInvariant().Where(RoomCodeAlphabet.Contains).Take(4).ToArray());
        _roomCode = filtered;
        UpdateRelayLabels();
    }

    private void NativeRoomCodeSubmitted(string text)
    {
        NativeRoomCodeChanged(text);
        if (_roomCode.Length != 4) return;
        CloseNativeRoomCodeKeyboard();
        StartRelayRequested();
    }

    private void StartRelayRequested()
    {
        if (_joining && _roomCode.Length != 4)
        {
            _roomCodeLabel.Text = "Enter all four characters";
            return;
        }
        ShowPage(Page.Lobby);
        if (_joining) OnJoinRoom?.Invoke(_roomCode);
        else OnHostRoom?.Invoke(_players, _modeId);
    }

    private void CyclePlayers()
    {
        _players = _players == 4 ? 2 : _players + 1;
        UpdateConfigLabels();
        if (_relay) UpdateRelayLabels();
    }

    private void CycleMode()
    {
        _modeId = _modeId == 1 ? 2 : 1;
        UpdateConfigLabels();
        if (_relay) UpdateRelayLabels();
    }

    private void AddressKey(string key)
    {
        if (!_joining) return;
        if (key == "Del")
        {
            if (_address.Length > 0) _address = _address[..^1];
        }
        else if (_address.Length < 15)
        {
            _address += key;
        }
        UpdateConfigLabels();
    }

    private void UpdateConfigLabels()
    {
        _playersButton.SetText($"Players: {_players}");
        _modeButton.SetText(_modeId == 2 ? "Mode: Rush" : "Mode: Survival");
        _addressLabel.Text = _joining ? $"Host: {_address}" : $"Port 31993\nThis device: {LocalIpv4()}";
    }

    private void StartRequested()
    {
        ShowPage(Page.Lobby);
        if (_joining) OnJoin?.Invoke(_address, _players, _modeId);
        else OnHost?.Invoke(_players, _modeId);
    }

    private void ShowPage(Page page)
    {
        _choice.Visible = page == Page.Choice;
        _relayConfig.Visible = page == Page.RelayConfigure;
        _lanChoice.Visible = page == Page.LanChoice;
        _config.Visible = page == Page.Configure;
        _lobby.Visible = page == Page.Lobby;
        IEnumerable<VrButton> active = page == Page.Choice ? _choiceButtons
            : page == Page.RelayConfigure ? _relayButtons
            : page == Page.LanChoice ? _lanChoiceButtons
            : page == Page.Configure ? _configButtons : _lobbyButtons;
        foreach (VrButton button in active) button.ResetPress();
    }

    private static Label3D AddLabel(Node3D parent, string text, float x, float y, int fontSize)
    {
        var label = new Label3D
        {
            Text = text,
            Position = new Vector3(x, y, 0.018f),
            FontSize = fontSize,
            PixelSize = 0.0007f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            NoDepthTest = true,
            // ClassicPanel occupies priorities 58..67. Without an explicit
            // UI priority its transparent plate can draw after Label3D, hiding
            // the label over the panel while leaving overflow visible outside.
            RenderPriority = 68,
        };
        parent.AddChild(label);
        return label;
    }

    internal static string FormatLobbySlot(NetworkLobbySlot slot)
    {
        if (!slot.Connected) return $"Player {slot.SlotIndex + 1}: Pending";
        string name = slot.PeerName.Trim();
        if (name.Length == 0 || string.Equals(name, "Player", StringComparison.OrdinalIgnoreCase))
            name = $"Player {slot.SlotIndex + 1}";
        return $"{name}: {(slot.Ready ? "Ready" : "Pending")}";
    }

    private static VrButton AddButton(Node3D parent, List<VrButton> list, string text,
        float x, float y, float width, float height, Action action)
    {
        var button = new VrButton();
        parent.AddChild(button);
        button.BuildClassic(width, height, text);
        button.Position = new Vector3(x, y, 0);
        button.OnPress += action;
        list.Add(button);
        return button;
    }

    private static string LocalIpv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?.ToString() ?? "device IPv4";
        }
        catch
        {
            return "device IPv4";
        }
    }
}
