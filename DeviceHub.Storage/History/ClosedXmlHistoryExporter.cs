using ClosedXML.Excel;
using DeviceHub.Core.History;

namespace DeviceHub.Storage.History;

/// <summary>
/// 历史数据 Excel 导出（ClosedXML，纯托管实现、无需装 Office）。
///
/// 报表约定：
/// - 首行中文表头 + 加粗灰底，数据从第二行起，按时间从旧到新；
/// - 列宽自适应（只按前 200 行计算——全量自适应在几万行时明显变慢，得不偿失）；
/// - 时间列固定 "yyyy-mm-dd hh:mm:ss" 显示格式，Excel 里可直接筛选排序；
/// - 空值（Bad 质量的数值、未恢复报警的恢复时刻）写空单元格，不写 0——
///   导出报表同样不许伪造数据。
/// </summary>
public sealed class ClosedXmlHistoryExporter : IHistoryExporter
{
    private const int AdaptiveWidthSampleRows = 200;

    public Task ExportPointHistoryAsync(
        IReadOnlyList<PointHistoryRecord> records, string filePath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("点位历史");

            sheet.Cell(1, 1).Value = "时间";
            sheet.Cell(1, 2).Value = "点位";
            sheet.Cell(1, 3).Value = "数值";
            sheet.Cell(1, 4).Value = "质量";
            StyleHeader(sheet.Row(1), width: 4);

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var row = i + 2;
                sheet.Cell(row, 1).Value = record.Timestamp;
                sheet.Cell(row, 2).Value = record.PointName;
                if (record.Value is { } value)
                {
                    sheet.Cell(row, 3).Value = value;
                }

                sheet.Cell(row, 4).Value = record.Quality.ToString();
            }

            sheet.Column(1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            AdjustColumns(sheet, width: 4);
            workbook.SaveAs(filePath);
        }, cancellationToken);

    public Task ExportAlarmEventsAsync(
        IReadOnlyList<AlarmEventRecord> events, string filePath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("报警事件");

            var headers = new[] { "事件时间", "报警号", "点位", "条件", "级别", "事件", "极值", "描述", "触发时刻", "恢复时刻" };
            for (var c = 0; c < headers.Length; c++)
            {
                sheet.Cell(1, c + 1).Value = headers[c];
            }

            StyleHeader(sheet.Row(1), width: headers.Length);

            for (var i = 0; i < events.Count; i++)
            {
                var record = events[i];
                var row = i + 2;
                sheet.Cell(row, 1).Value = record.EventTime;
                sheet.Cell(row, 2).Value = record.AlarmId;
                sheet.Cell(row, 3).Value = record.PointName;
                sheet.Cell(row, 4).Value = record.Condition;
                sheet.Cell(row, 5).Value = record.Level;
                sheet.Cell(row, 6).Value = record.Event;
                if (record.PeakValue is { } peak)
                {
                    sheet.Cell(row, 7).Value = peak;
                }

                sheet.Cell(row, 8).Value = record.Description;
                sheet.Cell(row, 9).Value = record.RaisedAt;
                if (record.ClearedAt is { } clearedAt)
                {
                    sheet.Cell(row, 10).Value = clearedAt;
                }
            }

            sheet.Column(1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            sheet.Column(9).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            sheet.Column(10).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            AdjustColumns(sheet, width: headers.Length);
            workbook.SaveAs(filePath);
        }, cancellationToken);

    private static void StyleHeader(IXLRow header, int width)
    {
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F0F0");
        header.Cells(1, width).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    private static void AdjustColumns(IXLWorksheet sheet, int width)
    {
        var sampleRows = Math.Min(sheet.LastRowUsed().RowNumber(), AdaptiveWidthSampleRows);
        sheet.Columns(1, width).AdjustToContents(1, sampleRows);
    }
}
