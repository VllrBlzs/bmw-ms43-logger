using System.Diagnostics;
using System.Drawing.Drawing2D;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.App;

internal sealed class LivePlotControl : Control
{
    private static readonly Color[] SeriesColors =
    [
        Color.FromArgb(31, 119, 180),
        Color.FromArgb(214, 39, 40),
        Color.FromArgb(44, 160, 44),
        Color.FromArgb(148, 103, 189),
        Color.FromArgb(255, 127, 14)
    ];

    private LivePlotSnapshot _snapshot = new(
        null,
        null,
        Array.Empty<LivePlotSeries>(),
        0,
        0,
        LivePlotHistory.DefaultTimeWindow,
        LivePlotHistory.DefaultMaximumPointsPerSeries);

    public long RenderedFrames { get; private set; }
    public double LastRenderMilliseconds { get; private set; }
    public double MaximumRenderMilliseconds { get; private set; }

    public LivePlotControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(35, 35, 35);
        MinimumSize = new Size(400, 180);
    }

    public void SetSnapshot(LivePlotSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        long renderStarted = Stopwatch.GetTimestamp();
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        LivePlotSnapshot snapshot = _snapshot;
        if (snapshot.Series.Count == 0)
        {
            DrawCentredMessage(
                graphics,
                "No plot channels selected.",
                Color.DimGray);
            RecordRender(renderStarted);
            return;
        }

        LivePlotPoint[] allPoints = snapshot.Series
            .SelectMany(series => series.Points)
            .ToArray();
        if (allPoints.Length == 0)
        {
            DrawCentredMessage(
                graphics,
                "Waiting for normalized acquisition samples…",
                Color.DimGray);
            RecordRender(renderStarted);
            return;
        }

        DateTimeOffset minimumTime = allPoints.Min(point => point.PlotTimeUtc);
        DateTimeOffset maximumTime = allPoints.Max(point => point.PlotTimeUtc);
        if (maximumTime <= minimumTime)
        {
            maximumTime = minimumTime.AddSeconds(1);
        }

        const float outerPadding = 8F;
        const float labelWidth = 210F;
        const float valueScaleWidth = 70F;
        const float bottomAxisHeight = 23F;
        float usableHeight = Math.Max(
            1F,
            ClientSize.Height - outerPadding * 2F - bottomAxisHeight);
        float laneHeight = usableHeight / snapshot.Series.Count;

        for (int index = 0; index < snapshot.Series.Count; index++)
        {
            LivePlotSeries series = snapshot.Series[index];
            float top = outerPadding + laneHeight * index;
            var lane = new RectangleF(
                outerPadding,
                top,
                Math.Max(1F, ClientSize.Width - outerPadding * 2F),
                Math.Max(1F, laneHeight));
            var plot = new RectangleF(
                lane.Left + labelWidth,
                lane.Top + 5F,
                Math.Max(1F, lane.Width - labelWidth - valueScaleWidth),
                Math.Max(1F, lane.Height - 10F));
            DrawSeriesLane(
                graphics,
                series,
                index,
                lane,
                plot,
                minimumTime,
                maximumTime);
        }

        DrawTimeAxis(
            graphics,
            minimumTime,
            maximumTime,
            outerPadding + labelWidth,
            ClientSize.Width - outerPadding - valueScaleWidth,
            ClientSize.Height - bottomAxisHeight + 2F);
        RecordRender(renderStarted);
    }

    private void DrawSeriesLane(
        Graphics graphics,
        LivePlotSeries series,
        int seriesIndex,
        RectangleF lane,
        RectangleF plot,
        DateTimeOffset minimumTime,
        DateTimeOffset maximumTime)
    {
        LoggerParameterDefinition definition =
            Ms43ParameterCatalog.Resolve(series.ParameterId);
        Color seriesColor = SeriesColors[seriesIndex % SeriesColors.Length];

        using var borderPen = new Pen(Color.FromArgb(220, 220, 220), 1F);
        using var gridPen = new Pen(Color.FromArgb(235, 235, 235), 1F)
        {
            DashStyle = DashStyle.Dash
        };
        graphics.DrawLine(borderPen, lane.Left, lane.Bottom, lane.Right, lane.Bottom);
        graphics.DrawRectangle(
            borderPen,
            plot.X,
            plot.Y,
            plot.Width,
            plot.Height);
        graphics.DrawLine(
            gridPen,
            plot.Left,
            plot.Top + plot.Height / 2F,
            plot.Right,
            plot.Top + plot.Height / 2F);

        string title = $"{definition.DisplayNameHu} [{definition.Unit}]";
        using var titleBrush = new SolidBrush(seriesColor);
        using var detailBrush = new SolidBrush(Color.DimGray);
        graphics.DrawString(
            title,
            Font,
            titleBrush,
            new RectangleF(lane.Left + 4F, lane.Top + 8F, 198F, 22F));
        graphics.DrawString(
            LatestText(definition, series.Points),
            Font,
            detailBrush,
            new RectangleF(lane.Left + 4F, lane.Top + 31F, 198F, 38F));

        LivePlotPoint[] valid = series.Points
            .Where(point =>
                point.Quality == SampleQuality.Valid && point.Value.HasValue)
            .ToArray();
        if (valid.Length == 0)
        {
            graphics.DrawString(
                "No valid samples",
                Font,
                detailBrush,
                plot.Left + 8F,
                plot.Top + 8F);
            DrawGapMarkers(
                graphics,
                series.Points,
                plot,
                minimumTime,
                maximumTime);
            return;
        }

        double minimumValue = valid.Min(point => point.Value!.Value);
        double maximumValue = valid.Max(point => point.Value!.Value);
        if (maximumValue <= minimumValue)
        {
            double padding = Math.Max(1.0, Math.Abs(minimumValue) * 0.02);
            minimumValue -= padding;
            maximumValue += padding;
        }
        else
        {
            double padding = (maximumValue - minimumValue) * 0.08;
            minimumValue -= padding;
            maximumValue += padding;
        }

        IReadOnlyList<IReadOnlyList<LivePlotPoint>> segments =
            LivePlotSegmenter.CreateValidSegments(series.Points);
        using var seriesPen = new Pen(seriesColor, 1.8F)
        {
            LineJoin = LineJoin.Round
        };
        using var pointBrush = new SolidBrush(seriesColor);
        foreach (IReadOnlyList<LivePlotPoint> segment in segments)
        {
            PointF[] coordinates = segment
                .Select(point => MapPoint(
                    point,
                    plot,
                    minimumTime,
                    maximumTime,
                    minimumValue,
                    maximumValue))
                .ToArray();
            if (coordinates.Length >= 2)
            {
                graphics.DrawLines(seriesPen, coordinates);
            }
            else if (coordinates.Length == 1)
            {
                PointF point = coordinates[0];
                graphics.FillEllipse(pointBrush, point.X - 2.5F, point.Y - 2.5F, 5F, 5F);
            }
        }
        DrawGapMarkers(
            graphics,
            series.Points,
            plot,
            minimumTime,
            maximumTime);

        graphics.DrawString(
            maximumValue.ToString("G5"),
            Font,
            detailBrush,
            plot.Right + 5F,
            plot.Top - 1F);
        graphics.DrawString(
            minimumValue.ToString("G5"),
            Font,
            detailBrush,
            plot.Right + 5F,
            plot.Bottom - Font.Height);
    }

    private void DrawGapMarkers(
        Graphics graphics,
        IReadOnlyList<LivePlotPoint> points,
        RectangleF plot,
        DateTimeOffset minimumTime,
        DateTimeOffset maximumTime)
    {
        foreach (LivePlotPoint point in points)
        {
            if (point.Quality == SampleQuality.Valid && point.Value.HasValue)
            {
                continue;
            }
            float x = MapX(point.PlotTimeUtc, plot, minimumTime, maximumTime);
            Color color = point.Quality switch
            {
                SampleQuality.Stale => Color.DarkOrange,
                SampleQuality.Missing => Color.DarkGoldenrod,
                SampleQuality.Timeout => Color.OrangeRed,
                SampleQuality.Error => Color.DarkRed,
                _ => Color.Gray
            };
            using var gapPen = new Pen(color, 1.5F);
            float y = plot.Top + plot.Height / 2F;
            graphics.DrawLine(gapPen, x - 3F, y - 3F, x + 3F, y + 3F);
            graphics.DrawLine(gapPen, x - 3F, y + 3F, x + 3F, y - 3F);
        }
    }

    private void DrawTimeAxis(
        Graphics graphics,
        DateTimeOffset minimumTime,
        DateTimeOffset maximumTime,
        float left,
        float right,
        float top)
    {
        using var brush = new SolidBrush(Color.DimGray);
        string start = minimumTime.UtcDateTime.ToString("HH:mm:ss.fff 'UTC'");
        string end = maximumTime.UtcDateTime.ToString("HH:mm:ss.fff 'UTC'");
        graphics.DrawString(start, Font, brush, left, top);
        SizeF endSize = graphics.MeasureString(end, Font);
        graphics.DrawString(end, Font, brush, right - endSize.Width, top);
    }

    private void DrawCentredMessage(
        Graphics graphics,
        string message,
        Color color)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(message, Font, brush, ClientRectangle, format);
    }

    private static PointF MapPoint(
        LivePlotPoint point,
        RectangleF plot,
        DateTimeOffset minimumTime,
        DateTimeOffset maximumTime,
        double minimumValue,
        double maximumValue)
    {
        float x = MapX(point.PlotTimeUtc, plot, minimumTime, maximumTime);
        double ratio = (point.Value!.Value - minimumValue) /
            (maximumValue - minimumValue);
        float y = plot.Bottom - (float)ratio * plot.Height;
        return new PointF(x, y);
    }

    private static float MapX(
        DateTimeOffset time,
        RectangleF plot,
        DateTimeOffset minimumTime,
        DateTimeOffset maximumTime)
    {
        double total = (maximumTime - minimumTime).TotalMilliseconds;
        double offset = (time - minimumTime).TotalMilliseconds;
        double ratio = total <= 0.0 ? 0.0 : Math.Clamp(offset / total, 0.0, 1.0);
        return plot.Left + (float)ratio * plot.Width;
    }

    private static string LatestText(
        LoggerParameterDefinition definition,
        IReadOnlyList<LivePlotPoint> points)
    {
        LivePlotPoint? latest = points.LastOrDefault();
        if (latest is null)
        {
            return "waiting";
        }
        if (latest.Quality != SampleQuality.Valid || !latest.Value.HasValue)
        {
            return latest.ErrorCode is null
                ? latest.Quality.ToString().ToLowerInvariant()
                : $"{latest.Quality.ToString().ToLowerInvariant()}: {latest.ErrorCode}";
        }
        return LiveValuePresenter.TryFormatValue(
            definition,
            latest.Value.Value,
            out string formatted)
            ? $"latest: {formatted} {definition.Unit}"
            : "latest: format error";
    }

    private void RecordRender(long startedTimestamp)
    {
        double elapsed = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        RenderedFrames++;
        LastRenderMilliseconds = elapsed;
        MaximumRenderMilliseconds = Math.Max(MaximumRenderMilliseconds, elapsed);
    }
}
