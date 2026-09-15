using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
var app = builder.Build();
var serverStartedAt = DateTimeOffset.UtcNow;
var process = Process.GetCurrentProcess();

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(dataDir);
var licensesFile = Path.Combine(dataDir, "licenses.json");
var appsFile = Path.Combine(dataDir, "apps.json");
var usersFile = Path.Combine(dataDir, "users.json");
var logsFile = Path.Combine(dataDir, "logs.json");
var plansFile = Path.Combine(dataDir, "plans.json");
var ordersFile = Path.Combine(dataDir, "orders.json");
var webhooksFile = Path.Combine(dataDir, "webhooks.json");
var eventsFile = Path.Combine(dataDir, "events.json");
var subscriptionsFile = Path.Combine(dataDir, "subscriptions.json");
var variablesFile = Path.Combine(dataDir, "variables.json");
var rulesFile = Path.Combine(dataDir, "rules.json");
var tokensFile = Path.Combine(dataDir, "tokens.json");
var chatMessagesFile = Path.Combine(dataDir, "chat_messages.json");
var notificationsFile = Path.Combine(dataDir, "notifications.json");
var settingsFile = Path.Combine(dataDir, "settings.json");
var alertsFile = Path.Combine(dataDir, "alerts.json");
var filesDir = Path.Combine(builder.Environment.ContentRootPath, "Storage");
var backupsDir = Path.Combine(builder.Environment.ContentRootPath, "Backups");
Directory.CreateDirectory(backupsDir);
Directory.CreateDirectory(filesDir);
var apiKey = builder.Configuration["NorthAuth:ApiKey"] ?? "CHANGE_THIS_API_KEY";
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

// NorthAuth 2.0 persistence: JSON files are kept as a local mirror/backup,
// but the authoritative data store is PostgreSQL when DATABASE_URL is present.
// This prevents users/licenses from disappearing when Render recreates the container.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? builder.Configuration.GetConnectionString("NorthAuth");
var requirePersistentStorage = string.Equals(Environment.GetEnvironmentVariable("NORTHAUTH_REQUIRE_PERSISTENCE"), "1", StringComparison.Ordinal);
if (requirePersistentStorage && string.IsNullOrWhiteSpace(databaseUrl))
    throw new InvalidOperationException("NorthAuth requer DATABASE_URL em produção para evitar perda de usuários e licenças.");
var store = new PersistentJsonStore(databaseUrl, dataDir, jsonOptions);
store.Initialize();

List<LicenseRecord> LoadLicenses() => store.Load("licenses", licensesFile, new List<LicenseRecord>());
List<AppRecord> LoadApps() => store.Load("apps", appsFile, new List<AppRecord>());
List<PanelUser> LoadUsers() => store.Load("users", usersFile, new List<PanelUser>());
List<AuditLog> LoadLogs() => store.Load("logs", logsFile, new List<AuditLog>());
List<PlanRecord> LoadPlans() => store.Load("plans", plansFile, new List<PlanRecord>());
List<OrderRecord> LoadOrders() => store.Load("orders", ordersFile, new List<OrderRecord>());
List<WebhookRecord> LoadWebhooks() => store.Load("webhooks", webhooksFile, new List<WebhookRecord>());
List<EventRecord> LoadEvents() => store.Load("events", eventsFile, new List<EventRecord>());
List<SubscriptionRecord> LoadSubscriptions() => store.Load("subscriptions", subscriptionsFile, new List<SubscriptionRecord>());
List<VariableRecord> LoadVariables() => store.Load("variables", variablesFile, new List<VariableRecord>());
List<RuleRecord> LoadRules() => store.Load("rules", rulesFile, new List<RuleRecord>());
List<TokenRecord> LoadTokens() => store.Load("tokens", tokensFile, new List<TokenRecord>());
List<ChatMessageRecord> LoadChatMessages() => store.Load("chat_messages", chatMessagesFile, new List<ChatMessageRecord>());
List<NotificationRecord> LoadNotifications() => store.Load("notifications", notificationsFile, new List<NotificationRecord>());
ServerSettings LoadSettings() => store.Load("settings", settingsFile, new ServerSettings());
List<AlertRecord> LoadAlerts() => store.Load("alerts", alertsFile, new List<AlertRecord>());
void SaveAlerts(List<AlertRecord> x) => store.Save("alerts", alertsFile, x);
void SaveSettings(ServerSettings x) => store.Save("settings", settingsFile, x);
void SavePlans(List<PlanRecord> x) => store.Save("plans", plansFile, x);
void SaveOrders(List<OrderRecord> x) => store.Save("orders", ordersFile, x);
void SaveWebhooks(List<WebhookRecord> x) => store.Save("webhooks", webhooksFile, x);
void SaveEvents(List<EventRecord> x) => store.Save("events", eventsFile, x);
void SaveSubscriptions(List<SubscriptionRecord> x) => store.Save("subscriptions", subscriptionsFile, x);
void SaveVariables(List<VariableRecord> x) => store.Save("variables", variablesFile, x);
void SaveRules(List<RuleRecord> x) => store.Save("rules", rulesFile, x);
void SaveTokens(List<TokenRecord> x) => store.Save("tokens", tokensFile, x);
void SaveChatMessages(List<ChatMessageRecord> x) => store.Save("chat_messages", chatMessagesFile, x);
void SaveNotifications(List<NotificationRecord> x) => store.Save("notifications", notificationsFile, x);
void SaveLicenses(List<LicenseRecord> items) => store.Save("licenses", licensesFile, items);
void SaveApps(List<AppRecord> items) => store.Save("apps", appsFile, items);
void SaveUsers(List<PanelUser> items) => store.Save("users", usersFile, items);
void SaveLogs(List<AuditLog> items) => store.Save("logs", logsFile, items);

// Garante que o painel sempre tenha uma aplicação inicial cadastrada.
// Só cria a aplicação automaticamente quando o arquivo está vazio; aplicações
// existentes nunca são sobrescritas.
void EnsureDefaultApplication()
{
    var apps = LoadApps();
    if (apps.Count > 0) return;

    var defaultApp = new AppRecord
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Novask22's Application",
        Version = "1.0",
        Description = "Aplicação padrão do North Auth",
        Active = true,
        CreatedAt = DateTimeOffset.UtcNow,
        OwnerId = "NORTH-OWNER-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
        Secret = "NORTH-APP-" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant()
    };

    apps.Add(defaultApp);
    SaveApps(apps);
}

EnsureDefaultApplication();
async Task DispatchWebhookAsync(EventRecord evt)
{
    var hooks = LoadWebhooks().Where(x => x.Active && x.Events.Count > 0 && x.Events.Contains(evt.Type, StringComparer.OrdinalIgnoreCase)).ToList();
    if (hooks.Count == 0) return;
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    foreach (var hook in hooks)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { @event = evt.Type, id = evt.Id, createdAt = evt.CreatedAt, actor = evt.Actor, target = evt.Target, details = evt.Details, ip = evt.Ip });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(hook.Url, content);
            hook.LastStatus = (int)response.StatusCode;
            hook.LastDeliveryAt = DateTimeOffset.UtcNow;
            hook.LastError = response.IsSuccessStatusCode ? "" : "HTTP " + (int)response.StatusCode;
        }
        catch (Exception ex)
        {
            hook.LastStatus = 0;
            hook.LastDeliveryAt = DateTimeOffset.UtcNow;
            hook.LastError = ex.Message.Length > 180 ? ex.Message[..180] : ex.Message;
        }
    }
    SaveWebhooks(hooks);
}

void AddEvent(HttpContext ctx, string type, string target = "", string details = "", string? actor = null)
{
    var evt = new EventRecord { Id = Guid.NewGuid().ToString("N"), Type = type, Target = target, Details = details, Actor = actor ?? CurrentUser(ctx.Request)?.Username ?? "system", Ip = GetClientIp(ctx), CreatedAt = DateTimeOffset.UtcNow };
    var items = LoadEvents();
    items.Add(evt);
    if (items.Count > 5000) items = items.Skip(items.Count - 5000).ToList();
    SaveEvents(items);
    _ = DispatchWebhookAsync(evt);
}

void AddLog(HttpContext ctx, string action, string target = "", string details = "", string? actor = null) { var logs=LoadLogs(); logs.Add(new AuditLog{Id=Guid.NewGuid().ToString("N"),Action=action,Target=target,Details=details,Actor=actor??CurrentUser(ctx.Request)?.Username??"system",Ip=GetClientIp(ctx),CreatedAt=DateTimeOffset.UtcNow}); if(logs.Count>2000) logs=logs.Skip(logs.Count-2000).ToList(); SaveLogs(logs); AddEvent(ctx, action, target, details, actor); }
bool Authorized(HttpRequest request, string? bodyApiKey = null) {
    var header = request.Headers["X-Api-Key"].FirstOrDefault();
    var candidate = !string.IsNullOrWhiteSpace(header) ? header : bodyApiKey;
    return string.Equals(candidate, apiKey, StringComparison.Ordinal);
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"success\":false,\"message\":\"Erro interno do servidor.\"}");
    });
});

// Hardening HTTP: reduz superfícies comuns de ataque sem alterar a interface.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    if (ctx.Request.IsHttps) ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Painel: login por usuário/senha, separado da API Key usada pelo loader.
var sessions = new Dictionary<string, PanelSession>(StringComparer.Ordinal);
var sessionLock = new object();
var loginFailures = new Dictionary<string, LoginThrottle>(StringComparer.OrdinalIgnoreCase);
var pendingTwoFactor = new Dictionary<string, string>(StringComparer.Ordinal);
var securityLock = new object();

static string NewCsrfToken() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
static bool SafeEquals(string? a, string? b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
void SetSecurityCookies(HttpContext ctx, string sessionToken)
{
    var csrf = NewCsrfToken();
    ctx.Response.Cookies.Append("north_panel_session", sessionToken, new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=ctx.Request.IsHttps,MaxAge=TimeSpan.FromHours(Math.Clamp(LoadSettings().SessionHours,1,72)),Path="/"});
    ctx.Response.Cookies.Append("north_csrf",csrf,new CookieOptions{HttpOnly=false,SameSite=SameSiteMode.Strict,Secure=ctx.Request.IsHttps,MaxAge=TimeSpan.FromHours(Math.Clamp(LoadSettings().SessionHours,1,72)),Path="/"});
}

bool CsrfValid(HttpRequest req)
{
    var cookie=req.Cookies["north_csrf"];
    var header=req.Headers["X-North-CSRF"].FirstOrDefault();
    if (SafeEquals(cookie,header)) return true;
    // Fallback para chamadas legítimas do próprio painel que ainda não enviem o header.
    var origin=req.Headers.Origin.FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(origin))
    {
        try { var o=new Uri(origin); return string.Equals(o.Host,req.Host.Host,StringComparison.OrdinalIgnoreCase) && o.Port==req.Host.Port; } catch { }
    }
    var referer=req.Headers.Referer.FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(referer))
    {
        try { var r=new Uri(referer); return string.Equals(r.Host,req.Host.Host,StringComparison.OrdinalIgnoreCase) && r.Port==req.Host.Port; } catch { }
    }
    return false;
}

app.Use(async (ctx,next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/panel") && ctx.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH")
    {
        var path=ctx.Request.Path.Value ?? "";
        var exempt=path.Equals("/api/panel/login",StringComparison.OrdinalIgnoreCase) || path.Equals("/api/panel/login/2fa",StringComparison.OrdinalIgnoreCase);
        if (!exempt && ctx.Request.Cookies.ContainsKey("north_panel_session") && !CsrfValid(ctx.Request))
        {
            ctx.Response.StatusCode=403;
            ctx.Response.ContentType="application/json; charset=utf-8";
            await ctx.Response.WriteAsync("{\"success\":false,\"message\":\"Validação de segurança inválida. Recarregue o painel.\"}");
            return;
        }
    }
    await next();
});

static string HashPassword(string password, byte[] salt) {
    using var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt, 120_000, System.Security.Cryptography.HashAlgorithmName.SHA256);
    return Convert.ToBase64String(pbkdf2.GetBytes(32));
}
static bool VerifyPassword(string password, string salt64, string hash64) {
    try { var salt=Convert.FromBase64String(salt64); var a=Convert.FromBase64String(HashPassword(password,salt)); var b=Convert.FromBase64String(hash64); return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a,b); } catch { return false; }
}
string SecurityKey(HttpContext ctx, string username) => (GetClientIp(ctx) ?? "unknown") + "|" + username.Trim().ToLowerInvariant();
bool IsLoginBlocked(HttpContext ctx, string username, out int seconds) { seconds=0; var k=SecurityKey(ctx,username); lock(securityLock) { if(!loginFailures.TryGetValue(k,out var f)) return false; if(f.BlockedUntil<=DateTimeOffset.UtcNow){loginFailures.Remove(k);return false;} seconds=(int)Math.Ceiling((f.BlockedUntil-DateTimeOffset.UtcNow).TotalSeconds); return true; } }
void RegisterLoginFailure(HttpContext ctx, string username) { var k=SecurityKey(ctx,username); lock(securityLock) { if(!loginFailures.TryGetValue(k,out var f)||f.WindowStart<DateTimeOffset.UtcNow.AddMinutes(-15)) f=new LoginThrottle{WindowStart=DateTimeOffset.UtcNow}; f.Failures++; if(f.Failures>=5) f.BlockedUntil=DateTimeOffset.UtcNow.AddMinutes(10); loginFailures[k]=f; } }
void ClearLoginFailures(HttpContext ctx, string username) { lock(securityLock) loginFailures.Remove(SecurityKey(ctx,username)); }
string NewTwoFactorSecret() { var bytes=System.Security.Cryptography.RandomNumberGenerator.GetBytes(20); return Base32Encode(bytes); }
static string Base32Encode(byte[] data) { const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var sb=new System.Text.StringBuilder(); int buffer=0,bits=0; foreach(var b in data){buffer=(buffer<<8)|b;bits+=8;while(bits>=5){sb.Append(alphabet[(buffer>>(bits-5))&31]);bits-=5;}} if(bits>0)sb.Append(alphabet[(buffer<<(5-bits))&31]); return sb.ToString(); }
static byte[] Base32Decode(string input) { const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; input=input.Trim().TrimEnd('='); int buffer=0,bits=0; using var ms=new MemoryStream(); foreach(var c in input.ToUpperInvariant()){int v=alphabet.IndexOf(c);if(v<0)continue;buffer=(buffer<<5)|v;bits+=5;if(bits>=8){ms.WriteByte((byte)((buffer>>(bits-8))&255));bits-=8;}} return ms.ToArray(); }
static string TotpCode(string secret, long counter) { var key=Base32Decode(secret); var bytes=BitConverter.GetBytes(counter); if(BitConverter.IsLittleEndian)Array.Reverse(bytes); using var h=new System.Security.Cryptography.HMACSHA1(key); var hash=h.ComputeHash(bytes); int o=hash[^1]&15; int bin=((hash[o]&127)<<24)|(hash[o+1]<<16)|(hash[o+2]<<8)|hash[o+3]; return (bin%1000000).ToString("D6"); }
static bool VerifyTotp(string secret,string code) { if(string.IsNullOrWhiteSpace(secret)||string.IsNullOrWhiteSpace(code)||code.Length!=6||!code.All(char.IsDigit))return false; var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30; for(long i=-1;i<=1;i++){if(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(TotpCode(secret,now+i)),System.Text.Encoding.ASCII.GetBytes(code)))return true;} return false; }
string CreateTwoFactorSetupUri(PanelUser u,string secret) => "otpauth://totp/North%20Auth:"+Uri.EscapeDataString(u.Username)+"?secret="+secret+"&issuer=North%20Auth";

string CreateSession(string username, HttpContext? ctx = null) { var token=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); lock(sessionLock) sessions[token]=new PanelSession{Username=username,ExpiresAt=DateTimeOffset.UtcNow.AddHours(Math.Clamp(LoadSettings().SessionHours, 1, 72)),CreatedAt=DateTimeOffset.UtcNow,LastSeenAt=DateTimeOffset.UtcNow,Ip=GetClientIp(ctx!),UserAgent=ctx?.Request.Headers.UserAgent.FirstOrDefault()??""}; return token; }
PanelUser? CurrentUser(HttpRequest req) { var token=req.Cookies["north_panel_session"]; if(string.IsNullOrWhiteSpace(token)) return null; string? username=null; lock(sessionLock) { if(!sessions.TryGetValue(token,out var ses)||ses.ExpiresAt<DateTimeOffset.UtcNow){sessions.Remove(token);return null;} ses.LastSeenAt=DateTimeOffset.UtcNow; username=ses.Username; } return LoadUsers().FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)&&x.Active); }
bool PanelAuthorized(HttpRequest req) => CurrentUser(req) is not null;
bool HasPanelPermission(HttpRequest req, string permission) { var u=CurrentUser(req); if(u is null) return false; if(string.Equals(u.Role,"Owner",StringComparison.OrdinalIgnoreCase)) return true; return u.Permissions?.Contains(permission,StringComparer.OrdinalIgnoreCase) == true; }

var startupUsers=LoadUsers();
if (string.Equals(Environment.GetEnvironmentVariable("NORTH_RESET_ADMIN"), "1", StringComparison.Ordinal)) {
    var salt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    startupUsers.RemoveAll(x=>x.Username.Equals("admin", StringComparison.OrdinalIgnoreCase));
    startupUsers.Insert(0,new PanelUser{Username="admin",DisplayName="Administrador",PasswordHash=HashPassword("North@123",salt),Salt=Convert.ToBase64String(salt),Role="Owner",Permissions=PanelPermissions.All.ToList(),Active=true,CreatedAt=DateTimeOffset.UtcNow});
    SaveUsers(startupUsers);
}
foreach(var su in startupUsers) {
    if(su.Permissions is null || su.Permissions.Count==0) su.Permissions = su.Role.Equals("Owner",StringComparison.OrdinalIgnoreCase) ? PanelPermissions.All.ToList() : PanelPermissions.StaffDefault.ToList();
}
SaveUsers(startupUsers);
if(startupUsers.Count==0) {
    var salt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    startupUsers.Add(new PanelUser{Username="admin",DisplayName="Administrador",PasswordHash=HashPassword("North@123",salt),Salt=Convert.ToBase64String(salt),Role="Owner",Active=true,CreatedAt=DateTimeOffset.UtcNow});
    SaveUsers(startupUsers);
}

