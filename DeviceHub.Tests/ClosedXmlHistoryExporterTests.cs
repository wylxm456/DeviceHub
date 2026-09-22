using ClosedXML.Excel;
using DeviceHub.Core.History;
using DeviceHub.Core.Models;
using DeviceHub.Storage.History;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Excel 导出测试：导出到临时文件，再用 ClosedXML 读回来断言——
/// 报表代码最怕"文件能生成但打开是空的/错的"，读回验证才能兜住。
/// </summary>
public class ClosedXmlHistoryExporterTests : IDisposable
{
    private readonly string _xlsxPath =
        Path.Combine(Path.GetTempPath(), $"devicehub-export-{Guid.NewGuid():N}.xlsx");

    private static readonly DateTime T0 = new(2026, 9, 22, 10, 0, 0);

    private readonly ClosedXmlHistoryExporter _exporter = new();

    public void Dispose()
    {
        try
        {
            File.Delete(_xlsxPath);
        }
        catch
        {
            // 临时文件删不掉不影响测试结论
        }
    }

    [Fact]
    public async Task PointHistory_Export_ReadBack_KeepsValuesNullsAndQuality()
    {
        var records = new List<PointHistoryRecord>
        {
            new("温度", 45.5, PointQuality.Good, T0),
            new("温度", null, PointQuality.Bad, T0.AddSeconds(1)),
            new("压力", 0.42, PointQuality.Good, T0.AddSeconds(2)),
        };

        await _exporter.ExportPointHistoryAsync(records, _xlsxPath);

        using var workbook = new XLWorkbook(_xlsxPath);
        var sheet = workbook.Worksheet("点位历史");
        Assert.Equal("时间", sheet.Cell(1, 1).GetString());
        Assert.Equal("点位", sheet.Cell(1, 2).GetString());

        Assert.Equal(45.5, sheet.Cell(2, 3).GetDouble());
        Assert.Equal("Good", sheet.Cell(2, 4).GetString());
        Assert.Equal(T0, sheet.Cell(2, 1).GetDateTime());

        // Bad 质量的数值是空单元格——报表同样不许伪造数据
        Assert.True(sheet.Cell(3, 3).IsEmpty());
        Assert.Equal("Bad", sheet.Cell(3, 4).GetString());
        Assert.Equal("压力", sheet.Cell(4, 2).GetString());
    }

    [Fact]
    public async Task AlarmEvents_Export_IncludesLifecycleFields()
    {
        var events = new List<AlarmEventRecord>
        {
            new(1, "温度", "HighLimit", "Critical", "Raised", 46.2, "高限报警：46.2 ≥ 阈值 45",
                T0, T0, null),
            new(1, "温度", "HighLimit", "Critical", "Cleared", 46.2, "高限报警：46.2 ≥ 阈值 45",
                T0.AddMinutes(3), T0, T0.AddMinutes(3)),
        };

        await _exporter.ExportAlarmEventsAsync(events, _xlsxPath);

        using var workbook = new XLWorkbook(_xlsxPath);
        var sheet = workbook.Worksheet("报警事件");
        Assert.Equal("事件时间", sheet.Cell(1, 1).GetString());
        Assert.Equal("Raised", sheet.Cell(2, 6).GetString());
        Assert.True(sheet.Cell(2, 10).IsEmpty()); // 未恢复：恢复时刻空
        Assert.Equal(T0.AddMinutes(3), sheet.Cell(3, 10).GetDateTime());
    }

    [Fact]
    public async Task EmptyList_ProducesHeaderOnly_File()
    {
        await _exporter.ExportAlarmEventsAsync([], _xlsxPath);

        using var workbook = new XLWorkbook(_xlsxPath);
        var sheet = workbook.Worksheet("报警事件");
        Assert.Equal("事件时间", sheet.Cell(1, 1).GetString());
        Assert.True(sheet.Cell(2, 1).IsEmpty());
    }

    [Fact]
    public async Task Export_OverwritesExistingFile()
    {
        await _exporter.ExportPointHistoryAsync(
            [new PointHistoryRecord("温度", 1, PointQuality.Good, T0)], _xlsxPath);
        await _exporter.ExportPointHistoryAsync(
            [new PointHistoryRecord("温度", 2, PointQuality.Good, T0)], _xlsxPath);

        using var workbook = new XLWorkbook(_xlsxPath);
        Assert.Equal(2, workbook.Worksheet("点位历史").Cell(2, 3).GetDouble());
    }
}
