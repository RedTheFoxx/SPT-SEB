// Port of live 1.3 DynamicBetterSourcePool: sources are borrowed from BetterAudio
// on demand, never cut while another one is free, and handed back once idle so an
// idle weapon keeps nothing (each held source is updated every frame by SPT).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    public sealed class DynamicBetterSourcePool
    {
        private const double IdleReleaseDelay = 2.0;

        private sealed class Slot
        {
            public SuperSource Source;
            public double StartTime;
            public bool DefaultReverb;
            public LiveFilterBlend FilterBlend;
        }

        private readonly List<Slot> _slots = new List<Slot>();
        private readonly int _maxPoolSize;
        private double _lastBorrowTime;

        public DynamicBetterSourcePool(int maxPoolSize)
        {
            _maxPoolSize = maxPoolSize;
        }

        public int PoolSize => _slots.Count;

        public bool IsBusy
        {
            get
            {
                double now = AudioSettings.dspTime;
                for (int i = 0; i < _slots.Count; i++)
                {
                    if (IsSlotBusy(_slots[i], now))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private static bool IsSlotBusy(Slot slot, double now)
        {
            SuperSource source = slot.Source;
            if (source == null)
            {
                return false;
            }
            // isPlaying alone is not trusted before the scheduled start
            if (now < slot.StartTime || source.source1.isPlaying || source.source2.isPlaying)
            {
                return true;
            }
            // the reverb sources replay the clip into the Meta XR reverb: stopping them early cuts it
            if (source is Audio.ReverbSubsystem.ReverbSuperSource reverb)
            {
                AudioSource a = GunshotEmitterBase.ReverbSourceARef(reverb);
                AudioSource b = GunshotEmitterBase.ReverbSourceBRef(reverb);
                return (a != null && a.isPlaying) || (b != null && b.isPlaying);
            }
            return false;
        }

        /// <summary>A free source, a new one while under the limit, or as a last resort the
        /// oldest ringing one. <paramref name="isNew"/> reports a source just taken from BetterAudio.</summary>
        public SuperSource Borrow(out bool isNew)
        {
            isNew = false;
            double now = AudioSettings.dspTime;
            _lastBorrowTime = now;
            Slot oldest = null;
            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                Slot slot = _slots[i];
                if (slot.Source == null)
                {
                    LiveFilterBlend.Unregister(slot.Source);
                    _slots.RemoveAt(i);
                    continue;
                }
                if (!IsSlotBusy(slot, now))
                {
                    slot.Source.Stop();
                    return slot.Source;
                }
                if (oldest == null || slot.StartTime < oldest.StartTime)
                {
                    oldest = slot;
                }
            }
            if (_slots.Count < _maxPoolSize)
            {
                SuperSource created = BorrowFromBetterAudio();
                if (created != null)
                {
                    isNew = true;
                    return created;
                }
            }
            if (oldest != null)
            {
                oldest.Source.Stop();
                return oldest.Source;
            }
            return null;
        }

        private SuperSource BorrowFromBetterAudio()
        {
            BetterAudio betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (betterAudio == null)
            {
                return null;
            }
            SuperSource source = betterAudio.GetSource(BetterAudio.AudioSourceGroupType.Gunshots) as SuperSource;
            if (source != null)
            {
                _slots.Add(new Slot
                {
                    Source = source,
                    DefaultReverb = source.EnableReverb,
                    FilterBlend = LiveFilterBlend.Register(source),
                });
            }
            return source;
        }

        private Slot Find(SuperSource source)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Source == source)
                {
                    return _slots[i];
                }
            }
            return null;
        }

        internal LiveFilterBlend GetFilterBlend(SuperSource source) => Find(source)?.FilterBlend;

        public void MarkScheduled(SuperSource source, double startTime)
        {
            Slot slot = Find(source);
            if (slot != null)
            {
                slot.StartTime = startTime;
            }
        }

        /// <summary>Hands the idle sources back to BetterAudio after a pause in the firing.</summary>
        public void Tick(double dspNow)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                Slot slot = _slots[i];
                if (slot.FilterBlend != null && slot.Source != null && IsSlotBusy(slot, dspNow))
                {
                    slot.FilterBlend.Update();
                }
            }
            if (_slots.Count == 0 || dspNow - _lastBorrowTime < IdleReleaseDelay)
            {
                return;
            }
            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                Slot slot = _slots[i];
                if (slot.Source == null)
                {
                    LiveFilterBlend.Unregister(slot.Source);
                    _slots.RemoveAt(i);
                }
                else if (!IsSlotBusy(slot, dspNow))
                {
                    _slots.RemoveAt(i);
                    ReleaseSlot(slot);
                }
            }
        }

        private static void ReleaseSlot(Slot slot)
        {
            LiveFilterBlend.Unregister(slot.Source);
            if (slot.Source == null)
            {
                return;
            }
            try
            {
                // the source goes back to a pool shared with the rest of the game
                slot.Source.EnableReverb = slot.DefaultReverb;
                slot.Source.Release();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"source release failed: {e.Message}");
            }
        }

        public void Dispose()
        {
            foreach (Slot slot in _slots)
            {
                ReleaseSlot(slot);
            }
            _slots.Clear();
        }
    }
}
