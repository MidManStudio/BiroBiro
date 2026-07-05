// PenController.cs
// Key changes from v1:
//   - UpdateOrientationServerRpc: live rotation preview during aim
//   - SubmitFlickServerRpc: hAngle+vAngle+force (no raw direction — derived server-side)
//   - AddForceAtPosition at pen tip → realistic spin/tumble on launch
//   - Phase-based kinematic toggle: kinematic during aim, dynamic during simulate
//   - PhysicsBody public accessor for CameraController auto-follow

using Unity.Netcode;
using UnityEngine;
using Biros.Core;
using Biros.Config;
using log4net.Util;
using System;

namespace Biros.Gameplay
{
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    public class PenController : NetworkBehaviour
    {
        private const float OOB_Y = -1.5f;
        private const float BASE_STATIC_FRIC = 0.22f;
        private const float BASE_DYNAMIC_FRIC = 0.12f;
        private const float BASE_BOUNCINESS = 0.15f;

        [SerializeField] private PenConfigSO _config;

        [Tooltip("World-space length tip-to-tip. Match your pen prefab scale.")]
        [SerializeField] private float _penLength = 0.35f;

        private Rigidbody _rb;
        private Collider _col;

        /// <summary>Exposed so CameraController can read linearVelocity during auto-follow.</summary>
        public Rigidbody PhysicsBody => _rb;

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

        public int OwnerSlot => _ownerSlot.Value;
        public ulong OwnerClientId => _ownerClientId.Value;
        public bool IsOutOfBounds => _isOutOfBounds.Value;
        public PenConfigSO Config => _config;

        public bool IsSettled(float linearThreshold, float angularThreshold)
        {
            if (!IsServer || _isOutOfBounds.Value) return true;
            return _rb.linearVelocity.magnitude < linearThreshold &&
                   _rb.angularVelocity.magnitude < angularThreshold;
        }

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            PenRegistry.Instance?.Register(this);

            // Default: kinematic. Server enables physics when SimulatePhysics begins.
            _rb.isKinematic = true;

            if (IsServer)
            {
                if (_config != null) ApplyConfig();
                if (MatchStateManager.Instance != null)
                    MatchStateManager.Instance.OnPhaseChanged += HandlePhaseChanged;
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            PenRegistry.Instance?.Unregister(this);

            if (IsServer && MatchStateManager.Instance != null)
                MatchStateManager.Instance.OnPhaseChanged -= HandlePhaseChanged;
        }

        private void FixedUpdate()
        {
            if (!IsServer || _isOutOfBounds.Value) return;
            if (transform.position.y < OOB_Y) ServerHandleOutOfBounds();
        }

        // ── Server Init ────────────────────────────────────────────────────
        /// <summary>
        /// Call on server after spawning this pen. Binds it to a player slot.
        /// </summary>
        public void ServerInitialize(int ownerSlot, ulong ownerClientId, PenConfigSO config)
        {
            if (!IsServer) return;
            _ownerSlot.Value = ownerSlot;
            _ownerClientId.Value = ownerClientId;
            _config = config;
            ApplyConfig();
        }

        // ── Phase Handling ─────────────────────────────────────────────────
        private void HandlePhaseChanged(MatchPhase prev, MatchPhase next)
        {
            if (!IsServer || _isOutOfBounds.Value) return;

            // Dynamic only while physics is actively running
            bool simulate = next == MatchPhase.SimulatePhysics ||
                            next == MatchPhase.ResolveRound;
            _rb.isKinematic = !simulate;
        }

        // ── Orientation RPC (live preview while player aims) ───────────────
        /// <summary>
        /// Rate-limited from FlickInputHandler (~20 Hz).
        /// Server sets transform.rotation so NetworkTransform broadcasts to all clients,
        /// giving everyone a live visual of the pen being aimed.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void UpdateOrientationServerRpc(float hAngle, float vAngle,
                                               ServerRpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (MatchStateManager.Instance?.CurrentPhase != MatchPhase.ActiveTurnInput) return;
            if (_ownerClientId.Value != sender) return;
            if (MatchStateManager.Instance.ActivePlayerClientId != sender) return;

            vAngle = Mathf.Clamp(vAngle, 0f, 85f);
            // Euler(-vAngle, hAngle, 0):
            //   hAngle rotates pen on desk (Y axis)
            //   -vAngle tilts nose upward (negative X = nose rises in Unity's left-hand coords)
            transform.rotation = Quaternion.Euler(-vAngle, hAngle, 0f);
        }

