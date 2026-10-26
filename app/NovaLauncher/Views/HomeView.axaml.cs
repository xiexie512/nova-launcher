using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NovaLauncher.Models;
using NovaLauncher.PluginApi;
using NovaLauncher.Services;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

public partial class HomeView : UserControl
{
    private readonly Dictionary<string, Border> _cards = new();
    private readonly Dictionary<string, Border> _cardBodies = new();
    private Point _pressPos;
    private bool _dragArmed;
    private bool _dragActive;
    private enum DragSrc { None, Sidebar, Move }
    private DragSrc _dragSrc = DragSrc.None;
    private string? _dragTypeKey;
    private string? _dragPluginId;
    private PlacedWidget? _dragMove;
    private double _dragOffsetX, _dragOffsetY;

    // 拖动让位状态（编辑主页时，其他卡片自动避让）
    private (int Row, int Col)? _avoidTarget;
    private Dictionary<string, (int Row, int Col)>? _avoidLayout;
    private DispatcherTimer? _avoidTimer;

    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => EnsureSetup();
        AttachedToVisualTree += (_, _) =>
        {
            if (Vm != null) Vm.RefreshHomeDisplay();
            EnsureSetup();
            // 布局完成后再补一次全量重测：确保字体/控件就绪后卡片自然高度准确（内容完整显示）
            Dispatcher.UIThread.Post(() => { if (_setup) RelayoutCards(); });
        };
    }

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private bool _setup;
    private void EnsureSetup()
    {
        if (_setup || Vm == null) return;
        _setup = true;

        Vm.HomeWidgets.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (PlacedWidget w in e.NewItems) AddCard(w);
            if (e.OldItems != null)
                foreach (PlacedWidget w in e.OldItems) RemoveCard(w);
            ScheduleRelayout();
        };
        Vm.AvailableWidgets.CollectionChanged += (_, _) => BuildSidebar();

        // 卡片正文数据变化 → 重新测量自然高度并重排（内容完整显示，不再卡片内滚动）
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.CustomizeOpen)
                or nameof(HomeViewModel.CalendarWeeks)
                or nameof(HomeViewModel.CalendarMonth)
                or nameof(HomeViewModel.TodayPlayedText)
                or nameof(HomeViewModel.DownloadStatus)
                or nameof(HomeViewModel.DownloadSourceText)
                or nameof(HomeViewModel.GreetingLine)
                or nameof(HomeViewModel.GreetingDate)
                or nameof(HomeViewModel.AccountName))
                ScheduleRelayout();
        };
        foreach (var col in new System.Collections.Specialized.INotifyCollectionChanged[]
        {
            Vm.Instances, Vm.PinnedInstances, Vm.PinnedWorlds, Vm.Servers,
            Vm.RecentProjects, Vm.Saves, Vm.DownloadVersions,
        })
            col.CollectionChanged += (_, _) => ScheduleRelayout();

        BuildSidebar();
        foreach (var w in Vm.HomeWidgets) AddCard(w);
        RelayoutCards();
    }

    // ================= 侧边栏 =================

    private void BuildSidebar()
    {
        if (Vm == null || SidebarItems == null) return;
        SidebarItems.DataContext = Vm;
        SidebarItems.Bind(ItemsControl.ItemsSourceProperty, new Binding("AvailableWidgets"));
        SidebarItems.ItemTemplate = new FuncDataTemplate<WidgetCatalogItem>((it, _) => BuildSidebarItem(it), true);
    }

    private static double Dist(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private Border BuildSidebarItem(WidgetCatalogItem it)
    {
        var border = new Border
        {
            Classes = { "rowItem" },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var root = new StackPanel { Spacing = 6 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = it.Icon, FontSize = 13 });
        var title = new TextBlock { Text = it.Title, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(title);
        root.Children.Add(titleRow);

        if (!string.IsNullOrEmpty(it.Description))
            root.Children.Add(new TextBlock
            {
                Text = it.Description,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#9AA0A6")),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            });

        var add = new Button
        {
            Classes = { "secondary" },
            Padding = new Thickness(8, 3),
            Content = new TextBlock { Text = "＋ 添加", FontSize = 10 },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0),
        };
        // 点「＋ 添加」：未在编辑模式时先进入编辑模式，再添加该组件
        add.Click += (_, _) =>
        {
            if (Vm?.CustomizeOpen != true && Vm != null) Vm.CustomizeOpen = true;
            Vm?.AddWidgetFromCatalog(it);
        };
        root.Children.Add(add);

        border.Child = root;

        // 从侧边拖拽放入网格（应用内拖放，规避 macOS native DragDrop 崩溃）
        border.PointerPressed += (_, e) =>
        {
            if (Vm?.CustomizeOpen != true) return; // 非编辑主页模式下禁止拖动
            _pressPos = e.GetPosition(border);
            _dragArmed = true; _dragActive = false;
            _dragSrc = DragSrc.Sidebar;
            _dragTypeKey = it.TypeKey; _dragPluginId = it.PluginCardId;
            e.Pointer.Capture(border);
        };
        border.PointerMoved += (_, e) =>
        {
            if (_dragSrc != DragSrc.Sidebar || !_dragArmed) return;
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) { CancelDrag(); return; }
            if (!_dragActive && Dist(e.GetPosition(border), _pressPos) >= 8) _dragActive = true;
        };
        border.PointerReleased += (_, e) =>
        {
            if (_dragSrc != DragSrc.Sidebar) return;
            var wasActive = _dragActive;
            var typeKey = _dragTypeKey; var pluginId = _dragPluginId;
            CancelDrag();
            if (!wasActive || Vm == null || typeKey == null) return;
            var pos = e.GetPosition(WidgetCanvas);
            if (!PointInCanvas(pos, Vm)) return;
            Vm.AddWidgetByType(typeKey, pluginId);
        };
        border.PointerCaptureLost += (_, _) => CancelDrag();
        return border;
    }

    // ================= 网格卡片 =================

    private void AddCard(PlacedWidget w)
    {
        var card = BuildWidgetCard(w);
        _cards[w.InstanceId] = card;
        w.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlacedWidget.X) or nameof(PlacedWidget.Y)
                or nameof(PlacedWidget.W) or nameof(PlacedWidget.H))
                UpdateGeo(card, w);
            else if (e.PropertyName is nameof(PlacedWidget.Cols) or nameof(PlacedWidget.Rows))
            {
                if (_cardBodies.TryGetValue(w.InstanceId, out var body))
                    body.Child = BuildBody(w); // 尺寸变化 → 重建正文（内容始终完整显示，高度自适应）
                RelayoutCards();
            }
        };
        WidgetCanvas.Children.Add(card);
        MeasureNatural(w, card);
        if (Vm != null) Vm.RecalcGridGeometry();
        UpdateGeo(card, w);
    }

    private void RemoveCard(PlacedWidget w)
    {
        if (_cards.Remove(w.InstanceId, out var card))
            WidgetCanvas.Children.Remove(card);
        _cardBodies.Remove(w.InstanceId);
    }

    private void UpdateGeo(Border card, PlacedWidget w)
    {
        Canvas.SetLeft(card, w.X);
        Canvas.SetTop(card, Vm?.RowTop(w.Row) ?? w.Y);
        card.Width = w.W;
        card.Height = Vm == null ? w.H : Math.Max(0, Vm.RowTop(w.Row + w.Rows) - Vm.RowTop(w.Row));
    }

    // ================= 卡片高度自适应（内容完整显示，无需卡片内滚动） =================

    /// <summary>测量卡片自然高度（内容完整显示所需高度），写入 w.NaturalHeight。</summary>
    private void MeasureNatural(PlacedWidget w, Border card)
    {
        card.Width = w.W;
        card.Height = double.NaN; // 临时取消固定高度，让内容决定自然高度
        card.Measure(new Size(w.W, double.PositiveInfinity));
        w.NaturalHeight = card.DesiredSize.Height;
    }

    /// <summary>重新测量所有卡片 → 重算行高与画布高度 → 重新落位。</summary>
    private void RelayoutCards()
    {
        if (Vm == null) return;
        foreach (var w in Vm.HomeWidgets)
            if (_cards.TryGetValue(w.InstanceId, out var card))
                MeasureNatural(w, card);
        Vm.RecalcGridGeometry();
        foreach (var w in Vm.HomeWidgets)
            if (_cards.TryGetValue(w.InstanceId, out var card))
                UpdateGeo(card, w);
    }

    private bool _relayoutQueued;
    private void ScheduleRelayout()
    {
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _relayoutQueued = false;
            RelayoutCards();
        });
    }

    private Border BuildWidgetCard(PlacedWidget w)
    {
        var card = new Border
        {
            Classes = { "card" },
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
        };
        UpdateGeo(card, w);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        // ---- 标题栏（可拖拽移动） ----
        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1D222B")),
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Padding = new Thickness(10, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = w.Icon, FontSize = 13 });
        titleRow.Children.Add(new TextBlock
        {
            Text = w.Title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E8EAED")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var sizeTag = new TextBlock
        {
            Text = w.SizeLabel,
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#7FE08C")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        titleRow.Children.Add(sizeTag);
        titleBar.Child = titleRow;
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        AttachMoveDrag(titleBar, w);

        // ---- 编辑工具条（调尺寸 / 删除） ----
        var toolbar = new WrapPanel { Margin = new Thickness(6, 4, 6, 2), IsVisible = Vm?.CustomizeOpen ?? false };
        toolbar.Bind(IsVisibleProperty, new Binding("CustomizeOpen") { Source = Vm });
        foreach (var (c, r) in new[] { (1, 1), (1, 2), (2, 1), (2, 2) })
        {
            var sb = new Button
            {
                Classes = { "secondary" },
                Padding = new Thickness(6, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Content = new TextBlock { Text = $"{c}×{r}", FontSize = 9 },
            };
            var cc = c; var rr = r;
            sb.Click += (_, _) => Vm?.ResizeWidget(w, cc, rr);
            toolbar.Children.Add(sb);
        }
        var del = new Button
        {
            Classes = { "danger" },
            Padding = new Thickness(8, 3),
            Content = new TextBlock { Text = "✕ 删除", FontSize = 9 },
            VerticalAlignment = VerticalAlignment.Top,
        };
        del.Click += (_, _) => Vm?.RemoveWidget(w);
        toolbar.Children.Add(del);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        // ---- 正文（内容始终完整显示：卡片高度随内容自适应，无需滚动） ----
        var body = new Border { Padding = new Thickness(4, 2, 4, 8), Child = BuildBody(w) };
        _cardBodies[w.InstanceId] = body;
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        card.Child = root;
        return card;
    }

    private void AttachMoveDrag(Border handle, PlacedWidget w)
    {
        // 非编辑主页模式下默认禁止拖动：光标随模式切换，拖动仅在编辑模式生效
        void RefreshCursor()
        {
            handle.Cursor = Vm?.CustomizeOpen == true
                ? new Cursor(StandardCursorType.Hand)
                : new Cursor(StandardCursorType.Arrow);
        }
        RefreshCursor();
        if (Vm != null)
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HomeViewModel.CustomizeOpen)) RefreshCursor();
            };

        handle.PointerPressed += (_, e) =>
        {
            if (Vm?.CustomizeOpen != true) return; // 非编辑主页模式下禁止拖动
            _pressPos = e.GetPosition(handle);
            _dragArmed = true; _dragActive = false;
            _dragSrc = DragSrc.Move;
            _dragMove = w;
            _dragOffsetX = _pressPos.X; _dragOffsetY = _pressPos.Y;
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (Vm?.CustomizeOpen != true) { CancelDrag(); return; } // 退出编辑模式立即中断拖拽
            if (_dragSrc != DragSrc.Move || !_dragArmed) return;
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) { CancelDrag(); return; }
            if (!_dragActive && Dist(e.GetPosition(handle), _pressPos) >= 8) _dragActive = true;
            if (_dragActive && _cards.TryGetValue(w.InstanceId, out var card))
            {
                var p = e.GetPosition(WidgetCanvas);
                double left = p.X - _dragOffsetX;
                double top = p.Y - _dragOffsetY;
                Canvas.SetLeft(card, left);
                Canvas.SetTop(card, top);
                card.Opacity = 0.7;

                // 拖动让位：按被拖卡片左上角落入的槽位，让其余卡片自动避让
                int stepX = (int)(WidgetGrid.CellW + WidgetGrid.Gap);
                int col = Math.Clamp((int)Math.Round(left / stepX), 0, WidgetGrid.Columns - w.Cols);
                int row = Vm.RowFromY(Math.Max(0, top));
                if (!(_avoidTarget is (int Row, int Col) t) || t.Row != row || t.Col != col)
                {
                    _avoidTarget = (row, col);
                    _avoidLayout = Vm?.ComputeAvoidLayout(w.InstanceId, row, col);
                    BeginAvoid();
                }
            }
        };
        handle.PointerReleased += (_, e) =>
        {
            if (_dragSrc != DragSrc.Move) return;
            var wasActive = _dragActive;
            var target = _dragMove;
            _avoidTimer?.Stop();
            _dragArmed = false; _dragActive = false;
            _dragSrc = DragSrc.None;
            _dragTypeKey = null; _dragPluginId = null; _dragMove = null;
            _avoidLayout = null; _avoidTarget = null;
            if (!wasActive || target == null || Vm == null) return;
            var pos = e.GetPosition(WidgetCanvas);
            if (!PointInCanvas(pos, Vm)) { RevertToCommitted(); return; }
            int stepX = (int)(WidgetGrid.CellW + WidgetGrid.Gap);
            double left = pos.X - _dragOffsetX, top = pos.Y - _dragOffsetY;
            int col = Math.Clamp((int)Math.Round(left / stepX), 0, WidgetGrid.Columns - target.Cols);
            int row = Vm.RowFromY(Math.Max(0, top));
            Vm.ApplyLayout(Vm.ComputeAvoidLayout(target.InstanceId, row, col));
            if (_cards.TryGetValue(target.InstanceId, out var card))
            {
                card.Opacity = 1.0;
                UpdateGeo(card, target);
            }
        };
        handle.PointerCaptureLost += (_, _) => CancelDrag();
    }

    /// <summary>开始让位动画（其余卡片平滑避让到计算出的新位置）。</summary>
    private void BeginAvoid()
    {
        if (_avoidTimer == null)
        {
            _avoidTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _avoidTimer.Tick += (_, _) => StepAvoid();
        }
        _avoidTimer.Start();
    }

    private void StepAvoid()
    {
        if (_avoidLayout == null || Vm == null) { _avoidTimer?.Stop(); return; }
        bool settled = true;
        foreach (var w in Vm.HomeWidgets)
        {
            if (_dragMove != null && w.InstanceId == _dragMove.InstanceId) continue;
            if (!_cards.TryGetValue(w.InstanceId, out var card)) continue;
            if (!_avoidLayout.TryGetValue(w.InstanceId, out var p)) continue;
            double tx = p.Col * (WidgetGrid.CellW + WidgetGrid.Gap);
            double ty = Vm.RowTop(p.Row);
            double cx = Canvas.GetLeft(card), cy = Canvas.GetTop(card);
            if (double.IsNaN(cx)) cx = 0;
            if (double.IsNaN(cy)) cy = 0;
            double nx = cx + (tx - cx) * 0.28;
            double ny = cy + (ty - cy) * 0.28;
            if (Math.Abs(nx - tx) < 0.6 && Math.Abs(ny - ty) < 0.6) { nx = tx; ny = ty; }
            else settled = false;
            Canvas.SetLeft(card, nx);
            Canvas.SetTop(card, ny);
        }
        if (settled) _avoidTimer?.Stop();
    }

    /// <summary>让所有卡片回到已提交（未发生让位提交）的模型位置。</summary>
    private void RevertToCommitted()
    {
        _avoidTimer?.Stop();
        _avoidLayout = null; _avoidTarget = null;
        if (Vm == null) return;
        foreach (var w in Vm.HomeWidgets)
            if (_cards.TryGetValue(w.InstanceId, out var c)) UpdateGeo(c, w);
    }

    private void CancelDrag()
    {
        _avoidTimer?.Stop();
        _avoidLayout = null; _avoidTarget = null;
        if (_dragSrc == DragSrc.Move && _dragMove != null &&
            _cards.TryGetValue(_dragMove.InstanceId, out var card))
        {
            card.Opacity = 1.0;
        }
        // 拖动期间只做了视图让位动画（未提交模型），取消时全部回到已提交位置
        if (Vm != null)
            foreach (var w in Vm.HomeWidgets)
                if (_cards.TryGetValue(w.InstanceId, out var c)) UpdateGeo(c, w);
        _dragArmed = false; _dragActive = false;
        _dragSrc = DragSrc.None;
        _dragTypeKey = null; _dragPluginId = null; _dragMove = null;
    }

    private static bool PointInCanvas(Point p, HomeViewModel vm)
        => p.X >= 0 && p.Y >= 0 && p.X <= vm.GridCanvasWidth && p.Y <= vm.GridCanvasHeight;

    // ================= 各种小组件正文 =================

    /// <summary>卡片正文：内容始终完整显示，卡片高度随内容自适应，无需卡片内滚动。</summary>
    private Control BuildBody(PlacedWidget w) => w.TypeKey switch
    {
        "greeting" => BodyGreeting(),
        "clock" => BodyClock(),
        "quick_launch" => BodyQuickLaunch(),
        "calendar" => BodyCalendar(),
        "pinned_instances" => BodyPinnedInstances(),
        "pinned_worlds" => BodyPinnedWorlds(),
        "servers" => BodyServers(),
        "recent" => BodyRecent(),
        "versions" => BodyVersions(),
        "lan" => BodyLan(),
        "saves" => BodySaves(),
        "monitor" => BodyMonitor(),
        "plugin_card" => BodyPluginCard(w),
        _ => new TextBlock { Text = "未知小组件", FontSize = 11 },
    };

    private TextBlock Txt(string text, double size = 11, string? color = null, FontWeight weight = FontWeight.Normal, bool wrap = false)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = color == null ? null : new SolidColorBrush(Color.Parse(color)),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    private static Control Panel(params Control[] children)
    {
        var s = new StackPanel { Spacing = 6 };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    private Button Btn(string text, EventHandler<RoutedEventArgs> click, string cls = "secondary", double fontSize = 10)
    {
        var b = new Button { Classes = { cls }, Padding = new Thickness(10, 5), Content = new TextBlock { Text = text, FontSize = fontSize } };
        b.Click += click;
        return b;
    }

    /// <summary>构造一个绑定到 Vm 集合的 ItemsControl + 行模板。</summary>
    private ItemsControl ListControl<T>(string itemsPath, Func<T, Control> row)
    {
        var ic = new ItemsControl { DataContext = Vm };
        ic.Bind(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
        ic.ItemTemplate = new FuncDataTemplate<T>((i, _) => row(i), true);
        return ic;
    }

    private Border Row(Control content)
        => new() { Classes = { "rowItem" }, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 6), Child = content };

    private Control BodyGreeting()
    {
        var date = Txt("", 16, "#E8EAED", FontWeight.SemiBold);
        var line = Txt("", 12, "#9AA0A6", FontWeight.Normal, true);
        date.Bind(TextBlock.TextProperty, new Binding("GreetingDate") { Source = Vm });
        line.Bind(TextBlock.TextProperty, new Binding("GreetingLine") { Source = Vm });
        var acc = Txt("", 10, "#7FE08C");
        acc.Bind(TextBlock.TextProperty, new Binding("AccountName") { Source = Vm, StringFormat = "账户：{0}" });
        return Panel(date, line, acc);
    }

    private Control BodyClock()
    {
        var time = Txt("", 26, "#7FE08C", FontWeight.Bold);
        var date = Txt("", 12, "#9AA0A6");
        time.Bind(TextBlock.TextProperty, new Binding("ClockNow") { Source = Vm });
        date.Bind(TextBlock.TextProperty, new Binding("ClockDate") { Source = Vm });
        return Panel(time, date);
    }

    private Control BodyQuickLaunch()
    {
        var box = new ComboBox { DataContext = Vm, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 220 };
        box.Bind(ComboBox.ItemsSourceProperty, new Binding("Instances") { Source = Vm });
        box.Bind(ComboBox.SelectedItemProperty, new Binding("SelectedEntry") { Source = Vm, Mode = BindingMode.TwoWay });
        box.ItemTemplate = new FuncDataTemplate<VersionEntry>((v, _) =>
            v == null ? Txt("") : Txt($"{v.Id}  ·  {v.Type}", 12, "#E8EAED", FontWeight.SemiBold), true);

        var launch = new Button { Classes = { "primary" }, Padding = new Thickness(16, 8), Content = Txt("启动游戏", 12) };
        launch.Bind(Button.CommandProperty, new Binding("LaunchCommand") { Source = Vm });
        launch.Bind(Button.IsEnabledProperty, new Binding("CanLaunch") { Source = Vm });

        return Panel(Txt("快捷启动", 12, "#9AA0A6"), box, launch);
    }

    private Control BodyCalendar()
    {
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        head.Children.Add(Txt("", 13, "#E8EAED", FontWeight.SemiBold));
        head.Children[0].Bind(TextBlock.TextProperty, new Binding("CalendarMonth") { Source = Vm });

        var weekHeader = new UniformGrid { Columns = 7, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
            weekHeader.Children.Add(new TextBlock { Text = d, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#5C6470")) });

        var weeks = new UniformGrid { Columns = 7 };

        var today = Txt("", 10, "#9AA0A6", FontWeight.Normal, true);
        today.Bind(TextBlock.TextProperty, new Binding("TodayPlayedText") { Source = Vm });

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(head);
        root.Children.Add(weekHeader);
        root.Children.Add(weeks);
        root.Children.Add(today);

        void Populate()
        {
            if (Vm == null) return;
            weeks.Children.Clear();
            foreach (var wl in Vm.CalendarWeeks)
            foreach (var d in wl)
            {
                var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(2, 3), Margin = new Thickness(1), DataContext = d };
                b.Bind(Border.BackgroundProperty, new Binding("IsToday") { Converter = ObjectConverters.IsTodayToBrush });
                var t = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };
                t.Bind(TextBlock.TextProperty, new Binding("DayText"));
                b.Child = t;
                weeks.Children.Add(b);
            }
        }
        Populate();
        if (Vm != null)
            Vm.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(HomeViewModel.CalendarWeeks)) Populate(); };
        return root;
    }

    private Control BodyPinnedInstances()
    {
        var ic = ListControl<PinnedInstance>("PinnedInstances", p =>
        {
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Txt(p.Id, 12, "#E8EAED", FontWeight.SemiBold));
            var last = Txt("", 9, "#9AA0A6");
            last.Bind(TextBlock.TextProperty, new Binding("LastPlayedText"));
            left.Children.Add(last);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var launch = new Button { Classes = { "primary" }, Padding = new Thickness(8, 3), Content = Txt("启动", 9) };
            launch.Bind(Button.CommandProperty, new Binding("LaunchPinnedCommand") { Source = Vm });
            launch.Bind(Button.CommandParameterProperty, new Binding("."));
            var star = new Button { Classes = { "secondary" }, Padding = new Thickness(7, 3), Content = Txt("★", 10) };
            star.Bind(Button.CommandProperty, new Binding("TogglePinInstanceCommand") { Source = Vm });
            star.Bind(Button.CommandParameterProperty, new Binding("."));
            btns.Children.Add(launch);
            btns.Children.Add(star);

            grid.Children.Add(left);
            grid.Children.Add(btns);
            Grid.SetColumn(btns, 1);
            return Row(grid);
        });
        return Panel(Txt("固定的实例", 12, "#9AA0A6"), ic);
    }

    private Control BodyPinnedWorlds()
    {
        var ic = ListControl<SaveInfo>("PinnedWorlds", s =>
        {
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Txt(s.Name, 12, "#E8EAED", FontWeight.SemiBold));
            left.Children.Add(Txt($"{s.Version} 存档", 9, "#7FE08C"));
            var un = new Button { Classes = { "secondary" }, Padding = new Thickness(7, 3), Content = Txt("取消固定", 9), VerticalAlignment = VerticalAlignment.Center };
            un.Bind(Button.CommandProperty, new Binding("PinWorldCommand") { Source = Vm });
            un.Bind(Button.CommandParameterProperty, new Binding("."));
            grid.Children.Add(left);
            grid.Children.Add(un);
            Grid.SetColumn(un, 1);
            return Row(grid);
        });
        return Panel(Txt("固定的世界", 12, "#9AA0A6"), ic);
    }

    private Control BodyServers()
    {
        var add = Btn("＋ 添加服务器", AddServer_Click, "secondary", 10);
        var ic = ListControl<ServerEntry>("Servers", s =>
        {
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Txt(s.Name, 12, "#E8EAED", FontWeight.SemiBold));
            var online = Txt("", 9, "#7FE08C");
            online.Bind(TextBlock.TextProperty, new Binding("OnlineText"));
            left.Children.Add(online);
            var addr = Txt(s.Address, 9, "#9AA0A6", FontWeight.Normal, true);
            var rm = new Button { Classes = { "danger" }, Padding = new Thickness(7, 3), Content = Txt("移除", 9), VerticalAlignment = VerticalAlignment.Center };
            rm.Bind(Button.CommandProperty, new Binding("RemoveServerCommand") { Source = Vm });
            rm.Bind(Button.CommandParameterProperty, new Binding("."));
            grid.Children.Add(left);
            grid.Children.Add(addr);
            grid.Children.Add(rm);
            Grid.SetColumn(addr, 1);
            Grid.SetColumn(rm, 2);
            return Row(grid);
        });
        return Panel(Txt("固定的服务器", 12, "#9AA0A6"), add, ic);
    }

    private Control BodyRecent()
    {
        var ic = ListControl<RecentProject>("RecentProjects", p =>
        {
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            nameRow.Children.Add(Txt(p.Name, 12, "#E8EAED", FontWeight.SemiBold));
            var tag = new Border { Classes = { "badge" }, Padding = new Thickness(5, 1), Child = Txt(p.Tag, 8, "#7FE08C") };
            nameRow.Children.Add(tag);
            left.Children.Add(nameRow);
            var last = Txt("", 9, "#9AA0A6");
            last.Bind(TextBlock.TextProperty, new Binding("LastPlayedText"));
            left.Children.Add(last);

            var go = new Button { Classes = { "primary" }, Padding = new Thickness(9, 3), VerticalAlignment = VerticalAlignment.Center };
            go.Bind(Button.CommandProperty, new Binding("LaunchRecentCommand") { Source = Vm });
            go.Bind(Button.CommandParameterProperty, new Binding("."));
            var gotext = new TextBlock { FontSize = 9 };
            gotext.Bind(TextBlock.TextProperty, new Binding("IsServer") { Converter = ObjectConverters.IsServerToAction });
            go.Content = gotext;

            grid.Children.Add(left);
            grid.Children.Add(go);
            Grid.SetColumn(go, 1);
            return Row(grid);
        });
        return Panel(Txt("从最近的项目开始", 12, "#9AA0A6"), ic);
    }

    private Control BodyVersions()
    {
        var box = new ComboBox { DataContext = Vm, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 200 };
        box.Bind(ComboBox.ItemsSourceProperty, new Binding("DownloadVersions") { Source = Vm });
        box.Bind(ComboBox.SelectedItemProperty, new Binding("SelectedDownloadVersion") { Source = Vm, Mode = BindingMode.TwoWay });
        box.ItemTemplate = new FuncDataTemplate<VersionEntry>((v, _) =>
        {
            if (v == null) return Txt("");
            var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            s.Children.Add(Txt(v.Id, 12, "#E8EAED", FontWeight.SemiBold));
            s.Children.Add(Txt(v.TypeLabel, 9, "#7FE08C"));
            return s;
        }, true);

        var src = Txt("", 9, "#9AA0A6");
        src.Bind(TextBlock.TextProperty, new Binding("DownloadSourceText") { Source = Vm });
        var status = Txt("", 9, "#9AA0A6", FontWeight.Normal, true);
        status.Bind(TextBlock.TextProperty, new Binding("DownloadStatus") { Source = Vm });

        var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var btn = (string t, string cmd, string? en = null) =>
        {
            var b = new Button { Classes = { t == "下载" ? "primary" : "secondary" }, Padding = new Thickness(10, 4), Content = Txt(t, 10), Margin = new Thickness(0, 0, 6, 4) };
            b.Bind(Button.CommandProperty, new Binding(cmd) { Source = Vm });
            if (en != null) b.Bind(Button.IsEnabledProperty, new Binding(en) { Source = Vm });
            return b;
        };
        bar.Children.Add(btn("刷新清单", "RefreshVersionsCommand", "!DownloadBusy"));
        bar.Children.Add(btn("下载", "DownloadVersionCommand", "CanDownloadVersion"));
        bar.Children.Add(btn("启动", "LaunchDownloadedCommand", "CanLaunchDownloaded"));
        bar.Children.Add(btn("打开目录", "OpenDownloadedDirCommand", "CanLaunchDownloaded"));

        return Panel(Txt("版本下载", 12, "#9AA0A6"), box, src, status, bar);
    }

    private Control BodyLan()
    {
        var status = Txt("", 11, "#E8EAED", FontWeight.SemiBold);
        status.Bind(TextBlock.TextProperty, new Binding("LanStatus") { Source = Vm });
        var ip = Txt("", 15, "#7FE08C", FontWeight.SemiBold);
        ip.Bind(TextBlock.TextProperty, new Binding("LanIp") { Source = Vm });

        var copy = Btn("复制 IP", CopyLanIp_Click, "secondary", 10);
        var go = new Button { Classes = { "primary" }, Padding = new Thickness(12, 6), Content = Txt("进入虚拟局域网页面", 10), HorizontalAlignment = HorizontalAlignment.Stretch };
        go.Bind(Button.CommandProperty, new Binding("GoLanCommand") { Source = Vm });

        return Panel(Txt("虚拟局域网", 12, "#9AA0A6"), status, ip, copy, go);
    }

    private Control BodySaves()
    {
        var ic = ListControl<SaveInfo>("Saves", s =>
        {
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Txt(s.Name, 12, "#E8EAED", FontWeight.SemiBold));
            left.Children.Add(Txt(s.SizeText, 9, "#9AA0A6"));
            var star = new Button { Classes = { "secondary" }, Padding = new Thickness(6, 3), Content = Txt("★", 10), VerticalAlignment = VerticalAlignment.Center };
            star.Bind(Button.CommandProperty, new Binding("PinWorldCommand") { Source = Vm });
            star.Bind(Button.CommandParameterProperty, new Binding("."));
            var open = new Button { Classes = { "secondary" }, Padding = new Thickness(6, 3), Content = Txt("打开", 9), VerticalAlignment = VerticalAlignment.Center };
            open.Bind(Button.CommandProperty, new Binding("OpenSaveCommand") { Source = Vm });
            open.Bind(Button.CommandParameterProperty, new Binding("."));
            grid.Children.Add(left);
            grid.Children.Add(star);
            grid.Children.Add(open);
            Grid.SetColumn(star, 1);
            Grid.SetColumn(open, 2);
            return Row(grid);
        });
        return Panel(Txt("我的存档", 12, "#9AA0A6"), ic);
    }

    private Control BodyMonitor()
    {
        var run = Txt("", 11, "#7FE08C", FontWeight.SemiBold);
        run.Bind(TextBlock.TextProperty, new Binding("MonitorRunningText") { Source = Vm });
        var cpu = Txt("", 13, "#E8EAED");
        cpu.Bind(TextBlock.TextProperty, new Binding("MonitorCpuText") { Source = Vm, StringFormat = "CPU：{0}" });
        var mem = Txt("", 13, "#E8EAED");
        mem.Bind(TextBlock.TextProperty, new Binding("MonitorMemText") { Source = Vm, StringFormat = "内存：{0}" });
        var th = Txt("", 13, "#E8EAED");
        th.Bind(TextBlock.TextProperty, new Binding("MonitorThreadsText") { Source = Vm, StringFormat = "线程：{0}" });
        return Panel(Txt("性能监控（游戏运行采样）", 12, "#9AA0A6"), run, cpu, mem, th);
    }

    private Control BodyPluginCard(PlacedWidget w)
    {
        var info = PluginService.RegisteredHomeCards.FirstOrDefault(c => c.Id == w.PluginCardId);
        var card = new StackPanel { Spacing = 4 };
        var title = Txt(info?.Title ?? w.Title, 13, "#E8EAED", FontWeight.SemiBold);
        var body = Txt(info?.Body ?? "", 10, "#9AA0A6", FontWeight.Normal, true);
        card.Children.Add(title);
        card.Children.Add(body);
        if (info != null)
            info.BodyChanged += () => { body.Text = info.Body; ScheduleRelayout(); };
        return card;
    }

    // ================= 对话框 / 复制 =================

    private async void AddServer_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = new TextBox { Watermark = "服务器名称，如 我的生存服", Width = 380 };
        var addr = new TextBox { Watermark = "地址:端口，如 play.example.com:25565", Width = 380, Margin = new Thickness(0, 8, 0, 0) };
        var ver = new TextBox { Watermark = "MC 版本（可选）", Width = 380, Margin = new Thickness(0, 8, 0, 0) };

        var dlg = new Window
        {
            Title = "添加服务器",
            Width = 460,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    name, addr, ver,
                    new Button { Content = "保存", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) },
                },
            },
        };
        var ok = (Button)((StackPanel)dlg.Content).Children[3];
        ok.Click += (_, _) =>
        {
            Vm.AddServerCommand.Execute($"{name.Text}|{addr.Text}|{ver.Text}");
            dlg.Close();
        };
        await dlg.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? (Window)this.VisualRoot!);
    }

    private async void CopyLanIp_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm == null || Vm.LanIp.StartsWith("未加入") || Vm.LanIp.StartsWith("未配置") || Vm.LanIp.StartsWith("尚未")) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard != null)
            await top.Clipboard.SetTextAsync(Vm.LanIp);
        Vm.Status = $"已复制虚拟 IP {Vm.LanIp}，发到房间内给好友，好友在游戏内通过虚拟 IP 连接";
    }
}
