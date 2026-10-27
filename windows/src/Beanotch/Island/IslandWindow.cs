using Beanotch.Services;
using Beanotch.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI.ViewManagement;

namespace Beanotch.Island;

// The island: a black pill at the top centre of the screen that grows into the panel on hover, standing in for the
// Mac's notch. Files dropped on it go to the Shelf.
public sealed partial class IslandWindow : Window
{
    // Mac points; scaled by Ui.K and then by the monitor's DPI. TopGap is 0 so the pill sits flush with the work area.
    private const double ClosedHeight = 30, OpenWidth = 680, OpenHeight = 336, TopGap = 0, OpenRadius = 22;
    private const int OpenFadeMs = 420, CloseFadeMs = 320, MorphMs = 380;

    private readonly Model model;
    private readonly nint hwnd;
    private readonly Grid root = new();
    private readonly ClosedView closed;
    private readonly Shell shell;
    private readonly DispatcherQueueTimer collapseTimer, tick, statsTimer, dataTimer, sizeTimer, updateTimer;
    private Notice? notice;
    private DateTime noticeUntil;
    private (int W, int H) placed;
    private double placedRadius = ClosedHeight / 2;
    private bool morphing;
    private DateTime morphStart;
    private (int W, int H) morphFrom, morphTo;
    private double radiusFrom, radiusTo;
    private int morphMs;
    private Storyboard? closedFade, shellFade;
    private int fadeToken;

