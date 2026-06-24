using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DSPPlannerExport
{
    // Exposes live game state over a tiny localhost HTTP endpoint so the DSP Resource
    // Tree Planner (an HTML file) can show a live view.
    //
    //   GET http://localhost:8765/state  ->  researched tech / upgrade levels
    //   { "version":1, "running":true,
    //     "states":[ {"id":1131,"level":1,"max":1,"unlocked":true}, ... ] }
    //
    //   GET http://localhost:8765/protos ->  static item protos (every item: id, name,
    //   grid; buildings additionally model/footprint/zstep/per-side sorter slots)
    //
    //   GET http://localhost:8765/deficits -> live net-negative items (consumed faster
    //   than produced). Computed once/second from the /rates tally deltas, shared by the
    //   in-game HUD overlay and the planner's deficit card so the two never disagree.
    //   { "version":1, "running":true, "gameTick":N, "windowTicks":dt,
    //     "mode":"sustained", "sustainSeconds":5, "minMagnitude":1,
    //     "items":[ {"id":1005,"name":"Stone","produce":455.7,"consume":577.2,
    //                "net":-121.5,"streak":7,"flagged":true}, ... ] }
    //   "flagged" applies the configured policy (sustained: deficit held >= sustainSeconds
    //   samples, so buffer flicker is ignored; instant: net-negative this sample).
    //
    //   GET http://localhost:8765/rates  ->  cumulative production/consumption counters
    //   { "version":1, "running":true, "gameTick":123456,
    //     "items":[ {"id":1101,"p":12345,"c":678}, ... ] }
    //   Counters accumulate across all factories since plugin load (Harmony prefix on
    //   FactoryProductionStat.GameTick reading product/consumeRegister before they are
    //   folded and zeroed). The planner polls twice and computes items/min from deltas:
    //   rate = delta / deltaTick * 3600 (60 ticks/s).
    //
    //   GET http://localhost:8765/events ->  Server-Sent Events push (REC #139): streams
    //   "event: state" / "event: rates" with the same JSON payloads whenever a snapshot
    //   CHANGES (checked once per second), plus keepalive comments. The planner uses
    //   EventSource and falls back to 3 s polling against plugins without this endpoint.
    //
    //   GET http://localhost:8765/techs  ->  static tech protos: per tech id, name,
    //   level span and the REAL prerequisite edges from LDB — "pre" (PreTechs, drawn
    //   as arrows in the in-game tree) and "preImplicit" (PreTechsImplicit, required
    //   but not drawn). v1.4: also "hash" (HashNeeded), "items"+"points" (matrix ids
    //   and their consumption per 3600 hashes — total cost = points*hash/3600) and
    //   "preItem" (items that must be obtained before the game SHOWS the tech).
    //   Lets the planner audit its curated tech tree against the game.
    //
    // "id" is the in-game proto id. Every tech AND every upgrade level is its own
    // proto; TechState.unlocked is the only researched flag (curLevel equals the
    // proto's own level even when unresearched — planner maps unlocked per id).
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "dev.ski.dspplannerexport";
        public const string NAME = "DSP Planner Export";
        public const string VERSION = "1.10.0";
        private const int Port = 8765;

        private HttpListener _listener;
        private Thread _thread;
        private Harmony _harmony;
        private volatile bool _running;
        // The HTTP thread must NOT touch Unity/GameMain objects directly, so the main
        // thread refreshes these snapshots (~1/sec in Update) and the listener just
        // serves the latest string.
        private volatile string _snapshot = "{\"version\":1,\"running\":false,\"states\":[]}";
        private volatile string _rates = "{\"version\":1,\"running\":false,\"gameTick\":0,\"items\":[]}";
        private volatile string _protos = null;   // static item protos (built + cached once localization is ready)
        private volatile string _techs = null;    // static tech protos (built + cached once localization is ready)
        // T66: the game's name strings come from lazy caches that, when first touched at the
        // main menu, return PRE-localization names. Set true on the main thread once a save is
        // actually loaded (localization applied); the HTTP thread only ever READS it and will
        // not permanently cache /protos or /techs until it is true, so a menu-time pull can
        // never bake the wrong strings into the cache.
        private volatile bool _locReady = false;
        private volatile string _deficits = "{\"version\":1,\"running\":false,\"items\":[]}";
        private volatile string _research = "{\"version\":1,\"running\":false}";   // #26: current research + queue
        private volatile string _power = "{\"version\":1,\"running\":false,\"planets\":[]}";   // #27: per-planet power grid
        private int _frame;

        // --- live deficit detection (in-game HUD + planner card share ONE computation) ---
        // Instant = net-negative this sample; Sustained = held for SustainSeconds;
        // Window = net averaged over the last WindowSeconds (buffer churn averages out).
        private enum DeficitMode { Sustained, Instant, Window }
        private enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }
        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<KeyboardShortcut> _cfgToggle;
        private ConfigEntry<DeficitMode> _cfgMode;
        private ConfigEntry<int> _cfgSustainSeconds;
        private ConfigEntry<int> _cfgWindowSeconds;
        private ConfigEntry<float> _cfgMinMagnitude;
        private ConfigEntry<int> _cfgMaxRows;
        private ConfigEntry<float> _cfgScale;
        private ConfigEntry<Corner> _cfgCorner;
        private ConfigEntry<bool> _cfgShowPending;
        private ConfigEntry<float> _cfgHudX, _cfgHudY;   // dragged position (-1 = use Corner)
        // T99 advisor: surface researched building-tier upgrades in the HUD, configurable per kind
        private ConfigEntry<bool> _cfgAdvisor, _cfgAdvBelts, _cfgAdvSorters, _cfgAdvMachines, _cfgAdvProlif;
        private ConfigEntry<KeyboardShortcut> _cfgAdvAckKey;
        private ConfigEntry<string> _cfgAdvAcked;   // internal: comma-separated item ids already dismissed
        private System.IO.FileSystemWatcher _cfgWatcher;

        private string[] _itemName;               // id -> display name (built once in-game)
        private float[] _negSeconds;              // seconds held in deficit, per id (Sustained streak)
        // Ring of cumulative tally snapshots (sparse: active items only), 1/sec. Window mode
        // diffs the live counters against the snapshot ~WindowSeconds ago; Instant/Sustained
        // diff against the previous second. Pruned to the span the current window needs.
        private struct Snap { public long tick; public int[] ids; public long[] p; public long[] c; }
        private readonly List<Snap> _snaps = new List<Snap>();
        private struct DefRow { public int id; public string name; public float prod, cons, net; public int streak; public bool flagged; }
        private volatile DefRow[] _hudRows = new DefRow[0];
        // T99 advisor — tier-up chains by config kind. Authoritative DSP proto ids (match the
        // codec's idmaps); Dark-Fog-only tiers (recomposing/negentropy/self-evolution) are
        // omitted on purpose — they're enemy drops, not a research path. The advisor flags the
        // highest UNLOCKED tier above the base, mirroring the in-app advisor (32-advisor.js).
        private struct Chain { public string kind; public int[] ids; public Chain(string k, int[] i) { kind = k; ids = i; } }
        private static readonly Chain[] UpgradeChains =
        {
            new Chain("Belts",        new[]{ 2001, 2002, 2003 }),
            new Chain("Sorters",      new[]{ 2011, 2012, 2013, 2014 }),
            new Chain("Machines",     new[]{ 2303, 2304, 2305 }),   // assemblers
            new Chain("Machines",     new[]{ 2302, 2315 }),          // smelters (arc -> plane)
            new Chain("Machines",     new[]{ 2309, 2317 }),          // chemical plants
            new Chain("Machines",     new[]{ 2301, 2316 }),          // miners
            new Chain("Proliferator", new[]{ 1141, 1142, 1143 }),
        };
        private struct Advice { public int id; public string text; }
        private static readonly Advice[] _noAdvice = new Advice[0];
        private volatile Advice[] _advice = _noAdvice;
        private volatile bool _inGame;
        private bool _hudVisible = true;
        private bool _dragging;
        private float _hudX, _hudY;
        private Vector2 _dragOffset;
        private GUIStyle _titleStyle, _rowStyle;
        private Texture2D _px;   // 1x1 white, tinted at draw time for an opaque panel

        private void Awake()
        {
            // Configurable deficit policy + HUD (the trigger that counts as a "deficit" is
            // very player-specific, so it lives in the BepInEx config and applies live).
            _cfgEnabled = Config.Bind("HUD", "Enabled", true, "Show the in-game live-deficit HUD overlay.");
            _cfgToggle = Config.Bind("HUD", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "Hotkey to show/hide the HUD at runtime.");
            _cfgMode = Config.Bind("Deficit", "Mode", DeficitMode.Window,
                "How a deficit is flagged. Window = net averaged over the last WindowSeconds (recommended — buffer churn averages out, only real shortages show). Sustained = net-negative held for SustainSeconds. Instant = net-negative in the latest 1s sample (most detail, spammy).");
            _cfgSustainSeconds = Config.Bind("Deficit", "SustainSeconds", 5, "Sustained mode: consecutive ~1s samples in deficit before a flag.");
            _cfgWindowSeconds = Config.Bind("Deficit", "WindowSeconds", 60, "Window mode: average net production over this many seconds (e.g. 30, 60, 120, 300, 600, 900, 1800, 3600). Longer = calmer, only structural deficits show. Capped at 3600 (1h).");
            _cfgMinMagnitude = Config.Bind("Deficit", "MinMagnitude", 1.0f, "Ignore deficits smaller than this many items/min (noise floor).");
            _cfgMaxRows = Config.Bind("HUD", "MaxRows", 12, "Maximum deficit rows drawn in the HUD.");
            _cfgScale = Config.Bind("HUD", "Scale", 1.0f, "HUD size multiplier.");
            _cfgCorner = Config.Bind("HUD", "Corner", Corner.TopRight, "Screen corner the HUD anchors to (used until you drag the HUD).");
            _cfgShowPending = Config.Bind("HUD", "ShowPending", true,
                "Also list items dipping but not yet sustained (dimmed, with their streak) so the HUD matches the planner card. Off = show only flagged deficits.");
            _cfgHudX = Config.Bind("HUD", "HudX", -1f, "HUD pixel X of the top-left corner. -1 = anchor to Corner. Set by dragging the HUD in-game.");
            _cfgHudY = Config.Bind("HUD", "HudY", -1f, "HUD pixel Y of the top-left corner. -1 = anchor to Corner. Set by dragging the HUD in-game.");
            // T99 advisor — researched-upgrade tips in the HUD; the player picks which kinds show.
            _cfgAdvisor = Config.Bind("Advisor", "Enabled", true, "Show 'researched upgrade available' tips in the HUD when a higher building tier unlocks (e.g. Mk.II belts). Mirrors the planner's 💡 Advisor.");
            _cfgAdvBelts = Config.Bind("Advisor", "Belts", true, "Advise when a higher Conveyor Belt tier is researched.");
            _cfgAdvSorters = Config.Bind("Advisor", "Sorters", true, "Advise when a higher Sorter tier is researched.");
            _cfgAdvMachines = Config.Bind("Advisor", "Machines", true, "Advise when a higher Assembler / Smelter / Chemical Plant / Mining Machine tier is researched.");
            _cfgAdvProlif = Config.Bind("Advisor", "Proliferator", true, "Advise when a higher Proliferator Mk is researched.");
            _cfgAdvAckKey = Config.Bind("Advisor", "DismissKey", new KeyboardShortcut(KeyCode.F9), "Hotkey to dismiss the current advisor tips (each reappears only when a still-newer tier unlocks).");
            _cfgAdvAcked = Config.Bind("Advisor", "Acknowledged", "", "Internal: comma-separated item ids already dismissed. Managed by DismissKey.");
            _hudVisible = _cfgEnabled.Value;

            // BepInEx does not apply external .cfg edits on its own — watch the file so
            // config tweaks (mode, thresholds, HUD) take effect live without a restart.
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Config.ConfigFilePath);
                var fn = System.IO.Path.GetFileName(Config.ConfigFilePath);
                _cfgWatcher = new System.IO.FileSystemWatcher(dir, fn)
                {
                    NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _cfgWatcher.Changed += (s, e) => { try { Config.Reload(); } catch { } };
            }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: config watcher failed (edit the cfg + restart to apply). " + e.Message); }

            try
            {
                _harmony = new Harmony(GUID);
                _harmony.PatchAll(typeof(ProductionTally));
            }
            catch (Exception e)
            {
                Logger.LogWarning("DSP Planner Export: stats patch failed (live /rates disabled). " + e.Message);
            }
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _running = true;
                _thread = new Thread(ServeLoop) { IsBackground = true, Name = "DSPPlannerExport" };
                _thread.Start();
                Logger.LogInfo($"DSP Planner Export: serving http://localhost:{Port}/state, /protos, /techs, /rates, /deficits, /research, /power, /config");
            }
            catch (Exception e)
            {
                Logger.LogError("DSP Planner Export: could not start HTTP listener. " +
                                "On Windows you may need to run the game as admin once, or pick another port. " + e);
            }
        }

        // Refresh the JSON snapshots on the main (Unity) thread.
        private void Update()
        {
            if (_cfgToggle.Value.IsDown()) _hudVisible = !_hudVisible;
            if (_cfgAdvAckKey.Value.IsDown()) AcknowledgeAdvice();
            if (++_frame % 60 != 0) return; // roughly once per second
            // T66: once a save is actually loaded, the game's name strings are localized.
            // Flip the gate (main thread, so touching GameMain is safe) and discard any
            // protos/techs the HTTP thread may have built fresh at the menu, so the next
            // pull rebuilds + caches with the correct localized names.
            if (!_locReady && GameMain.instance != null && GameMain.data != null && !DSPGame.IsMenuDemo)
            { _locReady = true; _protos = null; _techs = null; }
            try { _snapshot = BuildJson(); }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: snapshot error " + e.Message); }
            try { _rates = BuildRates(); }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: rates error " + e.Message); }
            try { ComputeDeficits(); }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: deficit error " + e.Message); }
            try { _research = BuildResearch(); }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: research error " + e.Message); }
            try { _power = BuildPower(); }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: power error " + e.Message); }
            try { ComputeAdvice(); }
            catch (Exception e) { Logger.LogWarning("DSP Planner Export: advice error " + e.Message); }
        }

        // T99: which researched tier-ups to advise — the highest UNLOCKED tier above the base in
        // each enabled chain, minus the ids the player has already dismissed (DismissKey).
        private bool AdvKindEnabled(string kind)
        {
            switch (kind)
            {
                case "Belts": return _cfgAdvBelts.Value;
                case "Sorters": return _cfgAdvSorters.Value;
                case "Machines": return _cfgAdvMachines.Value;
                case "Proliferator": return _cfgAdvProlif.Value;
                default: return true;
            }
        }
        private HashSet<int> AckedSet()
        {
            var set = new HashSet<int>();
            var raw = _cfgAdvAcked.Value;
            if (!string.IsNullOrEmpty(raw))
                foreach (var p in raw.Split(',')) if (int.TryParse(p.Trim(), out int v)) set.Add(v);
            return set;
        }
        private void ComputeAdvice()
        {
            if (!_cfgAdvisor.Value || GameMain.instance == null || GameMain.history == null || DSPGame.IsMenuDemo) { _advice = _noAdvice; return; }
            var acked = AckedSet();
            var list = new List<Advice>();
            foreach (var ch in UpgradeChains)
            {
                if (!AdvKindEnabled(ch.kind)) continue;
                int top = -1;
                for (int i = 0; i < ch.ids.Length; i++)
                    if (GameMain.history.ItemUnlocked(ch.ids[i])) top = i;
                if (top <= 0) continue;                  // only the base tier unlocked -> nothing to suggest
                int topId = ch.ids[top];
                if (acked.Contains(topId)) continue;      // already dismissed this tier
                var proto = LDB.items.Select(topId);
                list.Add(new Advice { id = topId, text = proto != null ? proto.name : ("item " + topId) });
            }
            _advice = list.ToArray();
        }
        private void AcknowledgeAdvice()
        {
            var adv = _advice;
            if (adv.Length == 0) return;
            var acked = AckedSet();
            foreach (var a in adv) acked.Add(a.id);
            _cfgAdvAcked.Value = string.Join(",", acked);
            _advice = _noAdvice;
        }

        private void ServeLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                try
                {
                    var res = ctx.Response;
                    res.AddHeader("Access-Control-Allow-Origin", "*"); // allow the file:// planner page
                    res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    res.AddHeader("Cache-Control", "no-store");
                    if (ctx.Request.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.OutputStream.Close(); continue; }
                    string path = ctx.Request.Url.AbsolutePath;
                    if (path.EndsWith("/events"))
                    {
                        // SSE connections stay open — hand each to its own thread so the
                        // accept loop keeps serving the JSON endpoints
                        var t = new Thread(() => ServeEvents(ctx)) { IsBackground = true, Name = "DSPPlannerExportSSE" };
                        t.Start();
                        continue;
                    }
                    res.ContentType = "application/json";
                    string body;
                    if (path.EndsWith("/protos"))
                    {
                        body = GetStaticProtos();
                    }
                    else if (path.EndsWith("/techs"))
                    {
                        body = GetStaticTechs();
                    }
                    else if (path.EndsWith("/rates")) body = _rates;
                    else if (path.EndsWith("/deficits")) body = _deficits;
                    else if (path.EndsWith("/research")) body = _research;   // #26
                    else if (path.EndsWith("/power")) body = _power;         // #27
                    else if (path.EndsWith("/config"))
                    {
                        if (ctx.Request.HttpMethod == "POST")
                        {
                            string post;
                            using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8)) post = sr.ReadToEnd();
                            try { ApplyConfig(post); } catch (Exception e) { Logger.LogWarning("DSP Planner Export: config apply error " + e.Message); }
                        }
                        body = BuildConfig();
                    }
                    else body = _snapshot;
                    byte[] buf = Encoding.UTF8.GetBytes(body);
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf, 0, buf.Length);
                    res.OutputStream.Close();
                }
                catch (Exception e) { Logger.LogWarning("DSP Planner Export: request error " + e.Message); }
            }
        }

        // One SSE client connection (REC #139): pushes a snapshot only when its JSON
        // actually changed (the main thread rebuilds them once per second), with a
        // keepalive comment otherwise so proxies/browsers keep the stream alive.
        private void ServeEvents(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            var os = res.OutputStream;
            try
            {
                res.ContentType = "text/event-stream";
                res.SendChunked = true;
                string lastState = null, lastRates = null;
                while (_running)
                {
                    string s = _snapshot, r = _rates;
                    var sb = new StringBuilder();
                    if (s != lastState) { sb.Append("event: state\ndata: ").Append(s).Append("\n\n"); lastState = s; }
                    if (r != lastRates) { sb.Append("event: rates\ndata: ").Append(r).Append("\n\n"); lastRates = r; }
                    if (sb.Length == 0) sb.Append(": keepalive\n\n");
                    byte[] buf = Encoding.UTF8.GetBytes(sb.ToString());
                    os.Write(buf, 0, buf.Length);
                    os.Flush();
                    Thread.Sleep(1000);
                }
            }
            catch { } // client went away — normal
            try { os.Close(); } catch { }
        }

        // NOTE: field names below (techStates / unlocked / curLevel / maxLevel) match current
        // DSP builds. If a future patch renames them, adjust here — the planner side needs no change.
        private static string BuildJson()
        {
            var sb = new StringBuilder(4096);
            bool inGame = GameMain.instance != null && GameMain.history != null && !DSPGame.IsMenuDemo;
            sb.Append("{\"version\":1,\"running\":").Append(inGame ? "true" : "false").Append(",\"states\":[");
            if (inGame)
            {
                var ts = GameMain.history.techStates;
                if (ts != null)
                {
                    bool first = true;
                    foreach (var kv in ts)
                    {
                        TechState st = kv.Value;
                        if (!st.unlocked && st.curLevel <= 0) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"id\":").Append(kv.Key)
                          .Append(",\"level\":").Append(st.curLevel)
                          .Append(",\"max\":").Append(st.maxLevel)
                          .Append(",\"unlocked\":").Append(st.unlocked ? "true" : "false")
                          .Append('}');
                    }
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // #26: live research progress — the tech being researched, its hash progress, and
        // the queue → feeds the research timeline (#3) ETA and "in progress, N% / queued"
        // in the tech inspector. (Field names match current DSP builds; adjust on a patch.)
        private static string BuildResearch()
        {
            var sb = new StringBuilder(512);
            bool inGame = GameMain.instance != null && GameMain.history != null && !DSPGame.IsMenuDemo;
            sb.Append("{\"version\":1,\"running\":").Append(inGame ? "true" : "false");
            if (inGame)
            {
                var h = GameMain.history;
                int cur = h.currentTech;
                sb.Append(",\"current\":").Append(cur);
                if (cur > 0 && h.techStates != null && h.techStates.ContainsKey(cur))
                {
                    var st = h.techStates[cur];
                    sb.Append(",\"hashUploaded\":").Append(st.hashUploaded)
                      .Append(",\"hashNeeded\":").Append(st.hashNeeded)
                      .Append(",\"level\":").Append(st.curLevel);
                }
                sb.Append(",\"queue\":[");
                var q = h.techQueue;
                if (q != null)
                {
                    int n = h.techQueueLength, w = 0;
                    for (int i = 0; i < n && i < q.Length; i++) { if (q[i] <= 0) continue; if (w++ > 0) sb.Append(','); sb.Append(q[i]); }
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        // #27: per-planet power-grid stats — generation capacity, consumption and stored
        // (accumulator) energy summed across each planet's power networks → "plan vs actual"
        // for the power card. Energies are per-tick (×60 ≈ /s); the planner converts.
        private static string BuildPower()
        {
            var sb = new StringBuilder(2048);
            bool inGame = GameMain.instance != null && GameMain.data != null && !DSPGame.IsMenuDemo;
            sb.Append("{\"version\":1,\"running\":").Append(inGame ? "true" : "false").Append(",\"planets\":[");
            if (inGame)
            {
                var factories = GameMain.data.factories;
                bool first = true;
                for (int f = 0; f < GameMain.data.factoryCount; f++)
                {
                    var fac = factories[f]; if (fac == null || fac.powerSystem == null || fac.planet == null) continue;
                    long capacity = 0, consumption = 0, stored = 0;
                    var ps = fac.powerSystem;
                    for (int i = 1; i < ps.netCursor; i++)
                    {
                        var net = ps.netPool[i]; if (net == null) continue;
                        capacity += net.energyCapacity;
                        consumption += net.energyServed;
                        stored += net.energyStored;
                    }
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"planet\":").Append(fac.planet.id)
                      .Append(",\"name\":\"").Append(JsonEscape(fac.planet.displayName ?? ""))
                      .Append("\",\"capacity\":").Append(capacity)
                      .Append(",\"consumption\":").Append(consumption)
                      .Append(",\"stored\":").Append(stored).Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // T66: serve the static protos/techs, but only PERMANENTLY cache them once
        // localization is ready (_locReady). Before a save loads, the game's lazy name
        // caches can return pre-localization strings, so we build a fresh (uncached)
        // best-effort response at the menu and let a later in-game pull build the real,
        // cached one. (BuildProtos/BuildTechs read only LDB static data — safe off-thread.)
        private string GetStaticProtos()
        {
            if (_protos != null) return _protos;
            string p; try { p = BuildProtos(); } catch (Exception e) { Logger.LogWarning("protos error " + e.Message); return "{\"items\":[]}"; }
            if (_locReady) _protos = p;
            return p;
        }
        private string GetStaticTechs()
        {
            if (_techs != null) return _techs;
            string t; try { t = BuildTechs(); } catch (Exception e) { Logger.LogWarning("techs error " + e.Message); return "{\"version\":1,\"techs\":[]}"; }
            if (_locReady) _techs = t;
            return t;
        }

        // Full item proto dump: every item gets id, name and inventory grid index;
        // buildings additionally get modelIndex, blueprintBoxSize (T55b: NOT belt-grid
        // cells — collider-ish extents; the planner ignores w/h and keeps its
        // fixture-verified footprints), "zstep" (vertical offset per stacked level,
        // prefabDesc.lapJoint.y) and per-side sorter slot counts.
        private static string BuildProtos()
        {
            var sb = new StringBuilder(65536);
            sb.Append("{\"version\":2,\"items\":[");
            bool first = true;
            var arr = LDB.items.dataArray;
            foreach (var it in arr)
            {
                if (it == null || it.ID <= 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(it.ID)
                  .Append(",\"name\":\"").Append(JsonEscape(it.name)).Append('"')
                  .Append(",\"grid\":").Append(it.GridIndex);
                if (it.ModelIndex <= 0) { sb.Append('}'); continue; }   // plain item: done
                sb.Append(",\"model\":").Append(it.ModelIndex);
                try
                {
                    var model = LDB.models.Select(it.ModelIndex);
                    var pd = model != null ? model.prefabDesc : null;
                    if (pd != null)
                    {
                        // blueprint footprint in grid cells (e.g. assembler 3x3, chem plant 3x2)
                        var box = pd.blueprintBoxSize;
                        if (box.x > 0 && box.y > 0) sb.Append(",\"w\":").Append((int)box.x).Append(",\"h\":").Append((int)box.y);
                        float zstep = pd.lapJoint.y;
                        if (zstep > 0f) sb.Append(",\"zstep\":").Append(zstep.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                        // per-side sorter slot counts [N,E,S,W], classified from each slot
                        // pose's local position (x = lateral, z = forward)
                        var sp = pd.slotPoses;
                        if (sp != null && sp.Length > 0)
                        {
                            var slots = new int[4];
                            foreach (var pose in sp)
                            {
                                var pos = pose.position;
                                int side = Math.Abs(pos.x) >= Math.Abs(pos.z)
                                    ? (pos.x >= 0 ? 1 : 3)
                                    : (pos.z >= 0 ? 0 : 2);
                                slots[side]++;
                            }
                            sb.Append(",\"slots\":[").Append(slots[0]).Append(',').Append(slots[1]).Append(',').Append(slots[2]).Append(',').Append(slots[3]).Append(']');
                        }
                    }
                }
                catch { }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Static tech protos with the REAL prerequisite edges from LDB. PreTechs are
        // drawn as arrows in the in-game tree; PreTechsImplicit are required but not
        // drawn — the planner's curated tree must distinguish the two (user report
        // 2026-06-12: planner showed a prereq the game tree doesn't draw).
        private static string BuildTechs()
        {
            var sb = new StringBuilder(65536);
            sb.Append("{\"version\":1,\"techs\":[");
            bool first = true;
            foreach (var t in LDB.techs.dataArray)
            {
                if (t == null || t.ID <= 0) continue;
                if (!first) sb.Append(',');
                first = false;
                long baseHash; try { baseHash = t.GetHashNeeded(t.Level); } catch { baseHash = t.HashNeeded; }
                sb.Append("{\"id\":").Append(t.ID)
                  .Append(",\"name\":\"").Append(JsonEscape(t.name)).Append('"')
                  .Append(",\"level\":").Append(t.Level)
                  .Append(",\"max\":").Append(t.MaxLevel)
                  .Append(",\"hash\":").Append(baseHash);
                // multi-level protos (incl. infinite tails) scale the hash per level —
                // "Energy Shield Lv26" wants 462k matrices, not the base rate. Export
                // the first 50 levels' hashes so the planner can show real costs.
                if (t.MaxLevel > t.Level)
                {
                    sb.Append(",\"hashByLevel\":[");
                    int top = Math.Min(t.MaxLevel, t.Level + 49);
                    for (int L = t.Level; L <= top; L++)
                    {
                        if (L > t.Level) sb.Append(',');
                        long h; try { h = t.GetHashNeeded(L); } catch { h = t.HashNeeded; }
                        sb.Append(h);
                    }
                    sb.Append(']');
                }
                sb.Append(",\"pre\":");
                AppendIntArray(sb, t.PreTechs);
                sb.Append(",\"preImplicit\":");
                AppendIntArray(sb, t.PreTechsImplicit);
                // research cost: Items[i] consumed at ItemPoints[i] per 3600 hashes
                // (in-game total = points * hash / 3600); PreItem = items the player
                // must OBTAIN before the game shows the tech in the tree at all
                sb.Append(",\"items\":");
                AppendIntArray(sb, t.Items);
                sb.Append(",\"points\":");
                AppendIntArray(sb, t.ItemPoints);
                sb.Append(",\"preItem\":");
                AppendIntArray(sb, t.PreItem);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendIntArray(StringBuilder sb, int[] a)
        {
            sb.Append('[');
            if (a != null) for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i]); }
            sb.Append(']');
        }

        // Cumulative per-item production/consumption counters (see ProductionTally).
        // Exported raw with the current game tick; the planner turns successive samples
        // into items/min. Cheap enough to rebuild every second.
        private static string BuildRates()
        {
            bool inGame = GameMain.instance != null && GameMain.data != null && !DSPGame.IsMenuDemo;
            var sb = new StringBuilder(16384);
            sb.Append("{\"version\":1,\"running\":").Append(inGame ? "true" : "false")
              .Append(",\"gameTick\":").Append(inGame ? GameMain.gameTick : 0)
              .Append(",\"items\":[");
            if (inGame)
            {
                bool first = true;
                for (int id = 0; id < ProductionTally.Produced.Length; id++)
                {
                    long p = ProductionTally.Produced[id], c = ProductionTally.Consumed[id];
                    if (p == 0 && c == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(id).Append(",\"p\":").Append(p).Append(",\"c\":").Append(c).Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Once a second: snapshot the cumulative tally and diff the live counters against a
        // reference sample to get net items/min. Window mode references the snapshot
        // ~WindowSeconds ago (averaging buffer churn out); Instant/Sustained reference the
        // previous second. Produces BOTH the /deficits JSON and _hudRows from one pass so
        // the planner card and the in-game HUD can never disagree.
        private void ComputeDeficits()
        {
            bool inGame = GameMain.instance != null && GameMain.data != null && !DSPGame.IsMenuDemo;
            _inGame = inGame;
            if (!inGame) { _deficits = "{\"version\":2,\"running\":false,\"items\":[]}"; _hudRows = new DefRow[0]; _snaps.Clear(); return; }

            int len = ProductionTally.Produced.Length;
            if (_itemName == null)
            {
                _itemName = new string[len];
                foreach (var it in LDB.items.dataArray)
                    if (it != null && it.ID > 0 && it.ID < len) _itemName[it.ID] = it.name;
            }
            if (_negSeconds == null) _negSeconds = new float[len];

            var P = ProductionTally.Produced; var C = ProductionTally.Consumed;
            long tick = GameMain.gameTick;
            // tick went backwards (save reload / counters re-seeded) -> drop stale history.
            if (_snaps.Count > 0 && tick <= _snaps[_snaps.Count - 1].tick) _snaps.Clear();

            var mode = _cfgMode.Value;
            int windowSec = Math.Max(5, Math.Min(3600, _cfgWindowSeconds.Value));
            // choose the reference snapshot (before adding the current one)
            int refIdx = -1;
            if (_snaps.Count > 0)
            {
                if (mode == DeficitMode.Window)
                {
                    long target = tick - (long)windowSec * 60;
                    refIdx = 0;                                  // oldest = warming-up fallback
                    for (int i = 0; i < _snaps.Count; i++) { if (_snaps[i].tick <= target) refIdx = i; else break; }
                }
                else refIdx = _snaps.Count - 1;                  // previous second
            }

            if (refIdx < 0) { PushSnap(tick, P, C, len); return; }   // need two samples
            var rf = _snaps[refIdx];
            long dt = tick - rf.tick;
            if (dt <= 0) { PushSnap(tick, P, C, len); return; }
            float windowSecActual = dt / 60f;

            // sparse reference lookup
            var rp = new Dictionary<int, long>(rf.ids.Length);
            var rc = new Dictionary<int, long>(rf.ids.Length);
            for (int i = 0; i < rf.ids.Length; i++) { rp[rf.ids[i]] = rf.p[i]; rc[rf.ids[i]] = rf.c[i]; }

            float minMag = _cfgMinMagnitude.Value;
            int sustain = Math.Max(1, _cfgSustainSeconds.Value);
            var rows = new List<DefRow>();
            for (int id = 0; id < len; id++)
            {
                long curP = P[id], curC = C[id];
                long dp = curP - (rp.TryGetValue(id, out var vp) ? vp : 0);
                long dc = curC - (rc.TryGetValue(id, out var vc) ? vc : 0);
                if (dp == 0 && dc == 0) { _negSeconds[id] = 0f; continue; }
                float prod = (float)(dp / (double)dt * 3600.0);
                float cons = (float)(dc / (double)dt * 3600.0);
                float net = prod - cons;
                if (net >= -minMag) { _negSeconds[id] = 0f; continue; }
                bool flagged; int streak;
                if (mode == DeficitMode.Sustained)
                {
                    _negSeconds[id] += 1f;                       // ~1s per compute
                    flagged = _negSeconds[id] >= sustain;
                    streak = (int)Math.Round(_negSeconds[id]);
                }
                else { flagged = true; streak = (int)Math.Round(windowSecActual); }   // Instant & Window: any negative averaged over the window is a real warning
                rows.Add(new DefRow { id = id, name = _itemName[id] ?? ("#" + id), prod = prod, cons = cons, net = net, streak = streak, flagged = flagged });
            }

            PushSnap(tick, P, C, len);
            // keep only the history the current window needs (+slack) so memory stays bounded
            long keepSecs = (mode == DeficitMode.Window ? windowSec : 2) + 120;
            long cutoff = tick - keepSecs * 60;
            while (_snaps.Count > 1 && _snaps[0].tick < cutoff) _snaps.RemoveAt(0);

            rows.Sort((x, y) => x.net.CompareTo(y.net)); // most negative first
            _hudRows = rows.ToArray();
            _deficits = BuildDeficitsJson(tick, dt, rows);
        }

        // Append a sparse snapshot (active items only) of the cumulative counters.
        private void PushSnap(long tick, long[] P, long[] C, int len)
        {
            int n = 0;
            for (int id = 0; id < len; id++) if (P[id] != 0 || C[id] != 0) n++;
            var ids = new int[n]; var p = new long[n]; var c = new long[n];
            int k = 0;
            for (int id = 0; id < len; id++) if (P[id] != 0 || C[id] != 0) { ids[k] = id; p[k] = P[id]; c[k] = C[id]; k++; }
            _snaps.Add(new Snap { tick = tick, ids = ids, p = p, c = c });
        }

        private string BuildDeficitsJson(long tick, long dt, List<DefRow> rows)
        {
            var sb = new StringBuilder(2048);
            sb.Append("{\"version\":2,\"running\":true,\"gameTick\":").Append(tick)
              .Append(",\"windowTicks\":").Append(dt)
              .Append(",\"windowSec\":").Append(Fmt(dt / 60f))
              .Append(",\"mode\":\"").Append(_cfgMode.Value.ToString().ToLowerInvariant()).Append('"')
              .Append(",\"sustainSeconds\":").Append(_cfgSustainSeconds.Value)
              .Append(",\"requestedWindowSec\":").Append(Math.Max(5, Math.Min(3600, _cfgWindowSeconds.Value)))
              .Append(",\"minMagnitude\":").Append(Fmt(_cfgMinMagnitude.Value))
              .Append(",\"items\":[");
            int n = Math.Min(rows.Count, 50);
            for (int i = 0; i < n; i++)
            {
                var r = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(r.id)
                  .Append(",\"name\":\"").Append(JsonEscape(r.name)).Append('"')
                  .Append(",\"produce\":").Append(Fmt(r.prod))
                  .Append(",\"consume\":").Append(Fmt(r.cons))
                  .Append(",\"net\":").Append(Fmt(r.net))
                  .Append(",\"streak\":").Append(r.streak)
                  .Append(",\"flagged\":").Append(r.flagged ? "true" : "false").Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Fmt(float v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        // GET /config -> current HUD/deficit settings as JSON; POST /config
        // (form-urlencoded, partial allowed) -> apply live and persist to the .cfg.
        // Lets the planner show & edit the config in-app instead of the BepInEx file.
        private string BuildConfig()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"version\":2")
              .Append(",\"mode\":\"").Append(_cfgMode.Value.ToString()).Append('"')
              .Append(",\"sustainSeconds\":").Append(_cfgSustainSeconds.Value)
              .Append(",\"windowSeconds\":").Append(_cfgWindowSeconds.Value)
              .Append(",\"minMagnitude\":").Append(Fmt(_cfgMinMagnitude.Value))
              .Append(",\"hudEnabled\":").Append(_cfgEnabled.Value ? "true" : "false")
              .Append(",\"toggleKey\":\"").Append(JsonEscape(_cfgToggle.Value.ToString())).Append('"')
              .Append(",\"showPending\":").Append(_cfgShowPending.Value ? "true" : "false")
              .Append(",\"corner\":\"").Append(_cfgCorner.Value.ToString()).Append('"')
              .Append(",\"scale\":").Append(Fmt(_cfgScale.Value))
              .Append(",\"maxRows\":").Append(_cfgMaxRows.Value)
              .Append(",\"hudX\":").Append(Fmt(_cfgHudX.Value))
              .Append(",\"hudY\":").Append(Fmt(_cfgHudY.Value))
              .Append(",\"dragged\":").Append(_cfgHudX.Value >= 0f && _cfgHudY.Value >= 0f ? "true" : "false")
              .Append('}');
            return sb.ToString();
        }

        private void ApplyConfig(string form)
        {
            if (string.IsNullOrEmpty(form)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var part in form.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq);
                string val = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')).Trim();
                try
                {
                    switch (key)
                    {
                        case "mode": if (Enum.TryParse<DeficitMode>(val, true, out var m)) _cfgMode.Value = m; break;
                        case "sustainSeconds": if (int.TryParse(val, out var ss)) _cfgSustainSeconds.Value = Math.Max(1, ss); break;
                        case "windowSeconds": if (int.TryParse(val, out var ws)) _cfgWindowSeconds.Value = Math.Max(5, Math.Min(3600, ws)); break;
                        case "minMagnitude": if (float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var mm)) _cfgMinMagnitude.Value = Math.Max(0f, mm); break;
                        case "maxRows": if (int.TryParse(val, out var mr)) _cfgMaxRows.Value = Math.Max(1, mr); break;
                        case "scale": if (float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var sc)) _cfgScale.Value = Mathf.Clamp(sc, 0.5f, 3f); break;
                        case "corner": if (Enum.TryParse<Corner>(val, true, out var c)) _cfgCorner.Value = c; break;
                        case "hudEnabled": _cfgEnabled.Value = (val == "true" || val == "1"); break;
                        case "showPending": _cfgShowPending.Value = (val == "true" || val == "1"); break;
                        case "hudX": if (float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var hx)) _cfgHudX.Value = hx; break;
                        case "hudY": if (float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var hy)) _cfgHudY.Value = hy; break;
                    }
                }
                catch { }
            }
        }

        // In-game overlay: the flagged deficits, drawn in the configured screen corner.
        // Reads only the cached _hudRows snapshot (never game logic), so it is GUI-thread safe.
        private void OnGUI()
        {
            if (!_hudVisible || !_inGame) return;
            bool showDef = _cfgEnabled.Value;
            var adv = _cfgAdvisor.Value ? _advice : _noAdvice;
            if (!showDef && adv.Length == 0) return;   // HUD off and no advice -> nothing to draw
            var rows = showDef ? _hudRows : new DefRow[0];
            bool showPending = _cfgShowPending.Value;
            // Mirror the planner card: by default list every dipping item (flagged +
            // pending), so the two surfaces agree. ShowPending=false = flagged only.
            int total = 0, flaggedN = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].flagged) { flaggedN++; total++; }
                else if (showPending) total++;
            }

            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                _rowStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            }
            if (_px == null) { _px = new Texture2D(1, 1); _px.SetPixel(0, 0, Color.white); _px.Apply(); _px.hideFlags = HideFlags.HideAndDontSave; }

            float s = Mathf.Clamp(_cfgScale.Value, 0.5f, 3f);
            int fs = Mathf.RoundToInt(14 * s);
            _titleStyle.fontSize = fs; _rowStyle.fontSize = fs;
            float pad = 10 * s, accent = 4 * s, rowH = fs + 8, w = 300 * s, margin = 14 * s;
            int cap = Math.Max(1, _cfgMaxRows.Value);
            int defRows = showDef ? (1 + (total > 0 ? Math.Min(total, cap) : 1)) : 0;   // title + rows (or the no-deficits line)
            int advRows = adv.Length > 0 ? adv.Length + 1 : 0;                            // advisor header + lines
            float h = pad * 2 + rowH * Math.Max(1, defRows + advRows);
            var corner = _cfgCorner.Value;
            bool left = corner == Corner.TopLeft || corner == Corner.BottomLeft;
            bool top = corner == Corner.TopLeft || corner == Corner.TopRight;
            float cornerX = left ? margin : Screen.width - w - margin;
            float cornerY = top ? margin : Screen.height - h - margin;
            // A dragged position (HudX/HudY >= 0) overrides the corner anchor.
            float px = _dragging ? _hudX : (_cfgHudX.Value >= 0f && _cfgHudY.Value >= 0f ? _cfgHudX.Value : cornerX);
            float py = _dragging ? _hudY : (_cfgHudX.Value >= 0f && _cfgHudY.Value >= 0f ? _cfgHudY.Value : cornerY);
            px = Mathf.Clamp(px, 0, Math.Max(0, Screen.width - w));
            py = Mathf.Clamp(py, 0, Math.Max(0, Screen.height - h));

            // Drag the panel with the left mouse button; persist the position on release.
            var ev = Event.current;
            if (ev != null)
            {
                if (ev.type == EventType.MouseDown && ev.button == 0 && new Rect(px, py, w, h).Contains(ev.mousePosition))
                { _dragging = true; _hudX = px; _hudY = py; _dragOffset = ev.mousePosition - new Vector2(px, py); ev.Use(); }
                else if (_dragging && ev.type == EventType.MouseDrag)
                {
                    px = _hudX = Mathf.Clamp(ev.mousePosition.x - _dragOffset.x, 0, Math.Max(0, Screen.width - w));
                    py = _hudY = Mathf.Clamp(ev.mousePosition.y - _dragOffset.y, 0, Math.Max(0, Screen.height - h));
                    ev.Use();
                }
                else if (_dragging && (ev.type == EventType.MouseUp || ev.rawType == EventType.MouseUp))
                { _dragging = false; _cfgHudX.Value = _hudX; _cfgHudY.Value = _hudY; ev.Use(); }
            }

            var saved = GUI.color;
            // Opaque dark panel: the built-in box texture is mostly transparent, so tint a
            // solid 1x1 texture instead — readable over any (bright) game scene.
            GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.93f);
            GUI.DrawTexture(new Rect(px, py, w, h), _px);
            GUI.color = flaggedN > 0 ? new Color(1f, 0.45f, 0.35f, 0.95f) : new Color(0.55f, 0.6f, 0.7f, 0.9f);
            GUI.DrawTexture(new Rect(px, py, accent, h), _px);   // severity accent bar
            GUI.color = saved;

            float tx = px + pad + accent, tw = w - pad * 2 - accent, ty = py + pad;
            // shadowed label: black behind + colour in front, so text never melts into the scene
            void L(float y, string text, Color col, GUIStyle st)
            {
                st.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
                GUI.Label(new Rect(tx + 1.5f, y + 1.5f, tw, rowH), text, st);
                st.normal.textColor = col;
                GUI.Label(new Rect(tx, y, tw, rowH), text, st);
            }

            // Deficit section (only when the deficit HUD is enabled).
            // ▲/✓ are in Unity's built-in Arial; ⚠ (U+26A0) and emoji often aren't -> avoid tofu.
            if (showDef)
            {
                if (total == 0)
                {
                    L(ty, "✓ No deficits", new Color(0.55f, 0.95f, 0.6f), _titleStyle);
                    ty += rowH;
                }
                else
                {
                    // Red alert when something is sustained; a calmer note when items are only dipping.
                    L(ty, flaggedN > 0 ? "▲ Live deficits (" + flaggedN + ")" : "Dipping — none sustained",
                      flaggedN > 0 ? new Color(1f, 0.78f, 0.32f) : new Color(0.92f, 0.94f, 1f), _titleStyle);
                    ty += rowH;
                    var red = new Color(1f, 0.5f, 0.42f);
                    var pend = new Color(0.9f, 0.92f, 0.97f);
                    int drawn = 0;
                    for (int i = 0; i < rows.Length && drawn < cap; i++)
                    {
                        var r = rows[i];
                        if (!r.flagged && !showPending) continue;
                        string txt = r.name + "   " + r.net.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/min"
                                   + (r.flagged ? "" : "  (" + r.streak + "s)");
                        L(ty, txt, r.flagged ? red : pend, _rowStyle);
                        ty += rowH; drawn++;
                    }
                }
            }
            // T99 advisor section: researched tier-ups the player hasn't dismissed yet.
            if (adv.Length > 0)
            {
                string keyName = _cfgAdvAckKey.Value.MainKey.ToString();
                L(ty, "Upgrades available (" + keyName + " to dismiss)", new Color(1f, 0.85f, 0.4f), _titleStyle);
                ty += rowH;
                var advCol = new Color(0.75f, 0.95f, 1f);
                foreach (var a in adv) { L(ty, "+ " + a.text + " researched", advCol, _rowStyle); ty += rowH; }
            }
            GUI.color = saved;
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private void OnDestroy()
        {
            _running = false;
            try { _cfgWatcher?.Dispose(); } catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }
    }

    // Tallies every item produced/consumed by every factory. FactoryProductionStat
    // .GameTick folds productRegister/consumeRegister into its history windows and
    // zeroes them each logic tick — this prefix reads them just before that happens
    // (same approach as the BetterStats mod). Runs on the game's logic thread; the
    // arrays are only read (never written) elsewhere, so a torn read at worst shows
    // a value one tick stale.
    [HarmonyPatch(typeof(FactoryProductionStat), "GameTick")]
    internal static class ProductionTally
    {
        internal static readonly long[] Produced = new long[12000];
        internal static readonly long[] Consumed = new long[12000];

        private static void Prefix(FactoryProductionStat __instance)
        {
            var pr = __instance.productRegister;
            var cr = __instance.consumeRegister;
            if (pr == null || cr == null) return;
            int n = Math.Min(Math.Min(pr.Length, cr.Length), Produced.Length);
            for (int id = 0; id < n; id++)
            {
                if (pr[id] != 0) Produced[id] += pr[id];
                if (cr[id] != 0) Consumed[id] += cr[id];
            }
        }
    }
}
