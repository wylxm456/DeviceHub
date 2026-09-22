using System.Security.Cryptography;
using System.Text;

namespace DeviceHub.Core.Security;

/// <summary>
/// 认证与会话：HMI 单会话模型——一台面板同一时刻只有一个当前用户，
/// 登录即建立会话，再次登录即切换用户。
///
/// 两条安全设计：
/// 1. 错误信息统一为"用户名或密码错误"，不区分"用户不存在"和"密码错误"——
///    不给撞库者反馈哪些用户名有效（防用户枚举）；
/// 2. 配置里的密码支持 "sha256:十六进制" 哈希形态（按前缀自动识别）——
///    密码不以明文落盘。本实现仍是无盐快速哈希，生产环境要换加盐慢哈希
///    （PBKDF2/bcrypt）并对接域账号：IAuthenticator 缝隙就是为此预留的。
/// </summary>
public sealed class AuthService
{
    public const string HashPrefix = "sha256:";

    private readonly Dictionary<string, AuthUser> _usersByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前会话用户，未登录为 null。</summary>
    public UserIdentity? CurrentUser { get; private set; }

    /// <summary>登录/登出/切换用户后触发（UI 线程调用 Login 时与界面同线程）。</summary>
    public event Action? CurrentUserChanged;

    public AuthService(IReadOnlyList<AuthUser> users)
    {
        if (users.Count == 0)
        {
            throw new ArgumentException("至少要配置一个登录账号。", nameof(users));
        }

        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrEmpty(user.Password))
            {
                throw new ArgumentException("账号配置缺少用户名或密码。", nameof(users));
            }

            // 重复用户名靠构造后的数量校验显式拒绝——比静默覆盖更早暴露配置错误
            _usersByName[user.Username] = user;
        }

        if (_usersByName.Count != users.Count)
        {
            throw new ArgumentException("账号配置存在重复用户名。", nameof(users));
        }
    }

    public LoginResult Login(string username, string password)
    {
        var unifiedError = "用户名或密码错误";

        if (string.IsNullOrWhiteSpace(username) || password is null ||
            !_usersByName.TryGetValue(username.Trim(), out var user) ||
            !VerifyPassword(user, password))
        {
            return new LoginResult(false, null, unifiedError);
        }

        CurrentUser = new UserIdentity(user.Username, user.Role);
        CurrentUserChanged?.Invoke();
        return new LoginResult(true, CurrentUser, null);
    }

    public void Logout()
    {
        if (CurrentUser is null)
        {
            return;
        }

        CurrentUser = null;
        CurrentUserChanged?.Invoke();
    }

    /// <summary>当前会话是否具备某项能力。未登录 = 一律不具备。</summary>
    public bool HasPermission(Permission permission) => CurrentUser?.HasPermission(permission) == true;

    private static bool VerifyPassword(AuthUser user, string password)
    {
        if (user.Password.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var stored = user.Password[HashPrefix.Length..].Trim();
            return string.Equals(stored, PasswordHasher.Hash(password), StringComparison.Ordinal);
        }

        return user.Password == password;
    }
}

/// <summary>登录结果。</summary>
public sealed record LoginResult(bool Success, UserIdentity? User, string? Error);

/// <summary>口令哈希工具：SHA-256 十六进制小写（不带前缀）。仅用于本地演示配置，生产请用加盐慢哈希。</summary>
public static class PasswordHasher
{
    public static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
}
