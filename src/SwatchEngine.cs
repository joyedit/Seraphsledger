using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphsLedger
{
    /// <summary>
    /// Works out which material substitutions are possible for a set of blocks,
    /// by treating a block code as hyphen-separated tokens and asking the block
    /// registry which single-token changes land on a block that actually exists.
    ///
    /// "cobblestone-andesite" and "stonebrickstairs-andesite-north-free" both
    /// carry the token "andesite"; swapping it for "chalk" hits
    /// "cobblestone-chalk" and "stonebrickstairs-chalk-north-free", so every
    /// shape of a stone moves together and stair facings are preserved. Nothing
    /// here is hardcoded to vanilla, so modded stone and wood are picked up for
    /// free - if the mod registers the block, it shows up as a choice.
    /// </summary>
    public class SwapEngine
    {
        readonly ICoreServerAPI sapi;

        /// <summary>Every registered block code, pre-split and bucketed by
        /// (domain, token count) - only blocks in the same bucket can be one
        /// token away from each other.</summary>
        Dictionary<(string domain, int len), List<(string[] parts, int id)>> buckets;

        /// <summary>Tokens that describe orientation or state rather than a
        /// material. Swapping these is never what someone means by "show me this
        /// wall in chalk", and they would otherwise crowd out the real choices.
        /// </summary>
        static readonly HashSet<string> StateTokens = new()
        {
            "north", "east", "south", "west", "up", "down", "left", "right",
            "horizontal", "vertical", "ns", "we", "ud", "free", "normal",
            "opened", "closed", "open", "close", "on", "off", "lit", "extinct",
            "empty", "full", "top", "bottom", "side", "front", "back", "center",
            "flipped", "inverted", "upside", "corner", "straight", "cross"
        };

        /// <summary>Kinds of block (the first token of a code) that the world
        /// generates rather than a player placing them. The creative-tab and
        /// material tests in <see cref="PlayerPlaced"/> do the real work; this
        /// only names the handful of vanilla terrain blocks those two can't see
        /// through - raw rock sits in the construction tab right next to
        /// cobblestone, and ground scatter declares neither tab nor material.
        /// </summary>
        static readonly HashSet<string> NaturalKinds = new()
        {
            "rock", "crackedrock", "stalagsection", "wildbeehive",
            "loosestones", "looseboulders", "looserubble", "looseores",
            "looseflints", "loosestick", "loosegears"
        };

        /// <summary>Tokens that mark a variant as world-grown: a log is
        /// "log-grown-oak-ud" on the tree and "log-placed-oak-ud" in a wall, and
        /// only the placed one carries creative tabs to judge it by.</summary>
        static readonly HashSet<string> NaturalVariants = new() { "grown" };

        /// <summary>Creative tabs the game files terrain and plant life under...
        /// </summary>
        static readonly HashSet<string> NaturalTabs = new() { "terrain", "flora" };

        /// <summary>...and the ones that mean somebody put the block there.
        /// </summary>
        static readonly HashSet<string> BuiltTabs = new() { "construction", "decorative" };

        /// <summary>Materials nothing gets built out of, for the blocks whose
        /// tabs settle nothing.</summary>
        static readonly HashSet<EnumBlockMaterial> NaturalMaterials = new()
        {
            EnumBlockMaterial.Soil, EnumBlockMaterial.Sand, EnumBlockMaterial.Gravel,
            EnumBlockMaterial.Ore, EnumBlockMaterial.Plant, EnumBlockMaterial.Leaves,
            EnumBlockMaterial.Snow, EnumBlockMaterial.Ice, EnumBlockMaterial.Water,
            EnumBlockMaterial.Lava
        };

        /// <summary>
        /// Whether a block belongs to somebody's build rather than to the
        /// landscape. Grass, soil, gravel, ore and raw stone otherwise fill the
        /// material list several times over, and "swap andesite for chalk" is
        /// asking about a wall, not about the hillside behind it.
        ///
        /// Judged by creative tab, which modded blocks declare the same way
        /// vanilla does: terrain and flora against construction and decorative,
        /// the latter winning wherever a block claims both. Where the tabs
        /// settle nothing - the general tab on its own, or no tab at all - the
        /// block's material decides. That last case is every facing of a stair
        /// but the one vanilla lists ("-up-north-free") as much as it is
        /// snow-covered grass, so it has to keep stone and wood.
        /// </summary>
        static bool PlayerPlaced(Block block, string[] parts)
        {
            if (NaturalKinds.Contains(parts[0])) return false;
            for (int i = 1; i < parts.Length; i++)
            {
                if (NaturalVariants.Contains(parts[i])) return false;
            }

            string[] tabs = block.CreativeInventoryTabs;
            if (tabs != null)
            {
                foreach (string tab in tabs)
                {
                    if (BuiltTabs.Contains(tab)) return true;
                }
                foreach (string tab in tabs)
                {
                    if (NaturalTabs.Contains(tab)) return false;
                }
            }
            return !NaturalMaterials.Contains(block.BlockMaterial);
        }

        static bool PlayerPlaced(Block block) =>
            block?.Code != null && PlayerPlaced(block, block.Code.Path.Split('-'));

        public SwapEngine(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
        }

        void EnsureIndex()
        {
            if (buckets != null) return;
            buckets = new Dictionary<(string, int), List<(string[], int)>>();
            foreach (Block block in sapi.World.Blocks)
            {
                if (block?.Code == null || block.Id == 0) continue;
                string[] parts = block.Code.Path.Split('-');
                // nothing should ever offer to turn a wall into raw stone or soil
                if (!PlayerPlaced(block, parts)) continue;
                var key = (block.Code.Domain, parts.Length);
                if (!buckets.TryGetValue(key, out var list))
                {
                    buckets[key] = list = new List<(string[], int)>();
                }
                list.Add((parts, block.Id));
            }
        }

        static bool UsableToken(string token) =>
            token.Length > 0 && !StateTokens.Contains(token) && !token.All(char.IsDigit);

        /// <summary>
        /// For a scanned selection (block id -> how many of them), which material
        /// tokens can be swapped and what can each become. Counts are how many
        /// blocks of the selection the swap would convert.
        /// </summary>
        public Analysis Analyze(Dictionary<int, int> selection)
        {
            EnsureIndex();
            var result = new Analysis();

            foreach (var (blockId, count) in selection)
            {
                Block block = sapi.World.GetBlock(blockId);
                if (block?.Code == null) continue;

                string[] parts = block.Code.Path.Split('-');
                // the ground a build stands on is not one of its materials
                if (!PlayerPlaced(block, parts)) continue;
                if (!buckets.TryGetValue((block.Code.Domain, parts.Length), out var candidates)) continue;

                // one (from -> to) can be reachable at more than one token
                // position of the same code; count this block's tally once
                var seen = new HashSet<(string, string)>();
                var seenSources = new HashSet<string>();

                foreach (var (candParts, candId) in candidates)
                {
                    if (candId == blockId) continue;

                    int diffAt = -1;
                    bool oneApart = true;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i] == candParts[i]) continue;
                        if (diffAt >= 0) { oneApart = false; break; }
                        diffAt = i;
                    }
                    if (!oneApart || diffAt < 0) continue;

                    string from = parts[diffAt], to = candParts[diffAt];
                    if (!UsableToken(from) || !UsableToken(to)) continue;

                    // token 0 of a code names the kind of block (cobblestone,
                    // slantedroofing); the tokens after it name what it is made
                    // of. Someone asking to restyle a build means the latter,
                    // so remember which is which for ordering.
                    if (diffAt >= 1) result.MaterialTokens.Add(from);

                    if (seen.Add((from, to)))
                    {
                        if (!result.Targets.TryGetValue(from, out var targets))
                        {
                            result.Targets[from] = targets = new Dictionary<string, int>();
                        }
                        targets[to] = targets.GetValueOrDefault(to) + count;
                    }
                    if (seenSources.Add(from))
                    {
                        result.SourceCounts[from] = result.SourceCounts.GetValueOrDefault(from) + count;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The block this one becomes when <paramref name="from"/> is replaced by
        /// <paramref name="to"/>, or 0 if there is no such block. The first token
        /// position that yields a real block wins. Terrain is left alone at both
        /// ends unless <paramref name="allowNatural"/> says otherwise, which is
        /// for the materials inside a chiselled block - somebody carved that, so
        /// it is theirs whatever they cut it from.
        /// </summary>
        public int ResolveTarget(int blockId, string from, string to, bool allowNatural = false)
        {
            Block block = sapi.World.GetBlock(blockId);
            if (block?.Code == null) return 0;

            string[] parts = block.Code.Path.Split('-');
            if (!allowNatural && !PlayerPlaced(block, parts)) return 0;

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != from) continue;

                string original = parts[i];
                parts[i] = to;
                Block swapped = sapi.World.GetBlock(new AssetLocation(block.Code.Domain, string.Join("-", parts)));
                parts[i] = original;

                if (swapped == null || swapped.Id == blockId) continue;
                if (!allowNatural && !PlayerPlaced(swapped)) continue;
                return swapped.Id;
            }
            return 0;
        }
    }

    public class Analysis
    {
        /// <summary>Material token -> blocks in the selection carrying it.</summary>
        public Dictionary<string, int> SourceCounts = new();

        /// <summary>Material token -> (replacement -> blocks that would convert).</summary>
        public Dictionary<string, Dictionary<string, int>> Targets = new();

        /// <summary>Tokens seen naming a material rather than a kind of block.</summary>
        public HashSet<string> MaterialTokens = new();

        /// <summary>Materials first, then kinds of block; each by how much of
        /// the selection they cover.</summary>
        public List<(string name, int count)> SourcesRanked() =>
            SourceCounts
                .OrderByDescending(kv => MaterialTokens.Contains(kv.Key))
                .ThenByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value)).ToList();

        public List<(string name, int count)> TargetsRanked(string from) =>
            Targets.TryGetValue(from, out var t)
                ? t.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value)).ToList()
                : new List<(string, int)>();
    }
}
