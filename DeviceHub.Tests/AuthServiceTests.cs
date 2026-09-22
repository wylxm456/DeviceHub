using DeviceHub.Core.Configuration;
using DeviceHub.Core.Security;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// 认证与会话单测：登录判定、统一错误信息（防用户枚举）、权限矩阵、
/// 哈希密码支持、配置校验。纯逻辑测试，零等待。
/// </summary>
public class AuthServiceTests
{
    private static List<AuthUser> DefaultUsers() =>
    [
        new AuthUser("operator", "op123", UserRole.Operator),
        new AuthUser("engineer", "eng123", UserRole.Engineer),
    ];

    [Fact]
    public void Login_Success_SetsSessionIdentity()
    {
        var auth = new AuthService(DefaultUsers());

        var result = auth.Login("engineer", "eng123");

        Assert.True(result.Success);
        Assert.Equal("engineer", auth.CurrentUser?.Username);
        Assert.Equal(UserRole.Engineer, auth.CurrentUser?.Role);
    }

    [Fact]
    public void Login_Failure_UsesUnifiedMessage_ForBothWrongPasswordAndUnknownUser()
    {
        var auth = new AuthService(DefaultUsers());

        // 密码错与用户不存在必须返回同一条错误：不给撞库者反馈哪些用户名有效
        var wrongPassword = auth.Login("engineer", "nope");
        var unknownUser = auth.Login("ghost", "whatever");

        Assert.False(wrongPassword.Success);
        Assert.False(unknownUser.Success);
        Assert.Equal(wrongPassword.Error, unknownUser.Error);
        Assert.Null(auth.CurrentUser);
    }

    [Fact]
    public void Login_ReLogin_SwitchesUser()
    {
        var auth = new AuthService(DefaultUsers());

        auth.Login("operator", "op123");
        auth.Login("engineer", "eng123");

        Assert.Equal("engineer", auth.CurrentUser?.Username);
    }

    [Fact]
    public void Logout_ClearsSession_AndRaisesChangedOncePerAction()
    {
        var auth = new AuthService(DefaultUsers());
        var changes = 0;
        auth.CurrentUserChanged += () => changes++;

        auth.Login("operator", "op123");
        auth.Logout();
        auth.Logout(); // 未登录再登出是无操作，不发事件

        Assert.Null(auth.CurrentUser);
        Assert.Equal(2, changes); // 登录一次 + 登出一次
    }

    [Fact]
    public void PermissionMatrix_OperatorLimited_EngineerFull()
    {
        var auth = new AuthService(DefaultUsers());

        auth.Login("operator", "op123");
        Assert.True(auth.CurrentUser!.HasPermission(Permission.AcknowledgeAlarm));
        Assert.False(auth.CurrentUser.HasPermission(Permission.MotionControl));
        Assert.False(auth.CurrentUser.HasPermission(Permission.VisionGuide));

        auth.Login("engineer", "eng123");
        Assert.True(auth.CurrentUser!.HasPermission(Permission.AcknowledgeAlarm));
        Assert.True(auth.CurrentUser.HasPermission(Permission.MotionControl));
        Assert.True(auth.CurrentUser.HasPermission(Permission.VisionGuide));
    }

    [Fact]
    public void HasPermission_WithoutLogin_IsAlwaysFalse()
    {
        var auth = new AuthService(DefaultUsers());

        Assert.False(auth.HasPermission(Permission.AcknowledgeAlarm));
    }

    [Fact]
    public void Login_SupportsSha256HashPassword()
    {
        var auth = new AuthService(
        [
            new AuthUser("hashed", AuthService.HashPrefix + PasswordHasher.Hash("secret-pass"), UserRole.Engineer),
        ]);

        Assert.False(auth.Login("hashed", "wrong").Success);
        Assert.True(auth.Login("hashed", "secret-pass").Success);
    }

    [Fact]
    public void AuthUserConfig_ParsesRoles_AndRejectsUnknowns()
    {
        var user = new AuthUserConfig { Username = "u", Password = "p", Role = "engineer" }.ToUser();
        Assert.Equal(UserRole.Engineer, user.Role);

        Assert.Throws<FormatException>(() =>
            new AuthUserConfig { Username = "u", Password = "p", Role = "Admin" }.ToUser());
    }

    [Fact]
    public void Constructor_ValidatesUsers()
    {
        Assert.Throws<ArgumentException>(() => new AuthService([]));
        Assert.Throws<ArgumentException>(() => new AuthService(
        [
            new AuthUser(" ", "pw", UserRole.Operator),
        ]));
        Assert.Throws<ArgumentException>(() => new AuthService(
        [
            new AuthUser("dup", "pw", UserRole.Operator),
            new AuthUser("dup", "pw2", UserRole.Engineer),
        ]));
    }
}
