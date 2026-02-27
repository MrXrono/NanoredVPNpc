using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Desktop.Controls;

public partial class StatusIndicator : UserControl
{
    /// <summary>
    /// Defines the <see cref="Status"/> styled property.
    /// Controls the color of the status dot based on the current connection state.
    /// </summary>
    public static readonly StyledProperty<ConnectionStatus> StatusProperty =
        AvaloniaProperty.Register<StatusIndicator, ConnectionStatus>(
            nameof(Status),
            defaultValue: ConnectionStatus.Disconnected);

    /// <summary>
    /// Gets or sets the current connection status displayed by this indicator.
    /// </summary>
    public ConnectionStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private Animation? _pulseAnimation;

    public StatusIndicator()
    {
        InitializeComponent();
        CreatePulseAnimation();
    }

    static StatusIndicator()
    {
        StatusProperty.Changed.AddClassHandler<StatusIndicator>(OnStatusChanged);
    }

    private void CreatePulseAnimation()
    {
        _pulseAnimation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(900),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters = { new Setter(OpacityProperty, 1.0) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters = { new Setter(OpacityProperty, 0.3) }
                }
            }
        };
    }

    private static void OnStatusChanged(StatusIndicator sender, AvaloniaPropertyChangedEventArgs e)
    {
        sender.UpdateIndicator((ConnectionStatus)e.NewValue!);
    }

    private void UpdateIndicator(ConnectionStatus status)
    {
        var dot = this.FindControl<Ellipse>("StatusDot");
        if (dot == null) return;

        // Stop any running animation
        dot.Opacity = 1.0;

        IBrush fill;
        bool animate = false;

        switch (status)
        {
            case ConnectionStatus.Connected:
                fill = GetBrushResource("GreenBrush") ?? new SolidColorBrush(Color.Parse("#4ADE80"));
                break;

            case ConnectionStatus.Connecting:
            case ConnectionStatus.Reconnecting:
                fill = new SolidColorBrush(Color.Parse("#FACC15")); // Yellow
                animate = true;
                break;

            case ConnectionStatus.Error:
                fill = new SolidColorBrush(Color.Parse("#EF4444")); // Red
                break;

            case ConnectionStatus.Disconnected:
            default:
                fill = GetBrushResource("TextMutedBrush") ?? new SolidColorBrush(Color.Parse("#6B7280"));
                break;
        }

        dot.Fill = fill;

        if (animate && _pulseAnimation != null)
        {
            _pulseAnimation.RunAsync(dot);
        }
    }

    private IBrush? GetBrushResource(string key)
    {
        if (this.TryFindResource(key, this.ActualThemeVariant, out var resource) && resource is IBrush brush)
            return brush;
        return null;
    }
}
