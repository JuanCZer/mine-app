namespace Notes.Api.Auth;

public static class IdentityHttpResults
{
    public static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null, string? traceId = null)
    {
        NoStore(context.Response);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(Envelope(code, error, fieldErrors, traceId), context.RequestAborted);
    }

    public static void NoStore(HttpResponse response) => response.Headers.CacheControl = "no-store";

    private static Dictionary<string, object> Envelope(string code, string error,
        IReadOnlyDictionary<string, string[]>? fieldErrors, string? traceId)
    {
        var result = new Dictionary<string, object>
        {
            ["error"] = error,
            ["code"] = code
        };
        if (fieldErrors is not null) result["fieldErrors"] = fieldErrors;
        if (!string.IsNullOrWhiteSpace(traceId)) result["traceId"] = traceId;
        return result;
    }
}