    public IslandWindow()
    {
        var ui = DispatcherQueue.GetForCurrentThread();
        model = new Model(ui);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Ui.Ui.Queue = ui;
        Pages.ShelfPage.Hwnd = hwnd;
        ApplyTheme();

        closed = new ClosedView(model);
        shell = new Shell(model, ApplyTheme);
        root.Children.Add(closed.View);
        root.Children.Add(shell.View);
        shell.View.Visibility = Visibility.Collapsed;
        root.PointerEntered += (_, _) => { collapseTimer?.Stop(); Expand(); };
        root.PointerExited += (_, _) => collapseTimer?.Start();
        root.AllowDrop = true;
        root.DragOver += OnDragOver;
        root.Drop += OnDrop;
        Content = root;

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Beanotch";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Beanotch.ico"));
        Native.SetFrame(hwnd, Theme.Ref(Theme.Island));
        Activated += (_, _) => Native.SetFrame(hwnd, Theme.Ref(model.Expanded ? Theme.Background : Theme.Island));

        collapseTimer = ui.CreateTimer();
        collapseTimer.Interval = TimeSpan.FromMilliseconds(800);
        collapseTimer.IsRepeating = false;
        collapseTimer.Tick += (_, _) => Collapse();
        sizeTimer = ui.CreateTimer();
        sizeTimer.Interval = TimeSpan.FromMilliseconds(16);
        sizeTimer.IsRepeating = true;
        sizeTimer.Tick += (_, _) => MorphTick();
        tick = ui.CreateTimer();
        tick.Interval = TimeSpan.FromMilliseconds(400);
        tick.Tick += (_, _) => { model.Tick(); Update(); };
        tick.Start();
        statsTimer = ui.CreateTimer();
        statsTimer.Interval = TimeSpan.FromSeconds(2);
        statsTimer.Tick += (_, _) => { if (model.Expanded) { model.Stats.Sample(); shell.Update(); } };
        statsTimer.Start();

        model.Media.Changed += Update;
        model.Watchers.Notice += Show;
        model.Notice += Show;
        model.Cleaner.Changed += () => shell.Update();
        _ = model.Media.StartAsync();
        model.Watchers.Start();
        model.Stats.Sample();
        _ = model.Cleaner.ScanAsync();
        _ = model.RefreshDataAsync();
        model.Updater.Found += version => Show(new Notice(Glyphs.Download, Theme.Accent, "Update available", version, TimeSpan.FromSeconds(8)));
        _ = model.Updater.CheckAsync();
        dataTimer = ui.CreateTimer();
        dataTimer.Interval = TimeSpan.FromMinutes(10);
        dataTimer.Tick += (_, _) => _ = model.RefreshDataAsync();
        dataTimer.Start();
        updateTimer = ui.CreateTimer();
        updateTimer.Interval = TimeSpan.FromHours(6);
        updateTimer.Tick += (_, _) => _ = model.Updater.CheckAsync();
        updateTimer.Start();
        Update();

        if (SnapshotFolder is { } dir)
        {
            var started = false;
            Activated += (_, _) =>
            {
                if (started) return;
                started = true;
                _ = SnapshotAsync(dir);
            };
        }
    }

    private void Show(Notice n)
    {
        notice = n;
        noticeUntil = DateTime.UtcNow + n.Duration;
        Update();
    }

    private void ApplyTheme()
    {
        var systemDark = new UISettings().GetColorValue(UIColorType.Background).R < 128;
        Theme.Apply(model.Settings.Theme, model.Settings.Accent, systemDark);
        root.RequestedTheme = Theme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        root.Background = Theme.Brush(model.Expanded ? Theme.Background : Theme.Island);
        Native.SetFrame(hwnd, Theme.Ref(model.Expanded ? Theme.Background : Theme.Island));
    }

    private void Update()
    {
        if (notice is not null && DateTime.UtcNow > noticeUntil) notice = null;
        if (model.Expanded) shell.Tick();
        else
        {
            var width = closed.Update(notice);
            Place(width, ClosedHeight, ClosedHeight / 2);
        }
    }

    private void Expand()
    {
        if (model.Expanded) return;
        collapseTimer.Stop();
        model.Expanded = true;
        model.Stats.Sample();
        var token = ++fadeToken;

        // Cross-fade while the window grows, so the panel doesn't snap into place.
        closed.View.Visibility = Visibility.Visible;
        closed.View.Opacity = 1;
        shell.View.Visibility = Visibility.Visible;
        shell.View.Opacity = 0;
        root.Background = Theme.Brush(Theme.Background);
        Native.SetFrame(hwnd, Theme.Ref(Theme.Background));
        shell.Shown();

        Fade(shell.View, 1, OpenFadeMs, ref shellFade);
        Fade(closed.View, 0, CloseFadeMs, ref closedFade, () =>
        {
            if (token == fadeToken) closed.View.Visibility = Visibility.Collapsed;
        });
        Morph(OpenWidth, OpenHeight, OpenRadius, MorphMs);
    }

    private void Collapse()
    {
        // An open drop-down or menu lives outside the window; wait until it closes.
        if (root.XamlRoot is not null && VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0)
        {
            collapseTimer.Start();
            return;
        }
        if (!model.Expanded) return;
        model.Expanded = false;
        var width = closed.Update(notice);
        var token = ++fadeToken;

        closed.View.Visibility = Visibility.Visible;
        closed.View.Opacity = 0;
        shell.View.Visibility = Visibility.Visible;
        shell.View.Opacity = 1;
        Fade(closed.View, 1, OpenFadeMs, ref closedFade);
        Fade(shell.View, 0, CloseFadeMs, ref shellFade, () =>
        {
            if (token == fadeToken) shell.View.Visibility = Visibility.Collapsed;
        });
        root.Background = Theme.Brush(Theme.Island);
        Native.SetFrame(hwnd, Theme.Ref(Theme.Island));
        Morph(width, ClosedHeight, ClosedHeight / 2, MorphMs);
    }

    // Centred at the top of the work area on the island's screen.
    private void Place(double width, double height, double radius)
    {
        if (morphing) return;
        var scale = Native.GetDpiForWindow(hwnd) / 96.0 * Ui.Ui.K;
        int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
        if (placed == (w, h)) return;
        ApplyBounds(w, h, radius);
    }

    private void Morph(double width, double height, double radius, int ms)
    {
        var scale = Native.GetDpiForWindow(hwnd) / 96.0 * Ui.Ui.K;
        int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
        if (!morphing && placed == (w, h))
        {
            placedRadius = radius;
            return;
        }
        morphFrom = placed;
        morphTo = (w, h);
        radiusFrom = placedRadius;
        radiusTo = radius;
        morphStart = DateTime.UtcNow;
        morphMs = Math.Max(1, ms);
        morphing = true;
        sizeTimer.Start();
        MorphTick();
    }

    private void MorphTick()
    {
        if (!morphing) return;
        var t = Math.Clamp((DateTime.UtcNow - morphStart).TotalMilliseconds / morphMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        int w = (int)Math.Round(morphFrom.W + (morphTo.W - morphFrom.W) * eased);
        int h = (int)Math.Round(morphFrom.H + (morphTo.H - morphFrom.H) * eased);
        ApplyBounds(w, h, radiusFrom + (radiusTo - radiusFrom) * eased);
        if (t < 1) return;
        morphing = false;
        sizeTimer.Stop();
        ApplyBounds(morphTo.W, morphTo.H, radiusTo);
    }

    private void ApplyBounds(int w, int h, double radius)
    {
        var scale = Native.GetDpiForWindow(hwnd) / 96.0 * Ui.Ui.K;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (int)Math.Round(TopGap * scale), w, h));
        Native.Round(hwnd, w, h, (int)Math.Round(radius * scale));
        Native.SetBorderColor(hwnd, Theme.Ref(model.Expanded ? Theme.Background : Theme.Island));
        placed = (w, h);
        placedRadius = radius;
    }

    private void Fade(UIElement element, double to, int ms, ref Storyboard? slot, Action? done = null)
    {
        slot?.Stop();
        var animation = new DoubleAnimation
        {
            From = element.Opacity,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (done is not null) storyboard.Completed += (_, _) => done();
        slot = storyboard;
        storyboard.Begin();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Keep on the Shelf";
        if (!model.Expanded)
        {
            Expand();
            shell.Open(Tab.Shelf);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            model.Shelf.Add(items.OfType<IStorageItem>().Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
            shell.Open(Tab.Shelf);
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }
}
