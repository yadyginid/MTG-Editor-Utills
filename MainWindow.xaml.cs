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
            SetBusy(true, "Поиск папки Magic Duels…");
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
                GameDirectoryBox.Text = "Каталог не выбран";
                TopStatusText.Text = "Выберите каталог Magic Duels";
                BottomStatusText.Text = "Нажмите «Выбрать…» и укажите папку, содержащую DATA_000.ZED и DATA_008.ZED.";
            }
        }
        catch (Exception ex)
        {
            ShowError("Не удалось открыть архивы игры", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenGameDirectoryAsync(string gameDirectory)
    {
        if (!GameRepository.IsGameDirectory(gameDirectory))
            throw new DirectoryNotFoundException("В выбранной папке не найдены DATA_000.ZED и DATA_008.ZED.");
        EditorSettings.SaveGameDirectory(gameDirectory);
        GameDirectoryBox.Text = Path.GetFullPath(gameDirectory);
        _repository = new GameRepository(gameDirectory);
        _loadedCard = null;
        SaveWorkingButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        await ReloadCatalogAsync();
        BottomStatusText.Text = "Выберите карту и нажмите Edit.";
    }

    private async void ChooseGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (_repository?.PendingChanges > 0 && MessageBox.Show(this,
                "При смене каталога несохранённая рабочая сессия будет закрыта. Продолжить?",
                "Смена каталога игры", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Выберите каталог Magic Duels",
            InitialDirectory = GameRepository.IsGameDirectory(GameDirectoryBox.Text)
                ? GameDirectoryBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            SetBusy(true, "Открывается каталог игры…");
            await OpenGameDirectoryAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            ShowError("Неверный каталог игры", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadCatalogAsync()
    {
        if (_repository is null) return;
        SetBusy(true, "Чтение каталогов DATA_000…DATA_008…");
        _allCards = await Task.Run(() => _repository.BuildCatalog(ReportProgress));
        ApplyFilter();
        TopStatusText.Text = $"Найдено карт: {_allCards.Count}";
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
        CardCountText.Text = $"Показано: {filtered.Count} из {_allCards.Count}";
    }

    private async Task LoadSelectedAsync()
    {
        if (_busy || _repository is null || CardsList.SelectedItem is not CardIndexItem item)
            return;
        try
        {
            SetBusy(true, $"Открывается {item.DisplayName}…");
            var loaded = await Task.Run(() => _repository.LoadCard(item));
            _loadedCard = loaded;
            PopulateEditor(loaded);
            BottomStatusText.Text = $"Открыта {loaded.Document.Title}. Изменения ещё не сохранены.";
            SaveWorkingButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ShowError("Не удалось открыть карту", ex);
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
            throw new InvalidOperationException("Сначала откройте карту.");
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
            BottomStatusText.Text = $"Выбрана {item.DisplayName}. Нажмите Edit для загрузки.";
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
        catch (Exception ex) { ShowError("Не удалось обновить каталог", ex); }
        finally { SetBusy(false); }
    }

    private void ChooseArt_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null)
        {
            MessageBox.Show(this, "Сначала откройте карту.", "MTG-Editor-Utills", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Выберите изображение карты",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Все файлы|*.*"
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
            BottomStatusText.Text = "Добавлен отдельный триггер смерти: 1 урон существу или игроку.";
        }
        catch (Exception ex) { ShowError("Не удалось добавить триггер", ex); }
    }

    private void AddMinusCounterDeathTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null) return;
        try
        {
            AbilitiesBox.Text = AbilityTemplates.AddDeathMinusCounter(_loadedCard.Document.Format, AbilitiesBox.Text);
            BottomStatusText.Text = "Добавлен отдельный триггер смерти: жетон −1/−1.";
        }
        catch (Exception ex) { ShowError("Не удалось добавить триггер", ex); }
    }

    private void ValidateAbilities_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedCard is null) return;
        try
        {
            ReadEditorIntoDocument();
            _loadedCard.Document.ValidateAbilities();
            BottomStatusText.Text = "Синтаксис способностей корректен.";
            MessageBox.Show(this, "Синтаксис способностей корректен.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Ошибка в способностях", ex); }
    }

    private async void SaveWorking_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _loadedCard is null || _busy) return;
        try
        {
            ReadEditorIntoDocument();
            SetBusy(true, "Сохранение рабочей копии…");
            var result = await Task.Run(() => _repository.SaveToWorkspace(_loadedCard, ReportProgress));
            SourceBox.Text = _loadedCard.Document.SourceText;
            _loadedCard.Index.DisplayName = _loadedCard.Document.Title;
            ApplyFilter();
            InstallButton.IsEnabled = true;
            BottomStatusText.Text = $"Сохранено: {result.CardPath}. Можно редактировать другие карты или установить все изменения.";
        }
        catch (Exception ex) { ShowError("Не удалось сохранить карту", ex); }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _busy) return;
        try
        {
            SetBusy(true, "Сборка и установка DATA_008.ZED…");
            var result = await Task.Run(() => _repository.InstallWorkspace(ReportProgress));
            InstallButton.IsEnabled = false;
            BottomStatusText.Text = $"Установлено. SHA-256: {result.Sha256}";
            MessageBox.Show(this,
                $"Изменения установлены.\n\nФайлов в архиве: {result.FileCount}\nРезервная копия:\n{result.BackupPath}\n\nТеперь можно запустить Magic Duels.",
                "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Не удалось установить изменения", ex); }
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
            TopStatusText.Text = $"Найдено карт: {_allCards.Count} · изменений в сессии: {_repository.PendingChanges}";
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
