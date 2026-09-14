using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
var app = builder.Build();

var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
Directory.CreateDirectory(dataDir);
var licensesFile = Path.Combine(dataDir, "licenses.json");
var appsFile = Path.Combine(dataDir, "apps.json");
var usersFile = Path.Combine(dataDir, "users.json");
var apiKey = builder.Configuration["NorthAuth:ApiKey"] ?? "CHANGE_THIS_API_KEY";
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

List<LicenseRecord> LoadLicenses() => LoadFile<List<LicenseRecord>>(licensesFile) ?? new();
List<AppRecord> LoadApps() => LoadFile<List<AppRecord>>(appsFile) ?? new();
List<PanelUser> LoadUsers() => LoadFile<List<PanelUser>>(usersFile) ?? new();
T? LoadFile<T>(string file) { try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), jsonOptions) : default; } catch { return default; } }
void SaveLicenses(List<LicenseRecord> items) => File.WriteAllText(licensesFile, JsonSerializer.Serialize(items, jsonOptions));
void SaveApps(List<AppRecord> items) => File.WriteAllText(appsFile, JsonSerializer.Serialize(items, jsonOptions));
void SaveUsers(List<PanelUser> items) => File.WriteAllText(usersFile, JsonSerializer.Serialize(items, jsonOptions));
bool Authorized(HttpRequest request, string? bodyApiKey = null) {
    var header = request.Headers["X-Api-Key"].FirstOrDefault();
    var candidate = !string.IsNullOrWhiteSpace(header) ? header : bodyApiKey;
    return string.Equals(candidate, apiKey, StringComparison.Ordinal);
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Painel: login por usuário/senha, separado da API Key usada pelo loader.
var sessions = new Dictionary<string, PanelSession>(StringComparer.Ordinal);
var sessionLock = new object();

static string HashPassword(string password, byte[] salt) {
    using var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt, 120_000, System.Security.Cryptography.HashAlgorithmName.SHA256);
    return Convert.ToBase64String(pbkdf2.GetBytes(32));
}
static bool VerifyPassword(string password, string salt64, string hash64) {
    try { var salt=Convert.FromBase64String(salt64); var a=Convert.FromBase64String(HashPassword(password,salt)); var b=Convert.FromBase64String(hash64); return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a,b); } catch { return false; }
}
string CreateSession(string username) { var token=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); lock(sessionLock) sessions[token]=new PanelSession{Username=username,ExpiresAt=DateTimeOffset.UtcNow.AddHours(12)}; return token; }
PanelUser? CurrentUser(HttpRequest req) { var token=req.Cookies["north_panel_session"]; if(string.IsNullOrWhiteSpace(token)) return null; string? username=null; lock(sessionLock) { if(!sessions.TryGetValue(token,out var ses)||ses.ExpiresAt<DateTimeOffset.UtcNow){sessions.Remove(token);return null;} username=ses.Username; } return LoadUsers().FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)&&x.Active); }
bool PanelAuthorized(HttpRequest req) => CurrentUser(req) is not null;

var startupUsers=LoadUsers();
if(startupUsers.Count==0) {
    var salt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    startupUsers.Add(new PanelUser{Username="admin",DisplayName="Administrador",PasswordHash=HashPassword("North@123",salt),Salt=Convert.ToBase64String(salt),Role="Owner",Active=true,CreatedAt=DateTimeOffset.UtcNow});
    SaveUsers(startupUsers);
}

