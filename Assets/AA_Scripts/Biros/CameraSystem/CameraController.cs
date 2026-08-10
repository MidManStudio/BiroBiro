// CameraController.cs
// Requires: com.unity.cinemachine 2.9.7 (your pinned version — this does NOT need
// an upgrade), com.unity.inputsystem.
// Attach to any persistent client-side GameObject.
// Assign a CinemachineFreeLook reference and the InputActionReferences in inspector.
//
// WHY CinemachineFreeLook AND NOT CinemachineVirtualCamera + CinemachineOrbitalTransposer:
// OrbitalTransposer only exposes m_XAxis (heading, AxisState, -180..180, wraps).
// It has no separate continuously-adjustable vertical-tilt or radius axis — on a
// plain Transposer, vertical offset and distance come from a single fixed
// m_FollowOffset Vector3, not something meant to be driven live from drag/scroll
// input. Driving m_XAxis for orbit AND vertical AND zoom all at once (which is
// what trying to force this onto OrbitalTransposer leads to) means all three
// controls fight over the same field.
// FreeLook is the idiomatic 2.x tool for "360 heading + vertical tilt + zoom":
// it blends 3 rigs (Top/Middle/Bottom) via m_YAxis (0..1), independent heading
// via m_XAxis (-180..180, wraps), and each rig has its own Height/Radius
// (CinemachineFreeLook.Orbit.m_Height / m_Radius).
//
// NOTE ON NUMBERS: _orbitSensV and the Y-axis clamp/overhead values below are
// starting points, not tested/tuned values — FreeLook's Y axis is a 0..1 rig
// blend, not degrees, so the old "10f, 85f" degree clamp doesn't carry over
// directly. Adjust by feel once you can actually drag the camera in Play mode.

using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using Cinemachine;
using Biros.Core;
using Biros.Gameplay;

namespace Biros.CameraSystem
{
    public class CameraController : MonoBehaviour
    {
        [Header("Feature Toggle")]
        [Tooltip("Off by default. When off, the camera still follows/looks at the active pen (Cinemachine's own damping), but none of the manual orbit/zoom/auto-follow/reset code below runs — camera sits at whatever the FreeLook rig's Inspector-configured axis values are.")]
        [SerializeField] private bool _enableOrbitControls = false;

        [Header("Cinemachine")]
        [SerializeField] private CinemachineFreeLook _freeLook;

        [Header("Input — Camera")]
        [Tooltip("Middle Mouse Button held = orbit modifier")]
        [SerializeField] private InputActionReference _orbitModifier; // MMB
        [Tooltip("Raw mouse delta — filtered by modifier in code")]
        [SerializeField] private InputActionReference _orbitDelta;    // Mouse Delta
        [SerializeField] private InputActionReference _zoomAction;    // Scroll Y
        [SerializeField] private InputActionReference _resetAction;   // F key

        [Header("Orbit Sensitivity")]
        [SerializeField] private float _orbitSensH = 0.30f;   // degrees per pixel of delta.x on m_XAxis
        [Tooltip("m_YAxis is 0..1, not degrees. Placeholder — tune by feel.")]
        [SerializeField] private float _orbitSensV = 0.004f;

        [Header("Vertical Range — m_YAxis (0 = Bottom rig, 1 = Top rig)")]
        [SerializeField] private float _yAxisMin = 0.10f;
        [SerializeField] private float _yAxisMax = 0.95f;

        [Header("Zoom (applied uniformly to all 3 rig radii)")]
        [Tooltip("Previous defaults (1.5–8.0) were too tight to see most of a 2.4x1.6 desk. Widened — tune further by feel.")]
        [SerializeField] private float _zoomMin = 2.5f;
        [SerializeField] private float _zoomMax = 14.0f;
        [SerializeField] private float _zoomSens = 0.5f;

        [Header("Auto-Follow (SimulatePhysics)")]
        [SerializeField] private float _autoFollowSpeed = 2.5f;

        [Header("Overhead Reset")]
        [SerializeField] private float _resetSpeed = 2.5f;
        [SerializeField] private float _overheadH = 0f;
        [Tooltip("0..1 target for m_YAxis on reset. Near 1 = mostly Top rig (looking down).")]
        [SerializeField] private float _overheadYAxis = 0.85f;
        [SerializeField] private float _overheadRadial = 9.0f;

        // ── Runtime ────────────────────────────────────────────────────────
        private PenController _trackedPen;
        private bool _autoFollowing;
        private Coroutine _resetRoutine;
        private float _currentRadius;

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake()
        {
            if (_freeLook == null)
            {
                Debug.LogError("[CameraController] _freeLook not assigned.");
                return;
            }
            // Seed the shared zoom radius from whatever the Middle rig (index 1) is set to.
            _currentRadius = Mathf.Clamp(_freeLook.m_Orbits[1].m_Radius, _zoomMin, _zoomMax);
        }

