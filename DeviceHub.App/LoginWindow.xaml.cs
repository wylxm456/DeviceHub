using System.Windows;
using System.Windows.Input;
using DeviceHub.Core.Security;

namespace DeviceHub.App;

/// <summary>
/// 登录窗口：启动时以模态出现（登录成功才进主界面），"锁定/切换用户"时再次出现。
/// 登录失败不弹窗打断，错误就地显示；关闭窗口 = 放弃登录（启动场景下应用直接退出）。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;

    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var result = _auth.Login(UsernameBox.Text, PasswordBox.Password);
        if (result.Success)
        {
            DialogResult = true;
            return;
        }

        // 统一错误信息由 AuthService 给出（防用户枚举），这里只负责展示
        ErrorText.Text = result.Error;
        PasswordBox.Clear();
        PasswordBox.Focus();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Login_Click(sender, e);
        }
    }
}
