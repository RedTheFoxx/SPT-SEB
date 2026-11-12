// Port of live 1.3 gun-shot emitters (Audio.Weapons):
//  - AutoFireGunshotEmitter: every bullet of a salve starts on its own pooled source;
//    the previous one keeps ringing and fades out once the next one starts.
//  - SingleGunshotEmitter: isolated shots, which ring out completely (live has no
//    fade for them: no pending-fade list, no fade time).
// LiveWeaponSoundEngine picks one or the other per fire mode.
using System;
using System.Collections.Generic;
using Audio.ReverbSubsystem;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace SoundEngineBackport.LiveEngine
{
    /// <summary>Live ClientAudioWeaponSettings.WeaponPlaybackSettings (1.3.0.48484 dump).
    /// FireLoopBreakMultiplier is the pause (in beats) that ends a salve and
    /// BurstFireFadeMultiplier the fade between shots (in beats).</summary>
    public sealed class WeaponPlaybackSettings
    {
        public float FireLoopBreakMultiplier;
        public float BurstFireFadeMultiplier;
        public float MinRandomPitch;
        public float MaxRandomPitch;
        public float MinClampPitch;
        public float MaxClampPitch;
        public float OcclusionThreshold;
        public bool AdaptiveFadeEnabled;
        public float AdaptiveFadeLeadBeatsThreshold;
        public float AdaptiveFadeMaxMultiplier;
        public float AdaptiveFadeMaxSeconds;
    }

    /// <summary>Live ShotPlaybackContext (Audio.Weapons), plus the position of the shot.</summary>
    public struct ShotPlaybackContext
    {
        public SoundBank Bank;
        public Vector3 Position;
        public float DistanceToListener;
        public float MaxDistance;
        public float BaseVolume;
        public EnvironmentType Environment;
        public bool IsFirstPerson;
        public float Delay;
        public float BeatDuration;
        public float PitchA;
        public float PitchB;
    }

    /// <summary>What the two emitters share: setting up a source and starting a shot.</summary>
    public abstract class GunshotEmitterBase
    {
        internal static readonly AccessTools.FieldRef<ReverbSuperSource, AudioSource> ReverbSourceARef =
            AccessTools.FieldRefAccess<ReverbSuperSource, AudioSource>("_reverbSourceA");
        internal static readonly AccessTools.FieldRef<ReverbSuperSource, AudioSource> ReverbSourceBRef =
            AccessTools.FieldRefAccess<ReverbSuperSource, AudioSource>("_reverbSourceB");

        protected readonly DynamicBetterSourcePool Pool;
        private readonly LiveWeaponSoundEngine _engine;

        protected GunshotEmitterBase(LiveWeaponSoundEngine engine, int maxSources)
        {
            _engine = engine;
            Pool = new DynamicBetterSourcePool(maxSources);
        }

        public bool IsBusy => Pool.IsBusy;

        public abstract void ScheduleShotSound(ShotPlaybackContext context);
        public virtual void Reset() { }
        public virtual void Tick(double dspNow) => Pool.Tick(dspNow);
        public void Dispose() => Pool.Dispose();

        /// <summary>Called before a pooled source is reused for a new shot.</summary>
        protected virtual void OnSourceBorrowed(SuperSource source) { }

        /// <summary>Starts a shot on a pooled source; false when there is nothing to play.</summary>
        protected bool TryStartShot(ShotPlaybackContext context, double scheduled, out SuperSource source, out float length)
        {
            source = null;
            length = 0f;

            AudioClip clip1 = null, clip2 = null;
            float balance = 1f;
            context.Bank.PickClipsByDistance(ref clip1, ref clip2, ref balance, (int)context.Environment, context.DistanceToListener);
            if (clip1 == null && clip2 == null)
            {
                // silenced banks have no clip in their farthest layer: past the last
                // distance of their BlendOptions a suppressed shot is not heard
                if (PluginConfig.LogShotDetails)
                {
                    Plugin.Log.LogWarning($"[shot-silent] {context.Bank.name}: no clip at {context.DistanceToListener:F1}m (env {context.Environment})");
                }
                return false;
            }

            source = Pool.Borrow(out bool isNew);
            if (source == null)
            {
                Plugin.Log.LogWarning($"[shot-lost] {context.Bank.name}: no audio source available");
                return false;
            }
            OnSourceBorrowed(source);

            source.Position = context.Position;
            LiveFilterBlend filterBlend = Pool.GetFilterBlend(source);
            filterBlend?.PrepareShot(!context.IsFirstPerson);
            source.SetRolloff(context.MaxDistance);
            source.IncludeInOcclusionProcess(!context.IsFirstPerson);
            if (!context.IsFirstPerson)
            {
                // every shot, like live: joins the container if needed and refreshes the
                // occlusion now, so volume and mixer group below use the current values
                _engine.ProcessOcclusion(source);
            }
            // after the occlusion registration: the first shot of a player creates the
            // container that decides which mixer group is used
            AudioMixerGroup mixerGroup = _engine.ResolveMixerGroup();
            if (mixerGroup != null)
            {
                source.SetMixerGroup(mixerGroup);
            }
            // a pitch per clip layer, the reverb sources follow their own layer
            source.SetPitch(context.PitchA);
            source.source2.pitch = context.PitchB;
            if (source is ReverbSuperSource reverbSource)
            {
                ReverbSourceBRef(reverbSource).pitch = context.PitchB;
            }
            source.EnableStereo(context.IsFirstPerson);
            if (LiveWeaponSoundEngine.ApplyReverbRouting)
            {
                source.EnableReverb = context.IsFirstPerson || context.Environment == EnvironmentType.Indoor;
            }
            _engine.ApplyPriority(source, context.DistanceToListener, context.MaxDistance);
            filterBlend?.ApplyNow();

            // same playback pattern as SPT's SuperAudioSample.PlayOn: raw volumes on both
            // sources + PlayScheduled, which turns them into pre-occlusion volumes
            float volume = context.BaseVolume * context.Bank.BaseVolume * LiveMixer.GunshotGroupVolume;
            source.source1.clip = clip1;
            source.source1.volume = balance * volume;
            source.source2.enabled = true;
            source.source2.clip = clip2;
            source.source2.volume = (1f - balance) * volume;
            source.PlayScheduled(scheduled);
            ScheduleEndBeforeWrap(source, clip1, clip2, scheduled, context.PitchA, context.PitchB);
            Pool.MarkScheduled(source, scheduled);

            length = Mathf.Max(
                clip1 != null ? clip1.length / Mathf.Max(0.01f, context.PitchA) : 0f,
                clip2 != null ? clip2.length / Mathf.Max(0.01f, context.PitchB) : 0f);

            if (PluginConfig.LogShotDetails)
            {
                Plugin.Log.LogWarning($"[sched] {GetType().Name} start=+{(scheduled - AudioSettings.dspTime) * 1000.0:F0}ms " +
                    $"lag={(scheduled - AudioSettings.dspTime - context.Delay) * 1000.0:F0}ms " +
                    $"clips=[{(clip1 != null ? clip1.name : "null")}|{(clip2 != null ? clip2.name : "null")}] balance={balance:F2} vol={volume:F2} " +
                    $"occ={source.OcclusionVolumeFactor:F2} mixer={(mixerGroup != null ? mixerGroup.name : "null")} reverb={source.EnableReverb} " +
                    $"pool={Pool.PoolSize} new={isNew}");
            }
            return true;
        }

        /// <summary>Seconds of output cut before the end of a clip: well past the few samples
        /// the resampler reads ahead, and inside the final silence of most clips (46 ms median
        /// over 916 clips of the live banks).</summary>
        internal const double EndMargin = 0.002;

        /// <summary>At the end of a clip, the resampler (44.1 kHz clips, 48 kHz output, pitch
        /// not 1) reads the first sample of the clip again. Two thirds of the live gunshot clips
        /// start at full scale: a dry click 1.4 s after each shot, measured on a recording of the
        /// game output (same sign and left/right ratio as the first sample of the clip). Each
        /// source stops just before its last samples instead.</summary>
        private static void ScheduleEndBeforeWrap(SuperSource source, AudioClip clip1, AudioClip clip2, double start, float pitchA, float pitchB)
        {
            double endA = clip1 != null ? start + clip1.length / Mathf.Max(0.01f, pitchA) - EndMargin : 0.0;
            double endB = clip2 != null ? start + clip2.length / Mathf.Max(0.01f, pitchB) - EndMargin : 0.0;
            if (clip1 != null)
            {
                source.source1.SetScheduledEndTime(endA);
            }
            if (clip2 != null)
            {
                source.source2.SetScheduledEndTime(endB);
            }
            if (source is ReverbSuperSource reverb)
            {
                // they replay the clip of source1 and source2, with the same pitches
                AudioSource a = ReverbSourceARef(reverb);
                AudioSource b = ReverbSourceBRef(reverb);
                if (a != null && a.clip != null && clip1 != null)
                {
                    a.SetScheduledEndTime(endA);
                }
                if (b != null && b.clip != null && clip2 != null)
                {
                    b.SetScheduledEndTime(endB);
                }
            }
        }
    }

    public sealed class AutoFireGunshotEmitter : GunshotEmitterBase
    {
        private sealed class PendingFade
        {
            public SuperSource Target;
            public double StartTime;
            public float Duration;
        }

        /// <summary>SPT's Stop(fade) lowers FadeFactor, which SuperSource applies to source1 and
        /// source2 only. The two reverb sources of a ReverbSuperSource keep their volume until
        /// OnStop cuts them: a dry click at the end of every faded shot. They fade here instead.</summary>
        private sealed class ReverbFade
        {
            public SuperSource Target;
            public AudioSource A;
            public AudioSource B;
            public float VolumeA;
            public float VolumeB;
            public double StartTime;
            public float Duration;
        }

        private const double BaseMinOffsetSec = 0.01;

        private readonly WeaponPlaybackSettings _settings;
        private readonly double _minOffset;
        private readonly List<PendingFade> _pendingFades = new List<PendingFade>();
        private readonly List<ReverbFade> _reverbFades = new List<ReverbFade>();
        private double _nextIdealShotTime = -1.0;
        private SuperSource _lastUsedSource;
        private double _lastUsedEndTime;

        public AutoFireGunshotEmitter(LiveWeaponSoundEngine engine, WeaponPlaybackSettings settings, int maxSources)
            : base(engine, maxSources)
        {
            _settings = settings;
            // SPT has no server clock to align on: the DSP clock is the only time base. Never
            // schedule inside the audio block being mixed, or the shot starts on the next
            // block instead of on its exact time (21.3 ms blocks in the raid logs)
            AudioSettings.GetDSPBufferSize(out int bufferLength, out _);
            int sampleRate = AudioSettings.outputSampleRate;
            _minOffset = Math.Max(BaseMinOffsetSec, sampleRate > 0 ? (double)bufferLength / sampleRate : 0.0);
        }

        public override void ScheduleShotSound(ShotPlaybackContext context)
        {
            if (context.Bank == null)
            {
                return;
            }
            double dspNow = AudioSettings.dspTime;
            double timeToHear = dspNow + context.Delay;
            float beat = context.BeatDuration;

            // a pause ends the salve: the next shot starts at once and the end of the
            // previous salve rings out over it (live Reset)
            if (ShotTiming.IsNewSequence(timeToHear, _nextIdealShotTime, beat, _settings.FireLoopBreakMultiplier))
            {
                Reset();
            }

            double scheduled = ShotTiming.ComputeScheduled(AudioSettings.dspTime, timeToHear, _nextIdealShotTime, beat, _minOffset);
            float fade = ShotTiming.ComputeFade(
                beat,
                (float)(scheduled - dspNow),
                _settings.BurstFireFadeMultiplier,
                _settings.AdaptiveFadeEnabled,
                _settings.AdaptiveFadeLeadBeatsThreshold,
                _settings.AdaptiveFadeMaxMultiplier,
                _settings.AdaptiveFadeMaxSeconds);

            SuperSource previous = _lastUsedSource;
            double previousEnd = _lastUsedEndTime;
            if (!TryStartShot(context, scheduled, out SuperSource source, out float length))
            {
                return;
            }

            // a source reused for this very shot has nothing left to fade
            if (previous != null && previous != source)
            {
                TrackPendingFade(previous, scheduled, Mathf.Min(fade, (float)(previousEnd - scheduled)));
            }
            _nextIdealShotTime = scheduled;
            _lastUsedSource = source;
            _lastUsedEndTime = scheduled + length;
        }

        private void TrackPendingFade(SuperSource target, double startTime, float duration)
        {
            if (duration > 0f)
            {
                _pendingFades.Add(new PendingFade { Target = target, StartTime = startTime, Duration = duration });
            }
        }

        protected override void OnSourceBorrowed(SuperSource source)
        {
            // what was planned for this source concerned its previous shot
            for (int i = _pendingFades.Count - 1; i >= 0; i--)
            {
                if (_pendingFades[i].Target == source)
                {
                    _pendingFades.RemoveAt(i);
                }
            }
            for (int i = _reverbFades.Count - 1; i >= 0; i--)
            {
                if (_reverbFades[i].Target == source)
                {
                    _reverbFades.RemoveAt(i);
                }
            }
            if (_lastUsedSource == source)
            {
                _lastUsedSource = null;
            }
        }

        private void StartReverbFade(SuperSource target, double dspNow, float duration)
        {
            if (!(target is ReverbSuperSource reverb))
            {
                return;
            }
            AudioSource a = ReverbSourceARef(reverb);
            AudioSource b = ReverbSourceBRef(reverb);
            if ((a == null || !a.isPlaying) && (b == null || !b.isPlaying))
            {
                return;
            }
            _reverbFades.Add(new ReverbFade
            {
                Target = target,
                A = a,
                B = b,
                VolumeA = a != null ? a.volume : 0f,
                VolumeB = b != null ? b.volume : 0f,
                StartTime = dspNow,
                Duration = duration,
            });
        }

        private void TickReverbFades(double dspNow)
        {
            for (int i = _reverbFades.Count - 1; i >= 0; i--)
            {
                ReverbFade fade = _reverbFades[i];
                float left = fade.Target == null ? 0f
                    : Mathf.Clamp01(1f - (float)((dspNow - fade.StartTime) / Mathf.Max(0.001f, fade.Duration)));
                if (fade.A != null)
                {
                    fade.A.volume = fade.VolumeA * left;
                }
                if (fade.B != null)
                {
                    fade.B.volume = fade.VolumeB * left;
                }
                if (left <= 0f)
                {
                    _reverbFades.RemoveAt(i);
                }
            }
        }

        public override void Tick(double dspNow)
        {
            for (int i = _pendingFades.Count - 1; i >= 0; i--)
            {
                PendingFade pending = _pendingFades[i];
                if (pending.Target == null)
                {
                    _pendingFades.RemoveAt(i);
                }
                else if (dspNow >= pending.StartTime)
                {
                    _pendingFades.RemoveAt(i);
                    if (pending.Target.source1.isPlaying || pending.Target.source2.isPlaying)
                    {
                        // Stop(fade) ends the source in the same step as the fade: stopping it
                        // later lets SPT restore FadeFactor=1 first, and the end of the clip
                        // comes back up for a few frames (ghost blip after every shot)
                        pending.Target.Stop(pending.Duration);
                        // a little shorter: SPT's fade follows game time and can end first
                        StartReverbFade(pending.Target, dspNow, pending.Duration * 0.8f);
                    }
                }
            }
            TickReverbFades(dspNow);
            Pool.Tick(dspNow);
        }

        /// <summary>Port of live Reset(): the shots already started ring out.</summary>
        public override void Reset()
        {
            // the reverb fades already started go on: their source is being stopped
            _pendingFades.Clear();
            _nextIdealShotTime = -1.0;
            _lastUsedSource = null;
        }
    }

    public sealed class SingleGunshotEmitter : GunshotEmitterBase
    {
        public SingleGunshotEmitter(LiveWeaponSoundEngine engine, int maxSources)
            : base(engine, maxSources)
        {
        }

        public override void ScheduleShotSound(ShotPlaybackContext context)
        {
            if (context.Bank != null)
            {
                TryStartShot(context, AudioSettings.dspTime + context.Delay, out _, out _);
            }
        }
    }
}
