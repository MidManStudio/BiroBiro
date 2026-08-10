// FlickInputHandler.cs — v5 (simplified: drag-to-flick only)
//
// STRIPPED DOWN ON PURPOSE. Rotate move and Spin move still exist below but are
// gated off by default via _enableRotateMove / _enableSpinMove — flip either to
// true to bring them back once the core flick loop is confirmed solid. Camera
// orbit controls have their own separate toggle over in CameraController.
//
// Flick is now click-and-drag, Angry-Birds style:
//   RMB down  → anchor point recorded on the desk plane under the cursor
//   drag      → trajectory line shows where the pen will go — in the OPPOSITE
//               direction of the drag (pull back-left, pen flies forward-right),
//               distance dragged sets force
//   RMB up    → fires in that direction with that force
//
// TWO BUGS FIXED HERE THAT TOGETHER FULLY EXPLAIN "physics doesn't work,
// nothing happens, turn just passes, and the trajectory line never showed":
//
//   1. _localPen used to come from PenRegistry.GetLocalPlayerPen(), which
//      picks a pen by OwnerClientId alone. TestMatchBootstrap assigns BOTH
//      pens the same clientId (the host) for solo testing — so that call
//      always returned the SAME pen (whichever registered first), regardless
//      of whose turn it actually was. Every other turn, _localPen pointed at
//      a pen that wasn't even the active one.
//   2. OnFlickStarted required the click to land within 90px of _localPen's
//      screen position, or directly hit its collider, before it would even
//      enter Phase.ChargingFlick. Combined with bug #1, or just from the pen
//      being small on screen after the camera distance got widened, this
//      gate silently failing meant Phase never left Idle — so the Update()
//      switch's ChargingFlick case (which is what calls the trajectory-
//      preview code) never ran at all. Explains both complaints as the same
//      root cause, not two separate bugs to hunt down.
//
// Fix for #1: resolve _localPen from the CURRENTLY ACTIVE SLOT
// (MatchStateManager.ActivePlayerSlot → PenRegistry.GetPenForSlot), only
// treating it as "mine" if that pen's owner matches my client id. Correct
// even with two same-owner pens, and also just the more correct definition
// in general — you should only ever be acting on the active pen.
// Fix for #2: removed the near-pen requirement entirely. Click anywhere,
// drag, release — no need to precisely hit a small 3D model.

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Biros.Core;
using Biros.Gameplay;

namespace Biros.Input
{
    public class FlickInputHandler : MonoBehaviour
    {
        [Header("Feature Toggles — off by default, flip on to bring a move back")]
        [SerializeField] private bool _enableRotateMove = false;
        [SerializeField] private bool _enableSpinMove   = false;

        [Header("Trajectory Preview")]
        [Tooltip("LineRenderer that draws the predicted flight path while dragging. Needs a material assigned (e.g. Sprites-Default) or it renders invisible/pink.")]
        [SerializeField] private LineRenderer _trajectoryLine;
        [SerializeField] private int   _trajectorySamples = 30;
        [SerializeField] private float _trajectoryTimeStep = 0.04f;
        [SerializeField] private float _trajectoryWidth = 0.015f;
        [Tooltip("Y position the desk surface sits at — drag is measured on this plane, and the preview stops once it would hit it.")]
        [SerializeField] private float _deskSurfaceY = 0.05f;

        [Header("Input Actions")]
        [SerializeField] private InputActionReference _selectAction;       // LMB — rotate move (off by default)
        [SerializeField] private InputActionReference _mouseDelta;         // Mouse Delta
        [SerializeField] private InputActionReference _mousePosition;      // Mouse Position
        [SerializeField] private InputActionReference _flickChargeAction;  // RMB — flick move (drag)
        [SerializeField] private InputActionReference _spinMoveAction;     // Space — spin move (off by default)

        [Header("Rotation (only used if _enableRotateMove)")]
        [SerializeField] private float _hSens = 0.45f;
        [SerializeField] private float _vSens = 0.30f;
        [SerializeField] private float _minTilt = 0f;
        [SerializeField] private float _maxTilt = 85f;
        [SerializeField] private float _minRotationToCommit = 5f;

        [Header("Drag-to-Flick (Angry Birds style)")]
        [Tooltip("Force applied even for a near-zero drag, as a fraction of maxFlickForce.")]
        [SerializeField] private float _minForceFrac = 0.15f;
        [Tooltip("World units of drag distance -> extra force above the minimum. Placeholder — tune by feel.")]
        [SerializeField] private float _dragForceScale = 8f;