app.MapPost("/api/panel/login", (PanelLoginRequest req, HttpContext ctx) => {
    var username=req.Username?.Trim()??"";
    if(IsLoginBlocked(ctx,username,out var wait)) return Results.Json(new {success=false,message=$"Muitas tentativas. Tente novamente em {wait}s."},statusCode:429);
    var u=LoadUsers().FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)&&x.Active);
    if(u is null || !VerifyPassword(req.Password??"",u.Salt,u.PasswordHash)) { RegisterLoginFailure(ctx,username); return Results.Json(new {success=false,message="Usuário ou senha inválidos."},statusCode:401); }
    ClearLoginFailures(ctx,username);
    if(u.TwoFactorEnabled) {
        var challenge=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)); lock(securityLock) pendingTwoFactor[challenge]=u.Username;
        ctx.Response.Cookies.Append("north_panel_2fa",challenge,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Lax,Secure=false,MaxAge=TimeSpan.FromMinutes(5),Path="/"});
        return Results.Ok(new {success=true,requiresTwoFactor=true,user=new {username=u.Username,displayName=u.DisplayName,role=u.Role}});
    }
    var token=CreateSession(u.Username,ctx); SetSecurityCookies(ctx,token);
    AddLog(ctx,"Panel Login",u.Username,"Login realizado",u.Username);
    return Results.Ok(new {success=true,requiresTwoFactor=false,user=new {username=u.Username,displayName=u.DisplayName,role=u.Role}});
});
app.MapPost("/api/panel/login/2fa", (TwoFactorLoginRequest req, HttpContext ctx) => {
    var challenge=ctx.Request.Cookies["north_panel_2fa"]; if(string.IsNullOrWhiteSpace(challenge)) return Results.Unauthorized(); string? username=null; lock(securityLock){if(!pendingTwoFactor.TryGetValue(challenge,out username)){return Results.Unauthorized();}pendingTwoFactor.Remove(challenge);}
    var u=LoadUsers().FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)&&x.Active&&x.TwoFactorEnabled); if(u is null||!VerifyTotp(u.TwoFactorSecret??"",req.Code??"")) return Results.Json(new {success=false,message="Código 2FA inválido."},statusCode:401);
    ctx.Response.Cookies.Delete("north_panel_2fa"); var token=CreateSession(u.Username,ctx); SetSecurityCookies(ctx,token); AddLog(ctx,"Panel Login",u.Username,"Login realizado com 2FA",u.Username); return Results.Ok(new {success=true,user=new {username=u.Username,displayName=u.DisplayName,role=u.Role}});
});
app.MapPost("/api/panel/logout", (HttpContext ctx) => { var t=ctx.Request.Cookies["north_panel_session"]; if(t is not null) lock(sessionLock) sessions.Remove(t); ctx.Response.Cookies.Delete("north_panel_session"); ctx.Response.Cookies.Delete("north_csrf"); return Results.Ok(new {success=true}); });
app.MapGet("/api/panel/sessions", (HttpRequest req) => { var me=CurrentUser(req); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); var now=DateTimeOffset.UtcNow; lock(sessionLock){foreach(var t in sessions.Where(x=>x.Value.ExpiresAt<now).Select(x=>x.Key).ToList())sessions.Remove(t); return Results.Ok(new {success=true,sessions=sessions.Select(x=>new {id=x.Key.Length>8?x.Key[..8]+"…":x.Key,username=x.Value.Username,createdAt=x.Value.CreatedAt,lastSeenAt=x.Value.LastSeenAt,expiresAt=x.Value.ExpiresAt,ip=x.Value.Ip,userAgent=x.Value.UserAgent,current=string.Equals(req.Cookies["north_panel_session"],x.Key,StringComparison.Ordinal)}).OrderByDescending(x=>x.lastSeenAt)});}});
app.MapPost("/api/panel/sessions/revoke-others", (HttpRequest req, HttpContext ctx) => { var me=CurrentUser(req); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); var current=req.Cookies["north_panel_session"]; int removed=0; lock(sessionLock){foreach(var t in sessions.Where(x=>x.Value.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase)&&x.Key!=current).Select(x=>x.Key).ToList()){sessions.Remove(t);removed++;}} AddLog(ctx,"Panel Sessions Revoked",me.Username,$"{removed} sessão(ões) encerrada(s)",me.Username); return Results.Ok(new {success=true,removed});});
app.MapPost("/api/panel/sessions/revoke/{id}", (string id, HttpRequest req, HttpContext ctx) => { var me=CurrentUser(req); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); var current=req.Cookies["north_panel_session"]; string? target=null; lock(sessionLock){target=sessions.Keys.FirstOrDefault(k=>k.StartsWith(id,StringComparison.Ordinal)&&sessions[k].Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase)); if(target is null)return Results.NotFound(); if(target==current)return Results.BadRequest(new {success=false,message="Use o logout para encerrar a sessão atual."}); sessions.Remove(target);} AddLog(ctx,"Panel Session Revoked",me.Username,"Sessão encerrada pelo administrador",me.Username); return Results.Ok(new {success=true});});
app.MapGet("/api/panel/me", (HttpRequest req) => { var u=CurrentUser(req); return u is null ? Results.Unauthorized() : Results.Ok(new {success=true,user=new {username=u.Username,displayName=u.DisplayName,role=u.Role,permissions=u.Permissions??new List<string>(),twoFactorEnabled=u.TwoFactorEnabled}}); });
app.MapGet("/api/panel/2fa/status", (HttpRequest req) => { var me=CurrentUser(req); if(me is null)return Results.Unauthorized(); return Results.Ok(new {success=true,enabled=me.TwoFactorEnabled}); });
app.MapPost("/api/panel/2fa/setup", (HttpContext ctx) => { var me=CurrentUser(ctx.Request); if(me is null)return Results.Unauthorized(); var secret=NewTwoFactorSecret(); var users=LoadUsers(); var u=users.First(x=>x.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase)); u.TwoFactorSecret=secret; u.TwoFactorEnabled=false; SaveUsers(users); AddLog(ctx,"2FA Setup Generated",u.Username,"Novo segredo 2FA gerado",u.Username); return Results.Ok(new {success=true,secret,uri=CreateTwoFactorSetupUri(u,secret)}); });
app.MapPost("/api/panel/2fa/enable", (TwoFactorCodeRequest req, HttpContext ctx) => { var me=CurrentUser(ctx.Request); if(me is null)return Results.Unauthorized(); var users=LoadUsers(); var u=users.First(x=>x.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase)); if(string.IsNullOrWhiteSpace(u.TwoFactorSecret))return Results.BadRequest(new {success=false,message="Gere um segredo 2FA primeiro."}); if(!VerifyTotp(u.TwoFactorSecret,req.Code??""))return Results.BadRequest(new {success=false,message="Código 2FA inválido."}); u.TwoFactorEnabled=true; SaveUsers(users); AddLog(ctx,"2FA Enabled",u.Username,"Autenticação de dois fatores ativada",u.Username); return Results.Ok(new {success=true,message="2FA ativado."}); });
app.MapPost("/api/panel/2fa/disable", (TwoFactorCodeRequest req, HttpContext ctx) => { var me=CurrentUser(ctx.Request); if(me is null)return Results.Unauthorized(); var users=LoadUsers(); var u=users.First(x=>x.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase)); if(!u.TwoFactorEnabled||!VerifyTotp(u.TwoFactorSecret??"",req.Code??""))return Results.BadRequest(new {success=false,message="Código 2FA inválido."}); u.TwoFactorEnabled=false; u.TwoFactorSecret=""; SaveUsers(users); AddLog(ctx,"2FA Disabled",u.Username,"Autenticação de dois fatores desativada",u.Username); return Results.Ok(new {success=true,message="2FA desativado."}); });
app.MapPost("/api/panel/password", (ChangePasswordRequest req, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request);
    if(me is null) return Results.Unauthorized();
    var current=req.CurrentPassword??""; var next=req.NewPassword??"";
    if(next.Length<8) return Results.BadRequest(new {success=false,message="A nova senha precisa ter pelo menos 8 caracteres."});
    if(next.Length>128) return Results.BadRequest(new {success=false,message="A nova senha é muito longa."});
    if(!VerifyPassword(current,me.Salt,me.PasswordHash)) return Results.BadRequest(new {success=false,message="A senha atual está incorreta."});
    if(string.Equals(current,next,StringComparison.Ordinal)) return Results.BadRequest(new {success=false,message="A nova senha deve ser diferente da atual."});
    var users=LoadUsers(); var u=users.FirstOrDefault(x=>x.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase));
    if(u is null) return Results.Unauthorized();
    var salt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    u.PasswordHash=HashPassword(next,salt); u.Salt=Convert.ToBase64String(salt); SaveUsers(users);
    var oldTokens=new List<string>(); lock(sessionLock){oldTokens=sessions.Where(x=>x.Value.Username.Equals(u.Username,StringComparison.OrdinalIgnoreCase)).Select(x=>x.Key).ToList();foreach(var t in oldTokens)sessions.Remove(t);}
    var newToken=CreateSession(u.Username,ctx); SetSecurityCookies(ctx,newToken);
    AddLog(ctx,"Panel Password Changed",u.Username,"Senha do painel alterada",u.Username);
    return Results.Ok(new {success=true,message="Senha alterada com sucesso."});
});
app.MapGet("/api/panel/users", (HttpRequest req) => { var me=CurrentUser(req); if(me is null) return Results.Unauthorized(); if(me.Role!="Owner") return Results.StatusCode(403); return Results.Ok(new {success=true,users=LoadUsers().Select(x=>new {x.Username,x.DisplayName,x.Role,x.Active,x.CreatedAt,x.TwoFactorEnabled,Permissions=x.Permissions??new List<string>()})}); });
app.MapPost("/api/panel/users", (CreatePanelUserRequest req, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null) return Results.Unauthorized(); if(me.Role!="Owner") return Results.StatusCode(403);
    var username=req.Username?.Trim()??""; var password=req.Password??""; if(username.Length<3||password.Length<6) return Results.BadRequest(new {success=false,message="Usuário precisa ter 3+ caracteres e senha 6+."});
    var users=LoadUsers(); if(users.Any(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Usuário já existe."});
    var salt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16); var u=new PanelUser{Username=username,DisplayName=string.IsNullOrWhiteSpace(req.DisplayName)?username:req.DisplayName.Trim(),PasswordHash=HashPassword(password,salt),Salt=Convert.ToBase64String(salt),Role=(req.Role=="Owner"?"Owner":"Staff"),Permissions=(req.Role=="Owner"?PanelPermissions.All.ToList():PanelPermissions.StaffDefault.ToList()),Active=true,CreatedAt=DateTimeOffset.UtcNow}; users.Add(u); SaveUsers(users); AddLog(ctx,"Panel User Created",u.Username,"Usuário do painel criado",me.Username); return Results.Ok(new {success=true});
});
app.MapPost("/api/panel/users/{username}/toggle", (string username, HttpContext ctx) => { var me=CurrentUser(ctx.Request); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); var users=LoadUsers(); var u=users.FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)); if(u is null)return Results.NotFound(); if(u.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new {success=false,message="Não é possível desativar seu próprio usuário."}); u.Active=!u.Active; SaveUsers(users); AddLog(ctx,u.Active?"Panel User Enabled":"Panel User Disabled",u.Username,"Status do usuário alterado",me.Username); return Results.Ok(new {success=true,active=u.Active}); });
app.MapDelete("/api/panel/users/{username}", (string username, HttpContext ctx) => { var me=CurrentUser(ctx.Request); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); if(username.Equals(me.Username,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new {success=false,message="Não é possível excluir seu próprio usuário."}); var users=LoadUsers(); var n=users.RemoveAll(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)); if(n==0)return Results.NotFound(); SaveUsers(users); AddLog(ctx,"Panel User Deleted",username,"Usuário do painel excluído",me.Username); return Results.Ok(new {success=true}); });

app.MapPut("/api/panel/users/{username}/role", (string username, ChangePanelRoleRequest req, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403);
    var role=(req.Role??"").Trim();
    if(role!="Owner" && role!="Staff") return Results.BadRequest(new {success=false,message="Cargo inválido. Use Owner ou Staff."});
    var users=LoadUsers(); var u=users.FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase));
    if(u is null)return Results.NotFound();
    if(u.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase) && role!="Owner")
        return Results.BadRequest(new {success=false,message="Não é possível remover seu próprio cargo de Owner."});
    u.Role=role; if(role=="Owner") u.Permissions=PanelPermissions.All.ToList(); else if(u.Permissions is null || u.Permissions.Count==0 || u.Permissions.SequenceEqual(PanelPermissions.All,StringComparer.OrdinalIgnoreCase)) u.Permissions=PanelPermissions.StaffDefault.ToList(); SaveUsers(users);
    AddLog(ctx,"Panel User Role Changed",u.Username,"Cargo alterado para "+role,me.Username);
    return Results.Ok(new {success=true,role=u.Role});
});

app.MapPut("/api/panel/users/{username}/permissions", (string username, PermissionUpdateRequest req, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null) return Results.Unauthorized(); if(me.Role!="Owner") return Results.StatusCode(403);
    var users=LoadUsers(); var u=users.FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)); if(u is null)return Results.NotFound();
    if(u.Role=="Owner") { u.Permissions=PanelPermissions.All.ToList(); SaveUsers(users); return Results.Ok(new {success=true,permissions=u.Permissions}); }
    var allowed=PanelPermissions.All.Where(x=>x!="users").ToHashSet(StringComparer.OrdinalIgnoreCase);
    u.Permissions=(req.Permissions??new List<string>()).Where(allowed.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    SaveUsers(users); AddLog(ctx,"Panel User Permissions Changed",u.Username,"Permissões: "+string.Join(", ",u.Permissions),me.Username);
    return Results.Ok(new {success=true,permissions=u.Permissions});
});

app.MapGet("/api/v1/admin/logs", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"logs")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized();
    var all=LoadLogs().OrderByDescending(x=>x.CreatedAt).ToList();
    var action=(req.Query["action"].ToString()??"").Trim();
    var actor=(req.Query["actor"].ToString()??"").Trim();
    var ip=(req.Query["ip"].ToString()??"").Trim();
    var q=(req.Query["q"].ToString()??"").Trim();
    DateTimeOffset? from=DateTimeOffset.TryParse(req.Query["from"].ToString(),out var fd)?fd: null;
    DateTimeOffset? to=DateTimeOffset.TryParse(req.Query["to"].ToString(),out var td)?td: null;
    if(from.HasValue) all=all.Where(x=>x.CreatedAt>=from.Value).ToList();
    if(to.HasValue) all=all.Where(x=>x.CreatedAt<=to.Value.AddDays(1).AddTicks(-1)).ToList();
    if(!string.IsNullOrWhiteSpace(action)) all=all.Where(x=>x.Action.Contains(action,StringComparison.OrdinalIgnoreCase)).ToList();
    if(!string.IsNullOrWhiteSpace(actor)) all=all.Where(x=>x.Actor.Contains(actor,StringComparison.OrdinalIgnoreCase)).ToList();
    if(!string.IsNullOrWhiteSpace(ip)) all=all.Where(x=>(x.Ip??"").Contains(ip,StringComparison.OrdinalIgnoreCase)).ToList();
    if(!string.IsNullOrWhiteSpace(q)) all=all.Where(x=>(x.Action+" "+x.Target+" "+x.Details+" "+x.Actor+" "+(x.Ip??"")).Contains(q,StringComparison.OrdinalIgnoreCase)).ToList();
    var limit=5000;
    if(int.TryParse(req.Query["limit"].ToString(),out var parsedLimit)) limit=Math.Clamp(parsedLimit,1,5000);
    var logs=all.Take(limit).ToList();
    var loginCount=all.Count(x=>x.Action.Contains("login",StringComparison.OrdinalIgnoreCase));
    var adminCount=all.Count(x=>x.Action.Contains("panel",StringComparison.OrdinalIgnoreCase)||x.Action.Contains("license",StringComparison.OrdinalIgnoreCase)||x.Action.Contains("app",StringComparison.OrdinalIgnoreCase));
    return Results.Ok(new {success=true,total=all.Count,returned=logs.Count,logins=loginCount,adminActions=adminCount,logs});
});

