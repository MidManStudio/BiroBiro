// ReplayRecorder.cs
// Client-side (runs on every peer, including host). Continuously records a
// rolling position/rotation history for every registered pen so a knockout
// has something to actually rewind through when it happens.
//
// Purely local — reads whatever Transform values are already visible on this
// peer (driven by NetworkTransform under normal play). No networking of its
// own, no server involvement. Every client independently has its own replay
// buffer built from what it's already rendering.
//
// Retention window is intentionally a bit longer than what actually gets
// played back (see ReplayController._replayWindowSeconds) so there's margin.
// Tune both together if you change either.

using System;
using System.Collections.Generic;
using UnityEngine;
using Biros.Core;
using Biros.Gameplay;

namespace Biros.Replay
{
    public struct PenSnapshot
    {
        public float      Time;
        public Vector3    Position;
        public Quaternion Rotation;
    }

    public class ReplayRecorder : MonoBehaviour
    {
        [Tooltip("How much history to keep per pen before old samples get pruned.")]
        [SerializeField] private float _retentionSeconds = 6f;

        private readonly Dictionary<PenController, List<PenSnapshot>> _history = new();

        private void FixedUpdate()
        {
            if (PenRegistry.Instance == null) return;

            float now = Time.time;

            foreach (var pen in PenRegistry.Instance.AllPens)
            {
                if (pen == null) continue;

                if (!_history.TryGetValue(pen, out var list))
                {
                    list = new List<PenSnapshot>();
                    _history[pen] = list;
                }

                list.Add(new PenSnapshot
                {
                    Time     = now,
                    Position = pen.transform.position,
                    Rotation = pen.transform.rotation
                });

                float cutoff = now - _retentionSeconds;
                int cut = 0;
                while (cut < list.Count && list[cut].Time < cutoff) cut++;
                if (cut > 0) list.RemoveRange(0, cut);
            }
        }

        /// <summary>
        /// The full retained history for a pen, oldest first. Empty (not null)
        /// if nothing's been recorded for it yet.
        /// </summary>
        public IReadOnlyList<PenSnapshot> GetHistory(PenController pen)
        {
            return _history.TryGetValue(pen, out var list)
                ? list
                : (IReadOnlyList<PenSnapshot>)Array.Empty<PenSnapshot>();
        }
    }
}
