NetworkStorage.Configure( "__PROJECT_ID__", "__PUBLIC_KEY__", "http://127.0.0.1:8080" );
var res = await NetworkStorage.CallEndpoint( "ping" );
NetworkStorage.TryGetLastEndpointError( "ping", out var code, out var message );
var values = await NetworkStorage.GetGameValues();
return new
{
	Application.IsDedicatedServer,
	Allowed = Http.IsAllowed( new Uri( "http://127.0.0.1:8080/v3/x" ) ),
	NetworkStorage.IsConfigured,
	NetworkStorage.BaseUrl,
	PingOk = res.HasValue,
	PingBody = res?.GetRawText(),
	PingError = code + " " + message,
	GameValues = values?.GetRawText(),
};
