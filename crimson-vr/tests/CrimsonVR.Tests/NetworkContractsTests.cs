using System.Runtime.CompilerServices;
using System.Text.Json;
using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

public sealed class NetworkContractsTests
{
    [Fact]
    public void NetworkUpdateMatchesAbiV27Layout()
    {
        Assert.Equal(60, Unsafe.SizeOf<Sim.NetworkUpdate>());
    }

    [Fact]
    public void LobbyStatusDecodesNativeSnakeCaseJson()
    {
        const string json = """
            {"phase":"lobby","role":"join","netcode":"rollback","room_code":"a1b2","local_slot":2,"bound_port":31994,
             "expected":4,"connected":3,"ready":3,"started":false,"mode_id":2,"failure":"",
             "slots":[{"slot_index":2,"connected":true,"ready":true,"is_host":false,"peer_name":"Quest"}]}
            """;

        NetworkLobbyStatus status = JsonSerializer.Deserialize<NetworkLobbyStatus>(json)!;
        Assert.Equal("lobby", status.Phase);
        Assert.Equal("rollback", status.Netcode);
        Assert.Equal("a1b2", status.RoomCode);
        Assert.Equal(2, status.LocalSlot);
        Assert.Equal(4, status.Expected);
        Assert.Equal(2, status.ModeId);
        Assert.Single(status.Slots);
        Assert.Equal("Quest", status.Slots[0].PeerName);
    }
}
