// MatchPhase.cs
// Byte-backed so it fits cleanly in NetworkVariable<MatchPhase>.
// Mirrors the state_machine_nodes from the game design spec exactly.

namespace Biros.Core
{
    /// <summary>
    /// Authoritative match phase. Server owns all transitions.
    /// Clients observe via NetworkVariable — never write directly.
    /// </summary>
    public enum MatchPhase : byte
    {
        None = 0,   // Pre-match / not yet initialized
        InitializeMatch = 1,   // Spawn pens, assign slots, prep arena
        ActiveTurnInput = 2,   // Waiting for active player's flick input
        ExecuteFlick = 3,   // Impulse queued — one-frame bridge before physics
        SimulatePhysics = 4,   // Server polling Rigidbody velocities
        ResolveRound = 5,   // Settle confirmed — evaluate OOB, score, hazards
        SwitchPlayer = 6,   // Advance turn slot, loop back to ActiveTurnInput
        MatchOver = 7,   // Terminal state; show results screen
    }
}