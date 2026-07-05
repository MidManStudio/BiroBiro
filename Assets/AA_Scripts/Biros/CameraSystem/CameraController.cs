// CameraController.cs
// Requires: com.unity.cinemachine 3.x, com.unity.inputsystem
// Attach to any persistent client-side GameObject.
// Assign CinemachineCamera reference and InputActionReferences in inspector.

using System.Collections;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using Biros.Core;
using Biros.Gameplay;

namespace Biros.CameraSystem
{
    public class CameraController : MonoBehaviour
    {
        [Header("Cinemachine")]
        [SerializeField] private CinemachineCamera _vcam;

        [Header("Input — Camera")]
        [Tooltip("Middle Mouse Button held = orbit modifier")]
        [SerializeField] private InputActionReference _orbitModifier; // MMB
        [Tooltip("Raw mouse delta — filtered by modifier in code")]
        [SerializeField] private InputActionReference _orbitDelta;    // Mouse Delta
        [SerializeField] private InputActionReference _zoomAction;    // Scroll Y
        [SerializeField] private InputActionReference _resetAction;   // F key

        [Header("Orbit Sensitivity")]
        [SerializeField] private float _orbitSensH = 0.30f;
        [SerializeField] private float _orbitSensV = 0.20f;

        [Header("Zoom")]
        [SerializeField] private float _zoomMin = 1.5f;
        [SerializeField] private float _zoomMax = 8.0f;
        [SerializeField] private float _zoomSens = 0.35f;

        [Header("Auto-Follow (SimulatePhysics)")]
        [SerializeField] private float _autoFollowSpeed = 2.5f;

        [Header("Overhead Reset")]
        [SerializeField] private float _resetSpeed = 2.5f;
        [SerializeField] private float _overheadH = 0f;
        [SerializeField] private float _overheadV = 65f;   // degrees elevation from horizon
        [SerializeField] private float _overheadRadial = 5.5f;

        // ── Runtime ────────────────────────────────────────────────────────
        private CinemachineOrbitalFollow _orbital;
        private PenController _trackedPen;
        private bool _autoFollowing;
        private Coroutine _resetRoutine;

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake()
        {
            _orbital = _vcam != null ? _vcam.GetComponent<CinemachineOrbitalFollow>() : null;
            if (_orbital == null)
                Debug.LogError("[CameraController] CinemachineOrbitalFollow not found on _vcam.");
        }

        private void OnEnable()
        {
            Enable(_orbitModifier); Enable(_orbitDelta);
            Enable(_zoomAction); Enable(_resetAction);

            if (_resetAction?.action != null)
                _resetAction.action.performed += _ => BeginOverheadReset();
        }

        private void OnDisable()
        {
            Disable(_orbitModifier); Disable(_orbitDelta);
            Disable(_zoomAction); Disable(_resetAction);

            if (_resetAction?.action != null)
                _resetAction.action.performed -= _ => BeginOverheadReset();
        }

        private void Start()
        {
            if (MatchStateManager.Instance == null) return;
            MatchStateManager.Instance.OnPhaseChanged += OnPhaseChanged;
            MatchStateManager.Instance.OnActivePlayerChanged += OnActivePlayerChanged;
        }

        private void OnDestroy()
        {
            if (MatchStateManager.Instance == null) return;
            MatchStateManager.Instance.OnPhaseChanged -= OnPhaseChanged;
            MatchStateManager.Instance.OnActivePlayerChanged -= OnActivePlayerChanged;
        }

        private void LateUpdate()
        {
            if (_orbital == null) return;

            if (_autoFollowing)
                TickAutoFollow();
            else
                TickManualOrbit();

            TickZoom();
        }

