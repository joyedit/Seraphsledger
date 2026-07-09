using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Server -> owning client: the full list of that player's hidden lockbox
    // positions, packed as x,y,z triples. Sent on join and whenever the list
    // changes; the client keeps only the latest copy (see SecretsPageHud).
    [ProtoContract]
    public class LockboxListPacket
    {
        [ProtoMember(1)]
        public int[] Positions;
    }

    // A container disguised as a cobblestone block. It reuses the whole generic
    // typed container machinery (inventory, lid animation, open/close sounds,
    // Lockable) but lies about everything a bystander could see:
    //   - the block info HUD reports the matching vanilla cobblestone name and
    //     no contents/"Carried contents" lines,
    //   - there is no mouse-over interaction help,
    //   - it only opens on sneak + right-click with nothing placeable in hand;
    //     a plain right-click behaves exactly like clicking real cobblestone
    //     (a held block simply gets placed against it).
    // Visually the only tell is a hairline seam around the drawer; opening it
    // slides the small stone section out (the "lidopen" animation in the shape).
    public class BlockHiddenLockbox : BlockGenericTypedContainer
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byPlayer?.Entity?.Controls?.ShiftKey != true) return false;
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack)
        {
            bool placed = base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack);
            if (!placed) return false;

            // The base class rotates container meshes toward the placer in 22.5°
            // steps; anything but quarter turns would out a "cobblestone" cube at
            // a glance, so snap to the nearest 90°.
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityGenericTypedContainer be)
            {
                be.MeshAngle = GameMath.PIHALF * (float)Math.Round(be.MeshAngle / GameMath.PIHALF);
                be.MarkDirty(true);
            }

            if (world.Side == EnumAppSide.Server)
            {
                LockboxRegistry.Instance?.Register(byPlayer?.PlayerUID, blockSel.Position);
            }
            return true;
        }

        public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
        {
            base.OnBlockRemoved(world, pos);
            if (world.Side == EnumAppSide.Server)
            {
                LockboxRegistry.Instance?.Unregister(pos);
            }
        }

        // ---- The disguise: report ourselves as plain cobblestone ---------------

        public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
        {
            return Lang.GetMatching("game:block-cobblestone-" + Variant["rock"]);
        }

        // No contents summary, no "has a lock" hint - cobblestone has nothing to say.
        public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        {
            return "";
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return Array.Empty<WorldInteraction>();
        }
    }

    // ---- Server-side ownership registry ------------------------------------

    [ProtoContract]
    public class LockboxSaveEntry
    {
        [ProtoMember(1)] public string OwnerUid;
        [ProtoMember(2)] public int X;
        [ProtoMember(3)] public int Y;
        [ProtoMember(4)] public int Z;
    }

    [ProtoContract]
    public class LockboxSaveData
    {
        [ProtoMember(1)] public List<LockboxSaveEntry> Entries = new List<LockboxSaveEntry>();
    }

    // Tracks who placed each hidden lockbox, persisted in the savegame. Always
    // active server-side (even with the feature toggled off) so the data stays
    // correct across toggles; only crafting and the label sync are gated.
    //
    // Reveal model: the server watches each player's active hotbar slot. While
    // a player holds a Page of Secrets, they get sent the positions of the
    // PAGE OWNER's lockboxes (not necessarily their own - stolen pages work)
    // that lie within SyncRangeBlocks of them, re-checked about once a second.
    // The client renders those as floating labels. Distant stashes are never
    // sent over the wire, so a modified client can't dump someone's whole
    // stash network - it only ever learns about boxes it is already near.
    public class LockboxRegistry
    {
        internal static LockboxRegistry Instance;

        private const string DataKey = "seraphsledger-lockboxes";
        private const int CheckIntervalMs = 900;
        public const double SyncRangeBlocks = 30;

        private readonly ICoreServerAPI sapi;
        private LockboxSaveData data = new LockboxSaveData();
        private long listenerId;

        // Last packet content sent per player uid, so unchanged lists aren't resent.
        private readonly Dictionary<string, string> lastSentByPlayer = new Dictionary<string, string>();

        public LockboxRegistry(ICoreServerAPI sapi, bool syncEnabled)
        {
            this.sapi = sapi;
            Instance = this;
            sapi.Event.SaveGameLoaded += OnLoad;
            sapi.Event.GameWorldSave += OnSave;
            sapi.Event.PlayerDisconnect += p => lastSentByPlayer.Remove(p.PlayerUID);

            if (syncEnabled)
            {
                listenerId = sapi.Event.RegisterGameTickListener(OnCheckHolders, CheckIntervalMs);
            }
        }

        private void OnLoad()
        {
            try
            {
                byte[] bytes = sapi.WorldManager.SaveGame.GetData(DataKey);
                if (bytes != null) data = SerializerUtil.Deserialize<LockboxSaveData>(bytes);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[seraphsledger] lockbox registry load failed: {0}", e.Message);
            }
            if (data == null) data = new LockboxSaveData();
            if (data.Entries == null) data.Entries = new List<LockboxSaveEntry>();
        }

        private void OnSave()
        {
            sapi.WorldManager.SaveGame.StoreData(DataKey, SerializerUtil.Serialize(data));
        }

        public void Register(string ownerUid, BlockPos pos)
        {
            if (ownerUid == null || pos == null) return;
            data.Entries.RemoveAll(e => e.X == pos.X && e.Y == pos.Y && e.Z == pos.Z);
            data.Entries.Add(new LockboxSaveEntry { OwnerUid = ownerUid, X = pos.X, Y = pos.Y, Z = pos.Z });
        }

        public void Unregister(BlockPos pos)
        {
            if (pos == null) return;
            data.Entries.RemoveAll(e => e.X == pos.X && e.Y == pos.Y && e.Z == pos.Z);
        }

        // The once-a-second reveal check for every online player.
        private void OnCheckHolders(float dt)
        {
            var channel = sapi.Network.GetChannel(SeraphsLedgerModSystem.NetworkChannelName);

            foreach (IPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (!(p is IServerPlayer player)) continue;
                if (player.ConnectionState != EnumClientState.Playing) continue;

                var packed = new List<int>();
                ItemSlot slot = player.InventoryManager?.ActiveHotbarSlot;
                ItemStack stack = slot?.Itemstack;

                if (stack?.Collectible is ItemSecretsPage)
                {
                    string ownerUid = ItemSecretsPage.OwnerUid(stack);

                    // Pages from before the seal existed bind to whoever holds
                    // them first.
                    if (ownerUid == null)
                    {
                        ItemSecretsPage.Bind(stack, player);
                        slot.MarkDirty();
                        ownerUid = player.PlayerUID;
                    }

                    var plrPos = player.Entity.Pos;
                    double rangeSq = SyncRangeBlocks * SyncRangeBlocks;
                    foreach (var e in data.Entries)
                    {
                        if (e.OwnerUid != ownerUid) continue;
                        double dx = e.X + 0.5 - plrPos.X;
                        double dy = e.Y + 0.5 - plrPos.Y;
                        double dz = e.Z + 0.5 - plrPos.Z;
                        if (dx * dx + dy * dy + dz * dz > rangeSq) continue;
                        packed.Add(e.X);
                        packed.Add(e.Y);
                        packed.Add(e.Z);
                    }
                }

                // Only send when the visible set actually changed (covers the
                // "stopped holding the page" case with an empty list).
                string signature = string.Join(",", packed);
                if (lastSentByPlayer.TryGetValue(player.PlayerUID, out string prev) && prev == signature) continue;
                lastSentByPlayer[player.PlayerUID] = signature;

                channel.SendPacket(new LockboxListPacket { Positions = packed.ToArray() }, player);
            }
        }

        public void Dispose()
        {
            if (listenerId != 0) sapi.Event.UnregisterGameTickListener(listenerId);
            Instance = null;
        }
    }
}
