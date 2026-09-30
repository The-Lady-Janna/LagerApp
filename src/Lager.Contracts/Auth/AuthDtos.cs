namespace Lager.Contracts.Auth;

public record LoginRequest(string Username, string Password);

public record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    UserDto User);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record AdminResetPasswordRequest(string NewPassword, bool MustChangeOnNextLogin = true);

public record CreateUserRequest(
    string Username,
    string Password,
    string[] Roles,
    string? Email = null,
    string? DisplayName = null,
    bool MustChangePassword = true);

public record UpdateUserRequest(
    string[] Roles,
    string? Email = null,
    string? DisplayName = null);

public record UserDto(
    Guid Id,
    string Username,
    string? Email,
    string? DisplayName,
    string[] Roles,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    DateTime CreatedAt);