app.MapGet("/api/v1/admin/logs/export", (HttpRequest req, HttpContext ctx) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"logs")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized();
    var logs=LoadLogs().OrderByDescending(x=>x.CreatedAt).ToList();
    var action=(req.Query["action"].ToString()??"").Trim(); var actor=(req.Query["actor"].ToString()??"").Trim(); var ip=(req.Query["ip"].ToString()??"").Trim(); var q=(req.Query["q"].ToString()??"").Trim();
    DateTimeOffset? from=DateTimeOffset.TryParse(req.Query["from"].ToString(),out var fd)?fd: null;
    DateTimeOffset? to=DateTimeOffset.TryParse(req.Query["to"].ToString(),out var td)?td: null;
    if(from.HasValue) logs=logs.Where(x=>x.CreatedAt>=from.Value).ToList();
    if(to.HasValue) logs=logs.Where(x=>x.CreatedAt<=to.Value.AddDays(1).AddTicks(-1)).ToList();
    if(!string.IsNullOrWhiteSpace(action)) logs=logs.Where(x=>x.Action.Contains(action,StringComparison.OrdinalIgnoreCase)).ToList();
    if(!string.IsNullOrWhiteSpace(actor)) logs=logs.Where(x=>x.Actor.Contains(actor,StringComparison.OrdinalIgnoreCase)).ToList();
    if(!string.IsNullOrWhiteSpace(ip)) logs=logs.Where(x=>(x.Ip??"").Contains(ip,StringComparison.OrdinalIgnoreCase)).ToList();
    if(!string.IsNullOrWhiteSpace(q)) logs=logs.Where(x=>(x.Action+" "+x.Target+" "+x.Details+" "+x.Actor+" "+(x.Ip??"")).Contains(q,StringComparison.OrdinalIgnoreCase)).ToList();
    static string Csv(string? v) => "\"" + (v??"").Replace("\"","\"\"") + "\"";
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("ID,Ação,Alvo,Detalhes,Usuário,IP,Data UTC");
    foreach(var x in logs) sb.AppendLine(string.Join(',',Csv(x.Id),Csv(x.Action),Csv(x.Target),Csv(x.Details),Csv(x.Actor),Csv(x.Ip),Csv(x.CreatedAt.ToString("O"))));
    var bytes=System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    AddLog(ctx,"Logs Exported","Audit","Exportação CSV: "+logs.Count+" registro(s)");
    return Results.File(bytes,"text/csv","north-auth-audit-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".csv");
});
// Webhooks + Events
app.MapGet("/api/v1/admin/webhooks", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"webhooks")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized();
    return Results.Ok(new { success=true, webhooks=LoadWebhooks().OrderByDescending(x=>x.CreatedAt).ToList() });
});
app.MapPost("/api/v1/admin/webhooks", (WebhookRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"webhooks")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var url=(req.Url??"").Trim();
    if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https")) return Results.BadRequest(new {success=false,message="Informe uma URL HTTP ou HTTPS válida."});
    var types=(req.Events??new List<string>()).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if(types.Count==0) return Results.BadRequest(new {success=false,message="Selecione pelo menos um evento."});
    var hooks=LoadWebhooks();
    var hook=new WebhookRecord{Id="WH-"+Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),Name=string.IsNullOrWhiteSpace(req.Name)?"Webhook":req.Name.Trim(),Url=url,Events=types,Active=req.Active};
    hooks.Add(hook); SaveWebhooks(hooks); AddLog(ctx,"Webhook Created",hook.Id,$"Nome={hook.Name}; URL={hook.Url}");
    return Results.Ok(new {success=true,webhook=hook});
});
app.MapPut("/api/v1/admin/webhooks/{id}", (string id, WebhookRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"webhooks")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var hooks=LoadWebhooks(); var hook=hooks.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(hook is null)return Results.NotFound();
    if(!string.IsNullOrWhiteSpace(req.Name))hook.Name=req.Name.Trim();
    if(!string.IsNullOrWhiteSpace(req.Url)){var url=req.Url.Trim();if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||(uri.Scheme!="http"&&uri.Scheme!="https"))return Results.BadRequest(new {success=false,message="URL HTTP/HTTPS inválida."});hook.Url=url;}
    if(req.Events is not null)hook.Events=req.Events.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    hook.Active=req.Active; SaveWebhooks(hooks); AddLog(ctx,"Webhook Updated",hook.Id,$"Status={(hook.Active?"Ativo":"Inativo")}"); return Results.Ok(new {success=true,webhook=hook});
});
app.MapDelete("/api/v1/admin/webhooks/{id}", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"webhooks")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var hooks=LoadWebhooks(); var hook=hooks.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(hook is null)return Results.NotFound(); hooks.Remove(hook); SaveWebhooks(hooks); AddLog(ctx,"Webhook Deleted",hook.Id,"Webhook excluído"); return Results.Ok(new {success=true});
});
app.MapPost("/api/v1/admin/webhooks/{id}/test", async (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"webhooks")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var hook=LoadWebhooks().FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(hook is null)return Results.NotFound();
    var test=new EventRecord{Id="TEST-"+Guid.NewGuid().ToString("N"),Type="webhook.test",Target=hook.Id,Details="Teste manual do webhook",Actor=CurrentUser(ctx.Request)?.Username??"system",Ip=GetClientIp(ctx),CreatedAt=DateTimeOffset.UtcNow};
    try { using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(8)}; var payload=JsonSerializer.Serialize(new {@event=test.Type,id=test.Id,createdAt=test.CreatedAt,actor=test.Actor,target=test.Target,details=test.Details,ip=test.Ip}); using var content=new StringContent(payload,System.Text.Encoding.UTF8,"application/json"); using var response=await client.PostAsync(hook.Url,content); hook.LastStatus=(int)response.StatusCode; hook.LastDeliveryAt=DateTimeOffset.UtcNow; hook.LastError=response.IsSuccessStatusCode?"":"HTTP "+(int)response.StatusCode; var hooks=LoadWebhooks(); var stored=hooks.FirstOrDefault(x=>x.Id==hook.Id); if(stored!=null){stored.LastStatus=hook.LastStatus;stored.LastDeliveryAt=hook.LastDeliveryAt;stored.LastError=hook.LastError;} SaveWebhooks(hooks); AddLog(ctx,"Webhook Test",hook.Id,$"HTTP={(int)response.StatusCode}"); return Results.Ok(new {success=response.IsSuccessStatusCode,status=(int)response.StatusCode,message=response.IsSuccessStatusCode?"Webhook entregue com sucesso.":"Webhook respondeu com erro."}); } catch(Exception ex){hook.LastStatus=0;hook.LastDeliveryAt=DateTimeOffset.UtcNow;hook.LastError=ex.Message.Length>180?ex.Message[..180]:ex.Message;var hooks=LoadWebhooks();var stored=hooks.FirstOrDefault(x=>x.Id==hook.Id);if(stored!=null){stored.LastStatus=0;stored.LastDeliveryAt=hook.LastDeliveryAt;stored.LastError=hook.LastError;}SaveWebhooks(hooks);return Results.Ok(new {success=false,status=0,message=hook.LastError});}
});
app.MapGet("/api/v1/admin/events", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"events")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized();
    var events=LoadEvents().OrderByDescending(x=>x.CreatedAt).Take(1000).ToList(); return Results.Ok(new {success=true,total=events.Count,events});
});

app.MapGet("/api/v1/admin/music/diagnose", async (HttpRequest req) =>
{
    var me = CurrentUser(req);
    if (me is null) return Results.Unauthorized();
    if (me.Role != "Owner" && !HasPanelPermission(req, "settings")) return Results.StatusCode(403);
    var settings = LoadSettings();
    var envKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
    var key = !string.IsNullOrWhiteSpace(settings.YouTubeApiKey) ? settings.YouTubeApiKey.Trim() : envKey?.Trim();
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new { success=false, code="youtube_api_key_missing", message="Nenhuma chave do YouTube está configurada." });
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = "https://www.googleapis.com/youtube/v3/i18nLanguages?part=snippet&maxResults=1&key=" + Uri.EscapeDataString(key);
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? reason=null; string? googleMessage=null;
            try
            {
                using var doc=JsonDocument.Parse(body);
                if(doc.RootElement.TryGetProperty("error",out var er))
                {
                    googleMessage=er.TryGetProperty("message",out var gm)?gm.GetString():null;
                    if(er.TryGetProperty("errors",out var arr) && arr.ValueKind==JsonValueKind.Array)
                    {
                        var first=arr.EnumerateArray().FirstOrDefault();
                        if(first.ValueKind!=JsonValueKind.Undefined && first.TryGetProperty("reason",out var rr)) reason=rr.GetString();
                    }
                }
            } catch { }
            var msg = reason switch
            {
                "keyInvalid" => "A chave do YouTube é inválida. Gere uma nova chave no Google Cloud.",
                "accessNotConfigured" => "A YouTube Data API v3 não está habilitada neste projeto do Google Cloud.",
                "quotaExceeded" => "A cota da YouTube Data API v3 foi excedida.",
                "dailyLimitExceeded" => "O limite diário da YouTube Data API v3 foi excedido.",
                "ipRefererBlocked" => "A chave está bloqueada pela restrição de aplicativo configurada no Google Cloud.",
                "forbidden" => "O Google recusou a chave. Confira as restrições da chave e a YouTube Data API v3.",
                _ => "O Google recusou a chave do YouTube. Confira a API habilitada e as restrições."
            };
            AddLog(req.HttpContext,"YouTube Music API Diagnose",reason??"unknown",googleMessage??msg,me.Username);
            return Results.Json(new { success=false, code="youtube_api_error", reason, message=msg, googleMessage }, statusCode:502);
        }
        AddLog(req.HttpContext,"YouTube Music API Diagnose","ok","Chave do YouTube validada com sucesso.",me.Username);
        return Results.Ok(new { success=true, message="YouTube Data API v3 está respondendo corretamente." });
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { success=false, code="youtube_timeout", message="O YouTube demorou para responder. Tente novamente." }, statusCode:504);
    }
    catch
    {
        return Results.Json(new { success=false, code="youtube_network_error", message="Não foi possível conectar ao YouTube." }, statusCode:502);
    }
});

app.MapPost("/api/v1/admin/music/diagnose", async (YouTubeKeyRequest req, HttpContext ctx) =>
{
    var me = CurrentUser(ctx.Request);
    if (me is null) return Results.Unauthorized();
    if (me.Role != "Owner" && !HasPanelPermission(ctx.Request, "settings")) return Results.StatusCode(403);

    var settings = LoadSettings();
    var submittedKey = req.ApiKey?.Trim();
    var envKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY")?.Trim();
    var key = !string.IsNullOrWhiteSpace(submittedKey) ? submittedKey : (!string.IsNullOrWhiteSpace(settings.YouTubeApiKey) ? settings.YouTubeApiKey.Trim() : envKey);
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new { success=false, code="youtube_api_key_missing", message="Cole a chave da YouTube Data API v3 e clique em Salvar e testar." });

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = "https://www.googleapis.com/youtube/v3/i18nLanguages?part=snippet&maxResults=1&key=" + Uri.EscapeDataString(key);
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? reason=null; string? googleMessage=null;
            try
            {
                using var doc=JsonDocument.Parse(body);
                if(doc.RootElement.TryGetProperty("error",out var er))
                {
                    googleMessage=er.TryGetProperty("message",out var gm)?gm.GetString():null;
                    if(er.TryGetProperty("errors",out var arr) && arr.ValueKind==JsonValueKind.Array)
                    {
                        var first=arr.EnumerateArray().FirstOrDefault();
                        if(first.ValueKind!=JsonValueKind.Undefined && first.TryGetProperty("reason",out var rr)) reason=rr.GetString();
                    }
                }
            } catch { }
            var msg = reason switch
            {
                "keyInvalid" => "A chave do YouTube é inválida. Gere uma nova chave no Google Cloud.",
                "accessNotConfigured" => "A YouTube Data API v3 não está habilitada neste projeto do Google Cloud.",
                "quotaExceeded" => "A cota da YouTube Data API v3 foi excedida.",
                "dailyLimitExceeded" => "O limite diário da YouTube Data API v3 foi excedido.",
                "ipRefererBlocked" => "A chave está bloqueada pela restrição de aplicativo configurada no Google Cloud.",
                "forbidden" => "O Google recusou a chave. Confira as restrições da chave e a YouTube Data API v3.",
                _ => "O Google recusou a chave do YouTube. Confira a API habilitada e as restrições."
            };
            AddLog(ctx,"YouTube Music API Diagnose",reason??"unknown",googleMessage??msg,me.Username);
            return Results.Json(new { success=false, code="youtube_api_error", reason, message=msg, googleMessage }, statusCode:502);
        }
        AddLog(ctx,"YouTube Music API Diagnose","ok","Chave do YouTube validada com sucesso.",me.Username);
        return Results.Ok(new { success=true, message="YouTube Data API v3 está respondendo corretamente." });
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { success=false, code="youtube_timeout", message="O YouTube demorou para responder. Tente novamente." }, statusCode:504);
    }
    catch
    {
        return Results.Json(new { success=false, code="youtube_network_error", message="Não foi possível conectar ao YouTube." }, statusCode:502);
    }
});

app.MapGet("/api/v1/admin/music/search", async (HttpRequest req, string? q) =>
{
    var me = CurrentUser(req);
    if (me is null) return Results.Unauthorized();
    if (me.Role != "Owner" && !HasPanelPermission(req, "dashboard")) return Results.StatusCode(403);

    var query = (q ?? "").Trim();
    if (query.Length < 2 || query.Length > 120)
        return Results.BadRequest(new { success=false, message="Informe o nome da música (2 a 120 caracteres)." });

    var settings = LoadSettings();
    var envKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY")?.Trim();
    var key = !string.IsNullOrWhiteSpace(settings.YouTubeApiKey) ? settings.YouTubeApiKey.Trim() : envKey;
    // A pesquisa também possui fallback sem API key. Isso evita que uma chave
    // inválida/restrita/quota excedida deixe a página de música inutilizável.
    if (string.IsNullOrWhiteSpace(key))
    {
        try
        {
            var fallback = await SearchYouTubeWebAsync(query);
            if (fallback.Count > 0)
                return Results.Ok(new { success=true, source="youtube_web", results=fallback });
        }
        catch { }
        return Results.BadRequest(new { success=false, code="youtube_search_unavailable", message="Não foi possível pesquisar no YouTube agora. Verifique a conexão do servidor." });
    }

    try
    {
        using var client = new HttpClient { Timeout=TimeSpan.FromSeconds(15) };
        var searchUrl = "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=10&q="
            + Uri.EscapeDataString(query) + "&key=" + Uri.EscapeDataString(key);

        using var response = await client.GetAsync(searchUrl);
        var body = await response.Content.ReadAsStringAsync();

        if(!response.IsSuccessStatusCode)
        {
            string? reason=null, googleMessage=null;
            try
            {
                using var doc=JsonDocument.Parse(body);
                if(doc.RootElement.TryGetProperty("error",out var er))
                {
                    googleMessage=er.TryGetProperty("message",out var gm)?gm.GetString():null;
                    if(er.TryGetProperty("errors",out var errors) && errors.ValueKind==JsonValueKind.Array)
                    {
                        var first=errors.EnumerateArray().FirstOrDefault();
                        if(first.ValueKind!=JsonValueKind.Undefined && first.TryGetProperty("reason",out var rr)) reason=rr.GetString();
                    }
                }
            } catch {}

            var friendly = reason switch
            {
                "keyInvalid" => "A chave do YouTube é inválida.",
                "accessNotConfigured" => "A YouTube Data API v3 não está habilitada no projeto do Google.",
                "quotaExceeded" or "dailyLimitExceeded" => "A cota diária da YouTube Data API v3 foi excedida.",
                "ipRefererBlocked" => "A chave do YouTube está bloqueada pelas restrições de IP/HTTP referrer. Para este servidor, use uma chave permitida para requisições do servidor.",
                "forbidden" => "O Google recusou a requisição da chave do YouTube.",
                _ => "O Google recusou a pesquisa do YouTube."
            };
            AddLog(req.HttpContext,"YouTube Music API Error",reason ?? "unknown",$"{friendly} Google: {googleMessage}",me.Username);

            // Se a API oficial falhar (chave inválida, restrição, quota etc.),
            // tenta a pesquisa pública do YouTube antes de mostrar erro ao usuário.
            try
            {
                var fallback = await SearchYouTubeWebAsync(query);
                if (fallback.Count > 0)
                    return Results.Ok(new { success=true, source="youtube_web_fallback", results=fallback });
            }
            catch { }

            return Results.Json(new {success=false,code="youtube_api_error",reason,message=friendly,googleMessage},statusCode:502);
        }

        using var doc2=JsonDocument.Parse(body);
        if(!doc2.RootElement.TryGetProperty("items",out var items) || items.ValueKind!=JsonValueKind.Array)
            return Results.Json(new {success=false,code="youtube_invalid_response",message="Resposta inválida da YouTube Data API v3."},statusCode:502);

        var results=new List<object>();
        foreach(var item in items.EnumerateArray())
        {
            if(!item.TryGetProperty("id",out var id) || !id.TryGetProperty("videoId",out var vid)) continue;
            var videoId=vid.GetString();
            if(string.IsNullOrWhiteSpace(videoId)) continue;
            var snippet=item.TryGetProperty("snippet",out var sn)?sn:default;
            var title=snippet.ValueKind!=JsonValueKind.Undefined && snippet.TryGetProperty("title",out var te)?te.GetString()??query:query;
            var channel=snippet.ValueKind!=JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle",out var ce)?ce.GetString()??"YouTube":"YouTube";
            string? thumbnail=null;
            if(snippet.ValueKind!=JsonValueKind.Undefined && snippet.TryGetProperty("thumbnails",out var th) && th.TryGetProperty("high",out var hi) && hi.TryGetProperty("url",out var hu)) thumbnail=hu.GetString();
            results.Add(new {videoId,title,channel,thumbnail});
        }

        if(results.Count==0)
        {
            try
            {
                var fallback = await SearchYouTubeWebAsync(query);
                if (fallback.Count > 0)
                    return Results.Ok(new { success=true, source="youtube_web_fallback", results=fallback });
            }
            catch { }
            return Results.NotFound(new {success=false,message="O YouTube respondeu, mas não encontrou vídeos para essa pesquisa."});
        }

        AddLog(req.HttpContext,"YouTube Music Search",query,$"{results.Count} resultado(s) encontrado(s)",me.Username);
        return Results.Ok(new {success=true,results});
    }
    catch(HttpRequestException ex)
    {
        AddLog(req.HttpContext,"YouTube Music API Error","network",ex.Message,me.Username);
        return Results.Json(new {success=false,code="youtube_network_error",message="O servidor não conseguiu conectar ao YouTube."},statusCode:502);
    }
    catch(TaskCanceledException)
    {
        return Results.Json(new {success=false,code="youtube_timeout",message="O YouTube demorou para responder. Tente novamente."},statusCode:504);
    }
    catch(JsonException)
    {
        return Results.Json(new {success=false,code="youtube_invalid_response",message="O YouTube retornou uma resposta inválida."},statusCode:502);
    }
    catch(Exception ex)
    {
        AddLog(req.HttpContext,"YouTube Music API Error","unknown",ex.Message,me.Username);
        return Results.Json(new {success=false,code="youtube_unknown_error",message="Não foi possível consultar o YouTube agora."},statusCode:502);
    }
});

app.MapGet("/api/health", () => Results.Ok(new { success = true, service = "North Auth", time = DateTimeOffset.UtcNow }));

// Captura o IP do cliente que usou a licença. Em acesso direto, usa RemoteIpAddress;
// atrás de um proxy/reverse-proxy, considera X-Forwarded-For quando presente.
static string? GetClientIp(HttpContext ctx)
{
    var forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwarded))
        return forwarded.Split(',')[0].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString();
}

