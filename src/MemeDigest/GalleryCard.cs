using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MemeDigest;

/// <summary>
/// One gallery tile: thumbnail, video badge, filename and a selection checkbox.
/// A card whose Media has an empty RelativePath renders as an empty slot.
/// </summary>
public sealed class GalleryCard : UserControl
{
    // ── Constants ──

    private const int CardWidth = 232;

    private const int ThumbnailHeight = 168;

    // ── Events ──

    public event Action<GalleryCard>? ThumbnailClicked;

    public event Action<GalleryCard>? SelectionChanged;

    // ── Properties ──

    public MediaFileInfo Media { get; private set; }

    public bool IsSelected
    {
        get { return selectCheckBox.IsChecked == true; }
    }

    public bool IsPlaceholder
    {
        get { return isPlaceholder; }
    }

    /// <summary>Sets the selection checkbox state directly (used by select-all / deselect-all).</summary>
    public void SetSelected(bool isSelected)
    {
        selectCheckBox.IsChecked = isSelected;
    }

    // ── Fields ──

    private readonly ThumbnailService thumbnailService;

    private readonly Image thumbnailImage;

    private readonly TextBlock tileMessageTextBlock;

    private readonly Border videoBadgeBorder;

    private readonly CheckBox selectCheckBox;

    private readonly TextBlock fileNameTextBlock;

    private readonly Border tileBorder;

    private bool isPlaceholder;

    // ── Construction ──

    public GalleryCard(MediaFileInfo media, ThumbnailService thumbnailService, int maxSelectionCount, bool isPlaceholder = false)
    {
        Media = media;
        this.thumbnailService = thumbnailService;
        this.isPlaceholder = isPlaceholder;

        Width = CardWidth;
        Margin = new Thickness(8);
        ContextMenu = null;

        // ── Tile (thumbnail area) ──
        tileBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x4E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Height = ThumbnailHeight,
            Cursor = Cursors.Hand
        };

        var tileGrid = new Grid();

        thumbnailImage = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(4),
            Visibility = Visibility.Collapsed
        };
        tileGrid.Children.Add(thumbnailImage);

        tileMessageTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0xA0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };
        tileGrid.Children.Add(tileMessageTextBlock);

        videoBadgeBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            Child = new TextBlock
            {
                Text = "▶ VIDEO",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            },
            Visibility = Visibility.Collapsed
        };
        tileGrid.Children.Add(videoBadgeBorder);

        tileBorder.Child = tileGrid;
        tileBorder.MouseLeftButtonDown += TileBorder_MouseLeftButtonDown;

        // ── Bottom bar (selection + name) ──
        selectCheckBox = new CheckBox
        {
            Content = "Select",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD8)),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 64,
            IsEnabled = !isPlaceholder
        };
        selectCheckBox.Checked += SelectCheckBox_CheckedChanged;
        selectCheckBox.Unchecked += SelectCheckBox_CheckedChanged;

        fileNameTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xC8)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 0, 0)
        };

        var bottomDockPanel = new DockPanel
        {
            LastChildFill = true
        };
        DockPanel.SetDock(selectCheckBox, Dock.Left);
        bottomDockPanel.Children.Add(selectCheckBox);
        bottomDockPanel.Children.Add(fileNameTextBlock);

        var bottomBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x4E)),
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(8, 5, 8, 5)
        };
        bottomBorder.Child = bottomDockPanel;

        // ── Root stack ──
        var rootStackPanel = new StackPanel();
        rootStackPanel.Children.Add(tileBorder);
        rootStackPanel.Children.Add(bottomBorder);

        Content = rootStackPanel;

        if (isPlaceholder || Media.Kind == MediaKind.Unsupported || string.IsNullOrEmpty(Media.RelativePath))
        {
            RenderAsEmptySlot();
        }
        else
        {
            fileNameTextBlock.Text = GetFileName();
            Loaded += GalleryCard_Loaded;
        }
    }

    // ── Public API ──

    /// <summary>
    /// Drops decoded media references so the UI thread can reclaim memory;
    /// used when cards are replaced or the window closes.
    /// </summary>
    public void ReleaseMedia()
    {
        // Cards never own a MediaElement (video previews are ffmpeg posters),
        // so releasing means dropping decoded image references.
        thumbnailImage.Source = null;
    }

    // ── Event handlers ──

    private void TileBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!isPlaceholder)
        {
            ThumbnailClicked?.Invoke(this);
        }
    }

    private void SelectCheckBox_CheckedChanged(object sender, RoutedEventArgs eventArgs)
    {
        SelectionChanged?.Invoke(this);
    }

    private async void GalleryCard_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= GalleryCard_Loaded;
        MediaFileInfo mediaSnapshot = Media;
        try
        {
            ImageSource? thumbnail = await thumbnailService.GetThumbnailAsync(mediaSnapshot.AbsolutePath, mediaSnapshot.Kind);
            if (!ReferenceEquals(Media, mediaSnapshot))
            {
                return;
            }

            if (thumbnail != null)
            {
                thumbnailImage.Source = thumbnail;
                thumbnailImage.Visibility = Visibility.Visible;
                if (mediaSnapshot.Kind == MediaKind.Video)
                {
                    videoBadgeBorder.Visibility = Visibility.Visible;
                }
            }
            else if (mediaSnapshot.Kind == MediaKind.Video)
            {
                thumbnailImage.Source = thumbnailService.GetVideoPlaceholder();
                thumbnailImage.Visibility = Visibility.Visible;
                videoBadgeBorder.Visibility = Visibility.Visible;
                tileMessageTextBlock.Text = "no preview" + Environment.NewLine + (thumbnailService.WasFfmpegFound
                    ? "(poster generation failed)"
                    : "(ffmpeg not found — set its path in Settings)");
                tileMessageTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                tileMessageTextBlock.Text = "preview unavailable";
                tileMessageTextBlock.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception)
        {
            tileMessageTextBlock.Text = "preview failed:" + Environment.NewLine + exception.Message;
            tileMessageTextBlock.Visibility = Visibility.Visible;
        }
    }

    // ── Helpers ──

    private string GetFileName()
    {
        int lastSlash = Media.RelativePath.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? Media.RelativePath.Substring(lastSlash + 1) : Media.RelativePath;
        return fileName;
    }

    private void RenderAsEmptySlot()
    {
        isPlaceholder = true;
        tileBorder.Cursor = Cursors.Arrow;
        tileBorder.Opacity = 0.45;
        fileNameTextBlock.Text = "(empty slot)";
        videoBadgeBorder.Visibility = Visibility.Collapsed;
        tileMessageTextBlock.Text = "slot freed — refilling…";
        tileMessageTextBlock.Visibility = Visibility.Visible;
    }
}