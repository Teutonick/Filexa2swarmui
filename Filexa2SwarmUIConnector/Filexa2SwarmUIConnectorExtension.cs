using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp.Formats.Jpeg;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Filexa.Extensions.Filexa2SwarmUIConnector;

public class Filexa2SwarmUIConnectorExtension : Extension
{
    private const string ConnectorVersion = "1.2";
    private const string ConnectorName = "Filexa2SwarmUI Connector";
    private const int MaxPromptChars = 8000;
    private const int MaxReferenceCount = 4;
    private const int MaxJsonResponseBytes = 1024 * 1024;
    private const int MaxImageBytes = 40 * 1024 * 1024;
    private const int UploadChunkBytes = 50 * 1024;
    private const int CompressionStartBytes = 768 * 1024;
    private const int UploadAttempts = 3;
    private static readonly TimeSpan DirectUploadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChunkUploadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FilexaJsonTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusUpdateTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SwarmSessionTimeout = TimeSpan.FromSeconds(30);
    private const int UploadJpegQuality = 80;
    private static readonly Regex JobIdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ModelCodePattern = new("^[A-Za-z0-9][A-Za-z0-9 _./\\\\:\\-()]{0,199}$", RegexOptions.Compiled);

    public static PermInfo PermFilexa2SwarmUIConnector = Permissions.Register(new(
        "filexa2swarmui_connector_configure",
        "[Filexa2SwarmUI Connector] Configure connector",
        "Allows configuring the Filexa2SwarmUI local generation connector.",
        PermissionDefault.ADMINS,
        Permissions.GroupAdmin
    ));

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(10);
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _activeTaskCancel;
    private string _activeCancelPath = "";
    private Filexa2SwarmUIConnectorConfig _config = new();

