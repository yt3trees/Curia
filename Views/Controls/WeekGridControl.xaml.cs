using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Curia.Models;
using Curia.Services;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;
using MouseEventArgs    = System.Windows.Input.MouseEventArgs;
using DragEventArgs     = System.Windows.DragEventArgs;
using DragDropEffects   = System.Windows.DragDropEffects;
using Point             = System.Windows.Point;
using Color             = System.Windows.Media.Color;
using Brush             = System.Windows.Media.Brush;
using Brushes           = System.Windows.Media.Brushes;
using Rectangle         = System.Windows.Shapes.Rectangle;
using Cursors           = System.Windows.Input.Cursors;
using ListBox           = System.Windows.Controls.ListBox;
using ListBoxItem       = System.Windows.Controls.ListBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Curia.Views.Controls;

/// <summary>
/// 週スケジュール グリッドの描画とインタラクション。
/// ViewModel を ViewModel プロパティ経由で受け取り、
/// TimedBlocks / AllDayBlocks の変化を監視して再描画する。
/// </summary>
public partial class WeekGridControl : WpfUserControl
{
    // --- 定数 ---
    private const double SlotHeight = 24.0;   // 1スロット(30分) の高さ (px)
    private const double SlotMinutes = 30.0;
    private const int    SlotCount = 48;       // 1日のスロット数
    private const double TimeColWidth = 60.0;  // 時刻ラベル列の幅
    private const double HeaderHeight = 36.0;
    private const double AllDayLaneHeight = 28.0; // 1レーンの高さ

    // --- ViewModel ---
    private WeeklyScheduleViewModel? _vm;

