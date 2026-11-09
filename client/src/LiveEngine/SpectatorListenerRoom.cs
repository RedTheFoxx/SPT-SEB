// The game takes the listener's room from the triggers around the local player's body, while
// the listener itself follows the camera. Spectator cameras (Fika after an extraction or a death)
// break that: Fika moves an extracted body 500 m up, the listener ends in no room and the game
// stops computing occlusion for the rest of the raid. A dead body stays in its last room.
// While the camera is away from the body, the listener's room is the one around the camera.
using System;
using System.Collections.Generic;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using EFT;
using EFT.CameraControl;
using EFT.GlobalEvents;
using EFT.GlobalEvents.AudioEvents;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    internal static class SpectatorListenerRoom
    {
        private const float RefreshInterval = 0.2f;

        private static readonly AccessTools.FieldRef<SpatialAudioSystem, AudioRoomStorage> StorageRef =
            AccessTools.FieldRefAccess<SpatialAudioSystem, AudioRoomStorage>("_audioRoomStorage");

        internal static bool Enabled = true;

        // the rooms the body is in, kept up to date while the camera is away from it
        private static readonly List<ISpatialAudioRoom> BodyIndoorRooms = new List<ISpatialAudioRoom>();
        private static readonly List<ISpatialAudioRoom> BodyOutdoorRooms = new List<ISpatialAudioRoom>();

        // not null while the listener's rooms are driven from here
        private static AudioRoomStorage _drivenStorage;
        private static float _nextRefresh;
        private static AudioSourceContainer _followedContainer;

        // true during the occlusion update of the followed player only
        internal static bool SuppressScores;

        public static void Tick(SpatialAudioSystem system)
        {
            AudioRoomStorage storage = StorageRef(system);
            if (storage == null)
            {
                return;
            }
            if (_drivenStorage != null && _drivenStorage != storage)
            {
                // a new raid: the rooms kept belong to the previous map
                _drivenStorage = null;
            }
            if (!Enabled || !IsCameraAwayFromBody())
            {
                _followedContainer = null;
                if (_drivenStorage != null)
                {
                    GiveBackToBody();
                }
                return;
            }
            if (Time.unscaledTime < _nextRefresh)
            {
                return;
            }
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Transform listener = system.ListenerTransform;
            Player followed = FindFollowedPlayer();
            _followedContainer = null;
            if (followed != null && system.TryGetPlayerAudioContainer(followed.Id, out AudioSourceContainer container))
            {
                _followedContainer = container;
            }
            if (listener != null)
            {
                FollowCamera(storage, listener.position, followed);
            }
        }

        /// <summary>Before the game updates the occlusion of a source container. The rays from a
        /// camera attached to a player start right behind that player and can hit whatever is
        /// around (the game then muffles a player standing next to the listener), so that
        /// player's own sounds are treated as unobstructed. Other players are left alone.</summary>
        public static void BeforeOcclusionUpdate(CombinedSpatialAudioOccluder occluder, AudioSourceContainer container)
        {
            SuppressScores = _followedContainer != null && ReferenceEquals(container, _followedContainer);
            if (SuppressScores)
            {
                occluder._smoothedPropagationEffect = 0f;
                occluder._smoothedObstructionEffect = 0f;
            }
        }

        /// <summary>Patch of Add/RemoveListenerCurrentRoom: true when the call was consumed. The
        /// triggers of the body only update what is given back to it later.</summary>
        public static bool TryInterceptBodyRoom(AudioRoomStorage storage, SpatialAudioRoom room, bool entered)
        {
            if (storage != _drivenStorage)
            {
                return false;
            }
            List<ISpatialAudioRoom> rooms = room.IsOutdoor ? BodyOutdoorRooms : BodyIndoorRooms;
            rooms.Remove(room);
            if (entered)
            {
                rooms.Insert(0, room);
            }
            return true;
        }

        /// <summary>Fika sets this on the local player for its spectator and free cameras.</summary>
        private static bool IsCameraAwayFromBody()
        {
            Player player = GamePlayerOwner.MyPlayer;
            return player != null
                && !(player is HideoutPlayer)
                && player.PlayerBody != null
                && player.PointOfView != EPointOfView.FirstPerson;
        }

        private static Player FindFollowedPlayer()
        {
            Camera camera = CameraManager.Instance.Camera;
            Transform parent = camera != null ? camera.transform.parent : null;
            return parent != null ? parent.GetComponentInParent<Player>() : null;
        }

        private static void FollowCamera(AudioRoomStorage storage, Vector3 position, Player followed)
        {
            ISpatialAudioRoom room = FindListenerRoom(storage, position, followed, out bool followsPlayer);
            // between two rooms, or the listener not placed yet: the last known room stays
            if (room == null || !room.IsValid)
            {
                return;
            }
            if (_drivenStorage == null)
            {
                BodyIndoorRooms.Clear();
                BodyIndoorRooms.AddRange(storage.list_3);
                BodyOutdoorRooms.Clear();
                BodyOutdoorRooms.AddRange(storage.list_4);
                _drivenStorage = storage;
            }
            else if (storage.ListenerCurrentRoomID == room.ID)
            {
                return;
            }
            storage.list_3.Clear();
            storage.list_4.Clear();
            if (room.IsOutdoor)
            {
                storage.list_4.Add(room);
            }
            else
            {
                storage.list_3.Add(room);
                ISpatialAudioRoom outdoor = storage.FindProposedRoom(storage._outdoorAudioRooms, position);
                if (outdoor != null && outdoor.IsValid)
                {
                    storage.list_4.Add(outdoor);
                }
            }
            Notify(storage, EPlayerRoomInteractionState.Enter);
            if (PluginConfig.LogShotDetails)
            {
                Plugin.Log.LogWarning($"[spectator-room] listener in room {room.ID} ({room.Type}) followsPlayer={followsPlayer}");
            }
        }

        /// <summary>A camera attached to a player shares that player's room: the room found
        /// from its own position can differ (overlapping rooms, camera behind a wall), and the
        /// game then occludes the sounds of a player standing next to the listener.</summary>
        private static ISpatialAudioRoom FindListenerRoom(AudioRoomStorage storage, Vector3 position, Player followed, out bool followsPlayer)
        {
            if (followed != null)
            {
                ISpatialAudioRoom playerRoom = storage.GetOtherPlayerCurrentRoom(followed.Id);
                if (playerRoom != null && playerRoom.IsValid)
                {
                    followsPlayer = true;
                    return playerRoom;
                }
            }
            followsPlayer = false;
            return storage.FindInitialRoomOptimized(position);
        }

        private static void GiveBackToBody()
        {
            AudioRoomStorage storage = _drivenStorage;
            _drivenStorage = null;
            storage.list_3.Clear();
            storage.list_3.AddRange(BodyIndoorRooms);
            storage.list_4.Clear();
            storage.list_4.AddRange(BodyOutdoorRooms);
            Notify(storage, EPlayerRoomInteractionState.Exit);
        }

        /// <summary>The same update as AudioRoomStorage.Add/RemoveListenerCurrentRoom: the
        /// ambience and the room tone follow this event.</summary>
        private static void Notify(AudioRoomStorage storage, EPlayerRoomInteractionState state)
        {
            ISpatialAudioRoom room = storage.ListenerCurrentRoom;
            storage._currentPlayerRoomTypeMask = room.Type;
            GlobalEventsController.CreateEvent<MyPlayerRoomChangedEvent>().Invoke(
                room, storage.ListenerCurrentRoomID, storage._currentPlayerRoomTypeMask, storage.ListenerCurrentOutdoorRoomID, state);
        }
    }

    [HarmonyPatch]
    public static class SpectatorListenerPatches
    {
        [HarmonyPatch(typeof(SpatialAudioSystem), "Update")]
        [HarmonyPrefix]
        public static void UpdatePrefix(SpatialAudioSystem __instance)
        {
            try
            {
                SpectatorListenerRoom.Tick(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"spectator listener room failed, turned off: {e}");
                SpectatorListenerRoom.Enabled = false;
            }
        }

        [HarmonyPatch(typeof(CombinedSpatialAudioOccluder), "OnUpdateOcclusionEffects")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static void OcclusionUpdatePrefix(CombinedSpatialAudioOccluder __instance, AudioSourceContainer sourceContainer)
        {
            SpectatorListenerRoom.BeforeOcclusionUpdate(__instance, sourceContainer);
        }

        [HarmonyPatch(typeof(CombinedSpatialAudioOccluder), "OnUpdateOcclusionEffects")]
        [HarmonyPostfix]
        public static void OcclusionUpdatePostfix()
        {
            SpectatorListenerRoom.SuppressScores = false;
        }

        [HarmonyPatch(typeof(SpatialAudioOcclusionCombinedCalculator), "GetScore")]
        [HarmonyPrefix]
        public static bool ObstructionScorePrefix(ref float __result) => KeepScore(ref __result);

        [HarmonyPatch(typeof(ParallelSoundPropagationCalculator), "GetScore")]
        [HarmonyPrefix]
        public static bool PropagationScorePrefix(ref float __result) => KeepScore(ref __result);

        private static bool KeepScore(ref float result)
        {
            if (!SpectatorListenerRoom.SuppressScores)
            {
                return true;
            }
            result = 0f;
            return false;
        }

        [HarmonyPatch(typeof(AudioRoomStorage), "AddListenerCurrentRoom")]
        [HarmonyPrefix]
        public static bool AddListenerRoomPrefix(AudioRoomStorage __instance, SpatialAudioRoom room)
        {
            return !SpectatorListenerRoom.TryInterceptBodyRoom(__instance, room, true);
        }

        [HarmonyPatch(typeof(AudioRoomStorage), "RemoveListenerCurrentRoom")]
        [HarmonyPrefix]
        public static bool RemoveListenerRoomPrefix(AudioRoomStorage __instance, SpatialAudioRoom room)
        {
            return !SpectatorListenerRoom.TryInterceptBodyRoom(__instance, room, false);
        }
    }
}
