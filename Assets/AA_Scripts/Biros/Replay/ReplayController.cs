// ReplayController.cs
// Client-side orchestrator for the knockout rewind-replay.
//
// TWO-CAMERA MODEL: _primaryCamera is a plain CinemachineVirtualCamera with
// no Body/Aim components — positioned once by hand at a fixed isometric
// angle wide enough to frame the whole desk, never touched by script.
// _replayCamera is the EXISTING CinemachineFreeLook rig (all the auto-follow/
// orbit/zoom machinery already built) — repurposed here as replay-only.
// During normal play only the primary camera is active (high priority,
// replay camera priority 0). CinemachineBrain blends to whichever vcam has
// the higher priority, so "cut to replay" and "cut back" are just priority
// swaps, nothing more exotic.
//
// TRIGGER: comes from MatchStateManager.OnReplayPauseChanged, not from
// watching any individual pen's IsOutOfBounds directly. The server is what
// actually freezes the match (every pen's physics, turn timer, settle
// polling) for the hold window — this script's only job is playing a visual
// inside a window that's already frozen for it. It doesn't decide when that
// happens or how long it lasts; MatchStateManager.ReplayHoldSeconds is the
// authority on duration, this just needs to fit its playback inside it.
//
// HOW SCRUBBING WORKS: while paused, nothing else is driving the subject
// pen's Transform — its Rigidbody is forced kinematic by the pause (see
// PenController.RecomputeKinematic), and the server isn't ticking anything
// that would move it. So this can safely take over transform.position/
// rotation directly for the duration of playback and hand control back
// after. No ghost objects, no hiding renderers needed — the real pen IS the
// actor, it's just inert for the window this runs in.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Cinemachine;
using Biros.Core;
using Biros.Gameplay;

namespace Biros.Replay
{
    public class ReplayController : MonoBehaviour
    {
        [Header("Cameras")]
        [Tooltip("Static, fixed, isometric, no Body/Aim components — always active outside of a replay.")]
        [SerializeField] private CinemachineVirtualCamera _primaryCamera;
        [Tooltip("The existing CinemachineFreeLook rig, repurposed as the replay-only camera.")]
        [SerializeField] private CinemachineFreeLook _replayCamera;
        [SerializeField] private int _primaryPriority = 10;
        [SerializeField] private int _replayPriority  = 20;

        [Header("Playback")]
        [SerializeField] private ReplayRecorder _recorder;
        [Tooltip("How far back into recorded history playback starts.")]
        [SerializeField] private float _replayWindowSeconds = 4f;
        [Tooltip("1 = real-time, less than 1 = slow-motion. Keep MatchStateManager.ReplayHoldSeconds >= _replayWindowSeconds / this, plus a buffer, or the match will resume before playback finishes.")]
        [SerializeField] private float _playbackSpeed = 0.6f;

        private Coroutine _playbackRoutine;

        private void Start()
        {
            if (_primaryCamera != null) _primaryCamera.Priority = _primaryPriority;
            if (_replayCamera  != null) _replayCamera.Priority  = 0;

            if (MatchStateManager.Instance != null)
                MatchStateManager.Instance.OnReplayPauseChanged += OnReplayPauseChanged;
        }

        private void OnDestroy()
        {
            if (MatchStateManager.Instance != null)
                MatchStateManager.Instance.OnReplayPauseChanged -= OnReplayPauseChanged;
        }

        private void OnReplayPauseChanged(bool paused)
        {
            if (paused) BeginReplay();
            else        EndReplay();
        }

        private void BeginReplay()
        {
            PenController subject = FindKnockedOutPen();
            if (subject == null || _recorder == null || _replayCamera == null) return;

            if (_playbackRoutine != null) StopCoroutine(_playbackRoutine);
            _playbackRoutine = StartCoroutine(PlaybackRoutine(subject));
        }

        private void EndReplay()
        {
            if (_playbackRoutine != null)
            {
                StopCoroutine(_playbackRoutine);
                _playbackRoutine = null;
            }

            if (_primaryCamera != null) _primaryCamera.Priority = _primaryPriority;
            if (_replayCamera  != null) _replayCamera.Priority  = 0;
        }

        private PenController FindKnockedOutPen()
        {
            if (PenRegistry.Instance == null) return null;

            foreach (var pen in PenRegistry.Instance.AllPens)
                if (pen != null && pen.IsOutOfBounds) return pen;

            return null;
        }

        private IEnumerator PlaybackRoutine(PenController subject)
        {
            IReadOnlyList<PenSnapshot> history = _recorder.GetHistory(subject);
            if (history.Count < 2) yield break; // nothing meaningful recorded yet

            float clipEnd   = history[history.Count - 1].Time;
            float clipStart = Mathf.Max(history[0].Time, clipEnd - _replayWindowSeconds);
            float clipLen   = Mathf.Max(clipEnd - clipStart, 0.01f);

            _replayCamera.Follow = subject.transform;
            _replayCamera.LookAt = subject.transform;
            _replayCamera.Priority = _replayPriority;
            if (_primaryCamera != null) _primaryCamera.Priority = 0;

            Vector3    realPos = subject.transform.position;
            Quaternion realRot = subject.transform.rotation;

            float playbackDuration = clipLen / Mathf.Max(_playbackSpeed, 0.01f);
            float elapsed = 0f;

            while (elapsed < playbackDuration)
            {
                elapsed += Time.deltaTime;
                float clipT      = Mathf.Clamp01(elapsed / playbackDuration);
                float sampleTime = clipStart + clipT * clipLen;

                Sample(history, sampleTime, out Vector3 pos, out Quaternion rot);
                subject.transform.SetPositionAndRotation(pos, rot);

                yield return null;
            }

            // Snap back to the real (frozen) position before handing control
            // back — avoids a one-frame pop when normal NetworkTransform sync
            // resumes driving this pen again.
            subject.transform.SetPositionAndRotation(realPos, realRot);
            _playbackRoutine = null;
        }

        private static void Sample(IReadOnlyList<PenSnapshot> history, float t,
                                    out Vector3 pos, out Quaternion rot)
        {
            if (history.Count == 0) { pos = Vector3.zero; rot = Quaternion.identity; return; }

            if (t <= history[0].Time)
            {
                pos = history[0].Position; rot = history[0].Rotation;
                return;
            }

            PenSnapshot last = history[history.Count - 1];
            if (t >= last.Time)
            {
                pos = last.Position; rot = last.Rotation;
                return;
            }

            for (int i = 0; i < history.Count - 1; i++)
            {
                if (t < history[i].Time || t > history[i + 1].Time) continue;

                float span = Mathf.Max(history[i + 1].Time - history[i].Time, 0.0001f);
                float frac = (t - history[i].Time) / span;
                pos = Vector3.Lerp(history[i].Position, history[i + 1].Position, frac);
                rot = Quaternion.Slerp(history[i].Rotation, history[i + 1].Rotation, frac);
                return;
            }

            pos = last.Position; rot = last.Rotation;
        }
    }
}
