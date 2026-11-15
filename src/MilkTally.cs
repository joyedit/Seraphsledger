using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // "Ready to milk" for the tally board, and the floating markers that go
    // with it.
    //
    // Vanilla's own gate is EntityBehaviorMilkable.CanMilk(), which is
    // protected, so the four conditions are mirrored here instead of called.
    // Three of them read public state - the milkable behaviour's presence, the
    // multiply behaviour's TotalDaysLastBirth, and the watched attribute
    // lastMilkedTotalHours - and all of it is synced to clients, which is what
    // lets the marker re-check itself on the client with no extra packets.
    //
    // The fourth, stressLevel > 0.1, is deliberately NOT mirrored. Stress is
    // transient: a cow you just walked through reads as un-milkable for a
    // minute or so, and on the board's 10-second beat that would make the
    // count flicker between 4 and 5 while a herd settles. The board answers
    // "which of these could I go and milk today", so it counts lactating and
    // not-milked-today and leaves stress to the moment you actually try.
    public static class MilkReady
    {
        // lactatingDaysAfterBirth is private on the behaviour and comes from
        // the entity's JSON (vanilla default 21), so it is read once per
        // entity type by reflection rather than assumed.
        private static readonly FieldInfo LactatingField =
            AccessTools.Field(typeof(EntityBehaviorMilkable), "lactatingDaysAfterBirth");
        private static readonly Dictionary<string, float> LactatingDaysByType =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private const float DefaultLactatingDays = 21f;

        public static bool IsReady(Entity entity)
        {
            if (entity == null || !entity.Alive) return false;

            var milkable = entity.GetBehavior<EntityBehaviorMilkable>();
            if (milkable == null) return false;

            // No multiply behaviour means it has never calved and vanilla's own
            // CanMilk bails out too.
            var multiply = entity.GetBehavior<EntityBehaviorMultiply>();
            if (multiply == null) return false;

            IGameCalendar calendar = entity.World?.Calendar;
            if (calendar == null) return false;

            double daysSinceBirth = Math.Max(0.0, calendar.TotalDays - multiply.TotalDaysLastBirth);
            if (daysSinceBirth >= LactatingDays(entity, milkable)) return false;

            double lastMilked = entity.WatchedAttributes.GetFloat("lastMilkedTotalHours", 0f);
            return calendar.TotalHours - lastMilked >= calendar.HoursPerDay;
        }

        private static float LactatingDays(Entity entity, EntityBehaviorMilkable milkable)
        {
            string type = entity.Code?.ToString() ?? "";
            if (LactatingDaysByType.TryGetValue(type, out float days)) return days;

            days = DefaultLactatingDays;
            try
            {
                if (LactatingField?.GetValue(milkable) is float f && f > 0f) days = f;
            }
            catch (Exception)
            {
                // A future engine version may rename or drop the field; the
                // vanilla default is a safe answer and the count stays useful.
            }
            LactatingDaysByType[type] = days;
            return days;
        }
    }

    // Server -> client: the animals a board counted as ready to milk, sent to
    // each player standing inside that board's pen. Ids rather than positions,
    // because the animals walk about between beats and the client follows them
    // by entity.
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class MilkMarkerPacket
    {
        public long[] EntityIds;
    }

    // The floating markers. A small milk-white plume over each ready animal
    // while the player is inside a board's pen.
    //
    // Why particles and not an outline: the engine has no API for outlining an
    // entity. Shaded highlights exist for blocks (World.HighlightBlocks) and
    // for nothing else, and a real silhouette means re-rendering the entity's
    // mesh with a flat shader through a render patch - fragile across engine
    // updates, for a cosmetic.
    //
    // The server sends the candidate set on its scan beat; this re-checks each
    // candidate against MilkReady every tick, so a cow you have just milked
    // loses its marker at once instead of at the next beat up to 10s later.
    public static class MilkMarkers
    {
        public static bool Enabled;

        private const int TickMs = 400;
        // One beat is 10s, so two missed beats stop the markers. Walking out of
        // the pen simply stops the packets.
        private const long StaleMs = 25000;

        private static ICoreClientAPI capi;
        private static long listenerId;
        private static long[] ids = Array.Empty<long>();
        private static long lastPacketMs;

        public static void Init(ICoreClientAPI api)
        {
            capi = api;
            if (listenerId == 0) listenerId = api.Event.RegisterGameTickListener(_ => OnTick(), TickMs);
        }

        public static void Dispose()
        {
            if (capi != null && listenerId != 0) capi.Event.UnregisterGameTickListener(listenerId);
            listenerId = 0;
            ids = Array.Empty<long>();
            capi = null;
        }

        public static void OnPacket(MilkMarkerPacket packet)
        {
            if (!Enabled || capi == null) return;
            ids = packet?.EntityIds ?? Array.Empty<long>();
            lastPacketMs = capi.World.ElapsedMilliseconds;
        }

        private static void OnTick()
        {
            if (!Enabled || capi == null || ids.Length == 0) return;
            if (capi.World.ElapsedMilliseconds - lastPacketMs > StaleMs)
            {
                ids = Array.Empty<long>();
                return;
            }

            foreach (long id in ids)
            {
                Entity entity = capi.World.GetEntityById(id);
                if (entity == null || !entity.Alive) continue;
                // Re-checked client-side: lastMilkedTotalHours is a watched
                // attribute, so milking one clears its marker immediately.
                if (!MilkReady.IsReady(entity)) continue;
                SpawnMark(entity);
            }
        }

        private static void SpawnMark(Entity entity)
        {
            Cuboidf box = entity.SelectionBox ?? entity.CollisionBox;
            double top = box?.Y2 ?? 1.2f;
            Vec3d at = entity.Pos.XYZ.Add(0, top + 0.35, 0);

            var props = new SimpleParticleProperties(
                1, 2,
                ColorUtil.ToRgba(190, 250, 250, 245),   // milk white, mostly opaque
                at.AddCopy(-0.12, 0, -0.12),
                at.AddCopy(0.12, 0.1, 0.12),
                new Vec3f(-0.02f, 0.14f, -0.02f),
                new Vec3f(0.02f, 0.26f, 0.02f),
                1.1f,                                   // lifetime, seconds
                -0.015f,                                // drifts up rather than falling
                0.25f, 0.45f,
                EnumParticleModel.Quad);
            props.ShouldDieInLiquid = false;
            props.SelfPropelled = true;

            capi.World.SpawnParticles(props);
        }
    }
}
