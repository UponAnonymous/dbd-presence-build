using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DbdPresence.LocalCi;

/// <summary>
/// The GitHub REST API for one repository (https://api.github.com only, the owner's fine-grained token). The token is read for each call and never logged. Whatever goes wrong with an answer (no
/// route, a connection that breaks or goes quiet in the middle of the body, a body of the wrong shape) leaves this class as a <see cref="GitHubApiException"/> and never as a raw framework
/// exception, so every caller's handling of GitHub failures covers it (local CI round 2, QA B1 and M5).
/// </summary>
public sealed class GitHubRestClient : IGitHubApi
{
    public const string ExpirationHeader = "github-authentication-token-expiration";

    /// <summary>The header with which GitHub lists the scopes of a classic or OAuth token. A fine-grained token has none, so an answer that carries it came to a token this program does not accept.</summary>
    public const string ScopesHeader = "X-OAuth-Scopes";

    /// <summary>The longest list of runners read (ten pages of a hundred): more registrations than that are not this program's, and a loop must end.</summary>
    private const int MaximumRunnerPages = 10;

    private const int RunnersPerPage = 100;

    /// <summary>The largest answer read from the API (the real ones are a few kilobytes; a release's notes are a few more).</summary>
    public const long MaximumAnswerBytes = 8L * 1024 * 1024;

    /// <summary>How long one read of an answer may wait for data before the answer is given up (a connection that died without closing).</summary>
    public static readonly TimeSpan DefaultReadInactivityLimit = TimeSpan.FromSeconds(60);

    private static readonly Uri s_apiRoot = new("https://" + AllowedHosts.Api + "/");

    private readonly HttpClient _client;
    private readonly Func<SecretText?> _token;
    private readonly string _repo;
    private readonly TimeSpan _inactivityLimit;
    private readonly TimeProvider _time;

