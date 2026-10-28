using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.Scanning;
using DeskMaster.Core.Visualization;

namespace DeskMaster.App.Controls;

/// <summary>Draws the children of a folder as a squarified treemap. Click a folder tile to open it.</summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<ScanNode>?> ItemsProperty =
        AvaloniaProperty.Register<TreemapControl, IReadOnlyList<ScanNode>?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<TreemapControl, ICommand?>(nameof(OpenCommand));

    // Muted palette that reads well in light and dark themes.
    private static readonly Color[] Palette =
    [
        Color.Parse("#4C78A8"), Color.Parse("#59A14F"), Color.Parse("#B07AA1"), Color.Parse("#E39C37"),
        Color.Parse("#4AA3A2"), Color.Parse("#D3685E"), Color.Parse("#7F8FA6"), Color.Parse("#9C755F"),
    ];

    private static readonly IBrush FileBrush = new SolidColorBrush(Color.Parse("#8A94A6"));
    private static readonly IBrush GroupBrush = new SolidColorBrush(Color.Parse("#B9C0CC"));
    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private IReadOnlyList<TreemapTile<ScanNode>> _tiles = [];
    private ScanNode? _hover;
    private Size _laidOutFor;

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(ItemsProperty);
        ItemsProperty.Changed.AddClassHandler<TreemapControl>((c, _) => c.InvalidateLayoutCache());
    }

    public TreemapControl()
    {
        ClipToBounds = true;
        FlowDirection = FlowDirection.LeftToRight; // never mirror the drawing (it would mirror the labels too)
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public IReadOnlyList<ScanNode>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    private void InvalidateLayoutCache()
    {
        _laidOutFor = default;
        _hover = null;
        InvalidateVisual();
    }

    private void EnsureLayout()
    {
        if (_laidOutFor == Bounds.Size)
            return;
        _laidOutFor = Bounds.Size;
        _tiles = Items is null
            ? []
            : Treemap.Layout(Items, n => n.Size, new TreemapRect(0, 0, Bounds.Width, Bounds.Height));
    }

    public override void Render(DrawingContext context)
    {
        EnsureLayout();
        var gap = new Pen(Brushes.Transparent);
        var hoverPen = new Pen(Brushes.White, 2);
        for (var i = 0; i < _tiles.Count; i++)
        {
            var (node, b) = (_tiles[i].Item, _tiles[i].Bounds);
            var rect = new Rect(b.X + 1, b.Y + 1, Math.Max(0, b.Width - 2), Math.Max(0, b.Height - 2));
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            var fill = node.Kind switch
            {
                ScanNodeKind.Directory => new SolidColorBrush(Palette[i % Palette.Length]),
                ScanNodeKind.SmallFiles => GroupBrush,
                _ => FileBrush,
            };
            context.DrawRectangle(fill, ReferenceEquals(node, _hover) ? hoverPen : gap, rect, 4, 4);

            if (rect.Width > 70 && rect.Height > 34)
            {
                using (context.PushClip(rect.Deflate(6)))
                {
                    var name = new FormattedText(node.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        LabelFace, 12, Brushes.White) { MaxTextWidth = rect.Width - 12, Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                    context.DrawText(name, new Point(rect.X + 7, rect.Y + 6));
                    var size = new FormattedText(ByteSize.Format(node.Size), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        Typeface.Default, 11, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
                    context.DrawText(size, new Point(rect.X + 7, rect.Y + 22));
                }
            }
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover))
            return;
        _hover = hit;
        ToolTip.SetTip(this, hit is null
            ? null
            : $"{hit.Name}\n{ByteSize.Format(hit.Size)} · {hit.FileCount:N0} files{(hit.IsDirectory && hit.Children.Count > 0 ? "\nClick to open" : "")}");
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit is { IsDirectory: true } && OpenCommand?.CanExecute(hit) == true)
            OpenCommand.Execute(hit);
    }

    private ScanNode? HitTest(Point p)
    {
        EnsureLayout();
        foreach (var tile in _tiles)
        {
            var b = tile.Bounds;
            if (p.X >= b.X && p.X < b.X + b.Width && p.Y >= b.Y && p.Y < b.Y + b.Height)
                return tile.Item;
        }
        return null;
    }
}
