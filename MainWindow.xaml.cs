using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UrukCardEditor.Core;

namespace UrukCardEditor;

public partial class MainWindow : Window
{
    private GameRepository? _repository;
    private IReadOnlyList<CardIndexItem> _allCards = [];
    private LoadedCard? _loadedCard;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            SetBusy(true, "Looking for the Magic Duels directory…");
            var gameDirectory = EditorSettings.LoadGameDirectory();
            if (!GameRepository.IsGameDirectory(gameDirectory))
            {
                try { gameDirectory = GameRepository.LocateGameDirectory(); }
                catch { gameDirectory = null; }
            }

            if (GameRepository.IsGameDirectory(gameDirectory))
            {
                await OpenGameDirectoryAsync(gameDirectory!);
            }
            else
            {
                GameDirectoryBox.Text = "No directory selected";
                TopStatusText.Text = "Select the Magic Duels directory";
                BottomStatusText.Text = "Click Browse and select the folder containing DATA_000.ZED and DATA_008.ZED.";
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not open the game archives", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenGameDirectoryAsync(string gameDirectory)
    {
        if (!GameRepository.IsGameDirectory(gameDirectory))
            throw new DirectoryNotFoundException("DATA_000.ZED and DATA_008.ZED were not found in the selected directory.");
        EditorSettings.SaveGameDirectory(gameDirectory);
        GameDirectoryBox.Text = Path.GetFullPath(gameDirectory);
        _repository = new GameRepository(gameDirectory);
        _loadedCard = null;
        SaveWorkingButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        await ReloadCatalogAsync();
        BottomStatusText.Text = "Select a card and click Edit.";
    }

    private async void ChooseGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (_repository?.PendingChanges > 0 && MessageBox.Show(this,
                "Changing the game directory will close the current unsaved working session. Continue?",
                "Change game directory", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select the Magic Duels directory",
            InitialDirectory = GameRepository.IsGameDirectory(GameDirectoryBox.Text)
                ? GameDirectoryBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            SetBusy(true, "Opening the game directory…");
            await OpenGameDirectoryAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            ShowError("Invalid game directory", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadCatalogAsync()
    {
        if (_repository is null) return;
        SetBusy(true, "Reading DATA_000 through DATA_008…");
        _allCards = await Task.Run(() => _repository.BuildCatalog(ReportProgress));
        ApplyFilter();
        TopStatusText.Text = $"Cards found: {_allCards.Count}";
        SetBusy(false);
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allCards
            : _allCards.Where(card =>
                card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                card.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        CardsList.ItemsSource = new ObservableCollection<CardIndexItem>(filtered);
        CardCountText.Text = $"Showing {filtered.Count} of {_allCards.Count}";
    }

    private async Task LoadSelectedAsync()
    {
        if (_busy || _repository is null || CardsList.SelectedItem is not CardIndexItem item)
            return;
        try
        {
            SetBusy(true, $"Opening {item.DisplayName}…");
            var loaded = await Task.Run(() => _repository.LoadCard(item));
            _loadedCard = loaded;
            PopulateEditor(loaded);
            BottomStatusText.Text = $"Opened {loaded.Document.Title}. Changes have not been saved yet.";
            SaveWorkingButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ShowError("Could not open the card", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateEditor(LoadedCard loaded)
    {
        TitleBox.Text = loaded.Document.Title;
        ManaBox.Text = loaded.Document.ManaCost;
        PowerBox.Text = loaded.Document.Power;
        ToughnessBox.Text = loaded.Document.Toughness;
        ArtistBox.Text = loaded.Document.Artist;
        InternalIdBox.Text = loaded.Index.Key;
        AbilitiesBox.Text = loaded.Document.AbilitiesText;
        SourceBox.Text = loaded.Document.SourceText;
        ArtPathBox.Text = "";
        ArtPreview.Source = null;
        ArtPlaceholder.Visibility = Visibility.Visible;
        EditorTabs.SelectedIndex = 0;
    }

    private void ReadEditorIntoDocument()
    {
        if (_loadedCard is null)
            throw new InvalidOperationException("Open a card first.");
        _loadedCard.Document.Title = TitleBox.Text.Trim();
        _loadedCard.Document.ManaCost = ManaBox.Text.Trim();
        _loadedCard.Document.Power = PowerBox.Text.Trim();
        _loadedCard.Document.Toughness = ToughnessBox.Text.Trim();
        _loadedCard.Document.Artist = ArtistBox.Text.Trim();
        _loadedCard.Document.AbilitiesText = AbilitiesBox.Text;
    }

    private async void EditSelected_Click(object sender, RoutedEventArgs e) => await LoadSelectedAsync();

    private async void CardThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (_repository is null || sender is not Image { Tag: CardIndexItem item } || item.Thumbnail is not null)
            return;
        if (!item.TryBeginThumbnailLoad())
            return;
        try
        {
            var thumbnail = await Task.Run(() => _repository.LoadThumbnail(item));
            if (thumbnail is not null)
                item.Thumbnail = thumbnail;
        }
        finally
        {
            item.EndThumbnailLoad();
        }
    }

    private void CardsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardsList.SelectedItem is CardIndexItem item && !_busy)
            BottomStatusText.Text = $"Selected {item.DisplayName}. Click Edit to load it.";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allCards.Count > 0)
            ApplyFilter();
    }

    private async void ReloadCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _busy) return;
        try { await ReloadCatalogAsync(); }
        catch (Exception ex) { ShowError("Could not reload the catalog", ex); }
        finally { SetBusy(false); }
    }

    private void ChooseArt_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null)
        {
            MessageBox.Show(this, "Open a card first.", "MTG-Editor-Utills", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Select card artwork",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _loadedCard.NewArtPath = dialog.FileName;
        ArtPathBox.Text = dialog.FileName;
        using var stream = File.OpenRead(dialog.FileName);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        ArtPreview.Source = bitmap;
        ArtPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void AddDamageDeathTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null) return;
        try
        {
            AbilitiesBox.Text = AbilityTemplates.AddDeathDamage(_loadedCard.Document.Format, AbilitiesBox.Text);
            BottomStatusText.Text = "Added a separate death trigger: deal 1 damage to a creature or player.";
        }
        catch (Exception ex) { ShowError("Could not add the trigger", ex); }
    }

    private void AddMinusCounterDeathTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null) return;
        try
        {
            AbilitiesBox.Text = AbilityTemplates.AddDeathMinusCounter(_loadedCard.Document.Format, AbilitiesBox.Text);
            BottomStatusText.Text = "Added a separate death trigger: put a −1/−1 counter on a creature.";
        }
        catch (Exception ex) { ShowError("Could not add the trigger", ex); }
    }

    private void ValidateAbilities_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null) return;
        try
        {
            ReadEditorIntoDocument();
            _loadedCard.Document.ValidateAbilities();
            BottomStatusText.Text = "Ability syntax is valid.";
            MessageBox.Show(this, "Ability syntax is valid.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Ability error", ex); }
    }

    private async void SaveWorking_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _loadedCard is null || _busy) return;
        try
        {
            ReadEditorIntoDocument();
            SetBusy(true, "Saving the working copy…");
            var result = await Task.Run(() => _repository.SaveToWorkspace(_loadedCard, ReportProgress));
            SourceBox.Text = _loadedCard.Document.SourceText;
            _loadedCard.Index.DisplayName = _loadedCard.Document.Title;
            ApplyFilter();
            InstallButton.IsEnabled = true;
            BottomStatusText.Text = $"Saved to {result.CardPath}. You can edit more cards or install all changes.";
        }
        catch (Exception ex) { ShowError("Could not save the card", ex); }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _busy) return;
        try
        {
            SetBusy(true, "Building and installing DATA_008.ZED…");
            var result = await Task.Run(() => _repository.InstallWorkspace(ReportProgress));
            InstallButton.IsEnabled = false;
            BottomStatusText.Text = $"Installed. SHA-256: {result.Sha256}";
            MessageBox.Show(this,
                $"Changes installed.\n\nFiles in archive: {result.FileCount}\nBackup:\n{result.BackupPath}\n\nYou can now start Magic Duels.",
                "Installation complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Could not install the changes", ex); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value, string? status = null)
    {
        _busy = value;
        System.Windows.Input.Mouse.OverrideCursor = value ? System.Windows.Input.Cursors.Wait : null;
        if (status is not null) TopStatusText.Text = status;
        CardsList.IsEnabled = !value;
        SearchBox.IsEnabled = !value;
        ChooseGameDirectoryButton.IsEnabled = !value;
        if (!value && _repository is not null)
            TopStatusText.Text = $"Cards found: {_allCards.Count} · session changes: {_repository.PendingChanges}";
        SaveWorkingButton.IsEnabled = !value && _loadedCard is not null;
        InstallButton.IsEnabled = !value && _repository?.PendingChanges > 0;
    }

    private void ReportProgress(string text) => Dispatcher.BeginInvoke(() => TopStatusText.Text = text);

    private void ShowError(string title, Exception ex)
    {
        BottomStatusText.Text = ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