    /// <param name="client">A client whose handler does not follow redirects.</param>
    /// <param name="token">Gives the token (or null when none is saved) at the time of each call.</param>
    /// <param name="owner">Repository owner.</param>
    /// <param name="repository">Repository name.</param>
    /// <param name="readInactivityLimit">How long one read of an answer may wait for data (default 60 seconds).</param>
    /// <param name="time">The clock that limit runs on (tests pass a fake one).</param>
    public GitHubRestClient(HttpClient client, Func<SecretText?> token, string owner, string repository, TimeSpan? readInactivityLimit = null, TimeProvider? time = null)
    {
        _client = client;
        _token = token;
        _repo = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        _inactivityLimit = readInactivityLimit ?? DefaultReadInactivityLimit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The body of the just-in-time runner request: labels, runner group, name and work folder.</summary>
    public static string BuildJitRequestBody(string name, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["runner_group_id"] = LocalCiDefaults.RunnerGroupId,
            ["labels"] = labels,
            ["work_folder"] = "_work",
        });
    }

    /// <summary>The token's expiry from the response header (format <c>2026-11-27 10:10:10 UTC</c>), or null when absent or unreadable.</summary>
    public static DateTimeOffset? ParseExpiration(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var text = header.Trim();
        if (text.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
            return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
                ? new DateTimeOffset(utc, TimeSpan.Zero)
                : null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    public async Task<RepoInfo> GetRepositoryAsync(CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Get, _repo, null, authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        return Shaped(reply, root => new RepoInfo(
            root.GetProperty("default_branch").GetString() ?? "main",
            ParseExpiration(reply.Header(ExpirationHeader)),
            root.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind is JsonValueKind.True or JsonValueKind.False ? isPrivate.GetBoolean() : null));
    }

    public async Task<RunnerRelease> GetLatestRunnerReleaseAsync(CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Get, "repos/actions/runner/releases/latest", null, authenticated: false, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        return Shaped(reply, root =>
        {
            var tag = root.GetProperty("tag_name").GetString() ?? throw new DownloadVerificationException("The runner release has no version.");
            var version = tag.TrimStart('v');
            if (!RunnerAssetPolicy.IsVersion(version))
            {
                throw new DownloadVerificationException("The runner release has a version that is not three numbers; refusing to use it.");
            }

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            Uri? linux = null;
            string? linuxDigest = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (!string.Equals(name, RunnerAssetPolicy.LinuxFileName(version), StringComparison.Ordinal))
                {
                    continue;
                }

                var text = asset.GetProperty("browser_download_url").GetString();
                if (!Uri.TryCreate(text, UriKind.Absolute, out var address))
                {
                    throw new DownloadVerificationException($"The runner release has an unreadable download address for {name}.");
                }

                linux = address;
                linuxDigest = RunnerAssetPolicy.ParseDigest(StringOrNull(asset, "digest"));
            }

            return new RunnerRelease(
                version,
                notes,
                linux ?? throw new DownloadVerificationException($"Runner release {version} has no linux-x64 package."),
                linuxDigest);
        });
    }

    public async Task<JitRunner> GenerateJitConfigAsync(string name, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(
            HttpMethod.Post, $"{_repo}/actions/runners/generate-jitconfig", BuildJitRequestBody(name, labels), authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        var status = reply.Status;
        return Shaped(reply, root =>
        {
            var encoded = root.GetProperty("encoded_jit_config").GetString();
            if (string.IsNullOrEmpty(encoded))
            {
                throw new GitHubApiException("GitHub answered without a runner configuration.", status, isTransient: false);
            }

            return new JitRunner(root.GetProperty("runner").GetProperty("id").GetInt64(), name, new SecretText(encoded));
        });
    }

    /// <summary>Every runner of the repository, page by page (a hundred to a page): a runner that is not on the first page must not look idle (local CI round 3, QA minor).</summary>
    public async Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync(CancellationToken cancellationToken)
    {
        var list = new List<RunnerInfo>();
        for (var page = 1; page <= MaximumRunnerPages; page++)
        {
            using var reply = await SendAsync(HttpMethod.Get, $"{_repo}/actions/runners?per_page={RunnersPerPage}&page={page.ToString(CultureInfo.InvariantCulture)}", null, authenticated: true, cancellationToken).ConfigureAwait(false);
            reply.RequireSuccess();
            var (onThisPage, total) = Shaped(reply, root =>
            {
                var found = new List<RunnerInfo>();
                foreach (var runner in root.GetProperty("runners").EnumerateArray())
                {
                    found.Add(new RunnerInfo(
                        runner.GetProperty("id").GetInt64(),
                        runner.GetProperty("name").GetString() ?? string.Empty,
                        runner.GetProperty("status").GetString() ?? string.Empty,
                        runner.GetProperty("busy").GetBoolean()));
                }

                return (found, root.TryGetProperty("total_count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number) ? number : (int?)null);
            });
            list.AddRange(onThisPage);

            // Done when the page was not full, or when GitHub's own count has been reached.
            if (onThisPage.Count < RunnersPerPage || (total is { } known && list.Count >= known))
            {
                break;
            }
        }

        return list;
    }

    public async Task<string?> GetVariableAsync(string name, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Get, $"{_repo}/actions/variables/{Uri.EscapeDataString(name)}", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        if (reply.Status == 404)
        {
            return null;
        }

        reply.RequireSuccess();
        return Shaped(reply, root => StringOrNull(root, "value"));
    }

    public async Task DeleteRunnerAsync(long runnerId, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Delete, $"{_repo}/actions/runners/{runnerId.ToString(CultureInfo.InvariantCulture)}", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        if (reply.Status != 404)
        {
            reply.RequireSuccess();
        }
    }

    public async Task SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = name, ["value"] = value });
        var escaped = Uri.EscapeDataString(name);
        using (var update = await SendAsync(HttpMethod.Patch, $"{_repo}/actions/variables/{escaped}", body, authenticated: true, cancellationToken).ConfigureAwait(false))
        {
            if (update.Status != 404)
            {
                update.RequireSuccess();
                return;
            }
        }

        using var create = await SendAsync(HttpMethod.Post, $"{_repo}/actions/variables", body, authenticated: true, cancellationToken).ConfigureAwait(false);
        create.RequireSuccess();
    }

    public async Task DeleteVariableAsync(string name, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Delete, $"{_repo}/actions/variables/{Uri.EscapeDataString(name)}", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        if (reply.Status != 404)
        {
            reply.RequireSuccess();
        }
    }

    public async Task<long?> DispatchWorkflowAsync(string workflowFile, string branch, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["ref"] = branch });
        using var reply = await SendAsync(HttpMethod.Post, $"{_repo}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/dispatches", body, authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        if (reply.Status == 200 && reply.HasJson && reply.Json.RootElement.ValueKind == JsonValueKind.Object && reply.Json.RootElement.TryGetProperty("workflow_run_id", out var id) && id.TryGetInt64(out var runId))
        {
            return runId;
        }

        return null;
    }

    public async Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(string workflowFile, string? eventName, CancellationToken cancellationToken)
    {
        var query = eventName is null ? "per_page=10" : $"event={Uri.EscapeDataString(eventName)}&per_page=10";
        using var reply = await SendAsync(HttpMethod.Get, $"{_repo}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/runs?{query}", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        return Shaped(reply, root =>
        {
            var list = new List<WorkflowRunInfo>();
            foreach (var run in root.GetProperty("workflow_runs").EnumerateArray())
            {
                list.Add(ReadRun(run));
            }

            return (IReadOnlyList<WorkflowRunInfo>)list;
        });
    }

    public async Task<WorkflowRunInfo> GetWorkflowRunAsync(long runId, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Get, $"{_repo}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        return Shaped(reply, ReadRun);
    }

    public async Task CancelWorkflowRunAsync(long runId, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Post, $"{_repo}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/cancel", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
    }

    public async Task<IReadOnlyList<WorkflowJobInfo>> ListWorkflowJobsAsync(long runId, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(HttpMethod.Get, $"{_repo}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/jobs?per_page=100", null, authenticated: true, cancellationToken).ConfigureAwait(false);
        reply.RequireSuccess();
        return Shaped(reply, root =>
        {
            var list = new List<WorkflowJobInfo>();
            foreach (var job in root.GetProperty("jobs").EnumerateArray())
            {
                var labels = new List<string>();
                if (job.TryGetProperty("labels", out var labelList) && labelList.ValueKind == JsonValueKind.Array)
                {
                    labels.AddRange(labelList.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!));
                }

                list.Add(new WorkflowJobInfo(
                    job.GetProperty("id").GetInt64(),
                    job.GetProperty("name").GetString() ?? string.Empty,
                    job.GetProperty("status").GetString() ?? string.Empty,
                    StringOrNull(job, "conclusion"),
                    StringOrNull(job, "runner_name"),
                    job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array ? steps.GetArrayLength() : 0,
                    labels));
            }

            return (IReadOnlyList<WorkflowJobInfo>)list;
        });
    }

    /// <summary>Reads what the caller needs from the answer; a body of another shape (a missing property, a wrong kind of value) becomes a <see cref="GitHubApiException"/>.</summary>
    private static T Shaped<T>(Reply reply, Func<JsonElement, T> read)
    {
        try
        {
            return read(reply.Json.RootElement);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new GitHubApiException($"GitHub's answer was not in the form this program expects [{reply.What}] ({ex.GetType().Name}).", reply.Status, isTransient: false, inner: ex);
        }
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static WorkflowRunInfo ReadRun(JsonElement run) =>
        new(
            run.GetProperty("id").GetInt64(),
            run.GetProperty("status").GetString() ?? string.Empty,
            StringOrNull(run, "conclusion"),
            run.TryGetProperty("created_at", out var created) && created.ValueKind == JsonValueKind.String
                ? created.GetDateTimeOffset()
                : DateTimeOffset.MinValue);

    private async Task<Reply> SendAsync(HttpMethod method, string path, string? jsonBody, bool authenticated, CancellationToken cancellationToken)
    {
        var url = new Uri(s_apiRoot, path);
        if (!AllowedHosts.IsAllowedApi(url))
        {
            throw new GitHubApiException("Refusing to call a host that is not on the approved list.", 0, isTransient: false);
        }

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DbdLocalCi", "1.0"));
        if (authenticated)
        {
            var token = _token() ?? throw new GitHubApiException("No GitHub token is saved. Run setup first.", 401, isTransient: false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException("GitHub could not be reached: " + ex.Message, 0, isTransient: true, inner: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException("GitHub did not answer in time.", 0, isTransient: true, inner: ex);
        }

        using (response)
        {
            var text = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            JsonDocument? json = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    json = JsonDocument.Parse(text);
                }
                catch (JsonException)
                {
                    // Not JSON (an HTML error page, for example): the status code still tells what happened.
                    json = null;
                }
            }

            if (authenticated && response.Headers.Contains(ScopesHeader))
            {
                // A classic token (or an OAuth token) lists its scopes in every answer; a fine-grained token never does. The program only works with fine-grained tokens.
                throw new GitHubApiException(
                    "GitHub says this token is a classic token (its answers list scopes). A classic token reaches every repository of your account, so this program refuses it. Make a fine-grained token that starts with github_pat_ and is limited to this one repository (the steps are in set-up), then run set-up again. (This program assumes that GitHub lists scopes only for classic tokens; that is not verified on a real GitHub yet. If the token you used starts with github_pat_ and was made as set-up describes, that assumption is wrong: tell whoever looks after this program, and do not use a classic token instead.)",
                    403,
                    isTransient: false)
                {
                    IsClassicToken = true,
                };
            }

            response.Headers.TryGetValues(ExpirationHeader, out var expiration);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var remaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var left) ? left.FirstOrDefault() : null;
            return new Reply((int)response.StatusCode, json, expiration?.FirstOrDefault(), retryAfter, remaining, method.Method + " " + path.Split('?')[0]);
        }
    }

    /// <summary>
    /// Reads the answer as text, stopping with an error when it is larger than <see cref="MaximumAnswerBytes"/>. A connection that breaks or goes quiet in the middle of the body is a
    /// transient <see cref="GitHubApiException"/>, like any other lost connection.
    /// </summary>
    private async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (response.Content.Headers.ContentLength is { } announced && announced > MaximumAnswerBytes)
        {
            throw new GitHubApiException("GitHub sent an answer larger than this program accepts.", status, isTransient: false);
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var memory = new MemoryStream();
                var buffer = new byte[16384];
                while (true)
                {
                    var read = await StreamStall.ReadAsync(stream, buffer, _inactivityLimit, _time, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (memory.Length + read > MaximumAnswerBytes)
                    {
                        throw new GitHubApiException("GitHub sent an answer larger than this program accepts.", status, isTransient: false);
                    }

                    memory.Write(buffer, 0, read);
                }

                return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            }
        }
        catch (StreamStalledException ex)
        {
            throw new GitHubApiException("GitHub stopped sending its answer. " + ex.Message, status, isTransient: true, inner: ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new GitHubApiException("GitHub's answer broke off: " + ex.Message, status, isTransient: true, inner: ex);
        }
    }

    private sealed class Reply : IDisposable
    {
        private readonly JsonDocument? _json;
        private readonly string? _expiration;
        private readonly TimeSpan? _retryAfter;
        private readonly string? _rateRemaining;

        public Reply(int status, JsonDocument? json, string? expiration, TimeSpan? retryAfter, string? rateRemaining, string what)
        {
            Status = status;
            _json = json;
            _expiration = expiration;
            _retryAfter = retryAfter;
            _rateRemaining = rateRemaining;
            What = what;
        }

        public int Status { get; }

        /// <summary>The request in a few words, for messages (method and path without the query).</summary>
        public string What { get; }

        public bool HasJson => _json is not null;

        public JsonDocument Json => _json ?? throw new GitHubApiException($"GitHub answered {Status} without data for {What}.", Status, isTransient: false);

        public string? Header(string name) => string.Equals(name, ExpirationHeader, StringComparison.OrdinalIgnoreCase) ? _expiration : null;

        public void RequireSuccess()
        {
            if (Status is >= 200 and < 300)
            {
                return;
            }

            var detail = _json is not null && _json.RootElement.ValueKind == JsonValueKind.Object && _json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
            var rateLimited = Status == 429 || (Status == 403 && (_retryAfter is not null || _rateRemaining == "0"));
            var transient = rateLimited || Status >= 500;
            var reason = Status switch
            {
                401 => "GitHub refused the token (wrong, expired or revoked).",
                403 when !rateLimited => "GitHub refused the request: the token lacks a permission.",
                404 => "GitHub could not find it (or the token cannot see it).",
                _ => $"GitHub answered {Status}.",
            };
            throw new GitHubApiException($"{reason} [{What}] {detail}".TrimEnd(), Status, transient, rateLimited ? _retryAfter ?? TimeSpan.FromMinutes(1) : null);
        }

        public void Dispose() => _json?.Dispose();
    }
}