// Loader authentication
app.MapPost("/api/v1/license/register", (LicenseRequest req, HttpContext ctx) => {
    if (!Authorized(ctx.Request, req.ApiKey)) return Results.Unauthorized();
    var license = req.License?.Trim(); var username = req.Username?.Trim(); var hwid = req.Hwid?.Trim();
    if (string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(username)) return Results.BadRequest(new { success=false, message="License e Username sao obrigatorios." });
    var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase));
    if(item is null) return Results.NotFound(new {success=false,message="Licenca nao encontrada."});
    if(item.Banned) return Results.StatusCode(403); if(item.Used) return Results.BadRequest(new {success=false,message="Licenca ja foi registrada."});
    item.Username=username; item.Hwid=hwid; item.LastIp=GetClientIp(ctx); item.LastSeenAt=DateTimeOffset.UtcNow; item.LoginCount=1; item.Used=true; item.ExpiresAt=CalculateExpiry(item.Subscription); SaveLicenses(items); AddLog(ctx,"License Registered",item.License,$"Usuário={item.Username}; IP={item.LastIp}",item.Username);
    return Results.Ok(new {success=true,message="Licenca registrada com sucesso.",username=item.Username,subscription=item.Subscription,expiresAt=item.ExpiresAt});
});
app.MapPost("/api/v1/license/login", (LicenseRequest req, HttpContext ctx) => {
    if (!Authorized(ctx.Request, req.ApiKey)) return Results.Unauthorized();
    var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(req.License?.Trim()??"",StringComparison.OrdinalIgnoreCase));
    if(item is null) return Results.NotFound(new {success=false,message="Licenca invalida."}); if(item.Banned) return Results.StatusCode(403); if(!item.Used) return Results.BadRequest(new {success=false,message="Licenca ainda nao foi registrada."});
    if(!item.Username.Equals(req.Username?.Trim()??"",StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {success=false,message="Usuario incorreto."});
    if(!string.IsNullOrWhiteSpace(item.Hwid)&&!item.Hwid.Equals(req.Hwid?.Trim()??"",StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {success=false,message="HWID diferente do registrado."});
    if(item.ExpiresAt.HasValue&&item.ExpiresAt.Value<=DateTimeOffset.UtcNow) return Results.BadRequest(new {success=false,message="Licenca expirada."});
    item.LastIp=GetClientIp(ctx); item.LastSeenAt=DateTimeOffset.UtcNow; item.LoginCount++; SaveLicenses(items); AddLog(ctx,"License Login",item.License,$"Usuário={item.Username}; IP={item.LastIp}; Acesso #{item.LoginCount}",item.Username);
    return Results.Ok(new {success=true,message="Login autorizado.",username=item.Username,subscription=item.Subscription,expiresAt=item.ExpiresAt,ip=item.LastIp,lastSeenAt=item.LastSeenAt});
});

// Key management
app.MapGet("/api/v1/admin/licenses", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"licenses")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized();
    var items=LoadLicenses(); return Results.Ok(new {success=true,total=items.Count,licenses=items});
});
app.MapPost("/api/v1/admin/licenses/generate", (GenerateLicensesRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses_create")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var count=Math.Clamp(req.Count,1,500);
    var subscription=string.IsNullOrWhiteSpace(req.Subscription)?"3 Days":req.Subscription.Trim();
    var mask=string.IsNullOrWhiteSpace(req.Mask)?"NORTH-XXXX-XXXXXXXX":req.Mask.Trim();
    var items=LoadLicenses(); var created=new List<LicenseRecord>();
    for(var i=0;i<count;i++){
        string key; int tries=0;
        do { key=GenerateKey(mask, req.Uppercase, req.Lowercase); tries++; } while(items.Any(x=>x.License.Equals(key,StringComparison.OrdinalIgnoreCase)) && tries<100);
        if(items.Any(x=>x.License.Equals(key,StringComparison.OrdinalIgnoreCase))) continue;
        created.Add(new LicenseRecord{License=key,Subscription=subscription,Note=req.Note,ExpiresAt=CalculateExpiry(subscription, req.Duration, req.ExpiryUnit)});
        items.Add(created[^1]);
    }
    SaveLicenses(items); AddLog(ctx,"License Bulk Generated",$"{created.Count} key(s)", $"Subscription={subscription}"); return Results.Ok(new {success=true,count=created.Count,licenses=created});
});
app.MapPost("/api/v1/admin/license/create", (AdminLicenseRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses_create")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); if(string.IsNullOrWhiteSpace(req.License)) return Results.BadRequest(new {success=false,message="License obrigatoria."});
    var items=LoadLicenses(); if(items.Any(x=>x.License.Equals(req.License.Trim(),StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Licenca ja existe."});
    var created=new LicenseRecord{License=req.License.Trim(),Subscription=string.IsNullOrWhiteSpace(req.Subscription)?"3 Days":req.Subscription.Trim(),Note=req.Note}; items.Add(created); SaveLicenses(items); AddLog(ctx,"License Created",created.License,$"Subscription={created.Subscription}"); return Results.Ok(new {success=true,message="Licenca criada.",license=created});
});
app.MapPost("/api/v1/admin/license/{license}/ban", (string license, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses_reset_hwid")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase)); if(item is null)return Results.NotFound(); item.Banned=!item.Banned; SaveLicenses(items); AddLog(ctx,item.Banned?"License Banned":"License Unbanned",item.License,"Status da licença alterado"); return Results.Ok(new {success=true,banned=item.Banned});
});
app.MapDelete("/api/v1/admin/license/{license}", (string license, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); var items=LoadLicenses(); var removed=items.RemoveAll(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase)); if(removed==0)return Results.NotFound(); SaveLicenses(items); AddLog(ctx,"License Deleted",license,"Licença excluída"); return Results.Ok(new {success=true});
});
app.MapPost("/api/v1/admin/license/{license}/reset", (string license, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase)); if(item is null)return Results.NotFound(); item.Used=false; item.Username=null; item.Hwid=null; item.ExpiresAt=null; item.Banned=false; item.LastIp=null; item.LastSeenAt=null; item.LoginCount=0; SaveLicenses(items); AddLog(ctx,"License Reset",item.License,"HWID e vínculo resetados"); return Results.Ok(new {success=true,message="License resetada."});
});
app.MapPut("/api/v1/admin/license/{license}", (string license, [FromBody] LicenseUpdateRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var items=LoadLicenses();
    var item=items.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase));
    if(item is null) return Results.NotFound(new {success=false,message="Licença não encontrada."});
    if(req.Subscription is not null) item.Subscription=req.Subscription.Trim();
    if(req.Note is not null) item.Note=req.Note.Trim();
    if(req.Duration.HasValue && req.Duration.Value>0 && !string.IsNullOrWhiteSpace(req.ExpiryUnit))
        item.ExpiresAt=CalculateExpiry(item.Subscription,req.Duration,req.ExpiryUnit);
    else if(req.Subscription is not null)
        item.ExpiresAt=item.Used ? CalculateExpiry(item.Subscription) : null;
    SaveLicenses(items);
    AddLog(ctx,"License Updated",item.License,$"Subscription={item.Subscription}");
    return Results.Ok(new {success=true,license=item});
});


app.MapPost("/api/v1/admin/licenses/bulk", (BulkLicenseRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) {
        var required = string.Equals(req.Action?.Trim(), "reset", StringComparison.OrdinalIgnoreCase) ? "licenses_reset_hwid" : "licenses";
        if(!HasPanelPermission(ctx.Request,required)) return Results.StatusCode(403);
    } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var keys=(req.Licenses??new()).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if(keys.Count==0) return Results.BadRequest(new {success=false,message="Selecione pelo menos uma licença."});
    var action=(req.Action??"").Trim().ToLowerInvariant();
    var items=LoadLicenses(); var matched=items.Where(x=>keys.Contains(x.License,StringComparer.OrdinalIgnoreCase)).ToList();
    if(matched.Count==0) return Results.NotFound(new {success=false,message="Nenhuma licença encontrada."});
    if(action=="ban") foreach(var x in matched) x.Banned=true;
    else if(action=="unban") foreach(var x in matched) x.Banned=false;
    else if(action=="reset") foreach(var x in matched){x.Used=false;x.Username=null;x.Hwid=null;x.ExpiresAt=null;x.Banned=false;x.LastIp=null;x.LastSeenAt=null;x.LoginCount=0;}
    else if(action=="delete") items.RemoveAll(x=>keys.Contains(x.License,StringComparer.OrdinalIgnoreCase));
    else return Results.BadRequest(new {success=false,message="Ação inválida."});
    SaveLicenses(items);
    AddLog(ctx,"License Bulk Action",$"{matched.Count} key(s)",$"Ação={action}");
    return Results.Ok(new {success=true,count=matched.Count,action});
});


// Manage Plans / Subscriptions
app.MapGet("/api/v1/admin/plans", (HttpRequest req) => { if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"licenses")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized(); var plans=LoadPlans(); return Results.Ok(new {success=true,plans}); });
app.MapPost("/api/v1/admin/plans", (PlanRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var name=(req.Name??"").Trim(); if(name.Length<2) return Results.BadRequest(new {success=false,message="Nome do plano obrigatório."});
    if(req.Duration<=0) return Results.BadRequest(new {success=false,message="Duração inválida."});
    var plans=LoadPlans(); if(plans.Any(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Plano já existe."});
    var plan=new PlanRecord{Id=Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),Name=name,Duration=req.Duration,Unit=(req.Unit??"days").Trim().ToLowerInvariant(),Price=Math.Max(0,req.Price),Description=(req.Description??"").Trim(),Active=req.Active};
    plans.Add(plan); SavePlans(plans); AddLog(ctx,"Plan Created",plan.Name,$"Duração={plan.Duration} {plan.Unit}; Preço={plan.Price:0.00}"); return Results.Ok(new {success=true,plan});
});
app.MapPut("/api/v1/admin/plans/{id}", (string id, PlanRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var plans=LoadPlans(); var p=plans.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(p is null) return Results.NotFound();
    if(!string.IsNullOrWhiteSpace(req.Name)) p.Name=req.Name.Trim(); if(req.Duration>0)p.Duration=req.Duration; if(!string.IsNullOrWhiteSpace(req.Unit))p.Unit=req.Unit.Trim().ToLowerInvariant(); if(req.Price>=0)p.Price=req.Price; if(req.Description is not null)p.Description=req.Description.Trim(); p.Active=req.Active; SavePlans(plans); AddLog(ctx,"Plan Updated",p.Name,$"Duração={p.Duration} {p.Unit}; Preço={p.Price:0.00}"); return Results.Ok(new {success=true,plan=p});
});
app.MapDelete("/api/v1/admin/plans/{id}", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"licenses")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var plans=LoadPlans(); var p=plans.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(p is null)return Results.NotFound(); plans.Remove(p); SavePlans(plans); AddLog(ctx,"Plan Deleted",p.Name,"Plano excluído"); return Results.Ok(new {success=true});
});

// Manage Sales / Orders
app.MapGet("/api/v1/admin/orders", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"sales")) return Results.StatusCode(403); } else if(!Authorized(req)) return Results.Unauthorized();
    var orders=LoadOrders().OrderByDescending(x=>x.CreatedAt).ToList();
    var paid=orders.Where(x=>x.Status.Equals("paid",StringComparison.OrdinalIgnoreCase)).ToList();
    return Results.Ok(new {success=true,orders,total=orders.Count,paid=paid.Count,pending=orders.Count(x=>x.Status.Equals("pending",StringComparison.OrdinalIgnoreCase)),cancelled=orders.Count(x=>x.Status.Equals("cancelled",StringComparison.OrdinalIgnoreCase)),revenue=paid.Sum(x=>x.Amount)});
});
app.MapPost("/api/v1/admin/orders", (OrderRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"sales")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var status=(req.Status??"paid").Trim().ToLowerInvariant(); if(status is not ("paid" or "pending" or "cancelled")) return Results.BadRequest(new {success=false,message="Status inválido."});
    var plan=(req.Plan??"").Trim(); if(plan.Length<2) return Results.BadRequest(new {success=false,message="Plano obrigatório."});
    if(req.Amount<0) return Results.BadRequest(new {success=false,message="Valor inválido."});
    var o=new OrderRecord{Id="ORD-"+Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),Plan=plan,Customer=(req.Customer??"").Trim(),Amount=req.Amount,Status=status,Note=(req.Note??"").Trim(),CreatedAt=DateTimeOffset.UtcNow};
    var orders=LoadOrders(); orders.Add(o); if(orders.Count>5000) orders=orders.Skip(orders.Count-5000).ToList(); SaveOrders(orders); AddLog(ctx,"Sale Created",o.Id,$"Plano={o.Plan}; Valor={o.Amount:0.00}; Status={o.Status}"); return Results.Ok(new {success=true,order=o});
});
app.MapPut("/api/v1/admin/orders/{id}", (string id, OrderRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"sales")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var orders=LoadOrders(); var o=orders.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(o is null) return Results.NotFound();
    if(req.Plan is not null && req.Plan.Trim().Length>=2)o.Plan=req.Plan.Trim(); if(req.Customer is not null)o.Customer=req.Customer.Trim(); if(req.Amount>=0)o.Amount=req.Amount; if(req.Note is not null)o.Note=req.Note.Trim(); if(!string.IsNullOrWhiteSpace(req.Status)){var st=req.Status.Trim().ToLowerInvariant(); if(st is not ("paid" or "pending" or "cancelled"))return Results.BadRequest(new {success=false,message="Status inválido."}); o.Status=st;} SaveOrders(orders); AddLog(ctx,"Sale Updated",o.Id,$"Status={o.Status}; Valor={o.Amount:0.00}"); return Results.Ok(new {success=true,order=o});
});
app.MapDelete("/api/v1/admin/orders/{id}", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"sales")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var orders=LoadOrders(); var o=orders.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(o is null)return Results.NotFound(); orders.Remove(o); SaveOrders(orders); AddLog(ctx,"Sale Deleted",o.Id,"Venda removida"); return Results.Ok(new {success=true});
});

// Manage Subscriptions
app.MapGet("/api/v1/admin/subscriptions", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"subscriptions")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var now=DateTimeOffset.UtcNow;
    var subs=LoadSubscriptions();
    foreach(var x in subs) if(x.Status.Equals("active",StringComparison.OrdinalIgnoreCase) && x.ExpiresAt.HasValue && x.ExpiresAt.Value<=now) x.Status="expired";
    SaveSubscriptions(subs);
    return Results.Ok(new {success=true,subscriptions=subs.OrderByDescending(x=>x.CreatedAt),total=subs.Count,active=subs.Count(x=>x.Status=="active"),expired=subs.Count(x=>x.Status=="expired"),cancelled=subs.Count(x=>x.Status=="cancelled")});
});
app.MapPost("/api/v1/admin/subscriptions", (SubscriptionRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"subscriptions")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var license=(req.License??"").Trim(); var planName=(req.Plan??"").Trim();
    if(license.Length<3 || planName.Length<2) return Results.BadRequest(new {success=false,message="License e plano são obrigatórios."});
    var lic=LoadLicenses().FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase));
    if(lic is null) return Results.NotFound(new {success=false,message="Licença não encontrada."});
    if(lic.Banned) return Results.BadRequest(new {success=false,message="A licença está banida."});
    var plan=LoadPlans().FirstOrDefault(x=>x.Name.Equals(planName,StringComparison.OrdinalIgnoreCase));
    if(plan is null) return Results.NotFound(new {success=false,message="Plano não encontrado."});
    var subs=LoadSubscriptions();
    var existing=subs.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase) && x.Status=="active");
    if(existing is not null) return Results.Conflict(new {success=false,message="A licença já possui uma assinatura ativa."});
    var start=DateTimeOffset.UtcNow; var expiry=CalculateExpiry("",plan.Duration,plan.Unit);
    var sub=new SubscriptionRecord{Id="SUB-"+Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),License=lic.License,Customer=lic.Username??req.Customer?.Trim()??"",Plan=plan.Name,StartedAt=start,ExpiresAt=expiry,Status="active",AutoRenew=req.AutoRenew,Note=req.Note?.Trim()??""};
    subs.Add(sub); SaveSubscriptions(subs); AddLog(ctx,"Subscription Created",sub.Id,$"License={sub.License}; Plano={sub.Plan}; Expira={sub.ExpiresAt:O}");
    return Results.Ok(new {success=true,subscription=sub});
});
app.MapPut("/api/v1/admin/subscriptions/{id}", (string id, SubscriptionRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"subscriptions")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var subs=LoadSubscriptions(); var sub=subs.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(sub is null)return Results.NotFound();
    if(!string.IsNullOrWhiteSpace(req.Customer)) sub.Customer=req.Customer.Trim(); if(req.Note is not null)sub.Note=req.Note.Trim(); sub.AutoRenew=req.AutoRenew;
    if(!string.IsNullOrWhiteSpace(req.Status)){var st=req.Status.Trim().ToLowerInvariant();if(st is not ("active" or "expired" or "cancelled"))return Results.BadRequest(new {success=false,message="Status inválido."});sub.Status=st;}
    SaveSubscriptions(subs); AddLog(ctx,"Subscription Updated",sub.Id,$"Status={sub.Status}"); return Results.Ok(new {success=true,subscription=sub});
});
app.MapPost("/api/v1/admin/subscriptions/{id}/renew", (string id, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"subscriptions")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request)) return Results.Unauthorized();
    var subs=LoadSubscriptions(); var sub=subs.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(sub is null)return Results.NotFound();
    var plan=LoadPlans().FirstOrDefault(x=>x.Name.Equals(sub.Plan,StringComparison.OrdinalIgnoreCase)); if(plan is null)return Results.NotFound(new {success=false,message="Plano da assinatura não encontrado."});
    var baseDate=sub.ExpiresAt.HasValue && sub.ExpiresAt.Value>DateTimeOffset.UtcNow?sub.ExpiresAt.Value:DateTimeOffset.UtcNow;
    var durationExpiry=CalculateExpiry("",plan.Duration,plan.Unit); var added=durationExpiry.HasValue?durationExpiry.Value-DateTimeOffset.UtcNow:TimeSpan.Zero; sub.StartedAt=DateTimeOffset.UtcNow; sub.ExpiresAt=baseDate+added; sub.Status="active";
    SaveSubscriptions(subs); AddLog(ctx,"Subscription Renewed",sub.Id,$"Plano={sub.Plan}; Nova expiração={sub.ExpiresAt:O}"); return Results.Ok(new {success=true,subscription=sub});
});
app.MapPost("/api/v1/admin/subscriptions/{id}/cancel", (string id, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"subscriptions")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request)) return Results.Unauthorized();
    var subs=LoadSubscriptions(); var sub=subs.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(sub is null)return Results.NotFound(); sub.Status="cancelled"; SaveSubscriptions(subs); AddLog(ctx,"Subscription Cancelled",sub.Id,$"License={sub.License}"); return Results.Ok(new {success=true,subscription=sub});
});
app.MapDelete("/api/v1/admin/subscriptions/{id}", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"subscriptions")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var subs=LoadSubscriptions(); var sub=subs.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(sub is null)return Results.NotFound(); subs.Remove(sub); SaveSubscriptions(subs); AddLog(ctx,"Subscription Deleted",sub.Id,"Assinatura removida"); return Results.Ok(new {success=true});
});

