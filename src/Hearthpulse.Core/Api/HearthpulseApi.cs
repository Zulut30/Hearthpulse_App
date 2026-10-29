using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.Api;

/// <summary>An error the Hearthpulse API reported, or a transport failure turned into the same shape.</summary>
/// <param name="Status">The HTTP status, or 0 when the server could not be reached.</param>
/// <param name="Code">The API's error code (for example INVALID_TOKEN), or a local one: OFFLINE, NOT_AVAILABLE, BAD_RESPONSE.</param>
public sealed record ApiError(int Status, string Code, string Message)
{
    public static ApiError Offline(string message) => new(0, "OFFLINE", message);
}

/// <summary>The outcome of one call: the response's <c>data</c> element, or an error.</summary>
public sealed class ApiResult
{
    private ApiResult(JsonElement data, ApiError? error)
    {
        Data = data;
        Error = error;
    }

    public JsonElement Data { get; }

    public ApiError? Error { get; }

    public bool Ok => Error is null;

    /// <summary>The <c>meta</c> element next to <c>data</c> (paging, periods, freshness), or <c>default</c> when there is none.</summary>
    public JsonElement Meta { get; private init; }

    internal static ApiResult Success(JsonElement data) => new(data, null);

    internal static ApiResult Success(JsonElement data, JsonElement meta) => new(data, null) { Meta = meta };

    internal static ApiResult Failure(ApiError error) => new(default, error);
}

/// <summary>
/// Client for the Hearthpulse data API (api.kolodahearthstone.com). Responses come wrapped as
/// <c>{"data": ...}</c>; errors as <c>{"detail": {"code", "message"}}</c> or FastAPI's validation list.
/// </summary>
public sealed class HearthpulseApi(HttpClient http, string? baseUrl = null)
{
    public const string DefaultBaseUrl = "https://api.kolodahearthstone.com/";

    public const string Attribution = "Data: Hearthpulse API (api.kolodahearthstone.com)";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The server: <paramref name="baseUrl"/>, else the HEARTHPULSE_API variable (for a test server), else the public one.</summary>
    public Uri BaseUri { get; } = new(EnsureSlash(baseUrl is { Length: > 0 } ? baseUrl
        : Environment.GetEnvironmentVariable("HEARTHPULSE_API") is { Length: > 0 } custom ? custom
        : DefaultBaseUrl));

    public Task<ApiResult> GetAsync(string path, string? bearer = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, null, bearer, cancellationToken);

    public Task<ApiResult> PostAsync(string path, object body, string? bearer = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, path, body, bearer, cancellationToken);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body, string? bearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse((int)response.StatusCode, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Failure(ApiError.Offline("The server did not answer in time."));
        }
        catch (HttpRequestException ex)
        {
            return ApiResult.Failure(ApiError.Offline(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // An answer in a charset .NET does not know (a captive portal, a proxy) cannot be read as text.
            return ApiResult.Failure(ApiError.Offline(ex.Message));
        }
    }

    internal static ApiResult Parse(int status, string text)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ApiResult.Failure(new ApiError(status, status is >= 200 and < 300 ? "BAD_RESPONSE" : StatusCode(status), $"Unexpected response ({status})."));
        }

        if (status is >= 200 and < 300)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                return ApiResult.Success(data, root.TryGetProperty("meta", out var meta) ? meta : default);
            }

            return ApiResult.Success(root);
        }

        var code = StatusCode(status);
        var message = $"The server answered {status}.";
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
        {
            switch (detail.ValueKind)
            {
                case JsonValueKind.Object:
                    code = detail.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : code;
                    message = detail.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : message;
                    break;
                case JsonValueKind.String:
                    message = detail.GetString()!;
                    break;
                case JsonValueKind.Array:
                    // FastAPI validation errors: [{"loc": [...], "msg": "..."}].
                    code = "VALIDATION_ERROR";
                    message = string.Join(' ', detail.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("msg", out var msg) ? msg.GetString() : null)
                        .Where(s => !string.IsNullOrEmpty(s)));
                    break;
            }
        }

        return ApiResult.Failure(new ApiError(status, code, message));
    }

    private static string StatusCode(int status) => status switch
    {
        (int)HttpStatusCode.NotFound or (int)HttpStatusCode.MethodNotAllowed => "NOT_AVAILABLE",
        (int)HttpStatusCode.Unauthorized => "INVALID_TOKEN",
        (int)HttpStatusCode.TooManyRequests => "RATE_LIMITED",
        >= 500 => "SERVER_ERROR",
        _ => "HTTP_" + status.ToString(CultureInfo.InvariantCulture),
    };

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
