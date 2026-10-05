using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MemeDigest;

public partial class MainWindow : Window, System.Windows.Forms.IWin32Window
{
    // ── Constants ──

    private const int MaxSelectionCount = 64;

    // ── Fields ──

    private readonly AppSettings settings;

    private readonly MemeLibraryScanner scanner;

    private readonly ThumbnailService thumbnailService;

    /// <summary>Guard so a slow scan cannot double-load concurrently.</summary>
    private readonly SemaphoreSlim loadGate = new SemaphoreSlim(1, 1);

    private UserHistory history;

    private readonly Dictionary<string, bool> selectedByRelativePath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    private readonly List<GalleryCard> galleryCards = new List<GalleryCard>();

    private readonly Random random = new Random();

    private bool suppressProfileEvents;

    private int viewerCardIndex;

    // ── Construction ──

    public MainWindow()
    {
        InitializeComponent();
        settings = AppSettings.LoadOrCreate();
        scanner = new MemeLibraryScanner();
        thumbnailService = new ThumbnailService(settings);
        history = UserHistory.LoadFromFile(settings.HistoryDirectory, settings.CurrentUserProfile);
        ProfileComboBox.ItemsSource = settings.UserProfiles;
        ProfileComboBox.SelectedItem = settings.CurrentUserProfile;
        DrawCountBox.Text = settings.DrawCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateTitle();
        RefreshHistorySummary();
        UpdateExportButtonStates();

        if (!settings.IsLibraryPathValid)
        {
            _ = ShowSettingsDialogAsync(firstRun: true);
        }
        else
        {
            _ = ReloadGalleryAsync();
        }
    }

    // ── Title / status ──

    private void UpdateTitle()
    {
        Title = nameof(MemeDigest) + " — " + settings.CurrentUserProfile;
    }

    // ── WinForms interop (folder dialog owner) ──

    public IntPtr Handle
    {
        get { return new System.Windows.Interop.WindowInteropHelper(this).Handle; }
    }

    private void RefreshHistorySummary()
    {
        StatusRightTextBlock.Text = "Picked: " + history.PickedCount + "   Discarded: " + history.DiscardedCount;
    }

    private void SetStatus(string message)
    {
        StatusLeftTextBlock.Text = message;
    }

    // ── Drawing ──

