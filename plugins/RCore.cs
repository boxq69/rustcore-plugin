using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;
using UnityEngine.Networking;
using ConVar;

// Core build by boxq

namespace Oxide.Plugins
{
    [Info("RCore", "rustcore.co", "1.5.0")]
    [Description("Connects a Rust server to RustCore.co")]
    public class RCore : RustPlugin
    {
        #region Configuration

        private const ulong DefaultAvatarSteamId = 76561198708173741UL;
        private const string ApiBaseUrl = "https://api.rustcore.co";

        private class Configuration
        {
            [JsonProperty("AvatarSteamId")]
            public ulong AvatarSteamId = DefaultAvatarSteamId;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try { _config = Config.ReadObject<Configuration>(); }
            catch (Exception ex)
            {
                PrintWarning($"config load failed: {ex.Message}");
                _config = null;
            }

            if (_config == null) _config = new Configuration();
            if (_config.AvatarSteamId < SteamIdBase)
                _config.AvatarSteamId = DefaultAvatarSteamId;

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Mute.Applied"] = "<color=#ff5555><b>You have been muted.</b></color>\nReason: {0}\nTime remaining: <color=#ffaa55>{1}</color>",
                ["Mute.Blocked"] = "<color=#ff5555><b>You are currently muted.</b></color>\nReason: {0}\nTime remaining: <color=#ffaa55>{1}</color>",
                ["Mute.VoiceBlocked"] = "<color=#ff5555><b>You are voice muted.</b></color>\nTime remaining: <color=#ffaa55>{0}</color>",
                ["Mute.Removed"] = "<color=#55ff55><b>You have been unmuted.</b></color>",
                ["Mute.Announced"] = "<color=#ffaa55>{0}</color> received a mute {1}. Reason: {2}",
                ["Ban.Announced"] = "<color=#ff5555>{0}</color> received a ban {1}. Reason: {2}",
                ["Unmute.Announced"] = "<color=#55ff55>{0}</color> is no longer muted.",
                ["Unban.Announced"] = "<color=#55ff55>{0}</color> is no longer banned.",
                ["Kick.Default"] = "Kicked by an administrator",
                ["Autokick.Default"] = "You are not allowed on this server.",
                ["Autokick.Unavailable"] = "Unable to verify access right now. Please try again shortly.",
                ["Runtime.Unavailable"] = "This feature is unavailable right now. Please try again later.",
                ["Runtime.Cooldown"] = "Please wait {0}s before using this command again."
            }, this);
        }

        private string Message(string key, BasePlayer player, params object[] args)
        {
            string template = lang.GetMessage(key, this, player?.UserIDString);
            if (args.Length == 0) return template;
            try { return string.Format(template, args); }
            catch (FormatException)
            {
                WarnThrottled("lang", $"message '{key}' has an invalid format string");
                return template;
            }
        }
        
        #endregion

        #region State

        private const int ProtocolVersion = 2;
        private const int MaxHttpConcurrent = 12;
        private const int MaxAuthHttpConcurrent = 6;
        private const int MaxAuthQueueDepth = 64;
        private const int MaxControlQueueDepth = 128;
        private const int MaxBulkQueueDepth = 256;
        private const int MaxPolicyPending = 512;
        private const int MaxPolicyBatch = 100;
        private const int MaxPolicyAttempts = 3;

        private const string PairPath = "/api/setup/pair";
        private const string VerifyPath = "/api/ingest/verify";
        private const string HeartbeatPath = "/api/ingest/heartbeat";
        private const string QueuePath = "/api/ingest/queue";
        private const string AckPath = "/api/ingest/queue/ack";
        private const string ChatPath = "/api/ingest/chat";
        private const string ReportsPath = "/api/ingest/reports";
        private const string KillsPath = "/api/ingest/deaths";
        private const string WipePath = "/api/ingest/wipe";
        private const string AutokickCheckPath = "/api/ingest/autokick-check";
        private const string MapPath = "/api/ingest/map";
        private const string PositionsPath = "/api/ingest/positions";
        private const string DrawingsPath = "/api/ingest/drawings";
        private const string DrawingsGonePath = "/api/ingest/drawings/gone";
        private const string DrawingsReconcilePath = "/api/ingest/drawings/reconcile";
        private const string NamedContentPath = "/api/ingest/named-content";
        private const string NamedContentGonePath = "/api/ingest/named-content/gone";
        private const string AudioPath = "/api/ingest/audio";
        private const string AudioGonePath = "/api/ingest/audio/gone";
        private const string AudioReconcilePath = "/api/ingest/audio/reconcile";
        private const string CommandPath = "/api/ingest/command";
        private const string EventsPath = "/api/ingest/events";

        private const string IdentityFileName = "RCore/identity";
        private const string MuteFileName = "RCore/mutes";
        private const string SpoolFileName = "RCore/spool";
        private const string TaskFileName = "RCore/tasks";
        private const string FingerprintFileName = "RCore/content";
        private const int MaxDrawingBytes = 4 * 1024 * 1024;
        private const int MaxAudioBytes = 2 * 1024 * 1024;
        private const int MaxReconcileNetIds = 20000;
        private const float FrameBudgetSeconds = 0.003f;
        private const float WarnInterval = 300f;
        private const float SpoolWriteDelay = 5f;

        private const ulong SteamIdBase = 76561197960265728UL;
        private const float RequestTimeoutSeconds = 10f;
        private const float IdleHeartbeatSeconds = 15f;
        private const float FullSyncIntervalSeconds = 30f;
        private const float MapPositionsInterval = 4f;
        private const float MapCheckInterval = 20f;
        private const int MapChunkBytes = 512 * 1024;
        private const float MapRequestTimeoutSeconds = 120f;
        private const int MapOceanMarginPx = 500;
        private const float MapRenderTargetPx = 2048f;
        private const float QueuePollInterval = 5f;
        private const float ChatFlushInterval = 3f;
        private const float ReportsFlushInterval = 5f;
        private const float CombatFlushInterval = 10f;
        private const float CombatLogGapSeconds = 20f;
        private const float CombatLogDepthSeconds = 30f;
        private const int MaxCombatLogEntries = 500;
        private const int MaxBufferItems = 5000;
        private const int MaxBatchItems = 50;
        private const int MaxDisconnectsPerHeartbeat = 200;
        private const int MaxTeamChangesPerHeartbeat = 200;
        private const int MaxSendRetries = 5;
        private const int UnlinkThreshold = 5;
        private const int MaxTasksPerAck = 200;
        private const long TaskRetentionSeconds = 86400;
        private const float SpoolWriteInterval = 60f;
        private const float LinkVerifyInterval = 30f;
        private const float MuteSweepInterval = 30f;
        private const float FullSyncDebounce = 5f;
        private const float TaskSaveInterval = 5f;
        private const int MaxProcessedTasks = 5000;
        private const float PolicyFlushInterval = 1.5f;
        private const float AutokickCacheTtl = 45f;
        private const int MaxAutokickCacheEntries = 1024;
        private const float VoiceToastCooldown = 3f;
        private const float WoundRecordTtlSeconds = 900f;
        private const float DropWarnInterval = 300f;

        private const string CodeServerDeleted = "SERVER_DELETED";
        private const string CodeKeyRevoked = "KEY_REVOKED";
#if CARBON
        private const string Framework = "carbon";
#else
        private const string Framework = "oxide";
