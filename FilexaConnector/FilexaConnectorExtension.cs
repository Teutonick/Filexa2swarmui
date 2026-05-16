using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Filexa.Extensions.FilexaConnector;

public class FilexaConnectorExtension : Extension
{
    private const string ConnectorVersion = "1.1";

    public static PermInfo PermFilexaConnector = Permissions.Register(new(
        "filexa_connector_configure",
        "[Filexa Connector] Configure connector",
        "Allows configuring the Filexa local generation connector.",
        PermissionDefault.ADMINS,
        Permissions.GroupAdmin
    ));

    private static readonly HttpClient Http = new();
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HeartbeatDelay = TimeSpan.FromSeconds(60);
    private CancellationTokenSource? _cancel;
    private FilexaConnectorConfig _config = new();

    public FilexaConnectorExtension()
    {
        ApplyMetadata();
    }

    public override void OnFirstInit()
    {
        ApplyMetadata(renameForDisplay: true);
    }

    public override void OnPreInit()
    {
        ApplyMetadata();
        ScriptFiles.Add("Assets/filexa_connector.js");
        StyleSheetFiles.Add("Assets/filexa_connector.css");
    }

    public override void PopulateMetadata()
    {
        // Keep local installs informative even when the extension folder is copied without git.
        ApplyMetadata(renameForDisplay: true);
    }

    private void ApplyMetadata(bool renameForDisplay = false)
    {
        if (renameForDisplay)
        {
            ExtensionName = "FilexaConnector";
        }
        Version = ConnectorVersion;
        Tags = ["api", "backend"];
        ExtensionAuthor = "Filexa";
        Description = "Connects SwarmUI to Filexa for local Telegram text-to-image and image-to-image generation.";
        ReadmeURL = "https://t.me/WorkOnBigFilesBot";
        License = "MIT";
    }

    public override void OnInit()
    {
        API.RegisterAPICall(GetFilexaConnectorConfig, false, PermFilexaConnector);
        API.RegisterAPICall(SaveFilexaConnectorConfig, true, PermFilexaConnector);
        API.RegisterAPICall(DisconnectFilexaConnector, true, PermFilexaConnector);
        _config = LoadConfig();
        _cancel = new CancellationTokenSource();
        _ = Task.Run(() => WorkerLoop(_cancel.Token));
        Logs.Init("[Filexa Connector] Extension loaded");
    }

    public Task<JObject> GetFilexaConnectorConfig(Session session)
    {
        return Task.FromResult(new JObject
        {
            ["enabled"] = _config.Enabled,
            ["api_url"] = _config.ApiUrl,
            ["swarm_url"] = _config.SwarmUrl,
            ["has_token"] = !string.IsNullOrWhiteSpace(_config.Token),
            ["status"] = _config.Status,
            ["last_event"] = _config.LastEvent,
            ["active_job_id"] = _config.ActiveJobId,
            ["active_kind"] = _config.ActiveKind,
            ["active_prompt_preview"] = _config.ActivePromptPreview,
            ["started_at_utc"] = _config.StartedAtUtc,
            ["updated_at_utc"] = _config.UpdatedAtUtc,
            ["poll_count"] = _config.PollCount,
            ["last_duration_seconds"] = _config.LastDurationSeconds,
            ["last_error"] = _config.LastError,
            ["debug_logging"] = _config.DebugLogging,
            ["server_time_utc"] = DateTime.UtcNow.ToString("O"),
        });
    }

    public Task<JObject> SaveFilexaConnectorConfig(Session session, string api_url, string token, string swarm_url, bool enabled, bool debug_logging = false)
    {
        _config.ApiUrl = CleanBaseUrl(api_url);
        _config.SwarmUrl = string.IsNullOrWhiteSpace(swarm_url) ? "http://127.0.0.1:7801" : CleanBaseUrl(swarm_url);
        if (!string.IsNullOrWhiteSpace(token))
        {
            _config.Token = token.Trim();
        }
        _config.Enabled = enabled;
        _config.DebugLogging = debug_logging;
        _config.Status = enabled ? "enabled" : "disabled";
        _config.LastEvent = enabled ? "Configuration saved" : "Connector disabled";
        _config.LastError = "";
        ClearActiveJob();
        SaveConfig(_config);
        return GetFilexaConnectorConfig(session);
    }

    public Task<JObject> DisconnectFilexaConnector(Session session)
    {
        _config.Enabled = false;
        _config.Token = "";
        _config.DebugLogging = false;
        _config.Status = "disabled";
        _config.LastEvent = "Disconnected";
        _config.LastError = "";
        ClearActiveJob();
        SaveConfig(_config);
        return GetFilexaConnectorConfig(session);
    }

