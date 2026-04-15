using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FinLedger;
using FinLedger.Models;

var builder = WebApplication.CreateBuilder(args);

// ── JWT ────────────────────────────────────────────────────────────────────────
var jwtKey    = "JVWLedgerSuperSecretKey2024!XyZ#";
var jwtIssuer = "JVWLedger";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = false,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtIssuer,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// ── Two separate databases ─────────────────────────────────────────────────────
// Ledger data → finledger.db
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite("Data Source=finledger.db"));

// User accounts → users.db (completely separate)
builder.Services.AddDbContext<UserDbContext>(opt =>
    opt.UseSqlite("Data Source=users.db"));

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ── Seed both databases on startup ─────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ledgerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await AppDbContext.SeedAsync(ledgerDb);

    var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
    await UserDbContext.SeedAsync(userDb);
}

// ── Helpers ────────────────────────────────────────────────────────────────────
static string GenRef() =>
    "TXN-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" +
    Random.Shared.Next(1000, 9999).ToString();

string GenerateToken(User user)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name,           user.Username),
        new Claim(ClaimTypes.Role,           user.Role)
    };
    var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        issuer:             jwtIssuer,
        claims:             claims,
        expires:            DateTime.UtcNow.AddHours(8),
        signingCredentials: creds
    );
    return new JwtSecurityTokenHandler().WriteToken(token);
}

// Read role from header (set by frontend after login)
static string GetRole(HttpContext ctx) =>
    ctx.Request.Headers["X-User-Role"].ToString();

// ══════════════════════════════════════════════════════════════════════════════
//  AUTH  /api/auth  — uses users.db
// ══════════════════════════════════════════════════════════════════════════════

// POST /api/auth/login
app.MapPost("/api/auth/login", async (LoginRequest req, UserDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest("Username and password are required.");

    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
    if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        return Results.Unauthorized();

    var token = GenerateToken(user);
    return Results.Ok(new { token, username = user.Username, role = user.Role });
});

// POST /api/auth/register  — admin only, creates a new system user
app.MapPost("/api/auth/register", async (RegisterRequest req, UserDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();

    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest("Username and password are required.");

    if (await db.Users.AnyAsync(u => u.Username == req.Username))
        return Results.BadRequest("Username already exists.");

    var newUser = new User
    {
        Username     = req.Username.Trim(),
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
        Role         = req.Role == "admin" ? "admin" : "viewer",
        CreatedAt    = DateTime.UtcNow
    };
    db.Users.Add(newUser);
    await db.SaveChangesAsync();
    return Results.Ok(new { newUser.Id, newUser.Username, newUser.Role });
});

// GET /api/auth/users  — list all system users (admin only)
app.MapGet("/api/auth/users", async (UserDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var users = await db.Users
        .Select(u => new { u.Id, u.Username, u.Role, u.CreatedAt })
        .ToListAsync();
    return Results.Ok(users);
});

// DELETE /api/auth/users/{id}  — delete a system user (admin only)
app.MapDelete("/api/auth/users/{id:int}", async (int id, UserDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var user = await db.Users.FindAsync(id);
    if (user is null) return Results.NotFound();
    if (user.Username == "admin") return Results.BadRequest("Cannot delete the default admin.");
    db.Users.Remove(user);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ══════════════════════════════════════════════════════════════════════════════
//  LEDGER ACCOUNTS  /api/accounts  — uses finledger.db
// ══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/accounts", async (AppDbContext db) =>
    await db.Accounts.OrderByDescending(a => a.Id).ToListAsync());

app.MapGet("/api/accounts/{id:int}", async (int id, AppDbContext db) =>
    await db.Accounts.FindAsync(id) is Account a ? Results.Ok(a) : Results.NotFound());

app.MapPost("/api/accounts", async (Account input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    input.Id = 0; input.CreatedAt = DateTime.UtcNow; input.UpdatedAt = DateTime.UtcNow;
    db.Accounts.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/accounts/{input.Id}", new {
    id          = input.Id,
    name        = input.Name,
    type        = input.Type,
    balance     = input.Balance,
    createdAt   = input.CreatedAt
});
});

