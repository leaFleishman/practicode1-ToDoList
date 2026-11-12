using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using TodoApi;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // כפתור Authorize ב-Swagger לשליחת JWT
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "הדביקי כאן את ה-token (בלי המילה Bearer)"
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
// הגדרת CORS - הרשאה כללית (כפי שמורה המשימה)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReact", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var connectionString = builder.Configuration.GetConnectionString("ToDoDB")
    ?? throw new InvalidOperationException("Connection string 'ToDoDB' is not configured.");
// גרסת השרת נקבעת פעם אחת, מחוץ ל-lambda. ServerVersion.AutoDetect בתוך ה-lambda
// פותח חיבור נוסף ל-DB בכל יצירת DbContext וגורם לחריגה מ-max_user_connections.
var serverVersion = new MySqlServerVersion(new Version(8, 0, 36));
// חלופה: זיהוי אוטומטי שרץ פעם אחת בלבד בעליית האפליקציה:
// var serverVersion = ServerVersion.AutoDetect(connectionString);
builder.Services.AddDbContext<ToDoDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

var jwtSettings = builder.Configuration.GetSection("Jwt");
var jwtSecret = jwtSettings["Secret"]
    ?? throw new InvalidOperationException("JWT secret is not configured. Set Jwt__Secret.");
var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);
if (jwtKey.Length < 32)
{
    throw new InvalidOperationException("JWT secret must be at least 32 bytes long.");
}

// ערך ריק (למשל משתנה סביבה ריק ב-Render) נחשב כלא מוגדר, ואז משתמשים בברירת המחדל.
// בלי זה, ערך ריק גורם לכל טוקן להידחות (IDX10208) והמשתמש מוחזר למסך ההתחברות.
var jwtIssuer = string.IsNullOrWhiteSpace(jwtSettings["Issuer"]) ? "TodoApi" : jwtSettings["Issuer"]!.Trim();
var jwtAudience = string.IsNullOrWhiteSpace(jwtSettings["Audience"]) ? "TodoListReact" : jwtSettings["Audience"]!.Trim();
var tokenLifetimeMinutes = jwtSettings.GetValue<int>("TokenLifetimeMinutes", 60);
if (tokenLifetimeMinutes <= 0)
{
    throw new InvalidOperationException("JWT token lifetime must be greater than zero.");
}

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// יצירת טבלת Users אם אינה קיימת
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ToDoDbContext>();
    db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS Users (
        Id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
        Username VARCHAR(50) NOT NULL,
        PasswordHash VARCHAR(512) NOT NULL,
        UNIQUE KEY IX_Users_Username (Username)
    ) CHARACTER SET utf8mb4;");

    // יצירת טבלת Items אם אינה קיימת (למשל במסד נתונים חדש בענן)
    db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS Items (
        Id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
        Name VARCHAR(100) NULL,
        IsComplete TINYINT(1) NULL,
        UserId INT NULL,
        INDEX IX_Items_UserId (UserId)
    ) CHARACTER SET utf8mb4;");

    // הוספת עמודת UserId לטבלת Items קיימת אם אינה קיימת - כדי שכל משתמש יראה רק את המשימות שלו
    var hasUserIdColumn = db.Database.SqlQueryRaw<int>(@"SELECT COUNT(*) AS Value
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Items' AND COLUMN_NAME = 'UserId'")
        .AsEnumerable()
        .Single() > 0;
    if (!hasUserIdColumn)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Items ADD COLUMN UserId INT NULL, ADD INDEX IX_Items_UserId (UserId);");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("AllowReact");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "API is running");

app.MapGet("/auth/validate", () => Results.NoContent())
    .RequireAuthorization();

app.MapPost("/auth/register", async (
    AuthRequest? request,
    ToDoDbContext context,
    IPasswordHasher<User> passwordHasher) =>
{
    var username = request?.Username?.Trim();
    if (string.IsNullOrWhiteSpace(username) || username.Length is < 3 or > 50)
    {
        return Results.BadRequest(new { message = "שם המשתמש חייב להכיל בין 3 ל-50 תווים." });
    }

    var password = request?.Password;
    if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 128)
    {
        return Results.BadRequest(new { message = "הסיסמה חייבת להכיל בין 8 ל-128 תווים." });
    }

    if (await context.Users.AnyAsync(user => user.Username == username))
    {
        return Results.Conflict(new { message = "שם המשתמש כבר קיים." });
    }

    var user = new User { Username = username };
    user.PasswordHash = passwordHasher.HashPassword(user, password);
    context.Users.Add(user);
    try
    {
        await context.SaveChangesAsync();
    }
    catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
    {
        return Results.Conflict(new { message = "שם המשתמש כבר קיים." });
    }

    return Results.Created("/auth/login", new { message = "ההרשמה הושלמה בהצלחה." });
})
.AllowAnonymous();

app.MapPost("/auth/login", async (
    AuthRequest? request,
    ToDoDbContext context,
    IPasswordHasher<User> passwordHasher) =>
{
    if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrEmpty(request.Password))
    {
        return Results.BadRequest(new { message = "יש למלא שם משתמש וסיסמה." });
    }

    var username = request.Username.Trim();
    var user = await context.Users.SingleOrDefaultAsync(candidate => candidate.Username == username);
    if (user is null ||
        passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(tokenLifetimeMinutes);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username)
    };
    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(jwtKey),
        SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        issuer: jwtIssuer,
        audience: jwtAudience,
        claims: claims,
        expires: expiresAt.UtcDateTime,
        signingCredentials: credentials);

    return Results.Ok(new LoginResponse(
        new JwtSecurityTokenHandler().WriteToken(token),
        expiresAt));
})
.AllowAnonymous();

// מזהה המשתמש המחובר, מתוך ה-JWT
static int CurrentUserId(ClaimsPrincipal user) =>
    int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated user has no id claim."));

// 1. שליפת המשימות של המשתמש המחובר בלבד
app.MapGet("/items", async (ClaimsPrincipal user, ToDoDbContext context) =>
{
    var userId = CurrentUserId(user);
    return await context.Items.Where(item => item.UserId == userId).ToListAsync();
}).RequireAuthorization();

// 2. הוספת משימה חדשה למשתמש המחובר
app.MapPost("/items", async (Item item, ClaimsPrincipal user, ToDoDbContext context) =>
{
    item.Id = 0;
    item.UserId = CurrentUserId(user);
    context.Items.Add(item);
    await context.SaveChangesAsync();

    return Results.Created($"/items/{item.Id}", item);
}).RequireAuthorization();

// 3. עדכון משימה
app.MapPut("/items/{id}", async (int id, Item updatedItem, ClaimsPrincipal user, ToDoDbContext context) =>
{
    var userId = CurrentUserId(user);
    var item = await context.Items.SingleOrDefaultAsync(i => i.Id == id && i.UserId == userId);

    if (item == null)
        return Results.NotFound();

    // אם לא נשלח שם - שומרים את השם הקיים
    item.Name = updatedItem.Name ?? item.Name;
    item.IsComplete = updatedItem.IsComplete;

    await context.SaveChangesAsync();

    return Results.Ok(item);
}).RequireAuthorization();

// 4. מחיקת משימה
app.MapDelete("/items/{id}", async (int id, ClaimsPrincipal user, ToDoDbContext context) =>
{
    var userId = CurrentUserId(user);
    var item = await context.Items.SingleOrDefaultAsync(i => i.Id == id && i.UserId == userId);

    if (item == null)
        return Results.NotFound();

    context.Items.Remove(item);
    await context.SaveChangesAsync();

    return Results.NoContent();
}).RequireAuthorization();

app.Run();