// Manage Applications
app.MapGet("/api/v1/admin/apps", (HttpRequest req) => { if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"applications")) return Results.StatusCode(403); } else if(!Authorized(req))return Results.Unauthorized(); var apps=LoadApps(); return Results.Ok(new {success=true,total=apps.Count,apps}); });
app.MapPost("/api/v1/admin/apps/create", (AppCreateRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"applications")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey))return Results.Unauthorized(); if(string.IsNullOrWhiteSpace(req.Name))return Results.BadRequest(new {success=false,message="Nome obrigatorio."});
    var apps=LoadApps(); if(apps.Any(x=>x.Name.Equals(req.Name.Trim(),StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new {success=false,message="Aplicacao ja existe."});
    var item=new AppRecord{Id=Guid.NewGuid().ToString("N"),Name=req.Name.Trim(),Version=string.IsNullOrWhiteSpace(req.Version)?"1.0":req.Version.Trim(),Description=req.Description?.Trim()??"",Active=true,CreatedAt=DateTimeOffset.UtcNow,OwnerId="NORTH-OWNER-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),Secret="NORTH-APP-"+Guid.NewGuid().ToString("N")[..24].ToUpperInvariant()}; apps.Add(item); SaveApps(apps); return Results.Ok(new {success=true,app=item});
});
app.MapPut("/api/v1/admin/apps/{id}", (string id, [FromBody] AppUpdateRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"applications")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey))return Results.Unauthorized(); var apps=LoadApps(); var item=apps.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null)return Results.NotFound();
    if(req.Name is not null)item.Name=req.Name.Trim(); if(req.Version is not null)item.Version=req.Version.Trim(); if(req.Description is not null)item.Description=req.Description.Trim(); if(req.Active.HasValue)item.Active=req.Active.Value; SaveApps(apps); return Results.Ok(new {success=true,app=item});
});
app.MapPost("/api/v1/admin/apps/{id}/secret/regenerate", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"applications")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var apps=LoadApps(); var item=apps.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
    if(item is null) return Results.NotFound(new {success=false,message="Aplicacao nao encontrada."});
    item.Secret="NORTH-APP-"+Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
    if(string.IsNullOrWhiteSpace(item.OwnerId)) item.OwnerId="NORTH-OWNER-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    SaveApps(apps);
    return Results.Ok(new {success=true,message="Client Secret regenerado.",secret=item.Secret,ownerId=item.OwnerId,app=item});
});
app.MapDelete("/api/v1/admin/apps/{id}", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => { if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"applications")) return Results.StatusCode(403); } else if(!Authorized(ctx.Request,req.ApiKey))return Results.Unauthorized(); var apps=LoadApps(); var removed=apps.RemoveAll(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(removed==0)return Results.NotFound(); SaveApps(apps); return Results.Ok(new {success=true}); });


// Premium 16.0 — Arquivos do painel (armazenamento local seguro)
app.MapGet("/api/v1/admin/files", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"files")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var items=Directory.EnumerateFiles(filesDir,"*",SearchOption.TopDirectoryOnly).Select(f=>new {
        name=Path.GetFileName(f), size=new FileInfo(f).Length, modifiedAt=File.GetLastWriteTimeUtc(f)
    }).OrderByDescending(x=>x.modifiedAt).ToList();
    return Results.Ok(new {success=true,total=items.Count,files=items});
});
app.MapPost("/api/v1/admin/files/upload", async (HttpRequest req, HttpContext ctx) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"files")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    if(!req.HasFormContentType) return Results.BadRequest(new {success=false,message="Envie multipart/form-data."});
    var form=await req.ReadFormAsync();
    var file=form.Files.GetFile("file");
    if(file is null || file.Length==0) return Results.BadRequest(new {success=false,message="Selecione um arquivo."});
    if(file.Length>25*1024*1024) return Results.BadRequest(new {success=false,message="Limite de 25 MB por arquivo."});
    var safe=Path.GetFileName(file.FileName).Trim();
    if(string.IsNullOrWhiteSpace(safe) || safe=="." || safe=="..") return Results.BadRequest(new {success=false,message="Nome de arquivo inválido."});
    safe=System.Text.RegularExpressions.Regex.Replace(safe,"[^A-Za-z0-9._ -]","_");
    var destination=Path.Combine(filesDir,safe);
    await using(var stream=File.Create(destination)) await file.CopyToAsync(stream);
    AddLog(ctx,"File Uploaded",safe,$"{file.Length} bytes");
    return Results.Ok(new {success=true,name=safe,size=file.Length});
});
app.MapDelete("/api/v1/admin/files/{name}", (string name, HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"files")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var safe=Path.GetFileName(name); if(!string.Equals(safe,name,StringComparison.Ordinal)) return Results.BadRequest(new {success=false,message="Nome inválido."});
    var target=Path.Combine(filesDir,safe); if(!File.Exists(target)) return Results.NotFound(); File.Delete(target); return Results.Ok(new {success=true});
});
app.MapGet("/api/v1/admin/files/download/{name}", (string name, HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"files")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var safe=Path.GetFileName(name); if(!string.Equals(safe,name,StringComparison.Ordinal)) return Results.BadRequest();
    var target=Path.Combine(filesDir,safe); if(!File.Exists(target)) return Results.NotFound();
    return Results.File(target,"application/octet-stream",safe);
});

// Premium 17.0 — Variáveis por aplicação
app.MapGet("/api/v1/admin/variables", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"variables")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var items=LoadVariables().OrderByDescending(x=>x.UpdatedAt).ToList();
    return Results.Ok(new {success=true,total=items.Count,active=items.Count(x=>x.Active),variables=items});
});
app.MapPost("/api/v1/admin/variables", (VariableRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"variables")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var appId=(req.AppId??"").Trim(); var name=(req.Name??"").Trim();
    if(string.IsNullOrWhiteSpace(appId)||string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new {success=false,message="Aplicação e nome são obrigatórios."});
    var apps=LoadApps(); if(!apps.Any(x=>x.Id.Equals(appId,StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new {success=false,message="Aplicação não encontrada."});
    var items=LoadVariables(); if(items.Any(x=>x.AppId.Equals(appId,StringComparison.OrdinalIgnoreCase)&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Já existe uma variável com esse nome nesta aplicação."});
    var now=DateTimeOffset.UtcNow; var item=new VariableRecord{Id=Guid.NewGuid().ToString("N"),AppId=appId,Name=name,Value=req.Value??"",Type=NormalizeVariableType(req.Type),Description=(req.Description??"").Trim(),Active=req.Active,CreatedAt=now,UpdatedAt=now};
    items.Add(item); SaveVariables(items); AddLog(ctx,"Variable Created",item.Name,$"Aplicação: {item.AppId}"); return Results.Ok(new {success=true,variable=item});
});
app.MapPut("/api/v1/admin/variables/{id}", (string id, VariableRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"variables")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var items=LoadVariables(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Variável não encontrada."});
    var appId=(req.AppId??item.AppId).Trim(); var name=(req.Name??item.Name).Trim(); if(string.IsNullOrWhiteSpace(appId)||string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new {success=false,message="Aplicação e nome são obrigatórios."});
    var apps=LoadApps(); if(!apps.Any(x=>x.Id.Equals(appId,StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new {success=false,message="Aplicação não encontrada."});
    if(items.Any(x=>x.Id!=item.Id&&x.AppId.Equals(appId,StringComparison.OrdinalIgnoreCase)&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Já existe uma variável com esse nome nesta aplicação."});
    item.AppId=appId; item.Name=name; item.Value=req.Value??""; item.Type=NormalizeVariableType(req.Type); item.Description=(req.Description??"").Trim(); item.Active=req.Active; item.UpdatedAt=DateTimeOffset.UtcNow; SaveVariables(items); AddLog(ctx,"Variable Updated",item.Name,$"Aplicação: {item.AppId}"); return Results.Ok(new {success=true,variable=item});
});
app.MapDelete("/api/v1/admin/variables/{id}", (string id, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"variables")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request)) return Results.Unauthorized();
    var items=LoadVariables(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Variável não encontrada."});
    items.Remove(item); SaveVariables(items); AddLog(ctx,"Variable Deleted",item.Name,$"Aplicação: {item.AppId}"); return Results.Ok(new {success=true});
});

// Premium 19.0 — Tokens administrativos do painel (não são usados pelo loader)
app.MapGet("/api/v1/admin/tokens", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"tokens")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var now=DateTimeOffset.UtcNow;
    var items=LoadTokens().OrderByDescending(x=>x.CreatedAt).Select(x=>new {
        x.Id,x.Name,x.Prefix,x.Scope,x.CreatedBy,x.CreatedAt,x.ExpiresAt,x.Revoked,x.LastUsedAt,
        status=x.Revoked?"revoked":(x.ExpiresAt.HasValue&&x.ExpiresAt.Value<=now?"expired":"active")
    }).ToList();
    return Results.Ok(new {success=true,total=items.Count,active=items.Count(x=>x.status=="active"),tokens=items});
});
app.MapPost("/api/v1/admin/tokens", (TokenRequest req, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null) return Results.Unauthorized(); if(!HasPanelPermission(ctx.Request,"tokens")) return Results.StatusCode(403);
    var name=(req.Name??"").Trim(); if(string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new {success=false,message="Informe um nome para o token."});
    var scope=(req.Scope??"admin").Trim().ToLowerInvariant(); if(scope is not "admin" and not "read") scope="admin";
    var days=Math.Clamp(req.ExpiresInDays,1,3650);
    var raw="nrt_"+Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(30)).Replace("+","-").Replace("/","_").TrimEnd('=');
    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    var now=DateTimeOffset.UtcNow; var item=new TokenRecord{Id=Guid.NewGuid().ToString("N"),Name=name,Prefix=raw[..Math.Min(12,raw.Length)],Hash=hash,Scope=scope,CreatedBy=me.Username,CreatedAt=now,ExpiresAt=now.AddDays(days),Revoked=false};
    var items=LoadTokens(); items.Add(item); SaveTokens(items); AddLog(ctx,"Admin Token Created",item.Name,$"Escopo: {item.Scope} · Expira em {item.ExpiresAt:yyyy-MM-dd}",me.Username);
    return Results.Ok(new {success=true,token=raw,item=new {item.Id,item.Name,item.Prefix,item.Scope,item.CreatedBy,item.CreatedAt,item.ExpiresAt,item.Revoked}});
});
app.MapPost("/api/v1/admin/tokens/{id}/revoke", (string id, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null) return Results.Unauthorized(); if(!HasPanelPermission(ctx.Request,"tokens")) return Results.StatusCode(403);
    var items=LoadTokens(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Token não encontrado."});
    if(item.Revoked) return Results.Ok(new {success=true,message="Token já estava revogado."});
    item.Revoked=true; SaveTokens(items); AddLog(ctx,"Admin Token Revoked",item.Name,"Token revogado",me.Username); return Results.Ok(new {success=true});
});
app.MapDelete("/api/v1/admin/tokens/{id}", (string id, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null) return Results.Unauthorized(); if(!HasPanelPermission(ctx.Request,"tokens")) return Results.StatusCode(403);
    var items=LoadTokens(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Token não encontrado."});
    items.Remove(item); SaveTokens(items); AddLog(ctx,"Admin Token Deleted",item.Name,"Token excluído",me.Username); return Results.Ok(new {success=true});
});
app.MapPost("/api/v1/admin/tokens/{id}/rotate", (string id, HttpContext ctx) => {
    var me=CurrentUser(ctx.Request); if(me is null) return Results.Unauthorized(); if(!HasPanelPermission(ctx.Request,"tokens")) return Results.StatusCode(403);
    var items=LoadTokens(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Token não encontrado."});
    if(item.Revoked) return Results.BadRequest(new {success=false,message="Revogue tokens antigos antes de criar um novo."});
    item.Revoked=true;
    var raw="nrt_"+Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(30)).Replace("+","-").Replace("/","_").TrimEnd('=');
    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    var now=DateTimeOffset.UtcNow; if(item.ExpiresAt <= now) return Results.BadRequest(new {success=false,message="Token expirado não pode ser rotacionado."}); var remainingDays=Math.Max(1, (int)Math.Ceiling((item.ExpiresAt.Value-now).TotalDays)); var replacement=new TokenRecord{Id=Guid.NewGuid().ToString("N"),Name=item.Name+" (rotacionado)",Prefix=raw[..Math.Min(12,raw.Length)],Hash=hash,Scope=item.Scope,CreatedBy=me.Username,CreatedAt=now,ExpiresAt=now.AddDays(remainingDays),Revoked=false};
    items.Add(replacement); SaveTokens(items); AddLog(ctx,"Admin Token Rotated",item.Name,"Token anterior revogado e novo token criado",me.Username);
    return Results.Ok(new {success=true,token=raw,item=new {replacement.Id,replacement.Name,replacement.Prefix,replacement.Scope,replacement.CreatedBy,replacement.CreatedAt,replacement.ExpiresAt,replacement.Revoked}});
});

// Premium 19.2 — Regras administrativas por aplicação
// Premium 22.0 — Central de notificações
app.MapGet("/api/v1/admin/notifications", (HttpRequest req) =>
{
    if (!HasPanelPermission(req, "notifications")) return Results.StatusCode(403);
    var items = LoadNotifications().OrderByDescending(x => x.CreatedAt).Take(500).ToList();
    var unread = items.Count(x => !x.Read);
    return Results.Ok(new { success = true, notifications = items, unread });
});

app.MapPost("/api/v1/admin/notifications", (NotificationRequest req, HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "notifications")) return Results.StatusCode(403);
    var title = (req.Title ?? "").Trim();
    var message = (req.Message ?? "").Trim();
    if (title.Length < 2 || title.Length > 120) return Results.BadRequest(new { success = false, message = "Título deve ter entre 2 e 120 caracteres." });
    if (message.Length < 1 || message.Length > 2000) return Results.BadRequest(new { success = false, message = "Mensagem deve ter entre 1 e 2000 caracteres." });
    var priority = NormalizeNotificationPriority(req.Priority);
    var items = LoadNotifications();
    var n = new NotificationRecord { Id = Guid.NewGuid().ToString("N"), Title = title, Message = message, Priority = priority, CreatedBy = CurrentUser(ctx.Request)?.Username ?? "system", CreatedAt = DateTimeOffset.UtcNow, Read = false };
    items.Add(n);
    if (items.Count > 1000) items = items.OrderByDescending(x => x.CreatedAt).Take(1000).ToList();
    SaveNotifications(items);
    AddLog(ctx, "Notification Created", n.Title, priority);
    return Results.Ok(new { success = true, notification = n });
});

app.MapPut("/api/v1/admin/notifications/{id}", (string id, NotificationRequest req, HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "notifications")) return Results.StatusCode(403);
    var items = LoadNotifications();
    var n = items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (n is null) return Results.NotFound(new { success = false, message = "Notificação não encontrada." });
    var title = (req.Title ?? n.Title).Trim();
    var message = (req.Message ?? n.Message).Trim();
    if (title.Length < 2 || title.Length > 120 || message.Length < 1 || message.Length > 2000) return Results.BadRequest(new { success = false, message = "Título ou mensagem inválidos." });
    n.Title = title; n.Message = message; n.Priority = NormalizeNotificationPriority(req.Priority ?? n.Priority); n.UpdatedAt = DateTimeOffset.UtcNow;
    SaveNotifications(items);
    AddLog(ctx, "Notification Updated", n.Title, n.Priority);
    return Results.Ok(new { success = true, notification = n });
});

app.MapPost("/api/v1/admin/notifications/{id}/read", (string id, HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "notifications")) return Results.StatusCode(403);
    var items = LoadNotifications(); var n = items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (n is null) return Results.NotFound(new { success = false, message = "Notificação não encontrada." });
    n.Read = true; n.ReadAt = DateTimeOffset.UtcNow; n.ReadBy = CurrentUser(ctx.Request)?.Username ?? "system"; SaveNotifications(items);
    return Results.Ok(new { success = true });
});

app.MapPost("/api/v1/admin/notifications/read-all", (HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "notifications")) return Results.StatusCode(403);
    var items = LoadNotifications(); var user = CurrentUser(ctx.Request)?.Username ?? "system"; var count = 0;
    foreach (var n in items.Where(x => !x.Read)) { n.Read = true; n.ReadAt = DateTimeOffset.UtcNow; n.ReadBy = user; count++; }
    SaveNotifications(items); return Results.Ok(new { success = true, count });
});

app.MapDelete("/api/v1/admin/notifications/{id}", (string id, HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "notifications")) return Results.StatusCode(403);
    var items = LoadNotifications(); var n = items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (n is null) return Results.NotFound(new { success = false, message = "Notificação não encontrada." });
    items.Remove(n); SaveNotifications(items); AddLog(ctx, "Notification Deleted", n.Title, n.Priority); return Results.Ok(new { success = true });
});

// Premium 21.0 — Bate-papo interno da equipe
app.MapGet("/api/v1/admin/chat/messages", (HttpRequest req) =>
{
    if (!HasPanelPermission(req, "chat")) return Results.StatusCode(403);
    var channel = (req.Query["channel"].FirstOrDefault() ?? "geral").Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(channel, "^[a-z0-9_-]{1,32}$")) channel = "geral";
    var messages = LoadChatMessages().Where(x => x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.CreatedAt).TakeLast(300).ToList();
    return Results.Ok(new { success = true, channel, messages });
});

app.MapPost("/api/v1/admin/chat/messages", (ChatMessageRequest req, HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "chat")) return Results.StatusCode(403);
    var user = CurrentUser(ctx.Request);
    if (user is null) return Results.Unauthorized();
    var channel = (req.Channel ?? "geral").Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(channel, "^[a-z0-9_-]{1,32}$")) return Results.BadRequest(new { success = false, message = "Canal inválido." });
    var text = (req.Text ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { success = false, message = "Digite uma mensagem." });
    if (text.Length > 2000) return Results.BadRequest(new { success = false, message = "A mensagem deve ter no máximo 2000 caracteres." });
    var items = LoadChatMessages();
    var msg = new ChatMessageRecord { Id = Guid.NewGuid().ToString("N"), Channel = channel, Sender = user.Username, DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName, Text = text, CreatedAt = DateTimeOffset.UtcNow };
    items.Add(msg);
    if (items.Count > 10000) items = items.Skip(items.Count - 10000).ToList();
    SaveChatMessages(items);
    AddLog(ctx, "Chat Message Sent", channel, text.Length > 120 ? text[..120] : text, user.Username);
    return Results.Ok(new { success = true, message = msg });
});

