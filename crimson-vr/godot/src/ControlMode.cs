namespace CrimsonVR;

/// <summary>
/// Where the player's hands act, which is what decides where the playfield can
/// be. Both modes project hand POSITION (never controller orientation) onto a
/// square plane and map that plane onto the arena; they differ only in which
/// plane, and therefore in how free the board is to move.
/// </summary>
public enum ControlMode
{
    /// <summary>Hands project directly onto the playfield: reaching into the
    /// miniature arena IS the control. The original scheme. Because the board
    /// doubles as the control surface it has to stay within arm's reach, and
    /// hand travel scales with however large the board is drawn.</summary>
    Tabletop = 0,

    /// <summary>Hands project onto a separate control rectangle at a comfortable
    /// seated position, like an arcade cabinet's control panel below its screen.
    /// The board becomes pure display — free to be large, distant and tilted up
    /// into a comfortable gaze line — and hand travel is fixed by the rectangle
    /// no matter what the board does.</summary>
    Cabinet = 1,
}