        [Header("Spin Move (only used if _enableSpinMove)")]
        [SerializeField] private float _maxChargeSec = 1.5f;
        [SerializeField] private float _spinForceMultiplier = 1.4f;

        [Header("Network Rate")]
        [SerializeField] private float _orientHz = 20f;

        // ── Events ─────────────────────────────────────────────────────────
        public event Action<float> OnChargeChanged;
        public event Action        OnTurnActionUsed;
        public event Action<bool>  OnInputActiveChanged;

        // ── State Machine ──────────────────────────────────────────────────
        private enum Phase { Idle, RotatingPen, ChargingFlick, ChargingSpinMove }
        private Phase _phase = Phase.Idle;

        private PenController _localPen;
        private bool          _inputActive;

        // Rotation state (only relevant if _enableRotateMove)
        private float _penH;
        private float _penV;
        private float _rotationAccumDeg;
        private float _lastSendT;

        // Spin charge state (only relevant if _enableSpinMove)
        private float _charge;

        // Drag-flick state
        private Vector3 _dragStartWorld;
        private Vector3 _currentLaunchDir;
        private float   _currentForce;

        private Camera _cam;

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake()
        {
            _cam = Camera.main;
            if (_trajectoryLine != null)
            {
                _trajectoryLine.startWidth = _trajectoryWidth;
                _trajectoryLine.endWidth   = _trajectoryWidth;
                _trajectoryLine.enabled    = false;
                _trajectoryLine.useWorldSpace = true;
            }
        }

        private void OnEnable()
        {
            Enable(_selectAction); Enable(_mouseDelta);
            Enable(_mousePosition); Enable(_flickChargeAction);
            Enable(_spinMoveAction);

            if (_selectAction?.action != null)
            {
                _selectAction.action.started  += OnSelectStarted;
                _selectAction.action.canceled += OnSelectCanceled;
            }
            if (_flickChargeAction?.action != null)
            {
                _flickChargeAction.action.started  += OnFlickStarted;
                _flickChargeAction.action.canceled += OnFlickCanceled;
            }
            if (_spinMoveAction?.action != null)
            {
                _spinMoveAction.action.started  += OnSpinStarted;
                _spinMoveAction.action.canceled += OnSpinCanceled;
            }
        }

        private void OnDisable()
        {
            Disable(_selectAction); Disable(_mouseDelta);
            Disable(_mousePosition); Disable(_flickChargeAction);
            Disable(_spinMoveAction);

            if (_selectAction?.action != null)
            {
                _selectAction.action.started  -= OnSelectStarted;
                _selectAction.action.canceled -= OnSelectCanceled;
            }
            if (_flickChargeAction?.action != null)
            {
                _flickChargeAction.action.started  -= OnFlickStarted;
                _flickChargeAction.action.canceled -= OnFlickCanceled;
            }
            if (_spinMoveAction?.action != null)
            {
                _spinMoveAction.action.started  -= OnSpinStarted;
                _spinMoveAction.action.canceled -= OnSpinCanceled;
            }
        }

        private void Start()
        {
            if (MatchStateManager.Instance == null) return;
            MatchStateManager.Instance.OnPhaseChanged        += OnMatchPhaseChanged;
            MatchStateManager.Instance.OnActivePlayerChanged += OnActivePlayerChanged;
        }

        private void OnDestroy()
        {
            if (MatchStateManager.Instance == null) return;
            MatchStateManager.Instance.OnPhaseChanged        -= OnMatchPhaseChanged;
            MatchStateManager.Instance.OnActivePlayerChanged -= OnActivePlayerChanged;
        }

        private void Update()
        {
            if (!_inputActive || _localPen == null) return;

            switch (_phase)
            {
                case Phase.RotatingPen:      if (_enableRotateMove) TickRotate();  break;
                case Phase.ChargingFlick:    TickDragFlick(); break;
                case Phase.ChargingSpinMove: if (_enableSpinMove)   TickCharge();  break;
            }
        }

        // ── Pen resolution — THE bug fix ─────────────────────────────────────
        private PenController ResolveMyActivePen()
        {
            if (MatchStateManager.Instance == null || PenRegistry.Instance == null) return null;
            if (NetworkManager.Singleton == null) return null;

            int activeSlot = MatchStateManager.Instance.ActivePlayerSlot;
            PenController activePen = PenRegistry.Instance.GetPenForSlot(activeSlot);
            if (activePen == null) return null;

            return activePen.OwnerClientId == NetworkManager.Singleton.LocalClientId
                ? activePen
                : null;
        }

