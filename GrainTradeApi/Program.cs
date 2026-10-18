using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;

// ---------------------------------------------------------------------------
//  Grain Trade API  -  Flutter app  <->  this API  <->  SQL Server (SSMS)
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

var cs = builder.Configuration.GetConnectionString("Default")
         ?? throw new Exception("Missing ConnectionStrings:Default in appsettings.json");
var jwtKey = builder.Configuration["Jwt:Key"]
             ?? throw new Exception("Missing Jwt:Key in appsettings.json");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            NameClaimType = "phone",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// ---------------------------- helpers ----------------------------

string MakeToken(string phone, string role)
{
    var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    var jwt = new JwtSecurityToken(
        claims: new[] { new Claim("phone", phone), new Claim("role", role) },
        expires: DateTime.UtcNow.AddDays(30),
        signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(jwt);
}

object ToUser(UserRow u) => new
{
    name = u.Name,
    phone = u.Phone,
    village = u.Village,
    address = u.Address,
    role = u.Role
};

string PhoneOf(ClaimsPrincipal p) => p.FindFirst("phone")!.Value;

const string RequestSelect = @"
SELECT r.Id,
       u.Name  AS FarmerName,
       r.FarmerPhone AS Phone,
       u.Village,
       u.Address,
       r.Item, r.Unit, r.Quantity, r.ExpectedPrice, r.Notes,
       TODATETIMEOFFSET(r.CreatedAt, 0) AS CreatedAt,
       r.Status, r.AdminPrice, r.FinalQuantity, r.PaymentStatus, r.AdminNote,
       TODATETIMEOFFSET(r.UpdatedAt, 0) AS UpdatedAt
FROM dbo.Requests r
JOIN dbo.Users u ON u.Phone = r.FarmerPhone ";

// ------------------- create default admin + demo farmer -------------------

void Seed(SqlConnection db, string phone, string name, string village, string password, string role)
{
    var exists = db.ExecuteScalar<int>("SELECT COUNT(1) FROM dbo.Users WHERE Phone = @phone", new { phone });
    if (exists > 0) return;
    db.Execute(
        "INSERT INTO dbo.Users (Phone, Name, Village, PasswordHash, Role) VALUES (@phone, @name, @village, @hash, @role)",
        new { phone, name, village, hash = BCrypt.Net.BCrypt.HashPassword(password), role });
}

using (var db = new SqlConnection(cs))
{
    db.Open();
    Seed(db, "admin", "Company Admin", "Head Office", "admin123", "admin");
    Seed(db, "9999999999", "Ramesh Patil", "Baramati", "farmer123", "farmer");
}

// ------------------------------- endpoints -------------------------------

app.MapGet("/api/ping", () => Results.Ok("ok"));

// ---- auth ----
app.MapPost("/api/auth/login", async (LoginDto d) =>
{
    using var db = new SqlConnection(cs);
    var u = await db.QuerySingleOrDefaultAsync<UserRow>(
        "SELECT * FROM dbo.Users WHERE Phone = @Phone", new { Phone = (d.Phone ?? "").Trim() });
    if (u == null || !BCrypt.Net.BCrypt.Verify(d.Password ?? "", u.PasswordHash))
        return Results.Json(new { error = "Wrong mobile number or password." }, statusCode: 401);
    return Results.Ok(new { token = MakeToken(u.Phone, u.Role), user = ToUser(u) });
});

app.MapPost("/api/auth/register", async (RegisterDto d) =>
{
    var name = (d.Name ?? "").Trim();
    var village = (d.Village ?? "").Trim();
    var phone = (d.Phone ?? "").Trim();
    var password = d.Password ?? "";

    if (name.Length == 0 || village.Length == 0)
        return Results.BadRequest(new { error = "Enter name and village." });
    if (phone.Length != 10 || !phone.All(char.IsDigit))
        return Results.BadRequest(new { error = "Enter a valid 10-digit mobile number." });
    if (password.Length < 4)
        return Results.BadRequest(new { error = "Password must be at least 4 characters." });

    using var db = new SqlConnection(cs);
    var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Users WHERE Phone = @phone", new { phone });
    if (exists > 0)
        return Results.BadRequest(new { error = "This mobile number is already registered." });

    await db.ExecuteAsync(
        "INSERT INTO dbo.Users (Phone, Name, Village, Address, PasswordHash, Role) VALUES (@phone, @name, @village, @address, @hash, 'farmer')",
        new { phone, name, village, address = (d.Address ?? "").Trim(), hash = BCrypt.Net.BCrypt.HashPassword(password) });

    var u = await db.QuerySingleAsync<UserRow>("SELECT * FROM dbo.Users WHERE Phone = @phone", new { phone });
    return Results.Ok(new { token = MakeToken(u.Phone, u.Role), user = ToUser(u) });
});

// ---- my profile ----
app.MapGet("/api/me", async (ClaimsPrincipal user) =>
{
    using var db = new SqlConnection(cs);
    var u = await db.QuerySingleOrDefaultAsync<UserRow>(
        "SELECT * FROM dbo.Users WHERE Phone = @p", new { p = PhoneOf(user) });
    return u == null ? Results.Unauthorized() : Results.Ok(ToUser(u));
}).RequireAuthorization();

app.MapPut("/api/me", async (ProfileDto d, ClaimsPrincipal user) =>
{
    var name = (d.Name ?? "").Trim();
    var village = (d.Village ?? "").Trim();
    if (name.Length == 0 || village.Length == 0)
        return Results.BadRequest(new { error = "Enter name and village." });

    using var db = new SqlConnection(cs);
    await db.ExecuteAsync(
        "UPDATE dbo.Users SET Name = @name, Village = @village, Address = @address WHERE Phone = @p",
        new { name, village, address = (d.Address ?? "").Trim(), p = PhoneOf(user) });
    var u = await db.QuerySingleAsync<UserRow>("SELECT * FROM dbo.Users WHERE Phone = @p", new { p = PhoneOf(user) });
    return Results.Ok(ToUser(u));
}).RequireAuthorization();

// ---- farmers list (admin) ----
app.MapGet("/api/farmers", async () =>
{
    using var db = new SqlConnection(cs);
    var rows = await db.QueryAsync<UserRow>("SELECT * FROM dbo.Users WHERE Role = 'farmer' ORDER BY Name");
    return Results.Ok(rows.Select(ToUser));
}).RequireAuthorization(p => p.RequireRole("admin"));

// ---- requests ----
// admin sees all requests, a farmer sees only his own
app.MapGet("/api/requests", async (ClaimsPrincipal user) =>
{
    using var db = new SqlConnection(cs);
    var sql = RequestSelect
              + (user.IsInRole("admin") ? "" : "WHERE r.FarmerPhone = @Phone ")
              + "ORDER BY r.CreatedAt DESC";
    var rows = await db.QueryAsync<RequestRow>(sql, new { Phone = PhoneOf(user) });
    return Results.Ok(rows);
}).RequireAuthorization();

// farmer raises a new request
app.MapPost("/api/requests", async (NewRequestDto d, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(d.Item) || d.Quantity <= 0)
        return Results.BadRequest(new { error = "Enter a valid quantity." });

    var unit = (d.Unit is "Kg" or "Quintal" or "Ton") ? d.Unit : "Quintal";
    var id = Guid.NewGuid().ToString("N");

    using var db = new SqlConnection(cs);
    await db.ExecuteAsync(
        @"INSERT INTO dbo.Requests (Id, FarmerPhone, Item, Unit, Quantity, ExpectedPrice, Notes)
          VALUES (@id, @phone, @item, @unit, @qty, @price, @notes)",
        new
        {
            id,
            phone = PhoneOf(user),
            item = d.Item.Trim(),
            unit,
            qty = d.Quantity,
            price = d.ExpectedPrice,
            notes = (d.Notes ?? "").Trim()
        });
    return Results.Ok(new { id });
}).RequireAuthorization(p => p.RequireRole("farmer"));

