using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace AiPlanning;

internal sealed class BoardNode
{
    public string Id = "";
    public string Kind = "", Title = "", Subtitle = "", Tone = "info", Summary = "", Status = "";
    public List<(string Label, string Text)> Parts = new();
    public object? Tag;
    public double X, Y;
    internal Border Root = null!;
    internal TextBlock TitleText = null!, KindText = null!, SummaryText = null!, StatusText = null!;
}

internal sealed record BoardLink(string From, string To, string Text, string Tone);

/// <summary>
/// The board of the page, built like Automation's canvas: cards you can drag, curved arrows between them, a view you can move (right mouse or the empty canvas),
/// zoom (Ctrl + wheel, pinch, or the buttons) and a small map. It only draws; what the cards mean is decided by the page.
/// </summary>
internal sealed class Board : Grid
{
    public const double CardW = 240, CardH = 120;

    private static readonly Windows.UI.Color Green = ColorHelper.FromArgb(255, 34, 165, 90);
    private static readonly Windows.UI.Color Red = ColorHelper.FromArgb(255, 229, 72, 77);
    private static readonly Windows.UI.Color Orange = ColorHelper.FromArgb(255, 245, 158, 11);
    private static readonly Windows.UI.Color Blue = ColorHelper.FromArgb(255, 59, 130, 246);
    private static readonly Windows.UI.Color Grey = ColorHelper.FromArgb(255, 138, 138, 138);
    private static readonly Windows.UI.Color Purple = ColorHelper.FromArgb(255, 139, 92, 246);

    public static Windows.UI.Color ToneColor(string tone) => tone switch
    {
        "agree" or "final" => Green,
        "object" or "error" => Red,
        "draft" => Blue,
        "flaws" or "warn" => Orange,
        "question" => Purple,
        _ => Grey,
    };

    private enum Side { Right, Bottom, Left, Top }

    private sealed class Drag { public string Id = ""; public Point Start; public double X0, Y0; public bool Moved; }
    private sealed class Pan { public Point Start; public double H, V; }

    private sealed class Plan
    {
        public BoardLink Link = null!;
        public BoardNode From = null!, To = null!;
        public int Index;
        public Side Out, In;
        public double OutOff, InOff;
    }

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollViewer _scroll = new();
    private readonly Dictionary<string, BoardNode> _nodes = new();
    private readonly List<BoardLink> _links = new();
    private readonly List<UIElement> _arrowElems = new();
    private readonly List<Rect> _badgeRects = new();
    private readonly Canvas _miniCanvas = new() { Width = 188, Height = 120 };
    private readonly Border _mini = new() { Visibility = Visibility.Collapsed };
    private double _miniScale = 1;
    private bool _miniDrag;
    private Drag? _drag;
    private Pan? _pan;
    private string? _selected;

    public event Action<BoardNode>? NodeClicked;
    public event Action<string>? ZoomChanged;

    public IEnumerable<BoardNode> Nodes => _nodes.Values;
    public BoardNode? Find(string id) => _nodes.GetValueOrDefault(id);

