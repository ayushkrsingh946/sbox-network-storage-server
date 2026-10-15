// Runs inside a playing editor or a -mcp game/dedicated server via sbox-cli run_csharp.
NetworkStorage.Configure( "__PROJECT_ID__", "__PUBLIC_KEY__", "http://127.0.0.1:8080" );
var r = new System.Collections.Generic.Dictionary<string, object>();
r["dedicated"] = Application.IsDedicatedServer;
r["editor"] = Application.IsEditor;
r["libraryVersion"] = NetworkStorage.LibraryVersion;
r["allowed"] = Http.IsAllowed( new Uri( "http://127.0.0.1:8080/v3/x" ) );
async System.Threading.Tasks.Task<(bool ok, string text)> Call( string slug, object input )
{
	var res = await NetworkStorage.CallEndpoint( slug, input );
	NetworkStorage.TryGetLastEndpointError( slug, out var code, out var message );
	return (res.HasValue, res.HasValue ? res.Value.GetRawText() : code + " " + message);
}
var ping = await Call( "ping", null );
var echo = await Call( "echo", new { hello = "world" } );
r["ping"] = ping.text;
r["echo"] = echo.text;
var values = await NetworkStorage.GetGameValues();
r["gameValues"] = values?.GetRawText();
r["pass"] = ping.ok && ping.text.Contains( "pong" ) && echo.ok && values.HasValue;
return r;
