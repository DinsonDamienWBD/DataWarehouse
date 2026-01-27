using Microsoft.Extensions.Logging;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Touch point tracking data for gesture recognition.
/// </summary>
public sealed class TouchPoint
{
    /// <summary>
    /// Gets or sets the unique touch identifier.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets or sets the X coordinate.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Gets or sets the Y coordinate.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Gets or sets the initial X coordinate when touch started.
    /// </summary>
    public double StartX { get; init; }

    /// <summary>
    /// Gets or sets the initial Y coordinate when touch started.
    /// </summary>
    public double StartY { get; init; }

    /// <summary>
    /// Gets or sets when the touch started.
    /// </summary>
    public DateTime StartTime { get; init; }

    /// <summary>
    /// Gets or sets the touch pressure (0.0 - 1.0).
    /// </summary>
    public double Pressure { get; set; }
}

/// <summary>
/// Gesture types that can be recognized.
/// </summary>
public enum GestureType
{
    None,
    Tap,
    DoubleTap,
    LongPress,
    SwipeLeft,
    SwipeRight,
    SwipeUp,
    SwipeDown,
    PinchIn,
    PinchOut,
    Rotate,
    Pan
}

/// <summary>
/// Event arguments for gesture events.
/// </summary>
public sealed class GestureEventArgs : EventArgs
{
    /// <summary>
    /// Gets the type of gesture recognized.
    /// </summary>
    public GestureType Gesture { get; init; }

    /// <summary>
    /// Gets the center X coordinate of the gesture.
    /// </summary>
    public double CenterX { get; init; }

    /// <summary>
    /// Gets the center Y coordinate of the gesture.
    /// </summary>
    public double CenterY { get; init; }

    /// <summary>
    /// Gets the delta X for pan/swipe gestures.
    /// </summary>
    public double DeltaX { get; init; }

    /// <summary>
    /// Gets the delta Y for pan/swipe gestures.
    /// </summary>
    public double DeltaY { get; init; }

    /// <summary>
    /// Gets the scale factor for pinch gestures (1.0 = no change).
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Gets the rotation angle in degrees for rotate gestures.
    /// </summary>
    public double Rotation { get; init; }

    /// <summary>
    /// Gets the velocity of the gesture (pixels per second).
    /// </summary>
    public double Velocity { get; init; }

    /// <summary>
    /// Gets or sets whether the gesture was handled.
    /// </summary>
    public bool Handled { get; set; }

    /// <summary>
    /// Gets the touch points involved in this gesture.
    /// </summary>
    public IReadOnlyList<TouchPoint> TouchPoints { get; init; } = Array.Empty<TouchPoint>();
}

/// <summary>
/// Event arguments for long press context menu.
/// </summary>
public sealed class ContextMenuEventArgs : EventArgs
{
    /// <summary>
    /// Gets the X coordinate where the context menu should appear.
    /// </summary>
    public double X { get; init; }

    /// <summary>
    /// Gets the Y coordinate where the context menu should appear.
    /// </summary>
    public double Y { get; init; }

    /// <summary>
    /// Gets the element identifier that was long-pressed.
    /// </summary>
    public string? ElementId { get; init; }

    /// <summary>
    /// Gets or sets whether the event was handled.
    /// </summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Manages touch input and gesture recognition for tablet/mobile support.
/// This is a UI-only service - delegates actions to Shared commands.
/// </summary>
public sealed class TouchManager : IDisposable
{
    private readonly ILogger<TouchManager> _logger;
    private readonly Dictionary<long, TouchPoint> _activeTouches = new();
    private readonly object _touchLock = new();

    private DateTime _lastTapTime;
    private double _lastTapX;
    private double _lastTapY;
    private CancellationTokenSource? _longPressCts;

    // Configuration
    private const int LongPressThresholdMs = 500;
    private const int DoubleTapThresholdMs = 300;
    private const double DoubleTapDistanceThreshold = 30;
    private const double SwipeThreshold = 50;
    private const double SwipeVelocityThreshold = 200;
    private const double PinchThreshold = 10;

    /// <summary>
    /// Event raised when a gesture is recognized.
    /// </summary>
    public event EventHandler<GestureEventArgs>? GestureRecognized;

    /// <summary>
    /// Event raised when a long press context menu should be shown.
    /// </summary>
    public event EventHandler<ContextMenuEventArgs>? ContextMenuRequested;

    /// <summary>
    /// Gets whether touch input is currently active.
    /// </summary>
    public bool IsTouchActive => _activeTouches.Count > 0;

    /// <summary>
    /// Gets the number of active touch points.
    /// </summary>
    public int ActiveTouchCount => _activeTouches.Count;

    /// <summary>
    /// Gets or sets the minimum tap target size in pixels (for accessibility).
    /// WCAG 2.1 AA requires 44x44 pixels minimum.
    /// </summary>
    public int MinimumTapTargetSize { get; set; } = 44;

    /// <summary>
    /// Gets or sets whether long press triggers context menu.
    /// </summary>
    public bool LongPressContextMenuEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether pinch-to-zoom is enabled.
    /// </summary>
    public bool PinchZoomEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether swipe gestures are enabled.
    /// </summary>
    public bool SwipeGesturesEnabled { get; set; } = true;