#endif

        private static RCore Instance;

        private Configuration _config;
        private IdentityData _identity;
        private MuteStore _mutes;
        private ProcessedTaskStore _tasks;
        private RCoreEngine _engine;

        private int _keyGeneration;

        private bool _commandsRegistered;
        private bool _pendingWipe;
        private bool _tasksDirty;

        private readonly Dictionary<string, HitRecord> _woundedHits = new Dictionary<string, HitRecord>();
        private readonly Dictionary<ulong, float> _voiceToastCooldowns = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, AfkTracker> _afkTrackers = new Dictionary<ulong, AfkTracker>();
        private readonly Dictionary<string, AutokickCacheEntry> _autokickCache = new Dictionary<string, AutokickCacheEntry>();
        private readonly Dictionary<string, PolicyPending> _policyPending = new Dictionary<string, PolicyPending>();
        // Connection can be between JoinedGame and Spawn, no list yet. Finish kick in OnPlayerConnected.
        private readonly Dictionary<ulong, string> _pendingKick = new Dictionary<ulong, string>();
        private bool _policyInFlight;
        private int _policyFailures;

        private string _cachedDisplayIp;
        private bool _publicIpLookupStarted;

        private readonly ContentIndex _content = new ContentIndex();
        private readonly Dictionary<int, bool> _cassetteItemIds = new Dictionary<int, bool>();
        private ContentFingerprintStore _fingerprints;
        private bool _fingerprintsDirty;
        private int _fingerprintScopeFrame = -1;

        private bool IsLinked => _identity != null && !string.IsNullOrEmpty(_identity.ApiKey);

        #endregion

        #region Lifecycle

        private void Init()
        {
            Instance = this;
            LoadConfig();
            LoadIdentity();
            LoadMutes();
            LoadProcessedTasks();
            EnsureStores();
            RegisterCommands();
        }

        private void EnsureStores()
        {
            if (_mutes == null) _mutes = new MuteStore();
            if (_mutes.ActiveMutes == null) _mutes.ActiveMutes = new Dictionary<string, MuteInfo>();
            if (_tasks == null) _tasks = new ProcessedTaskStore();
            if (_tasks.Processed == null) _tasks.Processed = new Dictionary<string, ProcessedTask>();
            if (_tasks.PendingAck == null) _tasks.PendingAck = new Dictionary<string, TaskResult>();
        }

        private T TryLoad<T>(string file, string label) where T : class, new()
        {
            try { return Interface.Oxide.DataFileSystem.ReadObject<T>(file) ?? new T(); }
            catch (Exception ex)
            {
                PrintWarning($"{label} load failed: {ex.Message}");
                return new T();
            }
        }

        private bool TrySave(string file, object data, string label)
        {
            if (data == null) return false;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(file, data);
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning($"{label} save failed: {ex.Message}");
                return false;
            }
        }

        private void OnServerInitialized()
        {
            RegisterCommands();
            SweepExpiredMutes();
            BeginPublicIpLookup();

            var host = new GameObject("RCore.Engine");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _engine = host.AddComponent<RCoreEngine>();

            Puts($"RCore v{Version} loaded.");
            if (!IsLinked)
                Puts("This server is not connected to RustCore. Get a code at rustcore.co and run: rcore.pair <CODE>");
        }

        private void Unload()
        {
            if (_engine != null)
            {
                // Destroy() waits a frame unload needs workers gone now
                UnityEngine.Object.DestroyImmediate(_engine.gameObject);
                _engine = null;
            }

            SaveProcessedTasks();
            SaveFingerprintsIfDirty();
            _woundedHits.Clear();
            _voiceToastCooldowns.Clear();
            _afkTrackers.Clear();
            _autokickCache.Clear();
            _policyPending.Clear();
            _pendingKick.Clear();
            _policyInFlight = false;
            _policyFailures = 0;
            Instance = null;
        }

        private void RegisterCommands()
        {
            if (_commandsRegistered) return;
            cmd.AddConsoleCommand("rcore.pair", this, nameof(CmdPair));
            cmd.AddConsoleCommand("global.rcore.pair", this, nameof(CmdPair));
            cmd.AddConsoleCommand("rcore.unpair", this, nameof(CmdUnpair));
            cmd.AddConsoleCommand("global.rcore.unpair", this, nameof(CmdUnpair));
            _commandsRegistered = true;
        }

        private void LoadIdentity()
        {
            _identity = TryLoad<IdentityData>(IdentityFileName, "identity");
            if (string.IsNullOrEmpty(_identity.InstalledAtUtc)) _identity.InstalledAtUtc = UtcNow();
            if (!string.IsNullOrEmpty(_identity.ApiKey)) _keyGeneration = 1;
            _pendingWipe = _identity.PendingWipe;
        }

        private void SaveIdentity()
        {
            if (_identity == null) return;
            TrySave(IdentityFileName, _identity, "identity");
        }

        private void SetPendingWipe(bool pending)
        {
            _pendingWipe = pending;
            if (_identity == null) return;
            if (_identity.PendingWipe == pending) return;
            _identity.PendingWipe = pending;
            SaveIdentity();
        }

        private void LoadMutes()
        {
            _mutes = TryLoad<MuteStore>(MuteFileName, "mutes");
            if (_mutes.ActiveMutes == null) _mutes.ActiveMutes = new Dictionary<string, MuteInfo>();
        }

        private void SaveMutes()
        {
            if (_mutes?.ActiveMutes == null) return;
            TrySave(MuteFileName, _mutes, "mutes");
        }

        private void ReconcileMutes(List<MuteSyncDto> snapshot)
        {
            if (snapshot == null || _mutes?.ActiveMutes == null) return;

            var desired = new Dictionary<string, MuteInfo>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Count; i++)
            {
                var row = snapshot[i];
                if (row == null || string.IsNullOrEmpty(row.SteamId)) continue;
                if (!IsRealSteamId(row.SteamId)) continue;

                var expiry = ParseExpiry(row.ExpiresAt);
                if (expiry != null && DateTimeOffset.UtcNow > expiry.Value) continue;

                desired[row.SteamId] = new MuteInfo
                {
                    Reason = string.IsNullOrEmpty(row.Reason) ? "Violation" : row.Reason,
                    ExpiresAt = row.ExpiresAt,
                    ExpiresAtParsed = expiry
                };
            }

            // Flags are set directly: BetterChat shadows the mute/unmute commands.
            var online = new Dictionary<string, BasePlayer>(BasePlayer.activePlayerList.Count, StringComparer.Ordinal);
            for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
            {
                var p = BasePlayer.activePlayerList[i];
                if (p != null && p.IsConnected) online[p.UserIDString] = p;
            }

            bool changed = false;
            var toClear = new List<string>();
            foreach (var key in _mutes.ActiveMutes.Keys)
            {
                if (!desired.ContainsKey(key)) toClear.Add(key);
            }
            for (int i = 0; i < toClear.Count; i++)
            {
                _mutes.ActiveMutes.Remove(toClear[i]);
                changed = true;
                BasePlayer player;
                if (online.TryGetValue(toClear[i], out player))
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, false);
            }

            foreach (var pair in desired)
            {
                MuteInfo existing;
                bool same = _mutes.ActiveMutes.TryGetValue(pair.Key, out existing)
                    && existing != null
                    && string.Equals(existing.Reason, pair.Value.Reason, StringComparison.Ordinal)
                    && string.Equals(existing.ExpiresAt ?? "", pair.Value.ExpiresAt ?? "", StringComparison.Ordinal);
                if (!same)
                {
                    _mutes.ActiveMutes[pair.Key] = pair.Value;
                    changed = true;
                }

                BasePlayer player;
                if (online.TryGetValue(pair.Key, out player) && !player.HasPlayerFlag(BasePlayer.PlayerFlags.ChatMute))
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, true);
            }

            if (changed) SaveMutes();
        }

        // FindByID sometimes misses connected players
        private static BasePlayer FindConnectedPlayer(string steamId)
        {
            if (!ulong.TryParse(steamId, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong uid))
                return null;

            var player = BasePlayer.FindByID(uid);
            if (player != null && player.IsConnected) return player;

            for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
            {
                var p = BasePlayer.activePlayerList[i];
                if (p != null && p.userID == uid && p.IsConnected) return p;
            }
            return null;
        }

        private void LoadProcessedTasks()
        {
            _tasks = TryLoad<ProcessedTaskStore>(TaskFileName, "tasks");
            if (_tasks.Processed == null) _tasks.Processed = new Dictionary<string, ProcessedTask>();
            if (_tasks.PendingAck == null) _tasks.PendingAck = new Dictionary<string, TaskResult>();
            PruneProcessedTasks();
        }

        private const uint MinAuthLevelForCommands = 2;

        private static bool IsOperator(ConsoleSystem.Arg arg) =>
            arg?.Connection == null || arg.Connection.authLevel >= MinAuthLevelForCommands;

        private void Reply(ConsoleSystem.Arg arg, string message)
        {
            if (arg?.Connection != null) arg.ReplyWith(message);
            else Puts(message);
        }

        private bool RequireOperator(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (IsOperator(arg)) return true;
            arg.ReplyWith("You do not have permission to use this command.");
            return false;
        }

        private void CmdPair(ConsoleSystem.Arg arg)
        {
            if (!RequireOperator(arg)) return;

            if (arg.Args == null || arg.Args.Length < 1)
            {
                Reply(arg, "Usage: rcore.pair <CODE>");
                return;
            }

            var link = _engine?.Link;
            if (link == null)
            {
                Reply(arg, "RCore is still starting. Try again shortly.");
                return;
            }

            var code = arg.GetString(0, string.Empty).Trim();
            if (code.Length == 0)
            {
                Reply(arg, "Usage: rcore.pair <CODE>");
                return;
            }

            Reply(arg, "Pairing started. Check the server console.");
            link.Pair(code);
        }

        private void CmdUnpair(ConsoleSystem.Arg arg)
        {
            if (!RequireOperator(arg)) return;

            if (!IsLinked)
            {
                Reply(arg, "This server is not connected to RustCore.");
                return;
            }

            var link = _engine?.Link;
            if (link == null)
            {
                Reply(arg, "RCore is still starting. Try again shortly.");
                return;
            }

            link.UnlinkByOperator();
            Reply(arg, "This server is no longer connected to RustCore. Run rcore.pair <CODE> to connect it again.");
        }

        #endregion

        #region Workers

        private class RCoreEngine : MonoBehaviour
        {
            public static RCoreEngine Current;

            public LinkWorker Link;
            public HttpPump Http;
            public StateWorker State;
            public QueueWorker Queue;
            public ChatWorker Chat;
            public ReportWorker Reports;
            public CombatWorker Combat;
            public MapWorker Map;
            public PositionsWorker Positions;
            public DrawingsWorker Drawings;
            public NamedContentWorker NamedContent;
            public AudioWorker Audio;
            public RuntimeWorker Runtime;

            private GameObject _session;
            private SpoolStore _spool = new SpoolStore();
            private bool _spoolDirty;
            private readonly HashSet<string>[] _spoolIds = new HashSet<string>[SpoolKindCount];

            private void Awake()
            {
                Current = this;
                LoadSpool();
                if (Instance != null) StartCoroutine(Instance._content.Seed());

                Http = gameObject.AddComponent<HttpPump>();
                Link = gameObject.AddComponent<LinkWorker>();
                Link.OnLinked += OpenSession;
                Link.OnUnlinked += CloseSession;

                InvokeRepeating(nameof(TickMutes), UnityEngine.Random.Range(0f, 5f), MuteSweepInterval);
                InvokeRepeating(nameof(TickSpool), UnityEngine.Random.Range(0f, 5f), SpoolWriteInterval);
                InvokeRepeating(nameof(TickTaskSave), TaskSaveInterval, TaskSaveInterval);

                if (Instance != null && Instance.IsLinked) OpenSession();
            }

            private void TickMutes()
            {
                Instance?.SweepExpiredMutes();
            }

            private void TickSpool()
            {
                RecoverSpool();
                WriteSpool();
                Instance?.SaveFingerprintsIfDirty();
            }

            private void TickTaskSave()
            {
                Instance?.FlushProcessedTasksIfDirty();
            }

            private void OnDestroy()
            {
                CancelInvoke();
                CloseSession();
                WriteSpool(true);
                Instance?.FlushProcessedTasksIfDirty(force: true);
                Instance?.SaveFingerprintsIfDirty();
                if (Current == this) Current = null;
            }

            private void OpenSession()
            {
                if (_session != null) return;

                _session = new GameObject("RCore.Session");
                _session.transform.SetParent(transform, false);

                State = _session.AddComponent<StateWorker>();
                Queue = _session.AddComponent<QueueWorker>();
                _session.AddComponent<PolicyWorker>();
                Chat = _session.AddComponent<ChatWorker>();
                Reports = _session.AddComponent<ReportWorker>();
                Combat = _session.AddComponent<CombatWorker>();
                Map = _session.AddComponent<MapWorker>();
                Positions = _session.AddComponent<PositionsWorker>();
                Drawings = _session.AddComponent<DrawingsWorker>();
                NamedContent = _session.AddComponent<NamedContentWorker>();
                Audio = _session.AddComponent<AudioWorker>();
                Runtime = _session.AddComponent<RuntimeWorker>();

                State.RequestSync(0.5f);
                Drawings.RequestFullSync(8f);
                NamedContent.RequestFullSync(10f);
                Audio.RequestFullSync(12f);
            }

            private void CloseSession()
            {
                if (_session == null) return;

                var session = _session;
                _session = null;
                State = null; Queue = null; Chat = null;
                Reports = null; Combat = null; Map = null; Positions = null; Drawings = null;
                NamedContent = null; Audio = null; Runtime = null;

                UnityEngine.Object.DestroyImmediate(session);
                WriteSpool(true);
            }

            public void HandleTasks(List<QueueTaskDto> tasks) => Queue?.Dispatch(tasks);

            public void RequestFullSync(float delay) => State?.RequestSync(delay);

            public void RecordDisconnect(string steamId, string reason) => State?.EnqueueDisconnect(steamId, reason);

            public void RecordTeamChange(string kind, string teamId, string actorSteamId, string targetSteamId)
                => State?.EnqueueTeamChange(kind, teamId, actorSteamId, targetSteamId);

            public void RaiseAlert(string type, string message, object data = null)
            {
                var alert = new AlertDto
                {
                    EventId = NewEventId(),
                    Type = type,
                    Message = message,
                    CreatedAt = UtcNow(),
                    Data = data == null ? null : JToken.FromObject(data)
                };

                if (State != null) State.EnqueueAlert(alert);
                else SpoolItems(SpoolKind.Alerts, alert);
            }

            public void SpoolItems<T>(SpoolKind kind, T item)
            {
                if (item == null) return;
                if (AddToSpool(kind, JToken.FromObject(item))) TrimSpool(kind);
            }

            public void SpoolItems<T>(SpoolKind kind, List<T> items)
            {
                if (items == null || items.Count == 0) return;
                bool added = false;
                foreach (var item in items)
                {
                    if (item == null) continue;
                    added |= AddToSpool(kind, JToken.FromObject(item));
                }
                if (added) TrimSpool(kind);
            }

            private bool AddToSpool(SpoolKind kind, JToken token)
            {
                string eventId = SpoolEventId(token);
                if (eventId != null && !SpoolIds(kind).Add(eventId)) return false;
                Bucket(kind).Add(token);
                _spoolDirty = true;
                return true;
            }

            private void TrimSpool(SpoolKind kind)
            {
                var bucket = Bucket(kind);
                int excess = bucket.Count - MaxBufferItems;
                if (excess <= 0) return;
                ForgetSpoolIds(kind, bucket, excess);
                bucket.RemoveRange(0, excess);
                _spoolDirty = true;
            }

            private HashSet<string> SpoolIds(SpoolKind kind)
            {
                var ids = _spoolIds[(int)kind];
                if (ids != null) return ids;

                ids = new HashSet<string>(StringComparer.Ordinal);
                AddSpoolIds(ids, InFlightBucket(kind));
                AddSpoolIds(ids, Bucket(kind));
                _spoolIds[(int)kind] = ids;
                return ids;
            }

            private static void AddSpoolIds(HashSet<string> ids, List<JToken> bucket)
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    string id = SpoolEventId(bucket[i]);
                    if (id != null) ids.Add(id);
                }
            }

            private void ForgetSpoolIds(SpoolKind kind, List<JToken> bucket, int count)
            {
                var ids = _spoolIds[(int)kind];
                if (ids == null) return;
                for (int i = 0; i < count && i < bucket.Count; i++)
                {
                    string id = SpoolEventId(bucket[i]);
                    if (id != null) ids.Remove(id);
                }
            }

            private List<T> Claim<T>(SpoolKind kind, int max)
            {
                var pending = Bucket(kind);
                var inFlight = InFlightBucket(kind);

                if (inFlight.Count > 0)
                    return DeserializeSpool<T>(kind, inFlight);

                int count = Math.Min(pending.Count, max);
                if (count <= 0) return new List<T>();

                for (int i = 0; i < count; i++)
                    inFlight.Add(pending[i]);
                pending.RemoveRange(0, count);
                ScheduleSpoolWrite();

                return DeserializeSpool<T>(kind, inFlight);
            }

            public void CommitSpool(SpoolKind kind, int sentCount)
            {
                if (sentCount <= 0) return;
                var inFlight = InFlightBucket(kind);
                int n = Math.Min(sentCount, inFlight.Count);
                if (n <= 0) return;
                ForgetSpoolIds(kind, inFlight, n);
                inFlight.RemoveRange(0, n);
                ScheduleSpoolWrite();
            }

            public void ReturnClaimed(SpoolKind kind)
            {
                var pending = Bucket(kind);
                var inFlight = InFlightBucket(kind);
                if (inFlight.Count == 0) return;
                pending.InsertRange(0, inFlight);
                inFlight.Clear();
                TrimSpool(kind);
                ScheduleSpoolWrite();
            }

            private void ScheduleSpoolWrite()
            {
                _spoolDirty = true;
                if (!IsInvoking(nameof(FlushSpool))) Invoke(nameof(FlushSpool), SpoolWriteDelay);
            }

            private void FlushSpool() => WriteSpool();

            private List<T> DeserializeSpool<T>(SpoolKind kind, List<JToken> source)
            {
                var result = new List<T>(source.Count);
                int i = 0;
                while (i < source.Count)
                {
                    T value;
                    if (TryReadSpoolItem(source[i], out value))
                    {
                        result.Add(value);
                        i++;
                        continue;
                    }

                    string id = SpoolEventId(source[i]);
                    if (id != null) _spoolIds[(int)kind]?.Remove(id);
                    source.RemoveAt(i);
                    _spoolDirty = true;
                }
                return result;
            }

            private static bool TryReadSpoolItem<T>(JToken token, out T value)
            {
                try
                {
                    value = token.ToObject<T>();
                    return value != null;
                }
                catch (JsonException ex)
                {
                    WarnThrottled("spool", $"dropping an unreadable {typeof(T).Name}: {ex.Message}");
                    value = default(T);
                    return false;
                }
            }

            private static string SpoolEventId(JToken token)
            {
                if (token == null || token.Type != JTokenType.Object) return null;
                var id = token["eventId"];
                return id != null && id.Type == JTokenType.String ? (string)id : null;
            }

            public void RecoverSpool()
            {
                if (Chat != null && Chat.WantsSpool)
                    Chat.EnqueueRange(Claim<ChatItemDto>(SpoolKind.Chat, Chat.SpoolChunk));

                if (Reports != null && Reports.WantsSpool)
                    Reports.EnqueueRange(Claim<ReportItemDto>(SpoolKind.Reports, Reports.SpoolChunk));

                if (Combat != null && Combat.WantsSpool)
                    Combat.EnqueueRange(Claim<DeathEventDto>(SpoolKind.Deaths, Combat.SpoolChunk));

                if (State == null || !State.Ready) return;

                if (State.WantsAlertSpool)
                {
                    foreach (var alert in Claim<AlertDto>(SpoolKind.Alerts, State.SpoolChunk))
                        State.EnqueueAlert(alert);
                }

                if (State.WantsDisconnectSpool)
                    State.EnqueueDisconnectRange(Claim<DisconnectDto>(SpoolKind.Disconnects, MaxDisconnectsPerHeartbeat));

                if (State.WantsTeamChangeSpool)
                    State.EnqueueTeamChangeRange(Claim<TeamChangeDto>(SpoolKind.TeamChanges, MaxTeamChangesPerHeartbeat));
            }

            public void WriteSpool(bool force = false)
            {
                if (!force && !_spoolDirty) return;
                if (Instance != null && Instance.TrySave(SpoolFileName, _spool, "spool"))
                    _spoolDirty = false;
            }

            private void LoadSpool()
            {
                _spool = Instance != null
                    ? Instance.TryLoad<SpoolStore>(SpoolFileName, "spool")
                    : new SpoolStore();
                if (_spool.ReclaimInFlight())
                {
                    _spoolDirty = true;
                    WriteSpool(true);
                }
                else _spool.Normalize();
            }

            private List<JToken> Bucket(SpoolKind kind) => _spool.Pending[(int)kind];
            private List<JToken> InFlightBucket(SpoolKind kind) => _spool.InFlight[(int)kind];
        }

        private abstract class RCoreWorker : MonoBehaviour
        {
            private static readonly float[] BackoffSteps = { 15f, 30f, 60f, 120f };

            private int _inFlight;
            private float _inFlightSince = -1f;
            private int _sendGeneration;
            private Action _watchdogFail;
            private int _failures;
            private float _backoffUntil;

            protected abstract float Interval { get; }
            protected abstract void Tick();

            protected RCoreEngine Engine => RCoreEngine.Current;

            public bool Ready
            {
                get
                {
                    if (Instance == null || Engine == null) return false;

                    float now = UnityEngine.Time.realtimeSinceStartup;

                    if (_inFlight > 0)
                    {
                        float limit = RequestTimeoutSeconds * 3f;
                        if (_inFlightSince < 0f || now - _inFlightSince <= limit) return false;

                        _sendGeneration++;
                        _inFlight = 0;
                        _inFlightSince = -1f;
                        var fail = _watchdogFail;
                        _watchdogFail = null;
                        fail?.Invoke();
                        return now >= _backoffUntil;
                    }

                    return now >= _backoffUntil;
                }
            }

            private void Awake()
            {
                // desync ticks across servers
                InvokeRepeating(nameof(Tick), UnityEngine.Random.Range(0f, 5f), Mathf.Max(1f, Interval));
            }

            private void OnDestroy()
            {
                CancelInvoke();
                _sendGeneration++;
                _watchdogFail = null;
                Drain();
            }

            protected virtual void Drain() { }

            protected void Send<T>(string path, object payload, Action<T> onSuccess,
                                   Action<int, string> onError = null,
                                   RequestMethod method = RequestMethod.POST) where T : class
            {
                var plugin = Instance;
                if (plugin == null) return;

                string body = null;
                if (payload != null)
                {
                    try
                    {
                        body = JsonConvert.SerializeObject(payload, Formatting.None);
                    }
                    catch (JsonException ex)
                    {
                        WarnThrottled("send", $"{path}: payload not serializable: {ex.Message}");
                        onError?.Invoke(0, null);
                        return;
                    }
                }

                if (_inFlight == 0) _inFlightSince = UnityEngine.Time.realtimeSinceStartup;
                _inFlight++;

                int keyGen = plugin._keyGeneration;
                int sendGen = ++_sendGeneration;
                _watchdogFail = () =>
                {
                    NoteFailure();
                    onError?.Invoke(0, null);
                };

                plugin.Request(path, body, method, (code, response) =>
                {
                    if (this == null) return;
                    if (sendGen != _sendGeneration) return;

                    _inFlight = Mathf.Max(0, _inFlight - 1);
                    if (_inFlight == 0) _inFlightSince = -1f;
                    _watchdogFail = null;

                    Engine?.Link?.NoteResponse(code, response, keyGen);

                    if (keyGen != plugin._keyGeneration)
                    {
                        if (code >= 200 && code < 300) onSuccess?.Invoke(null);
                        else onError?.Invoke(code, response);
                        return;
                    }

                    if (code >= 200 && code < 300)
                    {
                        _failures = 0;
                        _backoffUntil = 0f;

                        T parsed = null;
                        if (!string.IsNullOrEmpty(response))
                        {
                            try
                            {
                                parsed = JsonConvert.DeserializeObject<T>(response);
                            }
                            catch (JsonException ex)
                            {
                                WarnThrottled("send", $"{path}: unreadable response: {ex.Message}");
                            }
                        }

                        onSuccess?.Invoke(parsed);
                        return;
                    }

                    NoteFailure();
                    onError?.Invoke(code, response);
                });
            }

            private void NoteFailure()
            {
                _failures++;
                float delay = BackoffSteps[Mathf.Min(_failures - 1, BackoffSteps.Length - 1)];
                delay *= UnityEngine.Random.Range(0.75f, 1.25f);
                _backoffUntil = UnityEngine.Time.realtimeSinceStartup + delay;
            }
        }

        private abstract class BatchWorker<TItem> : RCoreWorker
        {
            private EventBuffer<TItem> _buffer;
            private List<TItem> _inFlightBatch;
            private int _retries;

            protected abstract string Path { get; }
            protected abstract SpoolKind Kind { get; }

            protected virtual int BatchSize => MaxBatchItems;
            protected virtual object BuildPayload(List<TItem> items) => new BatchPayload<TItem> { Items = items };

            public bool WantsSpool => Ready && Buffer.Count < BatchSize;

            public int SpoolChunk => BatchSize;

            protected EventBuffer<TItem> Buffer
            {
                get
                {
                    if (_buffer == null)
                    {
                        _buffer = new EventBuffer<TItem>(GetType().Name,
                            item => Engine?.SpoolItems(Kind, item));
                    }
                    return _buffer;
                }
            }

            public void Enqueue(TItem item)
            {
                Buffer.Add(item);
                if (Buffer.Count >= BatchSize) Flush();
            }

            public void EnqueueRange(IEnumerable<TItem> items) => Buffer.AddRange(items);

            protected override void Tick() => Flush();

            public void Flush()
            {
                if (!Ready || Buffer.Count == 0) return;

                var batch = Buffer.Take(BatchSize);
                _inFlightBatch = batch;

                Send<JObject>(Path, BuildPayload(batch),
                    _ =>
                    {
                        int sent = _inFlightBatch?.Count ?? 0;
                        _inFlightBatch = null;
                        _retries = 0;
                        Engine?.CommitSpool(Kind, sent);
                    },
                    (code, response) =>
                    {
                        _inFlightBatch = null;
                        Buffer.Requeue(batch);

                        if (++_retries < MaxSendRetries) return;
                        _retries = 0;

                        Engine?.ReturnClaimed(Kind);
                        Engine?.SpoolItems(Kind, Buffer.Take(BatchSize));
                    });
            }

            protected override void Drain()
            {
                Engine?.ReturnClaimed(Kind);
                var pending = Buffer.DrainAll();
                if (_inFlightBatch != null)
                {
                    pending.InsertRange(0, _inFlightBatch);
                    _inFlightBatch = null;
                }
                Engine?.SpoolItems(Kind, pending);
            }
        }

        private class LinkWorker : RCoreWorker
        {
            public event Action OnLinked;
            public event Action OnUnlinked;

            private int _fatalStreak;
            private bool _pairPending;

            protected override float Interval => LinkVerifyInterval;

            protected override void Tick()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;

                Send<VerifyResponse>(VerifyPath, null,
                    response =>
                    {
                        if (response == null || string.IsNullOrEmpty(response.ServerId)) return;
                        if (response.ServerId == Instance._identity.ServerId) return;

                        Instance._identity.ServerId = response.ServerId;
                        Instance._identity.ProjectId = response.ProjectId;
                        Instance.SaveIdentity();
                    },
                    null, RequestMethod.GET);
            }

            public void Pair(string code)
            {
                if (Instance == null) return;

                if (string.IsNullOrEmpty(code)) return;

                if (_pairPending)
                {
                    Announce("Pairing already in progress.");
                    return;
                }

                _pairPending = true;

                var currentServerId = Instance.IsLinked && !string.IsNullOrEmpty(Instance._identity.ServerId)
                    ? Instance._identity.ServerId
                    : null;

                var payload = new PairRequest
                {
                    Code = code,
                    ServerName = ConVar.Server.hostname ?? "Rust Server",
                    Port = ConVar.Server.port,
                    DisplayIp = Instance.ResolveDisplayIp(),
                    Version = Instance.Version.ToString(),
                    ProtocolVersion = ProtocolVersion,
                    Framework = Framework,
                    CurrentServerId = currentServerId,
                    HeaderImage = ConVar.Server.headerimage,
                    LogoImage = ConVar.Server.logoimage
                };

                string body = JsonConvert.SerializeObject(payload, Formatting.None);
                Instance.Request(PairPath, body, RequestMethod.POST, (status, response) =>
                {
                    _pairPending = false;
                    if (Instance == null) return;

                    if (status != 200 && status != 201)
                    {
                        Announce($"Pairing failed: {DescribeFailure(status, response)}");
                        return;
                    }

                    PairResponse parsed = null;
                    try
                    {
                        parsed = JsonConvert.DeserializeObject<PairResponse>(response ?? "");
                    }
                    catch (JsonException ex)
                    {
                        WarnThrottled("pair", $"unreadable pair response: {ex.Message}");
                    }

                    var data = parsed?.Data;
                    if (data == null || string.IsNullOrEmpty(data.ApiKey))
                    {
                        Announce("Pairing failed. Please try again with a new code.");
                        return;
                    }

                    Instance._identity.ApiKey = data.ApiKey;
                    Instance._identity.ServerId = data.ServerId;
                    Instance._identity.ProjectId = data.ProjectId;
                    Instance._identity.PairedAtUtc = UtcNow();
                    Instance._identity.LastError = null;
                    Instance._keyGeneration++;
                    Instance.ClearAutokickState();
                    Instance.SaveIdentity();

                    _fatalStreak = 0;
                    Announce("This server is now connected to RustCore.");
                    OnLinked?.Invoke();
                    Interface.CallHook("RCore_OnLinked", data.ServerId, data.ProjectId);
                }, false);
            }

            public void NoteResponse(int code, string body, int requestKeyGeneration = -1)
            {
                if (Instance == null) return;

                if (requestKeyGeneration >= 0 && requestKeyGeneration != Instance._keyGeneration) return;
                if (_pairPending) return;

                if (code >= 200 && code < 300)
                {
                    _fatalStreak = 0;
                    return;
                }

                if (code != 401 && code != 404) return;
                if (!IsRevocation(body)) return;

                _fatalStreak++;
                if (_fatalStreak < UnlinkThreshold) return;

                Unlink($"RustCore revoked the link ({code}).", true);
            }

            private static bool IsRevocation(string body)
            {
                string code, ignoredMessage;
                return TryReadErrorCode(body, out code, out ignoredMessage)
                       && (string.Equals(code, CodeServerDeleted, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(code, CodeKeyRevoked, StringComparison.OrdinalIgnoreCase));
            }

            private static string DescribeFailure(int status, string body)
            {
                if (status == 0) return "no response from RustCore.";

                string code, message;
                if (TryReadErrorCode(body, out code, out message))
                    return string.IsNullOrEmpty(message) ? code : message;

                return $"HTTP {status}.";
            }

            public void UnlinkByOperator()
            {
                if (Instance == null || !Instance.IsLinked) return;
                Unlink("Operator ran rcore.unpair.", false);
            }

            private void Unlink(string reason, bool announce)
            {
                if (Instance == null) return;

                if (announce)
                    Announce("This server is no longer connected to RustCore. Run rcore.pair <CODE> to connect it again.");

                Instance._identity.ApiKey = null;
                Instance._identity.ServerId = null;
                Instance._identity.ProjectId = null;
                Instance._identity.PairedAtUtc = null;
                Instance._identity.LastError = reason;
                Instance._keyGeneration++;
                Instance.ClearAutokickState();
                Instance.SaveIdentity();

                _fatalStreak = 0;
                // unlink only, reload keeps groups
                Engine?.Runtime?.ResetForUnlink();
                OnUnlinked?.Invoke();
                Interface.CallHook("RCore_OnUnlinked", reason);
            }
        }

        private class StateWorker : RCoreWorker
        {
            private EventBuffer<AlertDto> _alerts;
            private EventBuffer<DisconnectDto> _disconnects;
            private EventBuffer<TeamChangeDto> _teamChanges;
            private List<AlertDto> _alertsInFlight;
            private List<DisconnectDto> _disconnectsInFlight;
            private List<TeamChangeDto> _teamChangesInFlight;
            private float _lastFullSync = -999f;
            private float _scheduledSyncAt = -1f;
            private bool _forceFullSync = true;

            protected override float Interval => IdleHeartbeatSeconds;

            private EventBuffer<AlertDto> Alerts =>
                _alerts ?? (_alerts = new EventBuffer<AlertDto>("alerts",
                    item => Engine?.SpoolItems(SpoolKind.Alerts, item)));

            private EventBuffer<DisconnectDto> Disconnects =>
                _disconnects ?? (_disconnects = new EventBuffer<DisconnectDto>("disconnects",
                    item => Engine?.SpoolItems(SpoolKind.Disconnects, item)));

            private EventBuffer<TeamChangeDto> TeamChanges =>
                _teamChanges ?? (_teamChanges = new EventBuffer<TeamChangeDto>("teamChanges",
                    item => Engine?.SpoolItems(SpoolKind.TeamChanges, item)));

            public bool WantsAlertSpool => Ready && Alerts.Count < MaxBatchItems;
            public bool WantsDisconnectSpool => Ready && Disconnects.Count < MaxDisconnectsPerHeartbeat;
            public bool WantsTeamChangeSpool => Ready && TeamChanges.Count < MaxTeamChangesPerHeartbeat;

            public int SpoolChunk => MaxBatchItems;

            public void EnqueueAlert(AlertDto alert) => Alerts.Add(alert);

            public void EnqueueDisconnect(string steamId, string reason)
            {
                if (string.IsNullOrEmpty(steamId)) return;
                string trimmed = string.IsNullOrWhiteSpace(reason) ? "Disconnected" : reason.Trim();
                if (trimmed.Length > 256) trimmed = trimmed.Substring(0, 256);
                Disconnects.Add(new DisconnectDto { EventId = NewEventId(), SteamId = steamId, Reason = trimmed });
            }

            public void EnqueueDisconnectRange(List<DisconnectDto> items)
            {
                if (items == null || items.Count == 0) return;
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrEmpty(item.SteamId)) continue;
                    if (string.IsNullOrEmpty(item.EventId)) item.EventId = NewEventId();
                    Disconnects.Add(item);
                }
            }

            public void EnqueueTeamChange(string kind, string teamId, string actorSteamId, string targetSteamId)
            {
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(teamId)) return;
                TeamChanges.Add(new TeamChangeDto
                {
                    EventId = NewEventId(),
                    Kind = kind,
                    TeamId = teamId,
                    ActorSteamId = actorSteamId,
                    TargetSteamId = targetSteamId
                });
            }

            public void EnqueueTeamChangeRange(List<TeamChangeDto> items)
            {
                if (items == null || items.Count == 0) return;
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Kind) || string.IsNullOrEmpty(item.TeamId)) continue;
                    if (string.IsNullOrEmpty(item.EventId)) item.EventId = NewEventId();
                    TeamChanges.Add(item);
                }
            }

            public void RequestSync(float delay)
            {
                _forceFullSync = true;

                float now = UnityEngine.Time.realtimeSinceStartup;
                float at = Mathf.Max(now + Mathf.Max(0.1f, delay), _lastFullSync + FullSyncDebounce);
                if (_scheduledSyncAt > 0f && _scheduledSyncAt <= at) return;

                _scheduledSyncAt = at;
                CancelInvoke(nameof(ScheduledSync));
                Invoke(nameof(ScheduledSync), at - now);
            }

            private void ScheduledSync()
            {
                _scheduledSyncAt = -1f;
                if (!_forceFullSync) return;
                if (!Ready)
                {
                    RequestSync(1f);
                    return;
                }
                SyncNow();
            }

            protected override void Tick() => SyncNow();

            private void SyncNow()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;

                Instance.SendWipe();

                float now = UnityEngine.Time.realtimeSinceStartup;
                bool fullSync = _forceFullSync || (now - _lastFullSync) >= FullSyncIntervalSeconds;
                _forceFullSync = false;
                if (fullSync) _lastFullSync = now;

                var payload = new HeartbeatPayload
                {
                    ServerId = Instance._identity.ServerId,
                    IsFullSync = fullSync,
                    Timestamp = UtcNow(),
                    Fps = (int)Performance.current.frameRate,
                    Entities = fullSync ? BaseNetworkable.serverEntities.Count : 0,
                    Players = fullSync ? Instance.SnapshotPlayers() : null,
                    Server = fullSync ? Instance.SnapshotServer() : null,
                    Teams = fullSync ? Instance.SnapshotTeams() : null,
                    Runtime = fullSync ? Engine?.Runtime?.Capability() : null
                };

                _alertsInFlight = Alerts.Take(MaxBatchItems);
                if (_alertsInFlight.Count > 0) payload.Alerts = _alertsInFlight;

                _disconnectsInFlight = Disconnects.Take(MaxDisconnectsPerHeartbeat);
                if (_disconnectsInFlight.Count > 0) payload.Disconnects = _disconnectsInFlight;

                _teamChangesInFlight = TeamChanges.Take(MaxTeamChangesPerHeartbeat);
                if (_teamChangesInFlight.Count > 0) payload.TeamChanges = _teamChangesInFlight;

                Send<HeartbeatResponse>(HeartbeatPath, payload,
                    response =>
                    {
                        int alertSent = _alertsInFlight?.Count ?? 0;
                        int disconnectSent = _disconnectsInFlight?.Count ?? 0;
                        int teamSent = _teamChangesInFlight?.Count ?? 0;
                        _alertsInFlight = null;
                        _disconnectsInFlight = null;
                        _teamChangesInFlight = null;
                        Engine?.CommitSpool(SpoolKind.Alerts, alertSent);
                        Engine?.CommitSpool(SpoolKind.Disconnects, disconnectSent);
                        Engine?.CommitSpool(SpoolKind.TeamChanges, teamSent);
                        if (response?.Mutes != null)
                            Instance?.ReconcileMutes(response.Mutes);
                        if (response?.Tasks != null && response.Tasks.Count > 0)
                            Engine?.HandleTasks(response.Tasks);
                        if (response != null && response.MapNeeded)
                            Engine?.Map?.RequestDeliver();
                        // missing = keep what we have
                        if (response?.Manifest != null)
                            Engine?.Runtime?.ApplyFromBackend(response.Manifest, response.ManifestVersion);
                        if (response?.Groups != null)
                            Engine?.Runtime?.ReconcileGroups(response.Groups);
                    },
                    (code, body) =>
                    {
                        if (_alertsInFlight != null) Alerts.Requeue(_alertsInFlight);
                        _alertsInFlight = null;
                        if (_disconnectsInFlight != null) Disconnects.Requeue(_disconnectsInFlight);
                        _disconnectsInFlight = null;
                        if (_teamChangesInFlight != null) TeamChanges.Requeue(_teamChangesInFlight);
                        _teamChangesInFlight = null;
                    });
            }

            protected override void Drain()
            {
                Engine?.ReturnClaimed(SpoolKind.Alerts);
                Engine?.ReturnClaimed(SpoolKind.Disconnects);
                Engine?.ReturnClaimed(SpoolKind.TeamChanges);
                FlushDrain(SpoolKind.Alerts, Alerts, ref _alertsInFlight);
                FlushDrain(SpoolKind.Disconnects, Disconnects, ref _disconnectsInFlight);
                FlushDrain(SpoolKind.TeamChanges, TeamChanges, ref _teamChangesInFlight);
            }

            private void FlushDrain<T>(SpoolKind kind, EventBuffer<T> buffer, ref List<T> inFlight)
            {
                var pending = buffer.DrainAll();
                if (inFlight != null)
                {
                    pending.InsertRange(0, inFlight);
                    inFlight = null;
                }
                Engine?.SpoolItems(kind, pending);
            }
        }

        private class ChatWorker : BatchWorker<ChatItemDto>
        {
            protected override float Interval => ChatFlushInterval;
            protected override string Path => ChatPath;
            protected override SpoolKind Kind => SpoolKind.Chat;
        }

        private class ReportWorker : BatchWorker<ReportItemDto>
        {
            protected override float Interval => ReportsFlushInterval;
            protected override string Path => ReportsPath;
            protected override SpoolKind Kind => SpoolKind.Reports;
        }

        private class CombatWorker : BatchWorker<DeathEventDto>
        {
            protected override float Interval => CombatFlushInterval;
            protected override string Path => KillsPath;
            protected override SpoolKind Kind => SpoolKind.Deaths;

            protected override void Tick()
            {
                Instance?.PruneWoundRecords();
                base.Tick();
            }
        }

        private class PositionsWorker : RCoreWorker
        {
            protected override float Interval => MapPositionsInterval;

            protected override void Tick()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;

                var players = new List<object>(BasePlayer.activePlayerList.Count);
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected) continue;
                    var pos = player.transform.position;
                    var eyes = player.eyes;
                    float yaw = eyes != null ? eyes.rotation.eulerAngles.y : player.transform.eulerAngles.y;
                    players.Add(new
                    {
                        steamId = player.UserIDString,
                        x = pos.x,
                        y = pos.y,
                        z = pos.z,
                        yaw
                    });
                }

                Send<object>(PositionsPath, new
                {
                    serverId = Instance._identity.ServerId,
                    timestamp = UtcNow(),
                    players
                }, _ => { });
            }
        }

        private class MapWorker : RCoreWorker
        {
            private string _deliveredIdentity;
            private bool _inFlight;
            private bool _force;
            private int _imageWidth;
            private int _imageHeight;
            private float _padFraction = MapOceanMarginPx / MapRenderTargetPx;
            private string _contentType = "image/png";

            protected override float Interval => MapCheckInterval;

            public void RequestDeliver()
            {
                _force = true;
                if (!_inFlight) Tick();
            }

            protected override void Tick()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;
                if (_inFlight) return;

                string identity = BuildIdentityKey();
                if (!_force && string.Equals(_deliveredIdentity, identity, StringComparison.Ordinal))
                    return;

                _force = false;
                DeliverMap(identity);
            }

            private static string BuildIdentityKey()
            {
                string save = SaveCreatedTimeKey();
                return string.Join("|",
                    ConVar.Server.level ?? "",
                    ConVar.Server.seed.ToString(CultureInfo.InvariantCulture),
                    ConVar.Server.worldsize.ToString(CultureInfo.InvariantCulture),
                    save);
            }

            private static string TryOfficialMapUrl()
            {
                string url = MapUploader.ImageUrl;
                if (string.IsNullOrWhiteSpace(url)) return null;
                url = url.Trim();
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return null;
                return url;
            }

            private void DeliverMap(string identity)
            {
                string officialUrl = TryOfficialMapUrl();
                if (officialUrl != null)
                {
                    _inFlight = true;
                    StartCoroutine(FetchOfficialThenUpload(identity, officialUrl));
                    return;
                }

                DeliverRenderedFallback(identity);
            }

            private IEnumerator FetchOfficialThenUpload(string identity, string url)
            {
                byte[] bytes = null;
                string contentType = null;
                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = Mathf.Max(1, (int)MapRequestTimeoutSeconds);
                    yield return req.SendWebRequest();

                    bool ok = req.result == UnityWebRequest.Result.Success
                              && req.responseCode >= 200 && req.responseCode < 300
                              && req.downloadHandler != null
                              && req.downloadHandler.data != null
                              && req.downloadHandler.data.Length > 0;
                    if (!ok)
                    {
                        _inFlight = false;
                        DeliverRenderedFallback(identity);
                        yield break;
                    }

                    bytes = req.downloadHandler.data;
                    contentType = req.GetResponseHeader("Content-Type");
                }

                if (bytes == null || bytes.Length == 0)
                {
                    _inFlight = false;
                    DeliverRenderedFallback(identity);
                    yield break;
                }

                BeginUpload(identity, bytes, contentType);
            }

            private void DeliverRenderedFallback(string identity)
            {
                float worldSize = Mathf.Max(1f, ConVar.Server.worldsize);
                float scale = Mathf.Clamp(MapRenderTargetPx / worldSize, 0.1f, 4f);

                byte[] bytes;
                int width;
                int height;
                try
                {
                    Color background;
                    bytes = MapImageRenderer.Render(out width, out height, out background, scale, false);
                }
                catch (Exception ex)
                {
                    WarnThrottled("map", $"MapImageRenderer failed: {ex.Message}");
                    return;
                }

                if (bytes == null || bytes.Length == 0 || width <= 0 || height <= 0)
                    return;

                _imageWidth = width;
                _imageHeight = height;
                BeginUpload(identity, bytes, "image/png");
            }

            private static float PadFractionForWidth(int width)
            {
                if (width <= 0) width = (int)MapRenderTargetPx;
                return Mathf.Clamp(MapOceanMarginPx / (float)width, 0f, 0.45f);
            }

            private void BeginUpload(string identity, byte[] bytes, string contentType)
            {
                _contentType = NormalizeImageContentType(contentType, bytes);
                int parsedW;
                int parsedH;
                if (TryReadImageSize(bytes, out parsedW, out parsedH))
                {
                    _imageWidth = parsedW;
                    _imageHeight = parsedH;
                }
                else
                {
                    if (_imageWidth <= 0) _imageWidth = (int)MapRenderTargetPx;
                    if (_imageHeight <= 0) _imageHeight = (int)MapRenderTargetPx;
                }

                _padFraction = PadFractionForWidth(_imageWidth);

                string hash = Sha256Hex(bytes);
                string uploadId = Guid.NewGuid().ToString("N");
                int chunkCount = Math.Max(1, (bytes.Length + MapChunkBytes - 1) / MapChunkBytes);
                _inFlight = true;
                UploadChunk(identity, uploadId, hash, bytes, 0, chunkCount);
            }

            private static string NormalizeImageContentType(string header, byte[] bytes)
            {
                if (!string.IsNullOrWhiteSpace(header))
                {
                    string ct = header.Split(';')[0].Trim().ToLowerInvariant();
                    if (ct == "image/png" || ct == "image/jpeg" || ct == "image/jpg" || ct == "image/webp")
                        return ct == "image/jpg" ? "image/jpeg" : ct;
                }

                if (bytes != null && bytes.Length >= 8
                    && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                    return "image/png";
                if (bytes != null && bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                    return "image/jpeg";
                if (bytes != null && bytes.Length >= 12
                    && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                    && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
                    return "image/webp";
                return "image/png";
            }

            private static bool TryReadImageSize(byte[] bytes, out int width, out int height)
            {
                width = 0;
                height = 0;
                if (bytes == null || bytes.Length < 24) return false;

                if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                {
                    width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                    height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                    return width > 0 && height > 0;
                }

                // JPEG: scan SOF0/SOF2 for height/width.
                if (bytes[0] == 0xFF && bytes[1] == 0xD8)
                {
                    int i = 2;
                    while (i + 8 < bytes.Length)
                    {
                        if (bytes[i] != 0xFF) { i++; continue; }
                        byte marker = bytes[i + 1];
                        if (marker == 0xD9 || marker == 0xDA) break;
                        int segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                        if (segLen < 2) break;
                        if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7)
                            || (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                        {
                            height = (bytes[i + 5] << 8) | bytes[i + 6];
                            width = (bytes[i + 7] << 8) | bytes[i + 8];
                            return width > 0 && height > 0;
                        }
                        i += 2 + segLen;
                    }
                }

                return false;
            }

            private void UploadChunk(string identity, string uploadId, string hash, byte[] bytes, int index, int chunkCount)
            {
                int offset = index * MapChunkBytes;
                int len = Math.Min(MapChunkBytes, bytes.Length - offset);
                byte[] slice = new byte[len];
                Buffer.BlockCopy(bytes, offset, slice, 0, len);

                var headers = Instance.BuildHeaders(true);
                headers["Content-Type"] = "application/octet-stream";
                headers["X-Map-Upload-Id"] = uploadId;
                headers["X-Map-Identity-Key"] = identity;
                headers["X-Map-Chunk-Index"] = index.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-Chunk-Count"] = chunkCount.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-Byte-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-Content-Hash"] = hash;
                headers["X-Map-Level"] = ConVar.Server.level ?? "";
                headers["X-Map-Seed"] = ConVar.Server.seed.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-World-Size"] = ConVar.Server.worldsize.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-Save-Created-Time"] = SaveCreatedTimeKey();
                headers["X-Map-Image-Width"] = _imageWidth.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-Image-Height"] = _imageHeight.ToString(CultureInfo.InvariantCulture);
                headers["X-Map-Pad-Fraction"] = _padFraction.ToString("G", CultureInfo.InvariantCulture);
                headers["X-Map-Content-Type"] = _contentType ?? "image/png";

                var pump = Engine?.Http;
                if (pump == null)
                {
                    _inFlight = false;
                    return;
                }

                pump.Enqueue(
                    Instance.BuildUrl(MapPath),
                    UnityWebRequest.kHttpVerbPOST,
                    headers,
                    null,
                    (code, body) =>
                    {
                        if (code < 200 || code >= 300)
                        {
                            _inFlight = false;
                            return;
                        }
                        if (index + 1 < chunkCount)
                        {
                            UploadChunk(identity, uploadId, hash, bytes, index + 1, chunkCount);
                            return;
                        }
                        _inFlight = false;
                        _deliveredIdentity = identity;
                    },
                    HttpLane.Bulk,
                    slice,
                    (int)MapRequestTimeoutSeconds);
            }
        }

        private enum ContentKind { None, Paintable, Named, Cassette }

        private sealed class EntityReferenceComparer : IEqualityComparer<BaseEntity>
        {
            public static readonly EntityReferenceComparer Shared = new EntityReferenceComparer();
            public bool Equals(BaseEntity a, BaseEntity b) => ReferenceEquals(a, b);
            public int GetHashCode(BaseEntity entity) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entity);
        }

        private class ContentIndex
        {
            private const int SeedBatch = 20000;
            private static readonly Dictionary<uint, bool> CassettePrefabs = new Dictionary<uint, bool>();

            public readonly HashSet<BaseEntity> Paintables = new HashSet<BaseEntity>(EntityReferenceComparer.Shared);
            public readonly HashSet<BaseEntity> Sculptures = new HashSet<BaseEntity>(EntityReferenceComparer.Shared);
            public readonly HashSet<BaseEntity> Named = new HashSet<BaseEntity>(EntityReferenceComparer.Shared);
            public readonly HashSet<BaseEntity> Cassettes = new HashSet<BaseEntity>(EntityReferenceComparer.Shared);

            private bool _seeding;

            public bool Ready { get; private set; }

            public static ContentKind Classify(BaseEntity entity)
            {
                if (entity is Signage
                    || entity is PhotoFrame
                    || entity is CarvablePumpkin
                    || entity is BaseSculpture
                    || entity is PatternFirework
                    || entity is PaintedItemStorageEntity
                    || entity is ISignage)
                    return ContentKind.Paintable;
                if (entity is VendingMachine || entity is SleepingBag) return ContentKind.Named;
                if (IsCassette(entity)) return ContentKind.Cassette;
                return ContentKind.None;
            }

            /// <summary>Cached per prefab, so the hot spawn hook never builds strings.</summary>
            public static bool IsCassette(BaseEntity entity)
            {
                if (entity == null) return false;
                uint prefabId = entity.prefabID;
                bool result;
                if (prefabId != 0 && CassettePrefabs.TryGetValue(prefabId, out result)) return result;

                result = entity.GetType().Name.IndexOf("Cassette", StringComparison.OrdinalIgnoreCase) >= 0
                         || (entity.ShortPrefabName ?? "").IndexOf("cassette", StringComparison.OrdinalIgnoreCase) >= 0;
                if (prefabId != 0) CassettePrefabs[prefabId] = result;
                return result;
            }

            public void Add(BaseNetworkable networkable)
            {
                var entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity.net == null) return;

                switch (Classify(entity))
                {
                    case ContentKind.Paintable:
                        Paintables.Add(entity);
                        if (entity is BaseSculpture) Sculptures.Add(entity);
                        break;
                    case ContentKind.Named:
                        Named.Add(entity);
                        break;
                    case ContentKind.Cassette:
                        Cassettes.Add(entity);
                        break;
                }
            }

            public ContentKind Remove(BaseNetworkable networkable)
            {
                var entity = networkable as BaseEntity;
                if (ReferenceEquals(entity, null)) return ContentKind.None;

                if (Paintables.Remove(entity))
                {
                    Sculptures.Remove(entity);
                    return ContentKind.Paintable;
                }
                if (Named.Remove(entity)) return ContentKind.Named;
                if (Cassettes.Remove(entity)) return ContentKind.Cassette;

                // Before the seed pass finishes an entity may not be indexed yet.
                return Ready ? ContentKind.None : Classify(entity);
            }

            public IEnumerator Seed()
            {
                if (_seeding || Ready) yield break;
                _seeding = true;

                var snapshot = new List<BaseNetworkable>(BaseNetworkable.serverEntities.Count);
                foreach (var networkable in BaseNetworkable.serverEntities) snapshot.Add(networkable);

                for (int i = 0; i < snapshot.Count; i++)
                {
                    Add(snapshot[i]);
                    if ((i + 1) % SeedBatch == 0) yield return null;
                }

                _seeding = false;
                Ready = true;
            }

            /// <summary>Live members of <paramref name="set"/>; prunes entities destroyed without a kill hook.</summary>
            public List<BaseEntity> Live(HashSet<BaseEntity> set)
            {
                var live = new List<BaseEntity>(set.Count);
                List<BaseEntity> dead = null;
                foreach (var entity in set)
                {
                    if (entity == null || entity.IsDestroyed || entity.net == null)
                    {
                        if (dead == null) dead = new List<BaseEntity>();
                        dead.Add(entity);
                        continue;
                    }
                    live.Add(entity);
                }

                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                    {
                        set.Remove(dead[i]);
                        if (set == Paintables) Sculptures.Remove(dead[i]);
                    }
                }
                return live;
            }

            public static List<string> NetIds(List<BaseEntity> entities)
            {
                var ids = new List<string>(entities.Count);
                for (int i = 0; i < entities.Count; i++) ids.Add(NetIdOf(entities[i]));
                return ids;
            }
        }

        /// <summary>
        /// Last value uploaded per content key, persisted so a restart does not re-upload every
        /// sign, bag and cassette. Scoped to server + wipe; a new scope starts empty.
        /// </summary>
        private class ContentFingerprintStore
        {
            public string Scope;
            public Dictionary<string, string> Entries = new Dictionary<string, string>();
        }

        private Dictionary<string, string> Fingerprints()
        {
            if (_fingerprints == null)
            {
                _fingerprints = TryLoad<ContentFingerprintStore>(FingerprintFileName, "content fingerprints");
                if (_fingerprints.Entries == null) _fingerprints.Entries = new Dictionary<string, string>();
                _fingerprintScopeFrame = -1;
            }

            int frame = UnityEngine.Time.frameCount;
            if (frame != _fingerprintScopeFrame)
            {
                _fingerprintScopeFrame = frame;
                string scope = (_identity?.ServerId ?? "") + "#" + WipeIdentityKey();
                if (!string.Equals(_fingerprints.Scope, scope, StringComparison.Ordinal))
                {
                    _fingerprints.Scope = scope;
                    _fingerprints.Entries.Clear();
                    _fingerprintsDirty = true;
                }
            }
            return _fingerprints.Entries;
        }

        private bool HasFingerprint(string key, string value)
        {
            string stored;
            return key != null && Fingerprints().TryGetValue(key, out stored) && string.Equals(stored, value, StringComparison.Ordinal);
        }

        private void RememberFingerprint(string key, string value)
        {
            if (key == null || value == null) return;
            Fingerprints()[key] = value;
            _fingerprintsDirty = true;
        }

        private void ForgetFingerprint(string key)
        {
            if (key != null && Fingerprints().Remove(key)) _fingerprintsDirty = true;
        }

        private void SaveFingerprintsIfDirty()
        {
            if (!_fingerprintsDirty || _fingerprints == null) return;
            if (TrySave(FingerprintFileName, _fingerprints, "content fingerprints")) _fingerprintsDirty = false;
        }

        /// <summary>
        /// Rust renames private members between builds, so a few reads go through reflection.
        /// Lookups are cached per runtime type; a cached null means this build has no such member.
        /// </summary>
        private static class Members
        {
            private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private const BindingFlags DeclaredFlags = Flags | BindingFlags.DeclaredOnly;

            private static readonly string[] CassetteAudioNames = { "audioId", "AudioId", "_audioId", "oggId", "OggId" };
            private static readonly string[] CassetteLengthNames = { "MaxCassetteLength", "maxCassetteLength" };
            private static readonly string[] SculptureResetNames = { "ResetToDefault", "ResetSculpture", "ClearSculpture", "LoadDefault", "Reset" };
            private static readonly string[] PaintedImageNames = { "_currentImageCrc", "currentImageCrc", "imageCrc", "_imageCrc" };

            private static readonly Dictionary<Type, MemberInfo> SculptureCrcMembers = new Dictionary<Type, MemberInfo>();
            private static readonly Dictionary<Type, MemberInfo> CassetteAudioMembers = new Dictionary<Type, MemberInfo>();
            private static readonly Dictionary<Type, MemberInfo> CassetteLengthMembers = new Dictionary<Type, MemberInfo>();
            private static readonly Dictionary<Type, MethodInfo> SculptureResetMethods = new Dictionary<Type, MethodInfo>();

            private static bool _pumpkinResolved;
            private static FieldInfo _pumpkinTextureIds;
            private static bool _photoResolved;
            private static FieldInfo _photoOverlayCrc;
            private static bool _storageResolved;
            private static object _oggType;
            private static MethodInfo _typedGet;
            private static MethodInfo _typedRemove;
            private static MethodInfo _crcRemove;
            private static bool _paintedResolved;
            private static MemberInfo _paintedImageCrc;
            private static MethodInfo _paintedClearContent;
            private static object _pngType;
            private static MethodInfo _pngRemoveExact;
            private static MethodInfo _pngRemove;

            public static uint SculptureCrcOf(BaseEntity entity) =>
                ReadUInt(entity, Cached(SculptureCrcMembers, entity.GetType(), FindCrcMember));

            public static uint CassetteAudioIdOf(BaseEntity entity) =>
                ReadUInt(entity, Cached(CassetteAudioMembers, entity.GetType(), t => FindNamed(t, CassetteAudioNames, IsIdType)));

            public static void ClearCassetteAudioId(BaseEntity entity) =>
                WriteZero(entity, Cached(CassetteAudioMembers, entity.GetType(), t => FindNamed(t, CassetteAudioNames, IsIdType)));

            public static float CassetteLengthOf(BaseEntity entity)
            {
                var member = Cached(CassetteLengthMembers, entity.GetType(),
                    t => FindNamed(t, CassetteLengthNames, ft => ft == typeof(float)));
                object value = Read(entity, member);
                return value is float f ? f : 0f;
            }

            public static uint[] PumpkinTextureIds(CarvablePumpkin pumpkin)
            {
                if (!_pumpkinResolved)
                {
                    _pumpkinResolved = true;
                    var field = typeof(CarvablePumpkin).GetField("textureIDs", Flags);
                    _pumpkinTextureIds = field != null && field.FieldType == typeof(uint[]) ? field : null;
                }
                return Read(pumpkin, _pumpkinTextureIds) as uint[];
            }

            public static void ClearPhotoOverlay(PhotoFrame frame)
            {
                if (!_photoResolved)
                {
                    _photoResolved = true;
                    var field = typeof(PhotoFrame).GetField("_overlayTextureCrc", Flags);
                    _photoOverlayCrc = field != null && IsIdType(field.FieldType) ? field : null;
                }
                WriteZero(frame, _photoOverlayCrc);
            }

            public static bool ResetSculpture(BaseSculpture sculpture)
            {
                var type = sculpture.GetType();
                MethodInfo method;
                if (!SculptureResetMethods.TryGetValue(type, out method))
                {
                    method = null;
                    foreach (var name in SculptureResetNames)
                    {
                        method = type.GetMethod(name, Flags, null, Type.EmptyTypes, null);
                        if (method != null) break;
                    }
                    SculptureResetMethods[type] = method;
                }
                if (method == null) return false;

                try
                {
                    method.Invoke(sculpture, null);
                    return true;
                }
                catch (Exception ex)
                {
                    WarnThrottled("sculpture", $"{type.Name}.{method.Name} failed: {ex.GetBaseException().Message}");
                    return false;
                }
            }

            /// <summary>
            /// Wipe the image of a painted inventory item and drop its png from FileStorage.
            /// Prefers the game's own ClearContent (the F7 UGC browser path); otherwise zeroes the CRC.
            /// Returns false when this build exposes neither, so the task is not reported as done.
            /// </summary>
            public static bool ClearPaintedImage(PaintedItemStorageEntity painted)
            {
                ResolvePainted();
                if (_paintedClearContent == null && _paintedImageCrc == null) return false;

                uint crc = ReadUInt(painted, _paintedImageCrc);
                if (_paintedClearContent != null)
                {
                    try
                    {
                        _paintedClearContent.Invoke(painted, null);
                    }
                    catch (Exception ex)
                    {
                        WarnThrottled("painted", $"ClearContent failed: {ex.GetBaseException().Message}");
                        if (_paintedImageCrc == null) return false;
                        WriteZero(painted, _paintedImageCrc);
                    }
                }
                else
                {
                    WriteZero(painted, _paintedImageCrc);
                }

                if (crc != 0 && painted.net != null) RemovePngBlob(crc, painted.net.ID);
                return true;
            }

            private static void ResolvePainted()
            {
                if (_paintedResolved) return;
                _paintedResolved = true;

                var type = typeof(PaintedItemStorageEntity);
                _paintedImageCrc = FindNamed(type, PaintedImageNames, IsIdType) ?? FindCrcMember(type);
                _paintedClearContent = type.GetMethod("ClearContent", Flags, null, Type.EmptyTypes, null);

                var enumType = typeof(FileStorage).GetNestedType("Type");
                if (enumType == null || !enumType.IsEnum) return;
                foreach (var name in Enum.GetNames(enumType))
                {
                    if (!string.Equals(name, "png", StringComparison.OrdinalIgnoreCase)) continue;
                    _pngType = Enum.Parse(enumType, name);
                    break;
                }
                if (_pngType == null) return;

                _pngRemoveExact = typeof(FileStorage).GetMethod("RemoveExact",
                    new[] { typeof(uint), enumType, typeof(NetworkableId), typeof(uint) });
                _pngRemove = typeof(FileStorage).GetMethod("Remove",
                    new[] { typeof(uint), enumType, typeof(NetworkableId) });
            }

            private static void RemovePngBlob(uint crc, NetworkableId netId)
            {
                try
                {
                    if (_pngRemoveExact != null)
                        _pngRemoveExact.Invoke(FileStorage.server, new object[] { crc, _pngType, netId, 0u });
                    else if (_pngRemove != null)
                        _pngRemove.Invoke(FileStorage.server, new object[] { crc, _pngType, netId });
                }
                catch (Exception ex)
                {
                    WarnThrottled("painted", $"FileStorage remove failed for {netId.Value}: {ex.GetBaseException().Message}");
                }
            }

            /// <summary>Older builds only expose the typed Get(uint crc, FileStorage.Type, NetworkableId).</summary>
            public static byte[] TypedAudioGet(uint crc, NetworkableId netId)
            {
                ResolveStorage();
                if (_typedGet == null) return null;
                try
                {
                    return _typedGet.Invoke(FileStorage.server, new object[] { crc, _oggType, netId }) as byte[];
                }
                catch (Exception ex)
                {
                    WarnThrottled("audio", $"FileStorage typed read failed for {netId.Value}: {ex.GetBaseException().Message}");
                    return null;
                }
            }

            public static void RemoveAudioBlob(uint crc, NetworkableId netId)
            {
                ResolveStorage();
                try
                {
                    if (_typedRemove != null)
                        _typedRemove.Invoke(FileStorage.server, new object[] { crc, _oggType, netId });
                    else if (_crcRemove != null)
                        _crcRemove.Invoke(FileStorage.server, new object[] { crc });
                }
                catch (Exception ex)
                {
                    WarnThrottled("audio", $"FileStorage remove failed for {netId.Value}: {ex.GetBaseException().Message}");
                }
            }

            private static void ResolveStorage()
            {
                if (_storageResolved) return;
                _storageResolved = true;

                var enumType = typeof(FileStorage).GetNestedType("Type");
                if (enumType != null && enumType.IsEnum)
                {
                    foreach (var name in Enum.GetNames(enumType))
                    {
                        if (string.Equals(name, "ogg", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "audio", StringComparison.OrdinalIgnoreCase))
                        {
                            _oggType = Enum.Parse(enumType, name);
                            break;
                        }
                    }
                }

                if (_oggType != null)
                {
                    var signature = new[] { typeof(uint), _oggType.GetType(), typeof(NetworkableId) };
                    _typedGet = typeof(FileStorage).GetMethod("Get", signature);
                    _typedRemove = typeof(FileStorage).GetMethod("Remove", signature);
                }
                _crcRemove = typeof(FileStorage).GetMethod("Remove", new[] { typeof(uint) });
            }

            private static MemberInfo Cached(Dictionary<Type, MemberInfo> cache, Type type, Func<Type, MemberInfo> find)
            {
                MemberInfo member;
                if (!cache.TryGetValue(type, out member))
                {
                    member = find(type);
                    cache[type] = member;
                }
                return member;
            }

            private static bool IsIdType(Type type) => type == typeof(uint) || type == typeof(ulong);

            private static bool IsReadable(PropertyInfo property) =>
                property.CanRead && property.GetIndexParameters().Length == 0;

            private static MemberInfo FindCrcMember(Type type)
            {
                for (var t = type; t != null && t != typeof(BaseEntity) && t != typeof(object); t = t.BaseType)
                {
                    foreach (var property in t.GetProperties(DeclaredFlags))
                    {
                        if (IsIdType(property.PropertyType) && IsReadable(property)
                            && property.Name.IndexOf("crc", StringComparison.OrdinalIgnoreCase) >= 0)
                            return property;
                    }
                    foreach (var field in t.GetFields(DeclaredFlags))
                    {
                        if (IsIdType(field.FieldType)
                            && field.Name.IndexOf("crc", StringComparison.OrdinalIgnoreCase) >= 0)
                            return field;
                    }
                }
                return null;
            }

            private static MemberInfo FindNamed(Type type, string[] names, Func<Type, bool> valueType)
            {
                for (var t = type; t != null && t != typeof(BaseEntity) && t != typeof(object); t = t.BaseType)
                {
                    foreach (var name in names)
                    {
                        var field = t.GetField(name, DeclaredFlags);
                        if (field != null && valueType(field.FieldType)) return field;
                        var property = t.GetProperty(name, DeclaredFlags);
                        if (property != null && valueType(property.PropertyType) && IsReadable(property)) return property;
                    }
                }
                return null;
            }

            private static object Read(object target, MemberInfo member)
            {
                if (member == null || target == null) return null;
                try
                {
                    var field = member as FieldInfo;
                    return field != null ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target, null);
                }
                catch (Exception ex)
                {
                    WarnThrottled("reflection", $"{member.DeclaringType?.Name}.{member.Name}: {ex.GetBaseException().Message}");
                    return null;
                }
            }

            private static uint ReadUInt(object target, MemberInfo member)
            {
                object value = Read(target, member);
                if (value is uint u) return u;
                if (value is ulong ul) return (uint)ul;
                return 0;
            }

            private static void WriteZero(object target, MemberInfo member)
            {
                if (member == null || target == null) return;
                var field = member as FieldInfo;
                var property = member as PropertyInfo;
                Type type = field != null ? field.FieldType : property.PropertyType;
                object zero = type == typeof(ulong) ? (object)0UL : 0u;
                try
                {
                    if (field != null)
                    {
                        if (!field.IsInitOnly) field.SetValue(target, zero);
                    }
                    else if (property.CanWrite)
                    {
                        property.SetValue(target, zero, null);
                    }
                }
                catch (Exception ex)
                {
                    WarnThrottled("reflection", $"{member.DeclaringType?.Name}.{member.Name}: {ex.GetBaseException().Message}");
                }
            }
        }

        private static string NetIdOf(BaseNetworkable entity) =>
            entity.net.ID.Value.ToString(CultureInfo.InvariantCulture);

        private static string OwnerIdOf(BaseEntity entity) =>
            entity.OwnerID != 0UL ? entity.OwnerID.ToString(CultureInfo.InvariantCulture) : "";

        private static Vector3 PositionOf(BaseEntity entity) =>
            entity.transform != null ? entity.transform.position : Vector3.zero;

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        internal static string WipeIdentityKey()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}",
                ConVar.Server.level ?? "",
                ConVar.Server.seed,
                ConVar.Server.worldsize,
                SaveCreatedTimeKey());
        }

        /// <summary>
        /// Facepunch / panel map convention: north (+Z) = low number, 0-based (A0 at NW).
        /// Must match the grid overlay in web map-camera.ts.
        /// </summary>
        private static string GridLabel(Vector3 position)
        {
            float worldSize = Mathf.Max(1f, ConVar.Server.worldsize);
            int count = Math.Max(1, (int)Math.Floor(worldSize * 7.0 / 1024.0));
            float cell = worldSize / count;
            float half = worldSize * 0.5f;
            int col = Mathf.Clamp((int)Math.Floor((position.x + half) / cell), 0, count - 1);
            int rowFromSouth = Mathf.Clamp((int)Math.Floor((position.z + half) / cell), 0, count - 1);
            int rowFromNorth = count - 1 - rowFromSouth;
            return GridLetter(col + 1) + rowFromNorth.ToString(CultureInfo.InvariantCulture);
        }

        private static string GridLetter(int n)
        {
            if (n < 1) return "?";
            string letters = "";
            float tens = n / 26f;
            int ones = n % 26;
            if (ones == 0) ones = 26;
            if (tens > 1) letters += (char)(64 + (int)Math.Floor(tens));
            letters += (char)(64 + ones);
            return letters;
        }

        private static string PaintableKind(BaseEntity entity)
        {
            if (entity is PhotoFrame) return "photo_frame";
            if (entity is CarvablePumpkin || entity is BaseSculpture) return "pumpkin";
            if (entity is PatternFirework) return "firework";
            if (entity is PaintedItemStorageEntity) return "painted_item";

            string name = entity?.ShortPrefabName ?? entity?.PrefabName ?? "";
            if (ContainsText(name, "photo") || ContainsText(name, "pictureframe") || ContainsText(name, "picture_frame"))
                return "photo_frame";
            if (ContainsText(name, "pumpkin") || ContainsText(name, "sculpture") || ContainsText(name, "carvable"))
                return "pumpkin";
            if (ContainsText(name, "neon")) return "neon";
            return "sign";
        }

        private static bool ContainsText(string value, string part) =>
            value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private class DrawingsWorker : RCoreWorker
        {
            private const int MaxQueuedJobs = 256;
            private const long MaxQueuedBytes = 32L * 1024 * 1024;
            private const int MaxGoneBatch = 500;
            private const int MaxUploadAttempts = 3;
            private const float UploadSpacing = 0.75f;
            private const float RetryPause = 15f;
            private const float SculptureRescanInterval = 20f;
            private const float ReconcileInterval = 300f;

            private readonly Queue<DrawingUploadJob> _queue = new Queue<DrawingUploadJob>();
            private readonly Dictionary<string, string> _queuedFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<string> _destroyBuffer = new List<string>(32);
            private readonly HashSet<string> _destroyPending = new HashSet<string>();
            private readonly HashSet<string> _pendingCapture = new HashSet<string>();
            private long _queuedBytes;
            private bool _uploadInFlight;
            private float _nextUploadAt;
            private bool _goneInFlight;
            private bool _reconcileInFlight;
            private bool _syncQueued;
            private bool _syncRunning;
            private bool _rescanRunning;
            private float _syncAt = -1f;
            private float _nextSculptureRescanAt = -1f;
            private float _nextReconcileAt = -1f;

            protected override float Interval => 2f;

            /// <summary>Soft cap: full-sync passes wait here so uploads drain first.</summary>
            private bool QueueBusy => _queue.Count >= MaxQueuedJobs || _queuedBytes >= MaxQueuedBytes;

            public void RequestFullSync(float delay)
            {
                _syncQueued = true;
                _syncAt = UnityEngine.Time.realtimeSinceStartup + Mathf.Max(0.5f, delay);
            }

            /// <summary>
            /// FileStorage often gets the new CRC a beat after the paint hook, so read late and
            /// retry once when the first read is empty.
            /// </summary>
            public void ScheduleCapture(BaseEntity entity, string editorSteamId, int textureIndex, string kind)
            {
                if (entity == null || entity.IsDestroyed || entity.net == null || Instance == null) return;
                string key = NetIdOf(entity) + ":" + textureIndex.ToString(CultureInfo.InvariantCulture);
                if (!_pendingCapture.Add(key)) return;

                Instance.timer.Once(0.25f, () => CaptureAttempt(entity, editorSteamId, textureIndex, kind, key, true));
            }

            private void CaptureAttempt(BaseEntity entity, string editorSteamId, int textureIndex, string kind, string key, bool retry)
            {
                bool done = true;
                try
                {
                    if (this == null || entity == null || entity.IsDestroyed || entity.net == null) return;
                    if (EnqueueEntity(entity, editorSteamId, textureIndex, kind) || !retry || Instance == null) return;

                    done = false;
                    Instance.timer.Once(0.35f, () => CaptureAttempt(entity, editorSteamId, textureIndex, kind, key, false));
                }
                finally
                {
                    if (done) _pendingCapture.Remove(key);
                }
            }

            public bool EnqueueEntity(BaseEntity entity, string editorSteamId, int textureIndex, string kind)
            {
                if (entity == null || entity.IsDestroyed || entity.net == null) return false;
                if (entity is BaseSculpture) return EnqueueSculpture(entity, editorSteamId, force: true);

                uint[] crcs = FaceCrcs(entity);
                if (crcs == null || crcs.Length == 0) return false;
                int face = Mathf.Clamp(textureIndex, 0, crcs.Length - 1);
                return EnqueueFace(entity, editorSteamId, kind, face, crcs[face], force: true);
            }

            public void EnqueueBytes(BaseEntity entity, string editorSteamId, string kind, byte[] bytes, int textureIndex)
            {
                if (entity == null || entity.IsDestroyed || entity.net == null) return;
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaxDrawingBytes) return;
                string netId = NetIdOf(entity);
                int face = Math.Max(0, textureIndex);
                Enqueue(BuildJob(entity, netId, editorSteamId, kind, bytes, face, FaceKey(netId, face), null));
            }

            public bool EnqueueFirework(PatternFirework firework, ProtoBuf.PatternFirework.Design design, string editorSteamId, bool force)
            {
                if (firework == null || firework.IsDestroyed || firework.net == null) return false;
                string fingerprint = DesignFingerprint(design);
                if (fingerprint == null) return false;

                string netId = NetIdOf(firework);
                string key = FaceKey(netId, 0);
                if (!force && IsKnown(key, fingerprint)) return false;

                byte[] png = RenderFireworkDesignToPng(design);
                if (png == null) return false;
                return Enqueue(BuildJob(firework, netId, editorSteamId, "firework", png, 0, key, fingerprint));
            }

            /// <summary>
            /// BaseSculpture stores lz4 SDF in FileStorage, not a PNG. The CRC is the fingerprint
            /// and a generated preview is uploaded so the panel can list and track edits.
            /// </summary>
            public bool EnqueueSculpture(BaseEntity entity, string editorSteamId, bool force)
            {
                if (entity == null || entity.IsDestroyed || entity.net == null || !(entity is BaseSculpture)) return false;

                uint crc = SculptureCrc(entity);
                string netId = NetIdOf(entity);
                string key = FaceKey(netId, 0);
                string fingerprint = crc.ToString(CultureInfo.InvariantCulture);
                if (!force && IsKnown(key, fingerprint)) return false;

                byte[] png = CaptureSculpturePng(entity, crc);
                if (png == null) return false;
                return Enqueue(BuildJob(entity, netId, editorSteamId, "pumpkin", png, 0, key, fingerprint));
            }

            public void EnqueueDestroy(string netId)
            {
                if (string.IsNullOrEmpty(netId)) return;
                if (!_destroyPending.Add(netId)) return;
                _destroyBuffer.Add(netId);
            }

            protected override void Tick()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;
                float now = UnityEngine.Time.realtimeSinceStartup;

                if (_syncQueued && now >= _syncAt && !_syncRunning)
                {
                    _syncQueued = false;
                    StartCoroutine(RunFullSync());
                }

                if (_nextSculptureRescanAt < 0f) _nextSculptureRescanAt = now + SculptureRescanInterval;
                if (now >= _nextSculptureRescanAt && !_syncRunning && !_rescanRunning)
                {
                    _nextSculptureRescanAt = now + SculptureRescanInterval;
                    StartCoroutine(RescanSculptures());
                }

                if (_nextReconcileAt < 0f) _nextReconcileAt = now + ReconcileInterval;
                if (now >= _nextReconcileAt && !_syncRunning)
                {
                    _nextReconcileAt = now + ReconcileInterval;
                    Reconcile();
                }

                if (_destroyBuffer.Count > 0 && !_goneInFlight)
                    FlushDestroy();

                PumpUploads();
            }

            private IEnumerator RunFullSync()
            {
                _syncRunning = true;
                try
                {
                    var wait = new WaitForSeconds(1f);
                    var index = Instance?._content;
                    while (index != null && !index.Ready) yield return wait;
                    if (index == null) yield break;

                    var entities = index.Live(index.Paintables);
                    float sliceStart = UnityEngine.Time.realtimeSinceStartup;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        while (QueueBusy && Instance != null && Instance.IsLinked) yield return wait;
                        if (Instance == null || !Instance.IsLinked) yield break;

                        var entity = entities[i];
                        if (entity != null && !entity.IsDestroyed && entity.net != null)
                            SyncEntity(entity);

                        if (UnityEngine.Time.realtimeSinceStartup - sliceStart >= FrameBudgetSeconds)
                        {
                            yield return null;
                            sliceStart = UnityEngine.Time.realtimeSinceStartup;
                        }
                    }

                    _nextReconcileAt = 0f;
                }
                finally
                {
                    _syncRunning = false;
                }
            }

            private void SyncEntity(BaseEntity entity)
            {
                // Painted item bytes only arrive through OnItemPainted.
                if (entity is PaintedItemStorageEntity) return;

                if (entity is BaseSculpture)
                {
                    EnqueueSculpture(entity, null, force: false);
                    return;
                }

                var firework = entity as PatternFirework;
                if (firework != null)
                {
                    EnqueueFirework(firework, firework.Design, null, force: false);
                    return;
                }

                uint[] crcs = FaceCrcs(entity);
                if (crcs == null) return;
                string kind = PaintableKind(entity);
                for (int face = 0; face < crcs.Length; face++)
                    EnqueueFace(entity, null, kind, face, crcs[face], force: false);
            }

            /// <summary>Sculpt edits have no hook, so CRCs are polled.</summary>
            private IEnumerator RescanSculptures()
            {
                _rescanRunning = true;
                try
                {
                    var index = Instance?._content;
                    if (index == null || !index.Ready) yield break;

                    var sculptures = index.Live(index.Sculptures);
                    float sliceStart = UnityEngine.Time.realtimeSinceStartup;
                    for (int i = 0; i < sculptures.Count; i++)
                    {
                        if (QueueBusy || Instance == null) yield break;
                        var entity = sculptures[i];
                        if (entity != null && !entity.IsDestroyed && entity.net != null)
                            EnqueueSculpture(entity, null, force: false);

                        if (UnityEngine.Time.realtimeSinceStartup - sliceStart >= FrameBudgetSeconds)
                        {
                            yield return null;
                            sliceStart = UnityEngine.Time.realtimeSinceStartup;
                        }
                    }
                }
                finally
                {
                    _rescanRunning = false;
                }
            }

            /// <summary>Marks paintables destroyed without a kill hook as gone.</summary>
            private void Reconcile()
            {
                var index = Instance?._content;
                if (_reconcileInFlight || index == null || !index.Ready) return;

                var live = ContentIndex.NetIds(index.Live(index.Paintables));
                if (live.Count > MaxReconcileNetIds)
                {
                    WarnThrottled("drawings", $"{live.Count} paintables exceed the reconcile cap of {MaxReconcileNetIds}; skipping reconcile");
                    return;
                }

                _reconcileInFlight = true;
                Send<JObject>(
                    DrawingsReconcilePath,
                    new Dictionary<string, object>
                    {
                        ["wipeIdentityKey"] = WipeIdentityKey(),
                        ["liveNetIds"] = live
                    },
                    _ => { _reconcileInFlight = false; },
                    (code, body) => { _reconcileInFlight = false; });
            }

            private void FlushDestroy()
            {
                int take = Math.Min(MaxGoneBatch, _destroyBuffer.Count);
                var batch = _destroyBuffer.GetRange(0, take);
                _destroyBuffer.RemoveRange(0, take);
                for (int i = 0; i < batch.Count; i++) _destroyPending.Remove(batch[i]);

                _goneInFlight = true;
                Send<JObject>(
                    DrawingsGonePath,
                    new Dictionary<string, object> { ["netIds"] = batch },
                    _ => { _goneInFlight = false; },
                    (code, body) => { _goneInFlight = false; });
            }

            private bool EnqueueFace(BaseEntity entity, string editorSteamId, string kind, int face, uint crc, bool force)
            {
                if (crc == 0) return false;
                string netId = NetIdOf(entity);
                string key = FaceKey(netId, face);
                string fingerprint = crc.ToString(CultureInfo.InvariantCulture);
                if (!force && IsKnown(key, fingerprint)) return false;

                byte[] bytes = ReadFace(entity.net.ID, face, crc);
                if (bytes == null) return false;
                return Enqueue(BuildJob(entity, netId, editorSteamId, kind, bytes, face, key, fingerprint));
            }

            private bool IsKnown(string key, string fingerprint)
            {
                string queued;
                if (_queuedFingerprints.TryGetValue(key, out queued) && queued == fingerprint) return true;
                return Instance != null && Instance.HasFingerprint(key, fingerprint);
            }

            private bool Enqueue(DrawingUploadJob job)
            {
                if (job == null) return false;

                string queued;
                if (_queuedFingerprints.TryGetValue(job.FingerprintKey, out queued) && queued == job.Fingerprint)
                    return true;

                // Hook-driven captures may go past the soft cap; this is the hard ceiling.
                if (_queue.Count >= MaxQueuedJobs * 2 || _queuedBytes + job.Bytes.Length > MaxQueuedBytes * 2)
                {
                    WarnThrottled("drawings", "upload queue is full; dropping a capture");
                    return false;
                }

                _queue.Enqueue(job);
                _queuedBytes += job.Bytes.Length;
                _queuedFingerprints[job.FingerprintKey] = job.Fingerprint;
                return true;
            }

            private void ForgetQueued(DrawingUploadJob job)
            {
                string queued;
                if (_queuedFingerprints.TryGetValue(job.FingerprintKey, out queued) && queued == job.Fingerprint)
                    _queuedFingerprints.Remove(job.FingerprintKey);
            }

            private void PumpUploads()
            {
                if (_uploadInFlight || _queue.Count == 0 || Instance == null || !Instance.IsLinked) return;

                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now < _nextUploadAt)
                {
                    if (!IsInvoking(nameof(PumpUploads))) Invoke(nameof(PumpUploads), _nextUploadAt - now);
                    return;
                }

                var pump = Engine?.Http;
                if (pump == null) return;

                var job = _queue.Dequeue();
                _queuedBytes -= job.Bytes.Length;
                _uploadInFlight = true;

                pump.Enqueue(
                    Instance.BuildUrl(DrawingsPath),
                    UnityWebRequest.kHttpVerbPOST,
                    BuildHeaders(job),
                    null,
                    (code, body) => OnUploadDone(job, code),
                    HttpLane.Bulk,
                    job.Bytes,
                    30);
            }

            private void OnUploadDone(DrawingUploadJob job, int code)
            {
                _uploadInFlight = false;
                if (this == null) return;

                float now = UnityEngine.Time.realtimeSinceStartup;
                if (code >= 200 && code < 300)
                {
                    ForgetQueued(job);
                    Instance?.RememberFingerprint(job.FingerprintKey, job.Fingerprint);
                    _nextUploadAt = now + UploadSpacing;
                }
                else if (IsTransientHttpFailure(code) && ++job.Attempts < MaxUploadAttempts)
                {
                    _queue.Enqueue(job);
                    _queuedBytes += job.Bytes.Length;
                    _nextUploadAt = now + RetryPause;
                }
                else
                {
                    ForgetQueued(job);
                    WarnThrottled("drawings", $"upload for entity {job.EntityNetId} failed with HTTP {code}");
                    _nextUploadAt = now + UploadSpacing;
                }

                PumpUploads();
            }

            private static Dictionary<string, string> BuildHeaders(DrawingUploadJob job)
            {
                var headers = Instance.BuildHeaders(true);
                headers["Content-Type"] = "application/octet-stream";
                headers["X-Drawing-Event-Id"] = job.EventId;
                headers["X-Drawing-Entity-Net-Id"] = job.EntityNetId;
                headers["X-Drawing-Kind"] = job.Kind;
                headers["X-Drawing-Texture-Index"] = job.TextureIndex.ToString(CultureInfo.InvariantCulture);
                headers["X-Drawing-Entity-Prefab"] = job.EntityPrefab ?? "";
                if (!string.IsNullOrEmpty(job.EditorSteamId))
                    headers["X-Drawing-Editor-Steam-Id"] = job.EditorSteamId;
                if (!string.IsNullOrEmpty(job.OwnerSteamId))
                    headers["X-Drawing-Owner-Steam-Id"] = job.OwnerSteamId;
                headers["X-Drawing-Pos-X"] = job.PosX.ToString("G", CultureInfo.InvariantCulture);
                headers["X-Drawing-Pos-Y"] = job.PosY.ToString("G", CultureInfo.InvariantCulture);
                headers["X-Drawing-Pos-Z"] = job.PosZ.ToString("G", CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(job.Grid))
                    headers["X-Drawing-Grid"] = job.Grid;
                headers["X-Drawing-Content-Hash"] = job.ContentHash;
                headers["X-Drawing-Wipe-Identity-Key"] = job.WipeIdentityKey;
                headers["X-Drawing-Content-Type"] = job.ContentType ?? "image/png";
                headers["X-Drawing-Edited-At"] = job.EditedAt;
                return headers;
            }

            private static DrawingUploadJob BuildJob(
                BaseEntity entity, string netId, string editorSteamId, string kind, byte[] bytes,
                int textureIndex, string fingerprintKey, string fingerprint)
            {
                if (bytes == null || bytes.Length == 0) return null;

                string hash = Sha256Hex(bytes);
                Vector3 pos = PositionOf(entity);
                return new DrawingUploadJob
                {
                    EventId = NewEventId(),
                    EntityNetId = netId,
                    Kind = kind ?? "other",
                    TextureIndex = Math.Max(0, textureIndex),
                    EntityPrefab = entity.PrefabName ?? "",
                    EditorSteamId = editorSteamId ?? "",
                    OwnerSteamId = OwnerIdOf(entity),
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    Grid = GridLabel(pos),
                    ContentHash = hash,
                    WipeIdentityKey = WipeIdentityKey(),
                    Bytes = bytes,
                    ContentType = "image/png",
                    EditedAt = UtcNow(),
                    FingerprintKey = fingerprintKey,
                    Fingerprint = fingerprint ?? hash
                };
            }

            private static string FaceKey(string netId, int face) =>
                "d:" + netId + ":" + face.ToString(CultureInfo.InvariantCulture);

            private static uint[] FaceCrcs(BaseEntity entity)
            {
                var sign = entity as Signage;
                if (sign != null && sign.textureIDs != null && sign.textureIDs.Length > 0) return sign.textureIDs;

                var pumpkin = entity as CarvablePumpkin;
                if (pumpkin != null)
                {
                    var ids = Members.PumpkinTextureIds(pumpkin);
                    if (ids != null && ids.Length > 0) return ids;
                }

                var signage = entity as ISignage;
                return signage != null ? signage.GetTextureCRCs() : null;
            }

            /// <summary>FileStorage.Get(NetworkableId, ulong slot, uint crc); the slot is the face index.</summary>
            private static byte[] ReadFace(NetworkableId netId, int face, uint crc)
            {
                byte[] data;
                try
                {
                    data = FileStorage.server.Get(netId, (ulong)face, crc);
                }
                catch (Exception ex)
                {
                    WarnThrottled("drawings", $"FileStorage read failed for {netId.Value}: {ex.Message}");
                    return null;
                }
                return data != null && data.Length > 0 && data.Length <= MaxDrawingBytes ? data : null;
            }

            /// <summary>Falls back to net id ^ owner so a sculpture without a readable CRC still lists once.</summary>
            private static uint SculptureCrc(BaseEntity entity)
            {
                uint crc = Members.SculptureCrcOf(entity);
                if (crc != 0) return crc;
                ulong seed = entity.net.ID.Value ^ entity.OwnerID;
                return (uint)(seed ^ (seed >> 32));
            }

            private static byte[] CaptureSculpturePng(BaseEntity entity, uint crc)
            {
                byte[] raw = null;
                try
                {
                    raw = FileStorage.server.Get(entity.net.ID, 0UL, crc);
                }
                catch (Exception ex)
                {
                    WarnThrottled("drawings", $"FileStorage read failed for sculpture {entity.net.ID.Value}: {ex.Message}");
                }
                return IsPngBytes(raw) ? raw : RenderSculpturePlaceholderPng(crc);
            }

            private static bool IsPngBytes(byte[] data)
            {
                return data != null
                    && data.Length > 8
                    && data[0] == 0x89
                    && data[1] == 0x50
                    && data[2] == 0x4E
                    && data[3] == 0x47;
            }

            /// <summary>
            /// Ice/rock sculptures are 3D SDF with no paint texture. The CRC is encoded into a
            /// small PNG so each edit gets a new content hash. The panel shows the item icon.
            /// </summary>
            private static byte[] RenderSculpturePlaceholderPng(uint crc)
            {
                const int size = 256;
                var pixels = new Color32[size * size];
                byte r0 = (byte)(140 + ((crc >> 0) & 31));
                byte g0 = (byte)(190 + ((crc >> 5) & 31));
                byte b0 = (byte)(220 + ((crc >> 10) & 31));
                uint mix = crc == 0 ? 0xA5A5A5A5u : crc;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        uint h = mix ^ (uint)(x * 374761393 + y * 668265263);
                        h = (h ^ (h >> 13)) * 1274126177u;
                        byte n = (byte)(h & 24);
                        int dx = x - size / 2;
                        int dy = y - size / 2;
                        byte a = dx * dx + dy * dy < 90 * 90 ? (byte)255 : (byte)0;
                        pixels[y * size + x] = new Color32(
                            (byte)Mathf.Clamp(r0 + n, 0, 255),
                            (byte)Mathf.Clamp(g0 + n / 2, 0, 255),
                            (byte)Mathf.Clamp(b0 + n, 0, 255),
                            a);
                    }
                }
                return EncodePng(size, pixels);
            }

            private static string DesignFingerprint(ProtoBuf.PatternFirework.Design design)
            {
                var stars = design?.stars;
                if (stars == null || stars.Count == 0) return null;

                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < stars.Count; i++)
                {
                    var star = stars[i];
                    hash = Mix(hash, star.position.x);
                    hash = Mix(hash, star.position.y);
                    hash = Mix(hash, star.color.r);
                    hash = Mix(hash, star.color.g);
                    hash = Mix(hash, star.color.b);
                    hash = Mix(hash, star.color.a);
                }
                return "fw" + hash.ToString("x16", CultureInfo.InvariantCulture);
            }

            private static ulong Mix(ulong hash, float value)
            {
                unchecked
                {
                    hash ^= (uint)Mathf.RoundToInt(value * 10000f);
                    return hash * 1099511628211UL;
                }
            }

            private static byte[] RenderFireworkDesignToPng(ProtoBuf.PatternFirework.Design design)
            {
                var stars = design?.stars;
                if (stars == null || stars.Count == 0) return null;

                const int size = 250;
                const int radius = 9;
                var pixels = new Color32[size * size];
                for (int index = 0; index < stars.Count; index++)
                {
                    var star = stars[index];
                    int cx = (int)((star.position.x + 1f) * 125f);
                    int cy = (int)((-star.position.y + 1f) * 125f);
                    var col = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(star.color.r * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(star.color.g * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(star.color.b * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(star.color.a * 255f), 0, 255));
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            if (dx * dx + dy * dy > radius * radius) continue;
                            int x = cx + dx;
                            int y = cy + dy;
                            if (x < 0 || x >= size || y < 0 || y >= size) continue;
                            pixels[y * size + x] = col;
                        }
                    }
                }
                return EncodePng(size, pixels);
            }

            private static byte[] EncodePng(int size, Color32[] pixels)
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                try
                {
                    tex.SetPixels32(pixels);
                    tex.Apply(false, false);
                    return tex.EncodeToPNG();
                }
                catch (Exception ex)
                {
                    WarnThrottled("drawings", $"PNG encode failed: {ex.Message}");
                    return null;
                }
                finally
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }
        }

        private class DrawingUploadJob
        {
            public string EventId;
            public string EntityNetId;
            public string Kind;
            public int TextureIndex;
            public string EntityPrefab;
            public string EditorSteamId;
            public string OwnerSteamId;
            public float PosX;
            public float PosY;
            public float PosZ;
            public string Grid;
            public string ContentHash;
            public string WipeIdentityKey;
            public byte[] Bytes;
            public string ContentType;
            public string EditedAt;
            public string FingerprintKey;
            public string Fingerprint;
            public int Attempts;
        }

        /// <summary>
        /// Text PGC: vending shop names + sleeping bag / bed nicknames.
        /// </summary>
        private class NamedContentWorker : RCoreWorker
        {
            private const int MaxPending = 5000;
            private const int UploadBatch = 200;
            private const int MaxGoneBatch = 500;
            private const float RescanInterval = 30f;
            private const float ReconcileInterval = 300f;
            private const float ChainDelay = 1.5f;

            private readonly Dictionary<string, NamedContentItemDto> _pending = new Dictionary<string, NamedContentItemDto>(StringComparer.Ordinal);
            /// <summary>netId → last title sent or queued, compared without allocating.</summary>
            private readonly Dictionary<ulong, string> _titles = new Dictionary<ulong, string>();
            private readonly List<string> _destroyBuffer = new List<string>(32);
            private readonly HashSet<string> _destroyPending = new HashSet<string>();
            private bool _uploadInFlight;
            private bool _goneInFlight;
            private bool _syncQueued;
            private bool _syncRunning;
            private bool _synced;
            private float _syncAt = -1f;
            private float _nextRescanAt = -1f;
            private float _nextReconcileAt = -1f;

            protected override float Interval => 5f;

            public void RequestFullSync(float delay)
            {
                _syncQueued = true;
                _syncAt = UnityEngine.Time.realtimeSinceStartup + Mathf.Max(0.5f, delay);
            }

            public void EnqueueDestroy(string netId)
            {
                if (string.IsNullOrEmpty(netId)) return;
                if (!_destroyPending.Add(netId)) return;
                _destroyBuffer.Add(netId);
                _pending.Remove(netId);

                ulong raw;
                if (ulong.TryParse(netId, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                    _titles.Remove(raw);
                Instance?.ForgetFingerprint(NamedKey(netId));
            }

            /// <summary>Queues the entity when its title differs from the last one sent or queued.</summary>
            public void ObserveEntity(BaseEntity entity)
            {
                string kind;
                string title;
                if (!TryReadTitle(entity, out kind, out title)) return;

                ulong id = entity.net.ID.Value;
                string previous;
                if (_titles.TryGetValue(id, out previous) && string.Equals(previous, title, StringComparison.Ordinal))
                    return;

                if (QueueItem(entity, NetIdOf(entity), kind, title)) _titles[id] = title;
            }

            protected override void Tick()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;
                float now = UnityEngine.Time.realtimeSinceStartup;

                if (_syncQueued && now >= _syncAt && !_syncRunning)
                {
                    _syncQueued = false;
                    StartCoroutine(RunFullSync());
                }

                if (_nextRescanAt < 0f) _nextRescanAt = now + RescanInterval;
                if (_synced && !_syncRunning && now >= _nextRescanAt)
                {
                    _nextRescanAt = now + RescanInterval;
                    Rescan();
                }

                if (_destroyBuffer.Count > 0 && !_goneInFlight)
                {
                    FlushDestroy();
                    return;
                }

                bool reconcile = false;
                if (_nextReconcileAt < 0f) _nextReconcileAt = now + ReconcileInterval;
                if (_synced && !_syncRunning && now >= _nextReconcileAt)
                {
                    _nextReconcileAt = now + ReconcileInterval;
                    reconcile = true;
                }

                Flush(reconcile);
            }

            private IEnumerator RunFullSync()
            {
                _syncRunning = true;
                try
                {
                    var wait = new WaitForSeconds(1f);
                    var index = Instance?._content;
                    while (index != null && !index.Ready) yield return wait;
                    if (index == null) yield break;

                    var entities = index.Live(index.Named);
                    float sliceStart = UnityEngine.Time.realtimeSinceStartup;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        if (Instance == null || !Instance.IsLinked) yield break;
                        SyncEntity(entities[i]);

                        if (UnityEngine.Time.realtimeSinceStartup - sliceStart >= FrameBudgetSeconds)
                        {
                            yield return null;
                            sliceStart = UnityEngine.Time.realtimeSinceStartup;
                        }
                    }

                    _synced = true;
                    _nextReconcileAt = 0f;
                }
                finally
                {
                    _syncRunning = false;
                }
            }

            private void SyncEntity(BaseEntity entity)
            {
                string kind;
                string title;
                if (!TryReadTitle(entity, out kind, out title)) return;

                ulong id = entity.net.ID.Value;
                string previous;
                if (_titles.TryGetValue(id, out previous) && string.Equals(previous, title, StringComparison.Ordinal))
                    return;

                string netId = NetIdOf(entity);
                if (Instance.HasFingerprint(NamedKey(netId), kind + "|" + title))
                {
                    _titles[id] = title;
                    return;
                }

                if (QueueItem(entity, netId, kind, title)) _titles[id] = title;
            }

            /// <summary>Picks up renames after placement; there is no rename hook for bags and shops.</summary>
            private void Rescan()
            {
                var index = Instance?._content;
                if (index == null || !index.Ready) return;
                var entities = index.Live(index.Named);
                for (int i = 0; i < entities.Count; i++) ObserveEntity(entities[i]);
            }

            private bool QueueItem(BaseEntity entity, string netId, string kind, string title)
            {
                if (!_pending.ContainsKey(netId) && _pending.Count >= MaxPending)
                {
                    WarnThrottled("named-content", "pending queue is full; dropping a title update");
                    return false;
                }

                Vector3 pos = PositionOf(entity);
                _pending[netId] = new NamedContentItemDto
                {
                    EntityNetId = netId,
                    Kind = kind,
                    Title = title,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    Grid = GridLabel(pos),
                    OwnerSteamId = OwnerIdOf(entity),
                    WipeIdentityKey = WipeIdentityKey()
                };
                return true;
            }

            private void FlushPending()
            {
                if (Ready && Instance != null && Instance.IsLinked) Flush(false);
            }

            private void Flush(bool reconcile)
            {
                if (_uploadInFlight || Instance == null) return;

                List<string> live = null;
                if (reconcile)
                {
                    var index = Instance._content;
                    if (index != null && index.Ready)
                    {
                        live = ContentIndex.NetIds(index.Live(index.Named));
                        if (live.Count > MaxReconcileNetIds)
                        {
                            WarnThrottled("named-content", $"{live.Count} shops and bags exceed the reconcile cap of {MaxReconcileNetIds}; skipping reconcile");
                            live = null;
                        }
                    }
                    reconcile = live != null;
                }

                if (_pending.Count == 0 && !reconcile) return;

                var batch = TakePending(UploadBatch);
                var payload = new NamedContentBatchDto
                {
                    Items = new List<NamedContentItemWire>(batch.Count),
                    Reconcile = reconcile,
                    LiveNetIds = live,
                    WipeIdentityKey = reconcile ? WipeIdentityKey() : null
                };
                for (int i = 0; i < batch.Count; i++)
                {
                    var src = batch[i];
                    payload.Items.Add(new NamedContentItemWire
                    {
                        EntityNetId = src.EntityNetId,
                        Kind = src.Kind,
                        Title = src.Title,
                        PosX = src.PosX,
                        PosY = src.PosY,
                        PosZ = src.PosZ,
                        Grid = src.Grid,
                        OwnerSteamId = string.IsNullOrEmpty(src.OwnerSteamId) ? null : src.OwnerSteamId,
                        WipeIdentityKey = src.WipeIdentityKey
                    });
                }

                _uploadInFlight = true;
                Send<JObject>(NamedContentPath, payload,
                    _ =>
                    {
                        _uploadInFlight = false;
                        for (int i = 0; i < batch.Count; i++)
                            Instance?.RememberFingerprint(NamedKey(batch[i].EntityNetId), batch[i].Kind + "|" + batch[i].Title);
                        if (_pending.Count > 0 && !IsInvoking(nameof(FlushPending)))
                            Invoke(nameof(FlushPending), ChainDelay);
                    },
                    (code, response) =>
                    {
                        _uploadInFlight = false;
                        if (IsTransientHttpFailure(code))
                        {
                            Requeue(batch);
                            return;
                        }
                        WarnThrottled("named-content", $"batch rejected with HTTP {code}");
                    });
            }

            private List<NamedContentItemDto> TakePending(int max)
            {
                var batch = new List<NamedContentItemDto>(Math.Min(max, _pending.Count));
                foreach (var pair in _pending)
                {
                    if (batch.Count >= max) break;
                    batch.Add(pair.Value);
                }
                for (int i = 0; i < batch.Count; i++) _pending.Remove(batch[i].EntityNetId);
                return batch;
            }

            private void Requeue(List<NamedContentItemDto> batch)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var item = batch[i];
                    if (!_pending.ContainsKey(item.EntityNetId)) _pending[item.EntityNetId] = item;
                }
            }

            private void FlushDestroy()
            {
                int take = Math.Min(MaxGoneBatch, _destroyBuffer.Count);
                var batch = _destroyBuffer.GetRange(0, take);
                _destroyBuffer.RemoveRange(0, take);
                for (int i = 0; i < batch.Count; i++) _destroyPending.Remove(batch[i]);

                _goneInFlight = true;
                Send<JObject>(
                    NamedContentGonePath,
                    new Dictionary<string, object> { ["entityNetIds"] = batch },
                    _ => { _goneInFlight = false; },
                    (code, body) => { _goneInFlight = false; });
            }

            private static string NamedKey(string netId) => "n:" + netId;

            private static bool TryReadTitle(BaseEntity entity, out string kind, out string title)
            {
                kind = null;
                title = null;
                if (entity == null || entity.IsDestroyed || entity.net == null) return false;

                var vm = entity as VendingMachine;
                if (vm != null)
                {
                    kind = "shop";
                    title = vm.shopName;
                }
                else
                {
                    var bag = entity as SleepingBag;
                    if (bag == null) return false;
                    kind = "sleeping_bag";
                    title = bag.niceName;
                }

                title = title?.Trim();
                if (string.IsNullOrEmpty(title)) return false;
                if (title.Length > 128) title = title.Substring(0, 128);
                return true;
            }
        }

        /// <summary>
        /// Voice Props cassettes: FileStorage ogg blob → POST /api/ingest/audio, with the
        /// cassette's audio CRC as the change fingerprint.
        /// </summary>
        private class AudioWorker : RCoreWorker
        {
            private const int MaxQueuedJobs = 64;
            private const long MaxQueuedBytes = 32L * 1024 * 1024;
            private const int MaxGoneBatch = 500;
            private const int MaxUploadAttempts = 3;
            private const float RescanInterval = 25f;
            private const float ReconcileInterval = 300f;
            private const float RetryPause = 15f;

            private readonly Queue<AudioUploadJob> _queue = new Queue<AudioUploadJob>();
            /// <summary>netId → audio CRC last sent or queued.</summary>
            private readonly Dictionary<ulong, uint> _crcByNetId = new Dictionary<ulong, uint>();
            private readonly List<string> _destroyBuffer = new List<string>(32);
            private readonly HashSet<string> _destroyPending = new HashSet<string>();
            private long _queuedBytes;
            private bool _uploadInFlight;
            private float _nextUploadAt;
            private bool _goneInFlight;
            private bool _reconcileInFlight;
            private bool _syncQueued;
            private bool _syncRunning;
            private bool _synced;
            private float _syncAt = -1f;
            private float _nextRescanAt = -1f;
            private float _nextReconcileAt = -1f;

            protected override float Interval => 3f;

            private bool QueueBusy => _queue.Count >= MaxQueuedJobs || _queuedBytes >= MaxQueuedBytes;

            public void RequestFullSync(float delay)
            {
                _syncQueued = true;
                _syncAt = UnityEngine.Time.realtimeSinceStartup + Mathf.Max(0.5f, delay);
            }

            public void EnqueueDestroy(string netId)
            {
                if (string.IsNullOrEmpty(netId)) return;
                if (!_destroyPending.Add(netId)) return;
                _destroyBuffer.Add(netId);
                ForgetCrc(netId);
            }

            /// <summary>Forget the cached CRC after panel audio_clear (entity kept, bytes wiped).</summary>
            public void ForgetCrc(string netId)
            {
                if (string.IsNullOrEmpty(netId)) return;
                ulong raw;
                if (ulong.TryParse(netId, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                    _crcByNetId.Remove(raw);
                Instance?.ForgetFingerprint(AudioKey(netId));
            }

            protected override void Tick()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;
                float now = UnityEngine.Time.realtimeSinceStartup;

                if (_syncQueued && now >= _syncAt && !_syncRunning)
                {
                    _syncQueued = false;
                    StartCoroutine(RunFullSync());
                }

                if (_nextRescanAt < 0f) _nextRescanAt = now + RescanInterval;
                if (_synced && !_syncRunning && now >= _nextRescanAt)
                {
                    _nextRescanAt = now + RescanInterval;
                    Rescan();
                }

                if (_nextReconcileAt < 0f) _nextReconcileAt = now + ReconcileInterval;
                if (_synced && !_syncRunning && now >= _nextReconcileAt)
                {
                    _nextReconcileAt = now + ReconcileInterval;
                    Reconcile();
                }

                if (_destroyBuffer.Count > 0 && !_goneInFlight)
                    FlushDestroy();

                UploadNext();
            }

            private IEnumerator RunFullSync()
            {
                _syncRunning = true;
                try
                {
                    var wait = new WaitForSeconds(1f);
                    var index = Instance?._content;
                    while (index != null && !index.Ready) yield return wait;
                    if (index == null) yield break;

                    var entities = index.Live(index.Cassettes);
                    float sliceStart = UnityEngine.Time.realtimeSinceStartup;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        while (QueueBusy && Instance != null && Instance.IsLinked) yield return wait;
                        if (Instance == null || !Instance.IsLinked) yield break;

                        TryQueue(entities[i], null);

                        if (UnityEngine.Time.realtimeSinceStartup - sliceStart >= FrameBudgetSeconds)
                        {
                            yield return null;
                            sliceStart = UnityEngine.Time.realtimeSinceStartup;
                        }
                    }

                    _synced = true;
                    _nextReconcileAt = 0f;
                }
                finally
                {
                    _syncRunning = false;
                }
            }

            private void Rescan()
            {
                var index = Instance?._content;
                if (index == null || !index.Ready) return;
                var entities = index.Live(index.Cassettes);
                for (int i = 0; i < entities.Count && !QueueBusy; i++) TryQueue(entities[i], null);
            }

            private bool TryQueue(BaseEntity entity, string recorderSteamId)
            {
                if (entity == null || entity.IsDestroyed || entity.net == null || QueueBusy) return false;

                uint crc = Members.CassetteAudioIdOf(entity);
                if (crc == 0) return false;

                ulong id = entity.net.ID.Value;
                uint previous;
                if (_crcByNetId.TryGetValue(id, out previous) && previous == crc) return false;

                string netId = NetIdOf(entity);
                if (Instance.HasFingerprint(AudioKey(netId), crc.ToString(CultureInfo.InvariantCulture)))
                {
                    _crcByNetId[id] = crc;
                    return false;
                }

                byte[] bytes = ReadCassetteBytes(entity.net.ID, crc);
                if (bytes == null) return false;

                _queue.Enqueue(BuildJob(entity, id, netId, recorderSteamId, bytes, crc));
                _queuedBytes += bytes.Length;
                _crcByNetId[id] = crc;
                return true;
            }

            private void Reconcile()
            {
                var index = Instance?._content;
                if (_reconcileInFlight || index == null || !index.Ready) return;

                var live = ContentIndex.NetIds(index.Live(index.Cassettes));
                if (live.Count > MaxReconcileNetIds)
                {
                    WarnThrottled("audio", $"{live.Count} cassettes exceed the reconcile cap of {MaxReconcileNetIds}; skipping reconcile");
                    return;
                }

                _reconcileInFlight = true;
                Send<JObject>(
                    AudioReconcilePath,
                    new Dictionary<string, object>
                    {
                        ["wipeIdentityKey"] = WipeIdentityKey(),
                        ["liveNetIds"] = live
                    },
                    _ => { _reconcileInFlight = false; },
                    (code, body) => { _reconcileInFlight = false; });
            }

            private void FlushDestroy()
            {
                int take = Math.Min(MaxGoneBatch, _destroyBuffer.Count);
                var batch = _destroyBuffer.GetRange(0, take);
                _destroyBuffer.RemoveRange(0, take);
                for (int i = 0; i < batch.Count; i++) _destroyPending.Remove(batch[i]);

                _goneInFlight = true;
                Send<JObject>(
                    AudioGonePath,
                    new Dictionary<string, object> { ["netIds"] = batch },
                    _ => { _goneInFlight = false; },
                    (code, body) => { _goneInFlight = false; });
            }

            private void UploadNext()
            {
                if (_uploadInFlight || _queue.Count == 0) return;
                if (UnityEngine.Time.realtimeSinceStartup < _nextUploadAt) return;

                var pump = Engine?.Http;
                if (pump == null) return;

                var job = _queue.Dequeue();
                _queuedBytes -= job.Bytes.Length;
                _uploadInFlight = true;

                pump.Enqueue(
                    Instance.BuildUrl(AudioPath),
                    UnityWebRequest.kHttpVerbPOST,
                    BuildHeaders(job),
                    null,
                    (code, body) => OnUploadDone(job, code),
                    HttpLane.Bulk,
                    job.Bytes,
                    30);
            }

            private void OnUploadDone(AudioUploadJob job, int code)
            {
                _uploadInFlight = false;
                if (this == null) return;

                if (code >= 200 && code < 300)
                {
                    Instance?.RememberFingerprint(AudioKey(job.EntityNetId), job.AudioCrc.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                if (IsTransientHttpFailure(code) && ++job.Attempts < MaxUploadAttempts)
                {
                    _queue.Enqueue(job);
                    _queuedBytes += job.Bytes.Length;
                    _nextUploadAt = UnityEngine.Time.realtimeSinceStartup + RetryPause;
                    return;
                }

                uint queued;
                if (_crcByNetId.TryGetValue(job.NetId, out queued) && queued == job.AudioCrc)
                    _crcByNetId.Remove(job.NetId);
                WarnThrottled("audio", $"upload for entity {job.EntityNetId} failed with HTTP {code}");
            }

            private static Dictionary<string, string> BuildHeaders(AudioUploadJob job)
            {
                var headers = Instance.BuildHeaders(true);
                headers["Content-Type"] = "application/octet-stream";
                headers["X-Audio-Event-Id"] = job.EventId;
                headers["X-Audio-Entity-Net-Id"] = job.EntityNetId;
                headers["X-Audio-Kind"] = job.Kind;
                headers["X-Audio-Entity-Prefab"] = job.EntityPrefab ?? "";
                if (!string.IsNullOrEmpty(job.OwnerSteamId))
                    headers["X-Audio-Owner-Steam-Id"] = job.OwnerSteamId;
                if (!string.IsNullOrEmpty(job.RecorderSteamId))
                    headers["X-Audio-Recorder-Steam-Id"] = job.RecorderSteamId;
                headers["X-Audio-Pos-X"] = job.PosX.ToString("G", CultureInfo.InvariantCulture);
                headers["X-Audio-Pos-Y"] = job.PosY.ToString("G", CultureInfo.InvariantCulture);
                headers["X-Audio-Pos-Z"] = job.PosZ.ToString("G", CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(job.Grid))
                    headers["X-Audio-Grid"] = job.Grid;
                headers["X-Audio-Content-Hash"] = job.ContentHash;
                headers["X-Audio-Wipe-Identity-Key"] = job.WipeIdentityKey;
                headers["X-Audio-Content-Type"] = job.ContentType ?? "audio/ogg";
                if (job.DurationMs.HasValue)
                    headers["X-Audio-Duration-Ms"] = job.DurationMs.Value.ToString(CultureInfo.InvariantCulture);
                headers["X-Audio-Recorded-At"] = job.RecordedAt;
                return headers;
            }

            private static AudioUploadJob BuildJob(BaseEntity entity, ulong id, string netId, string recorderSteamId, byte[] bytes, uint crc)
            {
                Vector3 pos = PositionOf(entity);
                float maxLength = Members.CassetteLengthOf(entity);

                return new AudioUploadJob
                {
                    EventId = NewEventId(),
                    NetId = id,
                    EntityNetId = netId,
                    Kind = ClassifyAudioKind(entity),
                    EntityPrefab = entity.PrefabName ?? "",
                    OwnerSteamId = OwnerIdOf(entity),
                    RecorderSteamId = recorderSteamId ?? "",
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    Grid = GridLabel(pos),
                    ContentHash = Sha256Hex(bytes),
                    WipeIdentityKey = WipeIdentityKey(),
                    Bytes = bytes,
                    ContentType = IsOggHeader(bytes) ? "audio/ogg" : "application/octet-stream",
                    DurationMs = maxLength > 0f ? Mathf.RoundToInt(maxLength * 1000f) : (int?)null,
                    RecordedAt = UtcNow(),
                    AudioCrc = crc
                };
            }

            private static string AudioKey(string netId) => "a:" + netId;

            private static string ClassifyAudioKind(BaseEntity entity)
            {
                string name = entity.ShortPrefabName ?? entity.PrefabName ?? "";
                if (ContainsText(name, "boombox") || ContainsText(name, "boom_box")) return "boombox";
                if (ContainsText(name, "recorder")) return "recorder";
                if (ContainsText(name, "cassette")) return "cassette";
                return "other";
            }

            private static byte[] ReadCassetteBytes(NetworkableId netId, uint crc)
            {
                byte[] data = null;
                try
                {
                    data = FileStorage.server.Get(netId, 0UL, crc);
                }
                catch (Exception ex)
                {
                    WarnThrottled("audio", $"FileStorage read failed for {netId.Value}: {ex.Message}");
                }
                if (data == null || data.Length == 0) data = Members.TypedAudioGet(crc, netId);
                return data != null && data.Length > 0 && data.Length <= MaxAudioBytes ? data : null;
            }

            private static bool IsOggHeader(byte[] data)
            {
                return data != null
                    && data.Length >= 4
                    && data[0] == (byte)'O'
                    && data[1] == (byte)'g'
                    && data[2] == (byte)'g'
                    && data[3] == (byte)'S';
            }
        }

        private class AudioUploadJob
        {
            public string EventId;
            public ulong NetId;
            public string EntityNetId;
            public string Kind;
            public string EntityPrefab;
            public string OwnerSteamId;
            public string RecorderSteamId;
            public float PosX;
            public float PosY;
            public float PosZ;
            public string Grid;
            public string ContentHash;
            public string WipeIdentityKey;
            public byte[] Bytes;
            public string ContentType;
            public int? DurationMs;
            public string RecordedAt;
            public uint AudioCrc;
            public int Attempts;
        }

        private class NamedContentItemDto
        {
            public string EntityNetId;
            public string Kind;
            public string Title;
            public float PosX;
            public float PosY;
            public float PosZ;
            public string Grid;
            public string OwnerSteamId;
            public string WipeIdentityKey;
        }

        private class NamedContentBatchDto
        {
            [JsonProperty("items")] public List<NamedContentItemWire> Items;
            [JsonProperty("reconcile", NullValueHandling = NullValueHandling.Ignore)] public bool? Reconcile;
            [JsonProperty("liveNetIds", NullValueHandling = NullValueHandling.Ignore)] public List<string> LiveNetIds;
            [JsonProperty("wipeIdentityKey", NullValueHandling = NullValueHandling.Ignore)] public string WipeIdentityKey;
        }

        private class NamedContentItemWire
        {
            [JsonProperty("entityNetId")] public string EntityNetId;
            [JsonProperty("kind")] public string Kind;
            [JsonProperty("title")] public string Title;
            [JsonProperty("posX")] public float PosX;
            [JsonProperty("posY")] public float PosY;
            [JsonProperty("posZ")] public float PosZ;
            [JsonProperty("grid", NullValueHandling = NullValueHandling.Ignore)] public string Grid;
            [JsonProperty("ownerSteamId", NullValueHandling = NullValueHandling.Ignore)] public string OwnerSteamId;
            [JsonProperty("wipeIdentityKey")] public string WipeIdentityKey;
        }

        private class QueueWorker : RCoreWorker
        {
            private Dictionary<string, Func<JToken, TaskResult>> _handlers;

            protected override float Interval => QueuePollInterval;

            private Dictionary<string, Func<JToken, TaskResult>> Handlers
            {
                get
                {
                    if (_handlers == null) _handlers = Instance.BuildTaskHandlers();
                    return _handlers;
                }
            }

            private Dictionary<string, TaskResult> PendingAck
            {
                get
                {
                    if (Instance?._tasks == null) return null;
                    if (Instance._tasks.PendingAck == null)
                        Instance._tasks.PendingAck = new Dictionary<string, TaskResult>();
                    return Instance._tasks.PendingAck;
                }
            }

            protected override void Tick()
            {
                Instance?.PruneProcessedTasks();
                FlushAckIfIdle();
                Poll();
            }

            public void Poll()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;

                Send<List<QueueTaskDto>>(QueuePath, null,
                    tasks =>
                    {
                        Dispatch(tasks);
                        FlushAckIfIdle();
                    }, null, RequestMethod.GET);
            }

            public void Dispatch(List<QueueTaskDto> tasks)
            {
                if (tasks == null || tasks.Count == 0 || Instance == null) return;

                var results = new Dictionary<string, TaskResult>();
                int handled = 0;

                foreach (var task in tasks)
                {
                    if (task == null || string.IsNullOrEmpty(task.Type)) continue;

                    if (string.IsNullOrEmpty(task.Id)) continue;

                    if (handled >= MaxTasksPerAck) break;
                    handled++;

                    Func<JToken, TaskResult> handler;
                    if (!Handlers.TryGetValue(task.Type, out handler))
                    {
                        var unknown = TaskResult.Fail("UNKNOWN_TYPE", $"no handler for '{task.Type}'");
                        Instance.MarkProcessed(task.Id, unknown);
                        results[task.Id] = unknown;
                        continue;
                    }

                    TaskResult processed;
                    if (Instance.WasProcessed(task.Id, out processed))
                    {
                        if (IsIdempotentPunishment(task.Type))
                        {
                            try { handler(task.Data); }
                            catch (Exception ex)
                            {
                                results[task.Id] = TaskResult.Fail("HANDLER_ERROR", ex.Message);
                                continue;
                            }
                        }
                        if (processed != null) results[task.Id] = processed.AsReplay();
                        continue;
                    }

                    TaskResult result;
                    try { result = handler(task.Data) ?? TaskResult.Ok(); }
                    catch (Exception ex)
                    {
                        result = TaskResult.Fail("HANDLER_ERROR", ex.Message);
                    }

                    Instance.MarkProcessed(task.Id, result);
                    results[task.Id] = result;
                }

                if (results.Count > 0)
                {
                    QueueAck(results);
                    Instance.FlushProcessedTasksIfDirty(force: true);
                }
            }

            private void QueueAck(Dictionary<string, TaskResult> results)
            {
                if (results == null || results.Count == 0) return;
                var pending = PendingAck;
                if (pending == null) return;

                foreach (var pair in results) pending[pair.Key] = pair.Value;
                Instance._tasksDirty = true;
                FlushAckIfIdle();
            }

            private bool _ackInFlight;

            private void FlushAckIfIdle()
            {
                var pending = PendingAck;
                if (pending == null || pending.Count == 0) return;
                if (!Ready || Instance == null || !Instance.IsLinked) return;
                if (_ackInFlight) return;

                var payload = new Dictionary<string, TaskResult>(pending);
                _ackInFlight = true;

                Send<JObject>(AckPath, new AckPayload { Results = payload },
                    _ =>
                    {
                        _ackInFlight = false;
                        var live = PendingAck;
                        if (live == null) return;
                        foreach (var key in payload.Keys) live.Remove(key);
                        Instance._tasksDirty = true;
                        Instance.FlushProcessedTasksIfDirty(force: true);
                        FlushAckIfIdle();
                    },
                    (code, body) => { _ackInFlight = false; });
            }

            protected override void Drain()
            {
                FlushAckIfIdle();
                Instance?.FlushProcessedTasksIfDirty(force: true);
            }

            public void ScheduleWriteCfg()
            {
                CancelInvoke(nameof(WriteCfg));
                Invoke(nameof(WriteCfg), 5f);
            }

            private void WriteCfg() => ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.writecfg");
        }

        private class PolicyWorker : MonoBehaviour
        {
            private void Awake()
            {
                InvokeRepeating(nameof(Tick), UnityEngine.Random.Range(0.2f, 1f), PolicyFlushInterval);
            }

            private void OnDestroy()
            {
                CancelInvoke();
            }

            private void Tick()
            {
                Instance?.FlushPolicyQueue();
            }
        }

        #endregion

        #region Hooks

        private void OnNewSave(string filename)
        {
            SetPendingWipe(true);
            SendWipe();
            _engine?.Drawings?.RequestFullSync(15f);
            _engine?.NamedContent?.RequestFullSync(16f);
            _engine?.Audio?.RequestFullSync(17f);
        }

        private void OnSignUpdated(Signage sign, BasePlayer player, int textureIndex)
        {
            if (!IsLinked || sign == null || sign.IsDestroyed) return;
            string editor = player != null ? player.UserIDString : null;
            _engine?.Drawings?.ScheduleCapture(sign, editor, Math.Max(0, textureIndex), PaintableKind(sign));
        }

        private void OnSignUpdated(Signage sign, BasePlayer player)
        {
            OnSignUpdated(sign, player, 0);
        }

        private void OnSignUpdated(PhotoFrame frame, BasePlayer player)
        {
            if (!IsLinked || frame == null || frame.IsDestroyed) return;
            string editor = player != null ? player.UserIDString : null;
            _engine?.Drawings?.ScheduleCapture(frame, editor, 0, "photo_frame");
        }

        private void OnSignUpdated(CarvablePumpkin pumpkin, BasePlayer player)
        {
            if (!IsLinked || pumpkin == null || pumpkin.IsDestroyed) return;
            string editor = player != null ? player.UserIDString : null;
            _engine?.Drawings?.ScheduleCapture(pumpkin, editor, 0, "pumpkin");
        }

        private void OnItemPainted(PaintedItemStorageEntity entity, Item item, BasePlayer player, byte[] image)
        {
            if (!IsLinked || entity == null || entity.IsDestroyed || image == null || image.Length == 0) return;
            string editor = player != null ? player.UserIDString : null;
            _engine?.Drawings?.EnqueueBytes(entity, editor, "painted_item", image, 0);
        }

        private void OnFireworkDesignChanged(PatternFirework firework, ProtoBuf.PatternFirework.Design design, BasePlayer player)
        {
            if (!IsLinked || firework == null || firework.IsDestroyed || design == null) return;
            string editor = player != null ? player.UserIDString : null;
            _engine?.Drawings?.EnqueueFirework(firework, design, editor, force: true);
        }

        private void OnImagePost(BasePlayer player, string url, bool raw, ISignage signage, uint textureIndex)
        {
            if (!IsLinked || signage == null) return;
            var ent = signage as BaseEntity;
            if (ent == null || ent.IsDestroyed) return;
            string editor = player != null ? player.UserIDString : null;
            _engine?.Drawings?.ScheduleCapture(ent, editor, (int)textureIndex, PaintableKind(ent));
        }

        private void OnEntitySpawned(BaseNetworkable entity) => _content.Add(entity);

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (!IsLinked || plan == null || go == null) return;
            var ent = go.ToBaseEntity();
            if (ent == null) return;

            if (ent is VendingMachine || ent is SleepingBag)
            {
                NextTick(() => _engine?.NamedContent?.ObserveEntity(ent));
                return;
            }

            var player = plan.GetOwnerPlayer();
            string editor = player != null ? player.UserIDString : null;

            if (ent is BaseSculpture)
            {
                // FileStorage gets the sculpture CRC shortly after placement.
                timer.Once(0.5f, () => _engine?.Drawings?.EnqueueSculpture(ent, editor, force: true));
                return;
            }

            var signage = ent as ISignage;
            if (signage == null) return;
            NextTick(() =>
            {
                if (ent == null || ent.IsDestroyed || ent.net == null) return;
                var crcs = signage.GetTextureCRCs();
                if (crcs == null || crcs.Length == 0 || crcs[0] == 0) return;
                _engine?.Drawings?.ScheduleCapture(ent, editor, 0, PaintableKind(ent));
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null || entity.net == null) return;
            var kind = _content.Remove(entity);
            if (kind == ContentKind.None || !IsLinked || _engine == null) return;

            string netId = NetIdOf(entity);
            switch (kind)
            {
                case ContentKind.Paintable:
                    _engine.Drawings?.EnqueueDestroy(netId);
                    break;
                case ContentKind.Named:
                    _engine.NamedContent?.EnqueueDestroy(netId);
                    break;
                case ContentKind.Cassette:
                    _engine.Audio?.EnqueueDestroy(netId);
                    break;
            }
        }

        /// <summary>
        /// An inventory cassette destroyed through the item (F1 destroy, drop-clear) can skip
        /// the entity kill path, so resolve its held entity here.
        /// </summary>
        private void OnItemRemove(Item item)
        {
            if (item?.info == null) return;

            bool isCassette;
            int itemId = item.info.itemid;
            if (!_cassetteItemIds.TryGetValue(itemId, out isCassette))
            {
                isCassette = (item.info.shortname ?? "").IndexOf("cassette", StringComparison.OrdinalIgnoreCase) >= 0;
                _cassetteItemIds[itemId] = isCassette;
            }
            if (!isCassette) return;

            var held = item.GetHeldEntity();
            if (held == null || held.net == null) return;

            _content.Remove(held);
            if (IsLinked) _engine?.Audio?.EnqueueDestroy(NetIdOf(held));
        }

        private void OnClientAuth(Network.Connection connection)
        {
            if (!IsLinked || connection == null || !IsRealSteamId(connection.userid)) return;
            EnqueuePolicyCheck(connection.userid.ToString(), CleanIp(connection.ipaddress));
        }

        private object CanUserLogin(string name, string id, string ipAddress)
        {
            if (ulong.TryParse(id, out ulong uid))
                _pendingKick.Remove(uid);

            return EvaluateLoginAutokick(id, ipAddress);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            _voiceToastCooldowns.Remove(player.userID);
            _afkTrackers.Remove(player.userID);

            if (_pendingKick.TryGetValue(player.userID, out string pendingReason))
            {
                _pendingKick.Remove(player.userID);
                player.Kick(pendingReason);
                return;
            }

            if (IsLinked) _engine?.RequestFullSync(2f);

            if (IsLinked && IsRealSteamId(player.userID))
            {
                string ip = player.net?.connection != null ? CleanIp(player.net.connection.ipaddress) : "0.0.0.0";
                ApplyCachedPolicyOrEnqueue(player.UserIDString, ip);
            }

            string muteReason, muteTime;
            if (IsMuted(player, out muteReason, out muteTime))
                player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, true);

            if (IsLinked) _engine?.Runtime?.OnPlayerConnected(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            _voiceToastCooldowns.Remove(player.userID);
            _afkTrackers.Remove(player.userID);
            _woundedHits.Remove(player.UserIDString);
            _engine?.RecordDisconnect(player.UserIDString, reason);
            _engine?.RequestFullSync(1f);
            _engine?.Runtime?.RecordEvent("player.disconnected", player);
        }

        private void OnTeamCreated(BasePlayer player, RelationshipManager.PlayerTeam team)
        {
            if (team == null) return;
            RecordTeamChange("created", team, player.UserIDString, player.UserIDString);
        }

        private void OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong target)
        {
            if (team == null || !IsRealSteamId(target)) return;
            RecordTeamChange("kick", team, player.UserIDString, target.ToString());
        }

        private void OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (team == null) return;
            if (team.members != null && team.members.Count == 1) return;
            RecordTeamChange("leave", team, player.UserIDString, player.UserIDString);
        }

        private void OnTeamDisband(RelationshipManager.PlayerTeam team)
        {
            if (team == null || team.members == null) return;
            for (int i = 0; i < team.members.Count; i++)
            {
                ulong id = team.members[i];
                if (!IsRealSteamId(id)) continue;
                string steamId = id.ToString();
                RecordTeamChange("disband", team, steamId, steamId);
            }
        }

        private void RecordTeamChange(string kind, RelationshipManager.PlayerTeam team, string actorSteamId, string targetSteamId)
        {
            if (!IsLinked || team == null) return;
            _engine?.RecordTeamChange(kind, team.teamID.ToString(), actorSteamId, targetSteamId);
        }

        private object OnClientCommand(Network.Connection connection, string text)
        {
            if (connection == null || !IsRealSteamId(connection.userid)) return null;

            string body;
            if (!TryGetChatBody(text, out body)) return null;
            if (body.StartsWith("/", StringComparison.Ordinal)) return null;

            string steamId = connection.userid.ToString();
            string reason, timeLeft;
            if (IsMuted(steamId, out reason, out timeLeft))
            {
                var mutedPlayer = connection.player as BasePlayer;
                if (mutedPlayer != null)
                {
                    string notice = Message("Mute.Blocked", mutedPlayer, reason, timeLeft);
                    SendToast(mutedPlayer, notice);
                    mutedPlayer.ChatMessage(notice);
                }
                return false;
            }

            // Capture before IQChat/BetterChat OnPlayerChat (they return false and skip later plugins).
            EnqueueChatCapture(connection.player as BasePlayer, steamId, body, ChannelFromSayCommand(text));
            return null;
        }

        private object OnUserChat(Oxide.Core.Libraries.Covalence.IPlayer player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return null;
            if (!IsMuted(player.Id)) return null;
            return false;
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;

            string reason, timeLeft;
            if (IsMuted(player, out reason, out timeLeft))
            {
                string notice = Message("Mute.Blocked", player, reason, timeLeft);
                SendToast(player, notice);
                player.ChatMessage(notice);
                return false;
            }

            // Ingest happens in OnClientCommand, IQChat always returns false from OnPlayerChat.
            return null;
        }

        private void EnqueueChatCapture(BasePlayer player, string steamId, string message, string channelName)
        {
            var chat = _engine?.Chat;
            if (chat == null || string.IsNullOrWhiteSpace(message)) return;

            string teamId = null;
            if (channelName == "TEAM" && player != null && player.currentTeam != 0)
                teamId = player.currentTeam.ToString();

            chat.Enqueue(new ChatItemDto
            {
                EventId = NewEventId(),
                SteamId = steamId,
                Name = player != null ? (player.displayName ?? "Unknown") : "Unknown",
                Message = message,
                Channel = channelName,
                TeamId = teamId,
                CreatedAt = UtcNow()
            });
        }

        private static string ChannelFromSayCommand(string text)
        {
            if (text.StartsWith("chat.teamsay", StringComparison.OrdinalIgnoreCase)) return "TEAM";
            if (text.StartsWith("chat.localsay", StringComparison.OrdinalIgnoreCase)) return "LOCAL";
            return "GLOBAL";
        }

        private object OnPlayerVoice(BasePlayer player, byte[] data)
        {
            MuteInfo mute;
            if (!TryGetActiveMuteInfo(player.UserIDString, out mute)) return null;

            float lastWarning;
            if (!_voiceToastCooldowns.TryGetValue(player.userID, out lastWarning)
                || UnityEngine.Time.realtimeSinceStartup - lastWarning > VoiceToastCooldown)
            {
                string timeLeft = FormatTimeRemaining(mute.ExpiresAtParsed ?? ParseExpiry(mute.ExpiresAt));
                SendToast(player, Message("Mute.VoiceBlocked", player, timeLeft));
                _voiceToastCooldowns[player.userID] = UnityEngine.Time.realtimeSinceStartup;
            }

            return true;
        }

        private void OnPlayerReported(BasePlayer reporter, string targetName, string targetId,
                                      string subject, string message, string type)
        {
            var reports = _engine?.Reports;
            if (reports == null || reporter == null) return;

            reports.Enqueue(new ReportItemDto
            {
                EventId = NewEventId(),
                ReporterSteamId = reporter.UserIDString,
                ReporterName = reporter.displayName ?? "Unknown",
                TargetSteamId = targetId ?? "",
                TargetName = targetName ?? "Unknown",
                Subject = subject ?? "",
                Message = message ?? "",
                Type = type ?? "player_report",
                CreatedAt = UtcNow()
            });
        }

        private void OnPlayerWound(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || !IsRealSteamId(player.userID)) return;

            var initiator = info.InitiatorPlayer;
            if (initiator == null || initiator == player || !IsRealSteamId(initiator.userID)) return;

            _woundedHits[player.UserIDString] = new HitRecord(
                initiator.UserIDString,
                initiator.displayName ?? "Unknown",
                WeaponName(info),
                info.ProjectileDistance,
                info.isHeadshot,
                BodyPart(info),
                UnityEngine.Time.realtimeSinceStartup);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            _woundedHits.Remove(player.UserIDString);
            _engine?.Runtime?.RecordEvent("player.respawned", player);
        }

        private void OnPlayerRecovered(BasePlayer player)
        {
            if (player != null) _woundedHits.Remove(player.UserIDString);
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            var combat = _engine?.Combat;
            if (combat == null || player == null || !IsRealSteamId(player.userID)) return;

            string victimId = player.UserIDString;
            HitRecord wound;
            bool hasWound = _woundedHits.TryGetValue(victimId, out wound);
            _woundedHits.Remove(victimId);

            var death = BuildDeathEvent(player, info, hasWound, wound);
            // Only PvP kills are reported. PvE, suicide and environment deaths are skipped.
            if (death.DeathType != "pvp"
                || string.IsNullOrEmpty(death.KillerSteamId)
                || death.KillerSteamId == death.VictimSteamId)
                return;

            ulong victimUserId = player.userID;
            NextTick(() =>
            {
                if (_engine?.Combat == null) return;
                try
                {
                    death.CombatLog = ReadVictimCombatLog(victimUserId);
                }
                catch (Exception ex)
                {
                    _engine.RaiseAlert("combatlog_error", $"CombatLog.Get failed for {victimId}: {ex.Message}",
                        new { victimSteamId = victimId, side = "victim" });
                }
                _engine.Combat.Enqueue(death);
            });
        }

        #endregion

        #region Queue Tasks

        private Dictionary<string, Func<JToken, TaskResult>> BuildTaskHandlers()
        {
            return new Dictionary<string, Func<JToken, TaskResult>>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = HandleChatTask,
                ["command"] = HandleCommandTask,
                ["ban"] = HandleBanTask,
                ["unban"] = HandleUnbanTask,
                ["mute"] = HandleMuteTask,
                ["unmute"] = HandleUnmuteTask,
                ["kick"] = HandleKickTask,
                ["teleport"] = HandleTeleportTask,
                ["give"] = HandleGiveTask,
                ["drawing_clear"] = HandleDrawingClearTask,
                ["named_content_clear"] = HandleNamedContentClearTask,
                ["audio_clear"] = HandleAudioClearTask,
                ["action"] = HandleActionTask
            };
        }

        private TaskResult HandleChatTask(JToken raw)
        {
            var task = Parse<ChatTaskDto>(raw);
            string content = task?.Message ?? task?.Text ?? task?.Content;
            if (string.IsNullOrEmpty(content)) return TaskResult.Fail("EMPTY_MESSAGE", "no message body");

            string sender = string.IsNullOrEmpty(task.Name) ? "Admin" : task.Name;
            int channel = task.Channel == "TEAM" ? 1 : 0;

            bool hasTarget = !string.IsNullOrEmpty(task.TargetSteamId) && ulong.TryParse(task.TargetSteamId, out _);
            string header = hasTarget
                ? $"<color=#C4A882><size=12>Private message from Administrator {sender}</size></color>"
                : $"<color=#C4A882><size=12>Message from Administrator {sender}</size></color>";
            string formatted = $"{header}\n{content}";

            if (hasTarget)
            {
                BasePlayer target;
                TaskResult error;
                if (!TryResolvePlayer(task.TargetSteamId, out target, out error)) return error;
                target.SendConsoleCommand("chat.add", channel, _config.AvatarSteamId, formatted);
                return TaskResult.Ok();
            }

            Broadcast(channel, formatted);
            return TaskResult.Ok();
        }

        private TaskResult HandleCommandTask(JToken raw)
        {
            var task = Parse<CommandTaskDto>(raw);
            if (task == null || string.IsNullOrWhiteSpace(task.Command))
                return TaskResult.Fail("EMPTY_COMMAND", "no command supplied");

            return TaskResult.Fail("COMMANDS_DISABLED", "remote commands are disabled");
        }

        private TaskResult HandleBanTask(JToken raw)
        {
            var task = Parse<BanTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            if (!TryResolvePunishmentExpiry(task.DurationSeconds, task.ExpiresAt, out _, out string announceDuration))
                return TaskResult.Fail("ALREADY_EXPIRED", "ban expiry is in the past");

            string reason = string.IsNullOrEmpty(task.Reason) ? "Banned" : task.Reason;

            ConsoleSystem.Run(ConsoleSystem.Option.Server, "banid", task.SteamId, "RCore", reason);

            if (task.BanIp && !string.IsNullOrEmpty(task.Ip))
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "banip", task.Ip, reason);
            }

            _engine?.Queue?.ScheduleWriteCfg();

            var online = FindConnectedPlayer(task.SteamId);
            if (online != null) online.Kick("Banned: " + reason);

            if (task.Announce)
            {
                AnnouncePublic(
                    "Ban.Announced",
                    DisplayName(task.PlayerName, task.SteamId),
                    announceDuration,
                    reason);
            }

            return TaskResult.Ok();
        }

        private TaskResult HandleUnbanTask(JToken raw)
        {
            var task = Parse<TargetTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            ConsoleSystem.Run(ConsoleSystem.Option.Server, "unban", task.SteamId);
            _engine?.Queue?.ScheduleWriteCfg();

            if (task.Announce)
                AnnouncePublic("Unban.Announced", DisplayName(task.PlayerName, task.SteamId));

            return TaskResult.Ok();
        }

        private static bool IsIdempotentPunishment(string type) =>
            type == "mute" || type == "unmute" || type == "ban" || type == "unban";

        private TaskResult HandleDrawingClearTask(JToken raw)
        {
            var task = Parse<DrawingClearTaskDto>(raw);
            BaseEntity ent;
            TaskResult error;
            if (!TryFindTaskEntity(task?.EntityNetId, out ent, out error)) return error;

            try
            {
                if (ent is Signage signage)
                    ClearSignageTextures(signage);
                else if (ent is PhotoFrame frame)
                    ClearPhotoFrameTextures(frame);
                else if (ent is CarvablePumpkin pumpkin)
                    ClearPumpkinTextures(pumpkin);
                else if (ent is BaseSculpture sculpture)
                    ClearBaseSculpture(sculpture);
                else if (ent is PatternFirework firework)
                    ClearFireworkDesign(firework);
                else if (ent is PaintedItemStorageEntity painted)
                {
                    if (!Members.ClearPaintedImage(painted))
                        return TaskResult.Fail("UNSUPPORTED", "painted item image field not found on this build");
                    painted.SendNetworkUpdate();
                }
                else
                    return TaskResult.Fail("UNSUPPORTED", ent.ShortPrefabName ?? "entity");
            }
            catch (Exception ex)
            {
                return TaskResult.Fail("CLEAR_FAILED", ex.Message);
            }

            return TaskResult.Ok();
        }

        private TaskResult HandleNamedContentClearTask(JToken raw)
        {
            var task = Parse<NamedContentClearTaskDto>(raw);
            BaseEntity ent;
            TaskResult error;
            if (!TryFindTaskEntity(task?.EntityNetId, out ent, out error)) return error;

            try
            {
                var vm = ent as VendingMachine;
                if (vm != null)
                {
                    vm.shopName = "";
                    vm.SendNetworkUpdate();
                    _engine?.NamedContent?.EnqueueDestroy(task.EntityNetId);
                    return TaskResult.Ok();
                }

                var bag = ent as SleepingBag;
                if (bag != null)
                {
                    bag.niceName = "";
                    bag.SendNetworkUpdate();
                    _engine?.NamedContent?.EnqueueDestroy(task.EntityNetId);
                    return TaskResult.Ok();
                }

                return TaskResult.Fail("UNSUPPORTED", ent.ShortPrefabName ?? "entity");
            }
            catch (Exception ex)
            {
                return TaskResult.Fail("CLEAR_FAILED", ex.Message);
            }
        }

        private TaskResult HandleAudioClearTask(JToken raw)
        {
            var task = Parse<AudioClearTaskDto>(raw);
            BaseEntity ent;
            TaskResult error;
            if (!TryFindTaskEntity(task?.EntityNetId, out ent, out error)) return error;

            try
            {
                if (!ClearCassetteAudio(ent))
                    return TaskResult.Fail("UNSUPPORTED", ent.ShortPrefabName ?? "entity");
                _engine?.Audio?.ForgetCrc(task.EntityNetId);
                return TaskResult.Ok();
            }
            catch (Exception ex)
            {
                return TaskResult.Fail("CLEAR_FAILED", ex.Message);
            }
        }

        private static bool TryFindTaskEntity(string rawNetId, out BaseEntity entity, out TaskResult error)
        {
            entity = null;
            error = null;
            if (string.IsNullOrEmpty(rawNetId))
            {
                error = TaskResult.Fail("NO_TARGET", "entityNetId missing");
                return false;
            }

            ulong id;
            if (!ulong.TryParse(rawNetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                error = TaskResult.Fail("BAD_ENTITY", rawNetId);
                return false;
            }

            entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
            if (entity == null || entity.IsDestroyed)
            {
                entity = null;
                error = TaskResult.Fail("ENTITY_GONE", rawNetId);
                return false;
            }
            return true;
        }

        private static bool ClearCassetteAudio(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed || !ContentIndex.IsCassette(entity)) return false;

            uint previous = Members.CassetteAudioIdOf(entity);
            Members.ClearCassetteAudioId(entity);
            if (previous != 0 && entity.net != null) Members.RemoveAudioBlob(previous, entity.net.ID);

            entity.SendNetworkUpdate();
            return true;
        }

        private static void ClearSignageTextures(Signage signage)
        {
            if (signage.net == null) return;
            ZeroAll(signage.textureIDs);
            ZeroAll(((ISignage)signage).GetTextureCRCs());
            signage.SendNetworkUpdate();
        }

        private static void ClearPhotoFrameTextures(PhotoFrame frame)
        {
            if (frame.net == null) return;
            Members.ClearPhotoOverlay(frame);
            ZeroAll(((ISignage)frame).GetTextureCRCs());
            frame.SendNetworkUpdate();
        }

        private static void ClearPumpkinTextures(CarvablePumpkin pumpkin)
        {
            if (pumpkin.net == null) return;
            ZeroAll(Members.PumpkinTextureIds(pumpkin));
            pumpkin.SendNetworkUpdate();
        }

        private static void ClearBaseSculpture(BaseSculpture sculpture)
        {
            if (sculpture.IsDestroyed) return;
            if (Members.ResetSculpture(sculpture))
            {
                sculpture.SendNetworkUpdate();
                return;
            }
            sculpture.Kill();
        }

        private static void ClearFireworkDesign(PatternFirework firework)
        {
            var stars = firework.Design?.stars;
            if (stars != null) stars.Clear();
            firework.SendNetworkUpdate();
        }

        private static void ZeroAll(uint[] ids)
        {
            if (ids == null) return;
            for (int i = 0; i < ids.Length; i++) ids[i] = 0;
        }

        private TaskResult HandleMuteTask(JToken raw)
        {
            var task = Parse<MuteTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            string muteExpiresAt;
            string announceDuration;
            if (!TryResolvePunishmentExpiry(task.DurationSeconds, task.ExpiresAt, out muteExpiresAt, out announceDuration))
                return TaskResult.Fail("ALREADY_EXPIRED", "mute expiry is in the past");

            string reason = string.IsNullOrEmpty(task.Reason) ? "Rule Violation" : task.Reason;
            bool alreadyMuted = IsMuted(task.SteamId);
            ApplyMute(task.SteamId, reason, muteExpiresAt, task.PlayerName, notify: !alreadyMuted);

            if (task.Announce && !alreadyMuted)
            {
                AnnouncePublic(
                    "Mute.Announced",
                    DisplayName(task.PlayerName, task.SteamId),
                    announceDuration,
                    reason);
            }

            return TaskResult.Ok();
        }

        private TaskResult HandleUnmuteTask(JToken raw)
        {
            var task = Parse<TargetTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            ClearMute(task.SteamId, false);

            if (task.Announce)
                AnnouncePublic("Unmute.Announced", DisplayName(task.PlayerName, task.SteamId));

            return TaskResult.Ok();
        }

        private TaskResult HandleKickTask(JToken raw)
        {
            var task = Parse<KickTaskDto>(raw);
            if (task == null) return TaskResult.Fail("NO_TARGET", "steamId missing");

            BasePlayer player;
            TaskResult error;
            if (!TryResolvePlayer(task.SteamId, out player, out error)) return error;

            string reason = string.IsNullOrEmpty(task.Reason) ? Message("Kick.Default", player) : task.Reason;
            player.Kick(reason);
            return TaskResult.Ok();
        }

        private TaskResult HandleTeleportTask(JToken raw)
        {
            var task = Parse<TeleportTaskDto>(raw);
            if (task == null) return TaskResult.Fail("NO_TARGET", "steamId missing");

            BasePlayer player;
            TaskResult error;
            if (!TryResolvePlayer(task.SteamId, out player, out error)) return error;

            Vector3 destination;
            if (!string.IsNullOrEmpty(task.TargetSteamId))
            {
                BasePlayer anchor;
                if (!TryResolvePlayer(task.TargetSteamId, out anchor, out error, "destination player is not connected"))
                    return error;
                destination = anchor.transform.position;
            }
            else if (task.X.HasValue && task.Y.HasValue && task.Z.HasValue)
            {
                destination = new Vector3(task.X.Value, task.Y.Value, task.Z.Value);
            }
            else
            {
                // no coords used to mean 0,0,0
                return TaskResult.Fail("NO_DESTINATION", "targetSteamId or x/y/z required");
            }

            player.Teleport(destination);
            return TaskResult.Ok();
        }

        private TaskResult HandleGiveTask(JToken raw)
        {
            var task = Parse<GiveTaskDto>(raw);
            if (task == null) return TaskResult.Fail("NO_TARGET", "steamId missing");
            if (string.IsNullOrEmpty(task.Item)) return TaskResult.Fail("NO_ITEM", "item shortname missing");

            BasePlayer player;
            TaskResult error;
            if (!TryResolvePlayer(task.SteamId, out player, out error)) return error;

            int amount = task.Amount > 0 ? task.Amount : 1;
            var item = ItemManager.CreateByName(task.Item, amount, task.SkinId);
            if (item == null) return TaskResult.Fail("UNKNOWN_ITEM", task.Item);

            player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
            return TaskResult.Ok();
        }

        private bool TryResolvePlayer(string steamId, out BasePlayer player, out TaskResult error,
                                      string offlineMessage = "target is not connected")
        {
            player = null;
            error = null;
            if (string.IsNullOrEmpty(steamId))
            {
                error = TaskResult.Fail("NO_TARGET", "steamId missing");
                return false;
            }
            if (!ulong.TryParse(steamId, out ulong uid))
            {
                error = TaskResult.Fail("BAD_STEAMID", steamId);
                return false;
            }
            // FindByID sometimes misses connected players
            player = FindConnectedPlayer(uid.ToString(CultureInfo.InvariantCulture));
            if (player == null)
            {
                error = TaskResult.Fail("PLAYER_OFFLINE", offlineMessage);
                return false;
            }
            return true;
        }

        private bool WasProcessed(string key, out TaskResult result)
        {
            result = null;

            ProcessedTask entry;
            if (!_tasks.Processed.TryGetValue(key, out entry) || entry == null) return false;

            if (entry.ExpiresAt <= NowUnix())
            {
                _tasks.Processed.Remove(key);
                return false;
            }

            result = entry.Result;
            return true;
        }

        private void MarkProcessed(string key, TaskResult result)
        {
            _tasks.Processed[key] = new ProcessedTask
            {
                ExpiresAt = NowUnix() + TaskRetentionSeconds,
                Result = result
            };

            if (_tasks.Processed.Count > MaxProcessedTasks)
                PruneProcessedTasks(force: true);

            _tasksDirty = true;
        }

        private void FlushProcessedTasksIfDirty(bool force = false)
        {
            if (!force && !_tasksDirty) return;
            SaveProcessedTasks();
        }

        private void SaveProcessedTasks()
        {
            if (_tasks == null) return;
            if (TrySave(TaskFileName, _tasks, "tasks"))
                _tasksDirty = false;
        }

        private void PruneProcessedTasks(bool force = false)
        {
            if (_tasks?.Processed == null || _tasks.Processed.Count == 0) return;

            bool removedAny = false;
            long now = NowUnix();
            var stale = new List<string>();
            foreach (var entry in _tasks.Processed)
            {
                if (entry.Value == null || entry.Value.ExpiresAt <= now)
                    stale.Add(entry.Key);
            }
            foreach (var key in stale)
            {
                _tasks.Processed.Remove(key);
                removedAny = true;
            }

            if (force && _tasks.Processed.Count > MaxProcessedTasks)
            {
                var ordered = new List<KeyValuePair<string, ProcessedTask>>(_tasks.Processed);
                ordered.Sort((a, b) =>
                {
                    long ae = a.Value?.ExpiresAt ?? 0;
                    long be = b.Value?.ExpiresAt ?? 0;
                    return ae.CompareTo(be);
                });
                int remove = _tasks.Processed.Count - MaxProcessedTasks;
                for (int i = 0; i < remove; i++)
                {
                    _tasks.Processed.Remove(ordered[i].Key);
                    removedAny = true;
                }
            }

            if (removedAny) _tasksDirty = true;
        }

        #endregion

        #region Addon Runtime

        // Addon logic lives in the backend, the plugin only runs the primitives below.
        // See PROTOCOL_V2.md "Addon runtime".

        private const int RuntimeVersion = 1;
        private const string RuntimeFileName = "RCore/runtime";
        private const int CommandTimeoutSeconds = 5;
        private const float MinCommandCooldownSeconds = 2f;
        private const float RuntimeEventsFlushSeconds = 2f;
        private const int MaxRuntimeEventsBuffered = 1000;
        private const int MaxRuntimeEventsPerBatch = 200;
        private const int MaxManifestBytes = 256 * 1024;
        private const int MaxManifestCommands = 50;
        private const int MaxManifestTriggers = 200;
        private const int MaxManifestGroups = 32;
        private const int MaxActionsPerReply = 10;
        private const int MaxActionsPerTrigger = 5;
        private const int MaxRuntimeTextChars = 512;
        private const int MinScheduleSeconds = 30;
        private const int MaxScheduleSeconds = 7 * 86400;
        private const int MaxRuleDelaySeconds = 600;
        private const int MaxPendingRuleRuns = 2000;

        private static readonly string[] RuntimeActionNames = { "message", "toast", "group.add", "group.remove" };
        private static readonly string[] RuntimeEventNames = { "player.connected", "player.disconnected", "player.respawned" };
        private static readonly string[] RuntimeFeatureNames = { "commands", "schedules", "joinRules", "groups", "events" };
        private static readonly HashSet<string> RichTextTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "color", "size", "b", "i" };
        private static readonly System.Text.RegularExpressions.Regex RichTextTag =
            new System.Text.RegularExpressions.Regex(@"</?([a-zA-Z]+)(=[^<>]*)?>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RuntimeIdentifier =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RuntimeCommandName =
            new System.Text.RegularExpressions.Regex(@"^[a-z0-9_]{1,32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private class RuntimeStore
        {
            [JsonProperty("manifestVersion")] public string ManifestVersion;
            [JsonProperty("manifest")] public JObject Manifest;
            // group -> steamIds we added ourselves, only these get removed
            [JsonProperty("managed")] public Dictionary<string, List<string>> Managed = new Dictionary<string, List<string>>();
        }

        private class RuntimeManifest
        {
            [JsonProperty("version")] public int Version;
            [JsonProperty("commands")] public List<ManifestCommandDto> Commands = new List<ManifestCommandDto>();
            [JsonProperty("schedules")] public List<ManifestScheduleDto> Schedules = new List<ManifestScheduleDto>();
            [JsonProperty("rules")] public List<ManifestRuleDto> Rules = new List<ManifestRuleDto>();
            [JsonProperty("events")] public List<string> Events = new List<string>();
            [JsonProperty("groups")] public List<string> Groups = new List<string>();
        }

        private class ManifestCommandDto
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("addon")] public string Addon;
            [JsonProperty("cooldownSeconds")] public int CooldownSeconds;
        }

        private class ManifestScheduleDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("addon")] public string Addon;
            [JsonProperty("everySeconds")] public int EverySeconds;
            [JsonProperty("offsetSeconds")] public int OffsetSeconds;
            [JsonProperty("minOnline")] public int MinOnline;
            [JsonProperty("actions")] public List<JObject> Actions = new List<JObject>();
        }

        private class ManifestRuleDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("addon")] public string Addon;
            [JsonProperty("on")] public string On;
            [JsonProperty("delaySeconds")] public int DelaySeconds;
            [JsonProperty("actions")] public List<JObject> Actions = new List<JObject>();
        }

        private class RuntimeCapabilityDto
        {
            [JsonProperty("version")] public int Version = RuntimeVersion;
            [JsonProperty("manifestVersion")] public string ManifestVersion;
            [JsonProperty("actions")] public string[] Actions = RuntimeActionNames;
            [JsonProperty("events")] public string[] Events = RuntimeEventNames;
            [JsonProperty("features")] public string[] Features = RuntimeFeatureNames;
            [JsonProperty("conflicts")] public List<string> Conflicts = new List<string>();
        }

        private class CommandRequestDto
        {
            [JsonProperty("command")] public string Command;
            [JsonProperty("args")] public List<string> Args;
            [JsonProperty("steamId")] public string SteamId;
            [JsonProperty("playerName", NullValueHandling = NullValueHandling.Ignore)] public string PlayerName;
            [JsonProperty("lang", NullValueHandling = NullValueHandling.Ignore)] public string Lang;
            [JsonProperty("eventId")] public string EventId;
        }

        private class CommandResponseDto
        {
            [JsonProperty("actions")] public List<JObject> Actions;
        }

        private class RuntimeEventDto
        {
            [JsonProperty("eventId")] public string EventId;
            [JsonProperty("type")] public string Type;
            [JsonProperty("steamId")] public string SteamId;
            [JsonProperty("playerName", NullValueHandling = NullValueHandling.Ignore)] public string PlayerName;
            [JsonProperty("at")] public string At;
        }

        private class RuntimeEventsBatch
        {
            [JsonProperty("events")] public List<RuntimeEventDto> Events;
        }

        private class RuntimeWorker : RCoreWorker
        {
            private sealed class ScheduleState
            {
                public ManifestScheduleDto Def;
                public float NextAt;
            }

            private sealed class PendingRuleRun
            {
                public ManifestRuleDto Rule;
                public ulong SteamId;
                public float DueAt;
            }

            private RuntimeStore _store;
            private RuntimeManifest _manifest = new RuntimeManifest();
            private readonly Dictionary<string, ManifestCommandDto> _commands = new Dictionary<string, ManifestCommandDto>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _events = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _groups = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> _conflicts = new List<string>();
            private readonly List<ScheduleState> _schedules = new List<ScheduleState>();
            private readonly List<PendingRuleRun> _pendingRules = new List<PendingRuleRun>();
            private readonly Dictionary<string, float> _cooldowns = new Dictionary<string, float>(StringComparer.Ordinal);
            private readonly List<RuntimeEventDto> _eventBuffer = new List<RuntimeEventDto>();
            private List<RuntimeEventDto> _eventsInFlight;
            private float _lastEventsFlush;
            private bool _storeDirty;

            // 1s tick for schedules, delayed join rules and event flush
            protected override float Interval => 1f;

            public RuntimeCapabilityDto Capability() => new RuntimeCapabilityDto
            {
                ManifestVersion = Store.ManifestVersion,
                Conflicts = new List<string>(_conflicts)
            };

            private RuntimeStore Store
            {
                get
                {
                    if (_store != null) return _store;
                    _store = Instance != null ? Instance.TryLoad<RuntimeStore>(RuntimeFileName, "runtime") : new RuntimeStore();
                    if (_store.Managed == null) _store.Managed = new Dictionary<string, List<string>>();
                    if (_store.Manifest != null)
                    {
                        RuntimeManifest cached;
                        string error;
                        if (TryReadManifest(_store.Manifest, out cached, out error)) Apply(cached);
                        else
                        {
                            WarnThrottled("runtime", $"cached manifest ignored: {error}");
                            _store.Manifest = null;
                            _store.ManifestVersion = null;
                        }
                    }
                    return _store;
                }
            }

            private void Start()
            {
                var unused = Store;
            }

            protected override void Tick()
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                RunDueSchedules(now);
                RunDueRules(now);
                if (_eventBuffer.Count > 0 && now - _lastEventsFlush >= RuntimeEventsFlushSeconds) FlushEvents(now);
                if (_storeDirty) SaveStore();
            }

            protected override void Drain()
            {
                UnregisterAllCommands();
                _schedules.Clear();
                _pendingRules.Clear();
                _eventBuffer.Clear();
                _eventsInFlight = null;
                if (_storeDirty) SaveStore();
            }


            public void ApplyFromBackend(JObject manifestJson, string version)
            {
                if (manifestJson == null || string.IsNullOrEmpty(version)) return;
                RuntimeManifest parsed;
                string error;
                if (!TryReadManifest(manifestJson, out parsed, out error))
                {
                    // keep running the cached one
                    WarnThrottled("runtime", $"manifest {version} rejected: {error}");
                    return;
                }

                var store = Store;
                Apply(parsed);
                store.Manifest = manifestJson;
                store.ManifestVersion = version;
                _storeDirty = true;
                SaveStore();
            }

            private static bool TryReadManifest(JObject json, out RuntimeManifest manifest, out string error)
            {
                manifest = null;
                error = null;
                try
                {
                    if (json.ToString(Formatting.None).Length > MaxManifestBytes) { error = "too large"; return false; }
                    manifest = json.ToObject<RuntimeManifest>();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
                if (manifest == null) { error = "empty"; return false; }
                if (manifest.Version != RuntimeVersion) { error = $"unsupported version {manifest.Version}"; return false; }
                if (manifest.Commands == null) manifest.Commands = new List<ManifestCommandDto>();
                if (manifest.Schedules == null) manifest.Schedules = new List<ManifestScheduleDto>();
                if (manifest.Rules == null) manifest.Rules = new List<ManifestRuleDto>();
                if (manifest.Events == null) manifest.Events = new List<string>();
                if (manifest.Groups == null) manifest.Groups = new List<string>();

                if (manifest.Commands.Count > MaxManifestCommands) { error = "too many commands"; return false; }
                if (manifest.Schedules.Count + manifest.Rules.Count > MaxManifestTriggers) { error = "too many schedules and rules"; return false; }
                if (manifest.Groups.Count > MaxManifestGroups) { error = "too many groups"; return false; }
                foreach (var c in manifest.Commands)
                    if (c == null || string.IsNullOrEmpty(c.Name) || !RuntimeCommandName.IsMatch(c.Name)) { error = "bad command name"; return false; }
                foreach (var g in manifest.Groups)
                    if (string.IsNullOrEmpty(g) || !RuntimeIdentifier.IsMatch(g)) { error = "bad group name"; return false; }
                foreach (var s in manifest.Schedules)
                    if (s == null || s.EverySeconds < MinScheduleSeconds || s.EverySeconds > MaxScheduleSeconds
                        || s.OffsetSeconds < 0 || s.Actions == null || s.Actions.Count == 0 || s.Actions.Count > MaxActionsPerTrigger)
                    { error = "bad schedule"; return false; }
                foreach (var r in manifest.Rules)
                    if (r == null || r.On != "player.connected" || r.DelaySeconds < 0 || r.DelaySeconds > MaxRuleDelaySeconds
                        || r.Actions == null || r.Actions.Count == 0 || r.Actions.Count > MaxActionsPerTrigger)
                    { error = "bad rule"; return false; }
                return true;
            }

            private void Apply(RuntimeManifest next)
            {
                // diff so unchanged commands stay registered
                var wanted = new Dictionary<string, ManifestCommandDto>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in next.Commands) wanted[c.Name] = c;

                var toRemove = new List<string>();
                foreach (var name in _commands.Keys) if (!wanted.ContainsKey(name)) toRemove.Add(name);
                foreach (var name in toRemove) UnregisterCommand(name);

                _conflicts.Clear();
                foreach (var pair in wanted)
                {
                    if (_commands.ContainsKey(pair.Key)) { _commands[pair.Key] = pair.Value; continue; }
                    if (Instance != null && Instance.IsChatCommandTakenByOther(pair.Key)) { _conflicts.Add(pair.Key); continue; }
                    if (RegisterCommand(pair.Key)) _commands[pair.Key] = pair.Value;
                    else _conflicts.Add(pair.Key);
                }

                // unchanged schedules keep their timing
                float now = UnityEngine.Time.realtimeSinceStartup;
                var previous = new Dictionary<string, ScheduleState>(StringComparer.Ordinal);
                foreach (var s in _schedules) if (s.Def?.Id != null) previous[s.Def.Addon + "/" + s.Def.Id] = s;
                _schedules.Clear();
                foreach (var def in next.Schedules)
                {
                    ScheduleState keep;
                    float nextAt = previous.TryGetValue(def.Addon + "/" + def.Id, out keep) && keep.Def.EverySeconds == def.EverySeconds
                        ? keep.NextAt
                        : now + Mathf.Max(1, def.OffsetSeconds > 0 ? def.OffsetSeconds : def.EverySeconds);
                    _schedules.Add(new ScheduleState { Def = def, NextAt = nextAt });
                }

                _events.Clear();
                foreach (var e in next.Events) if (Array.IndexOf(RuntimeEventNames, e) >= 0) _events.Add(e);

                // group removed from manifest -> drop memberships we added
                var nextGroups = new HashSet<string>(next.Groups, StringComparer.Ordinal);
                foreach (var g in new List<string>(_groups))
                    if (!nextGroups.Contains(g)) ReleaseGroup(g);
                _groups.Clear();
                foreach (var g in nextGroups) _groups.Add(g);

                _pendingRules.RemoveAll(p => !next.Rules.Exists(r => r.Id == p.Rule.Id && r.Addon == p.Rule.Addon));
                _manifest = next;

                // report conflicts now, not on the next full sync
                if (_conflicts.Count > 0) Engine?.RequestFullSync(2f);
            }

            // unlink only, not on reload
            public void ResetForUnlink()
            {
                foreach (var g in new List<string>(Store.Managed.Keys)) ReleaseGroup(g);
                UnregisterAllCommands();
                _schedules.Clear();
                _pendingRules.Clear();
                _eventBuffer.Clear();
                _manifest = new RuntimeManifest();
                _store = new RuntimeStore();
                _storeDirty = true;
                SaveStore();
            }

            private void SaveStore()
            {
                if (Instance == null || _store == null) return;
                if (Instance.TrySave(RuntimeFileName, _store, "runtime")) _storeDirty = false;
            }


            private bool RegisterCommand(string name)
            {
                try
                {
                    Instance.cmd.AddChatCommand(name, Instance, (Action<BasePlayer, string, string[]>)OnRuntimeCommand);
                    return true;
                }
                catch (Exception ex)
                {
                    WarnThrottled("runtime", $"could not register /{name}: {ex.Message}");
                    return false;
                }
            }

            private void UnregisterCommand(string name)
            {
                _commands.Remove(name);
                try { Instance?.cmd.RemoveChatCommand(name, Instance); }
                catch (Exception ex) { WarnThrottled("runtime", $"could not unregister /{name}: {ex.Message}"); }
            }

            private void UnregisterAllCommands()
            {
                foreach (var name in new List<string>(_commands.Keys)) UnregisterCommand(name);
            }

            private void OnRuntimeCommand(BasePlayer player, string command, string[] args)
            {
                var plugin = Instance;
                if (plugin == null || player == null || !player.IsConnected || !plugin.IsLinked) return;

                ManifestCommandDto def;
                if (!_commands.TryGetValue(command ?? "", out def)) return;

                float now = UnityEngine.Time.realtimeSinceStartup;
                float cooldown = Mathf.Max(MinCommandCooldownSeconds, def.CooldownSeconds);
                string key = player.UserIDString + "/" + def.Name;
                float last;
                if (_cooldowns.TryGetValue(key, out last) && now - last < cooldown)
                {
                    player.ChatMessage(plugin.Message("Runtime.Cooldown", player, Mathf.CeilToInt(cooldown - (now - last))));
                    return;
                }
                _cooldowns[key] = now;
                if (_cooldowns.Count > 5000) _cooldowns.Clear();

                var argList = new List<string>();
                if (args != null)
                    for (int i = 0; i < args.Length && i < 16; i++)
                        argList.Add(args[i] != null && args[i].Length > 128 ? args[i].Substring(0, 128) : args[i] ?? "");

                var request = new CommandRequestDto
                {
                    Command = def.Name,
                    Args = argList,
                    SteamId = player.UserIDString,
                    PlayerName = player.displayName,
                    Lang = plugin.lang.GetLanguage(player.UserIDString),
                    EventId = NewEventId()
                };

                ulong uid = player.userID;
                // not Send(): it allows one request in flight, commands from many players would get dropped
                plugin.Request(CommandPath, JsonConvert.SerializeObject(request, Formatting.None), RequestMethod.POST,
                    (code, body) =>
                    {
                        if (this == null || Instance == null) return;
                        var self = FindConnectedPlayer(uid.ToString(CultureInfo.InvariantCulture));
                        if (self == null) return;

                        if (code < 200 || code >= 300)
                        {
                            self.ChatMessage(Instance.Message("Runtime.Unavailable", self));
                            return;
                        }

                        CommandResponseDto parsed = null;
                        try { parsed = string.IsNullOrEmpty(body) ? null : JsonConvert.DeserializeObject<CommandResponseDto>(body); }
                        catch (JsonException ex) { WarnThrottled("runtime", $"unreadable command reply: {ex.Message}"); }
                        if (parsed?.Actions == null) return;

                        for (int i = 0; i < parsed.Actions.Count && i < MaxActionsPerReply; i++)
                            Execute(parsed.Actions[i], self);
                    },
                    timeoutSeconds: CommandTimeoutSeconds);
            }


            private void RunDueSchedules(float now)
            {
                for (int i = 0; i < _schedules.Count; i++)
                {
                    var s = _schedules[i];
                    if (now < s.NextAt) continue;
                    s.NextAt = now + s.Def.EverySeconds;
                    if (BasePlayer.activePlayerList.Count < s.Def.MinOnline) continue;
                    foreach (var action in s.Def.Actions) Execute(action, null);
                }
            }

            private void RunDueRules(float now)
            {
                if (_pendingRules.Count == 0) return;
                for (int i = _pendingRules.Count - 1; i >= 0; i--)
                {
                    var run = _pendingRules[i];
                    if (now < run.DueAt) continue;
                    _pendingRules.RemoveAt(i);
                    var player = FindConnectedPlayer(run.SteamId.ToString(CultureInfo.InvariantCulture));
                    if (player == null) continue;
                    foreach (var action in run.Rule.Actions) Execute(action, player);
                }
            }

            public void OnPlayerConnected(BasePlayer player)
            {
                if (player == null || !IsRealSteamId(player.userID)) return;
                float now = UnityEngine.Time.realtimeSinceStartup;
                foreach (var rule in _manifest.Rules)
                {
                    if (rule.On != "player.connected") continue;
                    if (rule.DelaySeconds <= 0) { foreach (var a in rule.Actions) Execute(a, player); continue; }
                    if (_pendingRules.Count >= MaxPendingRuleRuns) break;
                    _pendingRules.Add(new PendingRuleRun { Rule = rule, SteamId = player.userID, DueAt = now + rule.DelaySeconds });
                }
                RecordEvent("player.connected", player);
            }

            public void RecordEvent(string type, BasePlayer player)
            {
                if (player == null || !_events.Contains(type) || !IsRealSteamId(player.userID)) return;
                if (_eventBuffer.Count >= MaxRuntimeEventsBuffered) _eventBuffer.RemoveAt(0);
                _eventBuffer.Add(new RuntimeEventDto
                {
                    EventId = NewEventId(),
                    Type = type,
                    SteamId = player.UserIDString,
                    PlayerName = player.displayName,
                    At = UtcNow()
                });
            }

            private void FlushEvents(float now)
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;
                _lastEventsFlush = now;
                int take = Math.Min(_eventBuffer.Count, MaxRuntimeEventsPerBatch);
                _eventsInFlight = _eventBuffer.GetRange(0, take);
                _eventBuffer.RemoveRange(0, take);

                Send<JObject>(EventsPath, new RuntimeEventsBatch { Events = _eventsInFlight },
                    _ => { _eventsInFlight = null; },
                    (code, body) =>
                    {
                        // requeue once, buffer cap limits memory on long outages
                        if (_eventsInFlight != null) _eventBuffer.InsertRange(0, _eventsInFlight);
                        _eventsInFlight = null;
                        if (_eventBuffer.Count > MaxRuntimeEventsBuffered)
                            _eventBuffer.RemoveRange(0, _eventBuffer.Count - MaxRuntimeEventsBuffered);
                    });
            }


            public void ReconcileGroups(Dictionary<string, List<string>> desired)
            {
                var plugin = Instance;
                if (plugin == null || desired == null) return;
                var store = Store;

                foreach (var pair in desired)
                {
                    string group = pair.Key;
                    if (!_groups.Contains(group) || pair.Value == null) continue;
                    EnsureGroup(group);

                    var want = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var id in pair.Value) if (IsRealSteamId(id)) want.Add(id);

                    List<string> managed;
                    if (!store.Managed.TryGetValue(group, out managed) || managed == null)
                        store.Managed[group] = managed = new List<string>();

                    foreach (var id in want)
                    {
                        if (plugin.permission.UserHasGroup(id, group)) continue;
                        plugin.permission.AddUserGroup(id, group);
                        if (!managed.Contains(id)) managed.Add(id);
                        _storeDirty = true;
                    }

                    // backend only knows online players, keep offline ones
                    for (int i = managed.Count - 1; i >= 0; i--)
                    {
                        string id = managed[i];
                        if (want.Contains(id) || FindConnectedPlayer(id) == null) continue;
                        plugin.permission.RemoveUserGroup(id, group);
                        managed.RemoveAt(i);
                        _storeDirty = true;
                    }
                }
            }

            private void EnsureGroup(string group)
            {
                var plugin = Instance;
                if (plugin == null || plugin.permission.GroupExists(group)) return;
                plugin.permission.CreateGroup(group, group, 0);
            }

            private void ReleaseGroup(string group)
            {
                var plugin = Instance;
                List<string> managed;
                if (plugin == null || !Store.Managed.TryGetValue(group, out managed) || managed == null)
                {
                    Store.Managed.Remove(group);
                    return;
                }
                foreach (var id in managed)
                    if (plugin.permission.UserHasGroup(id, group)) plugin.permission.RemoveUserGroup(id, group);
                Store.Managed.Remove(group);
                _storeDirty = true;
            }


            public TaskResult Execute(JObject action, BasePlayer self)
            {
                var plugin = Instance;
                if (plugin == null || action == null) return TaskResult.Fail("INVALID_ACTION", "empty");
                string type = (string)action["type"];
                try
                {
                    switch (type)
                    {
                        case "message": return plugin.RuntimeSendText(action, self, toast: false);
                        case "toast": return plugin.RuntimeSendText(action, self, toast: true);
                        case "group.add":
                        case "group.remove":
                            return ExecuteGroupAction(type == "group.add", (string)action["steamId"], (string)action["group"]);
                        default:
                            return TaskResult.Fail("UNKNOWN_ACTION", type ?? "(none)");
                    }
                }
                catch (Exception ex)
                {
                    return TaskResult.Fail("INVALID_ACTION", ex.Message);
                }
            }

            private TaskResult ExecuteGroupAction(bool add, string steamId, string group)
            {
                var plugin = Instance;
                if (!IsRealSteamId(steamId)) return TaskResult.Fail("INVALID_ACTION", "steamId");
                if (string.IsNullOrEmpty(group) || !_groups.Contains(group))
                    return TaskResult.Fail("GROUP_NOT_MANAGED", group ?? "(none)");

                List<string> managed;
                if (!Store.Managed.TryGetValue(group, out managed) || managed == null)
                    Store.Managed[group] = managed = new List<string>();

                if (add)
                {
                    EnsureGroup(group);
                    if (!plugin.permission.UserHasGroup(steamId, group))
                    {
                        plugin.permission.AddUserGroup(steamId, group);
                        if (!managed.Contains(steamId)) managed.Add(steamId);
                    }
                }
                else if (managed.Remove(steamId))
                {
                    // only remove what we added
                    plugin.permission.RemoveUserGroup(steamId, group);
                }
                _storeDirty = true;
                return TaskResult.Ok();
            }
        }

        private TaskResult HandleActionTask(JToken raw)
        {
            var runtime = _engine?.Runtime;
            if (runtime == null) return TaskResult.Fail("RUNTIME_UNAVAILABLE", "runtime not started");
            return runtime.Execute(raw as JObject, null);
        }

        // text rendered once per language, not per player
        private TaskResult RuntimeSendText(JObject action, BasePlayer self, bool toast)
        {
            var texts = action["texts"] as JObject;
            if (texts == null || !texts.HasValues) return TaskResult.Fail("INVALID_ACTION", "texts");

            var targets = ResolveRuntimeTargets(action["targets"], self);
            if (targets == null) return TaskResult.Fail("INVALID_ACTION", "targets");
            if (targets.Count == 0) return TaskResult.Ok();

            string defaultLanguage = (string)action["defaultLanguage"];
            string prefix = null;
            ulong avatar = _config.AvatarSteamId;
            int toastStyle = 0;
            var style = action["style"];
            if (!toast && style is JObject)
            {
                string rawPrefix = (string)style["prefix"];
                string color = (string)style["prefixColor"];
                if (!string.IsNullOrEmpty(rawPrefix))
                {
                    prefix = SanitizeRichText(rawPrefix);
                    if (!string.IsNullOrEmpty(color) && color.Length == 7 && color[0] == '#') prefix = $"<color={color}>{prefix}</color>";
                }
                ulong customAvatar;
                if (ulong.TryParse((string)style["avatarSteamId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out customAvatar)
                    && IsRealSteamId(customAvatar))
                    avatar = customAvatar;
            }
            else if (toast && style != null && style.Type == JTokenType.String && (string)style == "error")
                toastStyle = 1;

            var byLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var player in targets)
            {
                string language = lang.GetLanguage(player.UserIDString) ?? "en";
                string template;
                if (!byLanguage.TryGetValue(language, out template))
                {
                    template = PickRuntimeText(texts, language, defaultLanguage);
                    template = template == null ? null : SanitizeRichText(RenderServerPlaceholders(template));
                    byLanguage[language] = template;
                }
                if (string.IsNullOrEmpty(template)) continue;

                string text = template.IndexOf("{player.", StringComparison.Ordinal) >= 0
                    ? template.Replace("{player.name}", SafePlayerName(player.displayName))
                              .Replace("{player.steamId}", player.UserIDString)
                    : template;

                if (toast) player.SendConsoleCommand("gametip.showtoast", toastStyle, text, "");
                else player.SendConsoleCommand("chat.add", 0, avatar, prefix == null ? text : $"{prefix} {text}");
            }
            return TaskResult.Ok();
        }

        private List<BasePlayer> ResolveRuntimeTargets(JToken targets, BasePlayer self)
        {
            var result = new List<BasePlayer>();
            if (targets == null) return null;

            if (targets.Type == JTokenType.Array)
            {
                foreach (var id in targets)
                {
                    var p = FindConnectedPlayer((string)id);
                    if (p != null) result.Add(p);
                }
                return result;
            }
            if (targets.Type != JTokenType.String) return null;

            string spec = (string)targets;
            if (spec == "self")
            {
                if (self != null && self.IsConnected) result.Add(self);
                return result;
            }

            string group = spec.StartsWith("group:", StringComparison.Ordinal) ? spec.Substring(6) : null;
            string perm = spec.StartsWith("perm:", StringComparison.Ordinal) ? spec.Substring(5) : null;
            if (spec != "all" && group == null && perm == null) return null;

            for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
            {
                var p = BasePlayer.activePlayerList[i];
                if (p == null || !p.IsConnected) continue;
                if (group != null && !permission.UserHasGroup(p.UserIDString, group)) continue;
                if (perm != null && !permission.UserHasPermission(p.UserIDString, perm)) continue;
                result.Add(p);
            }
            return result;
        }

        private static string PickRuntimeText(JObject texts, string language, string defaultLanguage)
        {
            string text = (string)texts[language];
            if (text == null && language.Length > 2) text = (string)texts[language.Substring(0, 2)];
            if (text == null && !string.IsNullOrEmpty(defaultLanguage)) text = (string)texts[defaultLanguage];
            if (text == null) text = (string)texts["en"];
            if (text == null)
            {
                foreach (var pair in texts) { text = (string)pair.Value; break; }
            }
            if (text != null && text.Length > MaxRuntimeTextChars) text = text.Substring(0, MaxRuntimeTextChars);
            return text;
        }

        private static string RenderServerPlaceholders(string template)
        {
            if (template.IndexOf("{server.", StringComparison.Ordinal) < 0) return template;
            var queue = ServerMgr.Instance?.connectionQueue;
            return template
                .Replace("{server.name}", SafePlayerName(ConVar.Server.hostname))
                .Replace("{server.online}", BasePlayer.activePlayerList.Count.ToString(CultureInfo.InvariantCulture))
                .Replace("{server.max}", ConVar.Server.maxplayers.ToString(CultureInfo.InvariantCulture))
                .Replace("{server.queue}", (queue?.queue?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        // allow color/size/b/i only
        private static string SanitizeRichText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            return RichTextTag.Replace(text, m => RichTextTags.Contains(m.Groups[1].Value) ? m.Value : "");
        }

        // player names can contain rich text tags
        private static string SafePlayerName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("<", "").Replace(">", "");
        }

        // Oxide keeps chat commands in a private dict. If we can't read it (Carbon) assume free,
        // Oxide logs a warning on replace anyway.
        private bool IsChatCommandTakenByOther(string name)
        {
            try
            {
                var library = cmd;
                if (library == null) return false;
                var field = library.GetType().GetField("chatCommands", BindingFlags.Instance | BindingFlags.NonPublic);
                var dict = field?.GetValue(library) as IDictionary;
                if (dict == null || !dict.Contains(name)) return false;
                var entry = dict[name];
                if (entry == null) return false;
                var ownerMember = entry.GetType().GetField("Plugin") as MemberInfo ?? entry.GetType().GetProperty("Plugin");
                object owner = ownerMember is FieldInfo
                    ? ((FieldInfo)ownerMember).GetValue(entry)
                    : (ownerMember as PropertyInfo)?.GetValue(entry, null);
                return owner != null && !ReferenceEquals(owner, this);
            }
            catch (Exception ex)
            {
                WarnThrottled("runtime", $"command conflict probe unavailable: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Api

        private void Request(string path, string body, RequestMethod method,
                             Action<int, string> onComplete, bool authenticated = true,
                             bool isAutokick = false, int timeoutSeconds = 0)
        {
            var pump = _engine?.Http;
            if (pump == null)
            {
                onComplete?.Invoke(0, null);
                return;
            }

            if (authenticated && !IsLinked)
            {
                onComplete?.Invoke(0, null);
                return;
            }

            var lane = isAutokick ? HttpLane.Auth : InferHttpLane(path);

            pump.Enqueue(
                BuildUrl(path),
                ToHttpVerb(method),
                BuildHeaders(authenticated),
                body,
                onComplete,
                lane,
                timeoutSeconds: timeoutSeconds);
        }

        private static HttpLane InferHttpLane(string path)
        {
            switch (path)
            {
                case PairPath:
                case VerifyPath:
                case HeartbeatPath:
                case QueuePath:
                case AckPath:
                case WipePath:
                case CommandPath:
                    return HttpLane.Control;
                default:
                    return HttpLane.Bulk;
            }
        }

        private static string ToHttpVerb(RequestMethod method)
        {
            switch (method)
            {
                case RequestMethod.GET: return UnityWebRequest.kHttpVerbGET;
                default: return UnityWebRequest.kHttpVerbPOST;
            }
        }

        private string BuildUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) path = "/";
            path = path.Trim();
            if (!path.StartsWith("/")) path = "/" + path;
            return ApiBaseUrl + path;
        }

        private Dictionary<string, string> BuildHeaders(bool authenticated)
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-RCore-Protocol"] = ProtocolVersion.ToString(),
                ["X-RCore-Version"] = Version.ToString()
            };

            if (authenticated) headers["X-API-Key"] = _identity?.ApiKey ?? "";
            return headers;
        }

        // auth lane separate so chat flood can't delay autokick
        private enum HttpLane { Auth, Control, Bulk }

        private class HttpPump : MonoBehaviour
        {
            private class HttpJob
            {
                public string Url;
                public string Method;
                public Dictionary<string, string> Headers;
                public string Body;
                public byte[] BinaryBody;
                public Action<int, string> OnComplete;
                public HttpLane Lane;
                public int TimeoutSeconds;
            }

            private readonly Queue<HttpJob> _authQueue = new Queue<HttpJob>();
            private readonly Queue<HttpJob> _controlQueue = new Queue<HttpJob>();
            private readonly Queue<HttpJob> _bulkQueue = new Queue<HttpJob>();
            private readonly List<HttpJob> _running = new List<HttpJob>();
            private int _active;
            private int _authInFlight;
            private bool _destroyed;

            public void Enqueue(string url, string method, Dictionary<string, string> headers,
                                string body, Action<int, string> onComplete, HttpLane lane,
                                byte[] binaryBody = null, int timeoutSeconds = 0)
            {
                if (_destroyed)
                {
                    onComplete?.Invoke(0, null);
                    return;
                }

                var target = QueueFor(lane);
                int depthLimit = DepthLimitFor(lane);
                if (target.Count >= depthLimit)
                {
                    onComplete?.Invoke(0, null);
                    return;
                }

                target.Enqueue(new HttpJob
                {
                    Url = url,
                    Method = method,
                    Headers = headers,
                    Body = body,
                    BinaryBody = binaryBody,
                    OnComplete = onComplete,
                    Lane = lane,
                    TimeoutSeconds = timeoutSeconds
                });
                Pump();
            }

            private static int DepthLimitFor(HttpLane lane)
            {
                switch (lane)
                {
                    case HttpLane.Auth: return MaxAuthQueueDepth;
                    case HttpLane.Control: return MaxControlQueueDepth;
                    default: return MaxBulkQueueDepth;
                }
            }

            private Queue<HttpJob> QueueFor(HttpLane lane)
            {
                switch (lane)
                {
                    case HttpLane.Auth: return _authQueue;
                    case HttpLane.Control: return _controlQueue;
                    default: return _bulkQueue;
                }
            }

            private void Pump()
            {
                while (_active < MaxHttpConcurrent)
                {
                    HttpJob job = null;

                    if (_authQueue.Count > 0 && _authInFlight < MaxAuthHttpConcurrent)
                        job = _authQueue.Dequeue();
                    else if (_controlQueue.Count > 0)
                        job = _controlQueue.Dequeue();
                    else if (_bulkQueue.Count > 0)
                        job = _bulkQueue.Dequeue();
                    else break;

                    _active++;
                    if (job.Lane == HttpLane.Auth) _authInFlight++;
                    _running.Add(job);
                    StartCoroutine(Execute(job));
                }
            }

            private IEnumerator Execute(HttpJob job)
            {
                int code = 0;
                string text = null;
                UnityWebRequest request = null;

                try
                {
                    int timeout = job.TimeoutSeconds > 0
                        ? job.TimeoutSeconds
                        : Mathf.Max(1, (int)RequestTimeoutSeconds);
                    request = new UnityWebRequest(job.Url, job.Method)
                    {
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = timeout,
                        redirectLimit = 0 // no redirects, keep api key on same host
                    };

                    if (job.BinaryBody != null && job.BinaryBody.Length > 0)
                    {
                        request.uploadHandler = new UploadHandlerRaw(job.BinaryBody);
                    }
                    else if (!string.IsNullOrEmpty(job.Body))
                    {
                        byte[] raw = Encoding.UTF8.GetBytes(job.Body);
                        request.uploadHandler = new UploadHandlerRaw(raw);
                    }

                    if (job.Headers != null)
                    {
                        foreach (var pair in job.Headers)
                        {
                            if (string.IsNullOrEmpty(pair.Key)) continue;
                            request.SetRequestHeader(pair.Key, pair.Value ?? "");
                        }
                    }
                }
                catch (Exception ex)
                {
                    WarnThrottled("http", $"could not build request for {job.Url}: {ex.Message}");
                    request?.Dispose();
                    Finish(job, 0, null);
                    yield break;
                }

                yield return request.SendWebRequest();

                try
                {
                    if (request.result == UnityWebRequest.Result.ConnectionError
                        || request.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        code = 0;
                        text = null;
                    }
                    else
                    {
                        code = (int)request.responseCode;
                        text = request.downloadHandler != null ? request.downloadHandler.text : null;
                    }
                }
                catch (Exception ex)
                {
                    WarnThrottled("http", $"could not read response from {job.Url}: {ex.Message}");
                    code = 0;
                    text = null;
                }
                finally
                {
                    request.Dispose();
                }

                Finish(job, code, text);
            }

            private void Finish(HttpJob job, int code, string text)
            {
                if (_destroyed) return;

                _running.Remove(job);
                _active = Mathf.Max(0, _active - 1);
                if (job.Lane == HttpLane.Auth) _authInFlight = Mathf.Max(0, _authInFlight - 1);

                job.OnComplete?.Invoke(code, text);
                Pump();
            }

            private void OnDestroy()
            {
                _destroyed = true;
                StopAllCoroutines();

                var abandoned = new List<HttpJob>(_running.Count + _authQueue.Count + _controlQueue.Count + _bulkQueue.Count);
                abandoned.AddRange(_running);
                while (_authQueue.Count > 0) abandoned.Add(_authQueue.Dequeue());
                while (_controlQueue.Count > 0) abandoned.Add(_controlQueue.Dequeue());
                while (_bulkQueue.Count > 0) abandoned.Add(_bulkQueue.Dequeue());
                _running.Clear();
                _active = 0;
                _authInFlight = 0;

                foreach (var job in abandoned)
                    job.OnComplete?.Invoke(0, null);
            }
        }

        private void SendWipe()
        {
            if (!IsLinked || !_pendingWipe) return;

            var payload = new WipePayload
            {
                EventId = NewEventId(),
                ServerId = _identity.ServerId,
                CreatedAt = UtcNow(),
                Level = ConVar.Server.level,
                Seed = (uint)ConVar.Server.seed,
                WorldSize = (uint)ConVar.Server.worldsize,
                SaveCreatedTime = SaveCreatedTimeOrNull()
            };

            Request(WipePath, JsonConvert.SerializeObject(payload, Formatting.None), RequestMethod.POST,
                (code, response) =>
                {
                    if (code < 200 || code >= 300) return;
                    SetPendingWipe(false);
                    _engine?.RequestFullSync(0.5f);
                });
        }

        private object EvaluateLoginAutokick(string steamId, string ipAddress)
        {
            if (!IsLinked || !IsRealSteamId(steamId)) return null;

            string ip = CleanIp(ipAddress);
            AutokickCacheEntry cached;
            if (!TryGetAutokickCache(steamId, ip, out cached))
                return null;

            if (cached.Outcome == AutokickOutcome.Deny)
                return AutokickReason(steamId, cached.Reason);

            return null;
        }

        private void ApplyCachedPolicyOrEnqueue(string steamId, string ip)
        {
            AutokickCacheEntry cached;
            if (TryGetAutokickCache(steamId, ip, out cached))
            {
                if (cached.Outcome == AutokickOutcome.Deny)
                    CloseConnection(steamId, AutokickReason(steamId, cached.Reason));
                return;
            }

            EnqueuePolicyCheck(steamId, ip);
        }

        private void EnqueuePolicyCheck(string steamId, string ip)
        {
            if (!IsLinked || !IsRealSteamId(steamId)) return;
            if (string.IsNullOrEmpty(ip)) ip = "0.0.0.0";

            AutokickCacheEntry cached;
            if (TryGetAutokickCache(steamId, ip, out cached))
            {
                if (cached.Outcome == AutokickOutcome.Deny)
                    CloseConnection(steamId, AutokickReason(steamId, cached.Reason));
                return;
            }

            PolicyPending existing;
            if (_policyPending.TryGetValue(steamId, out existing) && existing != null)
            {
                existing.Ip = ip;
                return;
            }

            if (_policyPending.Count >= MaxPolicyPending)
            {
                string oldestKey = null;
                float oldestAt = float.MaxValue;
                foreach (var pair in _policyPending)
                {
                    if (pair.Value.EnqueuedAt < oldestAt)
                    {
                        oldestAt = pair.Value.EnqueuedAt;
                        oldestKey = pair.Key;
                    }
                }
                if (oldestKey != null) _policyPending.Remove(oldestKey);
            }

            _policyPending[steamId] = new PolicyPending
            {
                Ip = ip,
                EnqueuedAt = UnityEngine.Time.realtimeSinceStartup,
                Attempts = 0
            };
        }

        private void FlushPolicyQueue()
        {
            if (_policyInFlight) return;
            if (!IsLinked || _engine?.Http == null) return;
            if (_policyPending.Count == 0) return;

            var batch = new List<AutokickPlayerDto>(Math.Min(MaxPolicyBatch, _policyPending.Count));
            var taken = new List<string>(MaxPolicyBatch);
            foreach (var pair in _policyPending)
            {
                if (batch.Count >= MaxPolicyBatch) break;
                taken.Add(pair.Key);
                batch.Add(new AutokickPlayerDto { SteamId = pair.Key, Ip = pair.Value.Ip });
            }

            if (batch.Count == 0) return;

            for (int i = 0; i < taken.Count; i++)
                _policyPending.Remove(taken[i]);

            _policyInFlight = true;
            int keyGen = _keyGeneration;
            var payload = new AutokickBatchRequest
            {
                ServerId = _identity.ServerId,
                Players = batch
            };

            Request(AutokickCheckPath, JsonConvert.SerializeObject(payload, Formatting.None), RequestMethod.POST,
                (code, response) =>
                {
                    _policyInFlight = false;
                    if (Instance == null) return;
                    if (keyGen != _keyGeneration) return;

                    if (TryParseAutokickBatchResponse(code, response, out AutokickBatchResponse parsed))
                    {
                        _policyFailures = 0;
                        ApplyPolicyDecisions(parsed, batch);
                        return;
                    }

                    _policyFailures = Math.Min(_policyFailures + 1, MaxPolicyAttempts);
                    RequeuePolicyBatch(batch, code);
                }, true, true);
        }

        private void RequeuePolicyBatch(List<AutokickPlayerDto> batch, int code)
        {
            if (batch == null || batch.Count == 0) return;

            bool retry = IsTransientHttpFailure(code) && _policyFailures < MaxPolicyAttempts;
            if (!retry)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var p = batch[i];
                    if (p == null || string.IsNullOrEmpty(p.SteamId)) continue;
                    StoreAutokickCache(p.SteamId, p.Ip ?? "0.0.0.0", AutokickOutcome.Allow, null);
                }
                _policyFailures = 0;
                return;
            }

            float delay = PolicyRetryDelay(_policyFailures);
            timer.Once(delay, () =>
            {
                if (Instance == null || !IsLinked) return;
                for (int i = 0; i < batch.Count; i++)
                {
                    var p = batch[i];
                    if (p == null || string.IsNullOrEmpty(p.SteamId)) continue;
                    PolicyPending existing;
                    if (_policyPending.TryGetValue(p.SteamId, out existing) && existing != null)
                    {
                        existing.Ip = p.Ip ?? existing.Ip;
                        existing.Attempts++;
                        continue;
                    }
                    if (_policyPending.Count >= MaxPolicyPending) break;
                    _policyPending[p.SteamId] = new PolicyPending
                    {
                        Ip = p.Ip ?? "0.0.0.0",
                        EnqueuedAt = UnityEngine.Time.realtimeSinceStartup,
                        Attempts = 1
                    };
                }
            });
        }

        private void ApplyPolicyDecisions(AutokickBatchResponse parsed, List<AutokickPlayerDto> sent)
        {
            var bySteam = new Dictionary<string, AutokickDecisionDto>(StringComparer.Ordinal);
            if (parsed?.Decisions != null)
            {
                for (int i = 0; i < parsed.Decisions.Count; i++)
                {
                    var d = parsed.Decisions[i];
                    if (d == null || string.IsNullOrEmpty(d.SteamId)) continue;
                    bySteam[d.SteamId] = d;
                }
            }

            for (int i = 0; i < sent.Count; i++)
            {
                var p = sent[i];
                if (p == null || string.IsNullOrEmpty(p.SteamId)) continue;

                AutokickDecisionDto d;
                if (!bySteam.TryGetValue(p.SteamId, out d) || d == null)
                {
                    StoreAutokickCache(p.SteamId, p.Ip ?? "0.0.0.0", AutokickOutcome.Allow, null);
                    continue;
                }

                string decidedIp = string.IsNullOrEmpty(d.Ip) ? (p.Ip ?? "0.0.0.0") : d.Ip;
                if (d.Kick)
                {
                    StoreAutokickCache(p.SteamId, decidedIp, AutokickOutcome.Deny, d.Reason);
                    if (CurrentConnectionIp(p.SteamId, out string liveIp)
                        && !string.Equals(liveIp, decidedIp, StringComparison.Ordinal))
                    {
                        EnqueuePolicyCheck(p.SteamId, liveIp);
                        continue;
                    }
                    CloseConnection(p.SteamId, AutokickReason(p.SteamId, d.Reason));
                }
                else
                {
                    StoreAutokickCache(p.SteamId, decidedIp, AutokickOutcome.Allow, null);
                }
            }
        }

        private bool CurrentConnectionIp(string steamId, out string ip)
        {
            ip = null;
            if (!ulong.TryParse(steamId, out ulong uid)) return false;

            var player = BasePlayer.FindByID(uid);
            if (player != null && player.IsConnected && player.net?.connection != null)
            {
                ip = CleanIp(player.net.connection.ipaddress);
                return true;
            }

            Network.Connection found = FindConnection(uid);
            if (found != null)
            {
                ip = CleanIp(found.ipaddress ?? "");
                return true;
            }
            return false;
        }

        private static bool IsTransientHttpFailure(int code) =>
            code == 0 || code == 429 || code >= 500;

        private static float PolicyRetryDelay(int attempt)
        {
            float baseDelay = attempt <= 1 ? 1.5f : attempt == 2 ? 4f : 10f;
            return baseDelay * UnityEngine.Random.Range(0.75f, 1.25f);
        }

        private void ClearAutokickState()
        {
            _autokickCache.Clear();
            _policyPending.Clear();
            _pendingKick.Clear();
            _policyInFlight = false;
            _policyFailures = 0;
        }

        private string AutokickReason(string steamId, string reason)
        {
            return string.IsNullOrEmpty(reason)
                ? lang.GetMessage("Autokick.Default", this, steamId)
                : reason;
        }

        private static bool TryParseAutokickBatchResponse(int code, string response, out AutokickBatchResponse result)
        {
            result = null;
            if (code != 200 || string.IsNullOrEmpty(response)) return false;
            try
            {
                result = JsonConvert.DeserializeObject<AutokickBatchResponse>(response);
            }
            catch (JsonException ex)
            {
                WarnThrottled("autokick", $"unreadable policy response: {ex.Message}");
                return false;
            }
            return result != null && result.Decisions != null;
        }

        private bool TryGetAutokickCache(string steamId, string ip, out AutokickCacheEntry entry)
        {
            entry = null;
            if (!_autokickCache.TryGetValue(steamId, out entry)) return false;
            if (entry == null
                || !string.Equals(entry.Ip, ip, StringComparison.Ordinal)
                || UnityEngine.Time.realtimeSinceStartup - entry.At > AutokickCacheTtl)
            {
                _autokickCache.Remove(steamId);
                entry = null;
                return false;
            }
            return true;
        }

        private void StoreAutokickCache(string steamId, string ip, AutokickOutcome outcome, string reason)
        {
            PruneAutokickCache(force: _autokickCache.Count >= MaxAutokickCacheEntries);
            _autokickCache[steamId] = new AutokickCacheEntry
            {
                Ip = ip,
                Outcome = outcome,
                Reason = reason,
                At = UnityEngine.Time.realtimeSinceStartup
            };
        }

        private void PruneAutokickCache(bool force = false)
        {
            if (!force && _autokickCache.Count < MaxAutokickCacheEntries) return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            var stale = new List<string>();
            foreach (var pair in _autokickCache)
            {
                if (now - pair.Value.At > AutokickCacheTtl) stale.Add(pair.Key);
            }
            foreach (var key in stale) _autokickCache.Remove(key);

            if (_autokickCache.Count <= MaxAutokickCacheEntries) return;

            var ordered = new List<KeyValuePair<string, AutokickCacheEntry>>(_autokickCache);
            ordered.Sort((a, b) => a.Value.At.CompareTo(b.Value.At));
            int remove = _autokickCache.Count - MaxAutokickCacheEntries;
            for (int i = 0; i < remove; i++) _autokickCache.Remove(ordered[i].Key);
        }

        private bool CloseConnection(string steamId, string reason)
        {
            if (!ulong.TryParse(steamId, out ulong targetUserId))
                return false;

            if (string.IsNullOrEmpty(reason))
                reason = lang.GetMessage("Autokick.Default", this, steamId);

            var player = BasePlayer.FindByID(targetUserId);
            if (player != null && player.IsConnected)
            {
                player.Kick(reason);
                return true;
            }

            Network.Connection connection = FindConnection(targetUserId);
            if (connection != null)
            {
                Network.Net.sv.Kick(connection, reason);
                return true;
            }

            _pendingKick[targetUserId] = reason;
            return true;
        }

        private static Network.Connection FindConnection(ulong userid)
        {
            var authList = ConnectionAuth.m_AuthConnection;
            if (authList != null)
            {
                for (int i = 0; i < authList.Count; i++)
                {
                    var c = authList[i];
                    if (c != null && c.userid == userid) return c;
                }
            }

            var queue = ServerMgr.Instance?.connectionQueue;
            if (queue != null)
            {
                if (queue.joining != null)
                {
                    for (int i = 0; i < queue.joining.Count; i++)
                    {
                        var c = queue.joining[i];
                        if (c != null && c.userid == userid) return c;
                    }
                }
                if (queue.queue != null)
                {
                    for (int i = 0; i < queue.queue.Count; i++)
                    {
                        var c = queue.queue[i];
                        if (c != null && c.userid == userid) return c;
                    }
                }
            }

            var steamWaiting = Auth_Steam.waitingList;
            if (steamWaiting != null)
            {
                for (int i = 0; i < steamWaiting.Count; i++)
                {
                    var c = steamWaiting[i];
                    if (c != null && c.userid == userid) return c;
                }
            }

            return null;
        }

        private List<PlayerDto> SnapshotPlayers()
        {
            var players = new List<PlayerDto>(BasePlayer.activePlayerList.Count + 32);
            float now = UnityEngine.Time.realtimeSinceStartup;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;

                string ip = player.net?.connection != null ? CleanIp(player.net.connection.ipaddress) : "0.0.0.0";
                var position = player.transform.position;

                long afkSeconds = 0;
                AfkTracker tracker;
                if (_afkTrackers.TryGetValue(player.userID, out tracker))
                {
                    if (Vector3.Distance(position, tracker.LastPosition) > 1f)
                        _afkTrackers[player.userID] = new AfkTracker { LastPosition = position, LastMoveTime = now };
                    else
                        afkSeconds = (long)(now - tracker.LastMoveTime);
                }
                else
                {
                    _afkTrackers[player.userID] = new AfkTracker { LastPosition = position, LastMoveTime = now };
                }

                players.Add(new PlayerDto
                {
                    SteamId = player.UserIDString,
                    Name = player.displayName ?? "Unknown",
                    Ip = ip,
                    Ping = (Network.Net.sv != null && player.net?.connection != null)
                        ? Network.Net.sv.GetAveragePing(player.net.connection)
                        : 0,
                    ConnectedSeconds = player.net?.connection != null ? (long)player.net.connection.GetSecondsConnected() : 0,
                    Health = player.Health(),
                    IsAdmin = player.IsAdmin,
                    AfkSeconds = afkSeconds,
                    Status = "online"
                });
            }

            var queue = ServerMgr.Instance?.connectionQueue;
            if (queue != null)
            {
                if (queue.joining != null)
                {
                    foreach (var connection in queue.joining)
                    {
                        if (connection == null || connection.userid == 0) continue;
                        players.Add(new PlayerDto
                        {
                            SteamId = connection.userid.ToString(),
                            Name = ConnectionName(connection.username, "Connecting..."),
                            Ip = CleanIp(connection.ipaddress ?? ""),
                            Status = "connecting"
                        });
                    }
                }

                if (queue.queue != null)
                {
                    foreach (var connection in queue.queue)
                    {
                        if (connection == null || connection.userid == 0) continue;
                        players.Add(new PlayerDto
                        {
                            SteamId = connection.userid.ToString(),
                            Name = ConnectionName(connection.username, "Unknown"),
                            Ip = CleanIp(connection.ipaddress ?? ""),
                            Status = "queue"
                        });
                    }
                }
            }

            return players;
        }

        private List<TeamDto> SnapshotTeams()
        {
            var instance = RelationshipManager.ServerInstance;
            if (instance == null || instance.teams == null)
                return new List<TeamDto>();

            var result = new List<TeamDto>(instance.teams.Count);
            foreach (var pair in instance.teams)
            {
                var team = pair.Value;
                if (team == null || team.members == null || team.members.Count == 0)
                    continue;

                var members = new List<string>(team.members.Count);
                for (int i = 0; i < team.members.Count; i++)
                {
                    ulong id = team.members[i];
                    if (!IsRealSteamId(id)) continue;
                    members.Add(id.ToString());
                }
                if (members.Count == 0) continue;

                result.Add(new TeamDto
                {
                    TeamId = team.teamID.ToString(),
                    LeaderSteamId = IsRealSteamId(team.teamLeader) ? team.teamLeader.ToString() : null,
                    Members = members
                });
            }
            return result;
        }

        private ServerInfoDto SnapshotServer()
        {
            var queue = ServerMgr.Instance?.connectionQueue;

            return new ServerInfoDto
            {
                Hostname = ConVar.Server.hostname,
                MaxPlayers = ConVar.Server.maxplayers,
                Level = ConVar.Server.level,
                Seed = (uint)ConVar.Server.seed,
                WorldSize = (uint)ConVar.Server.worldsize,
                Port = ConVar.Server.port,
                DisplayIp = ResolveDisplayIp(),
                UptimeSeconds = (long)UnityEngine.Time.realtimeSinceStartup,
                MemoryUsedMb = (long)Performance.current.memoryUsageSystem,
                QueuedCount = queue?.queue?.Count ?? 0,
                JoiningCount = queue?.joining?.Count ?? 0,
                SaveCreatedTime = SaveCreatedTimeOrNull(),
                PluginVersion = Version.ToString(),
                ProtocolVersion = ProtocolVersion,
                Framework = Framework,
                RustProtocol = Rust.Protocol.printable,
                HeaderImage = ConVar.Server.headerimage,
                LogoImage = ConVar.Server.logoimage,
                LevelUrl = string.IsNullOrWhiteSpace(ConVar.Server.levelurl) ? null : ConVar.Server.levelurl.Trim()
            };
        }

        public bool RCore_IsLinked() => IsLinked;

        public string RCore_GetServerId() => _identity?.ServerId;

        public string RCore_GetProjectId() => _identity?.ProjectId;

        public bool RCore_IsMuted(string steamId)
        {
            string reason, timeLeft;
            return IsMuted(steamId, out reason, out timeLeft);
        }

        public string RCore_GetMuteReason(string steamId)
        {
            if (!RCore_IsMuted(steamId)) return null;
            return _mutes.ActiveMutes[steamId].Reason;
        }

        public void RCore_Mute(string steamId, string reason, string expiresAt)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            ApplyMute(steamId, string.IsNullOrEmpty(reason) ? "Rule Violation" : reason, expiresAt, null);
        }

        public void RCore_Unmute(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            ClearMute(steamId, false);
        }

        public void RCore_RaiseAlert(string type, string message)
        {
            if (string.IsNullOrEmpty(type)) return;
            _engine?.RaiseAlert(type, message);
        }

        #endregion

        #region Models

        private enum SpoolKind { Chat = 0, Reports, Deaths, Alerts, Disconnects, TeamChanges }
        private const int SpoolKindCount = 6;

        private class IdentityData
        {
            public string InstalledAtUtc;
            public string ApiKey;
            public string ServerId;
            public string ProjectId;
            public string PairedAtUtc;
            public string LastError;
            public bool PendingWipe;
        }

        private class MuteInfo
        {
            [JsonProperty("Reason")] public string Reason { get; set; }
            [JsonProperty("ExpiresAt")] public string ExpiresAt { get; set; }
            [JsonIgnore] public DateTimeOffset? ExpiresAtParsed;
        }

        private class MuteStore
        {
            [JsonProperty("ActiveMutes")]
            public Dictionary<string, MuteInfo> ActiveMutes { get; set; }
                = new Dictionary<string, MuteInfo>();
        }

        private class ProcessedTask
        {
            public long ExpiresAt;
            public TaskResult Result;
        }

        private class ProcessedTaskStore
        {
            public Dictionary<string, ProcessedTask> Processed = new Dictionary<string, ProcessedTask>();
            public Dictionary<string, TaskResult> PendingAck = new Dictionary<string, TaskResult>();
        }

        private class SpoolStore
        {
            [JsonIgnore] public List<JToken>[] Pending = AllocBuckets();
            [JsonIgnore] public List<JToken>[] InFlight = AllocBuckets();

            [JsonProperty("chat")]
            public List<JToken> Chat { get => Pending[0]; set => Pending[0] = value ?? new List<JToken>(); }
            [JsonProperty("reports")]
            public List<JToken> Reports { get => Pending[1]; set => Pending[1] = value ?? new List<JToken>(); }
            [JsonProperty("deaths")]
            public List<JToken> Deaths { get => Pending[2]; set => Pending[2] = value ?? new List<JToken>(); }
            [JsonProperty("alerts")]
            public List<JToken> Alerts { get => Pending[3]; set => Pending[3] = value ?? new List<JToken>(); }
            [JsonProperty("disconnects")]
            public List<JToken> Disconnects { get => Pending[4]; set => Pending[4] = value ?? new List<JToken>(); }
            [JsonProperty("teamChanges")]
            public List<JToken> TeamChanges { get => Pending[5]; set => Pending[5] = value ?? new List<JToken>(); }

            [JsonProperty("chatInFlight")]
            public List<JToken> ChatInFlight { get => InFlight[0]; set => InFlight[0] = value ?? new List<JToken>(); }
            [JsonProperty("reportsInFlight")]
            public List<JToken> ReportsInFlight { get => InFlight[1]; set => InFlight[1] = value ?? new List<JToken>(); }
            [JsonProperty("deathsInFlight")]
            public List<JToken> DeathsInFlight { get => InFlight[2]; set => InFlight[2] = value ?? new List<JToken>(); }
            [JsonProperty("alertsInFlight")]
            public List<JToken> AlertsInFlight { get => InFlight[3]; set => InFlight[3] = value ?? new List<JToken>(); }
            [JsonProperty("disconnectsInFlight")]
            public List<JToken> DisconnectsInFlight { get => InFlight[4]; set => InFlight[4] = value ?? new List<JToken>(); }
            [JsonProperty("teamChangesInFlight")]
            public List<JToken> TeamChangesInFlight { get => InFlight[5]; set => InFlight[5] = value ?? new List<JToken>(); }

            private static List<JToken>[] AllocBuckets()
            {
                var arr = new List<JToken>[SpoolKindCount];
                for (int i = 0; i < SpoolKindCount; i++) arr[i] = new List<JToken>();
                return arr;
            }

            private void EnsureBuckets()
            {
                if (Pending == null || Pending.Length != SpoolKindCount) Pending = AllocBuckets();
                if (InFlight == null || InFlight.Length != SpoolKindCount) InFlight = AllocBuckets();
                for (int i = 0; i < SpoolKindCount; i++)
                {
                    if (Pending[i] == null) Pending[i] = new List<JToken>();
                    if (InFlight[i] == null) InFlight[i] = new List<JToken>();
                }
            }

            public void Normalize()
            {
                EnsureBuckets();
                for (int i = 0; i < SpoolKindCount; i++)
                {
                    Pending[i] = ClampPending(Pending[i]);
                    InFlight[i] = EnsureList(InFlight[i]);
                }
            }

            public bool ReclaimInFlight()
            {
                EnsureBuckets();
                bool dirty = false;
                for (int i = 0; i < SpoolKindCount; i++)
                    dirty |= ReclaimOne(Pending[i], InFlight[i]);
                if (dirty) Normalize();
                else
                {
                    for (int i = 0; i < SpoolKindCount; i++)
                        Pending[i] = ClampPending(Pending[i]);
                }
                return dirty;
            }

            private static bool ReclaimOne(List<JToken> pending, List<JToken> inFlight)
            {
                if (pending == null || inFlight == null || inFlight.Count == 0) return false;
                pending.InsertRange(0, inFlight);
                inFlight.Clear();
                return true;
            }

            private static List<JToken> EnsureList(List<JToken> source) =>
                source ?? new List<JToken>();

            private static List<JToken> ClampPending(List<JToken> source)
            {
                source = EnsureList(source);
                if (source.Count <= MaxBufferItems) return source;
                source.RemoveRange(0, source.Count - MaxBufferItems);
                return source;
            }
        }

        private readonly struct HitRecord
        {
            public readonly string InitiatorSteamId;
            public readonly string InitiatorName;
            public readonly string Weapon;
            public readonly float Distance;
            public readonly bool IsHeadshot;
            public readonly string BodyPart;
            public readonly float RecordedAt;

            public HitRecord(string initiatorSteamId, string initiatorName, string weapon,
                             float distance, bool isHeadshot, string bodyPart, float recordedAt)
            {
                InitiatorSteamId = initiatorSteamId;
                InitiatorName = initiatorName;
                Weapon = weapon;
                Distance = distance;
                IsHeadshot = isHeadshot;
                BodyPart = bodyPart;
                RecordedAt = recordedAt;
            }
        }

        private struct AfkTracker
        {
            public Vector3 LastPosition;
            public float LastMoveTime;
        }

        private class TaskResult
        {
            [JsonProperty("ok")] public bool Success { get; set; }
            [JsonProperty("code")] public string Code { get; set; }
            [JsonProperty("message")] public string Message { get; set; }

            [JsonProperty("replayed", NullValueHandling = NullValueHandling.Ignore)] public bool? Replayed { get; set; }

            public static TaskResult Ok() => new TaskResult { Success = true, Code = "OK" };

            public static TaskResult Fail(string code, string message) =>
                new TaskResult { Success = false, Code = code, Message = message };

            public TaskResult AsReplay() =>
                new TaskResult { Success = Success, Code = Code, Message = Message, Replayed = true };
        }

        private class BatchPayload<T>
        {
            [JsonProperty("items")] public List<T> Items { get; set; }
        }

        private class PairRequest
        {
            [JsonProperty("code")] public string Code { get; set; }
            [JsonProperty("serverName")] public string ServerName { get; set; }
            [JsonProperty("port")] public int Port { get; set; }
            [JsonProperty("displayIp")] public string DisplayIp { get; set; }
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("protocolVersion")] public int ProtocolVersion { get; set; }
            [JsonProperty("framework")] public string Framework { get; set; }

            [JsonProperty("currentServerId", NullValueHandling = NullValueHandling.Ignore)]
            public string CurrentServerId { get; set; }
            [JsonProperty("headerImage", NullValueHandling = NullValueHandling.Ignore)]
            public string HeaderImage { get; set; }
            [JsonProperty("logoImage", NullValueHandling = NullValueHandling.Ignore)]
            public string LogoImage { get; set; }
        }

        private class PairResponse
        {
            [JsonProperty("success")] public bool Success { get; set; }
            [JsonProperty("data")] public PairData Data { get; set; }
        }

        private class PairData
        {
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("projectId")] public string ProjectId { get; set; }
            [JsonProperty("apiKey")] public string ApiKey { get; set; }
        }

        private class VerifyResponse
        {
            [JsonProperty("ok")] public bool Ok { get; set; }
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("projectId")] public string ProjectId { get; set; }
        }

        private class HeartbeatPayload
        {
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("isFullSync")] public bool IsFullSync { get; set; }
            [JsonProperty("timestamp")] public string Timestamp { get; set; }
            [JsonProperty("fps")] public int Fps { get; set; }
            [JsonProperty("entities")] public int Entities { get; set; }
            [JsonProperty("players", NullValueHandling = NullValueHandling.Ignore)] public List<PlayerDto> Players { get; set; }
            [JsonProperty("server", NullValueHandling = NullValueHandling.Ignore)] public ServerInfoDto Server { get; set; }
            [JsonProperty("alerts", NullValueHandling = NullValueHandling.Ignore)] public List<AlertDto> Alerts { get; set; }
            [JsonProperty("disconnects", NullValueHandling = NullValueHandling.Ignore)] public List<DisconnectDto> Disconnects { get; set; }
            [JsonProperty("teams", NullValueHandling = NullValueHandling.Ignore)] public List<TeamDto> Teams { get; set; }
            [JsonProperty("teamChanges", NullValueHandling = NullValueHandling.Ignore)] public List<TeamChangeDto> TeamChanges { get; set; }
            [JsonProperty("runtime", NullValueHandling = NullValueHandling.Ignore)] public RuntimeCapabilityDto Runtime { get; set; }
        }

        private class DisconnectDto
        {
            [JsonProperty("eventId", NullValueHandling = NullValueHandling.Ignore)] public string EventId { get; set; }
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
        }

        private class TeamDto
        {
            [JsonProperty("teamId")] public string TeamId { get; set; }
            [JsonProperty("leaderSteamId")] public string LeaderSteamId { get; set; }
            [JsonProperty("members")] public List<string> Members { get; set; }
        }

        private class TeamChangeDto
        {
            [JsonProperty("eventId", NullValueHandling = NullValueHandling.Ignore)] public string EventId { get; set; }
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("teamId")] public string TeamId { get; set; }
            [JsonProperty("actorSteamId")] public string ActorSteamId { get; set; }
            [JsonProperty("targetSteamId")] public string TargetSteamId { get; set; }
        }

        private class HeartbeatResponse
        {
            [JsonProperty("ok")] public bool Ok { get; set; }
            [JsonProperty("tasks")] public List<QueueTaskDto> Tasks { get; set; }
            [JsonProperty("mutes")] public List<MuteSyncDto> Mutes { get; set; }
            [JsonProperty("mapNeeded")] public bool MapNeeded { get; set; }
            [JsonProperty("manifest")] public JObject Manifest { get; set; }
            [JsonProperty("manifestVersion")] public string ManifestVersion { get; set; }
            [JsonProperty("groups")] public Dictionary<string, List<string>> Groups { get; set; }
        }

        private class MuteSyncDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
            [JsonProperty("expiresAt")] public string ExpiresAt { get; set; }
        }

        private class DrawingClearTaskDto
        {
            [JsonProperty("entityNetId")] public string EntityNetId { get; set; }
            [JsonProperty("drawingId")] public string DrawingId { get; set; }
        }

        private class NamedContentClearTaskDto
        {
            [JsonProperty("entityNetId")] public string EntityNetId { get; set; }
            [JsonProperty("namedContentId")] public string NamedContentId { get; set; }
        }

        private class AudioClearTaskDto
        {
            [JsonProperty("entityNetId")] public string EntityNetId { get; set; }
            [JsonProperty("audioId")] public string AudioId { get; set; }
        }

        private class ServerInfoDto
        {
            [JsonProperty("hostname")] public string Hostname { get; set; }
            [JsonProperty("maxPlayers")] public int MaxPlayers { get; set; }
            [JsonProperty("level")] public string Level { get; set; }
            [JsonProperty("seed")] public uint Seed { get; set; }
            [JsonProperty("worldSize")] public uint WorldSize { get; set; }
            [JsonProperty("port")] public int Port { get; set; }
            [JsonProperty("displayIp")] public string DisplayIp { get; set; }
            [JsonProperty("uptimeSeconds")] public long UptimeSeconds { get; set; }
            [JsonProperty("memoryUsedMb")] public long MemoryUsedMb { get; set; }
            [JsonProperty("queuedCount")] public int QueuedCount { get; set; }
            [JsonProperty("joiningCount")] public int JoiningCount { get; set; }
            [JsonProperty("saveCreatedTime")] public string SaveCreatedTime { get; set; }
            [JsonProperty("pluginVersion")] public string PluginVersion { get; set; }
            [JsonProperty("protocolVersion")] public int ProtocolVersion { get; set; }
            [JsonProperty("framework")] public string Framework { get; set; }
            [JsonProperty("rustProtocol")] public string RustProtocol { get; set; }
            [JsonProperty("headerImage")] public string HeaderImage { get; set; }
            [JsonProperty("logoImage")] public string LogoImage { get; set; }
            [JsonProperty("levelUrl", NullValueHandling = NullValueHandling.Ignore)] public string LevelUrl { get; set; }
        }

        private class PlayerDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("ip")] public string Ip { get; set; }
            [JsonProperty("ping")] public int Ping { get; set; }
            [JsonProperty("connectedSeconds")] public long ConnectedSeconds { get; set; }
            [JsonProperty("health")] public float Health { get; set; }
            [JsonProperty("isAdmin")] public bool IsAdmin { get; set; }
            [JsonProperty("afkSeconds")] public long AfkSeconds { get; set; }
            [JsonProperty("status")] public string Status { get; set; }
        }

        private class ChatItemDto
        {
            [JsonProperty("eventId")] public string EventId { get; set; }
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
            [JsonProperty("channel")] public string Channel { get; set; }
            [JsonProperty("teamId")] public string TeamId { get; set; }
            [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        }

        private class ReportItemDto
        {
            [JsonProperty("eventId")] public string EventId { get; set; }
            [JsonProperty("reporterSteamId")] public string ReporterSteamId { get; set; }
            [JsonProperty("reporterName")] public string ReporterName { get; set; }
            [JsonProperty("targetSteamId")] public string TargetSteamId { get; set; }
            [JsonProperty("targetName")] public string TargetName { get; set; }
            [JsonProperty("subject")] public string Subject { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        }

        private class DeathEventDto
        {
            [JsonProperty("eventId")] public string EventId { get; set; }
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("createdAt")] public string CreatedAt { get; set; }
            [JsonProperty("deathType")] public string DeathType { get; set; }
            [JsonProperty("damageType")] public string DamageType { get; set; }

            [JsonProperty("victimSteamId")] public string VictimSteamId { get; set; }
            [JsonProperty("victimName")] public string VictimName { get; set; }
            [JsonProperty("victimPos")] public Vec3Dto VictimPos { get; set; }
            [JsonProperty("victimSleeping")] public bool VictimSleeping { get; set; }
            [JsonProperty("victimWounded")] public bool VictimWounded { get; set; }

            [JsonProperty("killerSteamId", NullValueHandling = NullValueHandling.Ignore)] public string KillerSteamId { get; set; }
            [JsonProperty("killerName", NullValueHandling = NullValueHandling.Ignore)] public string KillerName { get; set; }
            [JsonProperty("killerPrefab", NullValueHandling = NullValueHandling.Ignore)] public string KillerPrefab { get; set; }
            [JsonProperty("killerPos", NullValueHandling = NullValueHandling.Ignore)] public Vec3Dto KillerPos { get; set; }

            [JsonProperty("weapon")] public string Weapon { get; set; }
            [JsonProperty("distance")] public float Distance { get; set; }
            [JsonProperty("isHeadshot")] public bool IsHeadshot { get; set; }
            [JsonProperty("bodyPart")] public string BodyPart { get; set; }

            [JsonProperty("combatLog", NullValueHandling = NullValueHandling.Ignore)]
            public List<CombatLogEventDto> CombatLog { get; set; }

            [JsonProperty("raw", NullValueHandling = NullValueHandling.Ignore)] public JToken Raw { get; set; }
        }

        private class CombatLogEventDto
        {
            [JsonProperty("time")] public float Time { get; set; }
            [JsonProperty("attackerSteamId", NullValueHandling = NullValueHandling.Ignore)] public string AttackerSteamId { get; set; }
            [JsonProperty("targetSteamId", NullValueHandling = NullValueHandling.Ignore)] public string TargetSteamId { get; set; }
            [JsonProperty("attacker")] public string Attacker { get; set; }
            [JsonProperty("target")] public string Target { get; set; }
            [JsonProperty("weapon")] public string Weapon { get; set; }
            [JsonProperty("ammo")] public string Ammo { get; set; }
            [JsonProperty("bone")] public string Bone { get; set; }
            [JsonProperty("distance")] public float Distance { get; set; }
            [JsonProperty("hpOld")] public float HpOld { get; set; }
            [JsonProperty("hpNew")] public float HpNew { get; set; }
            [JsonProperty("info")] public string Info { get; set; }
            [JsonProperty("projHits")] public int ProjHits { get; set; }
            [JsonProperty("projIntegrity")] public float ProjIntegrity { get; set; }
            [JsonProperty("projTravel")] public float ProjTravel { get; set; }
            [JsonProperty("projMismatch")] public float ProjMismatch { get; set; }
            [JsonProperty("desync")] public int Desync { get; set; }
            [JsonProperty("attackerDead")] public bool AttackerDead { get; set; }
        }

        private class Vec3Dto
        {
            [JsonProperty("x")] public float X { get; set; }
            [JsonProperty("y")] public float Y { get; set; }
            [JsonProperty("z")] public float Z { get; set; }

            public static Vec3Dto From(Vector3 value) =>
                new Vec3Dto { X = (float)Math.Round(value.x, 2), Y = (float)Math.Round(value.y, 2), Z = (float)Math.Round(value.z, 2) };
        }

        private class AlertDto
        {
            [JsonProperty("eventId")] public string EventId { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
            [JsonProperty("createdAt")] public string CreatedAt { get; set; }
            [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public JToken Data { get; set; }
        }

        private class WipePayload
        {
            [JsonProperty("eventId")] public string EventId { get; set; }
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("createdAt")] public string CreatedAt { get; set; }
            [JsonProperty("level")] public string Level { get; set; }
            [JsonProperty("seed")] public uint Seed { get; set; }
            [JsonProperty("worldSize")] public uint WorldSize { get; set; }
            [JsonProperty("saveCreatedTime")] public string SaveCreatedTime { get; set; }
        }

        private class AutokickPlayerDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("ip")] public string Ip { get; set; }
        }

        private class AutokickBatchRequest
        {
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("players")] public List<AutokickPlayerDto> Players { get; set; }
        }

        private class AutokickDecisionDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("ip")] public string Ip { get; set; }
            [JsonProperty("kick")] public bool Kick { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
        }

        private class AutokickBatchResponse
        {
            [JsonProperty("decisions")] public List<AutokickDecisionDto> Decisions { get; set; }
        }

        private enum AutokickOutcome { Allow, Deny }

        private class AutokickCacheEntry
        {
            public string Ip;
            public AutokickOutcome Outcome;
            public string Reason;
            public float At;
        }

        private class PolicyPending
        {
            public string Ip;
            public float EnqueuedAt;
            public int Attempts;
        }

        private class AckPayload
        {
            [JsonProperty("results")] public Dictionary<string, TaskResult> Results { get; set; }
        }

        private class QueueTaskDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("data")] public JToken Data { get; set; }
        }

        private class ChatTaskDto
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
            [JsonProperty("content")] public string Content { get; set; }
            [JsonProperty("channel")] public string Channel { get; set; }
            [JsonProperty("targetSteamId")] public string TargetSteamId { get; set; }
        }

        private class CommandTaskDto
        {
            [JsonProperty("command")] public string Command { get; set; }
        }

        private class TargetTaskDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("playerName")] public string PlayerName { get; set; }
            [JsonProperty("announce")] public bool Announce { get; set; }
        }

        private class BanTaskDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("playerName")] public string PlayerName { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
            [JsonProperty("expiresAt")] public string ExpiresAt { get; set; }
            [JsonProperty("durationSeconds")] public int? DurationSeconds { get; set; }
            [JsonProperty("banIp")] public bool BanIp { get; set; }
            [JsonProperty("ip")] public string Ip { get; set; }
            [JsonProperty("announce")] public bool Announce { get; set; }
        }

        private class MuteTaskDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("playerName")] public string PlayerName { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
            [JsonProperty("expiresAt")] public string ExpiresAt { get; set; }
            [JsonProperty("durationSeconds")] public int? DurationSeconds { get; set; }
            [JsonProperty("announce")] public bool Announce { get; set; }
        }

        private class KickTaskDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
        }

        private class TeleportTaskDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("targetSteamId")] public string TargetSteamId { get; set; }
            [JsonProperty("x")] public float? X { get; set; }
            [JsonProperty("y")] public float? Y { get; set; }
            [JsonProperty("z")] public float? Z { get; set; }
        }

        private class GiveTaskDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("item")] public string Item { get; set; }
            [JsonProperty("amount")] public int Amount { get; set; }
            [JsonProperty("skinId")] public ulong SkinId { get; set; }
        }

        private class EventBuffer<T>
        {
            private readonly LinkedList<T> _items = new LinkedList<T>();
            private readonly string _label;
            private readonly Action<T> _durableSpill;
            private int _spilled;
            private float _lastSpillWarn = -999f;

            public EventBuffer(string label, Action<T> durableSpill = null)
            {
                _label = label;
                _durableSpill = durableSpill;
            }

            public int Count => _items.Count;

            public void Add(T item)
            {
                if (item == null) return;
                if (_items.Count >= MaxBufferItems)
                    EvictOldest();
                _items.AddLast(item);
            }

            public void AddRange(IEnumerable<T> items)
            {
                if (items == null) return;
                foreach (var item in items) Add(item);
            }

            public List<T> Take(int max)
            {
                int count = Math.Min(_items.Count, max);
                var batch = new List<T>(count);
                for (int i = 0; i < count; i++)
                {
                    batch.Add(_items.First.Value);
                    _items.RemoveFirst();
                }
                return batch;
            }

            public void Requeue(List<T> batch)
            {
                if (batch == null || batch.Count == 0) return;
                for (int i = batch.Count - 1; i >= 0; i--)
                {
                    if (batch[i] == null) continue;
                    _items.AddFirst(batch[i]);
                }
                while (_items.Count > MaxBufferItems)
                    EvictOldest();
            }

            public List<T> DrainAll()
            {
                var all = new List<T>(_items.Count);
                while (_items.Count > 0)
                {
                    all.Add(_items.First.Value);
                    _items.RemoveFirst();
                }
                return all;
            }

            private void EvictOldest()
            {
                if (_items.Count == 0) return;
                var oldest = _items.First.Value;
                _items.RemoveFirst();

                _durableSpill?.Invoke(oldest);

                _spilled++;
                NoteSpill();
            }

            private void NoteSpill()
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastSpillWarn < DropWarnInterval) return;
                _lastSpillWarn = now;
                string mode = _durableSpill != null ? "spilled to disk" : "dropped";
                Instance?._engine?.RaiseAlert("buffer_overflow", $"{_label} {_spilled} events {mode}",
                    new { buffer = _label, spilled = _spilled, durable = _durableSpill != null });
            }
        }

        #endregion

        #region Utils

        private static bool IsRealSteamId(ulong id) => id >= SteamIdBase;

        private static bool IsRealSteamId(string id)
        {
            return ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong steamId)
                && IsRealSteamId(steamId);
        }

        private static string NewEventId() => Guid.NewGuid().ToString("N");

        private static string UtcNow() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        internal static string SaveCreatedTimeKey()
        {
            DateTime t = SaveRestore.SaveCreatedTime.ToUniversalTime();
            if (t.Year <= 2000) return "";
            return t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static string SaveCreatedTimeOrNull()
        {
            string key = SaveCreatedTimeKey();
            return key.Length == 0 ? null : key;
        }

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static void Announce(string message) => Instance?.Puts(message);

        private static bool TryReadErrorCode(string body, out string code, out string message)
        {
            code = null;
            message = null;
            if (string.IsNullOrEmpty(body)) return false;

            JObject parsed;
            try { parsed = JObject.Parse(body); }
            catch (JsonException) { return false; }

            code = parsed["code"]?.Type == JTokenType.String ? (string)parsed["code"] : null;
            message = parsed["message"]?.Type == JTokenType.String ? (string)parsed["message"] : null;
            return !string.IsNullOrEmpty(code);
        }

        private static string ConnectionName(string username, string fallback) =>
            string.IsNullOrEmpty(username) || username == "<blank>" ? fallback : username;

        private static T Parse<T>(JToken raw) where T : class
        {
            if (raw == null) return null;
            try
            {
                return raw.ToObject<T>();
            }
            catch (JsonException ex)
            {
                WarnThrottled("tasks", $"unreadable {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static readonly Dictionary<string, float> WarnedAt = new Dictionary<string, float>();

        private static void WarnThrottled(string key, string message)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            float last;
            if (WarnedAt.TryGetValue(key, out last) && now - last < WarnInterval) return;
            WarnedAt[key] = now;
            Instance?.PrintWarning($"[{key}] {message}");
        }

        private static string CleanIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "0.0.0.0";
            int index = ip.LastIndexOf(':');
            return index != -1 ? ip.Substring(0, index) : ip;
        }

        private static string WeaponName(HitInfo info)
        {
            if (info == null) return "unknown";
            return info.Weapon?.ShortPrefabName ?? info.WeaponPrefab?.ShortPrefabName ?? "unknown";
        }

        private static string BodyPart(HitInfo info)
        {
            if (info == null) return "unknown";
            return info.boneArea.ToString();
        }

        private static string MajorityDamage(HitInfo info)
        {
            if (info?.damageTypes == null) return "Unknown";
            return info.damageTypes.GetMajorityDamageType().ToString();
        }

        private static bool IsEnvironmentDamage(Rust.DamageType type)
        {
            switch (type)
            {
                case Rust.DamageType.Fall:
                case Rust.DamageType.Hunger:
                case Rust.DamageType.Thirst:
                case Rust.DamageType.Cold:
                case Rust.DamageType.Drowned:
                case Rust.DamageType.Heat:
                case Rust.DamageType.Bleeding:
                case Rust.DamageType.Poison:
                case Rust.DamageType.Radiation:
                case Rust.DamageType.RadiationExposure:
                case Rust.DamageType.ElectricShock:
                    return true;
                default:
                    return false;
            }
        }

        private static string ResolveCombatSteamId(ulong netId, Dictionary<ulong, string> idCache)
        {
            string cached;
            if (idCache.TryGetValue(netId, out cached)) return cached;
            var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(netId)) as BasePlayer;
            string sid = entity != null && IsRealSteamId(entity.userID) ? entity.UserIDString : "";
            idCache[netId] = sid;
            return sid;
        }

        private static string NormalizeCombatWeapon(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || fullName == "N/A") return fullName ?? "";
            const string suffix = ".entity.prefab";
            if (!fullName.EndsWith(suffix, StringComparison.Ordinal)) return fullName;
            int lastSlash = fullName.LastIndexOf('/');
            int start = lastSlash + 1;
            int len = fullName.Length - start - suffix.Length;
            return len > 0 ? fullName.Substring(start, len) : fullName;
        }

        private static CombatLogEventDto ToCombatLogDto(float killTime, CombatLog.Event ev, Dictionary<ulong, string> idCache)
        {
            string attackerSid = null;
            string targetSid = null;
            if (ev.attacker == "player" || ev.attacker == "you")
                attackerSid = ResolveCombatSteamId(ev.attacker_id, idCache);
            if (ev.target == "player" || ev.target == "you")
                targetSid = ResolveCombatSteamId(ev.target_id, idCache);

            return new CombatLogEventDto
            {
                Time = killTime - ev.time,
                AttackerSteamId = string.IsNullOrEmpty(attackerSid) ? null : attackerSid,
                TargetSteamId = string.IsNullOrEmpty(targetSid) ? null : targetSid,
                Attacker = ev.attacker,
                Target = ev.target,
                Weapon = NormalizeCombatWeapon(ev.weapon),
                Ammo = ev.ammo ?? "",
                Bone = ev.bone ?? "",
                Distance = (float)Math.Round(ev.distance, 2),
                HpOld = (float)Math.Round(ev.health_old, 2),
                HpNew = (float)Math.Round(ev.health_new, 2),
                Info = ev.info ?? "",
                ProjHits = ev.proj_hits,
                ProjIntegrity = ev.proj_integrity,
                ProjTravel = ev.proj_travel,
                ProjMismatch = ev.proj_mismatch,
                Desync = ev.desync,
                AttackerDead = ev.attacker_dead
            };
        }

        private static List<CombatLogEventDto> ReadVictimCombatLog(ulong victimUserId)
        {
            var all = CombatLog.Get(victimUserId);
            if (all == null || all.Count == 0) return null;

            var logsList = new List<CombatLog.Event>(all);
            int last = logsList.Count - 1;
            var killLog = logsList[last];
            var idCache = new Dictionary<ulong, string>();
            var container = new List<CombatLogEventDto>(Math.Min(logsList.Count, MaxCombatLogEntries));
            container.Add(ToCombatLogDto(killLog.time, killLog, idCache));

            for (int i = last - 1; i >= 0 && container.Count < MaxCombatLogEntries; i--)
            {
                var ev = logsList[i];
                if (ev.target != "player" && ev.target != "you") continue;
                if (ev.info == "killed") break;

                float gap = Math.Abs(ev.time - logsList[i + 1].time);
                if (gap > CombatLogGapSeconds) break;
                if (killLog.time - ev.time > CombatLogDepthSeconds) break;

                container.Add(ToCombatLogDto(killLog.time, ev, idCache));
            }

            return container.Count == 0 ? null : container;
        }

        private DeathEventDto BuildDeathEvent(BasePlayer victim, HitInfo info, bool hasWound, HitRecord wound)
        {
            var death = new DeathEventDto
            {
                EventId = NewEventId(),
                ServerId = _identity.ServerId,
                CreatedAt = UtcNow(),
                VictimSteamId = victim.UserIDString,
                VictimName = victim.displayName ?? "Unknown",
                VictimPos = Vec3Dto.From(victim.transform.position),
                VictimSleeping = victim.IsSleeping(),
                VictimWounded = victim.IsWounded(),
                Weapon = WeaponName(info),
                Distance = info?.ProjectileDistance ?? 0f,
                IsHeadshot = info?.isHeadshot ?? false,
                BodyPart = BodyPart(info),
                DamageType = MajorityDamage(info)
            };

            var damageType = info?.damageTypes != null ? info.damageTypes.GetMajorityDamageType() : Rust.DamageType.Generic;
            var killer = info?.InitiatorPlayer;
            var initiator = info?.Initiator;

            if (damageType == Rust.DamageType.Suicide || (killer != null && killer == victim))
            {
                death.DeathType = "suicide";
                return death;
            }

            if (killer != null && IsRealSteamId(killer.userID))
            {
                death.DeathType = "pvp";
                death.KillerSteamId = killer.UserIDString;
                death.KillerName = killer.displayName ?? "Unknown";
                death.KillerPos = Vec3Dto.From(killer.transform.position);
                return death;
            }

            if (killer != null)
            {
                death.DeathType = "npc";
                death.KillerName = killer.displayName ?? killer.ShortPrefabName;
                death.KillerPrefab = killer.ShortPrefabName;
                death.KillerPos = Vec3Dto.From(killer.transform.position);
                return death;
            }

            if (hasWound)
            {
                death.DeathType = "pvp";
                death.KillerSteamId = wound.InitiatorSteamId;
                death.KillerName = wound.InitiatorName;
                death.Weapon = wound.Weapon;
                death.Distance = wound.Distance;
                death.IsHeadshot = wound.IsHeadshot;
                death.BodyPart = wound.BodyPart;
                return death;
            }

            if (initiator != null)
            {
                death.DeathType = "entity";
                death.KillerPrefab = initiator.ShortPrefabName;
                death.KillerName = initiator.ShortPrefabName;
                death.KillerPos = Vec3Dto.From(initiator.transform.position);
                return death;
            }

            if (IsEnvironmentDamage(damageType))
            {
                death.DeathType = "environment";
                return death;
            }

            death.DeathType = "unknown";
            death.Raw = JToken.FromObject(new
            {
                damageType = damageType.ToString(),
                hasInfo = info != null,
                weapon = death.Weapon
            });
            return death;
        }

        private void PruneWoundRecords()
        {
            if (_woundedHits.Count == 0) return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            var stale = new List<string>();
            foreach (var entry in _woundedHits)
            {
                if (now - entry.Value.RecordedAt > WoundRecordTtlSeconds)
                    stale.Add(entry.Key);
            }

            foreach (var key in stale) _woundedHits.Remove(key);
        }

        private static readonly string[] ChatSayCommands = { "chat.teamsay", "chat.localsay", "chat.say" };

        private static bool TryGetChatBody(string text, out string body)
        {
            body = "";
            // Runs for every client console command; every say command starts with "chat.".
            if (text == null || text.Length < 8 || (text[0] != 'c' && text[0] != 'C')) return false;

            string matched = null;
            for (int i = 0; i < ChatSayCommands.Length; i++)
            {
                string cmd = ChatSayCommands[i];
                if (!text.StartsWith(cmd, StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Length != cmd.Length && !char.IsWhiteSpace(text[cmd.Length])) continue;
                matched = cmd;
                break;
            }

            if (matched == null) return false;

            string rest = text.Substring(matched.Length).Trim();
            if (rest.Length >= 2 && rest[0] == '"' && rest[rest.Length - 1] == '"')
                rest = rest.Substring(1, rest.Length - 2).Trim();

            int space = rest.IndexOf(' ');
            if (space > 0)
            {
                if (int.TryParse(rest.Substring(0, space), out int channel) && channel >= 0 && channel <= 2)
                    rest = rest.Substring(space + 1).Trim().Trim('"').Trim();
            }

            body = rest;
            return true;
        }

        private bool IsMuted(BasePlayer player, out string reason, out string timeRemaining)
        {
            if (player == null)
            {
                reason = null;
                timeRemaining = null;
                return false;
            }

            return TryGetActiveMute(player.UserIDString, out reason, out timeRemaining);
        }

        private bool IsMuted(string steamId) =>
            TryGetActiveMute(steamId, out _, out _, formatRemaining: false);

        private bool IsMuted(string steamId, out string reason, out string timeRemaining) =>
            TryGetActiveMute(steamId, out reason, out timeRemaining);

        private bool TryGetActiveMute(string steamId, out string reason, out string timeRemaining, bool formatRemaining = true)
        {
            reason = null;
            timeRemaining = null;

            MuteInfo mute;
            if (!TryGetActiveMuteInfo(steamId, out mute)) return false;

            reason = mute.Reason ?? "Violation";
            if (formatRemaining)
                timeRemaining = FormatTimeRemaining(mute.ExpiresAtParsed ?? ParseExpiry(mute.ExpiresAt));
            return true;
        }

        private bool TryGetActiveMuteInfo(string steamId, out MuteInfo mute)
        {
            mute = null;
            if (string.IsNullOrEmpty(steamId) || _mutes?.ActiveMutes == null) return false;
            if (!_mutes.ActiveMutes.TryGetValue(steamId, out mute) || mute == null) return false;

            var expires = mute.ExpiresAtParsed ?? ParseExpiry(mute.ExpiresAt);
            if (mute.ExpiresAtParsed == null) mute.ExpiresAtParsed = expires;
            if (expires != null && DateTimeOffset.UtcNow > expires.Value)
            {
                ClearMute(steamId, true);
                mute = null;
                return false;
            }
            return true;
        }

        private void ApplyMute(string steamId, string reason, string expiresAt, string playerName, bool notify = true)
        {
            if (_mutes?.ActiveMutes == null) return;

            _mutes.ActiveMutes[steamId] = new MuteInfo
            {
                Reason = reason,
                ExpiresAt = expiresAt,
                ExpiresAtParsed = ParseExpiry(expiresAt)
            };
            SaveMutes();

            var player = FindConnectedPlayer(steamId);
            if (player == null) return;

            player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, true);
            if (!notify) return;

            string timeLeft = FormatTimeRemaining(ParseExpiry(expiresAt));
            string notice = Message("Mute.Applied", player, reason, timeLeft);
            SendToast(player, notice);
            player.ChatMessage(notice);
        }

        private void ClearMute(string steamId, bool silent, bool save = true)
        {
            if (_mutes?.ActiveMutes == null) return;

            bool removed = _mutes.ActiveMutes.Remove(steamId);
            if (removed && save) SaveMutes();

            var player = FindConnectedPlayer(steamId);
            if (player == null) return;

            player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, false);
            if (silent || !removed) return;

            string notice = Message("Mute.Removed", player);
            SendToast(player, notice);
            player.ChatMessage(notice);
        }

        private int SweepExpiredMutes()
        {
            if (_mutes?.ActiveMutes == null || _mutes.ActiveMutes.Count == 0) return 0;

            var expired = new List<string>();
            foreach (var entry in _mutes.ActiveMutes)
            {
                var expires = entry.Value?.ExpiresAtParsed ?? ParseExpiry(entry.Value?.ExpiresAt);
                if (expires != null && DateTimeOffset.UtcNow > expires.Value)
                    expired.Add(entry.Key);
            }

            foreach (var steamId in expired) ClearMute(steamId, true, save: false);
            if (expired.Count > 0) SaveMutes();
            return expired.Count;
        }

        private DateTimeOffset? ParseExpiry(string expiresAt)
        {
            if (string.IsNullOrWhiteSpace(expiresAt)) return null;

            // RustCore timestamps are UTC naive parse as UTC, not local server time
            if (long.TryParse(expiresAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixValue))
            {
                return unixValue > 32503680000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                    : DateTimeOffset.FromUnixTimeSeconds(unixValue);
            }

            if (DateTimeOffset.TryParse(
                    expiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
                return parsed;

            return null;
        }

        private static string FormatTimeRemaining(DateTimeOffset? expires)
        {
            if (expires == null) return "Permanent";

            var remaining = expires.Value - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0) return "0s";
            if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
            if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
            return $"{remaining.Minutes}m {remaining.Seconds}s";
        }

        private string FormatAnnounceDuration(string expiresAt)
        {
            var expiry = ParseExpiry(expiresAt);
            if (expiry == null) return "permanently";
            return "for " + FormatTimeRemaining(expiry);
        }

        // Prefer durationSeconds (exact length from apply)
        private bool TryResolvePunishmentExpiry(
            int? durationSeconds,
            string expiresAt,
            out string resolvedExpiresAt,
            out string announceDuration)
        {
            if (durationSeconds != null && durationSeconds.Value > 0)
            {
                var expiry = DateTimeOffset.UtcNow.AddSeconds(durationSeconds.Value);
                resolvedExpiresAt = expiry.ToString("o", CultureInfo.InvariantCulture);
                announceDuration = "for " + FormatTimeSpan(TimeSpan.FromSeconds(durationSeconds.Value));
                return true;
            }

            var panelExpiry = ParseExpiry(expiresAt);
            if (panelExpiry != null && DateTimeOffset.UtcNow >= panelExpiry.Value)
            {
                resolvedExpiresAt = expiresAt;
                announceDuration = null;
                return false;
            }

            resolvedExpiresAt = expiresAt;
            announceDuration = FormatAnnounceDuration(expiresAt);
            return true;
        }

        private static string FormatTimeSpan(TimeSpan remaining)
        {
            if (remaining.TotalSeconds <= 0) return "0s";
            if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
            if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
            return $"{remaining.Minutes}m {remaining.Seconds}s";
        }

        private string ResolveDisplayIp()
        {
            if (IsPublicIpv4(_cachedDisplayIp))
                return _cachedDisplayIp;

            if (IsPublicIpv4(ConVar.Server.ip))
            {
                _cachedDisplayIp = ConVar.Server.ip.Trim();
                return _cachedDisplayIp;
            }

            if (Steamworks.SteamServer.IsValid)
            {
                var steamIp = Steamworks.SteamServer.PublicIp;
                if (steamIp != null)
                {
                    string steam = steamIp.ToString();
                    if (IsPublicIpv4(steam))
                    {
                        _cachedDisplayIp = steam;
                        return _cachedDisplayIp;
                    }
                }
            }

            BeginPublicIpLookup();
            return IsPublicIpv4(_cachedDisplayIp) ? _cachedDisplayIp : null;
        }

        private void BeginPublicIpLookup()
        {
            if (_publicIpLookupStarted) return;
            _publicIpLookupStarted = true;

            webrequest.Enqueue(
                "https://api.ipify.org",
                null,
                (code, response) =>
                {
                    if (Instance == null) return;
                    if (code != 200 || string.IsNullOrWhiteSpace(response)) return;
                    string ip = response.Trim();
                    if (!IsPublicIpv4(ip)) return;
                    Instance._cachedDisplayIp = ip;
                },
                this,
                RequestMethod.GET);
        }

        private static bool IsPublicIpv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            ip = ip.Trim();
            if (ip == "0.0.0.0") return false;

            string[] parts = ip.Split('.');
            if (parts.Length != 4) return false;

            int a, b, c, d;
            if (!int.TryParse(parts[0], out a) || !int.TryParse(parts[1], out b)
                || !int.TryParse(parts[2], out c) || !int.TryParse(parts[3], out d))
                return false;
            if (a < 0 || a > 255 || b < 0 || b > 255 || c < 0 || c > 255 || d < 0 || d > 255)
                return false;

            if (a == 10 || a == 127) return false;
            if (a == 192 && b == 168) return false;
            if (a == 172 && b >= 16 && b <= 31) return false;
            if (a == 169 && b == 254) return false;
            return true;
        }

        private static string DisplayName(string playerName, string steamId)
        {
            return string.IsNullOrEmpty(playerName) ? steamId : playerName;
        }

        private void AnnouncePublic(string key, params object[] args)
        {
            PrintToChat(Message(key, null, args));
        }

        private void SendToast(BasePlayer player, string message)
        {
            if (player == null || !player.IsConnected) return;
            player.SendConsoleCommand("gametip.showtoast", 0, message, "");
        }

        private void Broadcast(int channel, string message)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                player.SendConsoleCommand("chat.add", channel, _config.AvatarSteamId, message);
            }
        }

        #endregion
    }
}