    public Board()
    {
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollMode = ScrollMode.Enabled;
        _scroll.VerticalScrollMode = ScrollMode.Enabled;
        _scroll.ZoomMode = ZoomMode.Enabled;   // Ctrl + mouse wheel and a pinch zoom by themselves
        _scroll.MinZoomFactor = 0.3f;
        _scroll.MaxZoomFactor = 2f;
        _scroll.Content = _canvas;
        _scroll.ViewChanged += (_, _) => { ZoomChanged?.Invoke($"{(int)Math.Round(_scroll.ZoomFactor * 100)}%"); RefreshMini(); };
        _scroll.SizeChanged += (_, _) => UpdateCanvasSize();
        _canvas.AddHandler(PointerPressedEvent, new PointerEventHandler(Canvas_Pressed), true);
        _canvas.AddHandler(PointerMovedEvent, new PointerEventHandler(Canvas_Moved), true);
        _canvas.AddHandler(PointerReleasedEvent, new PointerEventHandler(Canvas_Released), true);
        _canvas.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Canvas_CaptureLost), true);
        Children.Add(_scroll);
        BuildMini();
        Children.Add(_mini);
    }

    // ───────── content ─────────

    public void Clear()
    {
        _canvas.Children.Clear();
        _nodes.Clear();
        _links.Clear();
        _arrowElems.Clear();
        _selected = null;
        UpdateCanvasSize();
    }

    public void AddNode(BoardNode n, double x, double y)
    {
        n.X = x; n.Y = y;
        BuildCard(n);
        _nodes[n.Id] = n;
        _canvas.Children.Add(n.Root);
        Canvas.SetZIndex(n.Root, 1);
        Canvas.SetLeft(n.Root, x);
        Canvas.SetTop(n.Root, y);
        UpdateCanvasSize();
        DrawArrows();
    }

    public void AddLink(BoardLink link)
    {
        _links.Add(link);
        DrawArrows();
    }

    public void MoveNode(BoardNode n, double x, double y)
    {
        n.X = x; n.Y = y;
        Canvas.SetLeft(n.Root, x);
        Canvas.SetTop(n.Root, y);
        UpdateCanvasSize();
        DrawArrows();
    }

    /// <summary>Shows the node's current texts and colors again (after the page changed it).</summary>
    public void Refresh(BoardNode n)
    {
        n.TitleText.Text = n.Title;
        n.KindText.Text = n.Subtitle.Length > 0 ? n.Kind + "  ·  " + n.Subtitle : n.Kind;
        n.SummaryText.Text = n.Summary;
        n.StatusText.Text = n.Status;
        ApplyBorder(n);
        DrawArrows();
        RefreshMini();
    }

    /// <summary>Scrolls so the card is in view (only when it is outside).</summary>
    public void Reveal(BoardNode n)
    {
        var z = _scroll.ZoomFactor;
        double? h = null, v = null;
        if ((n.X + CardW + 40) * z > _scroll.HorizontalOffset + _scroll.ViewportWidth) h = Math.Max(0, (n.X + CardW + 60) * z - _scroll.ViewportWidth);
        else if (n.X * z < _scroll.HorizontalOffset) h = Math.Max(0, n.X * z - 40);
        if ((n.Y + CardH + 20) * z > _scroll.VerticalOffset + _scroll.ViewportHeight) v = Math.Max(0, (n.Y + CardH + 40) * z - _scroll.ViewportHeight);
        else if (n.Y * z < _scroll.VerticalOffset) v = Math.Max(0, n.Y * z - 30);
        if (h != null || v != null) _scroll.ChangeView(h, v, null, true);
    }

    public void Select(string? id)
    {
        _selected = id;
        foreach (var n in _nodes.Values) ApplyBorder(n);
    }

    // ───────── zoom ─────────

    public void Zoom(float factor)
    {
        var z0 = _scroll.ZoomFactor;
        var z = Math.Clamp(z0 * factor, _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
        var cx = (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) / z0;
        var cy = (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / z0;
        _scroll.ChangeView(Math.Max(0, cx * z - _scroll.ViewportWidth / 2), Math.Max(0, cy * z - _scroll.ViewportHeight / 2), z, true);
    }

    public void ResetZoom() => _scroll.ChangeView(null, null, 1f, true);

    public void ZoomToFit()
    {
        if (_nodes.Count == 0) { ResetZoom(); return; }
        var (w, h) = Extent();
        var z = (float)Math.Clamp(Math.Min(_scroll.ViewportWidth / (w + 40), _scroll.ViewportHeight / (h + 40)), _scroll.MinZoomFactor, 1.0);
        _scroll.ChangeView(0, 0, z, true);
    }

    private (double W, double H) Extent()
    {
        double w = 0, h = 0;
        foreach (var n in _nodes.Values) { w = Math.Max(w, n.X + CardW); h = Math.Max(h, n.Y + CardH); }
        return (w, h);
    }

    private void UpdateCanvasSize()
    {
        var (w, h) = Extent();
        var z = Math.Max(0.1, _scroll.ZoomFactor);
        _canvas.Width = Math.Max(w + 400, Math.Max(_scroll.ViewportWidth / z, 600));
        _canvas.Height = Math.Max(h + 300, Math.Max(_scroll.ViewportHeight / z, 400));
        RefreshMini();
    }

    // ───────── cards ─────────

    private static SolidColorBrush Solid(Windows.UI.Color c, double opacity = 1) => new(c) { Opacity = opacity };

    private void BuildCard(BoardNode n)
    {
        var dark = ActualTheme == ElementTheme.Dark;
        n.TitleText = new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        n.KindText = new TextBlock { Text = n.Subtitle.Length > 0 ? n.Kind + "  ·  " + n.Subtitle : n.Kind, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        n.SummaryText = new TextBlock { Text = n.Summary, FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 3, Margin = new Thickness(0, 4, 0, 0) };
        n.StatusText = new TextBlock { Text = n.Status, FontSize = 12, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        var grid = new Grid();
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetRow(n.KindText, 1);
        Grid.SetRow(n.SummaryText, 2);
        Grid.SetRow(n.StatusText, 3);
        grid.Children.Add(n.TitleText);
        grid.Children.Add(n.KindText);
        grid.Children.Add(n.SummaryText);
        grid.Children.Add(n.StatusText);
        n.Root = new Border
        {
            Width = CardW, Height = CardH, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2),
            Padding = new Thickness(12, 8, 12, 8), Tag = n.Id, Child = grid,
            Background = Solid(dark ? ColorHelper.FromArgb(255, 43, 43, 43) : ColorHelper.FromArgb(255, 255, 255, 255)),
        };
        ToolTipService.SetToolTip(n.Root, n.Title + "\nClick to read it · drag to move");
        ApplyBorder(n);
    }

    private void ApplyBorder(BoardNode n)
    {
        var selected = n.Id == _selected;
        var color = ToneColor(n.Tone);
        n.Root.BorderBrush = Solid(selected ? ColorHelper.FromArgb(255, 96, 165, 250) : color, selected ? 1 : n.Tone == "info" ? 0.6 : 1);
        n.Root.BorderThickness = new Thickness(selected || n.Tone == "final" ? 3 : 2);
    }

    // ───────── arrows (same geometry as Automation) ─────────

    private static Point AnchorOf(double x, double y, Side side, double off) => side switch
    {
        Side.Right => new Point(x + CardW, y + CardH / 2 + off),
        Side.Left => new Point(x, y + CardH / 2 + off),
        Side.Bottom => new Point(x + CardW / 2 + off, y + CardH),
        _ => new Point(x + CardW / 2 + off, y),
    };

    private static (double X, double Y) NormalOf(Side side) => side switch
    {
        Side.Right => (1, 0), Side.Left => (-1, 0), Side.Bottom => (0, 1), _ => (0, -1),
    };

    private static Side Opposite(Side side) => side switch { Side.Right => Side.Left, Side.Left => Side.Right, Side.Bottom => Side.Top, _ => Side.Bottom };

    private static Side SideToward(double fromX, double fromY, double toX, double toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        if (Math.Abs(dx) / CardW >= Math.Abs(dy) / CardH) return dx >= 0 ? Side.Right : Side.Left;
        return dy >= 0 ? Side.Bottom : Side.Top;
    }

    private static double CenterAlong(BoardNode n, Side side) => side is Side.Right or Side.Left ? n.Y + CardH / 2 : n.X + CardW / 2;

    private static void Spread(IEnumerable<IGrouping<(string, Side), Plan>> groups, bool outgoing)
    {
        foreach (var g in groups)
        {
            var side = g.Key.Item2;
            var list = g.OrderBy(p => CenterAlong(outgoing ? p.To : p.From, side)).ThenBy(p => p.Index).ToList();
            var len = side is Side.Right or Side.Left ? CardH : CardW;
            var step = Math.Min(34, (len - 50) / Math.Max(1, list.Count - 1));
            for (var k = 0; k < list.Count; k++)
            {
                var off = (k - (list.Count - 1) / 2.0) * step;
                if (outgoing) list[k].OutOff = off; else list[k].InOff = off;
            }
        }
    }

    private void DrawArrows()
    {
        foreach (var el in _arrowElems) _canvas.Children.Remove(el);
        _arrowElems.Clear();
        _badgeRects.Clear();
        var plans = new List<Plan>();
        for (var i = 0; i < _links.Count; i++)
        {
            if (!_nodes.TryGetValue(_links[i].From, out var from) || !_nodes.TryGetValue(_links[i].To, out var to)) continue;
            var side = SideToward(from.X + CardW / 2, from.Y + CardH / 2, to.X + CardW / 2, to.Y + CardH / 2);
            plans.Add(new Plan { Link = _links[i], From = from, To = to, Index = i, Out = side, In = Opposite(side) });
        }
        Spread(plans.GroupBy(p => (p.From.Id, p.Out)), true);
        Spread(plans.GroupBy(p => (p.To.Id, p.In)), false);
        foreach (var plan in plans) DrawArrow(plan);
    }

    private static Point BezierAt(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private bool CrossesCard(Point p0, Point p1, Point p2, Point p3, string fromId, string toId)
    {
        foreach (var (id, n) in _nodes)
        {
            if (id == fromId || id == toId) continue;
            var x = n.X - 10;
            var y = n.Y - 10;
            for (var i = 1; i < 20; i++)
            {
                var q = BezierAt(p0, p1, p2, p3, i / 20.0);
                if (q.X >= x && q.X <= x + CardW + 20 && q.Y >= y && q.Y <= y + CardH + 20) return true;
            }
        }
        return false;
    }

    private void DrawArrow(Plan plan)
    {
        var p0 = AnchorOf(plan.From.X, plan.From.Y, plan.Out, plan.OutOff);
        var p3 = AnchorOf(plan.To.X, plan.To.Y, plan.In, plan.InOff);
        var reach = Math.Clamp(Math.Sqrt((p3.X - p0.X) * (p3.X - p0.X) + (p3.Y - p0.Y) * (p3.Y - p0.Y)) / 2.5, 50, 160);
        var n0 = NormalOf(plan.Out);
        var n1 = NormalOf(plan.In);
        var p1 = new Point(p0.X + n0.X * reach, p0.Y + n0.Y * reach);
        var p2 = new Point(p3.X + n1.X * reach, p3.Y + n1.Y * reach);

        if (CrossesCard(p0, p1, p2, p3, plan.From.Id, plan.To.Id))
        {
            var dx = p3.X - p0.X;
            var dy = p3.Y - p0.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > 1)
            {
                var nx = -dy / dist;
                var ny = dx / dist;
                foreach (var bulge in new[] { 70.0, -70.0, 120.0, -120.0, 180.0, -180.0, 260.0, -260.0 })
                {
                    var b1 = new Point(p1.X + nx * bulge, p1.Y + ny * bulge);
                    var b2 = new Point(p2.X + nx * bulge, p2.Y + ny * bulge);
                    if (CrossesCard(p0, b1, b2, p3, plan.From.Id, plan.To.Id)) continue;
                    p1 = b1; p2 = b2;
                    break;
                }
            }
        }

        var color = ToneColor(plan.Link.Tone);
        var figure = new PathFigure { StartPoint = p0, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var path = new Shapes.Path { Data = geometry, Stroke = Solid(color), StrokeThickness = 2.5, IsHitTestVisible = false };

        var vx = p3.X - p2.X;
        var vy = p3.Y - p2.Y;
        var len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1) { vx = 1; vy = 0; len = 1; }
        vx /= len; vy /= len;
        var px = -vy;
        var py = vx;
        var head = new Shapes.Polygon
        {
            Fill = Solid(color), IsHitTestVisible = false,
            Points = new PointCollection { p3, new Point(p3.X - 13 * vx + 6 * px, p3.Y - 13 * vy + 6 * py), new Point(p3.X - 13 * vx - 6 * px, p3.Y - 13 * vy - 6 * py) },
        };
        Canvas.SetZIndex(path, 0);
        Canvas.SetZIndex(head, 0);
        var elems = new List<UIElement> { path, head };

        if (plan.Link.Text.Length > 0)
        {
            var ink = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 160 ? Colors.Black : Colors.White;
            var badge = new Border
            {
                Background = Solid(color), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), IsHitTestVisible = false,
                Child = new TextBlock { Text = plan.Link.Text, FontSize = 11, Foreground = Solid(ink), MaxLines = 1 },
            };
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var bw = badge.DesiredSize.Width;
            var bh = badge.DesiredSize.Height;
            var mid = BezierAt(p0, p1, p2, p3, 0.5);
            foreach (var t in new[] { 0.5, 0.38, 0.62, 0.28, 0.72, 0.2, 0.8 })
            {
                var cand = BezierAt(p0, p1, p2, p3, t);
                var r = new Rect(cand.X - bw / 2 - 3, cand.Y - bh / 2 - 3, bw + 6, bh + 6);
                if (_badgeRects.Any(o => r.X < o.X + o.Width && o.X < r.X + r.Width && r.Y < o.Y + o.Height && o.Y < r.Y + r.Height)) continue;
                mid = cand;
                break;
            }
            _badgeRects.Add(new Rect(mid.X - bw / 2 - 3, mid.Y - bh / 2 - 3, bw + 6, bh + 6));
            Canvas.SetLeft(badge, mid.X - bw / 2);
            Canvas.SetTop(badge, mid.Y - bh / 2);
            Canvas.SetZIndex(badge, 3);
            elems.Add(badge);
        }
        foreach (var el in elems) { _canvas.Children.Add(el); _arrowElems.Add(el); }
    }

    // ───────── mouse: move a card, move the view ─────────

    private string? CardOf(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && !ReferenceEquals(d, _canvas))
        {
            if (d is Border b && b.Tag is string id && _nodes.TryGetValue(id, out var n) && ReferenceEquals(n.Root, b)) return id;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Canvas_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        if (pt.Properties.IsLeftButtonPressed)
        {
            var id = CardOf(e.OriginalSource);
            if (id != null && _nodes.TryGetValue(id, out var n))
            {
                _drag = new Drag { Id = id, Start = pt.Position, X0 = n.X, Y0 = n.Y };
                Select(id);
                _canvas.CapturePointer(e.Pointer);
                return;
            }
        }
        var ps = e.GetCurrentPoint(_scroll);
        var onEmpty = ps.Properties.IsLeftButtonPressed && ReferenceEquals(e.OriginalSource, _canvas);
        if (!ps.Properties.IsRightButtonPressed && !onEmpty) return;
        _pan = new Pan { Start = ps.Position, H = _scroll.HorizontalOffset, V = _scroll.VerticalOffset };
        _canvas.CapturePointer(e.Pointer);
        if (onEmpty) Select(null);
    }

    private void Canvas_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != null)
        {
            if (!_nodes.TryGetValue(_drag.Id, out var n)) return;
            var p = e.GetCurrentPoint(_canvas).Position;
            var dx = p.X - _drag.Start.X;
            var dy = p.Y - _drag.Start.Y;
            if (!_drag.Moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
            _drag.Moved = true;
            MoveNode(n, Math.Max(0, Math.Round((_drag.X0 + dx) / 10) * 10), Math.Max(0, Math.Round((_drag.Y0 + dy) / 10) * 10));
            return;
        }
        if (_pan == null) return;
        var q = e.GetCurrentPoint(_scroll).Position;
        _scroll.ChangeView(_pan.H - (q.X - _pan.Start.X), _pan.V - (q.Y - _pan.Start.Y), null, true);
    }

    private void Canvas_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != null)
        {
            var d = _drag;
            _drag = null;
            _canvas.ReleasePointerCapture(e.Pointer);
            if (!d.Moved && _nodes.TryGetValue(d.Id, out var n)) NodeClicked?.Invoke(n);   // a click opens the card
            return;
        }
        if (_pan == null) return;
        _pan = null;
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    private void Canvas_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pan = null;
        _drag = null;
    }

    // ───────── map ─────────

    private void BuildMini()
    {
        _mini.HorizontalAlignment = HorizontalAlignment.Right;
        _mini.VerticalAlignment = VerticalAlignment.Top;
        _mini.Margin = new Thickness(0, 10, 18, 0);
        _mini.Padding = new Thickness(4);
        _mini.CornerRadius = new CornerRadius(8);
        _mini.BorderThickness = new Thickness(1);
        _mini.BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.5 };
        _mini.Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.18 };
        _mini.Child = _miniCanvas;
        _miniCanvas.Background = new SolidColorBrush(Colors.Transparent);
        ToolTipService.SetToolTip(_mini, "Map of the board: click or drag to move the view");
        _miniCanvas.PointerPressed += (_, e) => { _miniDrag = true; _miniCanvas.CapturePointer(e.Pointer); MiniGo(e); };
        _miniCanvas.PointerMoved += (_, e) => { if (_miniDrag) MiniGo(e); };
        _miniCanvas.PointerReleased += (_, e) => { _miniDrag = false; _miniCanvas.ReleasePointerCapture(e.Pointer); };
        _miniCanvas.PointerCaptureLost += (_, _) => _miniDrag = false;
    }

    private void MiniGo(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_miniCanvas).Position;
        var z = Math.Max(0.1, _scroll.ZoomFactor);
        _scroll.ChangeView(Math.Max(0, p.X / _miniScale * z - _scroll.ViewportWidth / 2), Math.Max(0, p.Y / _miniScale * z - _scroll.ViewportHeight / 2), null, true);
    }

    private void RefreshMini()
    {
        if (_nodes.Count == 0) { _mini.Visibility = Visibility.Collapsed; return; }
        var (w0, h0) = Extent();
        double w = w0 + 40, h = h0 + 40;
        var z = Math.Max(0.1, _scroll.ZoomFactor);
        var viewW = _scroll.ViewportWidth / z;
        var viewH = _scroll.ViewportHeight / z;
        var needed = w > viewW * 1.05 || h > viewH * 1.05;
        _mini.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed) return;
        w = Math.Max(w, viewW); h = Math.Max(h, viewH);
        _miniScale = Math.Min(_miniCanvas.Width / w, _miniCanvas.Height / h);
        _miniCanvas.Children.Clear();
        foreach (var n in _nodes.Values)
            _miniCanvas.Children.Add(new Border
            {
                Width = Math.Max(3, CardW * _miniScale), Height = Math.Max(3, CardH * _miniScale), CornerRadius = new CornerRadius(2),
                Background = Solid(ToneColor(n.Tone)), Margin = new Thickness(n.X * _miniScale, n.Y * _miniScale, 0, 0),
            });
        var vx = _scroll.HorizontalOffset / z * _miniScale;
        var vy = _scroll.VerticalOffset / z * _miniScale;
        _miniCanvas.Children.Add(new Border
        {
            Width = Math.Max(6, Math.Min(viewW * _miniScale, _miniCanvas.Width - vx)), Height = Math.Max(6, Math.Min(viewH * _miniScale, _miniCanvas.Height - vy)),
            BorderThickness = new Thickness(1.5), BorderBrush = Solid(ColorHelper.FromArgb(255, 96, 165, 250)),
            Background = Solid(ColorHelper.FromArgb(255, 96, 165, 250), 0.12), CornerRadius = new CornerRadius(2), Margin = new Thickness(vx, vy, 0, 0),
        });
    }
}