    /// <summary>
    /// Initializes a new instance of the TouchManager class.
    /// </summary>
    /// <param name="logger">Logger for touch-related operations.</param>
    public TouchManager(ILogger<TouchManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Handles a touch start event.
    /// </summary>
    /// <param name="id">The unique touch identifier.</param>
    /// <param name="x">The X coordinate.</param>
    /// <param name="y">The Y coordinate.</param>
    /// <param name="pressure">The touch pressure (0.0 - 1.0).</param>
    public void HandleTouchStart(long id, double x, double y, double pressure = 1.0)
    {
        lock (_touchLock)
        {
            var touch = new TouchPoint
            {
                Id = id,
                X = x,
                Y = y,
                StartX = x,
                StartY = y,
                StartTime = DateTime.UtcNow,
                Pressure = pressure
            };

            _activeTouches[id] = touch;
            _logger.LogDebug("Touch start: id={Id}, pos=({X},{Y}), active={Count}",
                id, x, y, _activeTouches.Count);

            // Start long press detection for single touch
            if (_activeTouches.Count == 1 && LongPressContextMenuEnabled)
            {
                StartLongPressDetection(x, y);
            }
            else
            {
                CancelLongPressDetection();
            }
        }
    }

    /// <summary>
    /// Handles a touch move event.
    /// </summary>
    /// <param name="id">The unique touch identifier.</param>
    /// <param name="x">The new X coordinate.</param>
    /// <param name="y">The new Y coordinate.</param>
    /// <param name="pressure">The touch pressure.</param>
    public void HandleTouchMove(long id, double x, double y, double pressure = 1.0)
    {
        lock (_touchLock)
        {
            if (!_activeTouches.TryGetValue(id, out var touch))
                return;

            var prevX = touch.X;
            var prevY = touch.Y;
            touch.X = x;
            touch.Y = y;
            touch.Pressure = pressure;

            // Cancel long press if moved too far
            var distance = GetDistance(touch.StartX, touch.StartY, x, y);
            if (distance > 10)
            {
                CancelLongPressDetection();
            }

            // Handle multi-touch gestures
            if (_activeTouches.Count == 2 && PinchZoomEnabled)
            {
                HandlePinchGesture();
            }
            else if (_activeTouches.Count == 1)
            {
                HandlePanGesture(touch, prevX, prevY);
            }
        }
    }

    /// <summary>
    /// Handles a touch end event.
    /// </summary>
    /// <param name="id">The unique touch identifier.</param>
    public void HandleTouchEnd(long id)
    {
        lock (_touchLock)
        {
            if (!_activeTouches.TryGetValue(id, out var touch))
                return;

            var duration = (DateTime.UtcNow - touch.StartTime).TotalMilliseconds;
            var distance = GetDistance(touch.StartX, touch.StartY, touch.X, touch.Y);
            var velocity = distance / Math.Max(1, duration) * 1000; // pixels per second

            _logger.LogDebug("Touch end: id={Id}, duration={Duration}ms, distance={Distance}, velocity={Velocity}",
                id, duration, distance, velocity);

            CancelLongPressDetection();
            _activeTouches.Remove(id);

            // Detect gesture type
            if (distance < 10 && duration < 300)
            {
                // Tap detection
                HandleTapGesture(touch);
            }
            else if (SwipeGesturesEnabled && distance >= SwipeThreshold && velocity >= SwipeVelocityThreshold)
            {
                // Swipe detection
                HandleSwipeGesture(touch, distance, velocity);
            }
        }
    }

    /// <summary>
    /// Handles a touch cancel event.
    /// </summary>
    /// <param name="id">The unique touch identifier.</param>
    public void HandleTouchCancel(long id)
    {
        lock (_touchLock)
        {
            CancelLongPressDetection();
            _activeTouches.Remove(id);
            _logger.LogDebug("Touch cancelled: id={Id}", id);
        }
    }

    /// <summary>
    /// Clears all active touch points.
    /// </summary>
    public void ClearAllTouches()
    {
        lock (_touchLock)
        {
            CancelLongPressDetection();
            _activeTouches.Clear();
        }
    }

    /// <summary>
    /// Checks if an element meets minimum tap target size requirements.
    /// </summary>
    /// <param name="width">Element width in pixels.</param>
    /// <param name="height">Element height in pixels.</param>
    /// <returns>True if the element meets accessibility requirements.</returns>
    public bool MeetsTapTargetRequirements(double width, double height)
    {
        return width >= MinimumTapTargetSize && height >= MinimumTapTargetSize;
    }

    /// <summary>
    /// Gets the recommended padding to meet tap target requirements.
    /// </summary>
    /// <param name="currentWidth">Current element width.</param>
    /// <param name="currentHeight">Current element height.</param>
    /// <returns>Tuple of (horizontal padding, vertical padding) needed.</returns>
    public (double horizontalPadding, double verticalPadding) GetRequiredPadding(double currentWidth, double currentHeight)
    {
        var hPadding = Math.Max(0, (MinimumTapTargetSize - currentWidth) / 2);
        var vPadding = Math.Max(0, (MinimumTapTargetSize - currentHeight) / 2);
        return (hPadding, vPadding);
    }

    private void StartLongPressDetection(double x, double y)
    {
        CancelLongPressDetection();
        _longPressCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LongPressThresholdMs, _longPressCts.Token);

                lock (_touchLock)
                {
                    if (_activeTouches.Count == 1)
                    {
                        _logger.LogDebug("Long press detected at ({X},{Y})", x, y);

                        ContextMenuRequested?.Invoke(this, new ContextMenuEventArgs
                        {
                            X = x,
                            Y = y
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Long press was cancelled
            }
        });
    }

    private void CancelLongPressDetection()
    {
        _longPressCts?.Cancel();
        _longPressCts?.Dispose();
        _longPressCts = null;
    }

    private void HandleTapGesture(TouchPoint touch)
    {
        var now = DateTime.UtcNow;
        var timeSinceLastTap = (now - _lastTapTime).TotalMilliseconds;
        var distanceFromLastTap = GetDistance(_lastTapX, _lastTapY, touch.X, touch.Y);

        GestureType gesture;
        if (timeSinceLastTap < DoubleTapThresholdMs && distanceFromLastTap < DoubleTapDistanceThreshold)
        {
            gesture = GestureType.DoubleTap;
            _lastTapTime = DateTime.MinValue; // Reset to prevent triple tap
        }
        else
        {
            gesture = GestureType.Tap;
            _lastTapTime = now;
            _lastTapX = touch.X;
            _lastTapY = touch.Y;
        }

        RaiseGestureEvent(gesture, touch.X, touch.Y, 0, 0, 1.0, 0, 0, new[] { touch });
    }

    private void HandleSwipeGesture(TouchPoint touch, double distance, double velocity)
    {
        var deltaX = touch.X - touch.StartX;
        var deltaY = touch.Y - touch.StartY;
        var absX = Math.Abs(deltaX);
        var absY = Math.Abs(deltaY);

        GestureType gesture;
        if (absX > absY)
        {
            gesture = deltaX > 0 ? GestureType.SwipeRight : GestureType.SwipeLeft;
        }
        else
        {
            gesture = deltaY > 0 ? GestureType.SwipeDown : GestureType.SwipeUp;
        }

        RaiseGestureEvent(gesture, touch.StartX, touch.StartY, deltaX, deltaY, 1.0, 0, velocity, new[] { touch });
    }

    private void HandlePanGesture(TouchPoint touch, double prevX, double prevY)
    {
        var deltaX = touch.X - prevX;
        var deltaY = touch.Y - prevY;

        if (Math.Abs(deltaX) > 1 || Math.Abs(deltaY) > 1)
        {
            RaiseGestureEvent(GestureType.Pan, touch.X, touch.Y, deltaX, deltaY, 1.0, 0, 0, new[] { touch });
        }
    }

    private void HandlePinchGesture()
    {
        var touches = _activeTouches.Values.ToArray();
        if (touches.Length != 2) return;

        var t1 = touches[0];
        var t2 = touches[1];

        var currentDistance = GetDistance(t1.X, t1.Y, t2.X, t2.Y);
        var startDistance = GetDistance(t1.StartX, t1.StartY, t2.StartX, t2.StartY);

        if (startDistance < 1) return;

        var scale = currentDistance / startDistance;
        var centerX = (t1.X + t2.X) / 2;
        var centerY = (t1.Y + t2.Y) / 2;

        var gesture = scale > 1 ? GestureType.PinchOut : GestureType.PinchIn;

        // Also calculate rotation
        var currentAngle = Math.Atan2(t2.Y - t1.Y, t2.X - t1.X);
        var startAngle = Math.Atan2(t2.StartY - t1.StartY, t2.StartX - t1.StartX);
        var rotation = (currentAngle - startAngle) * 180 / Math.PI;

        RaiseGestureEvent(gesture, centerX, centerY, 0, 0, scale, rotation, 0, touches);
    }

    private void RaiseGestureEvent(
        GestureType gesture,
        double centerX,
        double centerY,
        double deltaX,
        double deltaY,
        double scale,
        double rotation,
        double velocity,
        TouchPoint[] touchPoints)
    {
        var args = new GestureEventArgs
        {
            Gesture = gesture,
            CenterX = centerX,
            CenterY = centerY,
            DeltaX = deltaX,
            DeltaY = deltaY,
            Scale = scale,
            Rotation = rotation,
            Velocity = velocity,
            TouchPoints = touchPoints
        };

        _logger.LogDebug("Gesture recognized: {Gesture} at ({X},{Y}), scale={Scale}, rotation={Rotation}",
            gesture, centerX, centerY, scale, rotation);

        GestureRecognized?.Invoke(this, args);
    }

    private static double GetDistance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Disposes resources used by the TouchManager.
    /// </summary>
    public void Dispose()
    {
        CancelLongPressDetection();
        _activeTouches.Clear();
    }
}
