using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using screenshot.Services;

namespace screenshot;

/// <summary>
/// Floating preview card for one screenshot, anchored to the bottom-right
/// corner of the work area of the screen that was captured (stacked upward
/// by <see cref="App"/>). The card itself is the primary action: click to
/// copy, drag out to drop the file into another app. Hover reveals the
/// secondary actions (view / save / discard). After a few idle seconds it
/// slides off the edge and archives itself to Pictures\Screenshots —
/// revealing where it went. Esc archives too; only Discard deletes.
/// </summary>
public partial class PreviewWindow : Window
{
    private const double MaxImageW = 320;
    private const double MaxImageH = 240;

    private readonly string? _imagePath;
    /// <summary>Set when the shot could not be written to disk; the card still shows it.</summary>
    private readonly BitmapSource? _image;

    /// <summary>Where auto-dismiss archives to when there is no working-copy file.</summary>
    private string? _archivedPath;
    private readonly DispatcherTimer _autoDismiss;
    private Point _pressPoint;
    private bool _pressPending;
    private bool _placed;
    private bool _dismissing;
    /// <summary>
    /// Set once the card has archived itself. Archiving runs from a timer as well as
    /// from Esc, and the timer keeps its own reference to this window — so without a
    /// latch the same shot can be written out twice.
    /// </summary>
    private bool _archived;
    /// <summary>The full-screen viewer, while one is open.</summary>
    private ViewerWindow? _viewer;

    /// <summary>The captured screen's work area, in virtual-screen DIPs.</summary>
    public Rect AnchorWorkArea { get; }

    /// <summary>Raised when the user grabs the card back mid auto-dismiss.</summary>
    public event EventHandler? Interacted;

    /// <summary>Working-copy file, or null when the shot only exists in memory.</summary>
    private string? ImagePath => _imagePath ?? _archivedPath;

    public PreviewWindow(string? imagePath, Rect anchorWorkArea, BitmapSource? image = null)
    {
        InitializeComponent();
        _imagePath = imagePath;
        _image = image;
        AnchorWorkArea = anchorWorkArea.IsEmpty ? SystemParameters.WorkArea : anchorWorkArea;

        WindowIcon.Apply(this);

        // Park at the corner until ContentRendered lets App place us exactly.
        var wa = AnchorWorkArea;
        Left = wa.Right;
        Top = wa.Bottom;

        // Prefer the file (cheap downscaled decode); fall back to the in-memory
        // image so a failed temp write can never cost the user the card.
        var bitmap = imagePath != null ? ScreenshotManager.LoadThumbnail(imagePath, (int)MaxImageW) : null;
        if (bitmap == null && image != null)
        {
            bitmap = new WriteableBitmap(image);
            bitmap.Freeze();
        }
        if (bitmap != null)
        {
            Img.Source = bitmap;
            Placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            DiagnosticLog.Write("preview: no image source available (file and bitmap both missing)");
        }

        // Clip the image to the card's rounded corners (image is edge-to-edge).
        ImgHost.SizeChanged += (_, _) =>
        {
            ImgHost.Clip = new RectangleGeometry(
                new Rect(0, 0, ImgHost.ActualWidth, ImgHost.ActualHeight), 6, 6);
        };

        _autoDismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _autoDismiss.Tick += (_, _) =>
        {
            // Never dismiss from under the user's cursor; MouseLeave restarts.
            if (IsMouseOver) return;
            _autoDismiss.Stop();
            BeginAutoDismiss();
        };

        // The timer holds this window (and its full-resolution image) alive, and a
        // tick arriving after the card is gone would archive the shot a second time.
        // Nothing may outlive the window.
        Closed += (_, _) => _autoDismiss.Stop();

        ContentRendered += (_, _) =>
        {
            PlayAppear();
            _autoDismiss.Start();
            AssertOnTop();
        };
    }

