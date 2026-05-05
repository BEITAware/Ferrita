using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Skyweaver;

public sealed class VfdClockControl : FrameworkElement
{
    private const double DesiredWidth = 420;
    private const double DesiredHeight = 56;
    private const double SidePadding = 12;
    private const double TopPadding = 8;
    private const double DigitWidth = 18;
    private const double DigitHeight = 42;
    private const double SegmentThickness = 4.2;
    private const double DigitGap = 4;
    private const double GroupGap = 12;
    private const double BottomPadding = 6;
    private const double BaseHeight = TopPadding + DigitHeight + BottomPadding;
    private static readonly int[] SegmentMasks =
    [
        0b0111111,
        0b0000110,
        0b1011011,
        0b1001111,
        0b1100110,
        0b1101101,
        0b1111101,
        0b0000111,
        0b1111111,
        0b1101111
    ];

    private static readonly Brush ActiveBrush = CreateBrush(255, 118, 255, 240);
    private static readonly Brush GlowBrush = CreateBrush(76, 91, 255, 238);
    private static readonly Brush InactiveBrush = CreateBrush(30, 80, 255, 234);
    private static readonly Brush HighlightBrush = CreateBrush(24, 118, 255, 240);
    private static readonly Pen GlassBorderPen = CreatePen(80, 112, 255, 244, 1);
    private readonly DispatcherTimer _timer;
    private string _displayText = string.Empty;

    public VfdClockControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => RefreshClock();

        Loaded += (_, _) =>
        {
            RefreshClock();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(DesiredWidth, DesiredHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        DrawGlass(drawingContext, ActualWidth, ActualHeight);

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var baseWidth = CalculateBaseWidth();
        var scale = Math.Min(ActualWidth / baseWidth, ActualHeight / BaseHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        var top = Math.Max(0, (ActualHeight - (BaseHeight * scale)) / 2);
        var digitTop = top + (TopPadding * scale);
        var x = SidePadding * scale;
        var groups = _displayText.Split(' ');

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];

            for (var digitIndex = 0; digitIndex < group.Length; digitIndex++)
            {
                DrawDigit(drawingContext, group[digitIndex], x, digitTop, scale);
                x += (DigitWidth + DigitGap) * scale;
            }

            if (group.Length > 0)
            {
                x -= DigitGap * scale;
            }

            x += GroupGap * scale;
        }
    }

    private static double CalculateBaseWidth()
    {
        var width = SidePadding * 2;
        width += (4 * DigitWidth) + (3 * DigitGap);
        width += 5 * ((2 * DigitWidth) + DigitGap);
        width += 5 * GroupGap;
        return width;
    }

    private static void DrawGlass(DrawingContext drawingContext, double width, double height)
    {
        var bounds = new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));
        var background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(7, 20, 29), 0),
                new GradientStop(Color.FromRgb(1, 8, 12), 0.55),
                new GradientStop(Color.FromRgb(9, 30, 36), 1)
            ]
        };

        drawingContext.DrawRectangle(background, GlassBorderPen, bounds);
        drawingContext.DrawRectangle(HighlightBrush, null, new Rect(8, 6, Math.Max(0, width - 16), 1));
    }

    private static void DrawDigit(DrawingContext drawingContext, char digit, double x, double y, double scale)
    {
        if (!char.IsDigit(digit))
        {
            return;
        }

        var mask = SegmentMasks[digit - '0'];
        var width = DigitWidth * scale;
        var height = DigitHeight * scale;
        var thickness = SegmentThickness * scale;
        var verticalHeight = (height - (3 * thickness)) / 2;

        var segments = new[]
        {
            new Rect(x + thickness, y, width - (2 * thickness), thickness),
            new Rect(x + width - thickness, y + thickness, thickness, verticalHeight),
            new Rect(x + width - thickness, y + (2 * thickness) + verticalHeight, thickness, verticalHeight),
            new Rect(x + thickness, y + height - thickness, width - (2 * thickness), thickness),
            new Rect(x, y + (2 * thickness) + verticalHeight, thickness, verticalHeight),
            new Rect(x, y + thickness, thickness, verticalHeight),
            new Rect(x + thickness, y + thickness + verticalHeight, width - (2 * thickness), thickness)
        };

        for (var index = 0; index < segments.Length; index++)
        {
            DrawSegment(drawingContext, segments[index], (mask & (1 << index)) != 0, scale);
        }
    }

    private static void DrawSegment(DrawingContext drawingContext, Rect segment, bool isActive, double scale)
    {
        if (!isActive)
        {
            drawingContext.DrawRectangle(InactiveBrush, null, segment);
            return;
        }

        var glow = segment;
        glow.Inflate(2.2 * scale, 2.2 * scale);
        drawingContext.DrawRectangle(GlowBrush, null, glow);
        drawingContext.DrawRectangle(ActiveBrush, null, segment);
    }

    private void RefreshClock()
    {
        var nextText = DateTime.Now.ToString("yyyy MM dd HH mm ss", CultureInfo.InvariantCulture);
        if (nextText == _displayText)
        {
            return;
        }

        _displayText = nextText;
        InvalidateVisual();
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte alpha, byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(CreateBrush(alpha, red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }
}
