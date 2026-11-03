namespace TodoApi;

/// <summary>Credentials submitted for registration or login.</summary>
public sealed record AuthRequest(string? Username, string? Password);

/// <summary>JWT returned after successful login.</summary>
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAtUtc);
