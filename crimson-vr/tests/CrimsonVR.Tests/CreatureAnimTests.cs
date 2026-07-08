using CrimsonVR;
using Xunit;

namespace CrimsonVR.Tests;

/// <summary>
/// Golden-value tests for CreatureAnim.SelectFrame against the Python reference
/// (creature_anim_select_frame, src/crimson/creatures/anim.py). Values were
/// generated directly from that reference; any drift in the port fails here.
/// </summary>
public sealed class CreatureAnimTests
{
    // zombie: base=0x20, mirror=false, flags=0 (long strip, base unused positive)
    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(3.4f, 3)]
    [InlineData(7.6f, 8)]
    [InlineData(15.0f, 15)]
    [InlineData(22.9f, 23)]
    [InlineData(31.0f, 31)]
    [InlineData(-1.0f, 47)] // negative phase -> base + 0x0F
    public void Zombie_LongStrip(float phase, int expected)
        => Assert.Equal(expected, CreatureAnim.SelectFrame(phase, 0x20, false, 0));

    // lizard: base=0x10, mirror=true, flags=0 (long strip, index folds >0x0F)
    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(3.4f, 3)]
    [InlineData(7.6f, 8)]
    [InlineData(15.0f, 15)]
    [InlineData(16.4f, 15)]
    [InlineData(22.9f, 8)]
    [InlineData(31.0f, 0)]
    public void Lizard_LongStrip_Mirror(float phase, int expected)
        => Assert.Equal(expected, CreatureAnim.SelectFrame(phase, 0x10, true, 0));

    // alien with RANGED_ATTACK_SHOCK (0x10): long strip + 0x20 offset
    [Theory]
    [InlineData(0.0f, 32)]
    [InlineData(7.6f, 40)]
    [InlineData(15.0f, 47)]
    public void Alien_Shock(float phase, int expected)
        => Assert.Equal(expected, CreatureAnim.SelectFrame(phase, 0x20, false, CreatureAnim.FlagRangedAttackShock));

    // spider with ANIM_PING_PONG (0x04): base + 0x10 + ping-pong idx
    [Theory]
    [InlineData(0.0f, 32)]
    [InlineData(2.4f, 34)]
    [InlineData(5.6f, 38)]
    [InlineData(7.4f, 39)]
    [InlineData(9.9f, 37)]
    [InlineData(13.0f, 34)]
    [InlineData(15.0f, 32)]
    public void Spider_PingPong(float phase, int expected)
        => Assert.Equal(expected, CreatureAnim.SelectFrame(phase, 0x10, true, CreatureAnim.FlagAnimPingPong));

    // PING_PONG | LONG_STRIP -> long strip wins (mirror folds)
    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(7.6f, 8)]
    [InlineData(20.0f, 11)]
    public void PingPong_And_LongStrip_IsLongStrip(float phase, int expected)
        => Assert.Equal(
            expected,
            CreatureAnim.SelectFrame(phase, 0x10, true,
                CreatureAnim.FlagAnimPingPong | CreatureAnim.FlagAnimLongStrip));

    [Fact]
    public void IsLongStrip_FlagLogic()
    {
        Assert.True(CreatureAnim.IsLongStrip(0));                                  // no ping-pong bit
        Assert.False(CreatureAnim.IsLongStrip(CreatureAnim.FlagAnimPingPong));     // ping-pong only
        Assert.True(CreatureAnim.IsLongStrip(                                      // long-strip overrides
            CreatureAnim.FlagAnimPingPong | CreatureAnim.FlagAnimLongStrip));
    }
}