app.MapPut("/api/accounts/{id:int}", async (int id, Account input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Accounts.FindAsync(id);
    if (ex is null) return Results.NotFound();
    ex.Name = input.Name; ex.Type = input.Type;
    ex.Balance = input.Balance; ex.Description = input.Description;
    ex.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ex);
});

app.MapDelete("/api/accounts/{id:int}", async (int id, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Accounts.FindAsync(id);
    if (ex is null) return Results.NotFound();
    db.Accounts.Remove(ex);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ══════════════════════════════════════════════════════════════════════════════
//  TRANSACTIONS  /api/transactions
// ══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/transactions", async (string? type, AppDbContext db) =>
{
    var q = db.Transactions.AsQueryable();
    if (!string.IsNullOrEmpty(type)) q = q.Where(t => t.Type == type);
    return await q.OrderByDescending(t => t.Id).ToListAsync();
});

app.MapGet("/api/transactions/{id:int}", async (int id, AppDbContext db) =>
    await db.Transactions.FindAsync(id) is Transaction t ? Results.Ok(t) : Results.NotFound());

app.MapPost("/api/transactions", async (Transaction input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var account = await db.Accounts.FindAsync(input.AccountId);
    if (account is null) return Results.BadRequest("Ledger account not found.");
    input.Id = 0; input.Ref = GenRef(); input.CreatedAt = DateTime.UtcNow;
    account.Balance   = account.Balance + input.Amount;
    account.UpdatedAt = DateTime.UtcNow;
    db.Transactions.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/transactions/{input.Id}", new {
    id          = input.Id,
    accountId   = input.AccountId,
    type        = input.Type,
    amount      = input.Amount,
    description = input.Description,
    status      = input.Status,
    refCode     = input.Ref,
    createdAt   = input.CreatedAt
});
});

app.MapPut("/api/transactions/{id:int}", async (int id, Transaction input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Transactions.FindAsync(id);
    if (ex is null) return Results.NotFound();
    ex.Description = input.Description; ex.Status = input.Status;
    await db.SaveChangesAsync();
    return Results.Ok(ex);
});

app.MapDelete("/api/transactions/{id:int}", async (int id, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Transactions.FindAsync(id);
    if (ex is null) return Results.NotFound();
    var account = await db.Accounts.FindAsync(ex.AccountId);
    if (account is not null) { account.Balance = account.Balance - ex.Amount; account.UpdatedAt = DateTime.UtcNow; }
    db.Transactions.Remove(ex);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Transfer between ledger accounts — fixed with explicit double casting
app.MapPost("/api/transactions/transfer", async (TransferRequest req, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    if (req.FromAccountId == req.ToAccountId)
        return Results.BadRequest("Source and destination accounts must differ.");
    if (req.Amount <= 0)
        return Results.BadRequest("Transfer amount must be positive.");

    var from = await db.Accounts.FindAsync(req.FromAccountId);
    var to   = await db.Accounts.FindAsync(req.ToAccountId);
    if (from is null || to is null) return Results.BadRequest("Ledger account not found.");

    var now  = DateTime.UtcNow;
    var @ref = GenRef();
    double amt = req.Amount;

    var debit = new Transaction
    {
        AccountId   = from.Id,
        Type        = "transfer",
        Amount      = -amt,
        Description = string.IsNullOrWhiteSpace(req.Note) ? $"Transfer to {to.Name}" : req.Note,
        Status      = string.IsNullOrWhiteSpace(req.Status) ? "completed" : req.Status,
        Ref         = @ref + "-OUT",
        CreatedAt   = now
    };
    var credit = new Transaction
    {
        AccountId   = to.Id,
        Type        = "transfer",
        Amount      = amt,
        Description = string.IsNullOrWhiteSpace(req.Note) ? $"Transfer from {from.Name}" : req.Note,
        Status      = string.IsNullOrWhiteSpace(req.Status) ? "completed" : req.Status,
        Ref         = @ref + "-IN",
        CreatedAt   = now
    };

    from.Balance   = from.Balance - amt;
    to.Balance     = to.Balance   + amt;
    from.UpdatedAt = now;
    to.UpdatedAt   = now;

    db.Transactions.AddRange(debit, credit);
    await db.SaveChangesAsync();
    return Results.Ok(new { debit, credit });
});

// ══════════════════════════════════════════════════════════════════════════════
//  PRODUCTS  /api/products
// ══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/products", async (AppDbContext db) =>
    await db.Products.OrderByDescending(p => p.Id).ToListAsync());

app.MapGet("/api/products/{id:int}", async (int id, AppDbContext db) =>
    await db.Products.FindAsync(id) is Product p ? Results.Ok(p) : Results.NotFound());

app.MapPost("/api/products", async (Product input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    input.Id = 0; input.CreatedAt = DateTime.UtcNow; input.UpdatedAt = DateTime.UtcNow;
    db.Products.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/products/{input.Id}", new {
    id          = input.Id,
    name        = input.Name,
    description = input.Description,
    price       = input.Price,
    stock       = input.Stock,
    createdAt   = input.CreatedAt
});
});

