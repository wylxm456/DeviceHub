using CommunityToolkit.Mvvm.ComponentModel;
using DeviceHub.Core.Models;

namespace DeviceHub.App.ViewModels;

/// <summary>表格里的一行：一个点位的实时状态。</summary>
public partial class PointRow : ObservableObject
{
    public string Name { get; }
    public string Address { get; }

    [ObservableProperty]
    private double? _value;

    [ObservableProperty]
    private string _quality = "-";

    [ObservableProperty]
    private string? _updateTime;

    public PointRow(string name, string address)
    {
        Name = name;
        Address = address;
    }

    public void Update(double? value, PointQuality quality, DateTime timestamp)
    {
        // 行模型是纯显示层：数值统一收敛到 3 位小数，S7 Real 的原始精度不进界面
        Value = value is null ? null : Math.Round(value.Value, 3);
        Quality = quality.ToString();
        UpdateTime = timestamp.ToString("HH:mm:ss.fff");
    }
}
