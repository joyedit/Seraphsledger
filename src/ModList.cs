using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace SeraphsLedger
{
    // ---- Mod list browser -----------------------------------------------------
    //
    // An in-game, sortable view of every mod installed, opened with Ctrl+Shift+N
    // or .slmods.
    //
    // Why a dialog of our own rather than sort buttons on the game's own mod
    // manager: that screen (GuiScreenMods) only exists in the main menu, and at
    // the main menu no mod code is running. The game reads mod *infos* at startup
    // (ScreenManager.loadMods -> ModLoader.LoadModInfos) but only instantiates
    // mod systems when a world loads, and the escape menu has no Mods button - so
    // there is no moment at which a mod can patch that list. The data behind it is
    // reachable from in-game though, which is what this does: it runs the same
    // LoadModInfos + DisableAndVerify pass the main menu runs, against a private
    // ModLoader, and renders the result in whatever order was asked for.
    //
    // Enabling and disabling mods does work from here, because it is the one
    // part of the mod manager that isn't tied to that screen: the game keeps the
    // state in ClientSettings.DisabledMods as "modid@version" entries, and all
    // GuiScreenMods does on a click is edit that list and save the settings. It
    // takes a restart to mean anything either way, so a row toggled during this
    // session says so. Installing and deleting mods stay out of it - those move
    // files the running game is reading.

    // What a column can be ordered by. Ascending is "low to high" in each case;
    // for the yes/no keys that means the plain state first and the notable one
    // last (loads fine before failed, up to date before update available), so
    // descending brings what you probably opened the list for to the top.
    public enum ModSortKey
    {
        Name,
        Installed,
        GameVersion,
        Update,
        LoadOrder,
        Problem,
        Enabled,
        Author,
        Description,
    }

    // One row: everything the browser can show or sort by, flattened off the
    // ModContainer so the containers themselves can be dropped right after the
    // scan (they hold a decoded mod icon each).
    public class ModListEntry
    {
        public string Name;          // display name, or the file name if modinfo is unreadable
        public string FileName;      // zip/folder name on disk
        public string ModId;
        public string Version;
        public string Authors;       // comma-joined
        public string Description;   // flattened to one line
        public string GameVersion;   // version of the "game" dependency; "" = unconstrained
        public string TypeName;      // code / content / theme
        public DateTime Installed;   // file date on disk; default(DateTime) when unknown
        public int LoadOrder;        // position in the dependency-sorted order, int.MaxValue if unloaded
        public bool Enabled;
        public bool Failed;
        public string Problem;       // short reason, when Failed
        public string LatestVersion; // newest version the mod DB has for this game version
        public int UpdateState;      // -1 unknown / not checked, 0 up to date, 1 update available
        public bool AheadOfDb;       // installed version is newer than anything published (a local build)
        public bool CoreMod;         // part of the base game, per its own modinfo
        public bool DisabledBySetting;      // listed in the game's disabled-mods setting
        public List<string> Dependents = new List<string>(); // loading mods that name this one as a dependency

        // How the game records a mod in its disabled-mods list. Pinned to the
        // version, which is the game's own behaviour and worth knowing about:
        // updating a disabled mod gives it a new token, so it comes back enabled.
        internal string Token => ModId + "@" + Version;
    }

    public class ModListModSystem : ModSystem
    {
        private ICoreClientAPI capi;

        // Scanning re-reads every mod archive on disk, so the result is held for
        // the session and only redone when the player asks for it (Rescan).
        private List<ModListEntry> entries;

        private GuiDialogModList dialog;

        // The disabled-mods list as it stood when the game started. A toggle made
        // here only reaches the settings file, so this is what tells a row apart
        // from how the game is actually running right now.
        private HashSet<string> launchDisabled;

        // Guards against a second mod DB sweep while one is in flight, and counts
        // its progress so the dialog can show it.
        private bool checking;
        private int checkDone;
        private int checkTotal;

        internal bool Checking => checking;
        internal int CheckDone => checkDone;
        internal int CheckTotal => checkTotal;

        // Mod infos are a client-side thing to read, and the whole feature is a
        // GUI; a dedicated server has nothing to do with it.
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);

            if (!SeraphsLedgerConfig.Load(api).ModListBrowser) return;

            capi = api;
            launchDisabled = new HashSet<string>(ClientSettings.DisabledMods ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            // The list's enable/disable controls are links in its richtext, the
            // same mechanism the trader log uses for [show on map]; clicking one
            // lands here and is handed to the dialog that drew it.
            api.RegisterLinkProtocol("slmod", comp => dialog?.OnLink(comp?.Href));

            api.Input.RegisterHotKey("seraphsledgermodlist", "Open mod list", GlKeys.N,
                HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("seraphsledgermodlist", _ => Toggle());

            api.ChatCommands.Create("slmods")
                .WithDescription("Browse the installed mods, sorted by name, install date, game version, update, load order, problems, enabled, author or description")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("sort"),
                          api.ChatCommands.Parsers.OptionalWord("desc"))
                .HandleWith(OnModsCmd);
        }

        private bool Toggle()
        {
            if (dialog == null) dialog = new GuiDialogModList(capi, this);
            if (dialog.IsOpened()) dialog.TryClose();
            else dialog.TryOpen();
            return true;
        }

        private TextCommandResult OnModsCmd(TextCommandCallingArgs args)
        {
            string key = args[0] as string;
            string order = args[1] as string;

            if (dialog == null) dialog = new GuiDialogModList(capi, this);

            if (!string.IsNullOrEmpty(key))
            {
                if (!TryParseSortKey(key, out ModSortKey parsed))
                {
                    return TextCommandResult.Error("Unknown sort: " + key + ". One of: "
                        + string.Join(", ", Enum.GetNames(typeof(ModSortKey))).ToLowerInvariant());
                }
                dialog.SetSort(parsed, !"desc".Equals(order, StringComparison.OrdinalIgnoreCase));
            }

            if (!dialog.IsOpened()) dialog.TryOpen();
            return TextCommandResult.Success();
        }

        private static bool TryParseSortKey(string text, out ModSortKey key)
        {
            // "installed", "gameversion"/"game", "loadorder"/"load", "problem"...
            if (Enum.TryParse(text, true, out key)) return true;
            switch (text.ToLowerInvariant())
            {
                case "game": key = ModSortKey.GameVersion; return true;
                case "load": key = ModSortKey.LoadOrder; return true;
                case "date": key = ModSortKey.Installed; return true;
                case "updates": key = ModSortKey.Update; return true;
                case "failed": key = ModSortKey.Problem; return true;
                case "author": key = ModSortKey.Author; return true;
                default: return false;
            }
        }

        // ---- The scan ---------------------------------------------------------

        internal List<ModListEntry> Entries(bool rescan = false)
        {
            if (entries == null || rescan) entries = Collect();
            return entries;
        }

        // Mirrors what the main menu does to fill its own list: read every mod
        // info off disk, then run the disable + dependency pass over a copy, which
        // marks each container's status and hands back the dependency-sorted
        // order. Done on a ModLoader of our own so the live one's state and the
        // client log are left alone - a second verify pass against the real
        // loader would append to its MissingDependencies list and re-log every
        // unresolved dependency in the game.
        private List<ModListEntry> Collect()
        {
            var live = capi.ModLoader as ModLoader;
            IEnumerable<string> searchPaths = live?.ModSearchPaths ?? ClientSettings.ModPaths;

            // A copy of the disabled list, so the verify pass can do what it likes
            // with it without that landing back in the player's settings file.
            var disabled = new List<string>(ClientSettings.DisabledMods ?? new List<string>());
            var disabledSet = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);

            var loader = new ModLoader(new QuietLogger(), EnumAppSide.Client, searchPaths, traceLog: false);
            List<ModContainer> all = loader.LoadModInfos();
            List<ModContainer> ordered = loader.DisableAndVerify(new List<ModContainer>(all), disabled);

            var loadOrder = new Dictionary<ModContainer, int>();
            for (int i = 0; i < ordered.Count; i++) loadOrder[ordered[i]] = i;

            var list = new List<ModListEntry>(all.Count);
            var deps = new List<(ModListEntry Entry, ModInfo Info)>(all.Count);
            foreach (ModContainer mod in all)
            {
                ModInfo info = mod.Info;
                var e = new ModListEntry
                {
                    FileName = mod.FileName,
                    Name = string.IsNullOrEmpty(info?.Name) ? mod.FileName : info.Name,
                    ModId = info?.ModID ?? "",
                    Version = info?.Version ?? "",
                    Authors = (info?.Authors != null && info.Authors.Count > 0) ? string.Join(", ", info.Authors) : "",
                    Description = Flatten(info?.Description),
                    GameVersion = info?.Dependencies?.FirstOrDefault(d => d.ModID == "game")?.Version ?? "",
                    TypeName = info != null ? info.Type.ToString().ToLowerInvariant() : "",
                    Installed = FileDate(mod.SourcePath),
                    LoadOrder = loadOrder.TryGetValue(mod, out int idx) ? idx : int.MaxValue,
                    Enabled = mod.Status == ModStatus.Enabled,
                    Failed = mod.Error.HasValue,
                    Problem = Problem(mod),
                    UpdateState = -1,
                    CoreMod = info?.CoreMod ?? false,
                };
                e.DisabledBySetting = e.ModId.Length > 0 && disabledSet.Contains(e.Token);
                list.Add(e);
                deps.Add((e, info));

                // The info pass decodes each mod's icon into an SKBitmap; nothing
                // here draws one, and the containers are dropped on the way out,
                // so let them go rather than leaving a bitmap per mod behind.
                try { mod.Icon?.Dispose(); } catch { /* a half-decoded icon is not worth a log line */ }
            }

            // Who would be pulled down with each mod. Only mods that are actually
            // going to load are counted: a mod that is already disabled or broken
            // loses nothing by one of its dependencies going away, and listing it
            // would bury the ones that matter.
            var byModId = new Dictionary<string, ModListEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModListEntry e in list)
            {
                if (e.ModId.Length > 0) byModId[e.ModId] = e;
            }
            foreach ((ModListEntry entry, ModInfo info) in deps)
            {
                if (!entry.Enabled || info?.Dependencies == null) continue;
                foreach (ModDependency d in info.Dependencies)
                {
                    // "game" is the engine itself, not something in this list.
                    if (d?.ModID == null || d.ModID == "game") continue;
                    if (byModId.TryGetValue(d.ModID, out ModListEntry target) && target != entry)
                        target.Dependents.Add(entry.Name);
                }
            }
            foreach (ModListEntry e in list) e.Dependents.Sort(StringComparer.OrdinalIgnoreCase);

            // Carry over anything a previous mod DB check found, so a rescan
            // doesn't silently blank the update column.
            if (entries != null)
            {
                var byId = entries.Where(p => !string.IsNullOrEmpty(p.ModId))
                                  .GroupBy(p => p.ModId).ToDictionary(g => g.Key, g => g.First());
                foreach (ModListEntry e in list)
                {
                    if (e.ModId.Length > 0 && byId.TryGetValue(e.ModId, out ModListEntry old))
                    {
                        e.LatestVersion = old.LatestVersion;
                        Rate(e);
                    }
                }
            }

            return list;
        }

        private static string Problem(ModContainer mod)
        {
            if (!mod.Error.HasValue) return null;
            switch (mod.Error.Value)
            {
                case ModError.Loading: return "failed to load";
                case ModError.ChangedVersion: return "version changed, needs a restart";
                case ModError.Blocked: return "blocked";
                case ModError.Dependency:
                    return (mod.MissingDependencies != null && mod.MissingDependencies.Count > 0)
                        ? "needs " + string.Join(", ", mod.MissingDependencies.Select(d => d.Replace("@", " v")))
                        : "a dependency has an error";
                default: return "unable to load";
            }
        }

        // The mod file's own date. A downloaded zip keeps the time it was written,
        // which is when it landed in the Mods folder - creation time is not
        // dependable across file systems, so this is the closest thing to an
        // install date that is actually there.
        private static DateTime FileDate(string path)
        {
            try
            {
                if (path == null) return default;
                if (File.Exists(path)) return File.GetLastWriteTime(path);
                if (Directory.Exists(path)) return Directory.GetLastWriteTime(path);
            }
            catch { /* unreadable path: no date, sorts with the unknowns */ }
            return default;
        }

        private static string Flatten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- Enabling and disabling -------------------------------------------

        // A mod with no id or no version in its modinfo can't be written to the
        // disabled list in the form the game reads back, so it isn't offered.
        internal static bool CanToggle(ModListEntry e)
        {
            return !string.IsNullOrEmpty(e.ModId) && !string.IsNullOrEmpty(e.Version);
        }

        // Whether this row's state differs from the one the running game started
        // with, i.e. whether it is waiting on a restart.
        internal bool PendingRestart(ModListEntry e)
        {
            if (!CanToggle(e) || launchDisabled == null) return false;
            return e.DisabledBySetting != launchDisabled.Contains(e.Token);
        }

        // The whole of what the game's own mod manager does for a click: edit the
        // disabled-mods list and save the settings. Nothing is unloaded or loaded
        // now - the mod loader has long since run - which is why every caller has
        // to say a restart is needed.
        internal void SetEnabled(ModListEntry e, bool enabled)
        {
            if (!CanToggle(e)) return;

            List<string> disabled = ClientSettings.DisabledMods ?? new List<string>();
            disabled.Remove(e.Token);
            if (!enabled) disabled.Add(e.Token);
            ClientSettings.DisabledMods = disabled;
            ClientSettings.Inst.Save(force: true);

            e.DisabledBySetting = !enabled;
        }

        // ---- Mod DB update check ----------------------------------------------

        // The mod DB's public per-mod endpoint, which lists every release with its
        // version and the game versions it was tagged for. Only the parts used
        // here are declared; the rest of the (large) response is ignored.
        // Json.NET matches these case-insensitively, the same way "modid" in a
        // modinfo.json lands on ModInfo.ModID.
        private class DbModResponse { public DbMod Mod { get; set; } }
        private class DbMod { public List<DbRelease> Releases { get; set; } }
        private class DbRelease
        {
            public string ModVersion { get; set; }
            public string[] Tags { get; set; }
        }

        // One HTTP client for the process. Reused rather than created per sweep,
        // because a fresh HttpClient per call leaks sockets.
        private static readonly HttpClient http = BuildHttpClient();

        private static HttpClient BuildHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "SeraphsLedger mod list (Vintage Story " + GameVersion.ShortGameVersion + ")");
            return client;
        }

        // Asks the mod DB what the newest release of each installed mod is, and
        // marks the rows whose installed version is behind it.
        //
        // One request per mod, four at a time. The engine's own bulk endpoint
        // (ModDbUtil.FetchModInstallInformation) would do this in a single
        // request, but it matches releases against one exact game version - a mod
        // last tagged for 1.22.2 is reported as "not found" to a 1.22.7 client,
        // which was most of the mods installed here when this was written. So the
        // newest release tagged anywhere in the running game's minor series
        // (1.22.x) is what counts as the latest version, which is also the one the
        // game's own mod browser would offer.
        //
        // Never automatic: it is a few megabytes of requests to someone else's
        // server, so it waits for the button.
        internal void CheckUpdates(Action onDone)
        {
            if (checking) return;

            List<ModListEntry> rows = Entries();
            Dictionary<string, ModListEntry> byId = rows
                .Where(e => !string.IsNullOrEmpty(e.ModId))
                .GroupBy(e => e.ModId)
                .ToDictionary(g => g.Key, g => g.First());
            if (byId.Count == 0) { onDone?.Invoke(); return; }

            string baseUrl = (ClientSettings.ModDbUrl ?? "https://mods.vintagestory.at/").TrimEnd('/') + "/api/mod/";
            string series = GameVersion.OverallMajorMinor;

            checking = true;
            checkDone = 0;
            checkTotal = byId.Count;

            var found = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var failures = 0;

            Task.Run(async () =>
            {
                using var gate = new SemaphoreSlim(4);
                IEnumerable<Task> sweep = byId.Keys.Select(async id =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        string json = await http.GetStringAsync(baseUrl + Uri.EscapeDataString(id));
                        string latest = NewestForSeries(JsonUtil.FromString<DbModResponse>(json), series);
                        if (latest != null) found[id] = latest;
                    }
                    catch
                    {
                        // A mod the DB has never heard of answers 404, which lands
                        // here along with any real network trouble; either way the
                        // row stays "unknown" rather than claiming to be current.
                        Interlocked.Increment(ref failures);
                    }
                    finally
                    {
                        Interlocked.Increment(ref checkDone);
                        gate.Release();
                    }
                });
                await Task.WhenAll(sweep);
            }).ContinueWith(_ =>
            {
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    checking = false;
                    foreach (var pair in found)
                    {
                        if (!byId.TryGetValue(pair.Key, out ModListEntry e)) continue;
                        e.LatestVersion = pair.Value;
                        Rate(e);
                    }
                    // Every single request failing is a connection problem, not a
                    // mod folder full of unpublished mods - worth saying out loud.
                    if (failures == checkTotal)
                    {
                        capi.ShowChatMessage("Mod list: could not reach the mod database.");
                    }
                    onDone?.Invoke();
                }, "seraphsledgermodlistupdates");
            });
        }

        // Newest release carrying a tag in the given game version series, e.g. the
        // newest one tagged 1.22, 1.22.2 or 1.22.7 when the series is "1.22".
        private static string NewestForSeries(DbModResponse resp, string series)
        {
            List<DbRelease> releases = resp?.Mod?.Releases;
            if (releases == null) return null;

            string best = null;
            foreach (DbRelease r in releases)
            {
                if (string.IsNullOrEmpty(r.ModVersion) || r.Tags == null) continue;
                if (!r.Tags.Any(t => InSeries(t, series))) continue;
                if (best == null || IsNewer(r.ModVersion, best)) best = r.ModVersion;
            }
            return best;
        }

        private static bool InSeries(string tag, string series)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            tag = tag.TrimStart('v');
            // The dot matters: "1.2" must not count as part of the "1.22" series.
            return tag == series || tag.StartsWith(series + ".", StringComparison.Ordinal);
        }

        private static bool IsNewer(string version, string reference)
        {
            try { return GameVersion.IsNewerVersionThan(version, reference); }
            catch { return false; }
        }

        private static void Rate(ModListEntry e)
        {
            if (string.IsNullOrEmpty(e.LatestVersion) || string.IsNullOrEmpty(e.Version))
            {
                e.UpdateState = -1;
                e.AheadOfDb = false;
                return;
            }
            e.UpdateState = IsNewer(e.LatestVersion, e.Version) ? 1 : 0;
            // Mod folders here hold locally built versions that are ahead of
            // anything published; saying "up to date" about those reads as if the
            // check hadn't understood them.
            e.AheadOfDb = e.UpdateState == 0 && IsNewer(e.Version, e.LatestVersion);
        }

        // ---- Sorting ----------------------------------------------------------

        // Reorders in place. Only the chosen key is reversed for a descending
        // sort; the name tiebreak stays A-Z so equal rows keep a readable order.
        internal static void Sort(List<ModListEntry> rows, ModSortKey key, bool ascending)
        {
            int dir = ascending ? 1 : -1;
            rows.Sort((a, b) =>
            {
                int c = Compare(a, b, key) * dir;
                if (c != 0) return c;
                c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int Compare(ModListEntry a, ModListEntry b, ModSortKey key)
        {
            switch (key)
            {
                case ModSortKey.Installed:
                    // An unreadable file date is an absence, so it sorts last
                    // either way round, the same as a blank author.
                    if (a.Installed == default || b.Installed == default)
                        return a.Installed == b.Installed ? 0 : (a.Installed == default ? 1 : -1);
                    return a.Installed.CompareTo(b.Installed);
                case ModSortKey.GameVersion:
                    return CompareVersions(a.GameVersion, b.GameVersion);
                case ModSortKey.Update:
                    return a.UpdateState.CompareTo(b.UpdateState);
                case ModSortKey.LoadOrder:
                    return a.LoadOrder.CompareTo(b.LoadOrder);
                case ModSortKey.Problem:
                    return (a.Failed ? 1 : 0).CompareTo(b.Failed ? 1 : 0);
                case ModSortKey.Enabled:
                    return (a.Enabled ? 1 : 0).CompareTo(b.Enabled ? 1 : 0);
                case ModSortKey.Author:
                    return CompareText(a.Authors, b.Authors);
                case ModSortKey.Description:
                    return CompareText(a.Description, b.Description);
                default:
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Blank sorts last either way round: a mod with no author or no
        // description recorded is an absence, not the first thing in the list.
        private static int CompareText(string a, string b)
        {
            bool ea = string.IsNullOrEmpty(a), eb = string.IsNullOrEmpty(b);
            if (ea || eb) return ea && eb ? 0 : (ea ? 1 : -1);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // A mod that names no game version ("game": "") is unconstrained rather
        // than old, so it sorts after every mod pinned to a version, the same way
        // a blank author does.
        private static int CompareVersions(string a, string b)
        {
            int[] va = VersionParts(a), vb = VersionParts(b);
            if (va == null || vb == null) return va == vb ? 0 : (va == null ? 1 : -1);
            for (int i = 0; i < Math.Max(va.Length, vb.Length); i++)
            {
                int pa = i < va.Length ? va[i] : 0;
                int pb = i < vb.Length ? vb[i] : 0;
                if (pa != pb) return pa.CompareTo(pb);
            }
            return 0;
        }

        private static int[] VersionParts(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            // Dependency versions can carry a comparison prefix (">=1.21.0").
            string trimmed = version.TrimStart('>', '<', '=', '^', '~', 'v', ' ');
            if (trimmed.Length == 0 || !char.IsDigit(trimmed[0])) return null;
            try { return GameVersion.SplitVersionString(trimmed); }
            catch { return null; }
        }

        // A logger that writes nowhere. The scan runs the game's own mod loading
        // code, which narrates what it finds and logs an error for every
        // unresolved dependency; none of that belongs in the client log a second
        // time just because someone opened a list.
        private class QuietLogger : LoggerBase
        {
            protected override void LogImpl(EnumLogType logType, string format, params object[] args) { }
        }
    }
}