app.MapPost("/api/panel/login", (PanelLoginRequest req, HttpContext ctx) => {
    var u=LoadUsers().FirstOrDefault(x=>x.Username.Equals(req.Username?.Trim()??"",StringComparison.OrdinalIgnoreCase)&&x.Active);
    if(u is null || !VerifyPassword(req.Password??"",u.Salt,u.PasswordHash)) return Results.Json(new {success=false,message="Usuário ou senha inválidos."},statusCode:401);
    var token=CreateSession(u.Username); ctx.Response.Cookies.Append("north_panel_session",token,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Lax,Secure=false,MaxAge=TimeSpan.FromHours(12),Path="/"});
    return Results.Ok(new {success=true,user=new {username=u.Username,displayName=u.DisplayName,role=u.Role}});
});
app.MapPost("/api/panel/logout", (HttpContext ctx) => { var t=ctx.Request.Cookies["north_panel_session"]; if(t is not null) lock(sessionLock) sessions.Remove(t); ctx.Response.Cookies.Delete("north_panel_session"); return Results.Ok(new {success=true}); });
app.MapGet("/api/panel/me", (HttpRequest req) => { var u=CurrentUser(req); return u is null ? Results.Unauthorized() : Results.Ok(new {success=true,user=new {username=u.Username,displayName=u.DisplayName,role=u.Role}}); });
app.MapGet("/api/panel/users", (HttpRequest req) => { var me=CurrentUser(req); if(me is null) return Results.Unauthorized(); if(me.Role!="Owner") return Results.StatusCode(403); return Results.Ok(new {success=true,users=LoadUsers().Select(x=>new {x.Username,x.DisplayName,x.Role,x.Active,x.CreatedAt})}); });
app.MapPost("/api/panel/users", (CreatePanelUserRequest req, HttpRequest ctx) => {
    var me=CurrentUser(ctx); if(me is null) return Results.Unauthorized(); if(me.Role!="Owner") return Results.StatusCode(403);
    var username=req.Username?.Trim()??""; var password=req.Password??""; if(username.Length<3||password.Length<6) return Results.BadRequest(new {success=false,message="Usuário precisa ter 3+ caracteres e senha 6+."});
    var users=LoadUsers(); if(users.Any(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Usuário já existe."});
    var salt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16); var u=new PanelUser{Username=username,DisplayName=string.IsNullOrWhiteSpace(req.DisplayName)?username:req.DisplayName.Trim(),PasswordHash=HashPassword(password,salt),Salt=Convert.ToBase64String(salt),Role="Staff",Active=true,CreatedAt=DateTimeOffset.UtcNow}; users.Add(u); SaveUsers(users); return Results.Ok(new {success=true});
});
app.MapPost("/api/panel/users/{username}/toggle", (string username, HttpRequest ctx) => { var me=CurrentUser(ctx); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); var users=LoadUsers(); var u=users.FirstOrDefault(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)); if(u is null)return Results.NotFound(); if(u.Username.Equals(me.Username,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new {success=false,message="Não é possível desativar seu próprio usuário."}); u.Active=!u.Active; SaveUsers(users); return Results.Ok(new {success=true,active=u.Active}); });
app.MapDelete("/api/panel/users/{username}", (string username, HttpRequest ctx) => { var me=CurrentUser(ctx); if(me is null)return Results.Unauthorized(); if(me.Role!="Owner")return Results.StatusCode(403); if(username.Equals(me.Username,StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new {success=false,message="Não é possível excluir seu próprio usuário."}); var users=LoadUsers(); var n=users.RemoveAll(x=>x.Username.Equals(username,StringComparison.OrdinalIgnoreCase)); if(n==0)return Results.NotFound(); SaveUsers(users); return Results.Ok(new {success=true}); });

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
    item.Username=username; item.Hwid=hwid; item.LastIp=GetClientIp(ctx); item.LastSeenAt=DateTimeOffset.UtcNow; item.LoginCount=1; item.Used=true; item.ExpiresAt=CalculateExpiry(item.Subscription); SaveLicenses(items);
    return Results.Ok(new {success=true,message="Licenca registrada com sucesso.",username=item.Username,subscription=item.Subscription,expiresAt=item.ExpiresAt});
});
app.MapPost("/api/v1/license/login", (LicenseRequest req, HttpContext ctx) => {
    if (!Authorized(ctx.Request, req.ApiKey)) return Results.Unauthorized();
    var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(req.License?.Trim()??"",StringComparison.OrdinalIgnoreCase));
    if(item is null) return Results.NotFound(new {success=false,message="Licenca invalida."}); if(item.Banned) return Results.StatusCode(403); if(!item.Used) return Results.BadRequest(new {success=false,message="Licenca ainda nao foi registrada."});
    if(!item.Username.Equals(req.Username?.Trim()??"",StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {success=false,message="Usuario incorreto."});
    if(!string.IsNullOrWhiteSpace(item.Hwid)&&!item.Hwid.Equals(req.Hwid?.Trim()??"",StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {success=false,message="HWID diferente do registrado."});
    if(item.ExpiresAt.HasValue&&item.ExpiresAt.Value<=DateTimeOffset.UtcNow) return Results.BadRequest(new {success=false,message="Licenca expirada."});
    item.LastIp=GetClientIp(ctx); item.LastSeenAt=DateTimeOffset.UtcNow; item.LoginCount++; SaveLicenses(items);
    return Results.Ok(new {success=true,message="Login autorizado.",username=item.Username,subscription=item.Subscription,expiresAt=item.ExpiresAt,ip=item.LastIp,lastSeenAt=item.LastSeenAt});
});

// Key management
app.MapGet("/api/v1/admin/licenses", (HttpRequest req) => {
    if(!PanelAuthorized(req) && !Authorized(req)) return Results.Unauthorized();
    var items=LoadLicenses(); return Results.Ok(new {success=true,total=items.Count,licenses=items});
});
app.MapPost("/api/v1/admin/licenses/generate", (GenerateLicensesRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
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
    SaveLicenses(items); return Results.Ok(new {success=true,count=created.Count,licenses=created});
});
app.MapPost("/api/v1/admin/license/create", (AdminLicenseRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); if(string.IsNullOrWhiteSpace(req.License)) return Results.BadRequest(new {success=false,message="License obrigatoria."});
    var items=LoadLicenses(); if(items.Any(x=>x.License.Equals(req.License.Trim(),StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new {success=false,message="Licenca ja existe."});
    items.Add(new LicenseRecord{License=req.License.Trim(),Subscription=string.IsNullOrWhiteSpace(req.Subscription)?"3 Days":req.Subscription.Trim(),Note=req.Note}); SaveLicenses(items); return Results.Ok(new {success=true,message="Licenca criada."});
});
app.MapPost("/api/v1/admin/license/{license}/ban", (string license, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase)); if(item is null)return Results.NotFound(); item.Banned=!item.Banned; SaveLicenses(items); return Results.Ok(new {success=true,banned=item.Banned});
});
app.MapDelete("/api/v1/admin/license/{license}", (string license, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); var items=LoadLicenses(); var removed=items.RemoveAll(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase)); if(removed==0)return Results.NotFound(); SaveLicenses(items); return Results.Ok(new {success=true});
});
app.MapPost("/api/v1/admin/license/{license}/reset", (string license, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized(); var items=LoadLicenses(); var item=items.FirstOrDefault(x=>x.License.Equals(license,StringComparison.OrdinalIgnoreCase)); if(item is null)return Results.NotFound(); item.Used=false; item.Username=null; item.Hwid=null; item.ExpiresAt=null; item.Banned=false; item.LastIp=null; item.LastSeenAt=null; item.LoginCount=0; SaveLicenses(items); return Results.Ok(new {success=true,message="License resetada."});
});
app.MapPut("/api/v1/admin/license/{license}", (string license, [FromBody] LicenseUpdateRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
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
    return Results.Ok(new {success=true,license=item});
});


