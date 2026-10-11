using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Adds <c>x-ns-server-version</c> to every response, including errors and rejected requests. Headers only: no
/// response body changes. The header is written when the response starts, so no response path misses it.
/// </summary>
public sealed class CompatHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            ((HttpContext)state).Response.Headers[CompatHeaders.ServerVersion] = BuildInfo.Version;
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