app.MapDelete("/api/v1/admin/chat/messages/{id}", (string id, HttpContext ctx) =>
{
    if (!HasPanelPermission(ctx.Request, "chat")) return Results.StatusCode(403);
    var user = CurrentUser(ctx.Request);
    if (user is null) return Results.Unauthorized();
    var items = LoadChatMessages();
    var msg = items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (msg is null) return Results.NotFound();
    if (!user.Role.Equals("Owner", StringComparison.OrdinalIgnoreCase) && !msg.Sender.Equals(user.Username, StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(403);
    items.Remove(msg);
    SaveChatMessages(items);
    AddLog(ctx, "Chat Message Deleted", msg.Channel, msg.Id, user.Username);
    return Results.Ok(new { success = true });
});

app.MapGet("/api/v1/admin/chat/presence", (HttpRequest req) =>
{
    if (!HasPanelPermission(req, "chat")) return Results.StatusCode(403);
    List<string> active;
    lock (sessionLock)
    {
        var now = DateTimeOffset.UtcNow;
        active = sessions.Values.Where(x => x.ExpiresAt > now && x.LastSeenAt >= now.AddMinutes(-5)).Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    var users = LoadUsers().Where(x => x.Active && active.Contains(x.Username, StringComparer.OrdinalIgnoreCase)).Select(x => new { username = x.Username, displayName = string.IsNullOrWhiteSpace(x.DisplayName) ? x.Username : x.DisplayName, role = x.Role }).ToList();
    return Results.Ok(new { success = true, users });
});

app.MapGet("/api/v1/admin/rules", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"rules")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var items=LoadRules().OrderBy(x=>x.Priority).ThenByDescending(x=>x.UpdatedAt).ToList();
    return Results.Ok(new {success=true,total=items.Count,active=items.Count(x=>x.Active),rules=items});
});
app.MapPost("/api/v1/admin/rules", (RuleRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"rules")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var appId=(req.AppId??"").Trim(); var name=(req.Name??"").Trim();
    if(string.IsNullOrWhiteSpace(appId)) return Results.BadRequest(new {success=false,message="Selecione uma aplicação."});
    if(string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new {success=false,message="Informe o nome da regra."});
    var apps=LoadApps(); if(!apps.Any(x=>x.Id.Equals(appId,StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new {success=false,message="Aplicação não encontrada."});
    var items=LoadRules(); if(items.Any(x=>x.AppId.Equals(appId,StringComparison.OrdinalIgnoreCase)&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Já existe uma regra com esse nome nesta aplicação."});
    var now=DateTimeOffset.UtcNow; var item=new RuleRecord{Id=Guid.NewGuid().ToString("N"),AppId=appId,Name=name,Type=NormalizeRuleType(req.Type),Operator=NormalizeRuleOperator(req.Operator),Value=req.Value??"",Description=(req.Description??"").Trim(),Active=req.Active,Priority=Math.Clamp(req.Priority,0,9999),CreatedAt=now,UpdatedAt=now};
    items.Add(item); SaveRules(items); AddLog(ctx,"Rule Created",item.Name,$"Aplicação: {item.AppId}"); return Results.Ok(new {success=true,rule=item});
});
app.MapPut("/api/v1/admin/rules/{id}", (string id, RuleRequest req, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"rules")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var items=LoadRules(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Regra não encontrada."});
    var appId=(req.AppId??"").Trim(); var name=(req.Name??"").Trim(); if(string.IsNullOrWhiteSpace(appId)||string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new {success=false,message="Aplicação e nome são obrigatórios."});
    if(items.Any(x=>x.Id!=item.Id&&x.AppId.Equals(appId,StringComparison.OrdinalIgnoreCase)&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Já existe uma regra com esse nome nesta aplicação."});
    item.AppId=appId; item.Name=name; item.Type=NormalizeRuleType(req.Type); item.Operator=NormalizeRuleOperator(req.Operator); item.Value=req.Value??""; item.Description=(req.Description??"").Trim(); item.Active=req.Active; item.Priority=Math.Clamp(req.Priority,0,9999); item.UpdatedAt=DateTimeOffset.UtcNow; SaveRules(items); AddLog(ctx,"Rule Updated",item.Name,$"Aplicação: {item.AppId}"); return Results.Ok(new {success=true,rule=item});
});
app.MapDelete("/api/v1/admin/rules/{id}", (string id, HttpContext ctx) => {
    if(PanelAuthorized(ctx.Request)) { if(!HasPanelPermission(ctx.Request,"rules")) return Results.StatusCode(403); }
    else if(!Authorized(ctx.Request)) return Results.Unauthorized();
    var items=LoadRules(); var item=items.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null) return Results.NotFound(new {success=false,message="Regra não encontrada."});
    items.Remove(item); SaveRules(items); AddLog(ctx,"Rule Deleted",item.Name,$"Aplicação: {item.AppId}"); return Results.Ok(new {success=true});
});

// Premium 20.0 — Dashboard avançado: séries diárias e ranking de aplicações.
app.MapGet("/api/v1/admin/settings", (HttpRequest req) => {
    if(!HasPanelPermission(req,"settings")) return Results.StatusCode(403);
    var x=LoadSettings();
    return Results.Ok(new { success=true, serverName=x.ServerName, sessionHours=x.SessionHours, maintenance=x.Maintenance, registrationEnabled=x.RegistrationEnabled, defaultLicenseDays=x.DefaultLicenseDays, auditRetention=x.AuditRetention, backupEnabled=x.BackupEnabled, backupRetentionCount=x.BackupRetentionCount, backupIntervalHours=x.BackupIntervalHours, lastAutomaticBackupAt=x.LastAutomaticBackupAt, cpuAlertPercent=x.CpuAlertPercent, memoryAlertMb=x.MemoryAlertMb, errorAlertCount=x.ErrorAlertCount, backupStaleHours=x.BackupStaleHours, youtubeApiKeyConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOUTUBE_API_KEY")) || !string.IsNullOrWhiteSpace(x.YouTubeApiKey) });
});
app.MapPut("/api/v1/admin/settings", (ServerSettingsRequest req, HttpContext ctx) => {
    if(!HasPanelPermission(ctx.Request,"settings")) return Results.StatusCode(403);
    var x=LoadSettings();
    if(!string.IsNullOrWhiteSpace(req.ServerName)) x.ServerName=req.ServerName.Trim()[..Math.Min(80,req.ServerName.Trim().Length)];
    x.SessionHours=Math.Clamp(req.SessionHours,1,72);
    x.Maintenance=req.Maintenance;
    x.RegistrationEnabled=req.RegistrationEnabled;
    x.DefaultLicenseDays=Math.Clamp(req.DefaultLicenseDays,1,3650);
    x.AuditRetention=Math.Clamp(req.AuditRetention,100,20000);
    x.BackupEnabled=req.BackupEnabled;
    x.BackupRetentionCount=Math.Clamp(req.BackupRetentionCount,1,100);
    x.BackupIntervalHours=Math.Clamp(req.BackupIntervalHours,1,168);
    x.CpuAlertPercent=Math.Clamp(req.CpuAlertPercent,50,100);
    x.MemoryAlertMb=Math.Clamp(req.MemoryAlertMb,256,32768);
    x.ErrorAlertCount=Math.Clamp(req.ErrorAlertCount,1,1000);
    x.BackupStaleHours=Math.Clamp(req.BackupStaleHours,1,720);
    if(req.YouTubeApiKey is not null) x.YouTubeApiKey=req.YouTubeApiKey.Trim();
    SaveSettings(x); AddLog(ctx,"Settings Updated","server",$"Configurações atualizadas: {x.ServerName}");
    return Results.Ok(new { success=true, message="Configurações salvas", serverName=x.ServerName, sessionHours=x.SessionHours, maintenance=x.Maintenance, registrationEnabled=x.RegistrationEnabled, defaultLicenseDays=x.DefaultLicenseDays, auditRetention=x.AuditRetention, backupEnabled=x.BackupEnabled, backupRetentionCount=x.BackupRetentionCount, backupIntervalHours=x.BackupIntervalHours, lastAutomaticBackupAt=x.LastAutomaticBackupAt, cpuAlertPercent=x.CpuAlertPercent, memoryAlertMb=x.MemoryAlertMb, errorAlertCount=x.ErrorAlertCount, backupStaleHours=x.BackupStaleHours, youtubeApiKeyConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOUTUBE_API_KEY")) || !string.IsNullOrWhiteSpace(x.YouTubeApiKey) });
});


string SafeBackupName(string name) => Path.GetFileName(name);
void CleanupBackups(int keep)
{
    keep=Math.Clamp(keep,1,100);
    var list=Directory.Exists(backupsDir)?Directory.GetFiles(backupsDir,"*.zip").OrderByDescending(File.GetCreationTimeUtc).ToList():new List<string>();
    foreach(var old in list.Skip(keep)){try{File.Delete(old);}catch{}}
}
void CreateDataBackup(string destination)
{
    var tmp=destination+".tmp";
    if(File.Exists(tmp)) File.Delete(tmp);
    using(var archive=ZipFile.Open(tmp,ZipArchiveMode.Create))
    {
        if(Directory.Exists(dataDir))
            foreach(var file in Directory.GetFiles(dataDir,"*.json",SearchOption.TopDirectoryOnly))
                archive.CreateEntryFromFile(file,"Data/"+Path.GetFileName(file),CompressionLevel.Optimal);
    }
    if(File.Exists(destination)) File.Delete(destination);
    File.Move(tmp,destination);
}
app.MapGet("/api/v1/admin/backups", (HttpRequest req) =>
{
    if(!HasPanelPermission(req,"backups")) return Results.StatusCode(403);
    var items=Directory.GetFiles(backupsDir,"*.zip").Select(f=>new {name=Path.GetFileName(f),size=new FileInfo(f).Length,createdAt=File.GetCreationTimeUtc(f)}).OrderByDescending(x=>x.createdAt).ToList();
    var st=LoadSettings();
    return Results.Ok(new {success=true,enabled=st.BackupEnabled,retentionCount=st.BackupRetentionCount,backups=items});
});
app.MapPost("/api/v1/admin/backups", (HttpRequest req, HttpContext ctx) =>
{
    if(!HasPanelPermission(req,"backups")) return Results.StatusCode(403);
    var stamp=DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
    var name=$"northauth_backup_{stamp}.zip";
    var path=Path.Combine(backupsDir,name);
    try { CreateDataBackup(path); CleanupBackups(LoadSettings().BackupRetentionCount); AddLog(ctx,"Backup Created",name,"Backup manual dos dados criado"); return Results.Ok(new {success=true,name,message="Backup criado com sucesso",size=new FileInfo(path).Length}); }
    catch(Exception ex){return Results.Problem("Falha ao criar backup: "+ex.Message);}
});
app.MapPost("/api/v1/admin/backups/{name}/restore", (string name, HttpRequest req, HttpContext ctx) =>
{
    if(!HasPanelPermission(req,"backups")) return Results.StatusCode(403);
    name=SafeBackupName(name); if(!name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {message="Backup inválido."});
    var path=Path.Combine(backupsDir,name); if(!File.Exists(path)) return Results.NotFound(new {message="Backup não encontrado."});
    try
    {
        var safety=Path.Combine(backupsDir,$"pre_restore_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip"); CreateDataBackup(safety);
        using var archive=ZipFile.OpenRead(path);
        var entries=archive.Entries.Where(e=>e.FullName.StartsWith("Data/",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrEmpty(e.Name)).ToList();
        if(entries.Count==0) return Results.BadRequest(new {message="Backup sem dados válidos."});
        foreach(var e in entries){var rel=e.FullName[5..].Replace('\\','/'); if(rel.Contains("..")||Path.IsPathRooted(rel)||!rel.EndsWith(".json",StringComparison.OrdinalIgnoreCase)) continue; var dest=Path.Combine(dataDir,rel.Replace('/',Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); using var input=e.Open(); using var output=File.Create(dest); input.CopyTo(output);}
        AddLog(ctx,"Backup Restored",name,"Backup restaurado; um backup de segurança pré-restauração foi criado"); CleanupBackups(Math.Max(LoadSettings().BackupRetentionCount,10));
        return Results.Ok(new {success=true,message="Backup restaurado com sucesso",safetyBackup=Path.GetFileName(safety)});
    }
    catch(Exception ex){return Results.Problem("Falha ao restaurar backup: "+ex.Message);}
});
app.MapDelete("/api/v1/admin/backups/{name}", (string name, HttpRequest req, HttpContext ctx) =>
{
    if(!HasPanelPermission(req,"backups")) return Results.StatusCode(403);
    name=SafeBackupName(name); var path=Path.Combine(backupsDir,name); if(!File.Exists(path)) return Results.NotFound(new {message="Backup não encontrado."});
    try{File.Delete(path);AddLog(ctx,"Backup Deleted",name,"Backup excluído");return Results.Ok(new {success=true,message="Backup excluído"});}catch(Exception ex){return Results.Problem("Falha ao excluir backup: "+ex.Message);}
});

app.MapGet("/api/v1/admin/monitoring", (HttpRequest req) => {
    if(!HasPanelPermission(req,"monitoring")) return Results.StatusCode(403);
    process.Refresh();
    var now=DateTimeOffset.UtcNow;
    var uptime=now-serverStartedAt;
    var cpuSeconds=process.TotalProcessorTime.TotalSeconds;
    var elapsed=Math.Max(0.001, uptime.TotalSeconds);
    var cpuPercent=Math.Clamp(cpuSeconds/elapsed/Environment.ProcessorCount*100.0,0,100);
    var working=process.WorkingSet64;
    var gc=GC.GetGCMemoryInfo();
    var dataFiles=Directory.Exists(dataDir)?Directory.GetFiles(dataDir,"*.json",SearchOption.TopDirectoryOnly).ToList():new List<string>();
    var dataBytes=dataFiles.Sum(f=>new FileInfo(f).Length);
    var storageBytes=Directory.Exists(filesDir)?Directory.GetFiles(filesDir,"*",SearchOption.AllDirectories).Sum(f=>new FileInfo(f).Length):0L;
    var backupBytes=Directory.Exists(backupsDir)?Directory.GetFiles(backupsDir,"*.zip",SearchOption.TopDirectoryOnly).Sum(f=>new FileInfo(f).Length):0L;
    var nowUtc=DateTimeOffset.UtcNow;
    int activeSessions;
    lock(sessionLock) { activeSessions=sessions.Values.Count(x=>x.ExpiresAt>nowUtc); }
    var logs=LoadLogs();
    var recentErrors=logs.Count(x=>x.CreatedAt>DateTimeOffset.UtcNow.AddHours(-24) && (x.Action.Contains("error",StringComparison.OrdinalIgnoreCase)||x.Action.Contains("fail",StringComparison.OrdinalIgnoreCase)));
    return Results.Ok(new {success=true,status="online",serverTime=now,startedAt=serverStartedAt,uptimeSeconds=(long)uptime.TotalSeconds,uptime=uptime.ToString(@"dd\:hh\:mm\:ss"),processId=process.Id,cpuPercent=Math.Round(cpuPercent,2),processorCount=Environment.ProcessorCount,workingSetBytes=working,gcHeapBytes=gc.HeapSizeBytes,dataBytes,storageBytes,backupBytes,dataFiles=dataFiles.Count,activeSessions,recentErrors,os=Environment.OSVersion.ToString(),framework=Environment.Version.ToString(),machine=Environment.MachineName});
});


// Premium 29.0 — Alertas integrados à central de notificações.
void CreateSystemNotification(string title, string message, string priority)
{
    var items=LoadNotifications();
    var exists=items.Any(x=>x.Title.Equals(title,StringComparison.OrdinalIgnoreCase)&&x.Message.Equals(message,StringComparison.Ordinal)&&x.CreatedAt>DateTimeOffset.UtcNow.AddMinutes(-10));
    if(exists)return;
    items.Add(new NotificationRecord{Id=Guid.NewGuid().ToString("N"),Title=title,Message=message,Priority=priority,CreatedBy="system",CreatedAt=DateTimeOffset.UtcNow,Read=false});
    if(items.Count>2000)items=items.Skip(items.Count-2000).ToList();
    SaveNotifications(items);
}

// Premium 28.0 — Alertas inteligentes do servidor.
void SaveAlert(string key, string severity, string title, string message, double value = 0, double threshold = 0)
{
    var items=LoadAlerts();
    var active=items.FirstOrDefault(x=>x.Key.Equals(key,StringComparison.OrdinalIgnoreCase)&&x.Status=="active");
    if(active is not null){ active.LastSeenAt=DateTimeOffset.UtcNow; active.Value=value; active.Threshold=threshold; SaveAlerts(items); return; }
    items.Add(new AlertRecord{Id=Guid.NewGuid().ToString("N"),Key=key,Severity=severity,Title=title,Message=message,Value=value,Threshold=threshold,Status="active",CreatedAt=DateTimeOffset.UtcNow,LastSeenAt=DateTimeOffset.UtcNow});
    if(items.Count>2000) items=items.Skip(items.Count-2000).ToList();
    SaveAlerts(items);
    CreateSystemNotification(title,message,severity.Equals("critical",StringComparison.OrdinalIgnoreCase)?"critical":"high");
    AddSystemLog("Alert Created",key,message);
}
void ResolveAlert(string key, string reason)
{
    var items=LoadAlerts(); var active=items.FirstOrDefault(x=>x.Key.Equals(key,StringComparison.OrdinalIgnoreCase)&&x.Status=="active");
    if(active is null) return;
    active.Status="resolved"; active.ResolvedAt=DateTimeOffset.UtcNow; active.Message=reason; SaveAlerts(items); CreateSystemNotification("Alerta resolvido",active.Title+" — "+reason,"normal"); AddSystemLog("Alert Resolved",key,reason);
}
void ScanAlerts()
{
    var st=LoadSettings(); process.Refresh();
    var now=DateTimeOffset.UtcNow; var uptime=Math.Max(1,(now-serverStartedAt).TotalSeconds);
    var cpu=Math.Clamp(process.TotalProcessorTime.TotalSeconds/uptime/Environment.ProcessorCount*100.0,0,100);
    var mem=process.WorkingSet64/1048576d;
    var errors=LoadLogs().Count(x=>x.CreatedAt>now.AddHours(-24)&&(x.Action.Contains("error",StringComparison.OrdinalIgnoreCase)||x.Action.Contains("fail",StringComparison.OrdinalIgnoreCase)));
    var backups=Directory.Exists(backupsDir)?Directory.GetFiles(backupsDir,"*.zip").OrderByDescending(File.GetCreationTimeUtc).ToList():new List<string>();
    var latest=backups.Count>0?new DateTimeOffset(File.GetCreationTimeUtc(backups[0]),TimeSpan.Zero):DateTimeOffset.MinValue;
    if(cpu>=st.CpuAlertPercent) SaveAlert("cpu_high","high","CPU elevada",$"CPU do processo em {cpu:0.0}% (limite {st.CpuAlertPercent}%).",cpu,st.CpuAlertPercent); else ResolveAlert("cpu_high","CPU voltou ao nível normal.");
    if(mem>=st.MemoryAlertMb) SaveAlert("memory_high","high","Memória elevada",$"Memória do processo em {mem:0} MB (limite {st.MemoryAlertMb} MB).",mem,st.MemoryAlertMb); else ResolveAlert("memory_high","Memória voltou ao nível normal.");
    if(errors>=st.ErrorAlertCount) SaveAlert("errors_high","critical","Muitos erros",$"Foram detectados {errors} erros nas últimas 24h (limite {st.ErrorAlertCount}).",errors,st.ErrorAlertCount); else ResolveAlert("errors_high","Quantidade de erros voltou ao nível normal.");
    var stale=latest==DateTimeOffset.MinValue || (now-latest).TotalHours>=st.BackupStaleHours;
    if(st.BackupEnabled && stale) SaveAlert("backup_stale","critical","Backup atrasado",latest==DateTimeOffset.MinValue?"Nenhum backup encontrado.":$"O backup mais recente tem {(now-latest).TotalHours:0.0}h.",(now-latest).TotalHours,st.BackupStaleHours); else ResolveAlert("backup_stale","Backups estão dentro do intervalo esperado.");
}
app.MapGet("/api/v1/admin/alerts", (HttpRequest req) =>
{
    if(!HasPanelPermission(req,"alerts")) return Results.StatusCode(403);
    ScanAlerts(); var items=LoadAlerts().OrderByDescending(x=>x.CreatedAt).Take(200).ToList();
    return Results.Ok(new {success=true,alerts=items,active=items.Count(x=>x.Status=="active"),critical=items.Count(x=>x.Status=="active"&&x.Severity=="critical"),high=items.Count(x=>x.Status=="active"&&x.Severity=="high")});
});
app.MapPost("/api/v1/admin/alerts/scan", (HttpRequest req) =>
{
    if(!HasPanelPermission(req,"alerts")) return Results.StatusCode(403); ScanAlerts(); return Results.Ok(new {success=true,message="Alertas atualizados"});
});
app.MapPost("/api/v1/admin/alerts/{id}/resolve", (string id, HttpRequest req, HttpContext ctx) =>
{
    if(!HasPanelPermission(req,"alerts")) return Results.StatusCode(403); var items=LoadAlerts(); var a=items.FirstOrDefault(x=>x.Id==id); if(a is null)return Results.NotFound(); a.Status="resolved";a.ResolvedAt=DateTimeOffset.UtcNow;SaveAlerts(items);AddLog(ctx,"Alert Resolved",a.Key,"Alerta resolvido manualmente");return Results.Ok(new {success=true});
});

app.MapGet("/api/v1/admin/dashboard", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"dashboard")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var licenses=LoadLicenses(); var apps=LoadApps(); var orders=LoadOrders(); var logs=LoadLogs();
    var days=Math.Clamp(int.TryParse(req.Query["days"],out var d)?d:7,7,30);
    var now=DateTimeOffset.UtcNow; var start=now.Date.AddDays(-(days-1));
    var series=Enumerable.Range(0,days).Select(i=>start.AddDays(i)).ToList();
    var daily=series.Select(day=>{
        var next=day.AddDays(1);
        var createdKeys=licenses.Count(x=>x.ExpiresAt.HasValue && x.ExpiresAt.Value >= day && x.ExpiresAt.Value < next); // fallback proxy for legacy data
        var logins=logs.Count(x=>x.CreatedAt>=day&&x.CreatedAt<next&&x.Action.Contains("login",StringComparison.OrdinalIgnoreCase));
        var events=logs.Count(x=>x.CreatedAt>=day&&x.CreatedAt<next);
        var revenue=orders.Where(x=>x.Status.Equals("paid",StringComparison.OrdinalIgnoreCase)&&x.CreatedAt>=day&&x.CreatedAt<next).Sum(x=>x.Amount);
        return new { date=day.ToString("yyyy-MM-dd"), keys=createdKeys, logins, events, revenue };
    }).ToList();
    var topApps=apps.Select(a=>new { id=a.Id,name=a.Name,active=a.Active,keys=licenses.Count(x=>!string.IsNullOrWhiteSpace(x.Username)&&x.Username.StartsWith(a.Name,StringComparison.OrdinalIgnoreCase)) })
        .OrderByDescending(x=>x.keys).ThenBy(x=>x.name).Take(8).ToList();
    var recent=logs.OrderByDescending(x=>x.CreatedAt).Take(8).Select(x=>new {x.Id,x.Action,x.Target,x.Actor,x.CreatedAt,x.Ip}).ToList();
    return Results.Ok(new {success=true,days,daily,topApps,recent});
});

app.MapGet("/api/v1/admin/stats", (HttpRequest req) => { if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"dashboard")) return Results.StatusCode(403); } else if(!Authorized(req))return Results.Unauthorized(); var l=LoadLicenses(); var a=LoadApps(); var o=LoadOrders(); var paid=o.Where(x=>x.Status.Equals("paid",StringComparison.OrdinalIgnoreCase)); return Results.Ok(new {success=true,licenses=l.Count,activeLicenses=l.Count(x=>x.Used&&!x.Banned&&(!x.ExpiresAt.HasValue||x.ExpiresAt>DateTimeOffset.UtcNow)),unused=l.Count(x=>!x.Used),banned=l.Count(x=>x.Banned),applications=a.Count,activeApplications=a.Count(x=>x.Active),sales=o.Count,revenue=paid.Sum(x=>x.Amount),paidSales=paid.Count(),pendingSales=o.Count(x=>x.Status.Equals("pending",StringComparison.OrdinalIgnoreCase))}); });


// Premium 15.0 — Clientes (visão consolidada a partir de licenças, assinaturas e vendas)
app.MapGet("/api/v1/admin/customers", (HttpRequest req) => {
    if(PanelAuthorized(req)) { if(!HasPanelPermission(req,"licenses")) return Results.StatusCode(403); }
    else if(!Authorized(req)) return Results.Unauthorized();
    var licenses=LoadLicenses();
    var subs=LoadSubscriptions();
    var orders=LoadOrders();
    var names=licenses.Where(x=>!string.IsNullOrWhiteSpace(x.Username)).Select(x=>x.Username!.Trim())
        .Concat(subs.Where(x=>!string.IsNullOrWhiteSpace(x.Customer)).Select(x=>x.Customer.Trim()))
        .Concat(orders.Where(x=>!string.IsNullOrWhiteSpace(x.Customer)).Select(x=>x.Customer.Trim()))
        .Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var now=DateTimeOffset.UtcNow;
    var customers=names.Select(name => {
        var ls=licenses.Where(x=>string.Equals(x.Username?.Trim(),name,StringComparison.OrdinalIgnoreCase)).ToList();
        var ss=subs.Where(x=>string.Equals(x.Customer?.Trim(),name,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.CreatedAt).ToList();
        var os=orders.Where(x=>string.Equals(x.Customer?.Trim(),name,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.CreatedAt).ToList();
        var active=ss.Any(x=>x.Status.Equals("active",StringComparison.OrdinalIgnoreCase) && (!x.ExpiresAt.HasValue || x.ExpiresAt.Value>now));
        var lastLogin=ls.Where(x=>x.LastSeenAt.HasValue).Select(x=>x.LastSeenAt).OrderByDescending(x=>x).FirstOrDefault();
        var spent=os.Where(x=>x.Status.Equals("paid",StringComparison.OrdinalIgnoreCase)).Sum(x=>x.Amount);
        return new { username=name, licenses=ls.Count, activeLicenses=ls.Count(x=>x.Used && !x.Banned && (!x.ExpiresAt.HasValue || x.ExpiresAt.Value>now)), subscriptions=ss.Count, activeSubscription=active, plan=ss.FirstOrDefault()?.Plan??"—", spent, lastLogin, banned=ls.Any(x=>x.Banned) };
    }).OrderByDescending(x=>x.lastLogin).ThenBy(x=>x.username).ToList();
    return Results.Ok(new {success=true,total=customers.Count,customers});
});


var backupLock = new object();
void AddSystemLog(string action, string target = "", string details = "")
{
    var logs = LoadLogs();
    logs.Add(new AuditLog { Id=Guid.NewGuid().ToString("N"), Action=action, Target=target, Details=details, Actor="system", Ip=null, CreatedAt=DateTimeOffset.UtcNow });
    var retention = Math.Clamp(LoadSettings().AuditRetention, 100, 20000);
    if (logs.Count > retention) logs = logs.Skip(logs.Count-retention).ToList();
    SaveLogs(logs);
    var evt = new EventRecord { Id=Guid.NewGuid().ToString("N"), Type=action, Target=target, Details=details, Actor="system", Ip=null, CreatedAt=DateTimeOffset.UtcNow };
    var events = LoadEvents(); events.Add(evt); if(events.Count>5000) events=events.Skip(events.Count-5000).ToList(); SaveEvents(events);
    _ = DispatchWebhookAsync(evt);
}

// Premium 26.0 — Backup automático agendado.
var backupSchedulerCts = new CancellationTokenSource();
var backupSchedulerTask = Task.Run(async () =>
{
    while (!backupSchedulerCts.IsCancellationRequested)
    {
        try
        {
            var st = LoadSettings();
            var interval = TimeSpan.FromHours(Math.Clamp(st.BackupIntervalHours, 1, 168));
            await Task.Delay(interval, backupSchedulerCts.Token);
            st = LoadSettings();
            if (!st.BackupEnabled) continue;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var name = $"northauth_auto_{stamp}.zip";
            var path = Path.Combine(backupsDir, name);
            lock (backupLock) CreateDataBackup(path);
            st.LastAutomaticBackupAt = DateTimeOffset.UtcNow;
            SaveSettings(st);
            CleanupBackups(st.BackupRetentionCount);
            AddSystemLog("Backup Automatico", name, $"Backup automático criado; intervalo de {st.BackupIntervalHours}h");
        }
        catch (OperationCanceledException) { break; }
        catch { /* o próximo ciclo tenta novamente */ }
    }
});


var alertSchedulerTask = Task.Run(async () =>
{
    while (!backupSchedulerCts.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), backupSchedulerCts.Token); ScanAlerts(); }
        catch (OperationCanceledException) { break; } catch { }
    }
});


// Estado de música compartilhado entre os clientes conectados.
SharedMusic? sharedMusic = null;
var musicClients = new System.Collections.Concurrent.ConcurrentDictionary<Guid, HttpResponse>();

app.MapGet("/api/v1/music/shared", () =>
{
    return Results.Ok(new { success = true, music = sharedMusic });
});

app.MapPost("/api/v1/music/shared", async (HttpContext ctx) =>
{
    try
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<SharedMusicRequest>(ctx.Request.Body);
        if (body is null || string.IsNullOrWhiteSpace(body.VideoId))
            return Results.BadRequest(new { success = false, message = "Vídeo inválido." });

        sharedMusic = new SharedMusic(body.VideoId, body.Title ?? "YouTube", body.Channel ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var payload = System.Text.Json.JsonSerializer.Serialize(new { type = "music", music = sharedMusic });
        var bytes = System.Text.Encoding.UTF8.GetBytes($"data: {payload}\n\n");

        foreach (var item in musicClients.ToArray())
        {
            try
            {
                await item.Value.Body.WriteAsync(bytes);
                await item.Value.Body.FlushAsync();
            }
            catch { musicClients.TryRemove(item.Key, out _); }
        }

        return Results.Ok(new { success = true, music = sharedMusic });
    }
    catch
    {
        return Results.BadRequest(new { success = false, message = "Não foi possível atualizar a música." });
    }
});

app.MapPost("/api/v1/music/shared/stop", async () =>
{
    sharedMusic = null;
    var payload = System.Text.Json.JsonSerializer.Serialize(new { type = "music_stop" });
    var bytes = System.Text.Encoding.UTF8.GetBytes($"data: {payload}\n\n");
    foreach (var item in musicClients.ToArray())
    {
        try { await item.Value.Body.WriteAsync(bytes); await item.Value.Body.FlushAsync(); }
        catch { musicClients.TryRemove(item.Key, out _); }
    }
    return Results.Ok(new { success = true });
});

app.MapGet("/api/v1/music/shared/events", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    var id = Guid.NewGuid();
    musicClients[id] = ctx.Response;
    try
    {
        if (sharedMusic is not null)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { type = "music", music = sharedMusic });
            await ctx.Response.WriteAsync($"data: {payload}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
        await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { }
    finally { musicClients.TryRemove(id, out _); }
});


// Fallback de pesquisa pública do YouTube.
// Não depende de API key e é usado somente quando a YouTube Data API v3
// estiver sem chave, bloqueada ou sem resultados.
static async Task<List<object>> SearchYouTubeWebAsync(string query)
{
    using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140 Safari/537.36");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");

    var urls = new[]
    {
        "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&hl=pt-BR",
        "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&hl=en",
        "https://m.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&hl=en"
    };

    var results = new List<object>();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var url in urls)
    {
        if (results.Count >= 10) break;

        string html;
        try
        {
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) continue;
            html = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            continue;
        }

        // Formato clássico: videoRenderer dentro do ytInitialData.
        const string marker = "\"videoRenderer\":";
        int pos = 0;
        while (results.Count < 10)
        {
            var markerPos = html.IndexOf(marker, pos, StringComparison.Ordinal);
            if (markerPos < 0) break;
            var startObj = markerPos + marker.Length;
            while (startObj < html.Length && char.IsWhiteSpace(html[startObj])) startObj++;
            if (startObj >= html.Length || html[startObj] != '{') { pos = startObj; continue; }

            int depth = 0, endObj = -1;
            bool inString = false, escaped = false;
            for (int i = startObj; i < html.Length; i++)
            {
                char c = html[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { endObj = i + 1; break; }
                }
            }
            if (endObj < 0) break;
            var json = html.Substring(startObj, endObj - startObj);
            pos = endObj;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("videoId", out var idEl)) continue;
                var videoId = idEl.GetString();
                if (string.IsNullOrWhiteSpace(videoId) || !seen.Add(videoId)) continue;

                string title = query;
                if (root.TryGetProperty("title", out var titleEl))
                {
                    if (titleEl.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0 && runs[0].TryGetProperty("text", out var textEl))
                        title = textEl.GetString() ?? query;
                    else if (titleEl.TryGetProperty("simpleText", out var simpleEl))
                        title = simpleEl.GetString() ?? query;
                }
                string channel = "YouTube";
                if (root.TryGetProperty("ownerText", out var ownerEl) && ownerEl.TryGetProperty("runs", out var ownerRuns) && ownerRuns.ValueKind == JsonValueKind.Array && ownerRuns.GetArrayLength() > 0 && ownerRuns[0].TryGetProperty("text", out var ownerText))
                    channel = ownerText.GetString() ?? "YouTube";

                results.Add(new { videoId, title, channel, thumbnail = $"https://i.ytimg.com/vi/{Uri.EscapeDataString(videoId)}/hqdefault.jpg" });
            }
            catch { }
        }

        // Formato alternativo: algumas respostas do YouTube não expõem videoRenderer,
        // mas ainda contêm os IDs dentro do JSON inicial da página.
        if (results.Count == 0)
        {
            var idMatches = System.Text.RegularExpressions.Regex.Matches(
                html, @"videoId""\s*:\s*""([A-Za-z0-9_-]{11})""");

            foreach (System.Text.RegularExpressions.Match match in idMatches)
            {
                if (results.Count >= 10) break;
                var videoId = match.Groups[1].Success ? match.Groups[1].Value : "";
                if (!seen.Add(videoId)) continue;

                var aroundStart = Math.Max(0, match.Index - 300);
                var aroundLength = Math.Min(html.Length - aroundStart, 3500);
                var around = html.Substring(aroundStart, aroundLength);
                var titleMatch = System.Text.RegularExpressions.Regex.Match(
                    around, @"title""\s*:\s*\{\s*""runs""\s*:\s*\[\s*\{\s*""text""\s*:\s*""((?:\\.|[^""\\])*)""");
                var channelMatch = System.Text.RegularExpressions.Regex.Match(
                    around, @"ownerText""\s*:\s*\{.*?""text""\s*:\s*""((?:\\.|[^""\\])*)""", System.Text.RegularExpressions.RegexOptions.Singleline);

                string title = query;
                string channel = "YouTube";
                if (titleMatch.Success)
                {
                    try { title = JsonSerializer.Deserialize<string>("\"" + titleMatch.Groups[1].Value + "\"") ?? query; } catch { }
                }
                if (channelMatch.Success)
                {
                    try { channel = JsonSerializer.Deserialize<string>("\"" + channelMatch.Groups[1].Value + "\"") ?? "YouTube"; } catch { }
                }
                results.Add(new { videoId, title, channel, thumbnail = $"https://i.ytimg.com/vi/{Uri.EscapeDataString(videoId)}/hqdefault.jpg" });
            }
        }
    }

    return results;
}