    public Filexa2SwarmUIConnectorExtension()
    {
        ApplyMetadata();
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new(new SocketsHttpHandler
        {
            Expect100ContinueTimeout = TimeSpan.Zero,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.ExpectContinue = false;
        return client;
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
        ApplyMetadata(renameForDisplay: true);
    }

    private void ApplyMetadata(bool renameForDisplay = false)
    {
        if (renameForDisplay)
        {
            ExtensionName = "Filexa2SwarmUIConnector";
        }
        Version = ConnectorVersion;
        Tags = ["api", "backend"];
        ExtensionAuthor = "Filexa";
        Description = "Connects SwarmUI to Filexa through Filexa2SwarmUI Connector for local Telegram image generation.";
        ReadmeURL = "https://github.com/Teutonick/Filexa2swarmui";
        License = "MIT";
    }

    public override void OnInit()
    {
        API.RegisterAPICall(GetFilexa2SwarmUIConnectorConfig, false, PermFilexa2SwarmUIConnector);
        API.RegisterAPICall(SaveFilexa2SwarmUIConnectorConfig, true, PermFilexa2SwarmUIConnector);
        API.RegisterAPICall(DisconnectFilexa2SwarmUIConnector, true, PermFilexa2SwarmUIConnector);
        API.RegisterAPICall(CancelFilexa2SwarmUIConnectorTask, true, PermFilexa2SwarmUIConnector);
        _config = LoadConfig();
        if (!string.IsNullOrWhiteSpace(_config.ActiveJobId))
        {
            ClearActiveJob();
            _config.Status = _config.Enabled ? "enabled" : "disabled";
            _config.LastEvent = "Recovered after SwarmUI restart";
            SaveConfig(_config);
        }
        _cancel = new CancellationTokenSource();
        _ = Task.Run(() => WorkerLoop(_cancel.Token));
        Logs.Init($"[{ConnectorName}] Extension loaded");
    }

    public Task<JObject> GetFilexa2SwarmUIConnectorConfig(Session session)
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
            ["compress_images_before_upload"] = _config.CompressImagesBeforeUpload,
            ["server_time_utc"] = DateTime.UtcNow.ToString("O"),
        });
    }

    public Task<JObject> SaveFilexa2SwarmUIConnectorConfig(
        Session session,
        string api_url,
        string token,
        string swarm_url,
        bool enabled,
        bool debug_logging = false,
        bool compress_images_before_upload = true
    )
    {
        _config.ApiUrl = string.IsNullOrWhiteSpace(api_url) && !enabled
            ? ""
            : CleanBaseUrl(api_url, "Filexa API URL");
        _config.SwarmUrl = string.IsNullOrWhiteSpace(swarm_url)
            ? "http://127.0.0.1:7801"
            : CleanBaseUrl(swarm_url, "SwarmUI URL");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _config.Token = token.Trim();
        }
        _config.Enabled = enabled;
        _config.DebugLogging = debug_logging;
        _config.CompressImagesBeforeUpload = compress_images_before_upload;
        _config.Status = enabled ? "enabled" : "disabled";
        _config.LastEvent = enabled ? "Configuration saved" : "Connector disabled";
        _config.LastError = "";
        ClearActiveJob();
        SaveConfig(_config);
        return GetFilexa2SwarmUIConnectorConfig(session);
    }

    public Task<JObject> DisconnectFilexa2SwarmUIConnector(Session session)
    {
        _activeTaskCancel?.Cancel();
        _config.Enabled = false;
        _config.Token = "";
        _config.DebugLogging = false;
        _config.CompressImagesBeforeUpload = true;
        _config.Status = "disabled";
        _config.LastEvent = "Disconnected";
        _config.LastError = "";
        ClearActiveJob();
        SaveConfig(_config);
        return GetFilexa2SwarmUIConnectorConfig(session);
    }

    public Task<JObject> CancelFilexa2SwarmUIConnectorTask(Session session)
    {
        string jobId = _config.ActiveJobId;
        if (string.IsNullOrWhiteSpace(jobId))
        {
            _config.Status = _config.Enabled ? "enabled" : "disabled";
            _config.LastEvent = "No active task to cancel";
            SaveConfig(_config);
            return GetFilexa2SwarmUIConnectorConfig(session);
        }
        _config.Status = "canceling";
        _config.LastEvent = $"Cancel requested for task {jobId}";
        _config.LastError = "";
        SaveConfig(_config);
        _activeTaskCancel?.Cancel();
        string cancelPath = _activeCancelPath;
        if (!string.IsNullOrWhiteSpace(cancelPath))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReportCancelSafe(cancelPath, "Canceled in Filexa2SwarmUI Connector", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DebugLog($"Cancel report skipped: {ex.Message}");
                }
            });
        }
        return GetFilexa2SwarmUIConnectorConfig(session);
    }

    private async Task WorkerLoop(CancellationToken cancel)
    {
        int consecutiveErrors = 0;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.Token) || string.IsNullOrWhiteSpace(_config.ApiUrl))
                {
                    await Task.Delay(PollDelay, cancel);
                    continue;
                }
                JObject poll = await PostJson($"{_config.ApiUrl}/local/v1/tasks/poll", new JObject
                {
                    ["client_name"] = ConnectorName,
                    ["client_version"] = ConnectorVersion,
                    ["status"] = _config.Status,
                }, cancel, timeout: FilexaJsonTimeout);
                consecutiveErrors = 0;
                _config.PollCount++;
                _config.Status = "enabled";
                _config.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                SaveConfig(_config);
                JObject? task = poll["task"] as JObject;
                if (task is not null)
                {
                    await RunTask(task, cancel);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FilexaUnauthorizedException ex)
            {
                StopAfterUnauthorized(ex.Message);
                consecutiveErrors = 0;
            }
            catch (Exception ex) when (IsFilexaServerUnavailable(ex))
            {
                StopAfterServerUnavailable("Filexa server is unavailable; check the API URL, server, network path, and connect again.");
                consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _config.Status = "waiting after error";
                _config.LastEvent = ex.Message;
                _config.LastError = ex.Message;
                _config.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                SaveConfig(_config);
                Logs.Warning($"[{ConnectorName}] Worker error: {ex.Message}");
            }
            await Task.Delay(BackoffDelay(consecutiveErrors), cancel);
        }
    }

    private async Task RunTask(JObject task, CancellationToken cancel)
    {
        string jobId = ValidateTask(task);
        DateTime startedAt = DateTime.UtcNow;
        DateTime deadline = ParseDeadline(task["deadline_at"]?.ToString());
        using CancellationTokenSource taskCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        CancellationToken taskToken = taskCancel.Token;
        _activeTaskCancel = taskCancel;
        try
        {
            StartActiveJob(task, startedAt);
            SaveConfig(_config);

            JObject parameters = BuildSwarmParameters(task);
            DebugLog($"Task {jobId}: kind={_config.ActiveKind}, prompt_len={parameters["prompt"]?.ToString().Length ?? 0}");

            await SetTaskStatus(task, "opening SwarmUI session", $"Task {jobId}: opening SwarmUI session", 12, taskToken);
            parameters["session_id"] = await GetSwarmSession(taskToken);

            await SetTaskStatus(task, "downloading references", $"Task {jobId}: downloading references", 18, taskToken);
            await AttachReferences(parameters, task["references"] as JArray, task, taskToken);
            DebugLog($"Task {jobId}: Swarm parameter keys={string.Join(", ", parameters.Properties().Select(p => p.Name))}");

            await SetTaskStatus(task, "generating in SwarmUI", $"Task {jobId}: SwarmUI generation started", 26, taskToken);
            DebugLog($"Task {jobId}: sending GenerateText2Image to SwarmUI");
            JObject generation = await PostJson($"{_config.SwarmUrl}/API/GenerateText2Image", parameters, taskToken, authorizeFilexa: false);
            DebugLog($"Task {jobId}: Swarm response image_count={(generation["images"] as JArray)?.Count ?? 0}");

            await SetTaskStatus(task, "reading result", $"Task {jobId}: reading generated image", 88, taskToken);
            byte[] image = await ReadGeneratedImage(generation, taskToken);
            if (image.Length > MaxImageBytes)
            {
                throw new InvalidDataException("Generated image is too large");
            }
            UploadPayload upload = PrepareUploadPayload(image);

            await SetTaskStatus(task, "uploading result", $"Task {jobId}: uploading result to Filexa ({upload.Bytes.Length} bytes)", 94, taskToken);
            await UploadResultWithRetry(task, task["result_upload_url"]?.ToString() ?? "", upload, deadline, taskToken);

            _config.LastDurationSeconds = Math.Round((DateTime.UtcNow - startedAt).TotalSeconds, 1);
            _config.Status = "idle";
            _config.LastEvent = $"Task {jobId} completed in {_config.LastDurationSeconds:0.0}s";
            _config.LastError = "";
            ClearActiveJob();
            SaveConfig(_config);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            await ReportCancelSafe(task["cancel_url"]?.ToString() ?? "", "Canceled in Filexa2SwarmUI Connector", CancellationToken.None);
            _config.Status = _config.Enabled ? "idle" : "disabled";
            _config.LastEvent = $"Task {jobId} canceled";
            _config.LastError = "";
            ClearActiveJob();
            SaveConfig(_config);
            Logs.Warning($"[{ConnectorName}] Task {jobId} canceled");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FilexaUnauthorizedException)
        {
            ClearActiveJob();
            SaveConfig(_config);
            throw;
        }
        catch (FilexaHttpException ex) when (ex.StatusCode == HttpStatusCode.Gone)
        {
            _config.Status = "idle";
            _config.LastEvent = $"Task {jobId} is no longer waiting in Filexa";
            _config.LastError = "";
            ClearActiveJob();
            SaveConfig(_config);
            Logs.Warning($"[{ConnectorName}] Task {jobId} is no longer waiting in Filexa");
        }
        catch (Exception ex)
        {
            await ReportFailureSafe(task["failure_url"]?.ToString() ?? "", ex.Message, cancel);
            _config.Status = "idle";
            _config.LastEvent = $"Task {jobId} failed: {ex.Message}";
            _config.LastError = ex.Message;
            ClearActiveJob();
            SaveConfig(_config);
            Logs.Warning($"[{ConnectorName}] Task {jobId} failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_activeTaskCancel, taskCancel))
            {
                _activeTaskCancel = null;
            }
            _activeCancelPath = "";
        }
    }

    private async Task AttachReferences(JObject parameters, JArray? references, JObject task, CancellationToken cancel)
    {
        if (references is null || references.Count == 0)
        {
            return;
        }
        if (references.Count > MaxReferenceCount)
        {
            throw new InvalidDataException("Too many reference images");
        }
        JArray dataUrls = new();
        for (int i = 0; i < references.Count; i++)
        {
            if (references[i] is not JObject item)
            {
                throw new InvalidDataException("Invalid reference descriptor");
            }
            string url = AbsoluteFilexaUrl(item["url"]?.ToString() ?? "");
            string mime = ValidateImageMime(item["mime_type"]?.ToString() ?? "image/jpeg");
            byte[] bytes = await GetBytes(url, cancel, authorizeFilexa: true, maxBytes: MaxImageBytes);
            DebugLog($"Downloaded reference {i}: mime={mime}, bytes={bytes.Length}");
            dataUrls.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
            await SetTaskStatus(task, "downloading references", $"Downloaded reference {i + 1}/{references.Count}", 18 + (i + 1) * 3, cancel);
        }
        parameters["promptimages"] = dataUrls;
        SetStatus("references ready", $"Downloaded {references.Count} reference image(s)");
    }

    private async Task<string> GetSwarmSession(CancellationToken cancel)
    {
        JObject session = await PostJson(
            $"{_config.SwarmUrl}/API/GetNewSession",
            new JObject(),
            cancel,
            authorizeFilexa: false,
            timeout: SwarmSessionTimeout
        );
        return session["session_id"]?.ToString() ?? throw new InvalidDataException("SwarmUI did not return session_id");
    }

    private async Task<byte[]> ReadGeneratedImage(JObject generation, CancellationToken cancel)
    {
        JArray images = (generation["images"] as JArray) ?? throw new InvalidDataException("SwarmUI returned no images");
        string first = images.First?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(first))
        {
            throw new InvalidDataException("SwarmUI returned an empty image URL");
        }
        if (first.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = first.IndexOf(',');
            if (comma < 0 || !first[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SwarmUI returned an invalid data URL");
            }
            byte[] data = Convert.FromBase64String(first[(comma + 1)..]);
            if (data.Length > MaxImageBytes)
            {
                throw new InvalidDataException("Generated image is too large");
            }
            return data;
        }
        return await GetBytes(AbsoluteSwarmUrl(first), cancel, authorizeFilexa: false, maxBytes: MaxImageBytes);
    }

    private UploadPayload PrepareUploadPayload(byte[] image)
    {
        string originalMime = DetectImageMime(image);
        if (!_config.CompressImagesBeforeUpload || image.Length < CompressionStartBytes)
        {
            return new UploadPayload(image, originalMime);
        }
        try
        {
            byte[] jpeg = ConvertToJpeg(image, UploadJpegQuality);
            DebugLog($"Converted image to JPEG before upload: {image.Length} -> {jpeg.Length} bytes, jpeg quality={UploadJpegQuality}");
            return new UploadPayload(jpeg, "image/jpeg");
        }
        catch (Exception ex)
        {
            DebugLog($"JPEG conversion skipped: {ex.Message}");
        }
        return new UploadPayload(image, originalMime);
    }

    private static byte[] ConvertToJpeg(byte[] image, int quality)
    {
        using SixLabors.ImageSharp.Image decoded = SixLabors.ImageSharp.Image.Load(image);
        using MemoryStream output = new();
        decoded.Save(output, new JpegEncoder { Quality = quality });
        return output.ToArray();
    }

    private async Task UploadResultWithRetry(JObject task, string path, UploadPayload image, DateTime deadline, CancellationToken cancel)
    {
        try
        {
            await SetTaskStatus(
                task,
                "uploading result",
                $"Upload attempt 1/1 to Filexa ({image.Bytes.Length} bytes)",
                94,
                cancel
            );
            DebugLog($"Upload attempt 1/1: bytes={image.Bytes.Length}, mime={image.MimeType}");
            await UploadResult(path, image.Bytes, image.MimeType, DirectUploadTimeout, cancel);
            return;
        }
        catch (FilexaUnauthorizedException)
        {
            throw;
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested && IsTransient(ex) && DateTime.UtcNow + TimeSpan.FromSeconds(5) < deadline)
        {
            DebugLog($"Direct upload failed, switching to chunked upload: {ex.Message}");
            await PostTaskStatusSafe(
                task["status_url"]?.ToString() ?? "",
                "direct upload failed; switching to chunked upload",
                94,
                cancel
            );
        }
        await UploadResultChunksWithRetry(task, path, image, deadline, cancel);
    }

    private async Task UploadResult(string path, byte[] image, string mimeType, TimeSpan timeout, CancellationToken cancel)
    {
        ByteArrayContent content = new(image);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Headers.ContentLength = image.Length;
        using HttpRequestMessage request = new(HttpMethod.Post, AbsoluteFilexaUrl(path));
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.ExpectContinue = false;
        request.Headers.Add("X-Filexa-Connector-Version", ConnectorVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        request.Content = content;
        using CancellationTokenSource timeoutCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeoutCancel.CancelAfter(timeout);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCancel.Token);
        await EnsureSuccess(response, authorizeFilexa: true, cancel);
        DebugLog($"Upload response: {(int)response.StatusCode} {response.StatusCode}");
    }

    private async Task UploadResultChunksWithRetry(JObject task, string resultPath, UploadPayload image, DateTime deadline, CancellationToken cancel)
    {
        string chunkBasePath = task["result_chunk_upload_url"]?.ToString() ?? $"{resultPath.TrimEnd('/')}/chunks";
        AbsoluteFilexaUrl(chunkBasePath);
        string uploadId = Guid.NewGuid().ToString("N");
        int chunkCount = Math.Max(1, (image.Bytes.Length + UploadChunkBytes - 1) / UploadChunkBytes);
        DebugLog($"Chunked upload started: upload_id={uploadId}, chunks={chunkCount}, bytes={image.Bytes.Length}, mime={image.MimeType}");
        for (int index = 0; index < chunkCount; index++)
        {
            int offset = index * UploadChunkBytes;
            int length = Math.Min(UploadChunkBytes, image.Bytes.Length - offset);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(image.Bytes, offset, chunk, 0, length);
            Exception? lastError = null;
        for (int attempt = 1; attempt <= UploadAttempts; attempt++)
            {
                if (DateTime.UtcNow + TimeSpan.FromSeconds(5) >= deadline)
                {
                    throw new TimeoutException("Filexa task deadline elapsed before upload completed");
                }
                try
                {
                    int progress = 94 + Math.Min(5, (int)Math.Floor(((double)(index + 1) / chunkCount) * 5));
                    await SetTaskStatus(
                        task,
                        "uploading chunked result",
                        $"Upload chunk {index + 1}/{chunkCount}, attempt {attempt}/{UploadAttempts} ({length} bytes)",
                        progress,
                        cancel
                    );
                    await UploadResultChunk(chunkBasePath, uploadId, index, chunkCount, image.Bytes.Length, image.MimeType, chunk, cancel);
                    break;
                }
                catch (FilexaUnauthorizedException)
                {
                    throw;
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested && IsTransient(ex) && DateTime.UtcNow + TimeSpan.FromSeconds(5) < deadline)
                {
                    lastError = ex;
                    TimeSpan delay = BackoffDelay(attempt);
                    await PostTaskStatusSafe(
                        task["status_url"]?.ToString() ?? "",
                        $"chunk upload retry after {ShortPreview(ex.Message, 80)}",
                        94,
                        cancel
                    );
                    DebugLog($"Chunk upload retry chunk={index + 1}/{chunkCount} attempt={attempt}: {ex.Message}; waiting {delay.TotalSeconds:0}s");
                    await Task.Delay(delay, cancel);
                }
                if (attempt == UploadAttempts)
                {
                    throw new IOException($"Could not upload generated image chunk {index + 1}/{chunkCount}: {lastError?.Message ?? "upload failed"}");
                }
            }
        }
        DebugLog($"Chunked upload completed: upload_id={uploadId}, chunks={chunkCount}");
    }

    private async Task UploadResultChunk(
        string chunkBasePath,
        string uploadId,
        int index,
        int chunkCount,
        int totalBytes,
        string mimeType,
        byte[] chunk,
        CancellationToken cancel
    )
    {
        ByteArrayContent content = new(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = chunk.Length;
        using HttpRequestMessage request = new(HttpMethod.Post, AbsoluteFilexaUrl($"{chunkBasePath.TrimEnd('/')}/{index}"));
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.ExpectContinue = false;
        request.Headers.Add("X-Filexa-Connector-Version", ConnectorVersion);
        request.Headers.Add("X-Filexa-Upload-Id", uploadId);
        request.Headers.Add("X-Filexa-Chunk-Index", index.ToString());
        request.Headers.Add("X-Filexa-Chunk-Count", chunkCount.ToString());
        request.Headers.Add("X-Filexa-Total-Bytes", totalBytes.ToString());
        request.Headers.Add("X-Filexa-Image-Mime", mimeType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        request.Content = content;
        using CancellationTokenSource timeoutCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeoutCancel.CancelAfter(ChunkUploadTimeout);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCancel.Token);
        await EnsureSuccess(response, authorizeFilexa: true, cancel);
        DebugLog($"Chunk upload response {index + 1}/{chunkCount}: {(int)response.StatusCode} {response.StatusCode}");
    }

    private async Task ReportFailureSafe(string path, string error, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            await PostJson(
                AbsoluteFilexaUrl(path),
                new JObject { ["error"] = ShortPreview(error, 1000) },
                cancel,
                timeout: FilexaJsonTimeout
            );
        }
        catch (FilexaUnauthorizedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog($"Failure report skipped: {ex.Message}");
        }
    }

    private async Task ReportCancelSafe(string path, string reason, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            await PostJson(
                AbsoluteFilexaUrl(path),
                new JObject { ["reason"] = ShortPreview(reason, 500) },
                cancel,
                timeout: FilexaJsonTimeout
            );
        }
        catch (FilexaUnauthorizedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog($"Cancel report skipped: {ex.Message}");
        }
    }

    private async Task SetTaskStatus(JObject task, string status, string lastEvent, int progress, CancellationToken cancel)
    {
        SetStatus(status, lastEvent);
        SaveConfig(_config);
        await PostTaskStatusSafe(task["status_url"]?.ToString() ?? "", status, progress, cancel);
    }

    private async Task PostTaskStatusSafe(string path, string status, int progress, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            await PostJson(AbsoluteFilexaUrl(path), new JObject
            {
                ["status"] = status,
                ["progress"] = Math.Clamp(progress, 0, 99),
            }, cancel, timeout: StatusUpdateTimeout);
        }
        catch (FilexaUnauthorizedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog($"Status update skipped: {ex.Message}");
        }
    }

    private async Task<JObject> PostJson(
        string url,
        JObject body,
        CancellationToken cancel,
        bool authorizeFilexa = true,
        TimeSpan? timeout = null
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.ExpectContinue = false;
        if (authorizeFilexa)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
        request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        CancellationTokenSource? timeoutCancel = null;
        try
        {
            if (timeout is not null)
            {
                timeoutCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeoutCancel.CancelAfter(timeout.Value);
            }
            using HttpResponseMessage response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancel?.Token ?? cancel
            );
            await EnsureSuccess(response, authorizeFilexa, cancel);
            string text = await ReadStringLimited(response.Content, MaxJsonResponseBytes, cancel);
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }
        finally
        {
            timeoutCancel?.Dispose();
        }
    }

    private async Task<byte[]> GetBytes(string url, CancellationToken cancel, bool authorizeFilexa, int maxBytes)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.ExpectContinue = false;
        if (authorizeFilexa)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        await EnsureSuccess(response, authorizeFilexa, cancel);
        return await ReadBytesLimited(response.Content, maxBytes, cancel);
    }

    private async Task EnsureSuccess(HttpResponseMessage response, bool authorizeFilexa, CancellationToken cancel)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string body = "";
        try
        {
            body = ShortPreview(await ReadStringLimited(response.Content, 4096, cancel), 300);
        }
        catch
        {
            body = "";
        }
        if (authorizeFilexa && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new FilexaUnauthorizedException("Filexa returned 401 Unauthorized; connector stopped until a new token/start is provided.");
        }
        throw new FilexaHttpException(response.StatusCode, body);
    }

    private string AbsoluteFilexaUrl(string path)
    {
        Uri baseUri = RequiredBaseUri(_config.ApiUrl, "Filexa API URL");
        Uri candidate = AbsoluteUrlForOrigin(baseUri, path, "Filexa URL");
        if (!candidate.AbsolutePath.StartsWith("/local/v1/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Filexa URL path is outside /local/v1/");
        }
        return candidate.ToString();
    }

    private string AbsoluteSwarmUrl(string path)
    {
        Uri baseUri = RequiredBaseUri(_config.SwarmUrl, "SwarmUI URL");
        return AbsoluteUrlForOrigin(baseUri, path, "SwarmUI URL").ToString();
    }

    private static Uri AbsoluteUrlForOrigin(Uri baseUri, string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException($"{label} is empty");
        }
        Uri candidate = Uri.TryCreate(path, UriKind.Absolute, out Uri? absolute)
            ? absolute
            : new Uri(new Uri($"{baseUri.Scheme}://{baseUri.Authority}/"), path.TrimStart('/'));
        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{label} must use http or https");
        }
        if (!SameOrigin(baseUri, candidate))
        {
            throw new InvalidDataException($"{label} origin does not match the configured origin");
        }
        return candidate;
    }

    private static bool SameOrigin(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && EffectivePort(left) == EffectivePort(right);
    }

    private static int EffectivePort(Uri uri)
    {
        if (!uri.IsDefaultPort)
        {
            return uri.Port;
        }
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    private static Uri RequiredBaseUri(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException($"{label} must be an absolute http(s) URL");
        }
        return uri;
    }

    private string ValidateTask(JObject task)
    {
        string jobId = task["job_id"]?.ToString() ?? "";
        if (!JobIdPattern.IsMatch(jobId))
        {
            throw new InvalidDataException("Invalid Filexa task id");
        }
        string kind = task["kind"]?.ToString() ?? "";
        if (kind is not ("image" or "image_edit"))
        {
            throw new InvalidDataException("Unsupported Filexa task kind");
        }
        string prompt = task["prompt"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptChars || HasControlChars(prompt))
        {
            throw new InvalidDataException("Invalid Filexa task prompt");
        }
        if (task["params"] is not JObject)
        {
            throw new InvalidDataException("Invalid Filexa task params");
        }
        AbsoluteFilexaUrl(task["result_upload_url"]?.ToString() ?? "");
        if (!string.IsNullOrWhiteSpace(task["result_chunk_upload_url"]?.ToString()))
        {
            AbsoluteFilexaUrl(task["result_chunk_upload_url"]!.ToString());
        }
        AbsoluteFilexaUrl(task["failure_url"]?.ToString() ?? "");
        if (!string.IsNullOrWhiteSpace(task["status_url"]?.ToString()))
        {
            AbsoluteFilexaUrl(task["status_url"]!.ToString());
        }
        if (!string.IsNullOrWhiteSpace(task["cancel_url"]?.ToString()))
        {
            AbsoluteFilexaUrl(task["cancel_url"]!.ToString());
        }
        if (task["references"] is JArray references)
        {
            if (references.Count > MaxReferenceCount)
            {
                throw new InvalidDataException("Too many Filexa reference images");
            }
            foreach (JToken token in references)
            {
                if (token is not JObject reference)
                {
                    throw new InvalidDataException("Invalid Filexa reference descriptor");
                }
                AbsoluteFilexaUrl(reference["url"]?.ToString() ?? "");
                ValidateImageMime(reference["mime_type"]?.ToString() ?? "image/jpeg");
            }
        }
        return jobId;
    }

    private JObject BuildSwarmParameters(JObject task)
    {
        JObject source = (task["params"] as JObject)!;
        string prompt = task["prompt"]?.ToString() ?? "";
        string model = source["model"]?.ToString() ?? task["model"]?.ToString() ?? "";
        ValidateModelCode(model);
        return new JObject
        {
            ["images"] = ValidateInt(source["images"], 1, 1, 4, "images"),
            ["model"] = model,
            ["width"] = ValidateInt(source["width"], 1024, 64, 4096, "width"),
            ["height"] = ValidateInt(source["height"], 1024, 64, 4096, "height"),
            ["steps"] = ValidateInt(source["steps"], 8, 1, 150, "steps"),
            ["cfgscale"] = ValidateDouble(source["cfgscale"], 1.0, 0, 30, "cfgscale"),
            ["seed"] = ValidateInt(source["seed"], -1, -1, int.MaxValue, "seed"),
            ["prompt"] = prompt,
        };
    }

    private static int ValidateInt(JToken? value, int fallback, int min, int max, string name)
    {
        if (value is null || value.Type == JTokenType.Null)
        {
            return fallback;
        }
        int result = value.Value<int>();
        if (result < min || result > max)
        {
            throw new InvalidDataException($"Invalid SwarmUI {name}");
        }
        return result;
    }

    private static double ValidateDouble(JToken? value, double fallback, double min, double max, string name)
    {
        if (value is null || value.Type == JTokenType.Null)
        {
            return fallback;
        }
        double result = value.Value<double>();
        if (double.IsNaN(result) || double.IsInfinity(result) || result < min || result > max)
        {
            throw new InvalidDataException($"Invalid SwarmUI {name}");
        }
        return result;
    }

    private static void ValidateModelCode(string model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || !ModelCodePattern.IsMatch(model)
            || model.Contains("..", StringComparison.Ordinal)
            || HasControlChars(model))
        {
            throw new InvalidDataException("Invalid SwarmUI model code");
        }
        string lower = model.ToLowerInvariant();
        if (lower.EndsWith(".exe") || lower.EndsWith(".bat") || lower.EndsWith(".cmd")
            || lower.EndsWith(".ps1") || lower.EndsWith(".sh") || lower.EndsWith(".dll"))
        {
            throw new InvalidDataException("Invalid SwarmUI model code");
        }
    }

    private static string ValidateImageMime(string mime)
    {
        string clean = (mime.Split(';', 2)[0] ?? "").Trim().ToLowerInvariant();
        if (clean is not ("image/png" or "image/jpeg" or "image/jpg" or "image/webp"))
        {
            throw new InvalidDataException("Unsupported reference image type");
        }
        return clean == "image/jpg" ? "image/jpeg" : clean;
    }

    private static string DetectImageMime(byte[] data)
    {
        if (data.Length >= 8
            && data[0] == 0x89
            && data[1] == 0x50
            && data[2] == 0x4E
            && data[3] == 0x47)
        {
            return "image/png";
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (data.Length >= 12
            && data[0] == 0x52
            && data[1] == 0x49
            && data[2] == 0x46
            && data[3] == 0x46
            && data[8] == 0x57
            && data[9] == 0x45
            && data[10] == 0x42
            && data[11] == 0x50)
        {
            return "image/webp";
        }
        return "image/png";
    }

    private static bool HasControlChars(string value)
    {
        return value.Any(ch => char.IsControl(ch) && ch is not ('\t' or '\r' or '\n'));
    }

    private static DateTime ParseDeadline(string? value)
    {
        if (DateTimeOffset.TryParse(value, out DateTimeOffset parsed))
        {
            return parsed.UtcDateTime;
        }
        return DateTime.UtcNow + TimeSpan.FromMinutes(10);
    }

    private Filexa2SwarmUIConnectorConfig LoadConfig()
    {
        string path = ConfigPath();
        if (File.Exists(path))
        {
            return JObject.Parse(File.ReadAllText(path)).ToObject<Filexa2SwarmUIConnectorConfig>() ?? new Filexa2SwarmUIConnectorConfig();
        }
        string legacyPath = LegacyConfigPath();
        if (File.Exists(legacyPath))
        {
            return JObject.Parse(File.ReadAllText(legacyPath)).ToObject<Filexa2SwarmUIConnectorConfig>() ?? new Filexa2SwarmUIConnectorConfig();
        }
        return new Filexa2SwarmUIConnectorConfig();
    }

    private void SaveConfig(Filexa2SwarmUIConnectorConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath())!);
        File.WriteAllText(ConfigPath(), JObject.FromObject(config).ToString());
    }

    private string ConfigPath() => Path.Combine(FilePath, "Data", "filexa2swarmui-connector.json");

    private string LegacyConfigPath() => Path.Combine(FilePath, "Data", "filexa-connector.json");

    private static string CleanBaseUrl(string value, string label)
    {
        Uri uri = RequiredBaseUri(value.Trim().TrimEnd('/'), label);
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private void StopAfterUnauthorized(string message)
    {
        _config.Enabled = false;
        _config.Status = "disabled";
        _config.LastEvent = message;
        _config.LastError = "";
        ClearActiveJob();
        SaveConfig(_config);
        Logs.Warning($"[{ConnectorName}] {message}");
    }

    private void StopAfterServerUnavailable(string message)
    {
        _config.Enabled = false;
        _config.Status = "disabled";
        _config.LastEvent = message;
        _config.LastError = "";
        ClearActiveJob();
        SaveConfig(_config);
        Logs.Warning($"[{ConnectorName}] {message}");
    }

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
        _activeCancelPath = task["cancel_url"]?.ToString() ?? "";
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
        _activeCancelPath = "";
    }

    private static TimeSpan BackoffDelay(int consecutiveErrors)
    {
        if (consecutiveErrors <= 0)
        {
            return PollDelay;
        }
        int seconds = Math.Min(120, 5 * (int)Math.Pow(2, Math.Min(5, consecutiveErrors - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return true;
        }
        if (ex is HttpRequestException or IOException)
        {
            return true;
        }
        return ex is FilexaHttpException http
            && ((int)http.StatusCode == 408 || (int)http.StatusCode == 429 || (int)http.StatusCode >= 500);
    }

    private static bool IsFilexaServerUnavailable(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return true;
        }
        if (ex is HttpRequestException requestException)
        {
            return requestException.StatusCode is null || (int)requestException.StatusCode >= 500;
        }
        if (ex is SocketException socketException)
        {
            return socketException.SocketErrorCode is SocketError.ConnectionRefused
                or SocketError.HostNotFound
                or SocketError.NetworkUnreachable
                or SocketError.TimedOut;
        }
        return ex.InnerException is not null && IsFilexaServerUnavailable(ex.InnerException);
    }

    private static async Task<string> ReadStringLimited(HttpContent content, int maxBytes, CancellationToken cancel)
    {
        byte[] bytes = await ReadBytesLimited(content, maxBytes, cancel);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadBytesLimited(HttpContent content, int maxBytes, CancellationToken cancel)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
        {
            throw new InvalidDataException("Response is too large");
        }
        await using Stream stream = await content.ReadAsStreamAsync(cancel);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancel)) > 0)
        {
            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("Response is too large");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
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
            Logs.Info($"[{ConnectorName}] {message}");
        }
    }
}

public class Filexa2SwarmUIConnectorConfig
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
    public bool CompressImagesBeforeUpload { get; set; } = true;
}

public class UploadPayload
{
    public UploadPayload(byte[] bytes, string mimeType)
    {
        Bytes = bytes;
        MimeType = mimeType;
    }

    public byte[] Bytes { get; }
    public string MimeType { get; }
}

public class FilexaUnauthorizedException : Exception
{
    public FilexaUnauthorizedException(string message) : base(message)
    {
    }
}

public class FilexaHttpException : Exception
{
    public FilexaHttpException(HttpStatusCode statusCode, string body)
        : base($"HTTP {(int)statusCode} {statusCode}" + (string.IsNullOrWhiteSpace(body) ? "" : $": {body}"))
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