        // ── Move A: Rotate (off by default) ─────────────────────────────────
        private void OnSelectStarted(InputAction.CallbackContext _)
        {
            if (!_enableRotateMove) return;
            if (!_inputActive || _localPen == null) return;
            if (_phase != Phase.Idle) return;

            _rotationAccumDeg = 0f;
            _phase            = Phase.RotatingPen;
        }

        private void OnSelectCanceled(InputAction.CallbackContext _)
        {
            if (_phase != Phase.RotatingPen) return;

            if (_rotationAccumDeg >= _minRotationToCommit)
                SubmitRotateAsMove();
            else
                _phase = Phase.Idle;

            _rotationAccumDeg = 0f;
        }

        private void TickRotate()
        {
            if (_mouseDelta?.action == null) return;
            Vector2 d = _mouseDelta.action.ReadValue<Vector2>();

            float dH = d.x * _hSens;
            float dV = d.y * _vSens;

            _penH += dH;
            _penV -= dV;
            _penV  = Mathf.Clamp(_penV, _minTilt, _maxTilt);

            _rotationAccumDeg += Mathf.Abs(dH) + Mathf.Abs(dV);

            if (Time.time - _lastSendT >= 1f / _orientHz)
            {
                _lastSendT = Time.time;
                _localPen.UpdateOrientationServerRpc(_penH, _penV);
            }
        }

        private void SubmitRotateAsMove()
        {
            _localPen.SubmitRotateServerRpc(_penH, _penV);
            FinishTurn();
        }

        // ── Move B: Flick — click and drag, Angry Birds style ───────────────
        private void OnFlickStarted(InputAction.CallbackContext _)
        {
            if (!_inputActive || _localPen == null) return;
            if (_phase != Phase.Idle) return;

            if (!TryGetDeskPlanePoint(ReadMousePos(), out _dragStartWorld)) return;

            _currentLaunchDir = _localPen.transform.forward;
            _currentForce     = _localPen.Config != null
                ? _localPen.Config.maxFlickForce * _minForceFrac
                : 0f;

            _phase = Phase.ChargingFlick;
            OnChargeChanged?.Invoke(0f);
        }

        private void TickDragFlick()
        {
            if (_localPen?.Config == null) return;
            if (!TryGetDeskPlanePoint(ReadMousePos(), out Vector3 currentWorld)) return;

            Vector3 dragVec = currentWorld - _dragStartWorld;
            dragVec.y = 0f;

            float dragDist = dragVec.magnitude;
            Vector3 launchDir = dragDist > 0.001f ? -dragVec.normalized : _localPen.transform.forward;

            float minForce = _localPen.Config.maxFlickForce * _minForceFrac;
            float force     = Mathf.Clamp(minForce + dragDist * _dragForceScale,
                                          minForce, _localPen.Config.maxFlickForce);

            _currentLaunchDir = launchDir;
            _currentForce     = force;

            OnChargeChanged?.Invoke(Mathf.InverseLerp(minForce, _localPen.Config.maxFlickForce, force));
            UpdateTrajectoryPreview(launchDir, force);
        }

        private void OnFlickCanceled(InputAction.CallbackContext _)
        {
            if (_phase == Phase.ChargingFlick) SubmitFlick();
        }

        private void SubmitFlick()
        {
            if (_localPen?.Config == null) { FinishTurn(); return; }

            float hAngle = Mathf.Atan2(_currentLaunchDir.x, _currentLaunchDir.z) * Mathf.Rad2Deg;

            _localPen.SubmitFlickServerRpc(hAngle, 0f, _currentForce, 0f);
            FinishTurn();
        }

        // ── Trajectory Preview ──────────────────────────────────────────────
        private void UpdateTrajectoryPreview(Vector3 launchDir, float force)
        {
            if (_trajectoryLine == null || _localPen?.Config == null || _localPen.PhysicsBody == null) return;

            float mass = _localPen.PhysicsBody.mass;
            float drag = _localPen.PhysicsBody.drag;

            float appliedForce = Mathf.Clamp(force, 0.05f, _localPen.Config.maxFlickForce)
                                  * _localPen.Config.flickForceMultiplier;

            Vector3 pos = _localPen.transform.position;
            Vector3 vel = launchDir.normalized * (appliedForce / Mathf.Max(mass, 0.0001f));

            var points = new List<Vector3>(_trajectorySamples) { pos };

            for (int i = 0; i < _trajectorySamples; i++)
            {
                vel += Physics.gravity * _trajectoryTimeStep;
                vel *= Mathf.Clamp01(1f - drag * _trajectoryTimeStep);
                pos += vel * _trajectoryTimeStep;
                points.Add(pos);

                if (pos.y <= _deskSurfaceY) break;
            }

            _trajectoryLine.positionCount = points.Count;
            _trajectoryLine.SetPositions(points.ToArray());
            _trajectoryLine.enabled = true;
        }

