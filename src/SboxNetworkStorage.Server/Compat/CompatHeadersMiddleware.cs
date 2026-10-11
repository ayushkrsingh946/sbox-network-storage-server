using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Adds the compatibility response headers to every response, including errors and rejected requests:
/// <c>x-ns-server-version</c> always, and <c>x-ns-library-status</c> (plus <c>-latest</c> and <c>-sunset</c>
/// where they apply) from the caller's <see cref="ClientIdentity"/>. Headers only: no response body changes, and
/// no request is refused because of its library version here. The headers are written when the response starts,
/// so a library version a handler found in the request body still counts. When <see cref="LibraryPolicyMiddleware"/>
/// refuses a request it stashes the enforced status first, so the header reports the refusal, not the matrix alone.
/// </summary>
public sealed class CompatHeadersMiddleware(RequestDelegate next, LibraryCompatProvider compat)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (httpContext, provider) = ((HttpContext, LibraryCompatProvider))state;
            var headers = httpContext.Response.Headers;
            headers[CompatHeaders.ServerVersion] = BuildInfo.Version;
            var result = provider.Current.Evaluate(ClientIdentityContext.Get(httpContext));
            headers[CompatHeaders.LibraryStatus] = httpContext.Items.TryGetValue(typeof(LibraryPolicyMiddleware), out var enforced)
                && enforced is string refused ? refused : result.Status.ToWire();
            if (result.Latest is not null) headers[CompatHeaders.LibraryLatest] = result.Latest;
            if (result.Sunset is not null) headers[CompatHeaders.LibrarySunset] = result.Sunset;
            return Task.CompletedTask;
        }, (context, compat));

        return next(context);
    }
}
