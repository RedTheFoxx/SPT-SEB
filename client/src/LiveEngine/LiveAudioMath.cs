// Pure maths of the live 1.3 sound path (no Unity dependency, so it can be exercised
// outside the game): shot timing and pitch, SimpleUnitySpatializer targets, dynamic HRTF,
// filter request blending.
using System;

namespace SoundEngineBackport.LiveEngine
{
    internal struct FilterTargets
    {
        public float LowPass;
        public float LowPassQ;
        public float HighPass;
        public float HighPassQ;
    }

    internal static class LiveAudioMath
    {
        // serialized values of live AudioSourceSuperCustomSpatialBuiltinEQReverbPrefab
        private const float LpFront = 22000f;
        private const float LpBehind = 15000f;
        private const float LpBelow = 10000f;
        private const float HpDefault = 20f;
        private const float HpAbove = 400f;
        private const float HpBehind = 80f;
        private const float MinQ = 0.8f;
        private const float MaxQ = 2f;
        private const float QFront = 1f;
        private const float QBehind = 1.5f;
        private const float QAbove = 2f;
        private const float QBelow = 1.8f;
        private const float BelowLpBlend = 0.8f;

        private const float BehindHpBlend = 0.4f;
        private const float BelowQBlend = 0.7f;
        private const float BehindQBlend = 0.4f;
        private const float ElevationFactor = 57.29578f / 90f;

        // live _pitchByAmmoCurve, the same on every weapon prefab: 1.0 full mag, 1.05 empty
        private const float PitchByAmmoEmpty = 1.05f;

        private const float RandomRangeEpsilon = 0.0001f;

        public static float DbToGain(float db) => (float)Math.Pow(10.0, db / 20.0);

        private static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

        private static float Lerp(float from, float to, float t) => from + (to - from) * t;

        /// <summary>Live AudioWeaponUtils.GetPitchOnShot: a pitch per clip layer, the second one
        /// half a turn away on the random range, offset by the ammo curve and the overheat.</summary>
        public static void ComputeShotPitch(
            float random01,
            float minRandom,
            float maxRandom,
            float minClamp,
            float maxClamp,
            int magazineCount,
            int magazineCapacity,
            float overheatFireRateMult,
            out float pitchA,
            out float pitchB)
        {
            float a = minRandom;
            float b = minRandom;
            if (Math.Abs(maxRandom - minRandom) > RandomRangeEpsilon)
            {
                float t = Clamp01(random01);
                float shifted = t + 0.5f;
                a = Lerp(minRandom, maxRandom, t);
                b = Lerp(minRandom, maxRandom, Clamp01(shifted - (float)Math.Floor(shifted)));
            }

            float offset = 1f;
            if (magazineCapacity >= 1)
            {
                float spent = 1f - Clamp01((float)magazineCount / magazineCapacity);
                float ratio = overheatFireRateMult > 0f ? 1f / overheatFireRateMult : 1f;
                offset = Lerp(1f, PitchByAmmoEmpty, spent) * ratio;
            }

            pitchA = Math.Min(Math.Max(a + offset, minClamp), maxClamp);
            pitchB = Math.Min(Math.Max(b + offset, minClamp), maxClamp);
        }

        /// <summary>Live BetterSourceHRTFCalculator.CalculateHRTF: no HRTF right next to the
        /// listener or in front at the same height, more with distance, angle and height.</summary>
        public static float ComputeDynamicHrtf(
            float sqrDistance, float absAngle, float absHeight, float sqrEnableDistance, float minAngle, float minHeight, float intensity)
        {
            const float SqrTooClose = 2.25f;
            const float MaxAngle = 180f;
            if (sqrDistance < SqrTooClose)
            {
                return 0f;
            }
            float distance = sqrEnableDistance != SqrTooClose ? Clamp01((sqrDistance - SqrTooClose) / (sqrEnableDistance - SqrTooClose)) : 0f;
            float angle = minAngle != MaxAngle ? Clamp01((absAngle - minAngle) / (MaxAngle - minAngle)) : 0f;
            float height = minHeight > 0f ? Clamp01(absHeight / minHeight) : 1f;
            float blend = 1f - (1f - height) * (1f - distance) * (1f - angle);
            // live HrtfBlendCurve: (0, 0) to (1, 1) with flat tangents
            float curve = blend * blend * (3f - 2f * blend);
            return Clamp01(curve * intensity);
        }

        /// <summary>Live SimpleUnitySpatializer.EvaluateSpatializationParameters. The direction
        /// goes from the listener to the source and must be normalized, the forward too.</summary>
        public static FilterTargets EvaluateSpatializer(
            float dirX, float dirY, float dirZ, float forwardX, float forwardY, float forwardZ)
        {
            float behind = Clamp01(-(dirX * forwardX + dirY * forwardY + dirZ * forwardZ));
            float elevation = (float)Math.Asin(Math.Min(Math.Max(dirY, -1f), 1f));
            float above = Clamp01(elevation * ElevationFactor);
            float below = Clamp01(-elevation * ElevationFactor);

            FilterTargets targets;
            targets.LowPass = Lerp(Lerp(LpFront, LpBehind, behind), LpBelow, Clamp01(below * BelowLpBlend));
            targets.HighPass = Lerp(Lerp(HpDefault, HpAbove, above), HpBehind, Clamp01(behind * BehindHpBlend));
            float lowQ = Lerp(Lerp(QFront, QBehind, behind), QBelow, Clamp01(below * BelowQBlend));
            float highQ = Lerp(Lerp(MinQ, QAbove, above), BehindQBlend * QBehind, behind);
            targets.LowPassQ = Math.Min(Math.Max(lowQ, MinQ), MaxQ);
            targets.HighPassQ = Math.Min(Math.Max(highQ, MinQ), MaxQ);
            return targets;
        }
    }