    /// <summary>
    /// Re-asserts topmost once the card is rendered. A window that was shown while
    /// something else owned the foreground (a fullscreen game, another topmost
    /// window) can end up behind it, which looks exactly like "no card appeared".
    /// Toggling Topmost forces the shell to re-evaluate our z-order.
    /// </summary>
    private void AssertOnTop()
    {
        try
        {
            Topmost = false;
            Topmost = true;
            Activate();
            DiagnosticLog.Write($"preview: on top, IsActive={IsActive}, bounds=({Left:F0},{Top:F0}) {ActualWidth:F0}x{ActualHeight:F0}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"preview: could not assert topmost — {ex.Message}");
        }
    }

    // ── Placement (App anchors the stack bottom-right) ───────

    public void MoveTo(double left, double top)
    {
        if (_dismissing) return;
        if (!_placed)
        {
            Left = left;
            Top = top;
            _placed = true;
            return;
        }
        AnimateTo(LeftProperty, left);
        AnimateTo(TopProperty, top);
    }

    private void AnimateTo(DependencyProperty prop, double to)
    {
        if (Math.Abs((double)GetValue(prop) - to) < 0.5) return;
        BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void PlayAppear()
    {
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var dur = TimeSpan.FromMilliseconds(260);
        AppearScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, dur) { EasingFunction = back });
        AppearScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, dur) { EasingFunction = back });
        AppearSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, dur)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    // ── Hover ────────────────────────────────────────────────

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_dismissing)
        {
            // The user reached for it mid-dismiss: stop sliding, re-anchor.
            _dismissing = false;
            BeginAnimation(LeftProperty, null);
            Interacted?.Invoke(this, EventArgs.Empty);
        }
        HoverLayer.IsHitTestVisible = true;
        AnimateOpacity(HoverLayer, 1, 110);
        RestartAutoDismiss();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateOpacity(HoverLayer, 0, 150);
        HoverLayer.IsHitTestVisible = false;
        RestartAutoDismiss();
    }

    private void RestartAutoDismiss()
    {
        if (_archived || _viewer != null) return;   // nothing left to dismiss, or the user is looking at it
        _autoDismiss.Stop();
        if (!_dismissing) _autoDismiss.Start();
    }

    private static void AnimateOpacity(UIElement el, double to, int ms)
    {
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    // ── Click = copy, drag = file out ────────────────────────

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton != MouseButton.Left) return;
        _pressPending = true;
        _pressPoint = e.GetPosition(this);
        CaptureMouse();
        RestartAutoDismiss();
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressPending) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressPending = false;
            ReleaseMouseCapture();
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _pressPoint.X) > 6 || Math.Abs(pos.Y - _pressPoint.Y) > 6)
        {
            _pressPending = false;
            ReleaseMouseCapture();
            var path = ImagePath;
            if (path != null && File.Exists(path))
            {
                DoDrag(new DataObject(DataFormats.FileDrop, new[] { path }));
            }
            else if (_image != null)
            {
                // No file to drop — hand the image over directly instead.
                var data = new DataObject();
                data.SetImage(_image);
                DoDrag(data);
            }
        }
    }

    /// <summary>
    /// Runs a drag with the auto-dismiss parked. The drag owns a modal loop and can
    /// last as long as the user takes to pick a target; letting the timer archive in
    /// the meantime would delete the very file the drop target is about to receive.
    /// </summary>
    private void DoDrag(DataObject data)
    {
        _autoDismiss.Stop();
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        finally
        {
            RestartAutoDismiss();
        }
    }

    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_pressPending) return;
        _pressPending = false;
        ReleaseMouseCapture();
        CopyWithFeedback();
    }

    private void CopyWithFeedback()
    {
        // Only celebrate a copy that actually landed: the clipboard can be locked by
        // another process, and a green check for a no-op copy is worse than silence.
        if (App.Capture.CopyToClipboard(ImagePath, _image))
        {
            ShowCopiedCheck();
        }
        else
        {
            DiagnosticLog.Write("preview: copy failed — no success feedback shown");
        }
    }

    private void ShowCopiedCheck()
    {
        CheckOverlay.Visibility = Visibility.Visible;

        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        var dur = TimeSpan.FromMilliseconds(240);
        CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, dur) { EasingFunction = back });
        CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, dur) { EasingFunction = back });

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        fade.Completed += (_, _) => CheckOverlay.Visibility = Visibility.Collapsed;
        CheckOverlay.BeginAnimation(OpacityProperty, fade);
    }

    // ── Auto-dismiss: slide off the edge, archive the shot ───

    private void BeginAutoDismiss()
    {
        if (_dismissing) return;
        _dismissing = true;

        // Say where it's going while it's still on screen.
        ArchiveChip.Visibility = Visibility.Visible;
        ArchiveChip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));

        var anim = new DoubleAnimation(Left + ActualWidth + 60, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) => ArchiveAndClose();
        BeginAnimation(LeftProperty, anim);
    }

    private void ArchiveAndClose()
    {
        // Esc and the auto-dismiss timer both land here, and a tick already in
        // flight can arrive after the card is closed: archive exactly once.
        if (_archived) return;
        _archived = true;

        string? archivedTo = null;
        try
        {
            var path = ImagePath;
            if (path != null && File.Exists(path))
            {
                string dir = ScreenshotManager.ArchiveDirectory;
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, Path.GetFileName(path));
                File.Copy(path, dst, overwrite: true);
                archivedTo = dst;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"archive: FAILED {ex.GetType().Name}: {ex.Message}");
        }

        // The archive location refused the file — fall back to the same chain the
        // in-memory branch uses, because a shot kept somewhere odd always beats a
        // shot that is deleted on its way out.
        if (archivedTo == null && _image != null)
        {
            _archivedPath = ScreenshotManager.TryArchive(
                _image, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            archivedTo = _archivedPath;
            DiagnosticLog.Write(archivedTo != null
                ? $"archive: saved from memory to {archivedTo}"
                : "archive: no location accepted the image");
        }

        // The working copy is only disposable once a copy exists elsewhere. Deleting
        // it after a failed archive would destroy the only remaining copy of the shot.
        if (archivedTo != null)
        {
            App.Capture.DeletePreview(_imagePath);
            ArchiveText.Text = archivedTo.StartsWith(ScreenshotManager.ArchiveDirectory, StringComparison.OrdinalIgnoreCase)
                ? "✓ Saved to Pictures › Screenshots"
                : $"✓ Saved to {Path.GetFileName(archivedTo)}";
        }
        else
        {
            DiagnosticLog.Write($"archive: keeping the working copy at {_imagePath ?? "(memory only)"}");
            ArchiveText.Text = "! Could not save — kept in memory";
        }

        Close();
    }

    // ── Secondary actions ────────────────────────────────────

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        // One viewer at a time: a second click would otherwise stack another window
        // over the same image.
        if (_viewer != null)
        {
            _viewer.Activate();
            return;
        }

        // Full-res for the big viewer (thumbnail is only for the card).
        var bitmap = (ImagePath != null ? ScreenshotManager.LoadBitmap(ImagePath) : null) ?? _image;
        if (bitmap == null) return;

        var viewer = new ViewerWindow(bitmap) { Owner = this };
        _viewer = viewer;
        // While the user is studying the shot the card must not slide out from under
        // them — and with an owner set, losing the card would take the viewer too.
        _autoDismiss.Stop();
        viewer.Closed += OnViewerClosed;
        viewer.Show();
    }

    private void OnViewerClosed(object? sender, EventArgs e)
    {
        _viewer = null;
        RestartAutoDismiss();
        ((App)Application.Current).ScheduleMemoryTrim();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        App.Capture.SaveImageToFile(ImagePath, this, _image);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseAndDelete();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // IME-active: WPF delivers Alt-routed keys as Key.System; the real key
        // is in SystemKey. Esc must archive either way.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            e.Handled = true;
            ArchiveAndClose();   // Esc puts it away safely; only Discard deletes.
        }
    }

    private void CloseAndDelete()
    {
        _archived = true;        // nothing to archive later; the timer stops with the window
        App.Capture.DeletePreview(_imagePath);
        Close();
    }
}
