// FlickInputHandler.cs — v4
// THE RULE: rotating = one move. flicking = one move. spinning = one move.
// You cannot rotate and then flick in the same turn. Ever.
//
// Turn flow options (mutually exclusive):
//   A) LMB down on pen → drag to rotate → LMB release → turn consumed (SubmitRotateServerRpc)
//   B) RMB down on pen → hold to charge → RMB release → turn consumed (SubmitFlickServerRpc)
//   C) Space down      → hold to charge → Space release → turn consumed (SpinMoveServerRpc)
//
// Accidental LMB tap safety: rotation only submits as a move if accumulated
// rotation delta exceeds MIN_ROTATION_TO_COMMIT (degrees). Below that threshold,
// LMB release just cancels back to Idle with no turn cost.

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
        [SerializeField] private InputActionReference _selectAction;       // LMB — rotate move
        [SerializeField] private InputActionReference _mouseDelta;         // Mouse Delta
        [SerializeField] private InputActionReference _mousePosition;      // Mouse Position
        [SerializeField] private InputActionReference _flickChargeAction;  // RMB — flick move
        [SerializeField] private InputActionReference _spinMoveAction;     // Space — spin move

        [Header("Rotation")]
        [SerializeField] private float _hSens = 0.45f;
        [SerializeField] private float _vSens = 0.30f;
        [SerializeField] private float _minTilt = 0f;
        [SerializeField] private float _maxTilt = 85f;
        [Tooltip("Minimum total rotation in degrees before LMB release submits it as a move. " +
                 "Prevents accidental taps from wasting a turn.")]
        [SerializeField] private float _minRotationToCommit = 5f;

        [Header("Flick Charge")]
        [SerializeField] private float _maxChargeSec = 1.5f;
        [SerializeField] private float _minForceFrac = 0.15f;

        [Header("Spin Move")]
        [SerializeField] private float _spinForceMultiplier = 1.4f;

        [Header("Selection")]
        [SerializeField] private float _clickRadiusPx = 90f;

        [Header("Network Rate")]
        [SerializeField] private float _orientHz = 20f;

        // ── Events ─────────────────────────────────────────────────────────
        /// <summary>0 = not charging, 1 = full. Fires during flick and spin charge phases.</summary>
        public event Action<float> OnChargeChanged;
        /// <summary>Fires when any move action is submitted (turn consumed).</summary>
        public event Action        OnTurnActionUsed;
        public event Action<bool>  OnInputActiveChanged;

        // ── State Machine ──────────────────────────────────────────────────
        // These three phases are completely exclusive — you enter exactly one per turn.
        private enum Phase { Idle, RotatingPen, ChargingFlick, ChargingSpinMove }
        private Phase _phase = Phase.Idle;

        private PenController _localPen;
        private bool          _inputActive;

        // Rotation state
        private float _penH;
        private float _penV;
        private float _rotationAccumDeg;   // tracks total rotation this turn for commit threshold
        private float _lastSendT;

        // Charge state (shared between flick and spin)
        private float _charge;

        // Flick-specific
        private float _flickOffsetNorm;    // -0.5 = tail, 0 = center, 0.5 = tip

        private Camera _cam;

        // ── Unity ──────────────────────────────────────────────────────────
        private void Awake() => _cam = Camera.main;

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
            MatchStateManager.Instance.OnPhaseChanged       += OnMatchPhaseChanged;
            MatchStateManager.Instance.OnActivePlayerChanged += OnActivePlayerChanged;
        }

        private void OnDestroy()
        {
            if (MatchStateManager.Instance == null) return;
            MatchStateManager.Instance.OnPhaseChanged       -= OnMatchPhaseChanged;
            MatchStateManager.Instance.OnActivePlayerChanged -= OnActivePlayerChanged;
        }

        private void Update()
        {
            if (!_inputActive || _localPen == null) return;

            switch (_phase)
            {
                case Phase.RotatingPen:      TickRotate(); break;
              //  case Phase.ChargingFlick:    TickCharge(); break;
              //  case Phase.ChargingSpinMove: TickCharge(); break;
            }
        }

        // ── Move A: Rotate ──────────────────────────────────────────────────
        // LMB down → start rotating. LMB release → auto-submit if enough was rotated.
        // CANNOT transition to flick from inside RotatingPen. Period.

        private void OnSelectStarted(InputAction.CallbackContext _)
        {
            if (!_inputActive || _localPen == null) return;
            if (_phase != Phase.Idle) return;           // already doing something this turn
            if (!IsNearPen(ReadMousePos())) return;

            _rotationAccumDeg = 0f;
            _phase            = Phase.RotatingPen;
        }

        private void OnSelectCanceled(InputAction.CallbackContext _)
        {
            if (_phase != Phase.RotatingPen) return;

            if (_rotationAccumDeg >= _minRotationToCommit)
            {
                // Significant rotation happened — this IS the player's move
                SubmitRotateAsMove();
            }
            else
            {
                // Accidental tap, too small to count — give the turn back
                _phase = Phase.Idle;
            }

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

            // Accumulate total rotation so OnSelectCanceled can decide if it counts as a move
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

        // ── Move B: Flick ───────────────────────────────────────────────────
        // RMB down → detect point on pen → charge → RMB release → flick.
        // Only available from Idle. NEVER from RotatingPen.

        private void OnFlickStarted(InputAction.CallbackContext _)
        {
            if (!_inputActive || _localPen == null) return;
            if (_phase != Phase.Idle) return;           // THE rule: must be idle, not mid-rotation

            Vector2 mousePos = ReadMousePos();

            // Require the click to be reasonably near the pen
            if (!IsNearPen(mousePos) && !IsDirectHitOnPen(mousePos)) return;

            _flickOffsetNorm = DetectPenHitOffset(mousePos);
            _charge          = 0f;
            _phase           = Phase.ChargingFlick;
            OnChargeChanged?.Invoke(0f);
        }

        private void OnFlickCanceled(InputAction.CallbackContext _)
        {
            if (_phase == Phase.ChargingFlick) SubmitFlick();
        }

        private void SubmitFlick()
        {
            if (_localPen?.Config == null) return;

            float frac     = Mathf.Clamp01(_charge / _maxChargeSec);
            float minForce = _localPen.Config.maxFlickForce * _minForceFrac;
            float force    = Mathf.Lerp(minForce, _localPen.Config.maxFlickForce, frac);

            _localPen.SubmitFlickServerRpc(_penH, _penV, force, _flickOffsetNorm);
            FinishTurn();
        }

        // ── Move C: Spin ────────────────────────────────────────────────────
        // Space down → charge → Space release → spin in place.
        // Only available from Idle. NEVER from RotatingPen.

        private void OnSpinStarted(InputAction.CallbackContext _)
        {
            if (!_inputActive || _localPen == null) return;
            if (_phase != Phase.Idle) return;           // must be idle

            _charge = 0f;
            _phase  = Phase.ChargingSpinMove;
            OnChargeChanged?.Invoke(0f);
        }

        private void OnSpinCanceled(InputAction.CallbackContext _)
        {
            if (_phase == Phase.ChargingSpinMove) SubmitSpinMove();
        }

        private void SubmitSpinMove()
        {
            if (_localPen?.Config == null) return;

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
            OnChargeChanged?.Invoke(0f);
            OnTurnActionUsed?.Invoke();
            OnInputActiveChanged?.Invoke(false);
        }

        // ── Phase / Player Events ──────────────────────────────────────────
        private void OnMatchPhaseChanged(MatchPhase prev, MatchPhase next)
        {
            if (next == MatchPhase.ActiveTurnInput)
            {
                _localPen    = PenRegistry.Instance?.GetLocalPlayerPen();
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
                OnChargeChanged?.Invoke(0f);
                OnInputActiveChanged?.Invoke(false);
            }
        }

        private void OnActivePlayerChanged(int _)
        {
            if (MatchStateManager.Instance?.CurrentPhase == MatchPhase.ActiveTurnInput)
                OnMatchPhaseChanged(MatchPhase.ActiveTurnInput, MatchPhase.ActiveTurnInput);
        }

        // ── Flick Point Detection ───────────────────────────────────────────
        private float DetectPenHitOffset(Vector2 screenPos)
        {
            if (_cam == null || _localPen == null) return 0f;

            Ray ray      = _cam.ScreenPointToRay(screenPos);
            Collider col = _localPen.GetComponent<Collider>();

            if (col != null && col.Raycast(ray, out RaycastHit hit, 100f))
            {
                Vector3 local   = _localPen.transform.InverseTransformPoint(hit.point);
                float   halfLen = _localPen.PenLength * 0.5f;
                return Mathf.Clamp(local.z / halfLen, -1f, 1f) * 0.5f;
            }

            return ScreenSpaceHitOffset(screenPos);
        }

        private float ScreenSpaceHitOffset(Vector2 screenPos)
        {
            float   half   = _localPen.PenLength * 0.5f;
            Vector3 fwd    = _localPen.transform.forward;
            Vector3 center = _localPen.transform.position;

            Vector2 tipScreen  = _cam.WorldToScreenPoint(center + fwd * half);
            Vector2 tailScreen = _cam.WorldToScreenPoint(center - fwd * half);
            Vector2 line       = tipScreen - tailScreen;
            float   lenSq      = line.sqrMagnitude;

            if (lenSq < 1f) return 0f;

            float t = Vector2.Dot(screenPos - tailScreen, line) / lenSq;
            return Mathf.Clamp01(t) - 0.5f;
        }

        private bool IsDirectHitOnPen(Vector2 screenPos)
        {
            if (_cam == null || _localPen == null) return false;
            Collider col = _localPen.GetComponent<Collider>();
            if (col == null) return false;
            return col.Raycast(_cam.ScreenPointToRay(screenPos), out _, 100f);
        }

        private bool IsNearPen(Vector2 screenPos)
        {
            if (_localPen == null || _cam == null) return false;
            Vector2 penScreen = _cam.WorldToScreenPoint(_localPen.transform.position);
            return Vector2.Distance(screenPos, penScreen) <= _clickRadiusPx;
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
