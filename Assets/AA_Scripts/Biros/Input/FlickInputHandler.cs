// FlickInputHandler.cs
// New Input System. Full rewrite from previous slingshot version.
//
// Biro mechanic (two-hand simulation):
//   Left hand  = Left Mouse Button held near pen → drag to rotate/tilt pen
//   Right hand = Right Mouse Button held → charge power → release to flick
//
// Pen angles:
//   H (horizontal): spins pen on the desk XZ plane — sets direction
//   V (vertical tilt): 0° = flat on desk, 85° = near-vertical launch
//
// Assign InputActionReferences in inspector after creating BiroInputActions asset.

using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Biros.Core;
using Biros.Gameplay;

namespace Biros.Input
{
    public class FlickInputHandler : MonoBehaviour
    {
        [Header("Input Actions")]
        [SerializeField] private InputActionReference _selectAction;       // LMB
        [SerializeField] private InputActionReference _mouseDelta;         // Mouse Delta
        [SerializeField] private InputActionReference _mousePosition;      // Mouse Position
        [SerializeField] private InputActionReference _flickChargeAction;  // RMB

        [Header("Rotation Sensitivity")]
        [SerializeField] private float _hSens = 0.45f;   // degrees per screen pixel
        [SerializeField] private float _vSens = 0.30f;

        [Header("Tilt Clamp")]
        [SerializeField] private float _minTilt = 0f;   // flat on desk
        [SerializeField] private float _maxTilt = 85f;   // near-vertical

        [Header("Flick Charge")]
        [SerializeField] private float _maxChargeSec = 1.5f;
        [SerializeField] private float _minForceFrac = 0.15f;  // fraction at zero charge

        [Header("Selection")]
        [SerializeField] private float _clickRadiusPx = 90f;

        [Header("Network Rate")]
        [SerializeField] private float _orientHz = 20f;   // orientation updates per second

        // ── Events — wire these to your UI ─────────────────────────────────
        /// <summary>0 = not charging, 1 = fully charged.</summary>
        public event Action<float> OnChargeChanged;
        public event Action OnFlickSubmitted;
        /// <summary>True when it's this client's turn and input is live.</summary>
        public event Action<bool> OnInputActiveChanged;

        // ── Internal State Machine ─────────────────────────────────────────
        private enum Phase { Idle, RotatingPen, ChargingFlick }
        private Phase _phase = Phase.Idle;

        private PenController _localPen;
        private bool _inputActive;

        // Pen aim state
        private float _penH;       // horizontal angle (degrees)
        private float _penV;       // tilt angle (degrees)

        // Charge
        private float _charge;

        // Rate-limit orientation RPCs
        private float _lastSendT;

        private Camera _cam;

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake() => _cam = Camera.main;

        private void OnEnable()
        {
            Enable(_selectAction); Enable(_mouseDelta);
            Enable(_mousePosition); Enable(_flickChargeAction);

            BindSelect();
            BindFlick();
        }

