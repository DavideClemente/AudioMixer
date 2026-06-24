using System;
using AudioMixerWin.Core.Controls;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace AudioMixerWin.Core.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    private sealed class DragState
    {
        public int DraggedIndex { get; set; } = -1;
        public Point StartPointer { get; set; }
        public bool IsActive { get; set; }
        public const double Threshold = 12.0;
    }

    private readonly DragState _drag = new();
    private FrameworkElement? _dragEl;
    private CompositeTransform? _dragTransform;
    private Storyboard? _activeStoryboard;

    public MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    // ── Pointer handlers called from KnobCard ──────────────────────────────

    internal void OnKnobCardPointerPressed(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (_drag.DraggedIndex >= 0) return;

            // Cancel any in-flight cosmetic animation so the element is clean.
            _activeStoryboard?.Stop();
            _activeStoryboard = null;

            var index = IndexOf(card);
            if (index < 0) return;

            _drag.DraggedIndex = index;
            // Relative to MainPage, the same space as el.TransformToVisual(this)
            // in IndexUnderCursor — mixing window-root coords here biases the
            // hit-test by the title-bar offset and breaks upward drops.
            _drag.StartPointer = e.GetCurrentPoint(this).Position;
            _drag.IsActive = false;

            _dragEl = ElementAt(index);
            if (_dragEl != null)
            {
                _dragEl.RenderTransformOrigin = new Point(0.5, 0.5);
                _dragTransform = new CompositeTransform();
                _dragEl.RenderTransform = _dragTransform;
                _dragEl.Opacity = 1.0;
            }

            // Capture on the card itself — not on e.OriginalSource (which could
            // be a Slider thumb or Button that would then eat PointerMoved events).
            card.CapturePointer(e.Pointer);
            e.Handled = true;
        }
        catch { HardReset(); }
    }

    internal void OnKnobCardPointerMoved(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (_drag.DraggedIndex < 0) return;

            var pt = e.GetCurrentPoint(this).Position;
            var dx = pt.X - _drag.StartPointer.X;
            var dy = pt.Y - _drag.StartPointer.Y;

            if (!_drag.IsActive && Math.Sqrt(dx * dx + dy * dy) >= DragState.Threshold)
            {
                _drag.IsActive = true;
                ViewModel.IsDragging = true;
                ViewModel.DraggedChannelIndex = _drag.DraggedIndex;
                _activeStoryboard = PickupAnim(_dragEl, _dragTransform);
            }

            if (!_drag.IsActive) return;

            // Card follows cursor — the "floating" feel.
            if (_dragTransform != null)
            {
                _dragTransform.TranslateX = dx;
                _dragTransform.TranslateY = dy;
            }

            // Index of the card under the cursor — drop lands in that card's slot.
            ViewModel.TargetDropIndex = IndexUnderCursor(pt.X, pt.Y);
            e.Handled = true;
        }
        catch { HardReset(); }
    }

    internal void OnKnobCardPointerReleased(KnobCard card, PointerRoutedEventArgs e)
    {
        try
        {
            if (_drag.DraggedIndex < 0) return;

            var fromIndex = _drag.DraggedIndex;
            var toIndex   = ViewModel.TargetDropIndex;
            var wasActive = _drag.IsActive;
            var el        = _dragEl;
            var t         = _dragTransform;

            card.ReleasePointerCapture(e.Pointer);
            ClearState();

            // A held storyboard (pickup) would override the manual transform
            // writes below, so stop it first.
            _activeStoryboard?.Stop();
            _activeStoryboard = null;

            if (!wasActive || el == null || t == null)
            {
                e.Handled = true;
                return;
            }

            // No target (dropped in a gap or back on itself): glide home.
            if (toIndex < 0 || toIndex == fromIndex)
            {
                _activeStoryboard = CancelAnim(el, t);
                e.Handled = true;
                return;
            }

            // Offset of the dragged card from its home slot at the moment of release.
            var relDx = t.TranslateX;
            var relDy = t.TranslateY;

            // Reset the dragged card's transform, then measure both grid squares
            // (RenderTransform feeds TransformToVisual, so measure after the reset).
            t.TranslateX = 0; t.TranslateY = 0;
            t.ScaleX = 1.0;   t.ScaleY = 1.0;
            el.Opacity = 1.0;

            var posFrom = SlotOrigin(fromIndex);
            var posTo   = SlotOrigin(toIndex);

            // Swap data. ItemsRepeater rebinds in place: the element in the
            // "from" square now shows the target channel, and vice versa.
            ViewModel.SwapChannels(fromIndex, toIndex);

            // FLIP: after layout, start each card at the OTHER square and slide
            // it home, so the two cards visibly trade places.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                // Target card: from its old square (posTo) into the home square (posFrom).
                SlideIntoPlace(ElementAt(fromIndex), posTo.X - posFrom.X, posTo.Y - posFrom.Y);

                // Dragged card: continues from where it was released into the target square.
                SlideIntoPlace(ElementAt(toIndex),
                               (posFrom.X + relDx) - posTo.X,
                               (posFrom.Y + relDy) - posTo.Y);
            });

            e.Handled = true;
        }
        catch { HardReset(); }
    }

    internal void OnKnobCardPointerCaptureLost(KnobCard card, PointerRoutedEventArgs e)
    {
        if (!_drag.IsActive) { ClearState(); return; }
        var el = _dragEl;
        var t  = _dragTransform;
        ClearState();
        if (el != null && t != null)
            _activeStoryboard = CancelAnim(el, t);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _drag.IsActive)
        {
            var el = _dragEl;
            var t  = _dragTransform;
            ClearState();
            if (el != null && t != null)
                _activeStoryboard = CancelAnim(el, t);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Animations ────────────────────────────────────────────────────────

    private Storyboard PickupAnim(FrameworkElement? el, CompositeTransform? t)
    {
        var sb = new Storyboard();
        if (el == null || t == null) return sb;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        Anim(sb, t,  "ScaleX",   1.0,  1.06, 150, ease);
        Anim(sb, t,  "ScaleY",   1.0,  1.06, 150, ease);
        Anim(sb, el, "Opacity",  1.0,  0.82, 150, ease);
        sb.Begin();
        return sb;
    }

    // Places the card at (offsetX, offsetY) from its final square, then slides
    // it to rest — used for both halves of a swap so they cross paths.
    private void SlideIntoPlace(FrameworkElement? el, double offsetX, double offsetY)
    {
        if (el == null) return;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var t = el.RenderTransform as CompositeTransform ?? new CompositeTransform();
        el.RenderTransform = t;
        t.TranslateX = offsetX;
        t.TranslateY = offsetY;
        t.ScaleX = 1.0; t.ScaleY = 1.0;
        el.Opacity = 1.0;

        var sb   = new Storyboard();
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        Anim(sb, t, "TranslateX", offsetX, 0, 260, ease);
        Anim(sb, t, "TranslateY", offsetY, 0, 260, ease);
        sb.Completed += (_, _) => { t.TranslateX = 0; t.TranslateY = 0; };
        sb.Begin();
    }

    private Storyboard CancelAnim(FrameworkElement el, CompositeTransform t)
    {
        var sb   = new Storyboard();
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        Anim(sb, t,  "TranslateX", t.TranslateX, 0,   180, ease);
        Anim(sb, t,  "TranslateY", t.TranslateY, 0,   180, ease);
        Anim(sb, t,  "ScaleX",     t.ScaleX,     1.0, 180, ease);
        Anim(sb, t,  "ScaleY",     t.ScaleY,     1.0, 180, ease);
        Anim(sb, el, "Opacity",    el.Opacity,   1.0, 180, ease);
        sb.Completed += (_, _) =>
        {
            t.TranslateX = 0; t.TranslateY = 0;
            t.ScaleX = 1.0;   t.ScaleY = 1.0;
            el.Opacity = 1.0;
        };
        sb.Begin();
        return sb;
    }

    private static void Anim(Storyboard sb, DependencyObject target, string prop,
                              double from, double to, int ms, EasingFunctionBase? ease = null)
    {
        var a = new DoubleAnimation
        {
            From = from, To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = ease
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, prop);
        sb.Children.Add(a);
    }

    // ── Target lookup ───────────────────────────────────────────────────────
    // Returns the index of the card the cursor is over. Dropping anywhere
    // inside a card targets that card's slot — the dragged item takes it and
    // the target shifts aside (remove-then-insert at the original index does
    // exactly this in either drag direction). Falls back to the nearest card
    // center when the cursor is in a gap or margin, so a drop is never lost.

    private int IndexUnderCursor(double x, double y)
    {
        if (ChannelsRepeater is null) return -1;
        int nearest = -1;
        double nearestDist = double.MaxValue;

        for (int i = 0; i < ViewModel.Channels.Count; i++)
        {
            if (i == _drag.DraggedIndex) continue;
            var el = ChannelsRepeater.TryGetElement(i) as FrameworkElement;
            if (el is null) continue;

            var origin = el.TransformToVisual(this).TransformPoint(new Point(0, 0));
            var w = el.ActualWidth;
            var h = el.ActualHeight;

            // Cursor strictly inside this card's bounds → exact hit.
            if (x >= origin.X && x < origin.X + w &&
                y >= origin.Y && y < origin.Y + h)
                return i;

            var cx = origin.X + w / 2;
            var cy = origin.Y + h / 2;
            var d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (d < nearestDist) { nearestDist = d; nearest = i; }
        }

        return nearest;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private int IndexOf(KnobCard card) =>
        card?.Channel is null ? -1 : ViewModel.Channels.IndexOf(card.Channel);

    private FrameworkElement? ElementAt(int i) =>
        ChannelsRepeater?.TryGetElement(i) as FrameworkElement;

    private Point SlotOrigin(int i)
    {
        var el = ElementAt(i);
        return el is null
            ? new Point(0, 0)
            : el.TransformToVisual(this).TransformPoint(new Point(0, 0));
    }

    private void ClearState()
    {
        ViewModel.IsDragging = false;
        ViewModel.DraggedChannelIndex = -1;
        ViewModel.TargetDropIndex = -1;
        ViewModel.DragDropIndicatorY = -1;
        _drag.DraggedIndex = -1;
        _drag.IsActive = false;
        _dragEl = null;
        _dragTransform = null;
    }

    private void HardReset()
    {
        _activeStoryboard?.Stop();
        _activeStoryboard = null;
        if (_dragEl != null && _dragTransform != null)
        {
            _dragTransform.TranslateX = 0; _dragTransform.TranslateY = 0;
            _dragTransform.ScaleX = 1.0;   _dragTransform.ScaleY = 1.0;
            _dragEl.Opacity = 1.0;
        }
        ClearState();
    }
}
