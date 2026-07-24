// PenConfigSO.cs
// ScriptableObject data container for a pen "loadout" — base physical stats
// plus CapMod/GripMod variants that get folded into the Computed* properties
// PenController and FlickInputHandler actually read at runtime.
//
// NOTE ON PLACEHOLDER NUMBERS: the exact per-mod offsets below (e.g. how much
// BittenChewed changes ComputedImpactDampening) aren't specified anywhere in
// the handover — I picked conservative placeholder values so the config
// compiles and behaves sensibly, not real balance numbers. Only
// ErraticSpinMultiplier is meaningfully wired to gameplay right now
// (PenController.SubmitFlickServerRpc checks it against 1f to decide whether
// to add chaos torque). Treat the rest as "tune me" until you've playtested.
//
// Create via: right-click in Project window > Create > Biros > Pen Config

using UnityEngine;

namespace Biros.Config
{
    public enum CapMod
    {
        Standard,
        BittenChewed, // adds random torque on flick/spin launch — see PenController
    }

    public enum GripMod
    {
        BarePlastic,
        // extend with more grip variants (rubberized, worn, etc.) as they're designed
    }

    [CreateAssetMenu(fileName = "PenConfig", menuName = "Biros/Pen Config", order = 0)]
    public class PenConfigSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Unique key used for PhysicsMaterial naming, save data, and prefab lookup.")]
        public string configId = "biro_unnamed";
        public string displayName = "Unnamed Biro";

        [Header("Base Physical Stats")]
        public float mass = 1.0f;
        public float drag = 0.05f;
        public float angularDrag = 0.05f;

        [Header("Flick")]
        public float flickForceMultiplier = 1.0f;
        public float maxFlickForce = 15.0f;

        [Header("Mods")]
        public CapMod capMod = CapMod.Standard;
        public GripMod gripMod = GripMod.BarePlastic;

        // ── Computed (mod-folded) properties ────────────────────────────────
        // PenController reads these instead of the raw fields above so mod
        // offsets never have to be duplicated at every call site.

        public float ComputedMass => mass; // no mod currently shifts mass

        public float ComputedFrictionScalar =>
            gripMod switch
            {
                GripMod.BarePlastic => 1.0f,
                _ => 1.0f,
            };

        public float ComputedImpactDampening =>
            capMod switch
            {
                CapMod.BittenChewed => 0.9f, // placeholder — slightly less bounce, chipped cap edge
                _ => 1.0f,
            };

        public float ErraticSpinMultiplier =>
            capMod switch
            {
                CapMod.BittenChewed => 1.3f, // placeholder — trips PenController's chaos-torque branch
                _ => 1.0f,
            };

        public float CenterOfMassShiftZ => 0f; // no mod currently shifts CoM
    }
}
