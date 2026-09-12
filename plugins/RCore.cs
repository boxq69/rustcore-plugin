using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;
using ConVar;

namespace Oxide.Plugins
{
    [Info("RCore", "rustcore.co", "1.2.0")]
    [Description("Live telemetry and moderation ingest for rustcore.co")]
    public class RCore : RustPlugin
    {
        #region Configuration

        private const ulong DefaultPanelAvatarSteamId = 76561198708173741UL;

        private class Configuration
        {
            [JsonProperty("PanelAvatarSteamId")]
            public ulong PanelAvatarSteamId = DefaultPanelAvatarSteamId;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try { _config = Config.ReadObject<Configuration>(); }
            catch (Exception ex)
            {
                PrintWarning($"config unreadable ({ex.Message}), falling back to defaults");
                _config = null;
            }

            if (_config == null) _config = new Configuration();
            if (_config.PanelAvatarSteamId < SteamIdBase)
                _config.PanelAvatarSteamId = DefaultPanelAvatarSteamId;

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
                ["Autokick.Default"] = "You are not allowed on this server."
            }, this);
        }

        private string Message(string key, BasePlayer player, params object[] args)
        {
            string template = lang.GetMessage(key, this, player?.UserIDString);
            return args.Length == 0 ? template : string.Format(template, args);
        }

        #endregion

        #region State

        private const string VersionString = "1.2.0";
        private const int ProtocolVersion = 2;

        private const string BaseUrl = "https://api.rustcore.co";
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
        private const string LegacyTaskFileName = "RCore/tasks";

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
        private const int MaxSendRetries = 5;
        private const int UnlinkThreshold = 5;
        private const int LogEveryNFailures = 5;
        private const int MaxTasksPerAck = 200;
        private const long TaskRetentionSeconds = 86400;
        private const float SpoolWriteInterval = 60f;
        private const float LinkVerifyInterval = 30f;
        private const float MuteSweepInterval = 30f;
        private const float LoginSyncDebounce = 3f;
        private const float AutokickCacheTtl = 45f;
        private const int AutokickLoginTimeoutMs = 3000;
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

        private bool _commandsRegistered;
        private bool _pendingWipe;
        private float _lastLoginSync = -999f;

        private readonly Dictionary<string, HitRecord> _woundedHits = new Dictionary<string, HitRecord>();
        private readonly Dictionary<ulong, float> _voiceToastCooldowns = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, AfkTracker> _afkTrackers = new Dictionary<ulong, AfkTracker>();
        private readonly Dictionary<string, AutokickCacheEntry> _autokickCache = new Dictionary<string, AutokickCacheEntry>();

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
            DropLegacyTaskFile();
            RegisterCommands();
        }

        private void OnServerInitialized()
        {
            RegisterCommands();
            SweepExpiredMutes();

            var host = new GameObject("RCore.Engine");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _engine = host.AddComponent<RCoreEngine>();

            Puts($"RCore v{VersionString} loaded (protocol {ProtocolVersion}, {Framework}).");
            if (!IsLinked) Puts("Server is not linked. Run: rcore.pair <CODE>");
        }

        private void Unload()
        {
            if (_engine != null)
            {
                UnityEngine.Object.DestroyImmediate(_engine.gameObject);
                _engine = null;
            }

            _woundedHits.Clear();
            _voiceToastCooldowns.Clear();
            _afkTrackers.Clear();
            Instance = null;
        }

        private void RegisterCommands()
        {
            if (_commandsRegistered) return;
            cmd.AddConsoleCommand("rcore.pair", this, nameof(CmdPair));
            cmd.AddConsoleCommand("global.rcore.pair", this, nameof(CmdPair));
            cmd.AddConsoleCommand("rcore.unlink", this, nameof(CmdUnlink));
            cmd.AddConsoleCommand("global.rcore.unlink", this, nameof(CmdUnlink));
            cmd.AddConsoleCommand("rcore.status", this, nameof(CmdStatus));
            cmd.AddConsoleCommand("rcore.poll", this, nameof(CmdPoll));
            _commandsRegistered = true;
        }

        private void LoadIdentity()
        {
            try { _identity = Interface.Oxide.DataFileSystem.ReadObject<IdentityData>(IdentityFileName) ?? new IdentityData(); }
            catch { _identity = new IdentityData(); }
            if (string.IsNullOrEmpty(_identity.InstalledAtUtc)) _identity.InstalledAtUtc = UtcNow();
        }

        private void SaveIdentity() => Interface.Oxide.DataFileSystem.WriteObject(IdentityFileName, _identity);

        private void LoadMutes()
        {
            try { _mutes = Interface.Oxide.DataFileSystem.ReadObject<MuteStore>(MuteFileName) ?? new MuteStore(); }
            catch { _mutes = new MuteStore(); }
            if (_mutes.ActiveMutes == null) _mutes.ActiveMutes = new Dictionary<string, MuteInfo>();
        }

        private void SaveMutes() => Interface.Oxide.DataFileSystem.WriteObject(MuteFileName, _mutes);

        private void LoadProcessedTasks()
        {
            _tasks = new ProcessedTaskStore();
        }

        private void DropLegacyTaskFile()
        {
            try
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(LegacyTaskFileName)) return;
                var path = Interface.Oxide.DataFileSystem.GetFile(LegacyTaskFileName).Filename;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                PrintWarning($"could not remove leftover task store: {ex.Message}");
            }
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
            PrintWarning($"{arg.Connection.username} ({arg.Connection.userid}) tried to run {arg.cmd?.FullName ?? "an rcore command"} without owner auth");
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
                Reply(arg, "RCore engine is not running yet, retry once the server has finished starting");
                return;
            }

            var code = arg.GetString(0, string.Empty).Trim();
            if (code.Length == 0)
            {
                Reply(arg, "Usage: rcore.pair <CODE>");
                return;
            }

            Reply(arg, "pairing requested — see the server console for the result");
            link.Pair(code);
        }

        private void CmdUnlink(ConsoleSystem.Arg arg)
        {
            if (!RequireOperator(arg)) return;

            if (!IsLinked)
            {
                Reply(arg, "Server is not linked.");
                return;
            }

            var link = _engine?.Link;
            if (link == null)
            {
                Reply(arg, "RCore engine is not running yet, retry once the server has finished starting");
                return;
            }

            var serverId = _identity.ServerId;
            link.UnlinkByOperator();
            Reply(arg, $"unlinked from server {serverId}. Run rcore.pair <CODE> to link again.");
        }

        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!RequireOperator(arg)) return;

            var lines = new List<string>
            {
                $"RCore v{VersionString} ({Framework}, protocol {ProtocolVersion})",
                IsLinked
                    ? $"linked: server={_identity.ServerId} project={_identity.ProjectId} since={_identity.PairedAtUtc}"
                    : "linked: no — run rcore.pair <CODE>",
                $"backend: {BaseUrl}",
                $"active mutes: {_mutes.ActiveMutes.Count}, tracked wounds: {_woundedHits.Count}",
                _engine != null ? _engine.DescribeBuffers() : "engine: not running"
            };
            Reply(arg, string.Join("\n", lines));
        }

        private void CmdPoll(ConsoleSystem.Arg arg)
        {
            if (!RequireOperator(arg)) return;

            var queue = _engine?.Queue;
            if (queue == null)
            {
                Reply(arg, "not linked, nothing to poll");
                return;
            }
            Reply(arg, "forcing queue poll");
            queue.Poll();
        }

        #endregion

        #region Workers

        private class RCoreEngine : MonoBehaviour
        {
            public static RCoreEngine Current;

            public LinkWorker Link;
            public StateWorker State;
            public QueueWorker Queue;
            public ChatWorker Chat;
            public ReportWorker Reports;
            public CombatWorker Combat;
            public MuteWorker Mutes;
            public SpoolWorker Spooler;

            private GameObject _session;
            private SpoolStore _spool = new SpoolStore();
            private bool _spoolDirty;

            private void Awake()
            {
                Current = this;
                LoadSpool();

                Link = gameObject.AddComponent<LinkWorker>();
                Link.OnLinked += OpenSession;
                Link.OnUnlinked += CloseSession;

                if (Instance != null && Instance.IsLinked) OpenSession();
            }

            private void OnDestroy()
            {
                CloseSession();
                WriteSpool(true);
                if (Current == this) Current = null;
            }

            private void OpenSession()
            {
                if (_session != null) return;

                _session = new GameObject("RCore.Session");
                _session.transform.SetParent(transform, false);

                State = _session.AddComponent<StateWorker>();
                Queue = _session.AddComponent<QueueWorker>();
                Chat = _session.AddComponent<ChatWorker>();
                Reports = _session.AddComponent<ReportWorker>();
                Combat = _session.AddComponent<CombatWorker>();
                Mutes = _session.AddComponent<MuteWorker>();
                Spooler = _session.AddComponent<SpoolWorker>();

                State.RequestSync(0.5f);
            }

            private void CloseSession()
            {
                if (_session == null) return;

                var doomed = _session;
                _session = null;
                State = null; Queue = null; Chat = null;
                Reports = null; Combat = null; Mutes = null; Spooler = null;

                UnityEngine.Object.DestroyImmediate(doomed);
                WriteSpool(true);
            }

            public void HandleTasks(List<QueueTaskDto> tasks) => Queue?.Dispatch(tasks);

            public void RequestFullSync(float delay) => State?.RequestSync(delay);

            public void RecordDisconnect(string steamId, string reason) => State?.EnqueueDisconnect(steamId, reason);

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
                else Retain(SpoolKind.Alerts, new List<AlertDto> { alert });
            }

            public void Retain<T>(SpoolKind kind, List<T> items)
            {
                if (items == null || items.Count == 0) return;
                var bucket = Bucket(kind);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (bucket.Count >= MaxBufferItems) bucket.RemoveAt(0);
                    bucket.Add(JToken.FromObject(item));
                }
                _spoolDirty = true;
            }

            private List<T> Recover<T>(SpoolKind kind, int max)
            {
                var bucket = Bucket(kind);
                int count = Math.Min(bucket.Count, max);
                var result = new List<T>();

                for (int i = 0; i < count; i++)
                {
                    try { result.Add(bucket[i].ToObject<T>()); }
                    catch { }
                }

                if (count == 0) return result;
                bucket.RemoveRange(0, count);
                _spoolDirty = true;
                return result;
            }

            public void RecoverSpool()
            {
                int total = 0;

                if (Chat != null && Chat.WantsSpool)
                {
                    var items = Recover<ChatItemDto>(SpoolKind.Chat, Chat.SpoolChunk);
                    Chat.EnqueueRange(items);
                    total += items.Count;
                }

                if (Reports != null && Reports.WantsSpool)
                {
                    var items = Recover<ReportItemDto>(SpoolKind.Reports, Reports.SpoolChunk);
                    Reports.EnqueueRange(items);
                    total += items.Count;
                }

                if (Combat != null && Combat.WantsSpool)
                {
                    var items = Recover<DeathEventDto>(SpoolKind.Deaths, Combat.SpoolChunk);
                    Combat.EnqueueRange(items);
                    total += items.Count;
                }

                if (State != null && State.WantsSpool)
                {
                    var items = Recover<AlertDto>(SpoolKind.Alerts, State.SpoolChunk);
                    foreach (var alert in items) State.EnqueueAlert(alert);
                    total += items.Count;
                }

                if (total == 0) return;

                Log($"recovered {total} spooled event(s) from disk");
                WriteSpool(true);
            }

            public void WriteSpool(bool force = false)
            {
                if (!force && !_spoolDirty) return;
                try
                {
                    Interface.Oxide.DataFileSystem.WriteObject(SpoolFileName, _spool);
                    _spoolDirty = false;
                }
                catch (Exception ex) { Warn($"spool write failed: {ex.Message}"); }
            }

            public string DescribeBuffers()
            {
                if (_session == null) return "engine: running, session closed";
                return $"buffers: chat={Chat?.Pending ?? 0} reports={Reports?.Pending ?? 0} " +
                       $"deaths={Combat?.Pending ?? 0} alerts={State?.PendingAlerts ?? 0} " +
                       $"spool={_spool.Chat.Count + _spool.Reports.Count + _spool.Deaths.Count + _spool.Alerts.Count}";
            }

            private void LoadSpool()
            {
                try { _spool = Interface.Oxide.DataFileSystem.ReadObject<SpoolStore>(SpoolFileName) ?? new SpoolStore(); }
                catch { _spool = new SpoolStore(); }
                _spool.Normalize();
            }

            private List<JToken> Bucket(SpoolKind kind)
            {
                switch (kind)
                {
                    case SpoolKind.Chat: return _spool.Chat;
                    case SpoolKind.Reports: return _spool.Reports;
                    case SpoolKind.Deaths: return _spool.Deaths;
                    default: return _spool.Alerts;
                }
            }
        }

        private abstract class RCoreWorker : MonoBehaviour
        {
            private static readonly float[] BackoffSteps = { 15f, 30f, 60f, 120f };

            private int _inFlight;
            private float _inFlightSince = -1f;
            private string _inFlightPath;
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

                        _inFlight = 0;
                        _inFlightSince = -1f;
                        Warn($"{GetType().Name}: request to {_inFlightPath} never completed after {limit:0}s, releasing the worker");
                        return true;
                    }

                    return now >= _backoffUntil;
                }
            }

            private void Awake()
            {
                InvokeRepeating(nameof(Tick), UnityEngine.Random.Range(0f, 5f), Mathf.Max(1f, Interval));
            }

            private void OnDestroy()
            {
                CancelInvoke();
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
                    catch (Exception ex)
                    {
                        Warn($"{GetType().Name}: payload serialization failed: {ex.Message}");
                        return;
                    }
                }

                if (_inFlight == 0) _inFlightSince = UnityEngine.Time.realtimeSinceStartup;
                _inFlight++;
                _inFlightPath = path;

                plugin.Request(path, body, method, (code, response) =>
                {
                    _inFlight = Mathf.Max(0, _inFlight - 1);
                    if (_inFlight == 0) _inFlightSince = -1f;
                    if (this == null) return;

                    Engine?.Link?.NoteResponse(code, response);

                    if (code >= 200 && code < 300)
                    {
                        _failures = 0;
                        _backoffUntil = 0f;

                        T parsed = null;
                        if (!string.IsNullOrEmpty(response))
                        {
                            try { parsed = JsonConvert.DeserializeObject<T>(response); }
                            catch (Exception ex) { Warn($"{GetType().Name}: malformed response from {path}: {ex.Message}"); }
                        }

                        onSuccess?.Invoke(parsed);
                        return;
                    }

                    NoteFailure(path, code, response);
                    onError?.Invoke(code, response);
                });
            }

            private void NoteFailure(string path, int code, string response)
            {
                _failures++;
                float delay = BackoffSteps[Mathf.Min(_failures - 1, BackoffSteps.Length - 1)];
                _backoffUntil = UnityEngine.Time.realtimeSinceStartup + delay;

                if (_failures == 1 || _failures % LogEveryNFailures == 0)
                    Warn($"{GetType().Name}: {path} failed {_failures}x (code {code}), retrying in {delay:0}s: {Excerpt(response)}");
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

            public int Pending => Buffer.Count + (_inFlightBatch?.Count ?? 0);

            public bool WantsSpool => Ready && Buffer.Count < BatchSize;

            public int SpoolChunk => BatchSize;

            protected EventBuffer<TItem> Buffer
            {
                get
                {
                    if (_buffer == null) _buffer = new EventBuffer<TItem>(GetType().Name);
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
                        _inFlightBatch = null;
                        _retries = 0;
                    },
                    (code, response) =>
                    {
                        _inFlightBatch = null;
                        Buffer.Requeue(batch);

                        if (++_retries < MaxSendRetries) return;
                        _retries = 0;

                        var spilled = Buffer.Take(BatchSize);
                        Engine?.Retain(Kind, spilled);
                        Warn($"{GetType().Name}: spooled {spilled.Count} event(s) to disk after {MaxSendRetries} failed attempts");
                    });
            }

            protected override void Drain()
            {
                var pending = Buffer.DrainAll();
                if (_inFlightBatch != null)
                {
                    pending.InsertRange(0, _inFlightBatch);
                    _inFlightBatch = null;
                }
                Engine?.Retain(Kind, pending);
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

                if (string.IsNullOrEmpty(code))
                {
                    Log("Usage: rcore.pair <CODE>");
                    return;
                }

                if (_pairPending)
                {
                    Log("pairing already in progress");
                    return;
                }

                _pairPending = true;

                var currentServerId = Instance.IsLinked && !string.IsNullOrEmpty(Instance._identity.ServerId)
                    ? Instance._identity.ServerId
                    : null;
                if (currentServerId != null)
                    Log($"already linked as server {currentServerId}: only a re-pair code for this server is accepted (run rcore.unlink first to link as a different server)");

                Log("pairing with rustcore.co...");

                var payload = new PairRequest
                {
                    Code = code,
                    ServerName = ConVar.Server.hostname ?? "Rust Server",
                    Port = ConVar.Server.port,
                    DisplayIp = SafeServerIp(),
                    Version = VersionString,
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

                    if (status != 200 && status != 201)
                    {
                        Warn($"pairing failed ({status}): {DescribeFailure(status, response)}");
                        return;
                    }

                    PairResponse parsed = null;
                    try { parsed = JsonConvert.DeserializeObject<PairResponse>(response ?? ""); }
                    catch (Exception ex) { Warn($"pairing failed, unreadable response: {ex.Message}"); }

                    var data = parsed?.Data;
                    if (data == null || string.IsNullOrEmpty(data.ApiKey))
                    {
                        Warn("pairing failed: backend did not return an API key");
                        return;
                    }

                    Instance._identity.ApiKey = data.ApiKey;
                    Instance._identity.ServerId = data.ServerId;
                    Instance._identity.ProjectId = data.ProjectId;
                    Instance._identity.PairedAtUtc = UtcNow();
                    Instance._identity.LastError = null;
                    Instance.SaveIdentity();

                    _fatalStreak = 0;
                    Log($"paired successfully (server {data.ServerId})");
                    OnLinked?.Invoke();
                    Interface.CallHook("RCore_OnLinked", data.ServerId, data.ProjectId);
                }, false);
            }

            public void NoteResponse(int code, string body)
            {
                if (code >= 200 && code < 300)
                {
                    _fatalStreak = 0;
                    return;
                }

                if (code != 401 && code != 404) return;
                if (!IsRevocation(body)) return;

                _fatalStreak++;
                if (_fatalStreak < UnlinkThreshold)
                {
                    Warn($"backend reported the link is gone ({code}), {UnlinkThreshold - _fatalStreak} more before unlinking");
                    return;
                }

                Unlink($"backend returned {code} with a revocation code {UnlinkThreshold} times in a row");
            }

            private static bool IsRevocation(string body)
            {
                string code, ignoredMessage;
                return TryReadErrorEnvelope(body, out code, out ignoredMessage)
                       && (string.Equals(code, CodeServerDeleted, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(code, CodeKeyRevoked, StringComparison.OrdinalIgnoreCase));
            }

            private static bool TryReadErrorEnvelope(string body, out string code, out string message)
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

            private static string DescribeFailure(int status, string body)
            {
                if (status == 0) return "no response from the backend (network error or timeout)";

                string code, message;
                if (TryReadErrorEnvelope(body, out code, out message))
                    return string.IsNullOrEmpty(message) ? code : $"{code} — {message}";

                var excerpt = Excerpt(body);
                return excerpt.Length == 0 ? $"HTTP {status} with an empty body" : excerpt;
            }

            public void UnlinkByOperator()
            {
                if (Instance == null || !Instance.IsLinked) return;
                Unlink($"operator ran rcore.unlink (was server {Instance._identity.ServerId}, project {Instance._identity.ProjectId})");
            }

            private void Unlink(string reason)
            {
                if (Instance == null) return;

                Warn($"unlinking: {reason}. Use 'rcore.pair <CODE>' to link again.");

                Instance._identity.ApiKey = null;
                Instance._identity.ServerId = null;
                Instance._identity.ProjectId = null;
                Instance._identity.PairedAtUtc = null;
                Instance._identity.LastError = reason;
                Instance.SaveIdentity();

                _fatalStreak = 0;
                OnUnlinked?.Invoke();
                Interface.CallHook("RCore_OnUnlinked", reason);
            }
        }

        private class StateWorker : RCoreWorker
        {
            private readonly EventBuffer<AlertDto> _alerts = new EventBuffer<AlertDto>("alerts");
            private readonly EventBuffer<DisconnectDto> _disconnects = new EventBuffer<DisconnectDto>("disconnects");
            private List<AlertDto> _alertsInFlight;
            private List<DisconnectDto> _disconnectsInFlight;
            private float _lastFullSync = -999f;
            private bool _forceFullSync = true;

            protected override float Interval => IdleHeartbeatSeconds;

            public int PendingAlerts => _alerts.Count + (_alertsInFlight?.Count ?? 0);

            public bool WantsSpool => Ready && _alerts.Count < MaxBatchItems;

            public int SpoolChunk => MaxBatchItems;

            public void EnqueueAlert(AlertDto alert) => _alerts.Add(alert);

            public void EnqueueDisconnect(string steamId, string reason)
            {
                if (string.IsNullOrEmpty(steamId)) return;
                string trimmed = string.IsNullOrWhiteSpace(reason) ? "Disconnected" : reason.Trim();
                if (trimmed.Length > 256) trimmed = trimmed.Substring(0, 256);
                _disconnects.Add(new DisconnectDto { SteamId = steamId, Reason = trimmed });
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
                    Server = fullSync ? Instance.SnapshotServer() : null
                };

                _alertsInFlight = _alerts.Take(MaxBatchItems);
                if (_alertsInFlight.Count > 0) payload.Alerts = _alertsInFlight;

                _disconnectsInFlight = _disconnects.Take(MaxDisconnectsPerHeartbeat);
                if (_disconnectsInFlight.Count > 0) payload.Disconnects = _disconnectsInFlight;

                Send<HeartbeatResponse>(HeartbeatPath, payload,
                    response =>
                    {
                        _alertsInFlight = null;
                        _disconnectsInFlight = null;
                        if (response?.Tasks != null && response.Tasks.Count > 0)
                            Engine?.HandleTasks(response.Tasks);
                    },
                    (code, body) =>
                    {
                        if (_alertsInFlight != null) _alerts.Requeue(_alertsInFlight);
                        _alertsInFlight = null;
                        if (_disconnectsInFlight != null) _disconnects.Requeue(_disconnectsInFlight);
                        _disconnectsInFlight = null;
                    });
            }

            protected override void Drain()
            {
                var pending = _alerts.DrainAll();
                if (_alertsInFlight != null)
                {
                    pending.InsertRange(0, _alertsInFlight);
                    _alertsInFlight = null;
                }
                Engine?.Retain(SpoolKind.Alerts, pending);
                _disconnectsInFlight = null;
                _disconnects.DrainAll();
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

        private class MuteWorker : RCoreWorker
        {
            protected override float Interval => MuteSweepInterval;

            protected override void Tick()
            {
                int cleared = Instance?.SweepExpiredMutes() ?? 0;
                if (cleared > 0) Log($"{cleared} mute(s) expired");
            }
        }

        private class SpoolWorker : RCoreWorker
        {
            protected override float Interval => SpoolWriteInterval;

            protected override void Tick()
            {
                var engine = Engine;
                if (engine == null) return;

                engine.RecoverSpool();
                engine.WriteSpool();
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

            protected override void Tick()
            {
                Instance?.PruneProcessedTasks();
                Poll();
            }

            public void Poll()
            {
                if (!Ready || Instance == null || !Instance.IsLinked) return;

                Send<List<QueueTaskDto>>(QueuePath, null,
                    tasks => Dispatch(tasks), null, RequestMethod.GET);
            }

            public void Dispatch(List<QueueTaskDto> tasks)
            {
                if (tasks == null || tasks.Count == 0 || Instance == null) return;

                var results = new Dictionary<string, TaskResult>();
                int handled = 0;

                foreach (var task in tasks)
                {
                    if (task == null || string.IsNullOrEmpty(task.Type)) continue;

                    if (string.IsNullOrEmpty(task.Id))
                    {
                        Warn($"task without id ignored: {task.Type}");
                        continue;
                    }

                    if (handled++ >= MaxTasksPerAck) break;

                    TaskResult processed;
                    if (Instance.WasProcessed(task.Id, out processed))
                    {
                        if (processed != null) results[task.Id] = processed.AsReplay();
                        continue;
                    }

                    Func<JToken, TaskResult> handler;
                    if (!Handlers.TryGetValue(task.Type, out handler))
                    {
                        Warn($"unknown task type '{task.Type}'");
                        var unknown = TaskResult.Fail("UNKNOWN_TYPE", $"no handler for '{task.Type}'");
                        Instance.MarkProcessed(task.Id, unknown);
                        results[task.Id] = unknown;
                        continue;
                    }

                    TaskResult result;
                    try { result = handler(task.Data) ?? TaskResult.Ok(); }
                    catch (Exception ex)
                    {
                        result = TaskResult.Fail("HANDLER_ERROR", ex.Message);
                        Warn($"task '{task.Type}' threw: {ex.Message}");
                    }

                    Instance.MarkProcessed(task.Id, result);
                    results[task.Id] = result;
                }

                if (results.Count > 0) Acknowledge(results);
            }

            private void Acknowledge(Dictionary<string, TaskResult> results)
            {
                Send<JObject>(AckPath, new AckPayload { Results = results }, null,
                    (code, body) => Warn($"task ack rejected ({code}): {Excerpt(body)}"));
            }

            public void ScheduleWriteCfg()
            {
                CancelInvoke(nameof(WriteCfg));
                Invoke(nameof(WriteCfg), 5f);
            }

            private void WriteCfg() => ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.writecfg");
        }

        #endregion

        #region Hooks

        private void OnNewSave(string filename)
        {
            _pendingWipe = true;
            SendWipe();
        }

        private void OnClientAuth(Network.Connection connection)
        {
            if (!IsLinked || connection == null || !IsRealSteamId(connection.userid)) return;
            BeginAutokickPrefetch(connection.userid.ToString(CultureInfo.InvariantCulture), CleanIp(connection.ipaddress));
        }

        private object CanUserLogin(string name, string id, string ipAddress)
        {
            if (IsLinked)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastLoginSync >= LoginSyncDebounce)
                {
                    _lastLoginSync = now;
                    _engine?.RequestFullSync(1.5f);
                }
            }

            return EvaluateLoginAutokick(id, ipAddress);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            _voiceToastCooldowns.Remove(player.userID);
            _afkTrackers.Remove(player.userID);
            _engine?.RequestFullSync(2f);

            if (IsLinked && IsRealSteamId(player.userID)) CheckAutokick(player);

            string muteReason, muteTime;
            if (IsMuted(player, out muteReason, out muteTime))
                player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, true);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;

            _voiceToastCooldowns.Remove(player.userID);
            _afkTrackers.Remove(player.userID);
            _woundedHits.Remove(player.UserIDString);
            _engine?.RecordDisconnect(player.UserIDString, reason);
            _engine?.RequestFullSync(1f);
        }

        private object OnClientCommand(Network.Connection connection, string text)
        {
            if (connection == null || !IsRealSteamId(connection.userid)) return null;

            string body;
            if (!TryGetChatBody(text, out body)) return null;
            if (body.StartsWith("/", StringComparison.Ordinal)) return null;

            string reason, timeLeft;
            if (!IsMuted(connection.userid.ToString(CultureInfo.InvariantCulture), out reason, out timeLeft))
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

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrWhiteSpace(message)) return null;

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
            if (player == null) return null;

            string reason, timeLeft;
            if (!IsMuted(player, out reason, out timeLeft)) return null;

            float lastWarning;
            if (!_voiceToastCooldowns.TryGetValue(player.userID, out lastWarning)
                || UnityEngine.Time.realtimeSinceStartup - lastWarning > VoiceToastCooldown)
            {
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
            string formatted = $"<size=12><color=#ffffffB3>Message from Administration</color></size>\n<color=#AAFF55>{sender}</color>: {content}";

            ulong targetUid;
            if (!string.IsNullOrEmpty(task.TargetSteamId) && ulong.TryParse(task.TargetSteamId, out targetUid))
            {
                var target = BasePlayer.FindByID(targetUid);
                if (target == null || !target.IsConnected) return TaskResult.Fail("PLAYER_OFFLINE", "target is not connected");
                target.SendConsoleCommand("chat.add", channel, _config.PanelAvatarSteamId, formatted);
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

            string command = task.Command.Trim();
            PrintWarning($"rejected remote command '{command}': remote commands are disabled");
            _engine?.RaiseAlert("command_rejected", "remote commands disabled", new { command });
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

            Puts($"banning {task.SteamId}: {reason}");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "banid", task.SteamId, "RCore", reason);

            if (task.BanIp && !string.IsNullOrEmpty(task.Ip))
            {
                Puts($"ip-banning {task.Ip}: {reason}");
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "banip", task.Ip, reason);
            }

            _engine?.Queue?.ScheduleWriteCfg();

            ulong uid;
            if (ulong.TryParse(task.SteamId, out uid))
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

            Puts($"unbanning {task.SteamId}");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "unban", task.SteamId);
            _engine?.Queue?.ScheduleWriteCfg();

            if (task.Announce)
                AnnouncePublic("Unban.Announced", DisplayName(task.PlayerName, task.SteamId));

            return TaskResult.Ok();
        }

        private TaskResult HandleMuteTask(JToken raw)
        {
            var task = Parse<MuteTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            var expiry = ParseExpiry(task.ExpiresAt);
            if (expiry != null && DateTimeOffset.UtcNow >= expiry.Value)
                return TaskResult.Fail("ALREADY_EXPIRED", "mute expiry is in the past");

            string reason = string.IsNullOrEmpty(task.Reason) ? "Rule Violation" : task.Reason;
            ApplyMute(task.SteamId, reason, task.ExpiresAt, task.PlayerName);

            if (task.Announce)
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

            Puts($"unmuting {task.SteamId}");
            ClearMute(task.SteamId, false);

            if (task.Announce)
                AnnouncePublic("Unmute.Announced", DisplayName(task.PlayerName, task.SteamId));

            return TaskResult.Ok();
        }

        private TaskResult HandleKickTask(JToken raw)
        {
            var task = Parse<KickTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            ulong uid;
            if (!ulong.TryParse(task.SteamId, out uid)) return TaskResult.Fail("BAD_STEAMID", task.SteamId);

            var player = BasePlayer.FindByID(uid);
            if (player == null || !player.IsConnected) return TaskResult.Fail("PLAYER_OFFLINE", "target is not connected");

            string reason = string.IsNullOrEmpty(task.Reason) ? Message("Kick.Default", player) : task.Reason;
            Puts($"kicking {player.displayName} ({task.SteamId}): {reason}");
            player.Kick(reason);
            return TaskResult.Ok();
        }

        private TaskResult HandleTeleportTask(JToken raw)
        {
            var task = Parse<TeleportTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");

            ulong uid;
            if (!ulong.TryParse(task.SteamId, out uid)) return TaskResult.Fail("BAD_STEAMID", task.SteamId);

            var player = BasePlayer.FindByID(uid);
            if (player == null || !player.IsConnected) return TaskResult.Fail("PLAYER_OFFLINE", "target is not connected");

            Vector3 destination;
            if (!string.IsNullOrEmpty(task.TargetSteamId))
            {
                ulong destUid;
                if (!ulong.TryParse(task.TargetSteamId, out destUid)) return TaskResult.Fail("BAD_STEAMID", task.TargetSteamId);

                var anchor = BasePlayer.FindByID(destUid);
                if (anchor == null || !anchor.IsConnected) return TaskResult.Fail("PLAYER_OFFLINE", "destination player is not connected");
                destination = anchor.transform.position;
            }
            else
            {
                destination = new Vector3(task.X, task.Y, task.Z);
            }

            Puts($"teleporting {player.displayName} to {destination}");
            player.Teleport(destination);
            return TaskResult.Ok();
        }

        private TaskResult HandleGiveTask(JToken raw)
        {
            var task = Parse<GiveTaskDto>(raw);
            if (task == null || string.IsNullOrEmpty(task.SteamId))
                return TaskResult.Fail("NO_TARGET", "steamId missing");
            if (string.IsNullOrEmpty(task.Item)) return TaskResult.Fail("NO_ITEM", "item shortname missing");

            ulong uid;
            if (!ulong.TryParse(task.SteamId, out uid)) return TaskResult.Fail("BAD_STEAMID", task.SteamId);

            var player = BasePlayer.FindByID(uid);
            if (player == null || !player.IsConnected) return TaskResult.Fail("PLAYER_OFFLINE", "target is not connected");

            int amount = task.Amount > 0 ? task.Amount : 1;
            var item = ItemManager.CreateByName(task.Item, amount, task.SkinId);
            if (item == null) return TaskResult.Fail("UNKNOWN_ITEM", task.Item);

            Puts($"giving {amount}x {task.Item} to {player.displayName}");
            player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
            return TaskResult.Ok();
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
        }

        private void PruneProcessedTasks()
        {
            if (_tasks?.Processed == null || _tasks.Processed.Count == 0) return;

            long now = NowUnix();
            var stale = _tasks.Processed
                .Where(entry => entry.Value == null || entry.Value.ExpiresAt <= now)
                .Select(entry => entry.Key)
                .ToList();
            if (stale.Count == 0) return;

            foreach (var key in stale) _tasks.Processed.Remove(key);
        }

        #endregion

        #region Api

        private void Request(string path, string body, RequestMethod method,
                             Action<int, string> onComplete, bool authenticated = true)
        {
            if (webrequest == null)
            {
                onComplete?.Invoke(0, null);
                return;
            }

            try
            {
                webrequest.Enqueue(BuildUrl(path), body, (code, response) => onComplete?.Invoke(code, response),
                    this, method, BuildHeaders(authenticated), RequestTimeoutSeconds);
            }
            catch (Exception ex)
            {
                PrintWarning($"request to {path} could not be queued: {ex.Message}");
                onComplete?.Invoke(0, null);
            }
        }

        private string BuildUrl(string path)
        {
            var baseUrl = BaseUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path)) path = "/";
            path = path.Trim();
            if (!path.StartsWith("/")) path = "/" + path;
            return baseUrl + path;
        }

        private Dictionary<string, string> BuildHeaders(bool authenticated)
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-RCore-Protocol"] = ProtocolVersion.ToString(CultureInfo.InvariantCulture),
                ["X-RCore-Version"] = VersionString
            };

            if (authenticated) headers["X-API-Key"] = _identity?.ApiKey ?? "";
            return headers;
        }

        private void SendWipe()
        {
            if (!IsLinked || !_pendingWipe) return;
            _pendingWipe = false;

            var payload = new WipePayload
            {
                EventId = NewEventId(),
                ServerId = _identity.ServerId,
                CreatedAt = UtcNow(),
                Level = ConVar.Server.level,

                Seed = (uint)ConVar.Server.seed,
                WorldSize = (uint)ConVar.Server.worldsize,
                SaveCreatedTime = SafeSaveCreatedTime()
            };

            Puts("map wipe detected, notifying backend");
            Request(WipePath, JsonConvert.SerializeObject(payload, Formatting.None), RequestMethod.POST,
                (code, response) =>
                {
                    if (code < 200 || code >= 300)
                    {
                        PrintWarning($"wipe notification failed ({code}): {Excerpt(response)}");
                        _pendingWipe = true;
                        return;
                    }
                    _engine?.RequestFullSync(0.5f);
                });
        }

        private object EvaluateLoginAutokick(string steamId, string ipAddress)
        {
            if (!IsLinked || !IsRealSteamId(steamId)) return null;

            string ip = CleanIp(ipAddress);
            AutokickCacheEntry cached;
            if (TryGetAutokickCache(steamId, ip, out cached) && cached.Decided)
            {
                if (!cached.Kick) return null;
                string cachedReason = AutokickReason(steamId, cached.Reason);
                Puts($"autokick (login): {steamId} {ip} — {cachedReason}");
                return cachedReason;
            }

            AutokickResponse result;
            if (!TryPostAutokickBlocking(steamId, ip, AutokickLoginTimeoutMs, out result))
                return null;

            StoreAutokickCache(steamId, ip, result);
            if (!result.Kick) return null;

            string reason = AutokickReason(steamId, result.Reason);
            Puts($"autokick (login): {steamId} {ip} — {reason}");
            return reason;
        }

        private void BeginAutokickPrefetch(string steamId, string ip)
        {
            AutokickCacheEntry cached;
            if (TryGetAutokickCache(steamId, ip, out cached) && cached.Decided) return;

            Request(AutokickCheckPath, SerializeAutokickRequest(steamId, ip), RequestMethod.POST, (code, response) =>
            {
                AutokickResponse result;
                if (!TryParseAutokickResponse(code, response, out result)) return;
                StoreAutokickCache(steamId, ip, result);
            });
        }

        private void CheckAutokick(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            string steamId = player.UserIDString;
            string ip = player.net?.connection != null ? CleanIp(player.net.connection.ipaddress) : "0.0.0.0";
            ulong uid = player.userID;

            AutokickCacheEntry cached;
            if (TryGetAutokickCache(steamId, ip, out cached) && cached.Decided)
            {
                if (!cached.Kick) return;
                KickForAutokick(player, AutokickReason(steamId, cached.Reason));
                return;
            }

            Request(AutokickCheckPath, SerializeAutokickRequest(steamId, ip), RequestMethod.POST, (code, response) =>
            {
                AutokickResponse result;
                if (!TryParseAutokickResponse(code, response, out result)) return;

                StoreAutokickCache(steamId, ip, result);
                if (!result.Kick) return;

                NextFrame(() =>
                {
                    var target = BasePlayer.FindByID(uid);
                    if (target == null || !target.IsConnected) return;
                    KickForAutokick(target, AutokickReason(target.UserIDString, result.Reason));
                });
            });
        }

        private void KickForAutokick(BasePlayer player, string reason)
        {
            if (player == null || !player.IsConnected) return;
            player.Kick(reason);
            Puts($"autokick: {player.displayName} ({player.UserIDString}) — {reason}");
        }

        private string AutokickReason(string steamId, string reason)
        {
            return string.IsNullOrEmpty(reason)
                ? lang.GetMessage("Autokick.Default", this, steamId)
                : reason;
        }

        private string SerializeAutokickRequest(string steamId, string ip)
        {
            return JsonConvert.SerializeObject(new AutokickRequest
            {
                ServerId = _identity.ServerId,
                SteamId = steamId,
                Ip = ip
            }, Formatting.None);
        }

        private static bool TryParseAutokickResponse(int code, string response, out AutokickResponse result)
        {
            result = null;
            if (code != 200 || string.IsNullOrEmpty(response)) return false;
            try { result = JsonConvert.DeserializeObject<AutokickResponse>(response); }
            catch { return false; }
            return result != null;
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

        private void StoreAutokickCache(string steamId, string ip, AutokickResponse result)
        {
            if (result == null) return;
            PruneAutokickCache();
            _autokickCache[steamId] = new AutokickCacheEntry
            {
                Ip = ip,
                Decided = true,
                Kick = result.Kick,
                Reason = result.Reason,
                At = UnityEngine.Time.realtimeSinceStartup
            };
        }

        private void PruneAutokickCache()
        {
            if (_autokickCache.Count < 64) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            var stale = new List<string>();
            foreach (var pair in _autokickCache)
            {
                if (now - pair.Value.At > AutokickCacheTtl) stale.Add(pair.Key);
            }
            foreach (var key in stale) _autokickCache.Remove(key);
        }

        private bool TryPostAutokickBlocking(string steamId, string ip, int timeoutMs, out AutokickResponse result)
        {
            result = null;
            if (_identity == null) return false;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(BuildUrl(AutokickCheckPath));
                request.Method = "POST";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.ContentType = "application/json";
                request.AutomaticDecompression = DecompressionMethods.None;
                request.KeepAlive = false;

                foreach (var header in BuildHeaders(true))
                {
                    if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    request.Headers[header.Key] = header.Value;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(SerializeAutokickRequest(steamId, ip));
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(bytes, 0, bytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null))
                {
                    return TryParseAutokickResponse((int)response.StatusCode, reader.ReadToEnd(), out result);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private List<PlayerDto> SnapshotPlayers()
        {
            var players = new List<PlayerDto>();
            float now = UnityEngine.Time.realtimeSinceStartup;

            try
            {
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
            catch (Exception ex)
            {
                PrintWarning($"player snapshot failed: {ex.Message}");
            }

            return players;
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
                DisplayIp = SafeServerIp(),
                UptimeSeconds = (long)UnityEngine.Time.realtimeSinceStartup,
                MemoryUsedMb = SafeMemoryUsedMb(),
                QueuedCount = queue?.queue?.Count ?? 0,
                JoiningCount = queue?.joining?.Count ?? 0,
                SaveCreatedTime = SafeSaveCreatedTime(),
                PluginVersion = VersionString,
                ProtocolVersion = ProtocolVersion,
                Framework = Framework,
                RustProtocol = SafeRustProtocol(),
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

        private enum SpoolKind { Chat, Reports, Deaths, Alerts }

        private class IdentityData
        {
            public string InstalledAtUtc;
            public string ApiKey;
            public string ServerId;
            public string ProjectId;
            public string PairedAtUtc;
            public string LastError;
        }

        private class MuteInfo
        {
            public string Reason;
            public string ExpiresAt;
        }

        private class MuteStore
        {
            public Dictionary<string, MuteInfo> ActiveMutes = new Dictionary<string, MuteInfo>();
        }

        private class ProcessedTask
        {
            public long ExpiresAt;
            public TaskResult Result;
        }

        private class ProcessedTaskStore
        {
            public Dictionary<string, ProcessedTask> Processed = new Dictionary<string, ProcessedTask>();
        }

        private class SpoolStore
        {
            [JsonProperty("chat")] public List<JToken> Chat = new List<JToken>();
            [JsonProperty("reports")] public List<JToken> Reports = new List<JToken>();
            [JsonProperty("deaths")] public List<JToken> Deaths = new List<JToken>();
            [JsonProperty("alerts")] public List<JToken> Alerts = new List<JToken>();

            public void Normalize()
            {
                if (Chat == null) Chat = new List<JToken>();
                if (Reports == null) Reports = new List<JToken>();
                if (Deaths == null) Deaths = new List<JToken>();
                if (Alerts == null) Alerts = new List<JToken>();
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
        }

        private class DisconnectDto
        {
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
        }

        private class HeartbeatResponse
        {
            [JsonProperty("ok")] public bool Ok { get; set; }
            [JsonProperty("tasks")] public List<QueueTaskDto> Tasks { get; set; }
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

        private class AutokickRequest
        {
            [JsonProperty("serverId")] public string ServerId { get; set; }
            [JsonProperty("steamId")] public string SteamId { get; set; }
            [JsonProperty("ip")] public string Ip { get; set; }
        }

        private class AutokickResponse
        {
            [JsonProperty("kick")] public bool Kick { get; set; }
            [JsonProperty("reason")] public string Reason { get; set; }
        }

        private class AutokickCacheEntry
        {
            public string Ip;
            public bool Decided;
            public bool Kick;
            public string Reason;
            public float At;
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
            private readonly List<T> _items = new List<T>();
            private readonly string _label;
            private int _dropped;
            private float _lastDropWarn = -999f;

            public EventBuffer(string label) { _label = label; }

            public int Count => _items.Count;

            public void Add(T item)
            {
                if (item == null) return;
                if (_items.Count >= MaxBufferItems)
                {
                    _items.RemoveAt(0);
                    _dropped++;
                    WarnOnOverflow();
                }
                _items.Add(item);
            }

            public void AddRange(IEnumerable<T> items)
            {
                if (items == null) return;
                foreach (var item in items) Add(item);
            }

            public List<T> Take(int max)
            {
                int count = Math.Min(_items.Count, max);
                var batch = _items.GetRange(0, count);
                _items.RemoveRange(0, count);
                return batch;
            }

            public void Requeue(List<T> batch)
            {
                if (batch == null || batch.Count == 0) return;
                _items.InsertRange(0, batch);
                TrimOverflow();
            }

            public List<T> DrainAll()
            {
                var all = new List<T>(_items);
                _items.Clear();
                return all;
            }

            private void TrimOverflow()
            {
                while (_items.Count > MaxBufferItems)
                {
                    _items.RemoveAt(0);
                    _dropped++;
                }
                if (_dropped > 0) WarnOnOverflow();
            }

            private void WarnOnOverflow()
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastDropWarn < DropWarnInterval) return;
                _lastDropWarn = now;
                Warn($"{_label} buffer is full ({MaxBufferItems}); {_dropped} event(s) dropped so far");
                Instance?._engine?.RaiseAlert("buffer_overflow", $"{_label} dropped {_dropped} events", new { buffer = _label, dropped = _dropped });
            }
        }

        #endregion

        #region Utils

        private static bool IsRealSteamId(ulong id) => id >= SteamIdBase;

        private static bool IsRealSteamId(string id)
        {
            ulong steamId;
            return ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out steamId)
                && IsRealSteamId(steamId);
        }

        private static string NewEventId() => Guid.NewGuid().ToString("N");

        private static string UtcNow() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static void Log(string message) => Instance?.Puts(message);

        private static void Warn(string message) => Instance?.PrintWarning(message);

        private static string Excerpt(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= 300 ? value : value.Substring(0, 300);
        }

        private static string ConnectionName(string username, string fallback) =>
            string.IsNullOrEmpty(username) || username == "<blank>" ? fallback : username;

        private static T Parse<T>(JToken raw) where T : class
        {
            if (raw == null) return null;
            try { return raw.ToObject<T>(); }
            catch (Exception ex)
            {
                Warn($"task payload could not be read as {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static string CleanIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "0.0.0.0";
            int index = ip.LastIndexOf(':');
            return index != -1 ? ip.Substring(0, index) : ip;
        }

        private static string SafeServerIp()
        {
            try
            {
                var ip = ConVar.Server.ip;
                return string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0" ? null : ip;
            }
            catch { return null; }
        }

        private static long SafeMemoryUsedMb()
        {
            try { return (long)Performance.current.memoryUsageSystem; }

            catch { return System.GC.GetTotalMemory(false) / 1048576; }
        }

        private static string SafeSaveCreatedTime()
        {
            try { return SaveRestore.SaveCreatedTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string SafeRustProtocol()
        {
            try { return Rust.Protocol.printable; }
            catch { return null; }
        }

        private static string WeaponName(HitInfo info)
        {
            if (info == null) return "unknown";
            return info.Weapon?.ShortPrefabName ?? info.WeaponPrefab?.ShortPrefabName ?? "unknown";
        }

        private static string BodyPart(HitInfo info)
        {
            if (info == null) return "unknown";
            try { return info.boneArea.ToString(); }
            catch { return "unknown"; }
        }

        private static string MajorityDamage(HitInfo info)
        {
            if (info?.damageTypes == null) return "Unknown";
            try { return info.damageTypes.GetMajorityDamageType().ToString(); }
            catch { return "Unknown"; }
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
            var stale = _woundedHits
                .Where(entry => now - entry.Value.RecordedAt > WoundRecordTtlSeconds)
                .Select(entry => entry.Key)
                .ToList();

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
                int channel;
                if (int.TryParse(rest.Substring(0, space), out channel) && channel >= 0 && channel <= 2)
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

            return IsMuted(player.UserIDString, out reason, out timeRemaining);
        }

        private bool IsMuted(string steamId, out string reason, out string timeRemaining)
        {
            reason = null;
            timeRemaining = null;

            if (string.IsNullOrEmpty(steamId) || _mutes?.ActiveMutes == null) return false;

            MuteInfo mute;
            if (!_mutes.ActiveMutes.TryGetValue(steamId, out mute)) return false;

            var expires = ParseExpiry(mute.ExpiresAt);
            if (expires != null && DateTimeOffset.UtcNow > expires.Value)
            {
                ClearMute(steamId, true);
                return false;
            }

            reason = mute.Reason ?? "Violation";
            timeRemaining = FormatTimeRemaining(expires);
            return true;
        }

        private void ApplyMute(string steamId, string reason, string expiresAt, string playerName)
        {
            Puts($"muting {steamId} ({playerName ?? "unknown"}): {reason}, expires {expiresAt ?? "never"}");

            _mutes.ActiveMutes[steamId] = new MuteInfo { Reason = reason, ExpiresAt = expiresAt };
            SaveMutes();

            string timeLeft = FormatTimeRemaining(ParseExpiry(expiresAt));

            ulong uid;
            if (ulong.TryParse(steamId, out uid))
            {
                var player = BasePlayer.FindByID(uid);
                if (player != null && player.IsConnected)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, true);
                    string notice = Message("Mute.Applied", player, reason, timeLeft);
                    SendToast(player, notice);
                    player.ChatMessage(notice);
                }
            }
        }

        private void ClearMute(string steamId, bool silent)
        {
            bool removed = _mutes.ActiveMutes.Remove(steamId);
            if (removed) SaveMutes();

            ulong uid;
            if (!ulong.TryParse(steamId, out uid)) return;

            var player = BasePlayer.FindByID(uid);
            if (player == null || !player.IsConnected) return;

            player.SetPlayerFlag(BasePlayer.PlayerFlags.ChatMute, false);
            if (silent || !removed) return;

            string notice = Message("Mute.Removed", player);
            SendToast(player, notice);
            player.ChatMessage(notice);
        }

        private int SweepExpiredMutes()
        {
            if (_mutes?.ActiveMutes == null || _mutes.ActiveMutes.Count == 0) return 0;

            var expired = _mutes.ActiveMutes
                .Where(entry =>
                {
                    var expires = ParseExpiry(entry.Value.ExpiresAt);
                    return expires != null && DateTimeOffset.UtcNow > expires.Value;
                })
                .Select(entry => entry.Key)
                .ToList();

            foreach (var steamId in expired) ClearMute(steamId, true);
            return expired.Count;
        }

        private DateTimeOffset? ParseExpiry(string expiresAt)
        {
            if (string.IsNullOrWhiteSpace(expiresAt)) return null;

            long unixValue;
            if (long.TryParse(expiresAt, out unixValue))
            {
                return unixValue > 32503680000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                    : DateTimeOffset.FromUnixTimeSeconds(unixValue);
            }

            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(expiresAt, null, DateTimeStyles.RoundtripKind, out parsed)) return parsed;

            PrintWarning($"could not parse expiry '{expiresAt}', treating as permanent");
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
                player.SendConsoleCommand("chat.add", channel, _config.PanelAvatarSteamId, message);
            }
        }

        #endregion
    }
}
