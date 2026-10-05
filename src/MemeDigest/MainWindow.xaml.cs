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

    /// <summary>Set while a viewer/batch/move mutation runs, so the same logical operation cannot re-enter.</summary>
    private bool isMutatingGallery;

    private UserHistory history;

    private readonly Dictionary<string, bool> selectedByRelativePath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    private readonly List<GalleryCard> galleryCards = new List<GalleryCard>();

    private readonly Random random = new Random();

    private bool suppressProfileEvents;

    private int viewerCardIndex;

    // ── Nested types ──

    private enum GalleryTab
    {
        Drawer,

        Kept,

        Discarded
    }

    // ── Video player state ──

    private bool isVideoPlaying;

    private bool isSeekDragging;

    /// <summary>Set while the slider is updated from playback so ValueChanged does not echo back a seek.</summary>
    private bool suppressSeekSliderEvents;

    private readonly System.Windows.Threading.DispatcherTimer viewerClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

    // ── Tab state ──

    /// <summary>Which main tab is active — drives which panel the viewer/selection logic touches.</summary>
    private GalleryTab activeTab = GalleryTab.Drawer;

    private int historyLoadVersion;

    private readonly List<GalleryCard> keptCards = new List<GalleryCard>();

    private readonly List<GalleryCard> discardedCards = new List<GalleryCard>();

    /// <summary>Selection state per tab: Drawer uses selectedByRelativePath; history tabs use historySelection.</summary>
    private readonly HashSet<string> historySelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ── Construction ──

    public MainWindow()
    {
        InitializeComponent();
        settings = AppSettings.LoadOrCreate();
        scanner = new MemeLibraryScanner();
        thumbnailService = new ThumbnailService(settings);
        history = UserHistory.LoadFromFile(settings.HistoryDirectory, settings.CurrentUserProfile);
        history.CollapseToLatestState();
        viewerClock.Tick += ViewerClock_Tick;
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
        string buildTag = string.IsNullOrWhiteSpace(BuildInfo.Sha) ? "dev" : (BuildInfo.Sha.Length > 8 ? BuildInfo.Sha[..8] : BuildInfo.Sha);
        Title = nameof(MemeDigest) + " [" + buildTag + "] — " + settings.CurrentUserProfile;
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

            // All UI-tree mutation happens synchronously here on the UI thread:
            // the dispatcher is the gallery lock — no await may carry the mutation across threads.
            CloseViewer();
            viewerCardIndex = -1;
            await RenderGalleryAsync(drawn);

            SaveSettings();

            if (drawn.Count == 0)
            {
                SetStatus("Nothing new to draw — every meme was already seen (or the library is empty).");
            }
            else
            {
                bool hasVideos = false;
                foreach (MediaFileInfo item in drawn)
                {
                    if (item.Kind == MediaKind.Video)
                    {
                        hasVideos = true;
                        break;
                    }
                }

                string ffmpegHint = hasVideos && !thumbnailService.FfmpegAvailable
                    ? "  Video previews disabled — ffmpeg not found (set its path in Settings)."
                    : string.Empty;
                SetStatus("Drew " + drawn.Count + " meme" + (drawn.Count == 1 ? string.Empty : "s") + "." + ffmpegHint);
            }
        }
        finally
        {
            loadGate.Release();
            DrawButton.IsEnabled = true;
        }
    }

    // ── Gallery rendering ──

    /// <summary>Builds all cards. Must run on the UI thread — touches galleryCards and GalleryPanel.</summary>
    private Task RenderGalleryAsync(List<MediaFileInfo> drawn)
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
        return Task.CompletedTask;
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
        if (activeTab == GalleryTab.Drawer)
        {
            selectedByRelativePath[card.Media.RelativePath] = card.IsSelected;
        }
        else
        {
            if (card.IsSelected)
            {
                historySelection.Add(card.Media.RelativePath);
            }
            else
            {
                historySelection.Remove(card.Media.RelativePath);
            }
        }

        UpdateExportButtonStates();
    }

    private List<string> GetSelectedAbsolutePaths()
    {
        List<string> paths = new List<string>();
        foreach (GalleryCard card in ActiveCards())
        {
            if (!card.IsPlaceholder && card.IsSelected)
            {
                paths.Add(card.Media.AbsolutePath);
            }
        }

        return paths;
    }

    private List<string> GetSelectedRelativePaths()
    {
        List<string> paths = new List<string>();
        foreach (GalleryCard card in ActiveCards())
        {
            if (!card.IsPlaceholder && card.IsSelected)
            {
                paths.Add(card.Media.RelativePath);
            }
        }

        return paths;
    }

    private void UpdateExportButtonStates()
    {
        // All decisions read the ACTIVE tab's cards. (Mixed sources used to leave
        // buttons stuck disabled on Kept/Discarded, because the check peeked at the
        // drawer's stale batch instead of the visible cards.)
        List<GalleryCard> activeCards = ActiveCards();
        bool hasSelection = false;
        bool hasSelectableCard = false;
        foreach (GalleryCard card in activeCards)
        {
            if (card.IsPlaceholder)
            {
                continue;
            }

            if (card.IsSelected)
            {
                hasSelection = true;
            }
            else
            {
                hasSelectableCard = true;
            }

            if (hasSelection && hasSelectableCard)
            {
                break;
            }
        }

        // Keep/discard-all only act on the drawer; on history tabs they are a hint.

        SelectAllButton.IsEnabled = hasSelectableCard;
        DeselectAllButton.IsEnabled = hasSelection;
        KeepAllButton.IsEnabled = hasSelection;
        DiscardAllButton.IsEnabled = hasSelection;
        RevealSelectedButton.IsEnabled = hasSelection;
        CopySelectedButton.IsEnabled = hasSelection;
        MoveSelectedButton.IsEnabled = hasSelection;
        CopyToFolderButton.IsEnabled = hasSelection;
    }

    // ── Replacement after discard/keep/move ──

    private async Task ReplaceConsumedCardsAsync()
    {
        int slotsToRefill = 0;
        HashSet<string> currentlyDisplayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GalleryCard card in galleryCards)
        {
            if (card.IsPlaceholder)
            {
                slotsToRefill++;
            }
            else
            {
                currentlyDisplayed.Add(card.Media.RelativePath);
            }
        }

        if (slotsToRefill == 0)
        {
            return;
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

        // Update the card list first, then rebuild the panel in one pass — no index-based
        // writes to Children anywhere (a stale index would double-attach a visual).
        int replacementIndex = 0;
        for (int slotIndex = 0; slotIndex < galleryCards.Count && replacementIndex < replacements.Count; slotIndex++)
        {
            if (!galleryCards[slotIndex].IsPlaceholder)
            {
                continue;
            }

            GalleryCard replacement = new GalleryCard(replacements[replacementIndex], thumbnailService, MaxSelectionCount);
            replacement.ThumbnailClicked += GalleryCard_ThumbnailClicked;
            replacement.SelectionChanged += GalleryCard_SelectionChanged;
            galleryCards[slotIndex] = replacement;
            replacementIndex++;
        }

        RebuildGalleryPanel();

        GalleryHeaderTextBlock.Text = GalleryHeaderTemplate();
        SetStatus("Replaced " + replacementIndex + " empty slot" + (replacementIndex == 1 ? string.Empty : "s") + " with new random memes.");
    }

    /// <summary>
    /// Rebuilds the gallery panel from galleryCards in a single pass. The only place
    /// that detaches/attaches visuals for slot replacements — a card can be attached
    /// exactly once because the source list cannot contain it twice.
    /// </summary>
    private void RebuildGalleryPanel()
    {
        GalleryPanel.Children.Clear();
        foreach (GalleryCard card in galleryCards)
        {
            GalleryPanel.Children.Add(card);
        }
    }

    // ── History tabs (Kept / Discarded) ──

    /// <summary>Loads the active history tab's cards from the drawn library + history.</summary>
            private async Task LoadHistoryTabAsync()
    {
        GalleryTab requestedTab = activeTab;
        MemeHistoryState state = requestedTab == GalleryTab.Kept ? MemeHistoryState.Picked : MemeHistoryState.Discarded;
        SetStatus("Scanning library…");

        int loadVersion = ++historyLoadVersion;
        // Background: validate which history entries still exist on disk.
        HashSet<string> existingRelativePaths = await Task.Run(() =>
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MemeHistoryEntry entry in history.Entries)
            {
                if (entry.State != state)
                {
                    continue;
                }

                string absolutePath = Path.Combine(settings.CurrentLibraryPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(absolutePath))
                {
                    result.Add(entry.RelativePath);
                }
            }

            return result;
        });

        // A newer load (rapid tab switching) supersedes this one entirely.
        if (loadVersion != historyLoadVersion)
        {
            return;
        }

        if (requestedTab == GalleryTab.Kept)
        {
            ReloadHistoryTabData(KeptPanel, KeptHeaderTextBlock, keptCards, state, existingRelativePaths);
        }
        else if (requestedTab == GalleryTab.Discarded)
        {
            ReloadHistoryTabData(DiscardedPanel, DiscardedHeaderTextBlock, discardedCards, state, existingRelativePaths);
        }

        SetStatus((requestedTab == GalleryTab.Kept ? "Kept" : "Discarded") + " tab loaded: " + (requestedTab == GalleryTab.Kept ? keptCards.Count : discardedCards.Count) + " item(s).");
    }

    /// <summary>Synchronous rebuild of one history tab from `history` (caller owns the mutation flag).</summary>
    private void ReloadHistoryTabData(WrapPanel targetPanel, TextBlock targetHeader, List<GalleryCard> targetCards, MemeHistoryState state, HashSet<string>? existingRelativePaths = null)
    {
        foreach (GalleryCard card in targetCards)
        {
            card.ReleaseMedia();
        }

        targetCards.Clear();
        targetPanel.Children.Clear();
        historySelection.Clear();

        foreach (MemeHistoryEntry entry in history.Entries)
        {
            if (entry.State != state)
            {
                continue;
            }

            if (existingRelativePaths != null && !existingRelativePaths.Contains(entry.RelativePath))
            {
                continue;
            }

            string absolutePath = Path.Combine(settings.CurrentLibraryPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            GalleryCard card = new GalleryCard(new MediaFileInfo(absolutePath, entry.RelativePath.Replace('\\', '/'), ResolveMediaKind(Path.GetExtension(absolutePath))), thumbnailService, MaxSelectionCount);
            card.ThumbnailClicked += GalleryCard_ThumbnailClicked;
            card.SelectionChanged += GalleryCard_SelectionChanged;
            targetCards.Add(card);
            targetPanel.Children.Add(card);
        }

        targetHeader.Text = (state == MemeHistoryState.Picked ? "Kept: " : "Discarded: ") + targetCards.Count
            + " meme" + (targetCards.Count == 1 ? string.Empty : "s") + " — tick, then Reset selected returns them to the drawer pool";
        UpdateExportButtonStates();
    }

/// <summary>Extension → MediaKind mapping for history-tab cards (library scan does its own).</summary>
    private MediaKind ResolveMediaKind(string extension)
    {
        if (settings.IsKnownImageExtension(extension))
        {
            return MediaKind.Image;
        }

        if (settings.IsKnownVideoExtension(extension))
        {
            return MediaKind.Video;
        }

        return MediaKind.Unsupported;
    }

    private async void ResetSelectedButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (isMutatingGallery || activeTab == GalleryTab.Drawer)
        {
            return;
        }

        List<string> toReset = new List<string>(historySelection);
        if (toReset.Count == 0)
        {
            SetStatus("Nothing selected to reset.");
            return;
        }

        MemeHistoryState state = activeTab == GalleryTab.Kept ? MemeHistoryState.Picked : MemeHistoryState.Discarded;
        foreach (string relativePath in toReset)
        {
            history.RemoveAll(state, relativePath);
        }

        // Reset removed entries: the other tab may still show stale copies, so both
        // history panels reload. Reload runs AFTER isMutatingGallery clears (it
        // early-returns while the flag is up).
        await Task.Run(() => history.SaveToFile(settings.HistoryDirectory));
        RefreshHistorySummary();
        isMutatingGallery = true;
        try
        {
            ReloadHistoryTabData(activeTab == GalleryTab.Kept ? KeptPanel : DiscardedPanel,
                activeTab == GalleryTab.Kept ? KeptHeaderTextBlock : DiscardedHeaderTextBlock,
                activeTab == GalleryTab.Kept ? keptCards : discardedCards,
                state);
        }
        finally
        {
            isMutatingGallery = false;
        }

        SetStatus(toReset.Count + " meme(s) returned to the drawable pool.");
    }


    // ── Viewer ──

    private void OpenViewer(GalleryCard card)
    {
        List<GalleryCard> cards = ActiveCards();
        for (int index = 0; index < cards.Count; index++)
        {
            if (ReferenceEquals(cards[index], card))
            {
                viewerCardIndex = index;
                ShowViewerForCurrentIndex();
                return;
            }
        }
    }

    private void ShowViewerForCurrentIndex()
    {
        List<GalleryCard> cards = ActiveCards();
        if (viewerCardIndex < 0 || viewerCardIndex >= cards.Count)
        {
            CloseViewer();
            return;
        }

        GalleryCard card = cards[viewerCardIndex];
        if (card.IsPlaceholder)
        {
            CloseViewer();
            return;
        }

        ViewerOverlay.Visibility = Visibility.Visible;
        ViewerOverlay.Focus();
        ViewerCaptionTextBlock.Text = card.Media.RelativePath + "   [" + (viewerCardIndex + 1) + "/" + cards.Count + "]" + (card.IsSelected ? "   ✓ selected" : string.Empty);
        ShowViewerMedia(card.Media);
    }

    private void ShowViewerMedia(MediaFileInfo media)
    {
        ViewerMediaErrorTextBlock.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        ViewerImage.Visibility = Visibility.Collapsed;
        StopViewerClock();
        StopViewerVideo();

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
                ViewerPlayPauseButton.Visibility = Visibility.Collapsed;
                ViewerSeekSlider.Visibility = Visibility.Collapsed;
                ViewerTimeTextBlock.Visibility = Visibility.Collapsed;
                ViewerMuteButton.Visibility = Visibility.Collapsed;
                ViewerVolumeSlider.Visibility = Visibility.Collapsed;
            }
            else
            {
                suppressSeekSliderEvents = true;
                ViewerSeekSlider.Value = 0;
                ViewerSeekSlider.Maximum = 100;
                suppressSeekSliderEvents = false;
                ViewerTimeTextBlock.Text = "00:00 / 00:00";
                ViewerPlayPauseButton.Visibility = Visibility.Visible;
                ViewerPlayPauseButton.Content = "⏸ Pause";
                ViewerSeekSlider.Visibility = Visibility.Visible;
                ViewerTimeTextBlock.Visibility = Visibility.Visible;
                ViewerMuteButton.Visibility = Visibility.Visible;
                ViewerVolumeSlider.Visibility = Visibility.Visible;
                ViewerVideo.Volume = ClampVolume(settings.VideoPlaybackVolume);
                ViewerVideo.Visibility = Visibility.Visible;
                isVideoPlaying = true;
                ViewerVideo.Source = new Uri(media.AbsolutePath, UriKind.Absolute);
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
        StopViewerClock();
        StopViewerVideo();
        ViewerImage.Source = null;
        ViewerOverlay.Visibility = Visibility.Collapsed;
        viewerCardIndex = -1;
    }

    private void StopViewerVideo()
    {
        isVideoPlaying = false;
        isSeekDragging = false;
        ViewerVideo.Stop();
        ViewerVideo.Close();
        ViewerVideo.Source = null;
        ViewerVideo.Visibility = Visibility.Collapsed;
    }

    private void StopViewerClock()
    {
        viewerClock.Stop();
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
        List<GalleryCard> cards = ActiveCards();
        if (cards.Count == 0)
        {
            return;
        }

        int nextIndex = viewerCardIndex + delta;
        if (nextIndex < 0)
        {
            nextIndex = cards.Count - 1;
        }
        else if (nextIndex >= cards.Count)
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
        if (isMutatingGallery)
        {
            return;
        }

        // Snapshot synchronously, then record history; every UI-tree write below happens
        // in a synchronous block on the UI thread (the dispatcher is the gallery lock).
        List<GalleryCard> cards = ActiveCards();
        if (viewerCardIndex < 0 || viewerCardIndex >= cards.Count)
        {
            return;
        }

        GalleryCard card = cards[viewerCardIndex];
        MediaFileInfo consumedMedia = card.Media;
        int consumedIndex = viewerCardIndex;
        GalleryTab viewerTab = activeTab;

        isMutatingGallery = true;
        try
        {
            await RecordHistoryEntryAsync(state, consumedMedia.RelativePath);

            if (viewerTab == GalleryTab.Drawer)
            {
                int cardIndex = FindCardIndex(card);

                // List update first, then one-pass panel rebuild — never `Children[index] = ...`.
                if (cardIndex >= 0)
                {
                    DisposeCardMedia(card);
                    galleryCards[cardIndex] = CreatePlaceholderCard();
                    RebuildGalleryPanel();
                }

                RefreshHistorySummary();
                UpdateExportButtonStates();

                // Keep/discard should chain: advance straight to the next drawn meme.
                int nextIndex = FindNextViewableIndex(consumedIndex);
                if (nextIndex >= 0)
                {
                    viewerCardIndex = nextIndex;
                    ShowViewerForCurrentIndex();
                }
                else
                {
                    CloseViewer();
                    SetStatus("All memes in this batch were processed — press Draw for a fresh one.");
                }

                await ReplaceConsumedCardsAsync();
            }
            else
            {
                // History tab: the meme already has its state recorded; just move on.
                RefreshHistorySummary();
                int nextIndex = FindNextViewableIndex(consumedIndex);
                if (nextIndex >= 0)
                {
                    viewerCardIndex = nextIndex;
                    ShowViewerForCurrentIndex();
                }
                else
                {
                    CloseViewer();
                    SetStatus("All memes in this tab were viewed.");
                }
            }
        }
        finally
        {
            isMutatingGallery = false;
        }
    }

    /// <summary>
    /// First non-placeholder card searching forward from consumedIndex (wrapping);
    /// -1 when nothing viewable remains.
    /// </summary>
    private int FindNextViewableIndex(int consumedIndex)
    {
        List<GalleryCard> cards = ActiveCards();
        int totalCards = cards.Count;
        if (totalCards == 0)
        {
            return -1;
        }

        for (int offset = 1; offset <= totalCards; offset++)
        {
            int index = (consumedIndex + offset) % totalCards;
            if (!cards[index].IsPlaceholder)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Keep/discard every currently-selected card in one pass (batch buttons in the export toolbar).</summary>
    private async Task MarkSelectedCards(MemeHistoryState state)
    {
        if (isMutatingGallery)
        {
            return;
        }

        // History tabs: keep/discard-all is a no-op there (cards are already in history);
        // the reset flow handles moving entries back to the drawable pool.
        if (activeTab != GalleryTab.Drawer)
        {
            SetStatus("This tab already has its history recorded — use Reset selected to draw memes again.");
            return;
        }

        // Snapshot synchronously on the UI thread before any await.
        List<GalleryCard> selectedCards = new List<GalleryCard>();
        foreach (GalleryCard card in galleryCards)
        {
            if (card.IsPlaceholder || !card.IsSelected)
            {
                continue;
            }

            selectedCards.Add(card);
        }

        if (selectedCards.Count == 0)
        {
            SetStatus("Nothing selected — tick one or more cards first (or press Select all).");
            return;
        }

        isMutatingGallery = true;
        try
        {
            foreach (GalleryCard card in selectedCards)
            {
                await RecordHistoryEntryAsync(state, card.Media.RelativePath);
            }

            CloseViewer();
            viewerCardIndex = -1;

            // Phase 1: replace consumed cards in the list only (no visual tree writes yet).
            HashSet<GalleryCard> consumedSet = new HashSet<GalleryCard>(selectedCards);
            for (int index = 0; index < galleryCards.Count; index++)
            {
                if (consumedSet.Contains(galleryCards[index]))
                {
                    DisposeCardMedia(galleryCards[index]);
                    galleryCards[index] = CreatePlaceholderCard();
                }
            }

            // Phase 2: rebuild the panel from the list — no index-based writes to Children.
            RebuildGalleryPanel();

            RefreshHistorySummary();
            UpdateExportButtonStates();
            await ReplaceConsumedCardsAsync();
        }
        finally
        {
            isMutatingGallery = false;
        }
    }

    private async void KeepAllButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await MarkSelectedCards(MemeHistoryState.Picked);
    }

    private async void DiscardAllButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await MarkSelectedCards(MemeHistoryState.Discarded);
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        int selectedCount = GetSelectedAbsolutePaths().Count;
        foreach (GalleryCard card in ActiveCards())
        {
            if (card.IsPlaceholder || card.IsSelected)
            {
                continue;
            }

            if (selectedCount >= MaxSelectionCount)
            {
                SetStatus("Selection limit reached (" + MaxSelectionCount + ") — deselect something first.");
                break;
            }

            card.SetSelected(true);
            selectedCount++;
        }
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        foreach (GalleryCard card in ActiveCards())
        {
            if (!card.IsPlaceholder && card.IsSelected)
            {
                card.SetSelected(false);
            }
        }
    }

    private GalleryCard CreatePlaceholderCard()
    {
        return new GalleryCard(
            new MediaFileInfo(string.Empty, string.Empty, MediaKind.Unsupported),
            thumbnailService,
            MaxSelectionCount,
            isPlaceholder: true);
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
        // Exactly one state per meme: drop stale entries in the other section
        // (legacy dual-written files or an explicit state flip), then record the
        // section matching the button that was pressed.
        MemeHistoryState oppositeState = state == MemeHistoryState.Picked
            ? MemeHistoryState.Discarded
            : MemeHistoryState.Picked;

        history.RemoveAll(oppositeState, relativePath);

        if (!history.Contains(state, relativePath))
        {
            history.Add(state, relativePath, DateTime.UtcNow);
        }

        await Task.Run(() => history.SaveToFile(settings.HistoryDirectory));
    }

    private void ViewerCloseButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        CloseViewer();
    }

    // ── Video player controls ──

    private void ViewerPlayPauseButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewerVideo.Visibility != Visibility.Visible || ViewerVideo.Source == null)
        {
            return;
        }

        if (isVideoPlaying)
        {
            ViewerVideo.Pause();
            SetVideoPlaying(false);
        }
        else
        {
            ViewerVideo.Play();
            SetVideoPlaying(true);
        }
    }

    private void SetVideoPlaying(bool playing)
    {
        isVideoPlaying = playing;
        ViewerPlayPauseButton.Content = playing ? "⏸ Pause" : "▶ Play";
        if (playing)
        {
            viewerClock.Start();
        }
        else
        {
            viewerClock.Stop();
        }
    }

    private void ViewerVideo_MediaOpened(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewerVideo.NaturalDuration.HasTimeSpan)
        {
            suppressSeekSliderEvents = true;
            ViewerSeekSlider.Maximum = ViewerVideo.NaturalDuration.TimeSpan.TotalSeconds;
            suppressSeekSliderEvents = false;
            viewerClock.Start();
        }
    }

    private void ViewerVideo_MediaEnded(object sender, RoutedEventArgs eventArgs)
    {
        SetVideoPlaying(false);
        ViewerPlayPauseButton.Content = "▶ Replay";
    }

    private void ViewerClock_Tick(object? sender, EventArgs eventArgs)
    {
        if (isSeekDragging || ViewerVideo.Source == null || !ViewerVideo.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        suppressSeekSliderEvents = true;
        ViewerSeekSlider.Value = ViewerVideo.Position.TotalSeconds;
        suppressSeekSliderEvents = false;
        ViewerTimeTextBlock.Text = FormatViewerTime(ViewerVideo.Position) + " / " + FormatViewerTime(ViewerVideo.NaturalDuration.TimeSpan);
    }

    private void ViewerSeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs eventArgs)
    {
        isSeekDragging = true;
    }

    private void ViewerSeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs eventArgs)
    {
        isSeekDragging = false;
        ApplyViewerSeek();
    }

    private void ViewerSeekSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (suppressSeekSliderEvents || isSeekDragging)
        {
            return;
        }

        ApplyViewerSeek();
    }

    private void ApplyViewerSeek()
    {
        if (ViewerVideo.Source == null || !ViewerVideo.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        double targetSeconds = ViewerSeekSlider.Value;
        if (targetSeconds < ViewerVideo.NaturalDuration.TimeSpan.TotalSeconds)
        {
            ViewerVideo.Position = TimeSpan.FromSeconds(targetSeconds);
        }
    }

    private void ViewerMuteButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        bool wasMuted = ViewerVideo.Volume <= 0.0001;
        double newVolume = wasMuted ? 0.6 : 0.0;
        ViewerVideo.Volume = newVolume;
        settings.VideoPlaybackVolume = newVolume;
        ViewerVolumeSlider.Value = newVolume;
    }

    private void ViewerVolumeSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (ViewerVideo == null)
        {
            return;
        }

        double clamped = ClampVolume(eventArgs.NewValue);
        ViewerVideo.Volume = clamped;
        settings.VideoPlaybackVolume = clamped;
        ViewerMuteButton.Content = clamped <= 0.0001 ? "🔇" : "🔊";
    }

    private static double ClampVolume(double value)
    {
        if (value < 0.0)
        {
            return 0.0;
        }

        if (value > 1.0)
        {
            return 1.0;
        }

        return value;
    }

    private static string FormatViewerTime(TimeSpan time)
    {
        return time.Hours > 0
            ? time.ToString("h\\:mm\\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : time.ToString("m\\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Keyboard handling ──

    /// <summary>
    /// Window-tunnel handler: PreviewKeyDown starts at the window, so it fires for
    /// EVERY key regardless of which child holds keyboard focus. This is the single
    /// routing point for viewer shortcuts — clicking any button/slider in the viewer
    /// can move focus freely without breaking Enter/Space/arrows.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
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
            case Key.Enter when !eventArgs.IsRepeat:
                _ = MarkCurrentViewerCard(MemeHistoryState.Picked);
                eventArgs.Handled = true;
                break;
            case Key.Space when !eventArgs.IsRepeat:
                _ = MarkCurrentViewerCard(MemeHistoryState.Discarded);
                eventArgs.Handled = true;
                break;
            case Key.C when System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Control:
                CopyCurrentViewerMediaToClipboard();
                eventArgs.Handled = true;
                break;
            case Key.X when System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Control:
                CutCurrentViewerMediaToClipboard();
                eventArgs.Handled = true;
                break;
        }
    }

    // ── Viewer export actions ──

    private void CopyCurrentViewerMediaToClipboard()
    {
        List<GalleryCard> cards = ActiveCards();
        if (viewerCardIndex < 0 || viewerCardIndex >= cards.Count)
        {
            return;
        }

        string absolutePath = cards[viewerCardIndex].Media.AbsolutePath;
        bool copied = ExportService.CopyFileListToClipboard(new[] { absolutePath });
        SetStatus(copied ? "Copied to clipboard — paste with Ctrl+V." : "Clipboard copy failed (another app may be holding the clipboard).");
    }

    /// <summary>
    /// Puts the file on the clipboard as a cut operation: pasting into Explorer moves
    /// the file, pasting into Discord still uploads it as a normal file drop.
    /// </summary>
    private void CutCurrentViewerMediaToClipboard()
    {
        List<GalleryCard> cards = ActiveCards();
        if (viewerCardIndex < 0 || viewerCardIndex >= cards.Count)
        {
            return;
        }

        string absolutePath = cards[viewerCardIndex].Media.AbsolutePath;
        try
        {
            System.Windows.DataObject dataObject = new System.Windows.DataObject();
            dataObject.SetFileDropList(new System.Collections.Specialized.StringCollection { absolutePath });
            byte[] preferredDropEffect = new byte[4];
            System.BitConverter.GetBytes(2).CopyTo(preferredDropEffect, 0); // DROPEFFECT_MOVE = 2
            dataObject.SetData("Preferred DropEffect", preferredDropEffect);
            Clipboard.SetDataObject(dataObject, copy: true);
            SetStatus("Cut to clipboard — paste in Explorer to move the file.");
        }
        catch (Exception)
        {
            SetStatus("Clipboard cut failed (another app may be holding the clipboard).");
        }
    }

    private void ViewerCopyButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        CopyCurrentViewerMediaToClipboard();
    }

    private void ViewerRevealButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        List<GalleryCard> cards = ActiveCards();
        if (viewerCardIndex < 0 || viewerCardIndex >= cards.Count)
        {
            return;
        }

        ExportService.RevealInExplorer(new[] { cards[viewerCardIndex].Media.AbsolutePath });
    }

    private void ViewerBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        CloseViewer();
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
        history.CollapseToLatestState();
        settings.CurrentUserProfile = profileName;
        SaveSettings();
        UpdateTitle();
        RefreshHistorySummary();
        DisposeAllCardMedia();
        GalleryPanel.Children.Clear();
        galleryCards.Clear();
        selectedByRelativePath.Clear();
        ClearHistoryTabPanels(disposeMedia: true);
        activeTab = GalleryTab.Drawer;
        suppressProfileEvents = true;
        DrawerTabButton.IsChecked = true;
        suppressProfileEvents = false;
        ApplyTabVisibility();
        GalleryHeaderTextBlock.Text = "Profile switched to \"" + profileName + "\" — draw fresh memes";
        UpdateExportButtonStates();
        SetStatus("Switched to profile: " + profileName);
    }

    /// <summary>Clears Kept/Discarded tab lists and panels (called on tab reload paths that need full disposal).</summary>
    private void ClearHistoryTabPanels(bool disposeMedia)
    {
        foreach (GalleryCard card in keptCards)
        {
            if (disposeMedia)
            {
                card.ReleaseMedia();
            }
        }

        keptCards.Clear();
        KeptPanel.Children.Clear();
        KeptHeaderTextBlock.Text = string.Empty;

        foreach (GalleryCard card in discardedCards)
        {
            if (disposeMedia)
            {
                card.ReleaseMedia();
            }
        }

        discardedCards.Clear();
        DiscardedPanel.Children.Clear();
        DiscardedHeaderTextBlock.Text = string.Empty;
        historySelection.Clear();
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
        history.CollapseToLatestState();
        UpdateTitle();
        RefreshHistorySummary();
        DisposeAllCardMedia();
        GalleryPanel.Children.Clear();
        galleryCards.Clear();
        selectedByRelativePath.Clear();
        ClearHistoryTabPanels(disposeMedia: true);
        activeTab = GalleryTab.Drawer;
        suppressProfileEvents = true;
        DrawerTabButton.IsChecked = true;
        suppressProfileEvents = false;
        ApplyTabVisibility();
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
        if (isMutatingGallery)
        {
            return;
        }

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

        isMutatingGallery = true;
        try
        {
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
        finally
        {
            isMutatingGallery = false;
        }
    }

    private void RemoveSelectedCards()
    {
        for (int index = galleryCards.Count - 1; index >= 0; index--)
        {
            string relativePath = galleryCards[index].Media.RelativePath;
            bool isSelected;
            if (selectedByRelativePath.TryGetValue(relativePath, out isSelected) && isSelected)
            {
                DisposeCardMedia(galleryCards[index]);
                galleryCards[index] = CreatePlaceholderCard();
                selectedByRelativePath.Remove(relativePath);
            }
        }

        viewerCardIndex = -1;
        RebuildGalleryPanel();
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

            thumbnailService.ResetFfmpegResolution();
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

    // ── Tabs ──

    private void DrawerTabButton_Checked(object sender, RoutedEventArgs eventArgs)
    {
        SwitchTab(GalleryTab.Drawer);
    }

    private void KeptTabButton_Checked(object sender, RoutedEventArgs eventArgs)
    {
        SwitchTab(GalleryTab.Kept);
    }

    private void DiscardedTabButton_Checked(object sender, RoutedEventArgs eventArgs)
    {
        SwitchTab(GalleryTab.Discarded);
    }

    private void SwitchTab(GalleryTab tab)
    {
        if (activeTab == tab)
        {
            return;
        }

        activeTab = tab;
        CloseViewer();
        UpdateExportButtonStates();

        if (tab == GalleryTab.Drawer)
        {
            ApplyTabVisibility();
            SetStatus("Ready — press Draw for fresh memes.");
        }
        else
        {
            ApplyTabVisibility();
            _ = LoadHistoryTabAsync();
        }
    }

    private void ApplyTabVisibility()
    {
        DrawerTabContent.Visibility = activeTab == GalleryTab.Drawer ? Visibility.Visible : Visibility.Collapsed;
        KeptPanelHost.Visibility = activeTab == GalleryTab.Kept ? Visibility.Visible : Visibility.Collapsed;
        DiscardedPanelHost.Visibility = activeTab == GalleryTab.Discarded ? Visibility.Visible : Visibility.Collapsed;
        ResetSelectedButton.Visibility = activeTab == GalleryTab.Drawer ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Card list of the active tab — viewer/selection logic routes through this.</summary>
    private List<GalleryCard> ActiveCards()
    {
        return activeTab switch
        {
            GalleryTab.Kept => keptCards,
            GalleryTab.Discarded => discardedCards,
            _ => galleryCards
        };
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
        CloseViewer();
        DisposeAllCardMedia();
        ClearHistoryTabPanels(disposeMedia: true);
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
