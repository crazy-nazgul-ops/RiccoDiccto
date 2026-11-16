using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Design;

namespace Murmur.App.Controls;

/// <summary>
/// A panel. The working face is brushed metal. The transport face is a flat night ground
/// with a few brass points and a brass top edge.
/// </summary>
public sealed class BrushedPanel : Decorator
{
    /// <summary>Corner radius of the panel.</summary>
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<BrushedPanel, double>(nameof(CornerRadius), Tokens.Radius.Panel);

    /// <summary>Night ground and brass edge, instead of the brushed working panel.</summary>
    public static readonly StyledProperty<bool> IsTransportFaceProperty =
        AvaloniaProperty.Register<BrushedPanel, bool>(nameof(IsTransportFace));

    /// <inheritdoc cref="CornerRadiusProperty"/>
    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <inheritdoc cref="IsTransportFaceProperty"/>
    public bool IsTransportFace
    {
        get => GetValue(IsTransportFaceProperty);
        set => SetValue(IsTransportFaceProperty, value);
    }

    static BrushedPanel() => AffectsRender<BrushedPanel>(CornerRadiusProperty, IsTransportFaceProperty);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var shape = new RoundedRect(bounds, CornerRadius);
        var face = IsTransportFace ? Tokens.Brushes.Transport : Tokens.Brushes.Panel;
        var highlight = IsTransportFace ? Tokens.Colors.TransportSeam : Tokens.Colors.PanelHighlight;
        var seamColor = IsTransportFace ? Tokens.Colors.TransportSeam : Tokens.Colors.Seam;
        context.DrawRectangle(face, null, shape);

        using (context.PushClip(bounds))
        {
            if (IsTransportFace) DrawStars(context, bounds);
            else DrawGrain(context, bounds);
        }

        // Top edge catches the light; the whole edge carries a seam.
        var bevel = new Pen(new SolidColorBrush(highlight, IsTransportFace ? 1 : 0.5), Tokens.Border.Bevel);
        context.DrawLine(bevel, bounds.TopLeft, bounds.TopRight);

        var seam = new Pen(new SolidColorBrush(seamColor, IsTransportFace ? 0.65 : 0.35), Tokens.Border.Seam);
        context.DrawRectangle(null, seam, shape);
    }

    /// <summary>Fine horizontal striations — the direction a rolled aluminium sheet is brushed.</summary>
    private static void DrawGrain(DrawingContext context, Rect bounds)
    {
        var light = new SolidColorBrush(Avalonia.Media.Colors.White, Tokens.Material.GrainLight);
        var dark = new SolidColorBrush(Avalonia.Media.Colors.Black, Tokens.Material.GrainDark);
        var alternate = false;

        for (var y = 0.0; y < bounds.Height; y += Tokens.Material.GrainPitch)
        {
            context.FillRectangle(
                alternate ? dark : light,
                new Rect(0, y, bounds.Width, Tokens.Material.GrainPitch / 2));
            alternate = !alternate;
        }
    }

    /// <summary>A few brass points on the night ground. Flat ink, not a glow.</summary>
    private static void DrawStars(DrawingContext context, Rect bounds)
    {
        var ink = new SolidColorBrush(Tokens.Colors.TransportSeam, Tokens.Material.StarOpacity);
        var pitch = Tokens.Material.StarPitch;
        var n = 0;

        for (var y = pitch; y < bounds.Height; y += pitch)
        {
            for (var x = pitch; x < bounds.Width; x += pitch)
            {
                n++;
                if (n % 3 != 0) continue;

                context.FillRectangle(ink, new Rect(x, y, Tokens.Border.Hairline, Tokens.Border.Hairline));
            }
        }
    }
}

/// <summary>
/// A silkscreened panel label: small, uppercase, tightly tracked.
/// </summary>
/// <remarks>
/// The uppercasing happens here rather than at the call site so a label can never be
/// half-styled — the look depends on all three of size, tracking and case.
/// </remarks>
public sealed class Silkscreen : TextBlock
{
    /// <summary>Uses the larger silkscreen size.</summary>
    public static readonly StyledProperty<bool> IsLargeProperty =
        AvaloniaProperty.Register<Silkscreen, bool>(nameof(IsLarge));

    /// <inheritdoc cref="IsLargeProperty"/>
    public bool IsLarge
    {
        get => GetValue(IsLargeProperty);
        set => SetValue(IsLargeProperty, value);
    }

