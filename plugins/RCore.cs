using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    [Info("RCore", "rustcore.co", "1.4.0")]
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
                ["Autokick.Unavailable"] = "Unable to verify access right now. Please try again shortly."
            }, this);
        }

        private string Message(string key, BasePlayer player, params object[] args)
        {
            string template = lang.GetMessage(key, this, player?.UserIDString);
            return args.Length == 0 ? template : string.Format(template, args);
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

        private const string IdentityFileName = "RCore/identity";
        private const string MuteFileName = "RCore/mutes";
        private const string SpoolFileName = "RCore/spool";
        private const string TaskFileName = "RCore/tasks";

        private const ulong SteamIdBase = 76561197960265728UL;
        private const float RequestTimeoutSeconds = 10f;
        private const float IdleHeartbeatSeconds = 15f;
        private const float FullSyncIntervalSeconds = 30f;
        private const float QueuePollInterval = 5f;
        private const float ChatFlushInterval = 3f;
        private const float ReportsFlushInterval = 5f;
        private const float CombatFlushInterval = 10f;
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
        private const float LoginSyncDebounce = 5f;
        private const float FullSyncDebounce = 5f;
        private const float TaskSaveInterval = 5f;
        private const int MaxProcessedTasks = 20000;
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
        private float _lastLoginSync = -999f;
        private float _lastFullSyncRequest = -999f;

        private readonly Dictionary<string, HitRecord> _woundedHits = new Dictionary<string, HitRecord>();
        private readonly Dictionary<ulong, float> _voiceToastCooldowns = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, AfkTracker> _afkTrackers = new Dictionary<ulong, AfkTracker>();
        private readonly Dictionary<string, AutokickCacheEntry> _autokickCache = new Dictionary<string, AutokickCacheEntry>();
        private readonly Dictionary<string, PolicyPending> _policyPending = new Dictionary<string, PolicyPending>();
        // Connection can be between JoinedGame and Spawn, no list yet. Finish kick in OnPlayerConnected.
        private readonly Dictionary<ulong, string> _pendingKick = new Dictionary<ulong, string>();
        private bool _policyInFlight;
        private int _policyFailures;

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
            // some consoles only see global.rcore.*
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

            var toClear = new List<string>();
            foreach (var key in _mutes.ActiveMutes.Keys)
            {
                if (!desired.ContainsKey(key)) toClear.Add(key);
            }
            for (int i = 0; i < toClear.Count; i++)
                ClearMute(toClear[i], true);

            foreach (var pair in desired)
            {
                MuteInfo existing;
                if (_mutes.ActiveMutes.TryGetValue(pair.Key, out existing)
                    && existing != null
                    && string.Equals(existing.Reason, pair.Value.Reason, StringComparison.Ordinal)
                    && string.Equals(existing.ExpiresAt ?? "", pair.Value.ExpiresAt ?? "", StringComparison.Ordinal))
                {
                    EnforceChatMuteFlag(pair.Key, true);
                    continue;
                }

                ApplyMute(pair.Key, pair.Value.Reason, pair.Value.ExpiresAt, null, notify: false);
            }
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

        // don't call mute/unmute cmds - BetterChat shadows them
        private static void EnforceChatMuteFlag(string steamId, bool muted)
        {
            var player = FindConnectedPlayer(steamId);
            if (player == null) return;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, muted);
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

            private GameObject _session;
            private SpoolStore _spool = new SpoolStore();
            private bool _spoolDirty;

            private void Awake()
            {
                Current = this;
                LoadSpool();

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

                State.RequestSync(0.5f);
            }

            private void CloseSession()
            {
                if (_session == null) return;

                var session = _session;
                _session = null;
                State = null; Queue = null; Chat = null;
                Reports = null; Combat = null;

                UnityEngine.Object.DestroyImmediate(session);
                WriteSpool(true);
            }

            public void HandleTasks(List<QueueTaskDto> tasks) => Queue?.Dispatch(tasks);

            public void RequestFullSync(float delay)
            {
                if (State == null || Instance == null) return;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - Instance._lastFullSyncRequest < FullSyncDebounce) return;
                Instance._lastFullSyncRequest = now;
                State.RequestSync(delay);
            }

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
                var bucket = Bucket(kind);
                var inFlight = InFlightBucket(kind);
                var token = JToken.FromObject(item);
                var eventId = SpoolEventId(token);
                if (eventId != null && SpoolContainsEvent(inFlight, eventId)) return;
                if (eventId != null && SpoolContainsEvent(bucket, eventId)) return;
                bucket.Add(token);
                if (bucket.Count > MaxBufferItems)
                    bucket.RemoveRange(0, bucket.Count - MaxBufferItems);
                _spoolDirty = true;
            }

            public void SpoolItems<T>(SpoolKind kind, List<T> items)
            {
                if (items == null || items.Count == 0) return;
                var bucket = Bucket(kind);
                var inFlight = InFlightBucket(kind);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    var token = JToken.FromObject(item);
                    var eventId = SpoolEventId(token);
                    if (eventId != null && SpoolContainsEvent(inFlight, eventId))
                        continue;
                    if (eventId != null && SpoolContainsEvent(bucket, eventId))
                        continue;
                    bucket.Add(token);
                }
                if (bucket.Count > MaxBufferItems)
                    bucket.RemoveRange(0, bucket.Count - MaxBufferItems);
                _spoolDirty = true;
            }

            private List<T> Claim<T>(SpoolKind kind, int max)
            {
                var pending = Bucket(kind);
                var inFlight = InFlightBucket(kind);

                if (inFlight.Count > 0)
                    return DeserializeSpool<T>(inFlight, inFlight.Count);

                int count = Math.Min(pending.Count, max);
                if (count <= 0) return new List<T>();

                for (int i = 0; i < count; i++)
                    inFlight.Add(pending[i]);
                pending.RemoveRange(0, count);
                _spoolDirty = true;
                WriteSpool(true);

                return DeserializeSpool<T>(inFlight, inFlight.Count);
            }

            public void CommitSpool(SpoolKind kind, int sentCount)
            {
                if (sentCount <= 0) return;
                var inFlight = InFlightBucket(kind);
                int n = Math.Min(sentCount, inFlight.Count);
                if (n <= 0) return;
                inFlight.RemoveRange(0, n);
                _spoolDirty = true;
                WriteSpool(true);
            }

            public void ReturnClaimed(SpoolKind kind)
            {
                var pending = Bucket(kind);
                var inFlight = InFlightBucket(kind);
                if (inFlight.Count == 0) return;
                pending.InsertRange(0, inFlight);
                inFlight.Clear();
                if (pending.Count > MaxBufferItems)
                    pending.RemoveRange(0, pending.Count - MaxBufferItems);
                _spoolDirty = true;
                WriteSpool(true);
            }

            private static List<T> DeserializeSpool<T>(List<JToken> source, int count)
            {
                var result = new List<T>(count);
                for (int i = 0; i < count && i < source.Count; i++)
                {
                    try { result.Add(source[i].ToObject<T>()); }
                    catch { }
                }
                return result;
            }

            private static string SpoolEventId(JToken token)
            {
                if (token == null || token.Type != JTokenType.Object) return null;
                var id = token["eventId"];
                return id != null && id.Type == JTokenType.String ? (string)id : null;
            }

            private static bool SpoolContainsEvent(List<JToken> bucket, string eventId)
            {
                if (string.IsNullOrEmpty(eventId) || bucket == null) return false;
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (string.Equals(SpoolEventId(bucket[i]), eventId, StringComparison.Ordinal))
                        return true;
                }
                return false;
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
                    try { body = JsonConvert.SerializeObject(payload, Formatting.None); }
                    catch { return; }
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
                            try { parsed = JsonConvert.DeserializeObject<T>(response); }
                            catch { parsed = null; }
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
                    DisplayIp = (string.IsNullOrWhiteSpace(ConVar.Server.ip) || ConVar.Server.ip == "0.0.0.0" ? null : ConVar.Server.ip),
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
                    try { parsed = JsonConvert.DeserializeObject<PairResponse>(response ?? ""); }
                    catch { parsed = null; }

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
                CancelInvoke(nameof(SyncNow));
                Invoke(nameof(SyncNow), Mathf.Max(0.1f, delay));
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
                    Teams = fullSync ? Instance.SnapshotTeams() : null
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

            if (IsLinked)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastLoginSync >= LoginSyncDebounce)
                {
                    _lastLoginSync = now;
                    _engine?.RequestFullSync(2f);
                }
            }

            if (IsLinked && IsRealSteamId(player.userID))
            {
                string ip = player.net?.connection != null ? CleanIp(player.net.connection.ipaddress) : "0.0.0.0";
                ApplyCachedPolicyOrEnqueue(player.UserIDString, ip);
            }

            string muteReason, muteTime;
            if (IsMuted(player, out muteReason, out muteTime))
                player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, true);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            _voiceToastCooldowns.Remove(player.userID);
            _afkTrackers.Remove(player.userID);
            _woundedHits.Remove(player.UserIDString);
            _engine?.RecordDisconnect(player.UserIDString, reason);
            _engine?.RequestFullSync(1f);
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

            string reason, timeLeft;
            if (!IsMuted(connection.userid.ToString(), out reason, out timeLeft))
                return null;

            var player = connection.player as BasePlayer;
            if (player != null)
            {
                string notice = Message("Mute.Blocked", player, reason, timeLeft);
                SendToast(player, notice);
                player.ChatMessage(notice);
            }

            return false;
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

            var chat = _engine?.Chat;
            if (chat == null) return null;

            string channelName = "GLOBAL";
            string teamId = null;

            switch (channel)
            {
                case ConVar.Chat.ChatChannel.Team:
                    channelName = "TEAM";
                    if (player.currentTeam != 0) teamId = player.currentTeam.ToString();
                    break;
                case ConVar.Chat.ChatChannel.Local:
                    channelName = "LOCAL";
                    break;
            }

            chat.Enqueue(new ChatItemDto
            {
                EventId = NewEventId(),
                SteamId = player.UserIDString,
                Name = player.displayName ?? "Unknown",
                Message = message,
                Channel = channelName,
                TeamId = teamId,
                CreatedAt = UtcNow()
            });

            return null;
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
            if (player != null) _woundedHits.Remove(player.UserIDString);
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

            combat.Enqueue(BuildDeathEvent(player, info, hasWound, wound));
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
                ["give"] = HandleGiveTask
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

            var expiry = ParseExpiry(task.ExpiresAt);
            if (expiry != null && DateTimeOffset.UtcNow >= expiry.Value)
                return TaskResult.Fail("ALREADY_EXPIRED", "ban expiry is in the past");

            string reason = string.IsNullOrEmpty(task.Reason) ? "Banned" : task.Reason;

            ConsoleSystem.Run(ConsoleSystem.Option.Server, "banid", task.SteamId, "RCore", reason);

            if (task.BanIp && !string.IsNullOrEmpty(task.Ip))
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "banip", task.Ip, reason);
            }

            _engine?.Queue?.ScheduleWriteCfg();

            if (ulong.TryParse(task.SteamId, out ulong uid))
            {
                var online = BasePlayer.FindByID(uid);
                if (online != null && online.IsConnected) online.Kick("Banned: " + reason);
            }

            if (task.Announce)
            {
                AnnouncePublic(
                    "Ban.Announced",
                    DisplayName(task.PlayerName, task.SteamId),
                    FormatAnnounceDuration(task.ExpiresAt),
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

        private TaskResult HandleMuteTask(JToken raw)
        {
            var task = Parse<MuteTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            var expiry = ParseExpiry(task.ExpiresAt);
            if (expiry != null && DateTimeOffset.UtcNow >= expiry.Value)
                return TaskResult.Fail("ALREADY_EXPIRED", "mute expiry is in the past");

            string reason = string.IsNullOrEmpty(task.Reason) ? "Rule Violation" : task.Reason;
            bool alreadyMuted = IsMuted(task.SteamId);
            ApplyMute(task.SteamId, reason, task.ExpiresAt, task.PlayerName, notify: !alreadyMuted);

            if (task.Announce && !alreadyMuted)
            {
                AnnouncePublic(
                    "Mute.Announced",
                    DisplayName(task.PlayerName, task.SteamId),
                    FormatAnnounceDuration(task.ExpiresAt),
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
            else
            {
                destination = new Vector3(task.X, task.Y, task.Z);
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
            player = BasePlayer.FindByID(uid);
            if (player == null || !player.IsConnected)
            {
                player = null;
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

        #region Api

        private void Request(string path, string body, RequestMethod method,
                             Action<int, string> onComplete, bool authenticated = true,
                             bool isAutokick = false)
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
                lane);
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
                public Action<int, string> OnComplete;
                public HttpLane Lane;
            }

            private readonly Queue<HttpJob> _authQueue = new Queue<HttpJob>();
            private readonly Queue<HttpJob> _controlQueue = new Queue<HttpJob>();
            private readonly Queue<HttpJob> _bulkQueue = new Queue<HttpJob>();
            private readonly List<HttpJob> _running = new List<HttpJob>();
            private int _active;
            private int _authInFlight;
            private bool _destroyed;

            public void Enqueue(string url, string method, Dictionary<string, string> headers,
                                string body, Action<int, string> onComplete, HttpLane lane)
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
                    OnComplete = onComplete,
                    Lane = lane
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
                    request = new UnityWebRequest(job.Url, job.Method)
                    {
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = Mathf.Max(1, (int)RequestTimeoutSeconds),
                        redirectLimit = 0 // no redirects, keep api key on same host
                    };

                    if (!string.IsNullOrEmpty(job.Body))
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
                catch
                {
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
                catch
                {
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
                SaveCreatedTime = SaveRestore.SaveCreatedTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
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
            try { result = JsonConvert.DeserializeObject<AutokickBatchResponse>(response); }
            catch { return false; }
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
                DisplayIp = (string.IsNullOrWhiteSpace(ConVar.Server.ip) || ConVar.Server.ip == "0.0.0.0" ? null : ConVar.Server.ip),
                UptimeSeconds = (long)UnityEngine.Time.realtimeSinceStartup,
                MemoryUsedMb = (long)Performance.current.memoryUsageSystem,
                QueuedCount = queue?.queue?.Count ?? 0,
                JoiningCount = queue?.joining?.Count ?? 0,
                SaveCreatedTime = SaveRestore.SaveCreatedTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                PluginVersion = Version.ToString(),
                ProtocolVersion = ProtocolVersion,
                Framework = Framework,
                RustProtocol = Rust.Protocol.printable,
                HeaderImage = ConVar.Server.headerimage,
                LogoImage = ConVar.Server.logoimage
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
        }

        private class MuteSyncDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
            [JsonProperty("expiresAt")] public string ExpiresAt { get; set; }
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

            [JsonProperty("raw", NullValueHandling = NullValueHandling.Ignore)] public JToken Raw { get; set; }
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
            [JsonProperty("x")] public float X { get; set; }
            [JsonProperty("y")] public float Y { get; set; }
            [JsonProperty("z")] public float Z { get; set; }
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
            try { return raw.ToObject<T>(); }
            catch { return null; }
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
            if (string.IsNullOrWhiteSpace(text)) return false;

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

        private void ClearMute(string steamId, bool silent)
        {
            if (_mutes?.ActiveMutes == null) return;

            bool removed = _mutes.ActiveMutes.Remove(steamId);
            if (removed) SaveMutes();

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

            foreach (var steamId in expired) ClearMute(steamId, true);
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
