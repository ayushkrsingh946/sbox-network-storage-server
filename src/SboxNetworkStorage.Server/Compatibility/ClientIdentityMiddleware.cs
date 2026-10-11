using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Compatibility;

public sealed class ClientIdentityMiddleware(RequestDelegate next, LibraryCompatibility compatibility)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ClientIdentity.ReadHeaders(context);
        context.Response.OnStarting(() =>
        {
            var identity = ClientIdentity.Get(context);
            context.Response.Headers["x-ns-server-version"] = BuildInfo.Version;
            var status = context.Items.TryGetValue(typeof(LibraryCompatibility), out var policyStatus) && policyStatus is string enforced
                ? enforced : compatibility.StatusFor(identity.LibraryVersion);
            context.Response.Headers["x-ns-library-status"] = status;
            if (status is not "supported" && compatibility.Matrix.Latest is { } latest)
                context.Response.Headers["x-ns-library-latest"] = latest;
            if (compatibility.SunsetFor(identity.LibraryVersion) is { } sunset)
                context.Response.Headers["x-ns-library-sunset"] = sunset;
            return Task.CompletedTask;
        });
        await next(context);
    }
}