        private void OnDisable()
        {
            Disable(_selectAction); Disable(_mouseDelta);
            Disable(_mousePosition); Disable(_flickChargeAction);

            UnbindSelect();
            UnbindFlick();
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

        private void Update()
        {
            if (!_inputActive || _localPen == null) return;

            switch (_phase)
            {
                case Phase.RotatingPen: TickRotate(); break;
                case Phase.ChargingFlick: TickCharge(); break;
            }
        }

        // ── LMB – select and rotate (left hand) ────────────────────────────
        private void OnSelectStarted(InputAction.CallbackContext _)
        {
            if (!_inputActive || _localPen == null || _phase != Phase.Idle) return;
            Vector2 pos = _mousePosition?.action.ReadValue<Vector2>() ?? Vector2.zero;
            if (!IsNearPen(pos)) return;
            _phase = Phase.RotatingPen;
        }

        private void OnSelectCanceled(InputAction.CallbackContext _)
        {
            if (_phase == Phase.RotatingPen) _phase = Phase.Idle;
        }

        // ── RMB – charge and release (right hand) ──────────────────────────
        private void OnFlickStarted(InputAction.CallbackContext _)
        {
            if (!_inputActive || _localPen == null) return;
            if (_phase == Phase.Idle || _phase == Phase.RotatingPen)
            {
                _charge = 0f;
                _phase = Phase.ChargingFlick;
                OnChargeChanged?.Invoke(0f);
            }
        }

        private void OnFlickCanceled(InputAction.CallbackContext _)
        {
            if (_phase == Phase.ChargingFlick) SubmitFlick();
        }

        // ── Tick: Rotate ───────────────────────────────────────────────────
        private void TickRotate()
        {
            if (_mouseDelta?.action == null) return;
            Vector2 d = _mouseDelta.action.ReadValue<Vector2>();

            // Horizontal drag: spin pen on desk
            // Vertical drag  : tilt pen nose up or down
            _penH += d.x * _hSens;
            _penV -= d.y * _vSens;      // drag up → tilt nose up
            _penV = Mathf.Clamp(_penV, _minTilt, _maxTilt);

            // Rate-limited orientation update so the server shows live rotation preview
            if (Time.time - _lastSendT >= 1f / _orientHz)
            {
                _lastSendT = Time.time;
                _localPen.UpdateOrientationServerRpc(_penH, _penV);
            }
        }

        // ── Tick: Charge ───────────────────────────────────────────────────
        private void TickCharge()
        {
            _charge = Mathf.Min(_charge + Time.deltaTime, _maxChargeSec);
            OnChargeChanged?.Invoke(_charge / _maxChargeSec);
        }

        // ── Submit Flick ───────────────────────────────────────────────────
        private void SubmitFlick()
        {
            if (_localPen?.Config == null) return;

            float frac = Mathf.Clamp01(_charge / _maxChargeSec);
            float minForce = _localPen.Config.maxFlickForce * _minForceFrac;
            float force = Mathf.Lerp(minForce, _localPen.Config.maxFlickForce, frac);

            // Send the final orientation + force in one atomic RPC
            // Server applies orientation, switches to dynamic physics, applies force
            _localPen.SubmitFlickServerRpc(_penH, _penV, force);

            _phase = Phase.Idle;
            _charge = 0f;
            _inputActive = false;    // locked until phase resets from server
            OnChargeChanged?.Invoke(0f);
            OnFlickSubmitted?.Invoke();
            OnInputActiveChanged?.Invoke(false);
        }

        // ── Phase / Player Events ──────────────────────────────────────────
        private void OnPhaseChanged(MatchPhase prev, MatchPhase next)
        {
            if (next == MatchPhase.ActiveTurnInput)
            {
                _localPen = PenRegistry.Instance?.GetLocalPlayerPen();
                _inputActive = MatchStateManager.Instance.IsLocalPlayerTurn;
                _phase = Phase.Idle;
                _charge = 0f;

                // Seed aim angles from pen's current world rotation so it doesn't snap
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
                _phase = Phase.Idle;
                OnChargeChanged?.Invoke(0f);
                OnInputActiveChanged?.Invoke(false);
            }
        }

        private void OnActivePlayerChanged(int newSlot)
        {
            // Re-check our turn status in case we need to re-enable
            if (MatchStateManager.Instance?.CurrentPhase == MatchPhase.ActiveTurnInput)
                OnPhaseChanged(MatchPhase.ActiveTurnInput, MatchPhase.ActiveTurnInput);
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private bool IsNearPen(Vector2 screenPos)
        {
            if (_localPen == null || _cam == null) return false;
            Vector2 penScreen = _cam.WorldToScreenPoint(_localPen.transform.position);
            return Vector2.Distance(screenPos, penScreen) <= _clickRadiusPx;
        }

        private void BindSelect()
        {
            if (_selectAction?.action == null) return;
            _selectAction.action.started += OnSelectStarted;
            _selectAction.action.canceled += OnSelectCanceled;
        }

        private void UnbindSelect()
        {
            if (_selectAction?.action == null) return;
            _selectAction.action.started -= OnSelectStarted;
            _selectAction.action.canceled -= OnSelectCanceled;
        }

        private void BindFlick()
        {
            if (_flickChargeAction?.action == null) return;
            _flickChargeAction.action.started += OnFlickStarted;
            _flickChargeAction.action.canceled += OnFlickCanceled;
        }

        private void UnbindFlick()
        {
            if (_flickChargeAction?.action == null) return;
            _flickChargeAction.action.started -= OnFlickStarted;
            _flickChargeAction.action.canceled -= OnFlickCanceled;
        }

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