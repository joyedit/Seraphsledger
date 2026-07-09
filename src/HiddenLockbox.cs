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

    // Tracks who placed each hidden lockbox, persisted in the savegame, and
    // syncs each owner's list to their client so the Page of Secrets HUD can
    // point at them. Always active server-side (even with the feature toggled
    // off) so the data stays correct across toggles; only crafting and the
    // client HUD are gated by the config switch.
    public class LockboxRegistry
    {
        internal static LockboxRegistry Instance;

        private const string DataKey = "seraphsledger-lockboxes";

        private readonly ICoreServerAPI sapi;
        private LockboxSaveData data = new LockboxSaveData();

        public LockboxRegistry(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            Instance = this;
            sapi.Event.SaveGameLoaded += OnLoad;
            sapi.Event.GameWorldSave += OnSave;
            sapi.Event.PlayerJoin += p => SyncTo(p);
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
            SyncOwner(ownerUid);
        }

        public void Unregister(BlockPos pos)
        {
            if (pos == null) return;
            string owner = null;
            data.Entries.RemoveAll(e =>
            {
                bool match = e.X == pos.X && e.Y == pos.Y && e.Z == pos.Z;
                if (match) owner = e.OwnerUid;
                return match;
            });
            if (owner != null) SyncOwner(owner);
        }

        private void SyncOwner(string ownerUid)
        {
            if (sapi.World.PlayerByUid(ownerUid) is IServerPlayer player)
            {
                SyncTo(player);
            }
        }

        public void SyncTo(IServerPlayer player)
        {
            var packed = new List<int>();
            foreach (var e in data.Entries)
            {
                if (e.OwnerUid != player.PlayerUID) continue;
                packed.Add(e.X);
                packed.Add(e.Y);
                packed.Add(e.Z);
            }
            sapi.Network.GetChannel(SeraphsLedgerModSystem.NetworkChannelName)
                .SendPacket(new LockboxListPacket { Positions = packed.ToArray() }, player);
        }

        public void Dispose()
        {
            Instance = null;
        }
    }
}