    /// <summary>Live AudioFilterBlendController: what the spatializer and the occluder ask of
    /// the same filter. The lowest low-pass and the highest high-pass win, each with its Q.</summary>
    internal sealed class FilterBlend
    {
        public const float DefaultLowPass = 22000f;
        public const float DefaultHighPass = 10f;
        public const float DefaultQ = 1f;

        private bool _hasSpatial;
        private FilterTargets _spatial;
        private bool _hasOccluderLow;
        private float _occluderLow;
        private float _occluderLowQ;
        private bool _hasOccluderHigh;
        private float _occluderHigh;
        private float _occluderHighQ;

        public void SetSpatial(FilterTargets targets)
        {
            _spatial = targets;
            _hasSpatial = true;
        }

        public void ClearSpatial() => _hasSpatial = false;

        public void SetOccluderLowPass(float frequency, float q)
        {
            _occluderLow = frequency;
            _occluderLowQ = q;
            _hasOccluderLow = true;
        }

        public void SetOccluderHighPass(float frequency, float q)
        {
            _occluderHigh = frequency;
            _occluderHighQ = q;
            _hasOccluderHigh = true;
        }

        public void ClearOccluder()
        {
            _hasOccluderLow = false;
            _hasOccluderHigh = false;
        }

        public FilterTargets Compute()
        {
            FilterTargets result;
            result.LowPass = _hasSpatial ? _spatial.LowPass : DefaultLowPass;
            result.LowPassQ = _hasSpatial ? _spatial.LowPassQ : DefaultQ;
            if (_hasOccluderLow && (!_hasSpatial || _occluderLow <= _spatial.LowPass))
            {
                result.LowPass = _occluderLow;
                result.LowPassQ = _occluderLowQ;
            }
            result.HighPass = _hasSpatial ? _spatial.HighPass : DefaultHighPass;
            result.HighPassQ = _hasSpatial ? _spatial.HighPassQ : DefaultQ;
            if (_hasOccluderHigh && (!_hasSpatial || _occluderHigh >= _spatial.HighPass))
            {
                result.HighPass = _occluderHigh;
                result.HighPassQ = _occluderHighQ;
            }
            return result;
        }
    }

    internal static class ShotTiming
    {
        private const double MinValidBeatDuration = 0.001;
        private const double MinLeadSeconds = 0.03;

        /// <summary>Largest amount a shot may be held back to stay on the metronome.</summary>
        public static double MaxLead(double beatDuration)
        {
            return Math.Max(MinLeadSeconds, beatDuration * 0.5);
        }

        /// <summary>
        /// DSP start time of a shot: previous shot + one beat when that stays close to
        /// the moment the shot should be heard, otherwise the moment itself.
        ///
        /// Deviation from live, which rounds up to a whole beat: on SPT the shot events
        /// reach us quantized on frames and on the DSP clock (21.3 ms steps in the raid
        /// logs), so being 1 ms late on the grid cost a full beat (a 92 ms lag on the first
        /// first-person shot, 160 ms holes inside 80 ms salves), and a real cadence faster
        /// than the nominal rate accumulated an unbounded lag behind the gun.
        /// </summary>
        public static double ComputeScheduled(double now, double timeToHear, double previousIdeal, double beatDuration, double minOffset)
        {
            double earliest = Math.Max(timeToHear, now + minOffset);
            if (double.IsNaN(earliest) || double.IsInfinity(earliest))
            {
                return now + minOffset;
            }
            if (previousIdeal <= 0.0 || double.IsNaN(previousIdeal) || double.IsInfinity(previousIdeal)
                || beatDuration < MinValidBeatDuration || double.IsNaN(beatDuration) || double.IsInfinity(beatDuration))
            {
                return earliest;
            }
            double ideal = previousIdeal + beatDuration;
            double latest = earliest + MaxLead(beatDuration);
            return Math.Min(Math.Max(ideal, earliest), latest);
        }

        /// <summary>
        /// Fade of the previous shot when the next one starts (live ScheduleShotSound):
        /// beat * BurstFireFadeMultiplier, stretched when the shot leads the grid by more
        /// than the adaptive threshold, always capped to AdaptiveFadeMaxSeconds.
        /// </summary>
        public static float ComputeFade(
            float beatDuration,
            float leadSeconds,
            float fadeMultiplier,
            bool adaptive,
            float adaptiveLeadThresholdBeats,
            float adaptiveMaxMultiplier,
            float maxSeconds)
        {
            if (beatDuration <= 0f)
            {
                return 0f;
            }
            float fade = beatDuration * fadeMultiplier;
            if (adaptive)
            {
                float ratio = leadSeconds / beatDuration;
                if (ratio > adaptiveLeadThresholdBeats)
                {
                    float t = Math.Min(1f, ratio - adaptiveLeadThresholdBeats);
                    fade *= (adaptiveMaxMultiplier - 1f) * t + 1f;
                }
            }
            return Math.Max(0f, Math.Min(fade, maxSeconds));
        }

        /// <summary>
        /// True when the gap since the last shot ends the salve (live
        /// _sequenceResetMultiplier = FireLoopBreakMultiplier). Measured between two
        /// audition times, so that the distance delay does not hide the pause.
        /// </summary>
        public static bool IsNewSequence(double timeToHear, double previousIdeal, double beatDuration, double resetBeats)
        {
            return previousIdeal <= 0.0 || timeToHear - previousIdeal > resetBeats * beatDuration;
        }
    }
}
