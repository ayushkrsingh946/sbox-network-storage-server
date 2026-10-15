// Runs inside sbox-dev via sbox-cli run_csharp: the library's editor Sync Tool, headless.
SyncToolConfig.Load();
var r = new System.Collections.Generic.Dictionary<string, object>();
r["baseUrl"] = SyncToolConfig.BaseUrl;
r["projectId"] = SyncToolConfig.ProjectId;
var v = await SyncToolApi.Validate();
r["validate"] = v.HasValue ? "ok" : "ERR " + SyncToolApi.LastErrorCode + " " + SyncToolApi.LastErrorMessage;
var eps = SyncToolConfig.LoadSourcePayloadResources( "endpoint", includeDeprecated: false );
var cols = SyncToolConfig.LoadSourcePayloadResources( "collection" );
r["local"] = new { endpoints = eps.Count, collections = cols.Count };
var batch = new System.Collections.Generic.Dictionary<string, object> { ["endpoints"] = eps, ["collections"] = cols };
var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>( System.Text.Json.JsonSerializer.Serialize( batch ) );
var pre = await SyncToolApi.PreflightSync( payload, "live" );
r["preflight"] = pre.HasValue && pre.Value.TryGetProperty( "summary", out var s ) ? s.GetRawText() : "ERR " + SyncToolApi.LastErrorCode + " " + SyncToolApi.LastErrorMessage;
var push = await SyncToolApi.PushSync( payload, "live" );
r["push"] = push.HasValue && push.Value.TryGetProperty( "ok", out var ok ) && ok.GetBoolean() ? "ok" : "ERR " + SyncToolApi.LastErrorCode + " " + SyncToolApi.LastErrorMessage;
r["pass"] = (string)r["validate"] == "ok" && (string)r["push"] == "ok";
return r;
