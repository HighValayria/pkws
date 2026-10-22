using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pkws.Core;

namespace Pkws.Desktop;

public partial class MainWindow : Window
{
    private readonly RuntimeCoordinator _runtime;
    private readonly DraftStore _draftStore;
    private readonly MarkdownEntrySerializer _serializer = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _previewTimer;
    private bool _allowClose;
    private bool _suppressDraftSave;
    private string _draftRequestId = Guid.CreateVersion7().ToString();
    private string _draftStartedDate = string.Empty;

    public MainWindow(RuntimeCoordinator runtime)
    {
        _runtime = runtime;
        _draftStore = new DraftStore(runtime.Paths.DraftFile);
        InitializeComponent();
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        DocumentPathBox.Text = runtime.Config.DocumentRoot;
        LoadDraft();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clockTimer.Tick += (_, _) => UpdateDateAndDraftNotice();
        _clockTimer.Start();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _previewTimer.Tick += async (_, _) =>
        {
            if (IsVisible)
            {
                await RefreshNotesAsync(false);
            }
        };
        _previewTimer.Start();
        UpdateDateAndDraftNotice();
    }

    public event EventHandler? CollapseRequested;
    public event EventHandler? ExitRequested;

    public async void ShowAndFocus()
    {
        if (!IsVisible)
        {
            Show();
        }

        WindowPositioner.PlacePanelAtCurrentScreenRight(this);
        Activate();
        Focus();
        await RefreshNotesAsync(false);
        NotesScrollViewer.ScrollToEnd();
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    public void PrepareForExit()
    {
        SaveDraft();
        _allowClose = true;
        _clockTimer.Stop();
        _previewTimer.Stop();
    }

    private void LoadDraft()
    {
        var draft = _draftStore.Load();
        _suppressDraftSave = true;
        if (draft is not null)
        {
            InputBox.Text = draft.Content;
            _draftStartedDate = draft.StartedLocalDate;
            _draftRequestId = Guid.TryParse(draft.RequestId, out _) ? draft.RequestId : Guid.CreateVersion7().ToString();
        }
        else
        {
            _draftStartedDate = CurrentLocalTime().ToString("yyyy-MM-dd");
        }

        _suppressDraftSave = false;
    }

    private void SaveDraft()
    {
        if (string.IsNullOrEmpty(InputBox.Text))
        {
            _draftStore.Clear();
            return;
        }

        if (string.IsNullOrWhiteSpace(_draftStartedDate))
        {
            _draftStartedDate = CurrentLocalTime().ToString("yyyy-MM-dd");
        }

        _draftStore.Save(new DraftState(InputBox.Text, _draftStartedDate, _draftRequestId));
    }

    private async Task RefreshNotesAsync(bool reportErrors)
    {
        UpdateDateAndDraftNotice();
        try
        {
            var (_, content) = await _runtime.ReadTodayAsync();
            RenderNoteBlocks(_serializer.ParseEntries(content));
        }
        catch (Exception exception) when (!reportErrors)
        {
            StatusText.Text = $"读取失败：{exception.Message}";
        }
    }

    private void RenderNoteBlocks(IReadOnlyList<MarkdownNoteBlock> entries)
    {
        var expectedTags = entries.Select(entry => $"{entry.EntryId}:{MarkdownEntrySerializer.Sha256(entry.Content)}");
        var currentTags = NoteBlocksPanel.Children.OfType<StackPanel>().Select(panel => panel.Tag as string);
        if (NoteBlocksPanel.Children.Count == entries.Count && currentTags.SequenceEqual(expectedTags))
        {
            return;
        }

        NoteBlocksPanel.Children.Clear();
        foreach (var entry in entries)
        {
            var block = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 26),
                Tag = $"{entry.EntryId}:{MarkdownEntrySerializer.Sha256(entry.Content)}"
            };
            block.Children.Add(new TextBlock
            {
                Text = entry.Hour,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(139, 126, 114))
            });
            block.Children.Add(MarkdownDocumentRenderer.CreateViewer(entry.Content));
            NoteBlocksPanel.Children.Add(block);
        }
    }

    private void UpdateDateAndDraftNotice()
    {
        var now = CurrentLocalTime();
        DateText.Text = now.ToString("yyyy 年 MM 月 dd 日");
        DraftHourText.Text = now.ToString("HH:00");
        DraftNoticeText.Text = !string.IsNullOrEmpty(InputBox.Text) && _draftStartedDate != now.ToString("yyyy-MM-dd")
            ? $"草稿始于 {_draftStartedDate}，保存时归入今天。"
            : string.Empty;
    }

    private DateTimeOffset CurrentLocalTime() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneResolver.Resolve(_runtime.Config.TimeZoneId));

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveCaptureAsync();

    private async Task SaveCaptureAsync()
    {
        var content = InputBox.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            StatusText.Text = "写点什么再保存。";
            return;
        }

        SaveButton.IsEnabled = false;
        StatusText.Text = "正在保存…";
        try
        {
            var result = await _runtime.CaptureAsync(new CaptureRequest
            {
                RequestId = _draftRequestId,
                SourceType = "desktop",
                Content = content
            });
            _suppressDraftSave = true;
            InputBox.Clear();
            _suppressDraftSave = false;
            _draftStore.Clear();
            _draftRequestId = Guid.CreateVersion7().ToString();
            _draftStartedDate = CurrentLocalTime().ToString("yyyy-MM-dd");
            StatusText.Text = result.Duplicate ? "已确认保存，没有重复记录。" : "已保存。";
            await RefreshNotesAsync(false);
            NotesScrollViewer.ScrollToEnd();
            InputBox.Focus();
        }
        catch (CaptureException exception)
        {
            StatusText.Text = $"保存失败：{exception.Message}";
            SaveDraft();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存失败：{exception.Message}";
            SaveDraft();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDraftSave)
        {
            return;
        }

        _draftRequestId = Guid.CreateVersion7().ToString();
        if (string.IsNullOrEmpty(InputBox.Text))
        {
            _draftStartedDate = CurrentLocalTime().ToString("yyyy-MM-dd");
        }

        SaveDraft();
        UpdateDateAndDraftNotice();
        if (DraftPreview.Visibility == Visibility.Visible)
        {
            UpdateDraftPreview();
        }
    }

    private void NotesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || NotesScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        // TextBox and FlowDocumentScrollViewer both consume wheel input even when
        // their own scroll bars are disabled. Route every wheel gesture in the note
        // body to the single outer page so scrolling does not stop over nested content.
        const double pixelsPerWheelDetent = 48;
        var distance = -(e.Delta / 120d) * pixelsPerWheelDetent;
        var target = Math.Clamp(
            NotesScrollViewer.VerticalOffset + distance,
            0,
            NotesScrollViewer.ScrollableHeight);
        NotesScrollViewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void PreviewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DraftPreview.Visibility == Visibility.Visible)
        {
            DraftPreview.Visibility = Visibility.Collapsed;
            PreviewToggleButton.Content = "预览";
            InputBox.Focus();
            return;
        }

        UpdateDraftPreview();
        DraftPreview.Visibility = Visibility.Visible;
        PreviewToggleButton.Content = "关闭预览";
    }

    private void UpdateDraftPreview() => DraftPreview.Document = MarkdownDocumentRenderer.CreateDocument(InputBox.Text);

    private void HeadingButton_Click(object sender, RoutedEventArgs e) => PrefixSelectedLines("## ");

    private void BoldButton_Click(object sender, RoutedEventArgs e) => WrapSelection("**", "**", "粗体");

    private void ItalicButton_Click(object sender, RoutedEventArgs e) => WrapSelection("*", "*", "斜体");

    private void ListButton_Click(object sender, RoutedEventArgs e) => PrefixSelectedLines("- ");

    private void QuoteButton_Click(object sender, RoutedEventArgs e) => PrefixSelectedLines("> ");

    private void CodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (InputBox.SelectedText.Contains('\n') || InputBox.SelectedText.Contains('\r'))
        {
            WrapSelection("```\n", "\n```", "代码");
        }
        else
        {
            WrapSelection("`", "`", "代码");
        }
    }

    private void WrapSelection(string prefix, string suffix, string placeholder)
    {
        var start = InputBox.SelectionStart;
        var selected = InputBox.SelectedText;
        var content = selected.Length == 0 ? placeholder : selected;
        InputBox.SelectedText = prefix + content + suffix;
        InputBox.Select(start + prefix.Length, content.Length);
        InputBox.Focus();
    }

    private void PrefixSelectedLines(string prefix)
    {
        var selectionStart = InputBox.SelectionStart;
        var selected = InputBox.SelectedText;
        if (selected.Length > 0)
        {
            var normalized = selected.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var replacement = string.Join(Environment.NewLine, normalized.Split('\n').Select(line => prefix + line));
            InputBox.SelectedText = replacement;
            InputBox.Select(selectionStart, replacement.Length);
        }
        else
        {
            var lineStart = InputBox.CaretIndex == 0 ? -1 : InputBox.Text.LastIndexOf('\n', InputBox.CaretIndex - 1);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            InputBox.Text = InputBox.Text.Insert(lineStart, prefix);
            InputBox.CaretIndex = selectionStart + prefix.Length;
        }

        InputBox.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OpenDocumentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.OpenDocumentFolder();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法打开文档目录：{exception.Message}";
        }
    }

    private void CreatePairingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pairing = _runtime.CreateExtensionPairingCode();
            PairingCodeText.Text = pairing.Code;
            StatusText.Text = $"插件配对码已生成，{pairing.ExpiresAt.ToLocalTime():HH:mm} 前有效。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法生成配对码：{exception.Message}";
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => CollapseToEdge();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SaveCaptureAsync();
        }
        else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control && InputBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            WrapSelection("**", "**", "粗体");
        }
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.Control && InputBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            WrapSelection("*", "*", "斜体");
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CollapseToEdge();
        }
    }

    private void CollapseToEdge()
    {
        SaveDraft();
        SettingsPanel.Visibility = Visibility.Collapsed;
        Hide();
        CollapseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        CollapseToEdge();
    }
}
