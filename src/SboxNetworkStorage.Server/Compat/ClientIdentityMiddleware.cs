namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Resolves and caches the request's <see cref="ClientIdentity"/> early, so downstream middleware and
/// handlers share one parse. Adds no headers and refuses nothing; <see cref="CompatHeadersMiddleware"/>
/// advertises the status and <see cref="LibraryPolicyMiddleware"/> enforces the opt-in policy.
/// </summary>
public sealed class ClientIdentityMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ClientIdentityContext.Get(context);
        return next(context);
    }
}
