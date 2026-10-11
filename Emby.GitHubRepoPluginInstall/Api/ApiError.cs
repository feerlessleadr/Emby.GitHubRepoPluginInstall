
namespace Emby.GitHubRepoPluginInstall.Api;

/// <summary>The error body every failure from this API uses.</summary>
public class ErrorBody
{
    public string Title  { get; set; }
    public int    Status { get; set; }
    public string Detail { get; set; }
}

/// <summary>
/// A reply with a chosen HTTP status (for example 202 Accepted). The service sets the status on Emby's response and
/// writes <see cref="Body"/> as the JSON body. This deliberately is not Emby's IHttpResult, which Emby serializes whole.
/// </summary>
public class ApiResult
{
    public ApiResult(int status, object body)
    {
        Status = status;
        Body   = body;
    }

    public int    Status { get; }
    public object Body   { get; }

    public static ApiResult Accepted(object body) => new ApiResult(202, body);
}
/// <summary>An error with a real HTTP status and a body of <see cref="ErrorBody"/>.</summary>
public class ApiError : ApiResult
{
    public ApiError(int status, string title, string detail)
        : base(status, new ErrorBody { Title = title, Status = status, Detail = detail })
    {
    }

    public static ApiError Disabled() =>
        new ApiError(403, "ApiDisabled", "The phone API is switched off. Turn it on in the plugin's Settings.");

    public static ApiError ManageDisabled() =>
        new ApiError(403, "ManageDisabled", "Adding and removing plugins from the app is switched off in the plugin's Settings.");

    public static ApiError NotFound(string detail) => new ApiError(404, "NotFound", detail);
    public static ApiError Invalid(string detail)  => new ApiError(400, "InvalidRequest", detail);
    public static ApiError Conflict(string title, string detail) => new ApiError(409, title, detail);
    public static ApiError RunInProgress() =>
        new ApiError(409, "RunInProgress", "An update run is already in progress. Wait for it to finish.");
}