// company updates status / price / payment
app.MapPut("/api/requests/{id}", async (string id, UpdateRequestDto d) =>
{
    string[] allowed = { "new", "contacted", "purchased", "rejected" };
    if (!allowed.Contains(d.Status))
        return Results.BadRequest(new { error = "Invalid status." });

    using var db = new SqlConnection(cs);
    var n = await db.ExecuteAsync(
        @"UPDATE dbo.Requests
          SET Status = @status, AdminPrice = @price, FinalQuantity = @qty,
              PaymentStatus = @pay, AdminNote = @note, UpdatedAt = SYSUTCDATETIME()
          WHERE Id = @id",
        new
        {
            id,
            status = d.Status,
            price = d.AdminPrice,
            qty = d.FinalQuantity,
            pay = d.PaymentStatus == "paid" ? "paid" : "pending",
            note = (d.AdminNote ?? "").Trim()
        });
    return n == 0 ? Results.NotFound(new { error = "Request not found." }) : Results.Ok();
}).RequireAuthorization(p => p.RequireRole("admin"));

// admin can delete any record; a farmer can cancel only his own NEW request
app.MapDelete("/api/requests/{id}", async (string id, ClaimsPrincipal user) =>
{
    using var db = new SqlConnection(cs);
    var n = user.IsInRole("admin")
        ? await db.ExecuteAsync("DELETE FROM dbo.Requests WHERE Id = @id", new { id })
        : await db.ExecuteAsync(
            "DELETE FROM dbo.Requests WHERE Id = @id AND FarmerPhone = @phone AND Status = 'new'",
            new { id, phone = PhoneOf(user) });
    return n == 0 ? Results.NotFound(new { error = "Request not found." }) : Results.Ok();
}).RequireAuthorization();

app.Run();

// ------------------------------- models -------------------------------

record LoginDto(string? Phone, string? Password);
record RegisterDto(string? Name, string? Village, string? Phone, string? Password, string? Address);
record ProfileDto(string? Name, string? Village, string? Address);
record NewRequestDto(string Item, decimal Quantity, string Unit, decimal? ExpectedPrice, string? Notes);
record UpdateRequestDto(string Status, decimal? AdminPrice, decimal? FinalQuantity, string? PaymentStatus, string? AdminNote);

class UserRow
{
    public string Phone { get; set; } = "";
    public string Name { get; set; } = "";
    public string Village { get; set; } = "";
    public string Address { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "";
}

class RequestRow
{
    public string Id { get; set; } = "";
    public string FarmerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Village { get; set; } = "";
    public string Address { get; set; } = "";
    public string Item { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal? ExpectedPrice { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "";
    public decimal? AdminPrice { get; set; }
    public decimal? FinalQuantity { get; set; }
    public string PaymentStatus { get; set; } = "";
    public string AdminNote { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
