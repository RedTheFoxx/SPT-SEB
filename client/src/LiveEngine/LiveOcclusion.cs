// Occlusion: the live gate of CombinedSpatialAudioOccluder, and an optional extra muffling.
using System;
using System.Runtime.CompilerServices;
using Audio.Effects;
using Audio.SpatialSystem;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    /// <summary>Live CombinedSpatialAudioOccluder.OnUpdateOcclusionEffects: a source stays silent
    /// and filtered until the occlusion of its position is known, and while it is out of hearing
    /// range. SPT lets it play unfiltered in the meantime, which is heard through walls.</summary>
    internal static class LiveOcclusionGate
    {
        private const float HearingRangeOffset = 5f;
        private const float ClosedLowPass = 20f;

        private sealed class State
        {
            public bool Calculated;
            public bool ScoreReady;
        }

        private static readonly ConditionalWeakTable<CombinedSpatialAudioOccluder, State> States =
            new ConditionalWeakTable<CombinedSpatialAudioOccluder, State>();

        internal static bool Enabled = true;

        public static void Reset(CombinedSpatialAudioOccluder occluder)
        {
            State state = States.GetOrCreateValue(occluder);
            state.Calculated = false;
            state.ScoreReady = false;
        }

        /// <summary>SPT has no ScoreUpdated flag: the calculators have run once the late
        /// update of the occluder has passed.</summary>
        public static void MarkCalculated(CombinedSpatialAudioOccluder occluder)
        {
            States.GetOrCreateValue(occluder).Calculated = true;
        }

        /// <summary>False when the source must stay closed and SPT's update must not run.</summary>
        public static bool BeforeEffects(CombinedSpatialAudioOccluder occluder, AudioSourceContainer container, ref bool updateImmediately)
        {
            if (!Enabled)
            {
                return true;
            }
            State state = States.GetOrCreateValue(occluder);
            if (container.IsOutOfHearingRangeWithOffset(HearingRangeOffset))
            {
                HoldClosed(container);
                return false;
            }
            if (!state.Calculated)
            {
                // nothing to wait for when the game has stopped computing occlusion (no
                // listener room): closed here, the source would stay silent for good
                if (occluder._audioSystem.CanUpdate)
                {
                    HoldClosed(container);
                    return false;
                }
                return true;
            }
            if (!state.ScoreReady)
            {
                // the first result applies at once instead of fading in from nothing
                occluder._smoothedPropagationEffect = occluder._propagationCalculator.GetScore();
                occluder._smoothedObstructionEffect = occluder._obstructionCalculator.GetScore();
                state.ScoreReady = true;
                updateImmediately = true;
            }
            return true;
        }

        private static void HoldClosed(AudioSourceContainer container)
        {
            for (int i = 0; i < container.Count; i++)
            {
                BetterSource source = container[i];
                source.SetOcclusionVolumeFactor(0f);
                source.SetOcclusionRolloffScale(1f);
                source.SetLowPassFilterParameters(ClosedLowPass, 1f, true);
            }
        }
    }

    /// <summary>Optional extra muffling, off by default. It does not exist on live: the cutoff
    /// calculation and the gunshot EQ presets are the same on both games. It stays for those who
    /// prefer a heavier muffling, set by ear.</summary>
    public static class LiveOcclusionModule
    {
        private static readonly AccessTools.FieldRef<AudioEQFilterBase, float> TargetLowPassRef =
            AccessTools.FieldRefAccess<AudioEQFilterBase, float>("_targetLpf");
        private static readonly AccessTools.FieldRef<AudioEQFilterBase, float> TargetLowPassQRef =
            AccessTools.FieldRefAccess<AudioEQFilterBase, float>("_targetLpfQ");

        // config-driven (set by Plugin on load)
        public static float MuffleStrength;   // 0 = same as live, 1 = strongest
        public static float MinCutoffFrequency = 500f;
        public static bool LogCutoffChanges;
        private static int _logCounter;

        // the extra muffling is meant for distant sounds behind walls: a nearby source
        // keeps the muffling computed by SPT alone, then the extra part fades in
        private const float CloseRangeStart = 15f;
        private const float CloseRangeEnd = 40f;

        /// <summary>
        /// Postfix of SpatialAudioOccluder.ApplyOcclusionToSource: deepens the low-pass the
        /// system just asked for. Runs every frame per occluded source.
        /// </summary>
        public static void Postfix(BetterSource source, bool updateImmediately)
        {
            if (MuffleStrength <= 0f || source == null)
            {
                return;
            }
            try
            {
                float factor = source.OcclusionVolumeFactor;
                float effect = 1f - Mathf.Clamp01(factor);
                if (effect <= 0.15f)
                {
                    return;
                }
                AudioEQFilterBase filter = source.source1 != null ? source.source1.GetComponent<AudioEQFilterBase>() : null;
                if (filter == null)
                {
                    return;
                }
                // the target, not the live filter value: when the system smooths (no immediate
                // update) the filter still holds the cutoff we already lowered last time
                float requested = TargetLowPassRef(filter);
                float strength = Mathf.Clamp01((effect - 0.15f) / 0.85f);
                float distance = CameraManager.Instance.Distance(source.Position);
                strength = Mathf.Pow(strength, 1.5f) * Mathf.Clamp01(MuffleStrength)
                    * Mathf.InverseLerp(CloseRangeStart, CloseRangeEnd, distance);
                float target = Mathf.Max(MinCutoffFrequency, Mathf.Lerp(requested, MinCutoffFrequency, strength));
                target = Mathf.Min(target, requested);
                if (LogCutoffChanges && Mathf.Abs(requested - target) > 300f && (++_logCounter % 200) == 0)
                {
                    Plugin.Log.LogWarning(
                        $"[occl] {(source.source1.clip != null ? source.source1.clip.name : source.name)} " +
                        $"cutoff {requested:F0}->{target:F0}Hz effect={effect:F2} strength={strength:F2} volFactor={factor:F2}");
                }
                source.SetLowPassFilterParameters(target, TargetLowPassQRef(filter), updateImmediately);
            }
            catch (Exception)
            {
                // never break the occlusion chain
            }
        }
    }
}
