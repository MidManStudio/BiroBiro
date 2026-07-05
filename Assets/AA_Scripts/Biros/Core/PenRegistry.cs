using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using MidManStudio.Core.Singleton;
using Biros.Gameplay;

namespace Biros.Core
{
    /// <summary>
    /// Lightweight registry of every live PenController in the scene.
    /// Server uses it for settle detection; clients use it to locate the local pen.
    /// Add to the persistent Managers prefab alongside MatchStateManager.
    /// </summary>
    public class PenRegistry : Singleton<PenRegistry>
    {
        private readonly List<PenController> _pens = new();

        public IReadOnlyList<PenController> AllPens => _pens;

        public void Register(PenController pen)
        {
            if (pen != null && !_pens.Contains(pen))
                _pens.Add(pen);
        }

        public void Unregister(PenController pen) =>
            _pens.Remove(pen);

        /// <summary>
        /// Returns true when every non-OOB pen is below both velocity thresholds.
        /// Server-only meaningful call; returns true on clients to be safe.
        /// </summary>
        public bool AreAllPensSettled(float linearThreshold, float angularThreshold)
        {
            foreach (var pen in _pens)
            {
                if (pen == null || pen.IsOutOfBounds) continue;
                if (!pen.IsSettled(linearThreshold, angularThreshold)) return false;
            }
            return true;
        }

        /// <summary>Returns the pen assigned to a given turn slot, or null.</summary>
        public PenController GetPenForSlot(int ownerSlot)
        {
            foreach (var pen in _pens)
                if (pen != null && pen.OwnerSlot == ownerSlot) return pen;
            return null;
        }

        /// <summary>
        /// Returns the pen owned by the local NGO client, or null.
        /// Uses OwnerClientId — works on any client including the host.
        /// </summary>
        public PenController GetLocalPlayerPen()
        {
            if (NetworkManager.Singleton == null) return null;
            ulong localId = NetworkManager.Singleton.LocalClientId;
            foreach (var pen in _pens)
                if (pen != null && pen.OwnerClientId == localId) return pen;
            return null;
        }
    }
}