    /// <summary>Creates an empty label.</summary>
    public Silkscreen()
    {
        FontFamily = Tokens.Fonts.Grotesque;
        FontSize = Tokens.Fonts.Silkscreen;
        FontWeight = FontWeight.Medium;
        LetterSpacing = Tokens.Fonts.SilkscreenTracking;
        Foreground = Tokens.Brushes.Silkscreen;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && Text is { } text)
        {
            var upper = text.ToUpperInvariant();
            if (!string.Equals(text, upper, StringComparison.Ordinal)) Text = upper;
        }
        else if (change.Property == IsLargeProperty)
        {
            FontSize = IsLarge ? Tokens.Fonts.SilkscreenLarge : Tokens.Fonts.Silkscreen;
        }
    }
}

/// <summary>
/// An indicator lamp behind a lens.
/// </summary>
/// <remarks>
/// A lit lamp gets a specular dot, not a bloom. The brief rules out glow, and real lamps read
/// as lit because of the highlight on the lens rather than light spilling past it.
/// </remarks>
public sealed class Lamp : Control
{
    /// <summary>Whether the lamp is lit.</summary>
    public static readonly StyledProperty<bool> IsLitProperty =
        AvaloniaProperty.Register<Lamp, bool>(nameof(IsLit));

    /// <summary>The lamp's colour when lit.</summary>
    public static readonly StyledProperty<Color> LampColorProperty =
        AvaloniaProperty.Register<Lamp, Color>(nameof(LampColor), Tokens.Colors.Record);

    /// <inheritdoc cref="IsLitProperty"/>
    public bool IsLit
    {
        get => GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    /// <inheritdoc cref="LampColorProperty"/>
    public Color LampColor
    {
        get => GetValue(LampColorProperty);
        set => SetValue(LampColorProperty, value);
    }

    static Lamp() => AffectsRender<Lamp>(IsLitProperty, LampColorProperty);

    /// <summary>Creates a lamp at the token size.</summary>
    public Lamp()
    {
        Width = Tokens.Material.LampSize;
        Height = Tokens.Material.LampSize;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2;

        var lens = new SolidColorBrush(LampColor, IsLit ? 1 : Tokens.Material.LampUnlitOpacity);
        context.DrawEllipse(lens, null, centre, radius, radius);

        var rim = new Pen(new SolidColorBrush(Tokens.Colors.Seam, 0.7), Tokens.Border.Hairline);
        context.DrawEllipse(null, rim, centre, radius, radius);

        if (!IsLit) return;

        var specular = new SolidColorBrush(Avalonia.Media.Colors.White, Tokens.Material.LampSpecular);
        var dot = size * 0.15;
        context.DrawEllipse(
            specular, null,
            new Point(centre.X - (radius * 0.3), centre.Y - (radius * 0.32)),
            dot, dot);
    }
}

/// <summary>
/// A transport key: rectangular, chunky, with real travel.
/// </summary>
/// <remarks>
/// Pressed means <i>pressed</i> — the cap sinks and its bevel inverts — rather than merely
/// tinted. That distinction is most of what separates equipment from software.
/// </remarks>
public sealed class TransportKey : Button
{
    /// <summary>Whether this key is latched down.</summary>
    public static readonly StyledProperty<bool> IsEngagedProperty =
        AvaloniaProperty.Register<TransportKey, bool>(nameof(IsEngaged));

    /// <summary>Whether this key wears the sand cap and brass rim of the transport strip.</summary>
    public static readonly StyledProperty<bool> IsDesertFaceProperty =
        AvaloniaProperty.Register<TransportKey, bool>(nameof(IsDesertFace));

    /// <summary>Label colour when engaged.</summary>
    public static readonly StyledProperty<Color> EngagedColorProperty =
        AvaloniaProperty.Register<TransportKey, Color>(nameof(EngagedColor), Tokens.Colors.Record);

    /// <inheritdoc cref="IsEngagedProperty"/>
    public bool IsEngaged
    {
        get => GetValue(IsEngagedProperty);
        set => SetValue(IsEngagedProperty, value);
    }

    /// <inheritdoc cref="IsDesertFaceProperty"/>
    public bool IsDesertFace
    {
        get => GetValue(IsDesertFaceProperty);
        set => SetValue(IsDesertFaceProperty, value);
    }

    /// <inheritdoc cref="EngagedColorProperty"/>
    public Color EngagedColor
    {
        get => GetValue(EngagedColorProperty);
        set => SetValue(EngagedColorProperty, value);
    }