        // ── Flick RPC ──────────────────────────────────────────────────────
        /// <summary>
        /// Sent once by FlickInputHandler on release.
        /// Applies the final orientation, switches to dynamic physics, then applies
        /// force at the pen's tip — creating linear velocity AND angular spin,
        /// exactly like a real flick where you hit the end of the pen.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SubmitFlickServerRpc(float hAngle, float vAngle, float force,
                                         ServerRpcParams rpcParams = default)
        {
            ulong sender = rpcParams.Receive.SenderClientId;
            if (MatchStateManager.Instance?.CurrentPhase != MatchPhase.ActiveTurnInput) return;
            if (_ownerClientId.Value != sender) return;
            if (MatchStateManager.Instance.ActivePlayerClientId != sender) return;

            // Apply final validated orientation
            vAngle = Mathf.Clamp(vAngle, 0f, 85f);
            transform.rotation = Quaternion.Euler(-vAngle, hAngle, 0f);

            // Enable physics before applying force
            _rb.isKinematic = false;

            Vector3 launchDir = transform.forward;

            // Force clamped to config cap — prevents exploit inputs
            float clampedForce = Mathf.Clamp(force, 0.05f, _config.maxFlickForce)
                                 * _config.flickForceMultiplier;

            // AddForceAtPosition at the pen tip:
            // Linear impulse  → pen moves in launch direction
            // Angular impulse → pen spins/tumbles realistically
            // 0.42 places the force point slightly behind the very tip (more natural arc)
            Vector3 flickPoint = transform.position + launchDir * (_penLength * 0.42f);
            _rb.AddForceAtPosition(launchDir * clampedForce, flickPoint, ForceMode.Impulse);

            // BittenChewed cap: extra random erratic torque
            if (_config.ErraticSpinMultiplier > 1f)
            {
                Vector3 chaos = UnityEngine.Random.insideUnitSphere
                                * clampedForce
                                * (_config.ErraticSpinMultiplier - 1f);
                _rb.AddTorque(chaos, ForceMode.Impulse);
            }

            MatchStateManager.Instance.ServerNotifyFlickSubmitted();
        }

        // ── Private ─────────────────────────────────────────────────────────
        private void ApplyConfig()
        {
            if (_config == null) return;

            _rb.mass = _config.ComputedMass;
            _rb.linearDamping = _config.drag;
            _rb.angularDamping = _config.angularDrag;
            _rb.centerOfMass = new Vector3(0f, 0f, _config.CenterOfMassShiftZ);

            // Unique material per pen instance — never mutate shared SO assets
            var mat = new PhysicsMaterial($"PenMat_{_config.configId}")
            {
                staticFriction = BASE_STATIC_FRIC * _config.ComputedFrictionScalar,
                dynamicFriction = BASE_DYNAMIC_FRIC * _config.ComputedFrictionScalar,
                bounciness = BASE_BOUNCINESS * _config.ComputedImpactDampening,
                frictionCombine = PhysicsMaterialCombine.Multiply,
                bounceCombine = PhysicsMaterialCombine.Average,
            };
            _col.material = mat;
        }

        private void ServerHandleOutOfBounds()
        {
            _isOutOfBounds.Value = true;
            _rb.isKinematic = true;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            MatchStateManager.Instance?.ServerOnPenExitedBounds(_ownerClientId.Value);
        }
    }
}