app.MapPut("/api/products/{id:int}", async (int id, Product input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Products.FindAsync(id);
    if (ex is null) return Results.NotFound();
    ex.Name = input.Name; ex.Category = input.Category; ex.Price = input.Price;
    ex.Stock = input.Stock; ex.Status = input.Status; ex.Description = input.Description;
    ex.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ex);
});

app.MapDelete("/api/products/{id:int}", async (int id, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Products.FindAsync(id);
    if (ex is null) return Results.NotFound();
    db.Products.Remove(ex);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ══════════════════════════════════════════════════════════════════════════════
//  CUSTOMERS  /api/customers
// ══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/customers", async (AppDbContext db) =>
    await db.Customers.OrderByDescending(c => c.Id).ToListAsync());

app.MapGet("/api/customers/{id:int}", async (int id, AppDbContext db) =>
    await db.Customers.FindAsync(id) is Customer c ? Results.Ok(c) : Results.NotFound());

app.MapPost("/api/customers", async (Customer input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    input.Id = 0; input.CreatedAt = DateTime.UtcNow; input.UpdatedAt = DateTime.UtcNow;
    db.Customers.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/customers/{input.Id}", new {
    id          = input.Id,
    
    email       = input.Email,
    phone       = input.Phone,
    
    createdAt   = input.CreatedAt
});
});

app.MapPut("/api/customers/{id:int}", async (int id, Customer input, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Customers.FindAsync(id);
    if (ex is null) return Results.NotFound();
    ex.FirstName = input.FirstName; ex.LastName = input.LastName;
    ex.Email = input.Email; ex.Phone = input.Phone; ex.City = input.City;
    ex.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ex);
});

app.MapDelete("/api/customers/{id:int}", async (int id, AppDbContext db, HttpContext ctx) =>
{
    if (GetRole(ctx) != "admin") return Results.Forbid();
    var ex = await db.Customers.FindAsync(id);
    if (ex is null) return Results.NotFound();
    db.Customers.Remove(ex);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ══════════════════════════════════════════════════════════════════════════════
//  STATS  /api/stats
// ══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/stats", async (AppDbContext db) =>
{
    var accountCount     = await db.Accounts.CountAsync();
    var accounts         = await db.Accounts.ToListAsync();
    var totalBalance     = accounts.Sum(a => a.Balance);
    var transactionCount = await db.Transactions.CountAsync();
    var productCount     = await db.Products.CountAsync();
    return Results.Ok(new { accounts = accountCount, totalBalance, transactions = transactionCount, products = productCount });
});

app.MapFallbackToFile("index.html");
app.Run();

// ── DTOs ───────────────────────────────────────────────────────────────────────
record TransferRequest(int FromAccountId, int ToAccountId, double Amount, string? Note, string? Status);
record LoginRequest(string Username, string Password);
record RegisterRequest(string Username, string Password, string Role);