    static TransportKey() => AffectsRender<TransportKey>(IsEngagedProperty, IsPressedProperty, IsDesertFaceProperty);

    /// <summary>Creates a key at the token dimensions.</summary>
    public TransportKey()
    {
        MinWidth = Tokens.Material.KeyMinWidth;
        Height = Tokens.Material.KeyHeight;
        Padding = new Thickness(Tokens.Space.Base, 0);
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Background = null;
        BorderBrush = null;
        FontFamily = Tokens.Fonts.Grotesque;
        FontSize = Tokens.Fonts.Silkscreen;
        FontWeight = FontWeight.Medium;
        Foreground = Tokens.Brushes.Ink;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // The cap sinks by the travel distance while held.
        var sunk = IsPressed
            ? bounds.Translate(new Vector(0, Tokens.Material.KeyTravel))
            : bounds;
        var shape = new RoundedRect(sunk, Tokens.Radius.Control);

        context.DrawRectangle(IsDesertFace ? Tokens.Brushes.TransportCap : Tokens.Brushes.Cap, null, shape);

        // Bevel: highlight on top when proud, shade on top when pressed.
        var bevelColor = IsPressed
            ? (IsDesertFace ? Tokens.Colors.TransportShade : Tokens.Colors.PanelShade)
            : (IsDesertFace ? Tokens.Colors.TransportHighlight : Tokens.Colors.PanelHighlight);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(bevelColor), Tokens.Border.Bevel), shape);

        var seamColor = IsDesertFace ? Tokens.Colors.TransportSeam : Tokens.Colors.Seam;
        var seam = new Pen(new SolidColorBrush(seamColor, IsDesertFace ? 1 : 0.5), Tokens.Border.Hairline);
        context.DrawRectangle(null, seam, shape);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Foreground is updated HERE, not in Render. Assigning a property during the render
        // pass invalidates the visual mid-pass, and Avalonia throws "Visual was invalidated
        // during the render pass" rather than merely logging it. Render must be a pure
        // function of current state.
        if (change.Property == IsEngagedProperty
            || change.Property == EngagedColorProperty
            || change.Property == IsDesertFaceProperty)
        {
            var ink = IsDesertFace ? Tokens.Colors.TransportBrass : Tokens.Colors.Ink;
            Foreground = new SolidColorBrush(IsEngaged ? EngagedColor : ink);
        }
    }
}

/// <summary>
/// A level display drawn as a traveling wave.
/// </summary>
/// <remarks>
/// <para>
/// The height of the wave follows the input. Silence is a flat line. Speech lifts it.
/// Loud input crosses into amber at <see cref="Tokens.Material.MeterZeroPoint"/>.
/// </para>
/// <para>
/// The motion lives in plain fields stepped by a timer, deliberately kept out of the property
/// system: a styled property invalidated 60 times a second would push a full layout pass each
/// frame for a value only this control's <c>Render</c> ever reads.
/// </para>
/// </remarks>
public sealed class VuMeter : Control
{
    /// <summary>Current input level, 0…1.</summary>
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<VuMeter, double>(nameof(Level));

    /// <summary>Whether the display is live.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<VuMeter, bool>(nameof(IsActive));

    /// <inheritdoc cref="LevelProperty"/>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <inheritdoc cref="IsActiveProperty"/>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private double _amplitude;
    private double _velocity;
    private double _phase;
    private DispatcherTimer? _ticker;

