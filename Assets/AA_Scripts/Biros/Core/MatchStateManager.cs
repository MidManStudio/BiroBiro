using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using MidManStudio.Netcode;
using MidManStudio.Netcode.Singleton;
using MidManStudio.Core.Timers;
using NetworkTimer = MidManStudio.Netcode.NetworkTimer;
namespace Biros.Core
{
    public class MatchStateManager : NetworkSingleton<MatchStateManager>,
                                     INetworkSingletonLifecycle
    {
        // ── Inspector ──────────────────────────────────────────────────────
        [Header("Turn Settings")]
        [SerializeField] private float _turnDurationSeconds = 15f;

        [Header("Settle Detection")]
        [SerializeField] private float _settleLinearThreshold = 0.02f;
        [SerializeField] private float _settleAngularThreshold = 0.02f;
        [SerializeField] private float _settleGracePeriod = 0.5f;
        [SerializeField] private float _settleMaxWait = 8f;

        // ── Replicated ─────────────────────────────────────────────────────
        private NetworkVariable<MatchPhase> _phase = new(
            MatchPhase.None,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<int> _activePlayerSlot = new(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<int> _roundNumber = new(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // ── Server-only ────────────────────────────────────────────────────
        private List<ulong> _playerClientIds = new();
        private CountdownTimer _turnTimer;
        private NetworkTimer _settlePoller;
        private float _settleGraceAccum;
        private float _settleMaxAccum;

        // ── Events ─────────────────────────────────────────────────────────
        public event Action<MatchPhase, MatchPhase> OnPhaseChanged;
        public event Action<int> OnActivePlayerChanged;
        public event Action<ulong> OnMatchOver;

        // ── Accessors ──────────────────────────────────────────────────────
        public MatchPhase CurrentPhase => _phase.Value;
        public int ActivePlayerSlot => _activePlayerSlot.Value;
        public int RoundNumber => _roundNumber.Value;

        public ulong ActivePlayerClientId =>
            _activePlayerSlot.Value < _playerClientIds.Count
                ? _playerClientIds[_activePlayerSlot.Value]
                : ulong.MaxValue;

        public bool IsLocalPlayerTurn =>
            IsClient &&
            NetworkManager.Singleton != null &&
            ActivePlayerClientId == NetworkManager.Singleton.LocalClientId;

        public float TurnTimerNormalized =>
            (_turnTimer != null && _turnDurationSeconds > 0f)
                ? Mathf.Clamp01(_turnTimer.Progress / _turnDurationSeconds)
                : 0f;

        // ── INetworkSingletonLifecycle ─────────────────────────────────────
        public void OnNetworkSpawned(bool isServer, bool isHost, bool isClient, bool isOwner)
        {
            _phase.OnValueChanged += HandlePhaseChanged;
            _activePlayerSlot.OnValueChanged += HandleActivePlayerChanged;

            if (!isServer) return;

            _turnTimer = new CountdownTimer(_turnDurationSeconds);
            _turnTimer.OnTimerComplete += OnTurnTimerExpired;
            _settlePoller = new NetworkTimer(20f);
        }

        public void OnNetworkDespawned()
        {
            _phase.OnValueChanged -= HandlePhaseChanged;
            _activePlayerSlot.OnValueChanged -= HandleActivePlayerChanged;
            _turnTimer?.Stop();
        }

        public void OnNetworkSceneChange(string prev, string curr) { }

        // ── Unity Loop ─────────────────────────────────────────────────────
        private void Update()
        {
            if (!IsServer) return;

            _turnTimer.Tick(Time.deltaTime);

            if (_phase.Value == MatchPhase.SimulatePhysics)
            {
                _settlePoller.Update(Time.deltaTime);
                while (_settlePoller.ShouldTick())
                    ServerTickSettle(1f / 20f);
            }
        }

        // ── Public Server API ──────────────────────────────────────────────

        /// <summary>
        /// Start a match. Call once NGO has spawned all player objects and
        /// you have the ordered list of participant client IDs.
        /// </summary>
        public void ServerStartMatch(List<ulong> orderedClientIds)
        {
            if (!IsServer) return;
            _playerClientIds = new List<ulong>(orderedClientIds);
            _roundNumber.Value = 0;
            _activePlayerSlot.Value = 0;
            ServerTransitionTo(MatchPhase.InitializeMatch);
        }

        /// <summary>
        /// Called by PenController immediately after applying the physics impulse.
        /// Advances: ActiveTurnInput → ExecuteFlick → (2 physics frames) → SimulatePhysics.
        /// </summary>
        public void ServerNotifyFlickSubmitted()
        {
            if (!IsServer || _phase.Value != MatchPhase.ActiveTurnInput) return;
            _turnTimer.Stop();
            ServerTransitionTo(MatchPhase.ExecuteFlick);
        }
        /// <summary>
/// Called by PenController when the player commits a rotation as their move.
/// Skips SimulatePhysics entirely — pen stays put, turn advances.
/// </summary>
public void ServerNotifyRotateSubmitted()
{
    if (!IsServer || _phase.Value != MatchPhase.ActiveTurnInput) return;
    _turnTimer.Stop();
    ServerTransitionTo(MatchPhase.SwitchPlayer);
}

        /// <summary>
        /// Called by PenController when a pen falls out of bounds.
        /// Hook point for future scoring / VFX coordination.
        /// </summary>
        public void ServerOnPenExitedBounds(ulong ownerClientId) { /* scoring hook */ }

        // ── State Machine ──────────────────────────────────────────────────
        private void ServerTransitionTo(MatchPhase next)
        {
            if (!IsServer) return;

            MatchPhase prev = _phase.Value;
            _phase.Value = next;

            switch (next)
            {
                case MatchPhase.InitializeMatch:
                    // Wait briefly for any pending pen spawns to complete
                    StartCoroutine(ServerDelayThenTransition(0.15f, MatchPhase.ActiveTurnInput));
                    break;

                case MatchPhase.ActiveTurnInput:
                    _roundNumber.Value++;
                    _turnTimer.Start();
                    break;

                case MatchPhase.ExecuteFlick:
                    // Give physics two FixedUpdate frames to register the impulse,
                    // then move to SimulatePhysics so settle-polling begins cleanly.
                    StartCoroutine(ServerDelayThenTransition(
                        Time.fixedDeltaTime * 2f, MatchPhase.SimulatePhysics));
                    break;

                case MatchPhase.SimulatePhysics:
                    _settleGraceAccum = 0f;
                    _settleMaxAccum = 0f;
                    break;

                case MatchPhase.ResolveRound:
                    _turnTimer.Stop(); // safety — should already be stopped
                    StartCoroutine(ServerDelayThenTransition(0.6f, MatchPhase.SwitchPlayer));
                    break;

                case MatchPhase.SwitchPlayer:
                    ServerAdvanceTurn();
                    break;

                case MatchPhase.MatchOver:
                    OnMatchOver?.Invoke(DetermineWinner());
                    break;
            }
        }

        private void ServerAdvanceTurn()
        {
            if (_playerClientIds.Count == 0) return;

            _activePlayerSlot.Value =
                (_activePlayerSlot.Value + 1) % _playerClientIds.Count;

            // TODO: insert win-condition check here via ScoreManager
            bool matchOver = false;
            ServerTransitionTo(matchOver ? MatchPhase.MatchOver : MatchPhase.ActiveTurnInput);
        }

        private void ServerTickSettle(float dt)
        {
            _settleMaxAccum += dt;

            if (_settleMaxAccum >= _settleMaxWait)
            {
                Debug.LogWarning("[MatchStateManager] Settle hard cap — forcing ResolveRound.");
                ServerTransitionTo(MatchPhase.ResolveRound);
                return;
            }

            bool allStill = PenRegistry.Instance == null ||
                            PenRegistry.Instance.AreAllPensSettled(
                                _settleLinearThreshold, _settleAngularThreshold);

            if (allStill)
            {
                _settleGraceAccum += dt;
                if (_settleGraceAccum >= _settleGracePeriod)
                    ServerTransitionTo(MatchPhase.ResolveRound);
            }
            else
            {
                _settleGraceAccum = 0f;
            }
        }

        private void OnTurnTimerExpired()
        {
            if (!IsServer || _phase.Value != MatchPhase.ActiveTurnInput) return;
            Debug.Log($"[MatchStateManager] Turn expired for slot {_activePlayerSlot.Value}.");
            ServerTransitionTo(MatchPhase.SwitchPlayer);
        }

        private ulong DetermineWinner()
        {
            // Stub — replace with ScoreManager lookup
            return _playerClientIds.Count > 0 ? _playerClientIds[0] : ulong.MaxValue;
        }

        private IEnumerator ServerDelayThenTransition(float delay, MatchPhase next)
        {
            yield return new WaitForSeconds(delay);
            if (IsServer) ServerTransitionTo(next);
        }

        // ── NetworkVariable Callbacks ──────────────────────────────────────
        private void HandlePhaseChanged(MatchPhase prev, MatchPhase next) =>
            OnPhaseChanged?.Invoke(prev, next);

        private void HandleActivePlayerChanged(int prev, int next) =>
            OnActivePlayerChanged?.Invoke(next);
    }
}
