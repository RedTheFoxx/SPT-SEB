// Volume per kind of isolated ambient sound (birds, dogs, ...).
// Each of these sounds is its own BaseAmbientSoundPlayer in the map's *_Sound scene, named after what it plays
// (Crow, Dogs, Frog, ...) and sitting under a group named the same way (FarDogs, DogsGroup). The mixer can't tell
// them from the wind, so the volume is applied where the player reads it: GetBaseVolume.
using System;
using System.Collections.Generic;
using Audio.AmbientSubsystem;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    internal enum AmbientCategory
    {
        None = 0,
        Birds,
        DogsAndWolves,
        SmallWildlife,
        Eerie,
    }

    internal static class AmbientSoundCategories
    {
        // below this a slider means "off"
        internal const float MuteDb = -60f;
        private const float RefreshFadeSeconds = 0.5f;
        private const int AncestorDepth = 3;

        private static readonly float[] Gains = { 1f, 1f, 1f, 1f, 1f };
        private static readonly Dictionary<int, AmbientCategory> Cache = new Dictionary<int, AmbientCategory>();

        // object names, lower case, without the " (n)" suffix; the live scenes misspell some of them on purpose
        private static readonly HashSet<string> BirdNames = new HashSet<string>
        {
            "bluetit", "bullfinch", "buzzard", "crow", "cucumber", "finch", "golfginch", "greenmocking", "lark",
            "magpie", "nightingale", "nightigale", "raven", "redpoll", "robin", "rook", "siskin", "snowy_owl", "owl",
            "owl_big", "owl_squeak", "sparrow", "starling", "thrush", "waxwing", "woodpecker", "bellbird", "gagara",
            "darkcornuta", "reed_warpler", "white_tailed_eagle", "black_kite", "sparrowingarbagerpp",
        };

        private static readonly HashSet<string> WildlifeNames = new HashSet<string>
        {
            "frog", "pitchedbat", "crickets", "corps_flies", "flies",
        };

        internal static AmbientCategory Classify(BaseAmbientSoundPlayer player)
        {
            int id = player.GetInstanceID();
            if (!Cache.TryGetValue(id, out AmbientCategory category))
            {
                category = ClassifyUncached(player.transform);
                Cache[id] = category;
            }
            return category;
        }

        private static AmbientCategory ClassifyUncached(Transform self)
        {
            Transform t = self;
            for (int depth = 0; t != null && depth <= AncestorDepth; depth++, t = t.parent)
            {
                // a group's name says what its children play, a player's name what it plays itself
                AmbientCategory category = ClassifyName(Normalize(t.name));
                if (category != AmbientCategory.None)
                {
                    return category;
                }
            }
            return AmbientCategory.None;
        }

        private static AmbientCategory ClassifyName(string name)
        {
            if (name.Contains("scary") || name.Contains("zombie") || name.Contains("ghost") || name.Contains("creepy")
                || name.Contains("scrapes") || name.Contains("underground_roar"))
            {
                return AmbientCategory.Eerie;
            }
            if (name.Contains("dogs") || name.Contains("wolves") || name.Contains("wolf"))
            {
                return AmbientCategory.DogsAndWolves;
            }
            if (BirdNames.Contains(name) || name.Contains("flocks"))
            {
                return AmbientCategory.Birds;
            }
            if (WildlifeNames.Contains(name) || name.Contains("rats") || name.Contains("ratgroup"))
            {
                return AmbientCategory.SmallWildlife;
            }
            return AmbientCategory.None;
        }

        // lower case, " (3)" removed, the Cyrillic "С" of one live object mapped to a Latin "c"
        private static string Normalize(string name)
        {
            name = name.Trim().ToLowerInvariant().Replace('с', 'c');
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && name.EndsWith(")", StringComparison.Ordinal))
            {
                name = name.Substring(0, open);
            }
            return name.Replace(" ", "");
        }

        internal static float GetGain(AmbientCategory category) => Gains[(int)category];

        internal static void SetTrimDb(AmbientCategory category, float db)
        {
            Gains[(int)category] = db <= MuteDb ? 0f : LiveAudioMath.DbToGain(db);
        }

        /// <summary>Players that already play keep the volume they faded to: fade them to the new one.</summary>
        internal static void Refresh()
        {
            foreach (BaseAmbientSoundPlayer player in UnityEngine.Object.FindObjectsOfType<BaseAmbientSoundPlayer>())
            {
                if (player != null && player.IsPlaying && Classify(player) != AmbientCategory.None)
                {
                    player.Fader?.FadeTo(player.GetBaseVolume(), RefreshFadeSeconds);
                }
            }
        }

        /// <summary>One line per category and object name, to check what a map plays.</summary>
        internal static void LogScene()
        {
            var counts = new SortedDictionary<string, int>();
            foreach (BaseAmbientSoundPlayer player in UnityEngine.Object.FindObjectsOfType<BaseAmbientSoundPlayer>(true))
            {
                string key = $"{Classify(player)}: {player.name}";
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            foreach (KeyValuePair<string, int> pair in counts)
            {
                Plugin.Log.LogInfo($"ambient player {pair.Key} x{pair.Value}");
            }
        }
    }

    [HarmonyPatch]
    public static class AmbientCategoryPatches
    {
        // after the system starts, its players exist: lists them when the debug log is on
        [HarmonyPatch(typeof(AmbientAudioSystem), "Init")]
        [HarmonyPostfix]
        public static void InitPostfix()
        {
            if (PluginConfig.LogShotDetails)
            {
                AmbientSoundCategories.LogScene();
            }
        }

        // read at each Play (FadeIn): a quiet category stays quiet for loops and one-shots alike
        [HarmonyPatch(typeof(BaseAmbientSoundPlayer), nameof(BaseAmbientSoundPlayer.GetBaseVolume))]
        [HarmonyPostfix]
        public static void BaseVolumePostfix(BaseAmbientSoundPlayer __instance, ref float __result)
        {
            AmbientCategory category = AmbientSoundCategories.Classify(__instance);
            if (category != AmbientCategory.None)
            {
                __result *= AmbientSoundCategories.GetGain(category);
            }
        }
    }
}
