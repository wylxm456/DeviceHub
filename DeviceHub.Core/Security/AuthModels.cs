namespace DeviceHub.Core.Security;

/// <summary>用户角色。数值即权限等级：Engineer 覆盖 Operator 的一切能力。</summary>
public enum UserRole
{
    Operator,
    Engineer,
}

/// <summary>
/// 受权限保护的操作能力。门禁原则：
/// 安全操作（急停/停止）不做权限门禁——任何时刻任何人都必须能停；
/// 被门禁的是"可能造成后果"的操作（动轴、标定、引导）。
/// </summary>
public enum Permission
{
    /// <summary>报警确认。</summary>
    AcknowledgeAlarm,

    /// <summary>运动操作：回零/Jog/定位（急停与"停止"除外）。</summary>
    MotionControl,

    /// <summary>视觉操作：连接相机/单帧定位/九点标定/视觉引导。</summary>
    VisionGuide,
}

/// <summary>角色 → 能力集的映射。权限判定的唯一出处——界面上不散落"角色 == 工程师"这类硬编码。</summary>
public static class RolePermissions
{
    public static IReadOnlySet<Permission> Of(UserRole role) => role switch
    {
        UserRole.Engineer => new HashSet<Permission>
        {
            Permission.AcknowledgeAlarm,
            Permission.MotionControl,
            Permission.VisionGuide,
        },
        UserRole.Operator => new HashSet<Permission>
        {
            Permission.AcknowledgeAlarm,
        },
        _ => new HashSet<Permission>(),
    };
}

/// <summary>登录成功的身份快照（会话内不可变）。</summary>
/// <param name="Username">用户名。</param>
/// <param name="Role">角色。</param>
public sealed record UserIdentity(string Username, UserRole Role)
{
    public bool HasPermission(Permission permission) => RolePermissions.Of(Role).Contains(permission);
}

/// <summary>一条账号（来自配置）。</summary>
/// <param name="Username">用户名。</param>
/// <param name="Password">明文，或 "sha256:十六进制" 哈希（AuthService 按 前缀 自动识别）。</param>
/// <param name="Role">角色。</param>
public sealed record AuthUser(string Username, string Password, UserRole Role);
