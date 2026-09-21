using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Animal tally board: a chalkboard hung inside a pen that counts the animals,
    // henbox eggs and loose eggs inside the fence, shown in its tooltip.
    //
    // The pen is found by flood-filling walkable ground outward from the board,
    // the way an animal could walk: fences, walls, closed gates and doors stop
    // it, open doorways let it through (so coops attached to the pen belong to
    // it). If the fill reaches the scan radius, the pen has a gap somewhere, and
    // the board falls back to counting everything within the radius and says
    // the pen is open.
    //
    // Scanning is server-side, every ScanIntervalMs while a player is nearby. The
    // result is stored in the block entity's tree and synced to clients, which
    // only format it. Animal codes are stored rather than names so each client
    // localizes them itself.
    public class BlockEntityTallyBoard : BlockEntity
    {
        // Set from the config at server start. Off leaves placed boards inert.
        internal static bool ScanEnabled = true;

        private const int Radius = 100;
        private const int VerticalRange = 24;
        private const int ScanIntervalMs = 10000;
        private const double PlayerRange = 64;

        // How high an animal can hop, and how far down it will step or drop.
        private const double JumpHeight = 1.05;
        private const int MaxDrop = 3;
        private const int WallDepth = 2;

        private bool scanned;
        private bool penClosed;
        private int penArea;
        private int eggsInBoxes;
        private int eggsFertile;
        private int eggsLoose;
        private int henboxes;
        private Dictionary<string, int> animals = new Dictionary<string, int>();

        private TagSetFast animalTag;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            if (api.Side != EnumAppSide.Server || !ScanEnabled) return;

            api.EntityTagRegistry.TryCreateTagSetAndLogIssues(out animalTag, "animal");
            // Every board ticks each second but only scans when the world clock
            // enters a new ScanIntervalMs window, so all boards count in the
            // same second - two boards in one pen show the same snapshot rather
            // than two moments up to 10s apart.
            RegisterGameTickListener(_ => ScanIfDue(), 1000);
            RegisterDelayedCallback(_ => Scan(), 500);
        }

        private long lastWindow = -1;

        private void ScanIfDue()
        {
            long window = Api.World.ElapsedMilliseconds / ScanIntervalMs;
            if (window == lastWindow) return;
            lastWindow = window;

            Vec3d center = Pos.ToVec3d().Add(0.5, 0.5, 0.5);
            foreach (IPlayer player in Api.World.AllOnlinePlayers)
            {
                if (player.Entity != null && player.Entity.Pos.SquareDistanceTo(center) < PlayerRange * PlayerRange)
                {
                    Scan();
                    return;
                }
            }
        }

        // --- the scan --------------------------------------------------------

        private void Scan()
        {
            PenFill pen = FillPen(false);

            penClosed = pen.Closed;
            penArea = pen.Closed ? pen.ColumnCount : 0;
            animals = new Dictionary<string, int>();
            eggsInBoxes = eggsFertile = eggsLoose = henboxes = 0;

            // Henboxes first: a hen sitting in one counts even when the box is
            // out of the walk's reach (a stacked row, a raised shelf).
            foreach (BlockEntityHenBox box in HenboxesInRange())
            {
                if (pen.Closed && !pen.TouchesBlock(box.Pos)) continue;
                henboxes++;
                pen.AddNest(box.Pos);
                InventoryBase inv = box.Inventory;
                for (int i = 0; i < inv.Count; i++)
                {
                    ItemStack egg = inv[i].Itemstack;
                    if (egg == null) continue;
                    eggsInBoxes += egg.StackSize;
                    if (egg.Attributes["chick"] != null) eggsFertile += egg.StackSize;
                }
            }

            Vec3d min = new Vec3d(Pos.X - Radius, Pos.Y - VerticalRange, Pos.Z - Radius);
            Vec3d max = new Vec3d(Pos.X + Radius + 1, Pos.Y + VerticalRange + 1, Pos.Z + Radius + 1);

            foreach (Entity entity in Api.World.GetEntitiesInsideCuboid(
                         new BlockPos((int)min.X, (int)min.Y, (int)min.Z),
                         new BlockPos((int)max.X, (int)max.Y, (int)max.Z)))
            {
                if (!InPen(pen, entity.Pos.XYZ)) continue;

                if (entity is EntityItem item)
                {
                    if (IsEgg(item.Itemstack)) eggsLoose += item.Itemstack.StackSize;
                    continue;
                }

                if (entity is EntityPlayer || !entity.Alive || !(entity is EntityAgent)) continue;
                if (!entity.Tags.Overlaps(animalTag)) continue;

                string code = entity.Code.ToString();
                animals.TryGetValue(code, out int n);
                animals[code] = n + 1;
            }

            scanned = true;
            MarkDirty();
        }

        // A board hung on the outside face of a wall would otherwise start its
        // walk outside the fence and report the whole countryside as an open
        // pen. So when the first fill escapes, try again from the far side of
        // the wall it hangs on, and keep that one if it closes.
        private PenFill FillPen(bool trackPath)
        {
            var pen = new PenFill(Api.World.BlockAccessor, Pos, Radius, MaxDrop, JumpHeight);
            pen.Run(0, 0, trackPath);
            if (pen.Closed) return pen;

            BlockFacing facing = BlockFacing.FromCode(Block?.Variant["side"]);
            if (facing == null) return pen;

            // The "side" variant points from the board into the wall it hangs
            // on (that is where HorizontalAttachable looks for its support), so
            // the far side lies that way: past one wall block for a fence or a
            // plain wall, past two for a thick one.
            for (int depth = 2; depth <= WallDepth + 1; depth++)
            {
                int bx = facing.Normali.X * depth, bz = facing.Normali.Z * depth;
                var behind = new PenFill(Api.World.BlockAccessor, Pos, Radius, MaxDrop, JumpHeight);
                behind.Run(bx, bz, trackPath);
                if (behind.Closed) return behind;
            }
            return pen;
        }

        // Right-clicking the board marks what it can see: the pen's outline when
        // it is closed, or the trail it followed out when it isn't - that trail
        // crosses the fence line exactly at the gap.
        private const int HighlightSlot = 9481;
        private const int HighlightSeconds = 25;

        public void ShowPen(IPlayer player)
        {
            if (!(player is IServerPlayer splayer)) return;

            PenFill pen = FillPen(true);
            List<BlockPos> marks;
            int color;
            string message;

            if (pen.Closed)
            {
                marks = pen.Outline(2500);
                color = ColorUtil.ColorFromRgba(90, 220, 120, 140);
                message = $"Pen closed: {pen.ColumnCount:N0} blocks, outline marked for {HighlightSeconds}s.";
            }
            else
            {
                marks = pen.EscapeRoute ?? new List<BlockPos>();
                color = ColorUtil.ColorFromRgba(60, 130, 255, 160);
                BlockPos end = marks.Count > 0 ? marks[marks.Count - 1] : Pos;
                message = marks.Count > 0
                    ? $"Pen is open. The marked trail is the way out, leaving the pen where it crosses your fence line, and ending at {end.X}, {end.Y}, {end.Z}."
                    : "Nowhere to stand under this board - hang it with ground beneath it.";
            }

            var colors = new List<int>();
            for (int i = 0; i < marks.Count; i++) colors.Add(color);
            Api.World.HighlightBlocks(player, HighlightSlot, marks, colors);
            splayer.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);

            RegisterDelayedCallback(
                _ => Api.World.HighlightBlocks(player, HighlightSlot, new List<BlockPos>()),
                HighlightSeconds * 1000);
        }

        // In a closed pen, only what stands on the filled ground (or sits in one
        // of its henboxes) counts, with a little slack upward for a bird
        // mid-flutter. An animal pressed against a fence has its centre over the
        // fence block - the fence's collision is a thin rail down the middle of
        // its block - so the footprint is sampled too: the centre and four
        // points FootprintReach out, which reaches back into the pen from
        // against a fence rail but not through a door panel into it. In an open
        // pen, the cuboid query already limited things to the radius.
        private const double FootprintReach = 0.2;

        private bool InPen(PenFill pen, Vec3d pos)
        {
            if (!pen.Closed)
            {
                return Math.Abs(Math.Floor(pos.X) - Pos.X) <= Radius
                    && Math.Abs(Math.Floor(pos.Z) - Pos.Z) <= Radius
                    && Math.Abs(pos.Y - Pos.Y) <= VerticalRange;
            }

            int y = (int)Math.Floor(pos.Y + 0.01);
            return InPenAt(pen, pos.X, y, pos.Z)
                || InPenAt(pen, pos.X + FootprintReach, y, pos.Z)
                || InPenAt(pen, pos.X - FootprintReach, y, pos.Z)
                || InPenAt(pen, pos.X, y, pos.Z + FootprintReach)
                || InPenAt(pen, pos.X, y, pos.Z - FootprintReach);
        }

        private bool InPenAt(PenFill pen, double x, int y, double z)
        {
            int dx = (int)Math.Floor(x) - Pos.X;
            int dz = (int)Math.Floor(z) - Pos.Z;
            return pen.Holds(dx, y, dz) || pen.Holds(dx, y - 1, dz) || pen.Holds(dx, y - 2, dz);
        }

        private static bool IsEgg(ItemStack stack)
        {
            string path = stack?.Collectible?.Code?.Path;
            return path != null && path.StartsWith("egg-", StringComparison.Ordinal);
        }

        // Block entities are indexed per chunk, so walking the chunks around the
        // board is far cheaper than probing every block in the radius.
        private IEnumerable<BlockEntityHenBox> HenboxesInRange()
        {
            IBlockAccessor ba = Api.World.BlockAccessor;
            int size = GlobalConstants.ChunkSize;
            for (int cx = (Pos.X - Radius) / size; cx <= (Pos.X + Radius) / size; cx++)
            for (int cz = (Pos.Z - Radius) / size; cz <= (Pos.Z + Radius) / size; cz++)
            for (int cy = Math.Max(0, Pos.Y - VerticalRange) / size; cy <= (Pos.Y + VerticalRange) / size; cy++)
            {
                IWorldChunk chunk = ba.GetChunk(cx, cy, cz);
                if (chunk?.BlockEntities == null) continue;
                foreach (BlockEntity be in chunk.BlockEntities.Values)
                {
                    if (!(be is BlockEntityHenBox box)) continue;
                    if (Math.Abs(box.Pos.X - Pos.X) > Radius || Math.Abs(box.Pos.Z - Pos.Z) > Radius) continue;
                    if (Math.Abs(box.Pos.Y - Pos.Y) > VerticalRange) continue;
                    yield return box;
                }
            }
        }

        // --- sync + tooltip --------------------------------------------------

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            var t = new TreeAttribute();
            t.SetBool("scanned", scanned);
            t.SetBool("closed", penClosed);
            t.SetInt("area", penArea);
            t.SetInt("eggsBox", eggsInBoxes);
            t.SetInt("eggsFertile", eggsFertile);
            t.SetInt("eggsLoose", eggsLoose);
            t.SetInt("henboxes", henboxes);
            var a = new TreeAttribute();
            foreach (var kv in animals) a.SetInt(kv.Key, kv.Value);
            t["animals"] = a;
            tree["tally"] = t;
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            if (!(tree["tally"] is ITreeAttribute t)) return;
            scanned = t.GetBool("scanned");
            penClosed = t.GetBool("closed");
            penArea = t.GetInt("area");
            eggsInBoxes = t.GetInt("eggsBox");
            eggsFertile = t.GetInt("eggsFertile");
            eggsLoose = t.GetInt("eggsLoose");
            henboxes = t.GetInt("henboxes");
            animals = new Dictionary<string, int>();
            if (t["animals"] is ITreeAttribute a)
            {
                foreach (var kv in a) animals[kv.Key] = a.GetInt(kv.Key);
            }
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            if (!ScanEnabled)
            {
                dsc.AppendLine("Turned off in the Seraph's Ledger settings (Ctrl+Shift+L).");
                return;
            }
            if (!scanned)
            {
                dsc.AppendLine("Counting...");
                return;
            }

            dsc.AppendLine(penClosed
                ? $"Pen closed ({penArea:N0} blocks)"
                : $"<font color=\"#e0a060\">Pen open - a gap or open gate somewhere. Counting everything within {Radius} blocks.</font>");

            if (animals.Count == 0)
            {
                dsc.AppendLine("No animals");
            }

            // Group by species (the first part of the entity code: chicken, pig,
            // goat), biggest group first, and list each kind within it.
            foreach (var species in animals
                         .GroupBy(kv => SpeciesOf(kv.Key))
                         .OrderByDescending(g => g.Sum(kv => kv.Value)))
            {
                dsc.AppendLine($"{SpeciesName(species.Key)}: {species.Sum(kv => kv.Value)}");
                dsc.AppendLine("  " + string.Join(" · ", species
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{KindName(kv.Key)} {kv.Value}")));
            }

            if (henboxes > 0 || eggsLoose > 0)
            {
                string boxed = eggsFertile > 0
                    ? $"{eggsInBoxes} in henboxes ({eggsFertile} fertile)"
                    : $"{eggsInBoxes} in henboxes";
                dsc.AppendLine($"Eggs: {boxed} · {eggsLoose} loose");
            }
        }

        private static string SpeciesOf(string code)
        {
            var loc = new AssetLocation(code);
            return loc.Domain + ":" + loc.FirstCodePart();
        }

        private static string SpeciesName(string species)
        {
            var loc = new AssetLocation(species);
            string name = Lang.GetIfExists(loc.Domain + ":creaturegroup-" + loc.Path);
            if (name != null) return name;
            return loc.Path.Length > 0 ? char.ToUpperInvariant(loc.Path[0]) + loc.Path.Substring(1) : loc.Path;
        }

        private static string KindName(string code)
        {
            var loc = new AssetLocation(code);
            return Lang.GetIfExists(loc.Domain + ":item-creature-" + loc.Path) ?? loc.Path;
        }
    }

    // The board itself: right-click marks the pen it is counting.
    public class BlockTallyBoard : Block
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.Side == EnumAppSide.Server
                && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityTallyBoard board)
            {
                board.ShowPen(byPlayer);
            }
            return true;
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return new[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "seraphsledger:blockhelp-tallyboard-showpen",
                    MouseButton = EnumMouseButton.Right
                }
            }.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
        }
    }

    // Flood fill of the ground an animal could walk on, starting at the board.
    //
    // A cell is a block position whose space an animal's feet occupy: either a
    // low block it stands on top of within the same block (slab, farmland,
    // henbox), or an empty block above a full-height one. Moving to a
    // neighbouring column picks the highest reachable cell there (a hop of up to
    // JumpHeight, a drop of up to MaxDrop), and is refused if a collision box in
    // the strip between the two block centres stands taller than the animal can
    // hop. That strip test is what makes a closed door or gate (a thin panel at
    // a block edge) a wall, while an open door (swung against the side of the
    // block, out of the strip) is a doorway.
    internal class PenFill
    {
        private readonly IBlockAccessor ba;
        private readonly BlockPos origin;
        private readonly int radius;
        private readonly int maxDrop;
        private readonly double jump;

        private readonly HashSet<long> cells = new HashSet<long>();
        private readonly HashSet<long> columns = new HashSet<long>();
        private readonly Dictionary<long, Cuboidf[]> boxCache = new Dictionary<long, Cuboidf[]>();
        private readonly BlockPos tmp = new BlockPos(0);

        public bool Closed { get; private set; }
        public int ColumnCount => columns.Count;

        private static readonly int[] DirX = { 1, -1, 0, 0 };
        private static readonly int[] DirZ = { 0, 0, 1, -1 };

        public PenFill(IBlockAccessor ba, BlockPos origin, int radius, int maxDrop, double jump)
        {
            this.ba = ba;
            this.origin = origin.Copy();
            this.radius = radius;
            this.maxDrop = maxDrop;
            this.jump = jump;
        }

        private readonly HashSet<long> nests = new HashSet<long>();
        private Dictionary<long, long> parents;

        // Set when a tracked fill escapes: the route from the board out.
        public List<BlockPos> EscapeRoute { get; private set; }

        public bool Contains(int dx, int y, int dz) => cells.Contains(Key(dx, y, dz));

        // Walkable ground, or a henbox belonging to the pen (and the space on
        // top of it).
        public bool Holds(int dx, int y, int dz)
        {
            long key = Key(dx, y, dz);
            return cells.Contains(key) || nests.Contains(key);
        }

        public void AddNest(BlockPos pos)
        {
            int dx = pos.X - origin.X, dz = pos.Z - origin.Z;
            nests.Add(Key(dx, pos.Y, dz));
            nests.Add(Key(dx, pos.Y + 1, dz));
        }

        // A henbox belongs to the pen if an animal can stand in it, on it, or
        // right next to it at the same level.
        public bool TouchesBlock(BlockPos pos)
        {
            int dx = pos.X - origin.X, dz = pos.Z - origin.Z;
            if (Contains(dx, pos.Y, dz) || Contains(dx, pos.Y + 1, dz)) return true;
            for (int d = 0; d < 4; d++)
            {
                if (Contains(dx + DirX[d], pos.Y, dz + DirZ[d])) return true;
            }
            return false;
        }

        // Fills from the ground under (startDx, startDz). Returns false if there
        // is nowhere to stand there at all.
        public bool Run(int startDx = 0, int startDz = 0, bool trackPath = false)
        {
            cells.Clear();
            columns.Clear();
            parents?.Clear();
            EscapeRoute = null;
            parents = trackPath ? new Dictionary<long, long>() : null;

            // The board hangs at any height on a wall; drop down to the ground
            // beneath it to start.
            int startY = int.MinValue;
            for (int y = origin.Y; y >= origin.Y - 6; y--)
            {
                if (!double.IsNaN(Feet(startDx, y, startDz))) { startY = y; break; }
            }
            if (startY == int.MinValue)
            {
                Closed = false;
                return false;
            }

            var queue = new Queue<(int dx, int y, int dz)>();
            long startKey = Key(startDx, startY, startDz);
            Add(startDx, startY, startDz, queue, startKey);
            Closed = true;

            while (queue.Count > 0)
            {
                var (dx, y, dz) = queue.Dequeue();
                double feet = Feet(dx, y, dz);

                for (int d = 0; d < 4; d++)
                {
                    int nx = dx + DirX[d], nz = dz + DirZ[d];
                    int ny = Step(dx, y, dz, feet, d);
                    if (ny == int.MinValue) continue;

                    // Walked out to the edge of the scan area: the pen is open.
                    if (Math.Abs(nx) >= radius || Math.Abs(nz) >= radius)
                    {
                        Closed = false;
                        if (parents != null) EscapeRoute = RouteTo(Key(dx, y, dz), startKey);
                        return true;
                    }
                    Add(nx, ny, nz, queue, Key(dx, y, dz));
                }
            }
            return true;
        }

        private void Add(int dx, int y, int dz, Queue<(int, int, int)> queue, long from)
        {
            long key = Key(dx, y, dz);
            if (cells.Add(key))
            {
                columns.Add(Key(dx, 0, dz));
                if (parents != null) parents[key] = from;
                queue.Enqueue((dx, y, dz));
            }
        }

        // The breadcrumb trail from the board to wherever the fill got out - it
        // crosses the fence line at the gap.
        private List<BlockPos> RouteTo(long key, long startKey)
        {
            var route = new List<BlockPos>();
            for (int guard = 0; guard < 4096; guard++)
            {
                route.Add(Unkey(key));
                if (key == startKey || !parents.TryGetValue(key, out long next) || next == key) break;
                key = next;
            }
            route.Reverse();
            return route;
        }

        // Cells with a side that isn't part of the pen: the pen's outline.
        public List<BlockPos> Outline(int cap)
        {
            var outline = new List<BlockPos>();
            foreach (long key in cells)
            {
                BlockPos pos = Unkey(key);
                int dx = pos.X - origin.X, dz = pos.Z - origin.Z;
                for (int d = 0; d < 4; d++)
                {
                    if (ColumnHeld(dx + DirX[d], dz + DirZ[d])) continue;
                    outline.Add(pos);
                    break;
                }
                if (outline.Count >= cap) break;
            }
            return outline;
        }

        private bool ColumnHeld(int dx, int dz) => columns.Contains(Key(dx, 0, dz));

        // The cell an animal at feet height `feet` in (dx,y,dz) reaches by
        // stepping in direction d, or int.MinValue if it can't.
        private int Step(int dx, int y, int dz, double feet, int d)
        {
            int nx = dx + DirX[d], nz = dz + DirZ[d];
            for (int ny = y + 1; ny >= y - maxDrop; ny--)
            {
                double nfeet = Feet(nx, ny, nz);
                if (double.IsNaN(nfeet)) continue;
                if (nfeet > feet + jump) return int.MinValue;

                // Anything in the crossing strip taller than a hop blocks it:
                // this cell's half facing d, and the neighbour's half facing
                // back. Two block layers each, to catch a door's upper half.
                if (BlocksCrossing(dx, y, dz, d, false, feet)
                    || BlocksCrossing(dx, y + 1, dz, d, false, feet)
                    || BlocksCrossing(nx, ny, nz, d, true, feet)
                    || BlocksCrossing(nx, ny + 1, nz, d, true, feet))
                {
                    return int.MinValue;
                }
                return ny;
            }
            return int.MinValue;
        }

        private bool BlocksCrossing(int dx, int y, int dz, int d, bool entering, double feet)
        {
            Cuboidf[] boxes = Boxes(dx, y, dz);
            if (boxes == null) return false;

            // The strip runs through the middle of the block along the
            // movement axis; `entering` means we cover the half we come in by.
            bool alongX = DirX[d] != 0;
            int sign = alongX ? DirX[d] : DirZ[d];
            bool farHalf = entering ? sign < 0 : sign > 0;
            float lo = farHalf ? 0.5f : 0f;
            float hi = farHalf ? 1f : 0.5f;

            foreach (Cuboidf box in boxes)
            {
                float a0 = alongX ? box.X1 : box.Z1, a1 = alongX ? box.X2 : box.Z2;
                float c0 = alongX ? box.Z1 : box.X1, c1 = alongX ? box.Z2 : box.X2;
                if (a1 <= lo || a0 >= hi) continue;
                if (c1 <= 0.3f || c0 >= 0.7f) continue;

                double top = y + box.Y2, bottom = y + box.Y1;
                if (top > feet + jump && bottom < feet + 0.5) return true;
            }
            return false;
        }

        // Feet height of an animal standing in (dx,y,dz), or NaN if it can't
        // stand there. Only boxes under the block's centre count as floor, so
        // a door panel or fence arm at the edge doesn't make a floor.
        private double Feet(int dx, int y, int dz)
        {
            double own = CenterTop(dx, y, dz);
            if (double.IsNaN(own)) return double.NaN; // unloaded
            if (own >= 1) return double.NaN;          // solid, or a fence post
            if (own > 0) return y + own;              // slab, farmland, henbox

            tmp.Set(origin.X + dx, y - 1, origin.Z + dz);
            if (ba.GetBlock(tmp, BlockLayersAccess.Fluid)?.IsLiquid() == true) return y;

            double below = CenterTop(dx, y - 1, dz);
            if (double.IsNaN(below) || below < 1) return double.NaN;
            return y - 1 + below;
        }

        private double CenterTop(int dx, int y, int dz)
        {
            tmp.Set(origin.X + dx, y, origin.Z + dz);
            if (ba.GetChunkAtBlockPos(tmp) == null) return double.NaN;

            Cuboidf[] boxes = Boxes(dx, y, dz);
            if (boxes == null) return 0;
            double top = 0;
            foreach (Cuboidf box in boxes)
            {
                if (box.X1 < 0.6f && box.X2 > 0.4f && box.Z1 < 0.6f && box.Z2 > 0.4f)
                {
                    top = Math.Max(top, box.Y2);
                }
            }
            return top;
        }

        private Cuboidf[] Boxes(int dx, int y, int dz)
        {
            long key = Key(dx, y, dz);
            if (boxCache.TryGetValue(key, out Cuboidf[] boxes)) return boxes;
            tmp.Set(origin.X + dx, y, origin.Z + dz);
            Block block = ba.GetBlock(tmp);
            boxes = block?.GetCollisionBoxes(ba, tmp);
            if (boxes != null && boxes.Length == 0) boxes = null;
            boxCache[key] = boxes;
            return boxes;
        }

        private static long Key(int dx, int y, int dz)
            => ((long)(dx + 1024) << 40) | ((long)(dz + 1024) << 20) | (long)(y & 0xFFFFF);

        private BlockPos Unkey(long key) => new BlockPos(
            origin.X + (int)((key >> 40) & 0xFFFFF) - 1024,
            (int)(key & 0xFFFFF),
            origin.Z + (int)((key >> 20) & 0xFFFFF) - 1024);
    }
}