    private async void DrawButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        int requestedCount = ParseDrawCount();
        if (requestedCount > 0)
        {
            await ReloadGalleryAsync(requestedCountOverride: requestedCount);
        }
    }

    private int ParseDrawCount()
    {
        string text = DrawCountBox.Text.Trim();
        int count;
        if (!int.TryParse(text, out count) || count < 1)
        {
            count = AppSettings.DefaultDrawCount;
        }

        if (count > 200)
        {
            count = 200;
        }

        DrawCountBox.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings.DrawCount = count;
        return count;
    }

    // ── Gallery loading ──

    private async Task ReloadGalleryAsync(int? requestedCountOverride = null)
    {
        if (!await loadGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            DrawButton.IsEnabled = false;
            SetStatus("Scanning library…");
            int requestedCount = requestedCountOverride ?? settings.DrawCount;

            List<MediaFileInfo> drawn = await Task.Run(() =>
            {
                HashSet<string> excluded = history.GetExcludedRelativePaths();
                List<MediaFileInfo> eligible = scanner.ScanEligibleFiles(settings, excluded);
                return scanner.DrawRandom(eligible, requestedCount, random);
            });

            selectedByRelativePath.Clear();
            RenderGallery(drawn);
            SaveSettings();

            if (drawn.Count == 0)
            {
                SetStatus("Nothing new to draw — every meme was already seen (or the library is empty).");
            }
            else
            {
                SetStatus("Drew " + drawn.Count + " meme" + (drawn.Count == 1 ? string.Empty : "s") + ".");
            }
        }
        finally
        {
            loadGate.Release();
            DrawButton.IsEnabled = true;
        }
    }

    // ── Gallery rendering ──

    private void RenderGallery(List<MediaFileInfo> drawn)
    {
        DisposeAllCardMedia();
        GalleryPanel.Children.Clear();
        galleryCards.Clear();
        selectedByRelativePath.Clear();

        foreach (MediaFileInfo item in drawn)
        {
            GalleryCard card = new GalleryCard(item, thumbnailService, MaxSelectionCount);
            card.ThumbnailClicked += GalleryCard_ThumbnailClicked;
            card.SelectionChanged += GalleryCard_SelectionChanged;
            GalleryPanel.Children.Add(card);
            galleryCards.Add(card);
        }

        GalleryHeaderTextBlock.Text = GalleryHeaderTemplate();
        UpdateExportButtonStates();
    }

    private string GalleryHeaderTemplate()
    {
        return galleryCards.Count + " memes drawn — click a tile to view it in a full window, tick tiles to select for export";
    }

    private void GalleryCard_ThumbnailClicked(GalleryCard card)
    {
        OpenViewer(card);
    }

    private void GalleryCard_SelectionChanged(GalleryCard card)
    {
        selectedByRelativePath[card.Media.RelativePath] = card.IsSelected;
        UpdateExportButtonStates();
    }

    private List<string> GetSelectedAbsolutePaths()
    {
        List<string> paths = new List<string>();
        foreach (GalleryCard card in galleryCards)
        {
            bool isSelected;
            if (selectedByRelativePath.TryGetValue(card.Media.RelativePath, out isSelected) && isSelected)
            {
                paths.Add(card.Media.AbsolutePath);
            }
        }

        return paths;
    }

    private List<string> GetSelectedRelativePaths()
    {
        List<string> paths = new List<string>();
        foreach (GalleryCard card in galleryCards)
        {
            bool isSelected;
            if (selectedByRelativePath.TryGetValue(card.Media.RelativePath, out isSelected) && isSelected)
            {
                paths.Add(card.Media.RelativePath);
            }
        }

        return paths;
    }

    private void UpdateExportButtonStates()
    {
        bool hasSelection = GetSelectedAbsolutePaths().Count > 0;
        RevealSelectedButton.IsEnabled = hasSelection;
        CopySelectedButton.IsEnabled = hasSelection;
        MoveSelectedButton.IsEnabled = hasSelection;
        CopyToFolderButton.IsEnabled = hasSelection;
    }

    // ── Replacement after discard/keep/move ──

    private async Task ReplaceConsumedCardsAsync()
    {
        int slotsToRefill = 0;
        HashSet<int> slotIndexes = new HashSet<int>();
        for (int index = 0; index < galleryCards.Count; index++)
        {
            if (galleryCards[index].Media.RelativePath.Length == 0)
            {
                slotIndexes.Add(index);
                slotsToRefill++;
            }
        }

        if (slotsToRefill == 0)
        {
            return;
        }

        HashSet<string> currentlyDisplayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GalleryCard card in galleryCards)
        {
            if (card.Media.RelativePath.Length > 0)
            {
                currentlyDisplayed.Add(card.Media.RelativePath);
            }
        }

        List<MediaFileInfo> replacements = await Task.Run(() =>
        {
            HashSet<string> excluded = history.GetExcludedRelativePaths();
            List<MediaFileInfo> eligible = scanner.ScanEligibleFiles(settings, excluded);
            List<MediaFileInfo> drawn = scanner.DrawRandom(eligible, slotsToRefill, random);
            List<MediaFileInfo> accepted = new List<MediaFileInfo>();
            foreach (MediaFileInfo item in drawn)
            {
                if (!currentlyDisplayed.Contains(item.RelativePath))
                {
                    accepted.Add(item);
                }
            }

            return accepted;
        });

        if (replacements.Count == 0)
        {
            SetStatus("Library exhausted — no unseen memes left to refill.");
            return;
        }

        int replacementIndex = 0;
        foreach (int slotIndex in slotIndexes)
        {
            if (replacementIndex >= replacements.Count)
            {
                break;
            }

            GalleryCard replacement = new GalleryCard(replacements[replacementIndex], thumbnailService, MaxSelectionCount);
            replacement.ThumbnailClicked += GalleryCard_ThumbnailClicked;
            replacement.SelectionChanged += GalleryCard_SelectionChanged;
            GalleryPanel.Children[slotIndex] = replacement;
            galleryCards[slotIndex] = replacement;
            replacementIndex++;
        }

        GalleryHeaderTextBlock.Text = GalleryHeaderTemplate();
        SetStatus("Replaced " + replacementIndex + " empty slot" + (replacementIndex == 1 ? string.Empty : "s") + " with new random memes.");
    }

    // ── Viewer ──

    private void OpenViewer(GalleryCard card)
    {
        for (int index = 0; index < galleryCards.Count; index++)
        {
            if (ReferenceEquals(galleryCards[index], card))
            {
                viewerCardIndex = index;
                ShowViewerForCurrentIndex();
                return;
            }
        }
    }

    private void ShowViewerForCurrentIndex()
    {
        if (viewerCardIndex < 0 || viewerCardIndex >= galleryCards.Count)
        {
            CloseViewer();
            return;
        }

        GalleryCard card = galleryCards[viewerCardIndex];
        ViewerOverlay.Visibility = Visibility.Visible;
        ViewerOverlay.Focus();
        ViewerCaptionTextBlock.Text = card.Media.RelativePath + "   [" + (viewerCardIndex + 1) + "/" + galleryCards.Count + "]" + (card.IsSelected ? "   ✓ selected" : string.Empty);
        ShowViewerMedia(card.Media);
    }

    private void ShowViewerMedia(MediaFileInfo media)
    {
        ViewerMediaErrorTextBlock.Visibility = Visibility.Collapsed;
        ViewerImage.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        ViewerVideo.Stop();
        ViewerVideo.Close();
        ViewerVideo.Source = null;
        ViewerVideo.Visibility = Visibility.Collapsed;

        try
        {
            if (media.Kind == MediaKind.Image)
            {
                BitmapImage fullImage = new BitmapImage();
                fullImage.BeginInit();
                fullImage.UriSource = new Uri(media.AbsolutePath, UriKind.Absolute);
                fullImage.CacheOption = BitmapCacheOption.OnLoad;
                fullImage.EndInit();
                fullImage.Freeze();
                ViewerImage.Source = fullImage;
                ViewerImage.Visibility = Visibility.Visible;
            }
            else
            {
                ViewerVideo.Source = new Uri(media.AbsolutePath, UriKind.Absolute);
                ViewerVideo.Visibility = Visibility.Visible;
                ViewerVideo.Position = TimeSpan.Zero;
                ViewerVideo.Play();
            }
        }
        catch (Exception exception)
        {
            ShowViewerMediaError("Cannot open " + Path.GetFileName(media.AbsolutePath) + Environment.NewLine + exception.Message);
        }
    }

    private void ShowViewerMediaError(string message)
    {
        ViewerMediaErrorTextBlock.Text = message;
        ViewerMediaErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void CloseViewer()
    {
        ViewerVideo.Stop();
        ViewerVideo.Close();
        ViewerVideo.Source = null;
        ViewerImage.Source = null;
        ViewerOverlay.Visibility = Visibility.Collapsed;
        viewerCardIndex = -1;
    }

    private void ViewerSurface_MouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ClickCount == 2)
        {
            CloseViewer();
        }
    }

    private void ViewerVideo_MediaFailed(object sender, ExceptionRoutedEventArgs eventArgs)
    {
        string fileName = string.Empty;
        if (ViewerVideo.Source != null)
        {
            fileName = Path.GetFileName(ViewerVideo.Source.ToString());
        }

        string message = eventArgs.ErrorException == null ? "codec missing" : eventArgs.ErrorException.Message;
        ShowViewerMediaError("Cannot play " + fileName + Environment.NewLine + message + Environment.NewLine + "(Install ffmpeg-compatible codecs or try another format.)");
    }

    private void ViewerPrevButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        StepViewer(-1);
    }

    private void ViewerNextButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        StepViewer(1);
    }

    private void StepViewer(int delta)
    {
        if (galleryCards.Count == 0)
        {
            return;
        }

        int nextIndex = viewerCardIndex + delta;
        if (nextIndex < 0)
        {
            nextIndex = galleryCards.Count - 1;
        }
        else if (nextIndex >= galleryCards.Count)
        {
            nextIndex = 0;
        }

        viewerCardIndex = nextIndex;
        ShowViewerForCurrentIndex();
    }

    private async void ViewerKeepButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await MarkCurrentViewerCard(MemeHistoryState.Picked);
    }

    private async void ViewerDiscardButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await MarkCurrentViewerCard(MemeHistoryState.Discarded);
    }

    private async Task MarkCurrentViewerCard(MemeHistoryState state)
    {
        if (viewerCardIndex < 0 || viewerCardIndex >= galleryCards.Count)
        {
            return;
        }

        GalleryCard card = galleryCards[viewerCardIndex];
        MediaFileInfo consumedMedia = card.Media;
        await RecordHistoryEntryAsync(state, consumedMedia.RelativePath);

        CloseViewer();
        int cardIndex = FindCardIndex(card);
        if (cardIndex >= 0)
        {
            DisposeCardMedia(card);
            galleryCards[cardIndex] = new GalleryCard(
                new MediaFileInfo(string.Empty, string.Empty, MediaKind.Unsupported),
                thumbnailService,
                MaxSelectionCount,
                isPlaceholder: true);
            GalleryPanel.Children[cardIndex] = galleryCards[cardIndex];
        }

        RefreshHistorySummary();
        UpdateExportButtonStates();
        await ReplaceConsumedCardsAsync();
    }

    private int FindCardIndex(GalleryCard card)
    {
        for (int index = 0; index < galleryCards.Count; index++)
        {
            if (ReferenceEquals(galleryCards[index], card))
            {
                return index;
            }
        }

        return -1;
    }

    private async Task RecordHistoryEntryAsync(MemeHistoryState state, string relativePath)
    {
        // Record in BOTH sections so the meme never draws again for this profile.
        if (!history.Contains(MemeHistoryState.Picked, relativePath))
        {
            history.Add(MemeHistoryState.Picked, relativePath, DateTime.UtcNow);
        }

        if (!history.Contains(MemeHistoryState.Discarded, relativePath))
        {
            history.Add(MemeHistoryState.Discarded, relativePath, DateTime.UtcNow);
        }

        await Task.Run(() => history.SaveToFile(settings.HistoryDirectory));
    }

    private void ViewerCloseButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        CloseViewer();
    }

    // ── Keyboard handling ──

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (ViewerOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.Escape:
                CloseViewer();
                eventArgs.Handled = true;
                break;
            case Key.Left:
                StepViewer(-1);
                eventArgs.Handled = true;
                break;
            case Key.Right:
                StepViewer(1);
                eventArgs.Handled = true;
                break;
        }
    }

    // ── Profiles ──

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (suppressProfileEvents)
        {
            return;
        }

        if (ProfileComboBox.SelectedItem is string selectedProfile && !string.Equals(selectedProfile, settings.CurrentUserProfile, StringComparison.Ordinal))
        {
            SwitchToProfile(selectedProfile);
        }
    }

    private void SwitchToProfile(string profileName)
    {
        SaveSettings();
        history = UserHistory.LoadFromFile(settings.HistoryDirectory, profileName);
        settings.CurrentUserProfile = profileName;
        SaveSettings();
        UpdateTitle();
        RefreshHistorySummary();
        DisposeAllCardMedia();
        GalleryPanel.Children.Clear();
        galleryCards.Clear();
        selectedByRelativePath.Clear();
        GalleryHeaderTextBlock.Text = "Profile switched to \"" + profileName + "\" — draw fresh memes";
        UpdateExportButtonStates();
        SetStatus("Switched to profile: " + profileName);
    }

    private void SaveSettings()
    {
        if (!string.IsNullOrWhiteSpace(DrawCountBox.Text))
        {
            int count;
            if (int.TryParse(DrawCountBox.Text.Trim(), out count) && count >= 1)
            {
                settings.DrawCount = Math.Min(count, 200);
            }
        }

        try
        {
            settings.Save();
        }
        catch (Exception exception)
        {
            SetStatus("Cannot save settings: " + exception.Message);
        }
    }

    private void AddProfileButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        ProfileNameWindow dialog = new ProfileNameWindow
        {
            Owner = this
        };

        bool? dialogResult = dialog.ShowDialog();
        if (dialogResult == true && !string.IsNullOrWhiteSpace(dialog.ProfileName))
        {
            string profileName = dialog.ProfileName.Trim();
            if (!settings.UserProfiles.Contains(profileName))
            {
                settings.UserProfiles.Add(profileName);
            }

            suppressProfileEvents = true;
            ProfileComboBox.ItemsSource = null;
            ProfileComboBox.ItemsSource = settings.UserProfiles;
            ProfileComboBox.SelectedItem = profileName;
            suppressProfileEvents = false;

            SwitchToProfile(profileName);
            SetStatus("Profile created: " + profileName);
        }
    }

    private void RemoveProfileButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (settings.UserProfiles.Count <= 1)
        {
            MessageBox.Show("At least one profile must remain.", nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string currentProfile = settings.CurrentUserProfile;
        MessageBoxResult confirm = MessageBox.Show(
            "Remove profile \"" + currentProfile + "\"? Its history file stays on disk and can be re-added later.",
            nameof(MemeDigest),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        settings.UserProfiles.Remove(currentProfile);
        string nextProfile = settings.UserProfiles[0];
        settings.CurrentUserProfile = nextProfile;
        suppressProfileEvents = true;
        ProfileComboBox.ItemsSource = null;
        ProfileComboBox.ItemsSource = settings.UserProfiles;
        ProfileComboBox.SelectedItem = nextProfile;
        suppressProfileEvents = false;

        SaveSettings();
        history = UserHistory.LoadFromFile(settings.HistoryDirectory, nextProfile);
        UpdateTitle();
        RefreshHistorySummary();
        DisposeAllCardMedia();
        GalleryPanel.Children.Clear();
        galleryCards.Clear();
        selectedByRelativePath.Clear();
        GalleryHeaderTextBlock.Text = "Profile switched to \"" + nextProfile + "\" — draw fresh memes";
        UpdateExportButtonStates();
    }

    // ── Export actions ──

    private void RevealSelectedButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        List<string> paths = GetSelectedAbsolutePaths();
        if (paths.Count > 0)
        {
            try
            {
                ExportService.RevealInExplorer(paths);
            }
            catch (Exception exception)
            {
                MessageBox.Show("Cannot open Explorer: " + exception.Message, nameof(MemeDigest), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void CopySelectedButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        List<string> paths = GetSelectedAbsolutePaths();
        if (paths.Count == 0)
        {
            return;
        }

        if (ExportService.CopyFileListToClipboard(paths))
        {
            SetStatus(paths.Count + " file(s) on the clipboard — paste into Discord with Ctrl+V.");
        }
        else
        {
            SetStatus("Could not place files on the clipboard (another app may hold it).");
        }
    }

    private void MoveSelectedButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        HandleMoveOrCopyToFolder(move: true);
    }

    private void CopyToFolderButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        HandleMoveOrCopyToFolder(move: false);
    }

    private async void HandleMoveOrCopyToFolder(bool move)
    {
        List<string> absolutePaths = GetSelectedAbsolutePaths();
        List<string> relativePaths = GetSelectedRelativePaths();
        if (absolutePaths.Count == 0)
        {
            return;
        }

        string? targetDirectory = PromptForFolder("Select target folder to " + (move ? "move" : "copy") + " selected memes");
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return;
        }

        int completedCount = await Task.Run(() => ExportService.MoveOrCopy(absolutePaths, targetDirectory, move));
        SetStatus(completedCount + " of " + absolutePaths.Count + " file(s) " + (move ? "moved" : "copied") + " to " + targetDirectory);

        if (move && completedCount > 0)
        {
            // Moved sources are gone from the library: mark picked so they never draw again,
            // remove their cards, then refill empty slots.
            foreach (string relativePath in relativePaths)
            {
                await RecordHistoryEntryAsync(MemeHistoryState.Picked, relativePath);
            }

            RemoveSelectedCards();
            await ReplaceConsumedCardsAsync();
        }
    }

    private void RemoveSelectedCards()
    {
        for (int index = galleryCards.Count - 1; index >= 0; index--)
        {
            bool isSelected;
            if (selectedByRelativePath.TryGetValue(galleryCards[index].Media.RelativePath, out isSelected) && isSelected)
            {
                DisposeCardMedia(galleryCards[index]);
                galleryCards[index] = new GalleryCard(
                    new MediaFileInfo(string.Empty, string.Empty, MediaKind.Unsupported),
                    thumbnailService,
                    MaxSelectionCount,
                    isPlaceholder: true);
                GalleryPanel.Children[index] = galleryCards[index];
            }
        }

        viewerCardIndex = -1;
        GalleryHeaderTextBlock.Text = GalleryHeaderTemplate();
    }

    // ── Settings dialog ──

    private async Task ShowSettingsDialogAsync(bool firstRun)
    {
        SettingsWindow dialog = new SettingsWindow(settings)
        {
            Owner = this
        };

        bool? dialogResult = dialog.ShowDialog();
        if (dialogResult == true)
        {
            SaveSettings();
            if (!settings.IsLibraryPathValid)
            {
                SetStatus("Set a valid library path in Settings to start drawing memes.");
                return;
            }

            thumbnailService.InvalidateCache();
            await ReloadGalleryAsync();
        }
        else if (firstRun)
        {
            SetStatus("No library configured — open Settings to choose one.");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _ = ShowSettingsDialogAsync(firstRun: false);
    }

    // ── Folder picker ──

    private string? PromptForFolder(string title)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            ShowNewFolderButton = true
        };
        System.Windows.Forms.DialogResult dialogResult = dialog.ShowDialog(this);
        if (dialogResult == System.Windows.Forms.DialogResult.OK)
        {
            return dialog.SelectedPath;
        }

        return null;
    }

    // ── Card media lifecycle ──

    private void DisposeCardMedia(GalleryCard card)
    {
        card.ReleaseMedia();
    }

    private void DisposeAllCardMedia()
    {
        foreach (GalleryCard card in galleryCards)
        {
            DisposeCardMedia(card);
        }
    }

    // ── Window closing ──

    protected override void OnClosing(System.ComponentModel.CancelEventArgs eventArgs)
    {
        base.OnClosing(eventArgs);
        DisposeAllCardMedia();
        try
        {
            history.SaveToFile(settings.HistoryDirectory);
        }
        catch (Exception)
        {
            // Shutting down; nothing sensible can be done about a save failure here.
        }
    }
}