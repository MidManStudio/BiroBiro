// PenController.cs — v3
// Changes from v2:
//   - SubmitFlickServerRpc: adds forceOffsetNorm (-0.5=tail, 0=center, 0.5=tip)
//     AddForceAtPosition uses this offset → physics produces spin/arch naturally
//   - SubmitRotateServerRpc: commits rotation as the turn move, no physics
//   - SpinMoveServerRpc: torque around pen's long axis to dislodge stacked biros
//   - PenLength public accessor for FlickInputHandler raycast offset calculation

using Unity.Netcode;
using UnityEngine;
using Biros.Core;
using Biros.Config;

namespace Biros.Gameplay
{
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    public class PenController : NetworkBehaviour
    {
        private const float OOB_Y             = -1.5f;
        private const float BASE_STATIC_FRIC  = 0.22f;
        private const float BASE_DYNAMIC_FRIC = 0.12f;
        private const float BASE_BOUNCINESS   = 0.15f;

        [SerializeField] private PenConfigSO _config;

        [Tooltip("World-space length tip to tail. Match your pen prefab scale exactly.")]
        [SerializeField] private float _penLength = 0.35f;

        private Rigidbody _rb;
        private Collider  _col;

        public Rigidbody  PhysicsBody => _rb;
        public float      PenLength   => _penLength;

        // ── Network State ──────────────────────────────────────────────────
        private NetworkVariable<int> _ownerSlot = new(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<ulong> _ownerClientId = new(
            ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<bool> _isOutOfBounds = new(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public int        OwnerSlot     => _ownerSlot.Value;
        public ulong      OwnerClientId => _ownerClientId.Value;
        public bool       IsOutOfBounds => _isOutOfBounds.Value;
        public PenConfigSO Config       => _config;

        public bool IsSettled(float linearThreshold, float angularThreshold)
        {
            if (!IsServer || _isOutOfBounds.Value) return true;
            return _rb.velocity.magnitude  < linearThreshold &&
                   _rb.angularVelocity.magnitude < angularThreshold;
        }

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake()
        {
            _rb  = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            PenRegistry.Instance?.Register(this);
            _rb.isKinematic = true;

            if (IsServer)
            {
                if (_config != null) ApplyConfig();
                if (MatchStateManager.Instance != null)
                {
                    MatchStateManager.Instance.OnPhaseChanged        += HandlePhaseChanged;
                    MatchStateManager.Instance.OnReplayPauseChanged  += HandleReplayPauseChanged;
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            PenRegistry.Instance?.Unregister(this);
            if (IsServer && MatchStateManager.Instance != null)
            {
                MatchStateManager.Instance.OnPhaseChanged       -= HandlePhaseChanged;
                MatchStateManager.Instance.OnReplayPauseChanged -= HandleReplayPauseChanged;
            }
        }

        private void FixedUpdate()
        {
            if (!IsServer || _isOutOfBounds.Value) return;
            if (transform.position.y < OOB_Y) ServerHandleOutOfBounds();
        }

        // ── Server Init ────────────────────────────────────────────────────
        public void ServerInitialize(int ownerSlot, ulong ownerClientId, PenConfigSO config)
        {
            if (!IsServer) return;
            _ownerSlot.Value     = ownerSlot;
            _ownerClientId.Value = ownerClientId;
            _config              = config;
            ApplyConfig();
        }

        // ── Phase / Pause: kinematic toggle ─────────────────────────────────
        private void HandlePhaseChanged(MatchPhase prev, MatchPhase next) => RecomputeKinematic();

        // Pausing for a replay does NOT change _phase.Value — that's the whole
        // point, the match freezes exactly where it was so it can resume from
        // there. Which means HandlePhaseChanged alone never fires when a pause
        // starts or ends. This is the hook that actually catches that.
        private void HandleReplayPauseChanged(bool paused) => RecomputeKinematic();

        private void RecomputeKinematic()
        {
            if (!IsServer || _isOutOfBounds.Value) return;
            if (MatchStateManager.Instance == null) return;

            // ExecuteFlick MUST be included here. SubmitFlickServerRpc sets isKinematic=false
            // and queues AddForceAtPosition, then immediately calls ServerNotifyFlickSubmitted()
            // which transitions the phase to ExecuteFlick. NetworkVariable.OnValueChanged fires
            // synchronously on the server, so without ExecuteFlick in this list, isKinematic got
            // set back to true in the same call stack — before PhysX ever got a FixedUpdate to
            // actually apply the queued impulse. The force was silently discarded every time.
            MatchPhase phase = MatchStateManager.Instance.CurrentPhase;
            bool simulate = !MatchStateManager.Instance.IsPausedForReplay &&
                            (phase == MatchPhase.ExecuteFlick ||
                             phase == MatchPhase.SimulatePhysics ||
                             phase == MatchPhase.ResolveRound);
            _rb.isKinematic = !simulate;
        }

        // ── RPC: Live orientation preview ──────────────────────────────────
        /// <summary>
        /// Sent at ~20 Hz while the player rotates the pen during their turn.
        /// Server sets transform.rotation; NetworkTransform broadcasts it to all clients.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void UpdateOrientationServerRpc(float hAngle, float vAngle,
                                               ServerRpcParams rpcParams = default)
        {
            if (!ValidateActiveOwner(rpcParams.Receive.SenderClientId)) return;
            if (MatchStateManager.Instance?.CurrentPhase != MatchPhase.ActiveTurnInput) return;

            vAngle = Mathf.Clamp(vAngle, 0f, 85f);
            transform.rotation = Quaternion.Euler(-vAngle, hAngle, 0f);
        }

        // ── RPC: Commit rotation as turn move ──────────────────────────────
        /// <summary>
        /// Player chose to use their turn purely for alignment.
        /// Locks in orientation, advances to SwitchPlayer with no physics.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SubmitRotateServerRpc(float hAngle, float vAngle,
                                          ServerRpcParams rpcParams = default)
        {
            if (!ValidateActiveOwner(rpcParams.Receive.SenderClientId)) return;
            if (MatchStateManager.Instance?.CurrentPhase != MatchPhase.ActiveTurnInput) return;

            vAngle = Mathf.Clamp(vAngle, 0f, 85f);
            transform.rotation = Quaternion.Euler(-vAngle, hAngle, 0f);

            MatchStateManager.Instance.ServerNotifyRotateSubmitted();
        }

        // ── RPC: Flick ─────────────────────────────────────────────────────
        /// <summary>
        /// forceOffsetNorm: -0.5 = tail, 0 = center, 0.5 = tip.
        /// AddForceAtPosition at that point creates the correct torque automatically:
        ///   center tap  → mostly linear, low spin → flat straight shot
        ///   tip tap     → high torque, lots of spin → pen arches and tumbles
        /// No artificial arching code — pure Unity physics.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SubmitFlickServerRpc(float hAngle, float vAngle,
                                         float force, float forceOffsetNorm,
                                         ServerRpcParams rpcParams = default)
        {
            if (!ValidateActiveOwner(rpcParams.Receive.SenderClientId)) return;
            if (MatchStateManager.Instance?.CurrentPhase != MatchPhase.ActiveTurnInput) return;

            vAngle = Mathf.Clamp(vAngle, 0f, 85f);
            transform.rotation = Quaternion.Euler(-vAngle, hAngle, 0f);

            _rb.isKinematic = false;

            Vector3 launchDir = transform.forward;

            float clampedForce = Mathf.Clamp(force, 0.05f, _config.maxFlickForce)
                                 * _config.flickForceMultiplier;

            // Clamp offset to valid range and compute world-space force point
            forceOffsetNorm = Mathf.Clamp(forceOffsetNorm, -0.5f, 0.5f);
            Vector3 forcePoint = transform.position + transform.forward
                                 * (_penLength * forceOffsetNorm);

            _rb.AddForceAtPosition(launchDir * clampedForce, forcePoint, ForceMode.Impulse);

            // BittenChewed cap: additional random torque on top of natural spin
            if (_config.ErraticSpinMultiplier > 1f)
            {
                Vector3 chaos = UnityEngine.Random.insideUnitSphere
                                * clampedForce
                                * (_config.ErraticSpinMultiplier - 1f);
                _rb.AddTorque(chaos, ForceMode.Impulse);
            }

            MatchStateManager.Instance.ServerNotifyFlickSubmitted();
        }

        // ── RPC: Spin move ─────────────────────────────────────────────────
        /// <summary>
        /// Applies torque around the pen's own long axis — spins it like a drill bit
        /// on the desk surface. When biros are stacked or touching, the contact friction
        /// during the spin pushes the other pen away. Counts as the player's turn.
        /// clockwise is relative to transform.forward pointing away from you.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SpinMoveServerRpc(float torqueForce, bool clockwise,
                                      ServerRpcParams rpcParams = default)
        {
            if (!ValidateActiveOwner(rpcParams.Receive.SenderClientId)) return;
            if (MatchStateManager.Instance?.CurrentPhase != MatchPhase.ActiveTurnInput) return;

            _rb.isKinematic = false;

            float direction = clockwise ? 1f : -1f;

            // Primary: spin around long axis
            _rb.AddTorque(transform.forward * torqueForce * direction, ForceMode.Impulse);

            // Tiny upward nudge reduces normal force briefly, lowering desk friction
            // so the spin can actually develop before being damped — feels snappier
            _rb.AddForce(transform.up * (torqueForce * 0.08f), ForceMode.Impulse);

            MatchStateManager.Instance.ServerNotifyFlickSubmitted();
        }

        // ── Private ─────────────────────────────────────────────────────────
        private bool ValidateActiveOwner(ulong senderId)
        {
            if (MatchStateManager.Instance == null) return false;
            return _ownerClientId.Value == senderId &&
                   MatchStateManager.Instance.ActivePlayerClientId == senderId;
        }

        private void ApplyConfig()
        {
            if (_config == null) return;

            _rb.mass           = _config.ComputedMass;
            _rb.drag  = _config.drag;
            _rb.angularDrag = _config.angularDrag;
            _rb.centerOfMass   = new Vector3(0f, 0f, _config.CenterOfMassShiftZ);
            
            var mat = new PhysicMaterial($"PenMat_{_config.configId}")
            {
                staticFriction  = BASE_STATIC_FRIC  * _config.ComputedFrictionScalar,
                dynamicFriction = BASE_DYNAMIC_FRIC * _config.ComputedFrictionScalar,
                bounciness      = BASE_BOUNCINESS   * _config.ComputedImpactDampening,
                frictionCombine = PhysicMaterialCombine.Multiply,
                bounceCombine   = PhysicMaterialCombine.Average,
            };
            _col.material = mat;
        }

        private void ServerHandleOutOfBounds()
        {
            _isOutOfBounds.Value = true;
            _rb.isKinematic      = true;
            _rb.velocity   = Vector3.zero;
            _rb.angularVelocity  = Vector3.zero;
            MatchStateManager.Instance?.ServerOnPenExitedBounds(_ownerClientId.Value);
        }
    }
}