        private void ClearTrajectoryPreview()
        {
            if (_trajectoryLine == null) return;
            _trajectoryLine.enabled = false;
            _trajectoryLine.positionCount = 0;
        }

        // ── Move C: Spin (off by default) ───────────────────────────────────
        private void OnSpinStarted(InputAction.CallbackContext _)
        {
            if (!_enableSpinMove) return;
            if (!_inputActive || _localPen == null) return;
            if (_phase != Phase.Idle) return;

            _charge = 0f;
            _phase  = Phase.ChargingSpinMove;
            OnChargeChanged?.Invoke(0f);
        }

        private void OnSpinCanceled(InputAction.CallbackContext _)
        {
            if (_phase == Phase.ChargingSpinMove) SubmitSpinMove();
        }

        private void TickCharge()
        {
            _charge = Mathf.Min(_charge + Time.deltaTime, _maxChargeSec);
            OnChargeChanged?.Invoke(Mathf.Clamp01(_charge / _maxChargeSec));
        }

        private void SubmitSpinMove()
        {
            if (_localPen?.Config == null) { FinishTurn(); return; }

            float frac     = Mathf.Clamp01(_charge / _maxChargeSec);
            float minForce = _localPen.Config.maxFlickForce * _minForceFrac;
            float maxForce = _localPen.Config.maxFlickForce * _spinForceMultiplier;
            float torque   = Mathf.Lerp(minForce, maxForce, frac);

            _localPen.SpinMoveServerRpc(torque, clockwise: true);
            FinishTurn();
        }

        // ── Shared: end turn ───────────────────────────────────────────────
        private void FinishTurn()
        {
            _phase       = Phase.Idle;
            _charge      = 0f;
            _inputActive = false;
            ClearTrajectoryPreview();
            OnChargeChanged?.Invoke(0f);
            OnTurnActionUsed?.Invoke();
            OnInputActiveChanged?.Invoke(false);
        }

        // ── Phase / Player Events ──────────────────────────────────────────
        private void OnMatchPhaseChanged(MatchPhase prev, MatchPhase next)
        {
            if (next == MatchPhase.ActiveTurnInput)
            {
                _localPen    = ResolveMyActivePen();
                _inputActive = MatchStateManager.Instance.IsLocalPlayerTurn;
                _phase       = Phase.Idle;
                _charge      = 0f;
                _rotationAccumDeg = 0f;

                if (_inputActive && _localPen != null)
                {
                    Vector3 e = _localPen.transform.eulerAngles;
                    _penH = e.y;
                    _penV = Mathf.Clamp(-e.x, _minTilt, _maxTilt);
                }

                OnInputActiveChanged?.Invoke(_inputActive);
            }
            else
            {
                _inputActive = false;
                _phase       = Phase.Idle;
                _rotationAccumDeg = 0f;
                ClearTrajectoryPreview();
                OnChargeChanged?.Invoke(0f);
                OnInputActiveChanged?.Invoke(false);
            }
        }

        private void OnActivePlayerChanged(int _)
        {
            if (MatchStateManager.Instance?.CurrentPhase == MatchPhase.ActiveTurnInput)
                OnMatchPhaseChanged(MatchPhase.ActiveTurnInput, MatchPhase.ActiveTurnInput);
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private bool TryGetDeskPlanePoint(Vector2 screenPos, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (_cam == null) return false;

            var plane = new Plane(Vector3.up, new Vector3(0f, _deskSurfaceY, 0f));
            Ray ray = _cam.ScreenPointToRay(screenPos);

            if (plane.Raycast(ray, out float distance))
            {
                worldPoint = ray.GetPoint(distance);
                return true;
            }
            return false;
        }

        private Vector2 ReadMousePos() =>
            _mousePosition?.action.ReadValue<Vector2>() ?? Vector2.zero;

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
