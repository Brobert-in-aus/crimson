from __future__ import annotations

from crimson.net.lockstep_protocol import (
    LockstepPacket,
    ResyncChunk,
    TickFrame,
    decode_packet,
    encode_packet,
)
from crimson.sim.input_providers import PerkPickCommand

# These are also decoded by crimson-zig/src/net/lockstep_protocol.zig. Keeping
# the same fixtures on both sides catches schema, tag, field-order and numeric
# representation drift without requiring a live socket.
_RESYNC_CHUNK = bytes.fromhex(
    "84a373657101a361636b00a872656c6961626c65c3a76d65737361676584"
    "a474797065ac726573796e635f6368756e6ba973747265616d5f6964a173"
    "ab6368756e6b5f696e64657802a77061796c6f6164c403616263",
)
_TICK_FRAME = bytes.fromhex(
    "84a373657103a361636b02a872656c6961626c65c3a76d65737361676584"
    "a474797065aa7469636b5f6672616d65aa7469636b5f696e64657805ac66"
    "72616d655f696e707574739195cb3fe0000000000000cb00000000000000"
    "00cb3ff0000000000000cb400000000000000003a8636f6d6d616e6473"
    "9183a474797065a97065726b5f7069636bac706c617965725f696e646578"
    "00ac63686f6963655f696e64657802",
)


def test_python_and_zig_share_resync_chunk_golden_packet() -> None:
    packet = decode_packet(_RESYNC_CHUNK)
    assert packet.seq == 1
    assert packet.ack == 0
    assert packet.reliable is True
    assert isinstance(packet.message, ResyncChunk)
    assert packet.message.stream_id == "s"
    assert packet.message.chunk_index == 2
    assert packet.message.payload == b"abc"
    assert encode_packet(packet) == _RESYNC_CHUNK


def test_python_and_zig_share_tick_frame_and_command_golden_packet() -> None:
    packet = decode_packet(_TICK_FRAME)
    assert packet.seq == 3
    assert packet.ack == 2
    assert packet.reliable is True
    assert isinstance(packet.message, TickFrame)
    assert packet.message.tick_index == 5
    assert packet.message.frame_inputs == [[0.5, 0.0, 1.0, 2.0, 3]]
    assert packet.message.commands == [PerkPickCommand(player_index=0, choice_index=2)]
    assert encode_packet(packet) == _TICK_FRAME


def test_golden_packets_are_reconstructable_from_python_types() -> None:
    packet = LockstepPacket(
        seq=3,
        ack=2,
        reliable=True,
        message=TickFrame(
            tick_index=5,
            frame_inputs=[[0.5, 0.0, 1.0, 2.0, 3]],
            commands=[PerkPickCommand(player_index=0, choice_index=2)],
        ),
    )
    assert encode_packet(packet) == _TICK_FRAME