        // ── Orbit ─────────────────────────────────────────────────────────
        private void TickManualOrbit()
        {
            if (_orbitModifier?.action.IsPressed() != true) return;
            if (_orbitDelta?.action == null) return;

            Vector2 delta = _orbitDelta.action.ReadValue<Vector2>();
            if (delta.sqrMagnitude < 0.01f) return;

            _orbital.HorizontalAxis.Value += delta.x * _orbitSensH;
            _orbital.VerticalAxis.Value -= delta.y * _orbitSensV;
            _orbital.VerticalAxis.Value = Mathf.Clamp(_orbital.VerticalAxis.Value, 10f, 85f);
        }

        // ── Auto-Follow during physics simulation ──────────────────────────
        // Positions the camera behind the pen's travel direction so the player
        // can see where it's going. Lerps smoothly so it doesn't snap.
        private void TickAutoFollow()
        {
            if (_trackedPen?.PhysicsBody == null) return;

            Vector3 vel = _trackedPen.PhysicsBody.linearVelocity;
            if (vel.sqrMagnitude < 0.1f) return;

            // Camera goes behind pen: angle = direction of travel + 180°
            float targetAngle = Mathf.Atan2(vel.x, vel.z) * Mathf.Rad2Deg + 180f;
            _orbital.HorizontalAxis.Value = Mathf.LerpAngle(
                _orbital.HorizontalAxis.Value, targetAngle,
                Time.deltaTime * _autoFollowSpeed);
        }

        // ── Zoom ───────────────────────────────────────────────────────────
        private void TickZoom()
        {
            if (_zoomAction?.action == null) return;
            float scroll = _zoomAction.action.ReadValue<float>();
            if (Mathf.Abs(scroll) < 0.001f) return;

            _orbital.RadialAxis.Value -= scroll * _zoomSens;
            _orbital.RadialAxis.Value = Mathf.Clamp(_orbital.RadialAxis.Value, _zoomMin, _zoomMax);
        }

        // ── Overhead Reset ─────────────────────────────────────────────────
        private void BeginOverheadReset()
        {
            if (_resetRoutine != null) StopCoroutine(_resetRoutine);
            _resetRoutine = StartCoroutine(LerpToOverhead());
        }

        private IEnumerator LerpToOverhead()
        {
            while (true)
            {
                float t = Time.deltaTime * _resetSpeed;
                _orbital.HorizontalAxis.Value = Mathf.LerpAngle(
                    _orbital.HorizontalAxis.Value, _overheadH, t);
                _orbital.VerticalAxis.Value = Mathf.Lerp(
                    _orbital.VerticalAxis.Value, _overheadV, t);
                _orbital.RadialAxis.Value = Mathf.Lerp(
                    _orbital.RadialAxis.Value, _overheadRadial, t);

                bool done =
                    Mathf.Abs(Mathf.DeltaAngle(_orbital.HorizontalAxis.Value, _overheadH)) < 1f &&
                    Mathf.Abs(_orbital.VerticalAxis.Value - _overheadV) < 0.5f &&
                    Mathf.Abs(_orbital.RadialAxis.Value - _overheadRadial) < 0.05f;

                if (done) yield break;
                yield return null;
            }
        }

        // ── Phase / Player Events ──────────────────────────────────────────
        private void OnPhaseChanged(MatchPhase prev, MatchPhase next)
        {
            _autoFollowing = next == MatchPhase.SimulatePhysics;

            if (next == MatchPhase.ResolveRound || next == MatchPhase.SwitchPlayer)
                BeginOverheadReset();
        }

        private void OnActivePlayerChanged(int newSlot)
        {
            if (PenRegistry.Instance == null) return;
            _trackedPen = PenRegistry.Instance.GetPenForSlot(newSlot);

            if (_trackedPen == null || _vcam == null) return;
            _vcam.Follow = _trackedPen.transform;
            _vcam.LookAt = _trackedPen.transform;
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static void Enable(InputActionReference r)
        {
            if (r?.action != null && !r.action.enabled) r.action.Enable();
        }

        private static void Disable(InputActionReference r)
        {
            if (r?.action != null && r.action.enabled) r.action.Disable();
        }
    }
}