// Persistence diagnostics: never exposes secrets or database credentials.
app.MapGet("/api/health/storage", () => Results.Ok(new {
    success = true,
    provider = store.ProviderName,
    persistent = store.IsPersistent,
    message = store.IsPersistent ? "PostgreSQL persistente ativo." : "Armazenamento local ativo apenas para desenvolvimento. Configure DATABASE_URL no Render."
}));

app.Run();

// Declarações de tipos devem ficar fora das instruções de nível superior.

static DateTimeOffset? CalculateExpiry(string subscription, int? duration=null, string? expiryUnit=null) {
    if(duration.HasValue && duration.Value>0 && !string.IsNullOrWhiteSpace(expiryUnit)) {
        var now=DateTimeOffset.UtcNow; var u=expiryUnit.Trim().ToLowerInvariant();
        if(u.StartsWith("min"))return now.AddMinutes(duration.Value); if(u.StartsWith("hour"))return now.AddHours(duration.Value); if(u.StartsWith("week"))return now.AddDays(duration.Value*7); if(u.StartsWith("month"))return now.AddMonths(duration.Value); if(u.StartsWith("year"))return now.AddYears(duration.Value); return now.AddDays(duration.Value);
    }
    if(string.IsNullOrWhiteSpace(subscription) || subscription.Contains("lifetime",StringComparison.OrdinalIgnoreCase))return null;
    var n=new string(subscription.Where(char.IsDigit).ToArray()); if(!int.TryParse(n,out var v)||v<=0)v=3;
    if(subscription.Contains("minute",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddMinutes(v); if(subscription.Contains("hour",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddHours(v); if(subscription.Contains("week",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddDays(v*7); if(subscription.Contains("month",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddDays(v*30); if(subscription.Contains("year",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddDays(v*365); return DateTimeOffset.UtcNow.AddDays(v);
}
static string NormalizeVariableType(string? type) { var t=(type??"string").Trim().ToLowerInvariant(); return t is "string" or "number" or "boolean" or "json" or "secret" ? t : "string"; }
static string NormalizeRuleType(string? type) { var t=(type??"feature").Trim().ToLowerInvariant(); return t is "feature" or "limit" or "requirement" or "setting" ? t : "feature"; }
static string NormalizeNotificationPriority(string? p) { var t=(p??"normal").Trim().ToLowerInvariant(); return t is "low" or "normal" or "high" or "critical" ? t : "normal"; }
static string NormalizeRuleOperator(string? op) { var t=(op??"equals").Trim().ToLowerInvariant(); return t is "equals" or "not_equals" or "contains" or "gte" or "lte" ? t : "equals"; }
static string GenerateKey(string mask, bool uppercase, bool lowercase) {
    var chars=uppercase&&!lowercase?"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789":lowercase&&!uppercase?"abcdefghijklmnopqrstuvwxyz0123456789":"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    return System.Text.RegularExpressions.Regex.Replace(mask, "X+", m => { var a=new char[m.Length]; for(int i=0;i<a.Length;i++) a[i]=chars[Random.Shared.Next(chars.Length)]; return new string(a); });
}

public sealed class LicenseRequest { public string? ApiKey{get;set;} public string? License{get;set;} public string? Username{get;set;} public string? Hwid{get;set;} }
public sealed class PanelUser { public string Username{get;set;}=""; public string DisplayName{get;set;}=""; public string PasswordHash{get;set;}=""; public string Salt{get;set;}=""; public string Role{get;set;}="Staff"; public List<string>? Permissions{get;set;} public bool Active{get;set;}=true; public bool TwoFactorEnabled{get;set;}=false; public string? TwoFactorSecret{get;set;} public DateTimeOffset CreatedAt{get;set;} }
public sealed class AuditLog { public string Id{get;set;}=""; public string Action{get;set;}=""; public string Target{get;set;}=""; public string Details{get;set;}=""; public string Actor{get;set;}=""; public string? Ip{get;set;} public DateTimeOffset CreatedAt{get;set;} }
public sealed class ChangePanelRoleRequest { public string? Role{get;set;} }
public sealed class PermissionUpdateRequest { public List<string>? Permissions{get;set;} }
public sealed class AlertRecord { public string Id{get;set;}=""; public string Key{get;set;}=""; public string Severity{get;set;}="high"; public string Title{get;set;}=""; public string Message{get;set;}=""; public double Value{get;set;} public double Threshold{get;set;} public string Status{get;set;}="active"; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; public DateTimeOffset LastSeenAt{get;set;}=DateTimeOffset.UtcNow; public DateTimeOffset? ResolvedAt{get;set;} }
public sealed class ServerSettings { public string ServerName {get;set;}="North Auth"; public int SessionHours {get;set;}=12; public bool Maintenance {get;set;}=false; public bool RegistrationEnabled {get;set;}=true; public int DefaultLicenseDays {get;set;}=30; public int AuditRetention {get;set;}=2000; public bool BackupEnabled {get;set;}=true; public int BackupRetentionCount {get;set;}=10; public int BackupIntervalHours {get;set;}=24; public int CpuAlertPercent {get;set;}=85; public int MemoryAlertMb {get;set;}=1024; public int ErrorAlertCount {get;set;}=10; public int BackupStaleHours {get;set;}=48; public DateTimeOffset? LastAutomaticBackupAt {get;set;} public string YouTubeApiKey {get;set;}=""; }
public sealed class YouTubeKeyRequest { public string? ApiKey {get;set;} }
public sealed class ServerSettingsRequest { public string? ServerName {get;set;} public int SessionHours {get;set;}=12; public bool Maintenance {get;set;}=false; public bool RegistrationEnabled {get;set;}=true; public int DefaultLicenseDays {get;set;}=30; public int AuditRetention {get;set;}=2000; public bool BackupEnabled {get;set;}=true; public int BackupRetentionCount {get;set;}=10; public int BackupIntervalHours {get;set;}=24; public int CpuAlertPercent {get;set;}=85; public int MemoryAlertMb {get;set;}=1024; public int ErrorAlertCount {get;set;}=10; public int BackupStaleHours {get;set;}=48; public DateTimeOffset? LastAutomaticBackupAt {get;set;} public string? YouTubeApiKey {get;set;} }

public static class PanelPermissions { public static readonly string[] All={"dashboard","applications","licenses","users","logs","settings","sales","webhooks","events","subscriptions","files","variables","rules","tokens","chat","notifications","backups","monitoring","alerts"}; public static readonly string[] StaffDefault={"dashboard","applications","licenses","logs","settings","sales","webhooks","events","subscriptions","files","variables","rules","tokens","chat","notifications","backups","monitoring","alerts"}; }
public sealed class ChangePasswordRequest { public string? CurrentPassword{get;set;} public string? NewPassword{get;set;} }
public sealed class PanelSession { public string Username{get;set;}=""; public DateTimeOffset CreatedAt{get;set;} public DateTimeOffset LastSeenAt{get;set;} public DateTimeOffset ExpiresAt{get;set;} public string? Ip{get;set;} public string UserAgent{get;set;}=""; }
public sealed class PanelLoginRequest { public string? Username{get;set;} public string? Password{get;set;} }
public sealed class TwoFactorLoginRequest { public string? Code{get;set;} }
public sealed class TwoFactorCodeRequest { public string? Code{get;set;} }
public sealed class LoginThrottle { public int Failures{get;set;} public DateTimeOffset WindowStart{get;set;} public DateTimeOffset BlockedUntil{get;set;} }
public sealed class CreatePanelUserRequest { public string? Username{get;set;} public string? DisplayName{get;set;} public string? Password{get;set;} public string? Role{get;set;} }
public sealed class ApiKeyRequest { public string? ApiKey{get;set;} }
public sealed class GenerateLicensesRequest { public int Count{get;set;}=1; public string Subscription{get;set;}="3 Days"; public string? Note{get;set;} public string? ApiKey{get;set;} public string? Mask{get;set;} public bool Uppercase{get;set;}=true; public bool Lowercase{get;set;} public int? Duration{get;set;} public string? ExpiryUnit{get;set;} }
public sealed class AdminLicenseRequest { public string? ApiKey{get;set;} public string? License{get;set;} public string? Subscription{get;set;} public string? Note{get;set;} }
public sealed class LicenseUpdateRequest { public string? ApiKey{get;set;} public string? Subscription{get;set;} public string? Note{get;set;} public int? Duration{get;set;} public string? ExpiryUnit{get;set;} }
public sealed class PlanRequest { public string? ApiKey{get;set;} public string? Name{get;set;} public int Duration{get;set;}=3; public string? Unit{get;set;}="days"; public decimal Price{get;set;}=0; public string? Description{get;set;} public bool Active{get;set;}=true; }
public sealed class PlanRecord { public string Id{get;set;}=""; public string Name{get;set;}=""; public int Duration{get;set;}=3; public string Unit{get;set;}="days"; public decimal Price{get;set;}=0; public string Description{get;set;}=""; public bool Active{get;set;}=true; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class VariableRequest { public string? ApiKey{get;set;} public string? AppId{get;set;} public string? Name{get;set;} public string? Value{get;set;} public string? Type{get;set;}="string"; public string? Description{get;set;} public bool Active{get;set;}=true; }
public sealed class RuleRequest { public string? ApiKey{get;set;} public string? AppId{get;set;} public string? Name{get;set;} public string? Type{get;set;}="feature"; public string? Operator{get;set;}="equals"; public string? Value{get;set;} public string? Description{get;set;} public bool Active{get;set;}=true; public int Priority{get;set;}=100; }
public sealed class TokenRequest { public string? Name{get;set;} public string? Scope{get;set;}="admin"; public int ExpiresInDays{get;set;}=30; }
public sealed class TokenRecord { public string Id{get;set;}=""; public string Name{get;set;}=""; public string Prefix{get;set;}=""; public string Hash{get;set;}=""; public string Scope{get;set;}="admin"; public string CreatedBy{get;set;}=""; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; public DateTimeOffset? ExpiresAt{get;set;} public bool Revoked{get;set;} public DateTimeOffset? LastUsedAt{get;set;} }
public sealed class RuleRecord { public string Id{get;set;}=""; public string AppId{get;set;}=""; public string Name{get;set;}=""; public string Type{get;set;}="feature"; public string Operator{get;set;}="equals"; public string Value{get;set;}=""; public string Description{get;set;}=""; public bool Active{get;set;}=true; public int Priority{get;set;}=100; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class VariableRecord { public string Id{get;set;}=""; public string AppId{get;set;}=""; public string Name{get;set;}=""; public string Value{get;set;}=""; public string Type{get;set;}="string"; public string Description{get;set;}=""; public bool Active{get;set;}=true; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class OrderRequest { public string? ApiKey{get;set;} public string? Plan{get;set;} public string? Customer{get;set;} public decimal Amount{get;set;} public string? Status{get;set;}="paid"; public string? Note{get;set;} }
public sealed class OrderRecord { public string Id{get;set;}=""; public string Plan{get;set;}=""; public string Customer{get;set;}=""; public decimal Amount{get;set;} public string Status{get;set;}="paid"; public string Note{get;set;}=""; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; }

public sealed class WebhookRequest { public string? ApiKey{get;set;} public string? Name{get;set;} public string? Url{get;set;} public List<string>? Events{get;set;} public bool Active{get;set;}=true; }
public sealed class WebhookRecord { public string Id{get;set;}=""; public string Name{get;set;}="Webhook"; public string Url{get;set;}=""; public List<string> Events{get;set;}=new(); public bool Active{get;set;}=true; public int LastStatus{get;set;} public DateTimeOffset? LastDeliveryAt{get;set;} public string LastError{get;set;}=""; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class SubscriptionRequest { public string? ApiKey{get;set;} public string? License{get;set;} public string? Plan{get;set;} public string? Customer{get;set;} public string? Status{get;set;}="active"; public bool AutoRenew{get;set;} public string? Note{get;set;} }
public sealed class SubscriptionRecord { public string Id{get;set;}=""; public string License{get;set;}=""; public string Customer{get;set;}=""; public string Plan{get;set;}=""; public DateTimeOffset StartedAt{get;set;} public DateTimeOffset? ExpiresAt{get;set;} public string Status{get;set;}="active"; public bool AutoRenew{get;set;} public string Note{get;set;}=""; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class ChatMessageRequest { public string? Channel{get;set;} public string? Text{get;set;} }
public sealed class NotificationRequest { public string? Title{get;set;} public string? Message{get;set;} public string? Priority{get;set;}="normal"; }
public sealed class NotificationRecord { public string Id{get;set;}=""; public string Title{get;set;}=""; public string Message{get;set;}=""; public string Priority{get;set;}="normal"; public string CreatedBy{get;set;}=""; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; public DateTimeOffset? UpdatedAt{get;set;} public bool Read{get;set;} public DateTimeOffset? ReadAt{get;set;} public string? ReadBy{get;set;} }
public sealed class ChatMessageRecord { public string Id{get;set;}=""; public string Channel{get;set;}="geral"; public string Sender{get;set;}=""; public string DisplayName{get;set;}=""; public string Text{get;set;}=""; public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class EventRecord { public string Id{get;set;}=""; public string Type{get;set;}=""; public string Target{get;set;}=""; public string Details{get;set;}=""; public string Actor{get;set;}="system"; public string? Ip{get;set;} public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow; }
public sealed class BulkLicenseRequest { public string? ApiKey{get;set;} public List<string>? Licenses{get;set;} public string? Action{get;set;} }
public sealed class LicenseRecord { public string License{get;set;}=""; public string Subscription{get;set;}="3 Days"; public DateTimeOffset? ExpiresAt{get;set;} public bool Used{get;set;} public bool Banned{get;set;} public string? Username{get;set;} public string? Hwid{get;set;} public string? LastIp{get;set;} public DateTimeOffset? LastSeenAt{get;set;} public int LoginCount{get;set;} public string? Note{get;set;} }
public sealed class AppRecord { public string Id{get;set;}=""; public string Name{get;set;}=""; public string Version{get;set;}="1.0"; public string Description{get;set;}=""; public bool Active{get;set;}=true; public DateTimeOffset CreatedAt{get;set;} public string OwnerId{get;set;}=""; public string Secret{get;set;}=""; }
public sealed class AppCreateRequest { public string? ApiKey{get;set;} public string? Name{get;set;} public string? Version{get;set;} public string? Description{get;set;} }
public sealed class AppUpdateRequest { public string? ApiKey{get;set;} public string? Name{get;set;} public string? Version{get;set;} public string? Description{get;set;} public bool? Active{get;set;} }


// Modelos da música compartilhada — devem ficar no fim do arquivo porque Program.cs usa top-level statements.
public sealed record SharedMusic(string VideoId, string Title, string Channel, long ChangedAt);
public sealed record SharedMusicRequest(string VideoId, string? Title, string? Channel);

// Armazenamento persistente do NorthAuth.
// Em produção (Render), DATABASE_URL aponta para PostgreSQL. O banco é a fonte de verdade;
// os arquivos JSON locais servem como espelho para backups e desenvolvimento.
public sealed class PersistentJsonStore
{
    private readonly string? _connectionString;
    private readonly string _dataDir;
    private readonly JsonSerializerOptions _options;
    private readonly object _sync = new();
    private bool _initialized;
    private static readonly Dictionary<string,string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["licenses"]="licenses.json", ["apps"]="apps.json", ["users"]="users.json", ["logs"]="logs.json",
        ["plans"]="plans.json", ["orders"]="orders.json", ["webhooks"]="webhooks.json", ["events"]="events.json",
        ["subscriptions"]="subscriptions.json", ["variables"]="variables.json", ["rules"]="rules.json",
        ["tokens"]="tokens.json", ["chat_messages"]="chat_messages.json", ["notifications"]="notifications.json",
        ["settings"]="settings.json", ["alerts"]="alerts.json"
    };

    public bool IsPersistent => !string.IsNullOrWhiteSpace(_connectionString);
    public string ProviderName => IsPersistent ? "PostgreSQL" : "Local JSON (development only)";

    public PersistentJsonStore(string? databaseUrl, string dataDir, JsonSerializerOptions options)
    {
        _connectionString = NormalizeConnectionString(databaseUrl);
        _dataDir = dataDir;
        _options = options;
    }

    public void Initialize()
    {
        lock (_sync)
        {
            if (_initialized) return;
            if (!IsPersistent)
            {
                // Local development is supported, but Render deployments must set DATABASE_URL.
                _initialized = true;
                return;
            }

            using var cn = new NpgsqlConnection(_connectionString);
            cn.Open();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS northauth_json_store (
                        key TEXT PRIMARY KEY,
                        json JSONB NOT NULL,
                        updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // One-time import: if a dataset is absent in PostgreSQL, import the JSON shipped with the app.
            // Existing database values are NEVER overwritten by a deploy/restart.
            foreach (var pair in Keys)
            {
                using var exists = cn.CreateCommand();
                exists.CommandText = "SELECT 1 FROM northauth_json_store WHERE key=@key LIMIT 1";
                exists.Parameters.AddWithValue("key", pair.Key);
                if (exists.ExecuteScalar() is not null) continue;

                var path = Path.Combine(_dataDir, pair.Value);
                var json = File.Exists(path) ? File.ReadAllText(path) : (pair.Key == "settings" ? "{}" : "[]");
                if (string.IsNullOrWhiteSpace(json)) json = pair.Key == "settings" ? "{}" : "[]";
                using var insert = cn.CreateCommand();
                insert.CommandText = "INSERT INTO northauth_json_store(key,json,updated_at) VALUES(@key,CAST(@json AS jsonb),NOW()) ON CONFLICT(key) DO NOTHING";
                insert.Parameters.AddWithValue("key", pair.Key);
                insert.Parameters.AddWithValue("json", json);
                insert.ExecuteNonQuery();
            }

            // Refresh the local mirror from the database after every container start.
            foreach (var pair in Keys)
            {
                using var read = cn.CreateCommand();
                read.CommandText = "SELECT json::text FROM northauth_json_store WHERE key=@key";
                read.Parameters.AddWithValue("key", pair.Key);
                var json = read.ExecuteScalar()?.ToString();
                if (json is null) continue;
                File.WriteAllText(Path.Combine(_dataDir, pair.Value), json);
            }
            _initialized = true;
        }
    }

    public T Load<T>(string key, string mirrorFile, T fallback)
    {
        lock (_sync)
        {
            if (!IsPersistent)
            {
                try { return File.Exists(mirrorFile) ? JsonSerializer.Deserialize<T>(File.ReadAllText(mirrorFile), _options) ?? fallback : fallback; }
                catch { return fallback; }
            }

            using var cn = new NpgsqlConnection(_connectionString);
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT json::text FROM northauth_json_store WHERE key=@key";
            cmd.Parameters.AddWithValue("key", key);
            var json = cmd.ExecuteScalar()?.ToString();
            if (json is null) throw new InvalidOperationException($"Persistent dataset '{key}' is missing from PostgreSQL.");
            var value = JsonSerializer.Deserialize<T>(json, _options) ?? fallback;
            File.WriteAllText(mirrorFile, json);
            return value;
        }
    }

    public void Save<T>(string key, string mirrorFile, T value)
    {
        var json = JsonSerializer.Serialize(value, _options);
        lock (_sync)
        {
            if (!IsPersistent)
            {
                File.WriteAllText(mirrorFile, json);
                return;
            }
            using var cn = new NpgsqlConnection(_connectionString);
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "INSERT INTO northauth_json_store(key,json,updated_at) VALUES(@key,CAST(@json AS jsonb),NOW()) ON CONFLICT(key) DO UPDATE SET json=EXCLUDED.json, updated_at=NOW()";
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("json", json);
            cmd.ExecuteNonQuery();
            File.WriteAllText(mirrorFile, json);
        }
    }

    private static string? NormalizeConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var raw = value.Trim();
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return raw;
        var uri = new Uri(raw);
        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var colon = userInfo.IndexOf(':');
        var user = colon >= 0 ? userInfo[..colon] : userInfo;
        var password = colon >= 0 ? userInfo[(colon + 1)..] : "";
        var database = uri.AbsolutePath.TrimStart('/');
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = user,
            Password = password,
            Database = Uri.UnescapeDataString(database),
            SslMode = SslMode.Require,
            TrustServerCertificate = true,
            Timeout = 15,
            CommandTimeout = 30,
            ApplicationName = "NorthAuth"
        };
        return builder.ConnectionString;
    }
}