        private void OnEnable()
        {
            Enable(_orbitModifier); Enable(_orbitDelta);
            Enable(_zoomAction); Enable(_resetAction);

            // Named method, not a lambda — a lambda here would create a new delegate
            // instance each time, so the OnDisable "-=" below would silently fail to
            // unsubscribe it (this was a real bug in the previous version).
            if (_resetAction?.action != null)
                _resetAction.action.performed += OnResetPerformed;
        }

        private void OnDisable()
        {
            Disable(_orbitModifier); Disable(_orbitDelta);
            Disable(_zoomAction); Disable(_resetAction);

            if (_resetAction?.action != null)
                _resetAction.action.performed -= OnResetPerformed;
        }

        private void OnResetPerformed(InputAction.CallbackContext _)
        {
            if (_enableOrbitControls) BeginOverheadReset();
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
            if (_freeLook == null || !_enableOrbitControls) return;

            if (_autoFollowing)
                TickAutoFollow();
            else
                TickManualOrbit();

            TickZoom();
            ApplyRadiusToOrbits();
        }

        // ── Orbit ─────────────────────────────────────────────────────────
        private void TickManualOrbit()
        {
            if (_orbitModifier?.action.IsPressed() != true) return;
            if (_orbitDelta?.action == null) return;

            Vector2 delta = _orbitDelta.action.ReadValue<Vector2>();
            if (delta.sqrMagnitude < 0.01f) return;

            _freeLook.m_XAxis.Value += delta.x * _orbitSensH;
            _freeLook.m_YAxis.Value += delta.y * _orbitSensV;
            _freeLook.m_YAxis.Value = Mathf.Clamp(_freeLook.m_YAxis.Value, _yAxisMin, _yAxisMax);
        }

        // ── Auto-Follow during physics simulation ──────────────────────────
        // Positions the camera behind the pen's travel direction so the player
        // can see where it's going. Lerps smoothly so it doesn't snap.
        private void TickAutoFollow()
        {
            if (_trackedPen?.PhysicsBody == null) return;

            Vector3 vel = _trackedPen.PhysicsBody.velocity;
            if (vel.sqrMagnitude < 0.1f) return;

            // Camera goes behind pen: angle = direction of travel + 180°
            float targetAngle = Mathf.Atan2(vel.x, vel.z) * Mathf.Rad2Deg + 180f;
            _freeLook.m_XAxis.Value = Mathf.LerpAngle(
                _freeLook.m_XAxis.Value, targetAngle,
                Time.deltaTime * _autoFollowSpeed);
        }

        // ── Zoom ───────────────────────────────────────────────────────────
        // FreeLook has no single "radius" axis — each rig has its own m_Radius.
        // Simplest correct approach: track one shared radius value and apply it
        // to all 3 rigs identically each frame. Heights stay whatever's set per
        // rig in the Inspector, which is what actually creates the vertical spread.
        private void TickZoom()
        {
            if (_zoomAction?.action == null) return;
            float scroll = _zoomAction.action.ReadValue<float>();
            if (Mathf.Abs(scroll) < 0.001f) return;

            _currentRadius -= scroll * _zoomSens;
            _currentRadius = Mathf.Clamp(_currentRadius, _zoomMin, _zoomMax);
        }

        private void ApplyRadiusToOrbits()
        {
            for (int i = 0; i < _freeLook.m_Orbits.Length; i++)
                _freeLook.m_Orbits[i].m_Radius = _currentRadius;
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
                _freeLook.m_XAxis.Value = Mathf.LerpAngle(_freeLook.m_XAxis.Value, _overheadH, t);
                _freeLook.m_YAxis.Value = Mathf.Lerp(_freeLook.m_YAxis.Value, _overheadYAxis, t);
                _currentRadius          = Mathf.Lerp(_currentRadius, _overheadRadial, t);

                bool done =
                    Mathf.Abs(Mathf.DeltaAngle(_freeLook.m_XAxis.Value, _overheadH)) < 1f &&
                    Mathf.Abs(_freeLook.m_YAxis.Value - _overheadYAxis) < 0.01f &&
                    Mathf.Abs(_currentRadius - _overheadRadial) < 0.05f;

                if (done) yield break;
                yield return null;
            }
        }

        // ── Phase / Player Events ──────────────────────────────────────────
        private void OnPhaseChanged(MatchPhase prev, MatchPhase next)
        {
            _autoFollowing = next == MatchPhase.SimulatePhysics;

            if (_enableOrbitControls &&
                (next == MatchPhase.ResolveRound || next == MatchPhase.SwitchPlayer))
                BeginOverheadReset();
        }

        private void OnActivePlayerChanged(int newSlot)
        {
            if (PenRegistry.Instance == null) return;
            _trackedPen = PenRegistry.Instance.GetPenForSlot(newSlot);

            if (_trackedPen == null || _freeLook == null) return;
            _freeLook.Follow = _trackedPen.transform;
            _freeLook.LookAt = _trackedPen.transform;
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