    private async Task WorkerLoop(CancellationToken cancel)
    {
        DateTime lastHeartbeat = DateTime.MinValue;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.Token) || string.IsNullOrWhiteSpace(_config.ApiUrl))
                {
                    await Task.Delay(HeartbeatDelay, cancel);
                    continue;
                }
                if (DateTime.UtcNow - lastHeartbeat > HeartbeatDelay)
                {
                    await PostJson($"{_config.ApiUrl}/local/v1/heartbeat", new JObject
                    {
                        ["client_name"] = "SwarmUI Filexa Connector",
                        ["client_version"] = ConnectorVersion,
                        ["status"] = _config.Status,
                    }, cancel);
                    lastHeartbeat = DateTime.UtcNow;
                }
                JObject poll = await PostJson($"{_config.ApiUrl}/local/v1/tasks/poll", new JObject
                {
                    ["client_name"] = "SwarmUI Filexa Connector",
                    ["client_version"] = ConnectorVersion,
                }, cancel);
                _config.PollCount++;
                _config.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                JObject? task = poll["task"] as JObject;
                if (task is not null)
                {
                    // The connector pulls exactly one task at a time; Filexa also enforces one
                    // active local job per Telegram user, so retries cannot overlap generations.
                    await RunTask(task, cancel);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _config.Status = "error";
                _config.LastEvent = ex.Message;
                _config.LastError = ex.Message;
                _config.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                SaveConfig(_config);
                Logs.Warning($"[Filexa Connector] Worker error: {ex.Message}");
            }
            await Task.Delay(PollDelay, cancel);
        }
    }

    private async Task RunTask(JObject task, CancellationToken cancel)
    {
        string jobId = task["job_id"]?.ToString() ?? "";
        DateTime startedAt = DateTime.UtcNow;
        try
        {
            StartActiveJob(task, startedAt);
            SaveConfig(_config);

            JObject parameters = (task["params"] as JObject) ?? new JObject();
            parameters["prompt"] = task["prompt"]?.ToString() ?? "";
            DebugLog($"Task {jobId}: kind={_config.ActiveKind}, prompt_len={parameters["prompt"]?.ToString().Length ?? 0}");
            SetStatus("opening SwarmUI session", $"Task {jobId}: opening SwarmUI session");
            parameters["session_id"] = await GetSwarmSession(cancel);
            // References are passed to SwarmUI as transient data URLs, then Filexa receives only
            // the final generated image bytes through the result upload endpoint.
            SetStatus("downloading references", $"Task {jobId}: downloading references");
            await AttachReferences(parameters, task["references"] as JArray, cancel);
            DebugLog($"Task {jobId}: Swarm parameter keys={string.Join(", ", parameters.Properties().Select(p => p.Name))}");

            SetStatus("generating in SwarmUI", $"Task {jobId}: SwarmUI generation started");
            JObject generation = await PostJson($"{_config.SwarmUrl}/API/GenerateText2Image", parameters, cancel, authorizeFilexa: false);
            DebugLog($"Task {jobId}: Swarm response image_count={(generation["images"] as JArray)?.Count ?? 0}");
            SetStatus("reading result", $"Task {jobId}: reading generated image");
            byte[] image = await ReadGeneratedImage(generation, cancel);
            SetStatus("uploading result", $"Task {jobId}: uploading result to Filexa");
            await UploadResult(task["result_upload_url"]?.ToString() ?? "", image, cancel);

            _config.LastDurationSeconds = Math.Round((DateTime.UtcNow - startedAt).TotalSeconds, 1);
            _config.Status = "idle";
            _config.LastEvent = $"Task {jobId} completed in {_config.LastDurationSeconds:0.0}s";
            _config.LastError = "";
            ClearActiveJob();
            SaveConfig(_config);
        }
        catch (Exception ex)
        {
            await ReportFailure(task["failure_url"]?.ToString() ?? "", ex.Message, cancel);
            _config.Status = "error";
            _config.LastEvent = $"Task {jobId} failed: {ex.Message}";
            _config.LastError = ex.Message;
            ClearActiveJob();
            SaveConfig(_config);
            throw;
        }
    }

    private async Task AttachReferences(JObject parameters, JArray? references, CancellationToken cancel)
    {
        if (references is null || references.Count == 0)
        {
            return;
        }
        JArray dataUrls = new();
        for (int i = 0; i < references.Count; i++)
        {
            JObject item = (JObject)references[i]!;
            string url = AbsoluteFilexaUrl(item["url"]?.ToString() ?? "");
            byte[] bytes = await GetBytes(url, cancel, authorizeFilexa: true);
            string mime = item["mime_type"]?.ToString() ?? "image/jpeg";
            DebugLog($"Downloaded reference {i}: mime={mime}, bytes={bytes.Length}");
            dataUrls.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
        }
        parameters["promptimages"] = dataUrls;
        SetStatus("references ready", $"Downloaded {references.Count} reference image(s)");
    }

    private async Task<string> GetSwarmSession(CancellationToken cancel)
    {
        JObject session = await PostJson($"{_config.SwarmUrl}/API/GetNewSession", new JObject(), cancel, authorizeFilexa: false);
        return session["session_id"]?.ToString() ?? throw new InvalidDataException("SwarmUI did not return session_id");
    }

    private async Task<byte[]> ReadGeneratedImage(JObject generation, CancellationToken cancel)
    {
        JArray images = (generation["images"] as JArray) ?? throw new InvalidDataException("SwarmUI returned no images");
        string first = images.First?.ToString() ?? "";
        if (first.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromBase64String(first.Split(',', 2)[1]);
        }
        return await GetBytes(AbsoluteSwarmUrl(first), cancel, authorizeFilexa: false);
    }

    private async Task UploadResult(string path, byte[] image, CancellationToken cancel)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent content = new(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "image", "filexa-local.png");
        using HttpRequestMessage request = new(HttpMethod.Post, AbsoluteFilexaUrl(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        request.Content = form;
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
    }

    private async Task ReportFailure(string path, string error, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        await PostJson(AbsoluteFilexaUrl(path), new JObject { ["error"] = error }, cancel);
    }

    private async Task<JObject> PostJson(string url, JObject body, CancellationToken cancel, bool authorizeFilexa = true)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        if (authorizeFilexa)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
        request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(cancel);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    private async Task<byte[]> GetBytes(string url, CancellationToken cancel, bool authorizeFilexa)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (authorizeFilexa)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancel);
    }

    private string AbsoluteFilexaUrl(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return $"{_config.ApiUrl}/{path.TrimStart('/')}";
    }

    private string AbsoluteSwarmUrl(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return new Uri(new Uri($"{_config.SwarmUrl}/"), path.TrimStart('/')).ToString();
    }

    private FilexaConnectorConfig LoadConfig()
    {
        string path = ConfigPath();
        if (!File.Exists(path))
        {
            return new FilexaConnectorConfig();
        }
        return JObject.Parse(File.ReadAllText(path)).ToObject<FilexaConnectorConfig>() ?? new FilexaConnectorConfig();
    }

    private void SaveConfig(FilexaConnectorConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath())!);
        File.WriteAllText(ConfigPath(), JObject.FromObject(config).ToString());
    }

    private string ConfigPath() => Path.Combine(FilePath, "Data", "filexa-connector.json");

    private static string CleanBaseUrl(string value) => value.Trim().TrimEnd('/');

    private void StartActiveJob(JObject task, DateTime startedAt)
    {
        string jobId = task["job_id"]?.ToString() ?? "";
        _config.ActiveJobId = jobId;
        _config.ActiveKind = task["kind"]?.ToString() ?? "";
        _config.ActivePromptPreview = ShortPreview(task["prompt"]?.ToString() ?? "", 140);
        _config.StartedAtUtc = startedAt.ToString("O");
        _config.UpdatedAtUtc = startedAt.ToString("O");
        _config.LastDurationSeconds = 0;
        _config.LastError = "";
        _config.Status = "task received";
        _config.LastEvent = $"Task {jobId} received";
    }

    private void SetStatus(string status, string lastEvent)
    {
        _config.Status = status;
        _config.LastEvent = lastEvent;
        _config.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
    }

    private void ClearActiveJob()
    {
        _config.ActiveJobId = "";
        _config.ActiveKind = "";
        _config.ActivePromptPreview = "";
        _config.StartedAtUtc = "";
        _config.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
    }

    private static string ShortPreview(string value, int limit)
    {
        string clean = string.Join(" ", value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= limit ? clean : $"{clean[..limit]}...";
    }

    private void DebugLog(string message)
    {
        if (_config.DebugLogging)
        {
            Logs.Info($"[Filexa Connector] {message}");
        }
    }
}

public class FilexaConnectorConfig
{
    public bool Enabled { get; set; }
    public string ApiUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string SwarmUrl { get; set; } = "http://127.0.0.1:7801";
    public string Status { get; set; } = "disabled";
    public string LastEvent { get; set; } = "";
    public string ActiveJobId { get; set; } = "";
    public string ActiveKind { get; set; } = "";
    public string ActivePromptPreview { get; set; } = "";
    public string StartedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public long PollCount { get; set; }
    public double LastDurationSeconds { get; set; }
    public string LastError { get; set; } = "";
    public bool DebugLogging { get; set; }
}