    public WeeklyScheduleViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm != null)
            {
                _vm.TimedBlocks.CollectionChanged   -= OnBlocksChanged;
                _vm.AllDayBlocks.CollectionChanged  -= OnBlocksChanged;
                _vm.OutlookEvents.CollectionChanged -= OnOutlookEventsChanged;
            }
            _vm = value;
            if (_vm != null)
            {
                _vm.TimedBlocks.CollectionChanged   += OnBlocksChanged;
                _vm.AllDayBlocks.CollectionChanged  += OnBlocksChanged;
                _vm.OutlookEvents.CollectionChanged += OnOutlookEventsChanged;
            }
            Refresh();
        }
    }

    // --- 状態 ---
    private double _dayColWidth;
    private DispatcherTimer? _clockTimer;

    // Outlook イベントコールバック (WeeklySchedulePage がセット)
    public Action<Curia.Models.OutlookEvent>? OnLogMeetingNotes { get; set; }
    public Action<string>? OnOpenAsanaUrl { get; set; }
    public Action<Curia.Models.ProjectInfo, string>? OnOpenInEditorCallback { get; set; }

    // タスク選択ポップアップ
    private Popup? _taskPickerPopup;
    private DateTime _pendingTimedStart;
    private int _pendingTimedSlots;

    // 時間範囲ドラッグ
    private bool _isDraggingTimeRange;
    private int _dragStartSlot;
    private int _dragStartDay;
    private Rectangle? _selectionRect;

    // ブロック移動ドラッグ
    private ScheduleBlock? _movingBlock;
    private Point _moveStartPoint;
    private double _moveStartCanvasTop;
    private double _moveStartCanvasLeft;
    private bool _isMovingAllDay;

    // AllDay 範囲ドラッグ (空セルをマウスで横にスワイプ → タスク選択)
    private bool _isDraggingAllDayRange;
    private int _allDayDragStartDay;
    private Popup? _allDayTaskPickerPopup;
    private DateTime _pendingAllDayStart;

    // Outlook イベントツールチップ: ホバー中のイベント切り替え検知用
    private Curia.Models.OutlookEvent? _lastTimedTooltipEvent;
    private Curia.Models.OutlookEvent? _lastAllDayTooltipEvent;
    private DateTime _pendingAllDayEnd;

    // --- カラーマップ ---
    private static readonly Dictionary<string, Color> ColorMap = new()
    {
        ["blue"]   = Color.FromRgb(0x00, 0x78, 0xD4),
        ["green"]  = Color.FromRgb(0x10, 0x7C, 0x10),
        ["orange"] = Color.FromRgb(0xFF, 0x8C, 0x00),
        ["purple"] = Color.FromRgb(0x87, 0x64, 0xB8),
        ["teal"]   = Color.FromRgb(0x00, 0xB2, 0x94),
        ["pink"]   = Color.FromRgb(0xC2, 0x39, 0xB3),
        ["cyan"]   = Color.FromRgb(0x00, 0xB7, 0xC3),
        ["red"]    = Color.FromRgb(0xE8, 0x11, 0x23),
    };

    public WeekGridControl()
    {
        InitializeComponent();
    }

    // ─── ライフサイクル ──────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clockTimer.Tick += (_, _) => RenderCurrentTimeIndicator();
        _clockTimer.Start();

        SetupOutlookTooltips();
        Refresh();
        ScrollToCurrentTime();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _clockTimer?.Stop();
        _clockTimer = null;
        if (_vm != null)
        {
            _vm.TimedBlocks.CollectionChanged   -= OnBlocksChanged;
            _vm.AllDayBlocks.CollectionChanged  -= OnBlocksChanged;
            _vm.OutlookEvents.CollectionChanged -= OnOutlookEventsChanged;
        }
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Refresh();

    private void OnOutlookEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Refresh();

    // ─── リフレッシュ ────────────────────────────────────────────────

    public void Refresh()
    {
        if (!IsLoaded) return;
        _dayColWidth = (RootGrid.ActualWidth - TimeColWidth) / 7.0;
        if (_dayColWidth <= 0) return;

        DrawHeader();
        DrawAllDayLane();
        DrawTimeGrid();
        RenderTimedBlocks();
        RenderOutlookTimedEvents();
        RenderCurrentTimeIndicator();
    }

    private void OnTimeGridSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();
    private void OnAllDaySizeChanged(object sender, SizeChangedEventArgs e) => DrawAllDayLane();

    // ─── ヘッダー描画 ───────────────────────────────────────────────

    private void DrawHeader()
    {
        HeaderCanvas.Children.Clear();
        if (_vm == null) return;

        var weekStart = _vm.WeekStart;
        var today = DateTime.Today;

        // 時刻列プレースホルダ (60px)
        var timePlaceholder = new TextBlock
        {
            Text = "Week",
            Foreground = GetBrush("AppSubtext0"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = TimeColWidth,
        };
        Canvas.SetLeft(timePlaceholder, 0);
        Canvas.SetTop(timePlaceholder, 10);
        HeaderCanvas.Children.Add(timePlaceholder);

        // 曜日ヘッダー
        var dayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        for (int i = 0; i < 7; i++)
        {
            var date = weekStart.AddDays(i);
            var isToday = date.Date == today;

            var container = new Border
            {
                Width = _dayColWidth,
                Height = HeaderHeight,
                Child = new TextBlock
                {
                    Text = $"{dayNames[i]} {date:MM/dd}",
                    FontSize = 12,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isToday
                        ? GetBrush("AppBlue")
                        : GetBrush(i >= 5 ? "AppSubtext0" : "AppText"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            Canvas.SetLeft(container, TimeColWidth + i * _dayColWidth);
            Canvas.SetTop(container, 0);
            HeaderCanvas.Children.Add(container);
        }
    }

    // ─── All-day レーン描画 ──────────────────────────────────────────

    private void DrawAllDayLane()
    {
        AllDayBgCanvas.Children.Clear();
        AllDayBlockCanvas.Children.Clear();
        OutlookAllDayCanvas.Children.Clear();

        if (_vm == null) return;

        var allDayBlocks = _vm.AllDayBlocks.ToList();
        var outlookAllDay = _vm.OutlookEvents.Where(e => e.IsAllDay).ToList();

        var laneCount = AssignAllDayLanes(allDayBlocks);
        var outlookLaneCount = AssignOutlookAllDayLanes(outlookAllDay);
        var totalLanes = Math.Max(1, laneCount + outlookLaneCount);
        var laneHeight = totalLanes * AllDayLaneHeight;

        AllDayBorder.Height = laneHeight + 2;

        // 背景: 縦線と "All-day" ラベル
        var allDayLabel = new TextBlock
        {
            Text = "All-day",
            FontSize = 10,
            Foreground = GetBrush("AppSubtext0"),
            Width = TimeColWidth - 4,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Canvas.SetLeft(allDayLabel, 0);
        Canvas.SetTop(allDayLabel, 6);
        AllDayBgCanvas.Children.Add(allDayLabel);

        for (int i = 0; i <= 7; i++)
        {
            var line = new Line
            {
                X1 = TimeColWidth + i * _dayColWidth,
                Y1 = 0,
                X2 = TimeColWidth + i * _dayColWidth,
                Y2 = laneHeight,
                Stroke = GetBrush("AppSurface2"),
                StrokeThickness = 0.5,
            };
            AllDayBgCanvas.Children.Add(line);
        }

        // AllDay ブロックカード
        var weekStart = _vm.WeekStart;
        var weekEnd = weekStart.AddDays(7);
        foreach (var block in allDayBlocks)
        {
            if (!block.StartDate.HasValue || !block.EndDate.HasValue) continue;

            // 週境界クリップ
            var drawStart = block.StartDate.Value.Date < weekStart ? weekStart : block.StartDate.Value.Date;
            var drawEnd   = block.EndDate.Value.Date >= weekEnd ? weekEnd.AddDays(-1) : block.EndDate.Value.Date;
            if (drawStart > drawEnd) continue;

            var startDayIdx = (drawStart - weekStart).Days;
            var spanDays = (drawEnd - drawStart).Days + 1;

            bool continuesLeft  = block.StartDate.Value.Date < weekStart;
            bool continuesRight = block.EndDate.Value.Date >= weekEnd;

            int laneIdx = GetLaneIndex(block);
            var card = CreateAllDayCard(block, spanDays, continuesLeft, continuesRight);

            Canvas.SetLeft(card, TimeColWidth + startDayIdx * _dayColWidth + 2);
            Canvas.SetTop(card, laneIdx * AllDayLaneHeight + 2);
            AllDayBlockCanvas.Children.Add(card);
        }

        // Outlook 終日イベント (Curia レーンの下に続けて描画)
        foreach (var ev in outlookAllDay)
        {
            var drawStart = ev.Start.Date < weekStart ? weekStart : ev.Start.Date;
            var drawEnd   = ev.End.Date > weekEnd ? weekEnd.AddDays(-1) : ev.End.AddDays(-1).Date;
            if (ev.IsAllDay && ev.End.Date == ev.Start.Date.AddDays(1))
                drawEnd = ev.Start.Date; // 終日イベントの End は翌日 0:00 のため調整

            drawEnd = drawEnd < drawStart ? drawStart : drawEnd;
            if (drawStart >= weekEnd) continue;

            var startDayIdx = (drawStart - weekStart).Days;
            var spanDays = (drawEnd - drawStart).Days + 1;
            bool continuesLeft  = ev.Start.Date < weekStart;
            bool continuesRight = ev.End.Date > weekEnd;

            int laneIdx = GetOutlookAllDayLane(ev) + laneCount; // Curia レーンの下に続ける
            var card = CreateOutlookAllDayCard(ev, spanDays, continuesLeft, continuesRight);

            Canvas.SetLeft(card, TimeColWidth + startDayIdx * _dayColWidth + 2);
            Canvas.SetTop(card, laneIdx * AllDayLaneHeight + 2);
            OutlookAllDayCanvas.Children.Add(card);
        }
    }

    // ─── 時刻グリッド描画 ────────────────────────────────────────────

    private void DrawTimeGrid()
    {
        TimeGridBgCanvas.Children.Clear();
        TimeGridBgCanvas.Width = TimeGridContainer.ActualWidth;
        TimeGridBgCanvas.Height = SlotCount * SlotHeight;

        if (_vm == null) return;

        var today = DateTime.Today;
        var weekStart = _vm.WeekStart;

        // 定時外 (09:00 前 / 18:00 以降) を暗くするオーバーレイ
        const int WorkStartSlot = 9 * 2;  // 09:00 = slot 18
        const int WorkEndSlot   = 18 * 2; // 18:00 = slot 36
        double offHoursDimWidth = TimeGridContainer.ActualWidth - TimeColWidth;
        var offHoursBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0x00, 0x00));

        // 00:00 〜 09:00
        var preWorkRect = new Rectangle
        {
            Width  = offHoursDimWidth,
            Height = WorkStartSlot * SlotHeight,
            Fill   = offHoursBrush,
        };
        Canvas.SetLeft(preWorkRect, TimeColWidth);
        Canvas.SetTop(preWorkRect, 0);
        TimeGridBgCanvas.Children.Add(preWorkRect);

        // 18:00 〜 24:00
        var postWorkRect = new Rectangle
        {
            Width  = offHoursDimWidth,
            Height = (SlotCount - WorkEndSlot) * SlotHeight,
            Fill   = offHoursBrush,
        };
        Canvas.SetLeft(postWorkRect, TimeColWidth);
        Canvas.SetTop(postWorkRect, WorkEndSlot * SlotHeight);
        TimeGridBgCanvas.Children.Add(postWorkRect);

        // 今日の列を薄くハイライト
        for (int d = 0; d < 7; d++)
        {
            var date = weekStart.AddDays(d);
            if (date.Date == today)
            {
                var todayBg = new Rectangle
                {
                    Width = _dayColWidth,
                    Height = SlotCount * SlotHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x78, 0xD4)),
                };
                Canvas.SetLeft(todayBg, TimeColWidth + d * _dayColWidth);
                Canvas.SetTop(todayBg, 0);
                TimeGridBgCanvas.Children.Add(todayBg);
            }
        }

        // 水平線 (30分ごと)
        for (int slot = 0; slot <= SlotCount; slot++)
        {
            double y = slot * SlotHeight;
            bool isHour = slot % 2 == 0;

            var line = new Line
            {
                X1 = isHour ? 0 : TimeColWidth,
                Y1 = y,
                X2 = TimeGridContainer.ActualWidth,
                Y2 = y,
                Stroke = GetBrush("AppSurface2"),
                StrokeThickness = isHour ? 0.7 : 0.3,
            };
            TimeGridBgCanvas.Children.Add(line);

            // 時刻ラベル (1時間ごと)
            if (isHour && slot < SlotCount)
            {
                var hour = slot / 2;
                var label = new TextBlock
                {
                    Text = $"{hour:D2}:00",
                    FontSize = 10,
                    Foreground = GetBrush("AppSubtext0"),
                    Width = TimeColWidth - 6,
                    TextAlignment = TextAlignment.Right,
                };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 7);
                TimeGridBgCanvas.Children.Add(label);
            }
        }

        // 縦線 (曜日境界)
        for (int d = 0; d <= 7; d++)
        {
            double x = TimeColWidth + d * _dayColWidth;
            var line = new Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = SlotCount * SlotHeight,
                Stroke = GetBrush("AppSurface2"),
                StrokeThickness = 0.5,
            };
            TimeGridBgCanvas.Children.Add(line);
        }
    }

    // ─── Timed ブロック描画 ──────────────────────────────────────────

    private void RenderTimedBlocks()
    {
        TimeBlockCanvas.Children.Clear();
        if (_vm == null) return;

        var weekStart = _vm.WeekStart;

        foreach (var block in _vm.TimedBlocks)
        {
            if (!block.StartAt.HasValue) continue;

            var startAt = block.StartAt.Value;
            if (startAt.Date < weekStart || startAt.Date >= weekStart.AddDays(7)) continue;

            int dayIdx = (startAt.Date - weekStart.Date).Days;
            int slotIdx = (int)(startAt.TimeOfDay.TotalMinutes / 30);
            double top = slotIdx * SlotHeight;
            double left = TimeColWidth + dayIdx * _dayColWidth;
            double height = block.DurationSlots * SlotHeight;
            double width = _dayColWidth - 2;

            var card = CreateTimedCard(block, width, height);
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            TimeBlockCanvas.Children.Add(card);
        }
    }

    // ─── Outlook Timed イベント描画 ──────────────────────────────────

    private void RenderOutlookTimedEvents()
    {
        OutlookCanvas.Children.Clear();
        if (_vm == null) return;

        var weekStart = _vm.WeekStart;
        var timedEvents = _vm.OutlookEvents.Where(e => !e.IsAllDay).ToList();

        foreach (var ev in timedEvents)
        {
            var startAt = ev.Start;
            var endAt   = ev.End;

            // 週範囲チェック
            if (startAt.Date >= weekStart.AddDays(7) || endAt <= weekStart) continue;

            // 開始が週の前なら月曜 00:00 にクリップ
            if (startAt < weekStart) startAt = weekStart;
            // 終了が週を超えるなら日曜 24:00 にクリップ
            if (endAt > weekStart.AddDays(7)) endAt = weekStart.AddDays(7);

            int dayIdx = (startAt.Date - weekStart.Date).Days;
            double top = startAt.TimeOfDay.TotalMinutes / SlotMinutes * SlotHeight;
            double height = Math.Max(SlotHeight, (endAt - startAt).TotalMinutes / SlotMinutes * SlotHeight);
            double left = TimeColWidth + dayIdx * _dayColWidth + 1;
            double width = _dayColWidth - 4;

            var card = CreateOutlookTimedCard(ev, width, height);
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            OutlookCanvas.Children.Add(card);
        }
    }

    private Border CreateOutlookTimedCard(OutlookEvent ev, double width, double height)
    {
        // 半透明グレー背景 (読み取り専用を視覚的に表現)
        var bgColor = Color.FromArgb(0x55, 0x6E, 0x76, 0x81);
        var borderColor = ev.HasLinkedTask
            ? Color.FromArgb(0xCC, 0x00, 0x78, 0xD4)  // リンク済み: アクセントカラー
            : Color.FromArgb(0x99, 0x6E, 0x76, 0x81);

        var timeText = $"{ev.Start:HH:mm} - {ev.End:HH:mm}";

        var subjectBlock = new TextBlock
        {
            Text = ev.Subject,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Foreground = GetBrush("AppSubtext0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(4, 0, 4, 0),
        };

        var timeBlock = new TextBlock
        {
            Text = timeText,
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0x8B, 0x94, 0x9E)),
            Margin = new Thickness(4, 0, 4, 0),
        };

        var badgeText = ev.HasLinkedTask ? "⛓" : "OL";
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0x6E, 0x76, 0x81)),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Child = new TextBlock
            {
                Text = badgeText,
                FontSize = 9,
                Foreground = ev.HasLinkedTask ? GetBrush("AppBlue") : GetBrush("AppSubtext0"),
            },
        };

        var content = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        content.Children.Add(badge);

        var textPanel = new StackPanel();
        textPanel.Children.Add(subjectBlock);
        if (height >= 40) textPanel.Children.Add(timeBlock);
        content.Children.Add(textPanel);

        var card = new Border
        {
            Width = Math.Max(4, width),
            Height = height,
            Background = new SolidColorBrush(bgColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            IsHitTestVisible = ev.HasLinkedTask,
            Child = content,
        };

        if (ev.HasLinkedTask)
            card.ContextMenu = BuildOutlookEventContextMenu(ev);

        return card;
    }

    private Border CreateOutlookAllDayCard(OutlookEvent ev, int spanDays,
        bool continuesLeft, bool continuesRight)
    {
        var title = new TextBlock
        {
            Text = (continuesLeft ? "◀ " : "") + ev.Subject + (continuesRight ? " ▶" : ""),
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0x8B, 0x94, 0x9E)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };

        var allDayCard = new Border
        {
            Width = Math.Max(4, spanDays * _dayColWidth - 4),
            Height = AllDayLaneHeight - 4,
            Background = new SolidColorBrush(Color.FromArgb(0x44, 0x6E, 0x76, 0x81)),
            BorderBrush = new SolidColorBrush(ev.HasLinkedTask
                ? Color.FromArgb(0xCC, 0x00, 0x78, 0xD4)
                : Color.FromArgb(0x66, 0x6E, 0x76, 0x81)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(
                continuesLeft ? 0 : 3,
                continuesRight ? 0 : 3,
                continuesRight ? 0 : 3,
                continuesLeft ? 0 : 3),
            ClipToBounds = true,
            IsHitTestVisible = ev.HasLinkedTask,
            Child = title,
        };
        if (ev.HasLinkedTask)
            allDayCard.ContextMenu = BuildOutlookEventContextMenu(ev);
        return allDayCard;
    }

    private void RenderCurrentTimeIndicator()
    {
        // 既存のインジケータを削除 (Line + Ellipse の両方を対象にする)
        for (int i = TimeBlockCanvas.Children.Count - 1; i >= 0; i--)
            if (TimeBlockCanvas.Children[i] is FrameworkElement fe && fe.Tag is string s && s == "CurrentTime")
                TimeBlockCanvas.Children.RemoveAt(i);

        if (_vm == null) return;

        var today = DateTime.Today;
        var weekStart = _vm.WeekStart;
        if (today < weekStart || today >= weekStart.AddDays(7)) return;

        int dayIdx = (today - weekStart).Days;
        var now = DateTime.Now.TimeOfDay;
        double top = now.TotalMinutes / SlotMinutes * SlotHeight;
        double left = TimeColWidth + dayIdx * _dayColWidth;

        var line = new Line
        {
            Tag = "CurrentTime",
            X1 = left - 4,
            Y1 = top,
            X2 = left + _dayColWidth,
            Y2 = top,
            Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            StrokeThickness = 2,
        };
        var dot = new Ellipse
        {
            Tag = "CurrentTime",
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
        };
        Canvas.SetLeft(dot, left - 8);
        Canvas.SetTop(dot, top - 4);
        TimeBlockCanvas.Children.Add(line);
        TimeBlockCanvas.Children.Add(dot);
    }

    private void ScrollToCurrentTime()
    {
        // 7:30 を先頭に表示 (slot 15 = 7.5h * 2)
        const double startHour = 7.5;
        var targetTop = startHour * 60 / SlotMinutes * SlotHeight;
        TimeScrollViewer.ScrollToVerticalOffset(targetTop);
    }

    // ─── カード生成: Timed ───────────────────────────────────────────

    private Border CreateTimedCard(ScheduleBlock block, double width, double height)
    {
        var color = GetBlockColor(block.ColorKey);
        var bg = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));
        var border = new SolidColorBrush(color);

        var titleBlock = new TextBlock
        {
            Text = block.TitleSnapshot,
            FontSize = 11,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(3, 0, 3, 0),
        };

        var timeBlock = new TextBlock
        {
            Text = GetTimedBlockTimeText(block),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(3, 0, 3, 0),
        };

        var content = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        content.Children.Add(titleBlock);
        if (height >= 40) content.Children.Add(timeBlock);

        // cardRef は後で card を代入する前方参照。クロージャが card を間接キャプチャする。
        Border? cardRef = null;

        // リサイズ Thumb (上端) — DragDelta でカード直接更新、DragCompleted でモデル確定
        var topThumb = new Thumb
        {
            Height = 5,
            Cursor = Cursors.SizeNS,
            Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        topThumb.DragDelta += (s, e) =>
        {
            if (cardRef == null) return;
            double curTop    = Canvas.GetTop(cardRef);
            double curHeight = cardRef.Height;
            double newTop    = curTop + e.VerticalChange;
            double snapped   = Math.Round(newTop / SlotHeight) * SlotHeight;
            double newHeight = curHeight - (snapped - curTop);
            if (newHeight < SlotHeight || snapped < 0) return;
            Canvas.SetTop(cardRef, snapped);
            cardRef.Height = newHeight;
        };
        topThumb.DragCompleted += (s, e) =>
        {
            if (cardRef == null || _vm == null) return;
            double top  = Canvas.GetTop(cardRef);
            double left = Canvas.GetLeft(cardRef);
            int dayIdx  = Math.Max(0, Math.Min(6, (int)Math.Round((left - TimeColWidth) / _dayColWidth)));
            int slotIdx = Math.Max(0, Math.Min(SlotCount - 1, (int)Math.Round(top / SlotHeight)));
            int slots   = Math.Max(1, (int)Math.Round(cardRef.Height / SlotHeight));
            _vm.ResizeTimedBlock(block.Id,
                _vm.WeekStart.AddDays(dayIdx).AddMinutes(slotIdx * SlotMinutes), slots);
        };

        // リサイズ Thumb (下端)
        var bottomThumb = new Thumb
        {
            Height = 5,
            Cursor = Cursors.SizeNS,
            Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        bottomThumb.DragDelta += (s, e) =>
        {
            if (cardRef == null) return;
            double snapped = Math.Round((cardRef.Height + e.VerticalChange) / SlotHeight) * SlotHeight;
            if (snapped < SlotHeight) return;
            cardRef.Height = snapped;
        };
        bottomThumb.DragCompleted += (s, e) =>
        {
            if (cardRef == null || _vm == null) return;
            int slots = Math.Max(1, (int)Math.Round(cardRef.Height / SlotHeight));
            _vm.ResizeTimedBlock(block.Id, null, slots);
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        Grid.SetRow(topThumb, 0);
        Grid.SetRow(content, 1);
        Grid.SetRow(bottomThumb, 2);
        grid.Children.Add(topThumb);
        grid.Children.Add(content);
        grid.Children.Add(bottomThumb);

        var card = new Border
        {
            Width = width,
            Height = height,
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Cursor = Cursors.SizeAll,
            Child = grid,
        };

        // forward-reference クロージャへの代入: この行より上の DragDelta/DragCompleted が card を参照できる
        cardRef = card;

        card.MouseLeftButtonDown += (s, e) => OnTimedCardMouseDown(card, block, e);
        card.ContextMenu = BuildBlockContextMenu(block);
        card.ToolTip = BuildBlockToolTip(block);
        ToolTipService.SetShowDuration(card, 10000);
        ToolTipService.SetInitialShowDelay(card, 400);
        return card;
    }

    // ─── カード生成: AllDay ──────────────────────────────────────────

    private Border CreateAllDayCard(ScheduleBlock block, int spanDays,
        bool continuesLeft, bool continuesRight)
    {
        var color = GetBlockColor(block.ColorKey);
        var bg = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));

        double width = spanDays * _dayColWidth - 4;

        var title = new TextBlock
        {
            Text = (continuesLeft ? "◀ " : "") + block.TitleSnapshot + (continuesRight ? " ▶" : ""),
            FontSize = 11,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };

        var card = new Border
        {
            Width = Math.Max(4, width),
            Height = AllDayLaneHeight - 4,
            Background = bg,
            CornerRadius = new CornerRadius(
                continuesLeft ? 0 : 3,
                continuesRight ? 0 : 3,
                continuesRight ? 0 : 3,
                continuesLeft ? 0 : 3),
            ClipToBounds = true,
            Cursor = Cursors.SizeAll,
            Child = title,
        };

        card.MouseLeftButtonDown += (s, e) => OnAllDayCardMouseDown(card, block, e);
        card.ContextMenu = BuildBlockContextMenu(block);
        card.ToolTip = BuildBlockToolTip(block);
        ToolTipService.SetShowDuration(card, 10000);
        ToolTipService.SetInitialShowDelay(card, 400);
        return card;
    }

    // ─── ツールチップ ────────────────────────────────────────────────

    // ─── Outlook イベント ツールチップ ──────────────────────────────

    private void SetupOutlookTooltips()
    {
        var timedTip = new System.Windows.Controls.ToolTip();
        TimeDropCanvas.ToolTip = timedTip;
        ToolTipService.SetShowDuration(TimeDropCanvas, 10000);
        ToolTipService.SetInitialShowDelay(TimeDropCanvas, 400);
        TimeDropCanvas.ToolTipOpening += (_, e) =>
        {
            var pos = Mouse.GetPosition(TimeDropCanvas);
            var ev = FindOutlookTimedEventAt(pos);
            if (ev == null) { e.Handled = true; return; }
            timedTip.Content = BuildOutlookEventToolTipPanel(ev);
        };
        // ToolTip は「同じ要素上に留まる限り一度しか開かない」ため、
        // ホバー対象イベントが切り替わったタイミングで明示的に閉じて再オープンする。
        // IsOpen を false→true と同一ティックで切り替えると再オープンが無視され
        // 前回の内容のまま表示され続けるため、Content を直接更新したうえで
        // 再オープンを 1 ディスパッチサイクル遅延させる。
        TimeDropCanvas.PreviewMouseMove += (_, e) =>
        {
            var ev = FindOutlookTimedEventAt(e.GetPosition(TimeDropCanvas));
            if (ReferenceEquals(ev, _lastTimedTooltipEvent)) return;
            _lastTimedTooltipEvent = ev;
            timedTip.IsOpen = false;
            if (ev == null) return;
            timedTip.Content = BuildOutlookEventToolTipPanel(ev);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_lastTimedTooltipEvent, ev)) timedTip.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        };
        TimeDropCanvas.MouseLeave += (_, _) =>
        {
            _lastTimedTooltipEvent = null;
            timedTip.IsOpen = false;
        };

        var allDayTip = new System.Windows.Controls.ToolTip();
        AllDayCanvas.ToolTip = allDayTip;
        ToolTipService.SetShowDuration(AllDayCanvas, 10000);
        ToolTipService.SetInitialShowDelay(AllDayCanvas, 400);
        AllDayCanvas.ToolTipOpening += (_, e) =>
        {
            var pos = Mouse.GetPosition(AllDayCanvas);
            var ev = FindOutlookAllDayEventAt(pos);
            if (ev == null) { e.Handled = true; return; }
            allDayTip.Content = BuildOutlookEventToolTipPanel(ev);
        };
        AllDayCanvas.PreviewMouseMove += (_, e) =>
        {
            var ev = FindOutlookAllDayEventAt(e.GetPosition(AllDayCanvas));
            if (ReferenceEquals(ev, _lastAllDayTooltipEvent)) return;
            _lastAllDayTooltipEvent = ev;
            allDayTip.IsOpen = false;
            if (ev == null) return;
            allDayTip.Content = BuildOutlookEventToolTipPanel(ev);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_lastAllDayTooltipEvent, ev)) allDayTip.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        };
        AllDayCanvas.MouseLeave += (_, _) =>
        {
            _lastAllDayTooltipEvent = null;
            allDayTip.IsOpen = false;
        };
    }

    private OutlookEvent? FindOutlookTimedEventAt(Point pos)
    {
        if (_vm == null) return null;
        var weekStart = _vm.WeekStart;

        foreach (var ev in _vm.OutlookEvents.Where(e => !e.IsAllDay))
        {
            var startAt = ev.Start;
            var endAt   = ev.End;
            if (startAt.Date >= weekStart.AddDays(7) || endAt <= weekStart) continue;
            if (startAt < weekStart) startAt = weekStart;
            if (endAt > weekStart.AddDays(7)) endAt = weekStart.AddDays(7);

            int dayIdx = (startAt.Date - weekStart.Date).Days;
            double top    = startAt.TimeOfDay.TotalMinutes / SlotMinutes * SlotHeight;
            double height = Math.Max(SlotHeight, (endAt - startAt).TotalMinutes / SlotMinutes * SlotHeight);
            double left   = TimeColWidth + dayIdx * _dayColWidth + 1;
            double width  = _dayColWidth - 4;

            if (pos.X >= left && pos.X <= left + width &&
                pos.Y >= top  && pos.Y <= top  + height)
                return ev;
        }
        return null;
    }

    private OutlookEvent? FindOutlookAllDayEventAt(Point pos)
    {
        if (_vm == null) return null;
        var weekStart = _vm.WeekStart;

        int dayIdx = (int)Math.Floor((pos.X - TimeColWidth) / _dayColWidth);
        if (dayIdx < 0 || dayIdx > 6) return null;
        var cursorDate = weekStart.AddDays(dayIdx);

        return _vm.OutlookEvents
            .Where(e => e.IsAllDay &&
                        e.Start.Date <= cursorDate &&
                        cursorDate < e.End.Date)
            .FirstOrDefault();
    }

    private StackPanel BuildOutlookEventToolTipPanel(OutlookEvent ev)
    {
        var panel = new StackPanel { Margin = new Thickness(2) };

        // カレンダー名
        if (!string.IsNullOrEmpty(ev.CalendarName))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ev.CalendarName,
                FontSize = 10,
                Foreground = GetBrush("AppSubtext0"),
            });
        }

        // 件名
        panel.Children.Add(new TextBlock
        {
            Text = ev.Subject,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("AppText"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        });

        // 時刻 / 日付
        string rangeText = ev.IsAllDay
            ? ev.Start.Date == ev.End.Date.AddDays(-1)
                ? ev.Start.ToString("M/d")
                : $"{ev.Start:M/d} - {ev.End.AddDays(-1):M/d}"
            : $"{ev.Start:HH:mm} - {ev.End:HH:mm}";

        panel.Children.Add(new TextBlock
        {
            Text = rangeText,
            FontSize = 11,
            Foreground = GetBrush("AppSubtext0"),
            Margin = new Thickness(0, 2, 0, 0),
        });

        // 場所
        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ev.Location,
                FontSize = 11,
                Foreground = GetBrush("AppSubtext0"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        // リンク済みタスク情報
        if (ev.HasLinkedTask)
        {
            panel.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = GetBrush("AppSurface2"),
                Margin = new Thickness(0, 6, 0, 4),
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"⛓ {ev.LinkedProjectShortName ?? ""} / {ev.LinkedTaskTitle ?? ev.LinkedAsanaGid}",
                FontSize = 11,
                Foreground = GetBrush("AppBlue"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Right-click to log meeting notes",
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = GetBrush("AppSubtext0"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        return panel;
    }

    private System.Windows.Controls.ContextMenu BuildOutlookEventContextMenu(OutlookEvent ev)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        if (!string.IsNullOrEmpty(ev.LinkedAsanaGid))
        {
            var logItem = new System.Windows.Controls.MenuItem
            {
                Header = "Log Meeting Notes...",
                FontWeight = FontWeights.SemiBold,
            };
            logItem.Click += (_, _) => OnLogMeetingNotes?.Invoke(ev);
            menu.Items.Add(logItem);

            var asanaItem = new System.Windows.Controls.MenuItem
            {
                Header = "Open Task in Asana",
            };
            asanaItem.Click += (_, _) =>
            {
                var url = $"https://app.asana.com/0/0/{ev.LinkedAsanaGid}";
                OnOpenAsanaUrl?.Invoke(url);
            };
            menu.Items.Add(asanaItem);

            if (ev.LinkedProject != null)
            {
                var editorItem = new System.Windows.Controls.MenuItem
                {
                    Header = "Open Project in Editor",
                };
                var capturedProject = ev.LinkedProject;
                var capturedFocus = capturedProject.FocusFile ?? "";
                editorItem.Click += (_, _) => OnOpenInEditorCallback?.Invoke(capturedProject, capturedFocus);
                menu.Items.Add(editorItem);
            }
        }

        return menu;
    }

    private System.Windows.Controls.ToolTip BuildBlockToolTip(ScheduleBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(2) };

        // プロジェクト名
        if (!string.IsNullOrEmpty(block.ProjectShortName))
        {
            panel.Children.Add(new TextBlock
            {
                Text = block.ProjectShortName,
                FontSize = 10,
                Foreground = GetBrush("AppSubtext0"),
            });
        }

        // タスクタイトル
        panel.Children.Add(new TextBlock
        {
            Text = block.TitleSnapshot,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("AppText"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        });

        // 時刻 / 日付
        string rangeText = block.Kind == ScheduleBlockKind.Timed
            ? GetTimedBlockTimeText(block)
            : block.StartDate.HasValue && block.EndDate.HasValue
                ? block.StartDate.Value.Date == block.EndDate.Value.Date
                    ? block.StartDate.Value.ToString("M/d")
                    : $"{block.StartDate.Value:M/d} - {block.EndDate.Value:M/d}"
                : "";

        if (!string.IsNullOrEmpty(rangeText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = rangeText,
                FontSize = 11,
                Foreground = GetBrush("AppSubtext0"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        // メモ
        if (!string.IsNullOrWhiteSpace(block.Note))
        {
            panel.Children.Add(new TextBlock
            {
                Text = block.Note,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = GetBrush("AppSubtext0"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return new System.Windows.Controls.ToolTip
        {
            Content = panel,
            Background = GetBrush("AppSurface1"),
            BorderBrush = GetBrush("AppSurface2"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
        };
    }

    // ─── コンテキストメニュー ────────────────────────────────────────

    private ContextMenu BuildBlockContextMenu(ScheduleBlock block)
    {
        var menu = new ContextMenu();

        // ブロックタイトル (無効行として表示)
        var detailItem = new MenuItem { Header = $"[{block.ProjectShortName}] {block.TitleSnapshot}" };
        detailItem.IsEnabled = false;
        menu.Items.Add(detailItem);

        menu.Items.Add(new Separator());

        var unscheduleItem = new MenuItem { Header = "Unschedule" };
        unscheduleItem.Click += (_, _) => _vm?.RemoveBlock(block.Id);
        menu.Items.Add(unscheduleItem);

        return menu;
    }

    // ─── Timed ブロック移動 ──────────────────────────────────────────

    private void OnTimedCardMouseDown(Border card, ScheduleBlock block, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            return;
        }
        // ContextMenu は左クリックで開かない
        if (e.ChangedButton != MouseButton.Left) return;

        _movingBlock = block;
        _moveStartPoint = e.GetPosition(TimeBlockCanvas);
        _moveStartCanvasLeft = Canvas.GetLeft(card);
        _moveStartCanvasTop = Canvas.GetTop(card);
        _isMovingAllDay = false;

        card.CaptureMouse();
        card.MouseMove += OnTimedCardMouseMove;
        card.MouseLeftButtonUp += OnTimedCardMouseUp;
        e.Handled = true;
    }

    private void OnTimedCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_movingBlock == null || sender is not Border card) return;
        var current = e.GetPosition(TimeBlockCanvas);
        double dx = current.X - _moveStartPoint.X;
        double dy = current.Y - _moveStartPoint.Y;

        double newLeft = _moveStartCanvasLeft + dx;
        double newTop  = _moveStartCanvasTop  + dy;

        // スナップ
        newTop = Math.Round(newTop / SlotHeight) * SlotHeight;
        int dayIdx = (int)Math.Round((newLeft - TimeColWidth) / _dayColWidth);
        dayIdx = Math.Max(0, Math.Min(6, dayIdx));
        newLeft = TimeColWidth + dayIdx * _dayColWidth;

        Canvas.SetLeft(card, newLeft);
        Canvas.SetTop(card, Math.Max(0, Math.Min(SlotCount * SlotHeight - card.Height, newTop)));
        e.Handled = true;
    }

    private void OnTimedCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_movingBlock == null || sender is not Border card) return;
        card.ReleaseMouseCapture();
        card.MouseMove -= OnTimedCardMouseMove;
        card.MouseLeftButtonUp -= OnTimedCardMouseUp;

        double newLeft = Canvas.GetLeft(card);
        double newTop  = Canvas.GetTop(card);
        int dayIdx  = Math.Max(0, Math.Min(6, (int)Math.Round((newLeft - TimeColWidth) / _dayColWidth)));
        int slotIdx = Math.Max(0, Math.Min(SlotCount - 1, (int)Math.Round(newTop / SlotHeight)));

        var newStartAt = _vm!.WeekStart.AddDays(dayIdx).AddMinutes(slotIdx * SlotMinutes);
        _vm.MoveTimedBlock(_movingBlock.Id, newStartAt);
        _movingBlock = null;
        e.Handled = true;
    }

    // ─── AllDay ブロック移動 ─────────────────────────────────────────

    private void OnAllDayCardMouseDown(Border card, ScheduleBlock block, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _movingBlock = block;
        _moveStartPoint = e.GetPosition(AllDayBlockCanvas);
        _moveStartCanvasLeft = Canvas.GetLeft(card);
        _isMovingAllDay = true;

        card.CaptureMouse();
        card.MouseMove += OnAllDayCardMouseMove;
        card.MouseLeftButtonUp += OnAllDayCardMouseUp;
        e.Handled = true;
    }

    private void OnAllDayCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_movingBlock == null || !_isMovingAllDay || sender is not Border card) return;
        var current = e.GetPosition(AllDayBlockCanvas);
        double dx = current.X - _moveStartPoint.X;
        int dayDelta = (int)Math.Round(dx / _dayColWidth);
        double newLeft = _moveStartCanvasLeft + dayDelta * _dayColWidth;
        Canvas.SetLeft(card, newLeft);
        e.Handled = true;
    }

    private void OnAllDayCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_movingBlock == null || !_isMovingAllDay || sender is not Border card) return;
        card.ReleaseMouseCapture();
        card.MouseMove -= OnAllDayCardMouseMove;
        card.MouseLeftButtonUp -= OnAllDayCardMouseUp;

        double newLeft = Canvas.GetLeft(card);
        int origDay = _movingBlock.StartDate.HasValue
            ? (int)(_movingBlock.StartDate.Value.Date - _vm!.WeekStart).TotalDays
            : 0;
        int newDay = Math.Max(0, Math.Min(6, (int)Math.Round((newLeft - TimeColWidth) / _dayColWidth)));
        int delta = newDay - origDay;
        if (delta != 0)
            _vm?.MoveAllDayBlock(_movingBlock.Id, delta);
        else
            DrawAllDayLane(); // 元の位置に戻す
        _movingBlock = null;
        e.Handled = true;
    }

    // ─── AllDay 空セル 範囲ドラッグ (横スワイプ → タスク選択) ────────

    private void OnAllDayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pos = e.GetPosition(AllDayCanvas);
        int dayIdx = (int)Math.Floor((pos.X - TimeColWidth) / _dayColWidth);
        _allDayDragStartDay = Math.Max(0, Math.Min(6, dayIdx));
        _isDraggingAllDayRange = true;
        AllDayCanvas.CaptureMouse();
        UpdateAllDaySelectionOverlay(_allDayDragStartDay, _allDayDragStartDay);
        e.Handled = true;
    }

    private void OnAllDayMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingAllDayRange) return;
        var pos = e.GetPosition(AllDayCanvas);
        int curDay = (int)Math.Floor((pos.X - TimeColWidth) / _dayColWidth);
        curDay = Math.Max(0, Math.Min(6, curDay));
        int startDay = Math.Min(_allDayDragStartDay, curDay);
        int endDay   = Math.Max(_allDayDragStartDay, curDay);
        UpdateAllDaySelectionOverlay(startDay, endDay);
        e.Handled = true;
    }

    private void OnAllDayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingAllDayRange) return;
        AllDayCanvas.ReleaseMouseCapture();
        _isDraggingAllDayRange = false;
        AllDaySelectionCanvas.Children.Clear();
        e.Handled = true;
    }

    private void OnAllDayRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var pos = e.GetPosition(AllDayCanvas);
        int dayIdx = (int)Math.Floor((pos.X - TimeColWidth) / _dayColWidth);
        dayIdx = Math.Max(0, Math.Min(6, dayIdx));
        _pendingAllDayStart = _vm.WeekStart.AddDays(dayIdx).Date;
        _pendingAllDayEnd   = _pendingAllDayStart;
        ShowAllDayTaskPickerPopup(e.GetPosition(this));
        e.Handled = true;
    }

    private void UpdateAllDaySelectionOverlay(int startDay, int endDay)
    {
        AllDaySelectionCanvas.Children.Clear();
        double left   = TimeColWidth + startDay * _dayColWidth;
        double width  = (endDay - startDay + 1) * _dayColWidth - 2;
        double height = AllDayBorder.ActualHeight - 2;

        var rect = new Rectangle
        {
            Width  = Math.Max(2, width),
            Height = Math.Max(2, height),
            Fill   = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x78, 0xD4)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x78, 0xD4)),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, 0);
        AllDaySelectionCanvas.Children.Add(rect);
    }

    private void ShowAllDayTaskPickerPopup(Point pos)
    {
        if (_vm == null) return;

        if (_allDayTaskPickerPopup != null) _allDayTaskPickerPopup.IsOpen = false;

        var listBox = new ListBox
        {
            MaxHeight = 280,
            Background = GetBrush("AppSurface1"),
            Foreground = GetBrush("AppText"),
            BorderThickness = new Thickness(0),
        };

        var scheduledIdentitiesAd = _vm.TimedBlocks
            .Select(b => b.TaskIdentity)
            .Concat(_vm.AllDayBlocks.Select(b => b.TaskIdentity))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var group in _vm.AllTaskGroups)
        {
            listBox.Items.Add(new ListBoxItem
            {
                Content = $"── {group.ProjectShortName} ──",
                IsEnabled = false,
                Foreground = GetBrush("AppSubtext0"),
                FontSize = 11,
            });
            foreach (var task in group.Tasks)
            {
                var isScheduled = scheduledIdentitiesAd.Contains(WeeklyScheduleViewModel.BuildIdentity(task));
                var item = new ListBoxItem
                {
                    Content = isScheduled
                        ? $"{task.DisplayMainTitle}  [scheduled]"
                        : task.DisplayMainTitle,
                    Tag = task,
                    Foreground = isScheduled ? GetBrush("AppSubtext0") : GetBrush("AppText"),
                    FontSize = 12,
                };
                listBox.Items.Add(item);
            }
        }

        var startLabel = _pendingAllDayStart == _pendingAllDayEnd
            ? _pendingAllDayStart.ToString("MM/dd")
            : $"{_pendingAllDayStart:MM/dd} - {_pendingAllDayEnd:MM/dd}";

        listBox.MouseDoubleClick += (s, e) =>
        {
            if (listBox.SelectedItem is ListBoxItem li && li.Tag is TodayQueueTask task)
            {
                _vm.AddAllDayBlock(task, _pendingAllDayStart, _pendingAllDayEnd);
                _allDayTaskPickerPopup!.IsOpen = false;
            }
        };

        var popup = new Popup
        {
            Child = new Border
            {
                Background = GetBrush("AppSurface1"),
                BorderBrush = GetBrush("AppSurface2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                MinWidth = 220,
                MaxWidth = 320,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Add All-day block ({startLabel})",
                            FontSize = 11,
                            Foreground = GetBrush("AppSubtext0"),
                            Margin = new Thickness(4, 2, 4, 4),
                        },
                        listBox,
                    }
                },
            },
            StaysOpen = false,
            Placement = PlacementMode.MousePoint,
            IsOpen = true,
        };

        _allDayTaskPickerPopup = popup;
    }

    // ─── DragDrop: 左ペインからのドロップ ───────────────────────────

    private void OnTimeDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("Curia.TaskDragPayload"))
            e.Effects = DragDropEffects.Copy;
        else if (e.Data.GetDataPresent("Curia.BlockMoveDragPayload"))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTimeDrop(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        var pos = e.GetPosition(TimeDropCanvas);
        var (dayIdx, slotIdx) = PositionToCell(pos);
        var startAt = _vm.WeekStart.AddDays(dayIdx).AddMinutes(slotIdx * SlotMinutes);

        if (e.Data.GetData("Curia.TaskDragPayload") is TaskDragPayload payload)
        {
            _vm.AddTimedBlock(payload.Task, startAt, durationSlots: 2);
        }
        e.Handled = true;
    }

    private void OnAllDayDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("Curia.TaskDragPayload"))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAllDayDrop(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        var pos = e.GetPosition(AllDayCanvas);
        int dayIdx = (int)Math.Floor((pos.X - TimeColWidth) / _dayColWidth);
        dayIdx = Math.Max(0, Math.Min(6, dayIdx));
        var date = _vm.WeekStart.AddDays(dayIdx).Date;

        if (e.Data.GetData("Curia.TaskDragPayload") is TaskDragPayload payload)
        {
            _vm.AddAllDayBlock(payload.Task, date, date);
        }
        e.Handled = true;
    }

    // ─── 時刻グリッド 時間範囲ドラッグ ──────────────────────────────

    private void OnTimeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pos = e.GetPosition(TimeDropCanvas);
        (_dragStartDay, _dragStartSlot) = PositionToCell(pos);
        _isDraggingTimeRange = true;
        _selectionRect = null;
        TimeDropCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTimeRange) return;
        var pos = e.GetPosition(TimeDropCanvas);
        var (curDay, curSlot) = PositionToCell(pos);

        // 同じ列のみ縦ドラッグ
        if (curDay != _dragStartDay)
        {
            TimeSelectionCanvas.Children.Clear();
            return;
        }

        int startSlot = Math.Min(_dragStartSlot, curSlot);
        int endSlot   = Math.Max(_dragStartSlot, curSlot) + 1;

        TimeSelectionCanvas.Children.Clear();
        _selectionRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD4)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x78, 0xD4)),
            StrokeThickness = 1,
            Width = _dayColWidth - 2,
            Height = (endSlot - startSlot) * SlotHeight,
        };
        Canvas.SetLeft(_selectionRect, TimeColWidth + curDay * _dayColWidth);
        Canvas.SetTop(_selectionRect, startSlot * SlotHeight);
        TimeSelectionCanvas.Children.Add(_selectionRect);
        e.Handled = true;
    }

    private void OnTimeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTimeRange) return;
        TimeDropCanvas.ReleaseMouseCapture();
        _isDraggingTimeRange = false;
        TimeSelectionCanvas.Children.Clear();
        e.Handled = true;
    }

    private void OnTimeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var pos = e.GetPosition(TimeDropCanvas);
        var (dayIdx, slotIdx) = PositionToCell(pos);
        _pendingTimedStart = _vm.WeekStart.AddDays(dayIdx).AddMinutes(slotIdx * SlotMinutes);
        _pendingTimedSlots = 2; // デフォルト 1 時間
        ShowTaskPickerPopup(e.GetPosition(this));
        e.Handled = true;
    }

    // ─── タスク選択ポップアップ ──────────────────────────────────────

    private void ShowTaskPickerPopup(Point screenPos)
    {
        if (_vm == null) return;

        if (_taskPickerPopup != null) _taskPickerPopup.IsOpen = false;

        var listBox = new ListBox
        {
            MaxHeight = 280,
            Background = GetBrush("AppSurface1"),
            Foreground = GetBrush("AppText"),
            BorderThickness = new Thickness(0),
        };

        var scheduledIdentities = _vm.TimedBlocks
            .Select(b => b.TaskIdentity)
            .Concat(_vm.AllDayBlocks.Select(b => b.TaskIdentity))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var group in _vm.AllTaskGroups)
        {
            listBox.Items.Add(new ListBoxItem
            {
                Content = $"── {group.ProjectShortName} ──",
                IsEnabled = false,
                Foreground = GetBrush("AppSubtext0"),
                FontSize = 11,
            });
            foreach (var task in group.Tasks)
            {
                var isScheduled = scheduledIdentities.Contains(WeeklyScheduleViewModel.BuildIdentity(task));
                var item = new ListBoxItem
                {
                    Content = isScheduled
                        ? $"{task.DisplayMainTitle}  [scheduled]"
                        : task.DisplayMainTitle,
                    Tag = task,
                    Foreground = isScheduled ? GetBrush("AppSubtext0") : GetBrush("AppText"),
                    FontSize = 12,
                };
                listBox.Items.Add(item);
            }
        }

        listBox.MouseDoubleClick += (s, e) =>
        {
            if (listBox.SelectedItem is ListBoxItem li && li.Tag is TodayQueueTask task)
            {
                _vm.AddTimedBlock(task, _pendingTimedStart, _pendingTimedSlots);
                _taskPickerPopup!.IsOpen = false;
            }
        };

        var popup = new Popup
        {
            Child = new Border
            {
                Background = GetBrush("AppSurface1"),
                BorderBrush = GetBrush("AppSurface2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                MinWidth = 220,
                MaxWidth = 320,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Assign task ({_pendingTimedStart:HH:mm} - {_pendingTimedStart.AddMinutes(_pendingTimedSlots * 30):HH:mm})",
                            FontSize = 11,
                            Foreground = GetBrush("AppSubtext0"),
                            Margin = new Thickness(4, 2, 4, 4),
                        },
                        listBox,
                    }
                },
            },
            StaysOpen = false,
            Placement = PlacementMode.MousePoint,
            IsOpen = true,
        };

        _taskPickerPopup = popup;
    }

    // ─── ユーティリティ ──────────────────────────────────────────────

    private (int day, int slot) PositionToCell(Point pos)
    {
        int dayIdx = (int)Math.Floor((pos.X - TimeColWidth) / _dayColWidth);
        int slotIdx = (int)Math.Floor(pos.Y / SlotHeight);
        return (
            Math.Max(0, Math.Min(6, dayIdx)),
            Math.Max(0, Math.Min(SlotCount - 1, slotIdx)));
    }

    private static string GetTimedBlockTimeText(ScheduleBlock block)
    {
        if (!block.StartAt.HasValue) return "";
        var end = block.StartAt.Value.AddMinutes(block.DurationSlots * SlotMinutes);
        return $"{block.StartAt.Value:HH:mm} - {end:HH:mm}";
    }

    private static Color GetBlockColor(string? colorKey)
    {
        if (colorKey != null && ColorMap.TryGetValue(colorKey, out var c)) return c;
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private Brush GetBrush(string key)
    {
        if (TryFindResource(key) is Brush b) return b;
        return key switch
        {
            "AppText"      => new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)),
            "AppSubtext0"  => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
            "AppBlue"      => new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)),
            "AppSurface1"  => new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)),
            "AppSurface2"  => new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58)),
            "AppBackground"=> new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
            _ => Brushes.Gray,
        };
    }

    // AllDay レーン割り当て: greedy アルゴリズム。spec §7-1 参照。
    private static int AssignAllDayLanes(List<ScheduleBlock> blocks)
    {
        _laneIndexMap.Clear();
        var lanes = new List<DateTime>();

        foreach (var b in blocks.OrderBy(x => x.StartDate))
        {
            int placed = -1;
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] <= b.StartDate!.Value.Date)
                {
                    placed = i;
                    break;
                }
            }
            if (placed < 0) { placed = lanes.Count; lanes.Add(DateTime.MinValue); }
            lanes[placed] = b.EndDate!.Value.Date.AddDays(1);
            _laneIndexMap[b.Id] = placed;
        }
        return lanes.Count;
    }

    // block.Id → lane index マップ (AllDay 描画のため)
    private static readonly Dictionary<string, int> _laneIndexMap = [];

    private static int GetLaneIndex(ScheduleBlock block)
        => _laneIndexMap.TryGetValue(block.Id, out var idx) ? idx : 0;

    // Outlook 終日イベントのレーン割り当て
    private static readonly Dictionary<string, int> _outlookLaneIndexMap = [];

    private static int AssignOutlookAllDayLanes(List<OutlookEvent> events)
    {
        _outlookLaneIndexMap.Clear();
        var lanes = new List<DateTime>();

        foreach (var ev in events.OrderBy(x => x.Start))
        {
            var startDate = ev.Start.Date;
            var endDate   = ev.IsAllDay ? ev.End.AddDays(-1).Date : ev.End.Date;

            int placed = -1;
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] <= startDate)
                {
                    placed = i;
                    break;
                }
            }
            if (placed < 0) { placed = lanes.Count; lanes.Add(DateTime.MinValue); }
            lanes[placed] = endDate.AddDays(1);
            _outlookLaneIndexMap[ev.EntryId] = placed;
        }
        return lanes.Count;
    }

    private static int GetOutlookAllDayLane(OutlookEvent ev)
        => _outlookLaneIndexMap.TryGetValue(ev.EntryId, out var idx) ? idx : 0;
}

/// <summary>左ペインのタスク項目をドラッグする際のペイロード。</summary>
public record TaskDragPayload(TodayQueueTask Task);