    static VuMeter() => AffectsRender<VuMeter>(IsActiveProperty);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _ticker.Tick += (_, _) => { Advance(); InvalidateVisual(); };
        _ticker.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ticker?.Stop();
        _ticker = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Steps the wave's height toward the current level, and rolls it forward.</summary>
    private void Advance()
    {
        var target = IsActive ? Math.Clamp(Level, 0, 1) : 0;
        var rising = target > _amplitude;
        var time = rising ? Tokens.Motion.NeedleAttackSeconds : Tokens.Motion.NeedleReleaseSeconds;

        var stiffness = 1 / time;
        _velocity += (target - _amplitude) * stiffness * 0.16;
        _velocity *= 0.72;
        _amplitude = Math.Clamp(_amplitude + _velocity, 0, 1 + Tokens.Motion.NeedleOvershoot);
        _phase += Tokens.Material.WaveSpeed;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var shape = new RoundedRect(bounds, Tokens.Radius.Chip);
        context.DrawRectangle(Tokens.Brushes.TransportDeck, null, shape);

        var mid = bounds.Top + (bounds.Height / 2);
        var baseline = new Pen(new SolidColorBrush(Tokens.Colors.TransportSeam), Tokens.Border.Hairline);
        context.DrawLine(baseline, new Point(bounds.Left, mid), new Point(bounds.Right, mid));

        var reach = _amplitude * ((bounds.Height / 2) - Tokens.Space.Tight);
        if (reach >= Tokens.Border.Hairline)
        {
            var loud = _amplitude >= Tokens.Material.MeterZeroPoint;
            var ink = loud ? Tokens.Colors.MeterAmber : Tokens.Colors.MeterGreen;
            var trace = new Pen(new SolidColorBrush(ink), Tokens.Material.NeedleWidth);
            var cycles = Tokens.Material.WaveCycles * Math.PI * 2;
            Point? previous = null;

            for (var x = bounds.Left; x <= bounds.Right; x += Tokens.Space.Hair)
            {
                var along = (x - bounds.Left) / bounds.Width;
                var y = mid - (Math.Sin((along * cycles) + _phase) * reach);
                var point = new Point(x, y);
                if (previous is Point from) context.DrawLine(trace, from, point);
                previous = point;
            }
        }

        var frame = new Pen(new SolidColorBrush(Tokens.Colors.TransportSeam), Tokens.Border.Hairline);
        context.DrawRectangle(null, frame, shape);
    }
}

/// <summary>A run of small arches, the vent pattern on the transport strip.</summary>
public sealed class Vents : Control
{
    /// <summary>How many slots to draw.</summary>
    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<Vents, int>(nameof(Count), 6);

    /// <inheritdoc cref="CountProperty"/>
    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    static Vents() => AffectsRender<Vents>(CountProperty);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(
        (Count * Tokens.Material.VentSlotWidth) + ((Count - 1) * Tokens.Material.VentSlotGap),
        Tokens.Material.VentSlotHeight);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var brush = new SolidColorBrush(Tokens.Colors.TransportSeam);
        var pitch = Tokens.Material.VentSlotWidth + Tokens.Material.VentSlotGap;
        var width = Tokens.Material.VentSlotWidth;
        var rise = width / 2;

        for (var i = 0; i < Count; i++)
        {
            var x = i * pitch;
            var crown = new Point(x + rise, rise);
            context.DrawEllipse(brush, null, crown, rise, rise);
            context.FillRectangle(
                brush,
                new Rect(x, rise, width, Tokens.Material.VentSlotHeight - rise));
        }
    }
}

/// <summary>
/// The counter window: a night field under a single arch, edged in brass.
/// </summary>
public sealed class ArchedReadout : Decorator
{
    /// <summary>Creates the window with the digits set below the arch.</summary>
    public ArchedReadout()
    {
        Padding = new Thickness(Tokens.Space.Base, Tokens.Material.ArchRise, Tokens.Space.Base, Tokens.Space.Snug);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var pen = new Pen(new SolidColorBrush(Tokens.Colors.TransportSeam), Tokens.Border.Hairline);
        context.DrawGeometry(Tokens.Brushes.TransportDeck, pen, Arch(bounds));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var pad = Padding;
        var child = Child;
        if (child is null) return new Size(pad.Left + pad.Right, pad.Top + pad.Bottom);

        child.Measure(Deflate(availableSize, pad));
        return new Size(
            child.DesiredSize.Width + pad.Left + pad.Right,
            child.DesiredSize.Height + pad.Top + pad.Bottom);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var pad = Padding;
        Child?.Arrange(new Rect(
            pad.Left,
            pad.Top,
            Math.Max(0, finalSize.Width - pad.Left - pad.Right),
            Math.Max(0, finalSize.Height - pad.Top - pad.Bottom)));

        Clip = Arch(new Rect(finalSize));
        return finalSize;
    }

    private static StreamGeometry Arch(Rect bounds)
    {
        var rise = Math.Min(Tokens.Material.ArchRise, bounds.Height / 2);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(bounds.Left, bounds.Top + rise), true);
            figure.ArcTo(
                new Point(bounds.Right, bounds.Top + rise),
                new Size(bounds.Width / 2, rise),
                0,
                false,
                SweepDirection.Clockwise);
            figure.LineTo(bounds.BottomRight);
            figure.LineTo(bounds.BottomLeft);
            figure.EndFigure(true);
        }

        return geometry;
    }

    private static Size Deflate(Size available, Thickness pad) => new(
        Math.Max(0, available.Width - pad.Left - pad.Right),
        Math.Max(0, available.Height - pad.Top - pad.Bottom));
}
