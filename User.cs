
namespace TodoApi;

public sealed class User
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public string PasswordHash { get; set; } = string.Empty;
}
