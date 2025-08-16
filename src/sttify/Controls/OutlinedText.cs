using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using MediaSystemFonts = System.Windows.SystemFonts;
using MediaPoint = System.Windows.Point;
using MediaPen = System.Windows.Media.Pen;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaFontFamily = System.Windows.Media.FontFamily;
using MediaSize = System.Windows.Size;

namespace Sttify.Controls;

public class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedText),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(MediaSystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(MediaSystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(MediaBrush), typeof(OutlinedText),
            new FrameworkPropertyMetadata(MediaBrushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(MediaBrush), typeof(OutlinedText),
            new FrameworkPropertyMetadata(MediaBrushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(OutlinedText),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedText),
            new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TextWrappingProperty =
        DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedText),
            new FrameworkPropertyMetadata(TextWrapping.Wrap, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MediaBrush Fill
    {
        get => (MediaBrush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public MediaBrush Stroke
    {
        get => (MediaBrush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public MediaFontFamily FontFamily
    {
        get => (MediaFontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    protected override MediaSize MeasureOverride(MediaSize availableSize)
    {
        var formatted = CreateFormattedText(Text, availableSize.Width);
        return new MediaSize(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace), Math.Ceiling(formatted.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var formatted = CreateFormattedText(Text, ActualWidth > 0 ? ActualWidth : double.PositiveInfinity);
        var geometry = formatted.BuildGeometry(new MediaPoint(0, 0));

        if (StrokeThickness > 0)
        {
            var pen = new MediaPen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
            drawingContext.DrawGeometry(null, pen, geometry);
        }
        drawingContext.DrawGeometry(Fill, null, geometry);
    }

    private FormattedText CreateFormattedText(string text, double constraintWidth)
    {
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var formatted = new FormattedText(
            text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Fill,
            null,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        formatted.TextAlignment = TextAlignment;
        if (!double.IsInfinity(constraintWidth))
        {
            formatted.MaxTextWidth = Math.Max(0, constraintWidth);
        }
        return formatted;
    }
}