// Manage Applications
app.MapGet("/api/v1/admin/apps", (HttpRequest req) => { if(!PanelAuthorized(req) && !Authorized(req))return Results.Unauthorized(); var apps=LoadApps(); return Results.Ok(new {success=true,total=apps.Count,apps}); });
app.MapPost("/api/v1/admin/apps/create", (AppCreateRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey))return Results.Unauthorized(); if(string.IsNullOrWhiteSpace(req.Name))return Results.BadRequest(new {success=false,message="Nome obrigatorio."});
    var apps=LoadApps(); if(apps.Any(x=>x.Name.Equals(req.Name.Trim(),StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new {success=false,message="Aplicacao ja existe."});
    var item=new AppRecord{Id=Guid.NewGuid().ToString("N"),Name=req.Name.Trim(),Version=string.IsNullOrWhiteSpace(req.Version)?"1.0":req.Version.Trim(),Description=req.Description?.Trim()??"",Active=true,CreatedAt=DateTimeOffset.UtcNow,OwnerId="NORTH-OWNER-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),Secret="NORTH-APP-"+Guid.NewGuid().ToString("N")[..24].ToUpperInvariant()}; apps.Add(item); SaveApps(apps); return Results.Ok(new {success=true,app=item});
});
app.MapPut("/api/v1/admin/apps/{id}", (string id, [FromBody] AppUpdateRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey))return Results.Unauthorized(); var apps=LoadApps(); var item=apps.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(item is null)return Results.NotFound();
    if(req.Name is not null)item.Name=req.Name.Trim(); if(req.Version is not null)item.Version=req.Version.Trim(); if(req.Description is not null)item.Description=req.Description.Trim(); if(req.Active.HasValue)item.Active=req.Active.Value; SaveApps(apps); return Results.Ok(new {success=true,app=item});
});
app.MapPost("/api/v1/admin/apps/{id}/secret/regenerate", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => {
    if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey)) return Results.Unauthorized();
    var apps=LoadApps(); var item=apps.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
    if(item is null) return Results.NotFound(new {success=false,message="Aplicacao nao encontrada."});
    item.Secret="NORTH-APP-"+Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
    if(string.IsNullOrWhiteSpace(item.OwnerId)) item.OwnerId="NORTH-OWNER-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    SaveApps(apps);
    return Results.Ok(new {success=true,message="Client Secret regenerado.",secret=item.Secret,ownerId=item.OwnerId,app=item});
});
app.MapDelete("/api/v1/admin/apps/{id}", (string id, [FromBody] ApiKeyRequest req, HttpContext ctx) => { if(!PanelAuthorized(ctx.Request) && !Authorized(ctx.Request,req.ApiKey))return Results.Unauthorized(); var apps=LoadApps(); var removed=apps.RemoveAll(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(removed==0)return Results.NotFound(); SaveApps(apps); return Results.Ok(new {success=true}); });

app.MapGet("/api/v1/admin/stats", (HttpRequest req) => { if(!PanelAuthorized(req) && !Authorized(req))return Results.Unauthorized(); var l=LoadLicenses(); var a=LoadApps(); return Results.Ok(new {success=true,licenses=l.Count,activeLicenses=l.Count(x=>x.Used&&!x.Banned&&(!x.ExpiresAt.HasValue||x.ExpiresAt>DateTimeOffset.UtcNow)),unused=l.Count(x=>!x.Used),banned=l.Count(x=>x.Banned),applications=a.Count,activeApplications=a.Count(x=>x.Active)}); });

app.Run();

static DateTimeOffset? CalculateExpiry(string subscription, int? duration=null, string? expiryUnit=null) {
    if(duration.HasValue && duration.Value>0 && !string.IsNullOrWhiteSpace(expiryUnit)) {
        var now=DateTimeOffset.UtcNow; var u=expiryUnit.Trim().ToLowerInvariant();
        if(u.StartsWith("min"))return now.AddMinutes(duration.Value); if(u.StartsWith("hour"))return now.AddHours(duration.Value); if(u.StartsWith("week"))return now.AddDays(duration.Value*7); if(u.StartsWith("month"))return now.AddMonths(duration.Value); if(u.StartsWith("year"))return now.AddYears(duration.Value); return now.AddDays(duration.Value);
    }
    if(string.IsNullOrWhiteSpace(subscription) || subscription.Contains("lifetime",StringComparison.OrdinalIgnoreCase))return null;
    var n=new string(subscription.Where(char.IsDigit).ToArray()); if(!int.TryParse(n,out var v)||v<=0)v=3;
    if(subscription.Contains("minute",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddMinutes(v); if(subscription.Contains("hour",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddHours(v); if(subscription.Contains("week",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddDays(v*7); if(subscription.Contains("month",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddDays(v*30); if(subscription.Contains("year",StringComparison.OrdinalIgnoreCase))return DateTimeOffset.UtcNow.AddDays(v*365); return DateTimeOffset.UtcNow.AddDays(v);
}
static string GenerateKey(string mask, bool uppercase, bool lowercase) {
    var chars=uppercase&&!lowercase?"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789":lowercase&&!uppercase?"abcdefghijklmnopqrstuvwxyz0123456789":"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    return System.Text.RegularExpressions.Regex.Replace(mask, "X+", m => { var a=new char[m.Length]; for(int i=0;i<a.Length;i++) a[i]=chars[Random.Shared.Next(chars.Length)]; return new string(a); });
}

public sealed class LicenseRequest { public string? ApiKey{get;set;} public string? License{get;set;} public string? Username{get;set;} public string? Hwid{get;set;} }
public sealed class PanelUser { public string Username{get;set;}=""; public string DisplayName{get;set;}=""; public string PasswordHash{get;set;}=""; public string Salt{get;set;}=""; public string Role{get;set;}="Staff"; public bool Active{get;set;}=true; public DateTimeOffset CreatedAt{get;set;} }
public sealed class PanelSession { public string Username{get;set;}=""; public DateTimeOffset ExpiresAt{get;set;} }
public sealed class PanelLoginRequest { public string? Username{get;set;} public string? Password{get;set;} }
public sealed class CreatePanelUserRequest { public string? Username{get;set;} public string? DisplayName{get;set;} public string? Password{get;set;} }
public sealed class ApiKeyRequest { public string? ApiKey{get;set;} }
public sealed class GenerateLicensesRequest { public int Count{get;set;}=1; public string Subscription{get;set;}="3 Days"; public string? Note{get;set;} public string? ApiKey{get;set;} public string? Mask{get;set;} public bool Uppercase{get;set;}=true; public bool Lowercase{get;set;} public int? Duration{get;set;} public string? ExpiryUnit{get;set;} }
public sealed class AdminLicenseRequest { public string? ApiKey{get;set;} public string? License{get;set;} public string? Subscription{get;set;} public string? Note{get;set;} }
public sealed class LicenseUpdateRequest { public string? ApiKey{get;set;} public string? Subscription{get;set;} public string? Note{get;set;} public int? Duration{get;set;} public string? ExpiryUnit{get;set;} }
public sealed class LicenseRecord { public string License{get;set;}=""; public string Subscription{get;set;}="3 Days"; public DateTimeOffset? ExpiresAt{get;set;} public bool Used{get;set;} public bool Banned{get;set;} public string? Username{get;set;} public string? Hwid{get;set;} public string? LastIp{get;set;} public DateTimeOffset? LastSeenAt{get;set;} public int LoginCount{get;set;} public string? Note{get;set;} }
public sealed class AppRecord { public string Id{get;set;}=""; public string Name{get;set;}=""; public string Version{get;set;}="1.0"; public string Description{get;set;}=""; public bool Active{get;set;}=true; public DateTimeOffset CreatedAt{get;set;} public string OwnerId{get;set;}=""; public string Secret{get;set;}=""; }
public sealed class AppCreateRequest { public string? ApiKey{get;set;} public string? Name{get;set;} public string? Version{get;set;} public string? Description{get;set;} }
public sealed class AppUpdateRequest { public string? ApiKey{get;set;} public string? Name{get;set;} public string? Version{get;set;} public string? Description{get;set;} public bool? Active{get;set;} }
