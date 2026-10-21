// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NXPilot.Integrations;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Extensão PowerPoint → escolher figuras: o agente busca, a pessoa VÊ as miniaturas (com autor,
/// licença e fonte) e marca as que entram. Só o que ela marcar é baixado para a apresentação.
/// Dá para refazer a busca aqui mesmo; "Nenhuma serve" segue sem imagem.
/// </summary>
public sealed class ImagePickerWindow : Window
{
    private readonly WrapPanel _cards = new() { Margin = new Thickness(12) };
    private readonly TextBox _query = new() { MinWidth = 380, Padding = new Thickness(6, 4, 6, 4), FontSize = 14 };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 6, 16, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly Button _insert = new() { Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
    private readonly List<(CheckBox Box, ImageHit Hit)> _items = new();
    private readonly int _count;

    /// <summary>As figuras marcadas (vazio = nenhuma serve; nulo = fechou sem decidir).</summary>
    public List<ImageHit>? Selected { get; private set; }

    public ImagePickerWindow(string query, string purpose, int count)
    {
        _count = count;
        Title = T("Escolha as figuras", "Choose the pictures", "Elija las imágenes") + (purpose.Length > 0 ? " — " + purpose : "");
        Width = 1100; Height = 760; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Topmost = true; // a pessoa está no meio da tarefa: a janela não pode ficar escondida

        _query.Text = query;
        var search = new Button { Content = T("Buscar", "Search", "Buscar"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        search.Click += async (_, _) => await LoadAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await LoadAsync(); } };
        _insert.Click += (_, _) => { Selected = _items.Where(i => i.Box.IsChecked == true).Select(i => i.Hit).ToList(); DialogResult = true; };
        var none = new Button { Content = T("Nenhuma serve", "None of these", "Ninguna sirve"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        none.Click += (_, _) => { Selected = new(); DialogResult = true; };
        UpdateInsert();

        var head = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        head.Children.Add(new TextBlock
        {
            Text = T("Marque as figuras que entram nos slides. Só imagens com licença para uso em aula (pasta de imagens, Openverse, Wikimedia Commons e NASA); o crédito de cada uma vai junto.",
                     "Tick the pictures that go into the slides. Only images licensed for classroom use (images folder, Openverse, Wikimedia Commons and NASA); each one keeps its credit.",
                     "Marque las imágenes que van en las diapositivas. Solo imágenes con licencia para uso en clase (carpeta de imágenes, Openverse, Wikimedia Commons y NASA); cada una lleva su crédito."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8),
        });
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(_query);
        bar.Children.Add(search);
        // A pessoa pode ter uma figura melhor: escolhe do computador (ou arrasta para a janela) e ela já entra marcada.
        var own = new Button { Content = T("+ Usar uma imagem minha…", "+ Use my own image…", "+ Usar una imagen mía…"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(16, 0, 0, 0) };
        own.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Imagem para o slide", "Image for the slide", "Imagen para la diapositiva"), Multiselect = true,
                Filter = T("Imagens", "Images", "Imágenes") + " (*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp",
            };
            if (dialog.ShowDialog(this) == true) AddOwn(dialog.FileNames);
        };
        bar.Children.Add(own);
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddOwn(files); };
        head.Children.Add(bar);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 14) };
        foot.Children.Add(_insert);
        foot.Children.Add(none);

        var root = new DockPanel { Background = Brushes.White };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            // Só teste/demonstração (NXPILOT_AUTOPICK=n): faz o papel da pessoa — marca as n primeiras, grava um print e insere.
            if (int.TryParse(Environment.GetEnvironmentVariable("NXPILOT_AUTOPICK"), out var n) && n > 0 && _items.Count > 0)
            {
                await Task.Delay(2500); // miniaturas
                TickFirstForShot(n);
                if (Environment.GetEnvironmentVariable("NXPILOT_AUTOPICK_SHOT") is { Length: > 0 } shotPath) SaveShot(shotPath);
                Selected = _items.Where(i => i.Box.IsChecked == true).Select(i => i.Hit).ToList();
                DialogResult = true;
            }
        };
    }

    private int _searchId;

    /// <summary>Busca progressiva: cada acervo aparece assim que responde (o lento não segura os outros).</summary>
    private async Task LoadAsync()
    {
        var id = ++_searchId; // nova busca: o que chegar da anterior é ignorado
        _cards.Children.Clear();
        _items.Clear();
        UpdateInsert();
        _status.Text = T("Buscando… (as figuras aparecem conforme cada acervo responde)", "Searching… (pictures appear as each collection answers)", "Buscando… (las imágenes aparecen según responde cada acervo)");
        var seen = new HashSet<string>();
        try
        {
            await ImageSearch.SearchProgressiveAsync(_query.Text.Trim(), _count, PowerPointSettings.Load(), batch => Dispatcher.Invoke(() =>
            {
                if (id != _searchId) return;
                foreach (var hit in batch.Where(h => seen.Add(h.ImageUrl))) _cards.Children.Add(Card(hit));
                _status.Text = T($"{_items.Count} figura(s) até agora…", $"{_items.Count} picture(s) so far…", $"{_items.Count} imagen(es) hasta ahora…");
            }));
            if (id != _searchId) return;
            _status.Text = (_items.Count == 0
                    ? T("Nada encontrado. Tente palavras mais simples — em inglês costuma trazer mais (ex.: \"solar system\").", "Nothing found. Try simpler words.", "Nada encontrado. Pruebe palabras más simples — en inglés suele traer más (p. ej., \"solar system\").")
                    : T($"{_items.Count} figura(s). Clique numa figura para marcar.", $"{_items.Count} picture(s). Click a picture to tick it.", $"{_items.Count} imagen(es). Haga clic en una imagen para marcarla."))
                + (ImageSearch.LastErrors.Count > 0 ? "  ·  " + T("Sem resposta: ", "No answer: ", "Sin respuesta: ") + string.Join("; ", ImageSearch.LastErrors) : "");
        }
        catch (Exception ex) { if (id == _searchId) _status.Text = T("Não foi possível buscar: ", "Could not search: ", "No se pudo buscar: ") + ex.Message; }
    }

    /// <summary>Miniatura + título + autor · licença · fonte, com a caixa de marcar (clicar na figura também marca).</summary>
    private Border Card(ImageHit hit)
    {
        var box = new CheckBox { Margin = new Thickness(0, 6, 0, 0), Content = T("Usar esta", "Use this", "Usar esta"), FontWeight = FontWeights.SemiBold };
        box.Checked += (_, _) => UpdateInsert();
        box.Unchecked += (_, _) => UpdateInsert();
        _items.Add((box, hit));

        var image = new Image { Height = 170, Stretch = Stretch.Uniform, Cursor = System.Windows.Input.Cursors.Hand };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(hit.ThumbUrl);
            bmp.DecodePixelHeight = 340;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            image.Source = bmp;
        }
        catch { /* miniatura indisponível: o card mostra só o texto */ }
        image.MouseLeftButtonUp += (_, _) => box.IsChecked = box.IsChecked != true;

        var panel = new StackPanel { Width = 230 };
        panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9)), Height = 170, Child = image });
        panel.Children.Add(new TextBlock { Text = hit.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0), ToolTip = hit.Title });
        var detail = hit.Source == ImageHit.OwnSource ? T("Sua imagem", "Your image", "Su imagen")
            : hit.LocalPath != null ? T("Pasta de imagens", "Images folder", "Carpeta de imágenes")
            : $"{(hit.Author.Length > 0 ? hit.Author : T("autor não informado", "unknown author", "autor no informado"))} · {hit.License} · {hit.Source}";
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxHeight = 44, ToolTip = hit.Credit });
        if (hit.Width > 0) panel.Children.Add(new TextBlock { Text = $"{hit.Width} × {hit.Height}", FontSize = 11, Foreground = Brushes.Gray });
        panel.Children.Add(box);
        return new Border
        {
            Child = panel, Margin = new Thickness(8), Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xE5)), BorderThickness = new Thickness(1),
        };
    }

    private static readonly string[] OwnExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>Imagens do computador da pessoa: entram no início da lista, já marcadas.</summary>
    private void AddOwn(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var file in files.Where(f => OwnExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
        {
            var saved = SaveToImagesFolder(file);
            if (_items.Any(i => i.Hit.LocalPath == saved)) continue;
            var card = Card(ImageHit.Own(saved));
            _cards.Children.Insert(0, card);
            _items[^1].Box.IsChecked = true;
            added++;
        }
        if (added > 0) _status.Text = T($"{added} imagem(ns) sua(s) incluída(s), marcada(s) e guardada(s) na pasta de imagens ({ImagesFolder()}).",
            $"{added} image(s) of yours added, ticked and kept in the images folder ({ImagesFolder()}).",
            $"{added} imagen(es) suya(s) incluida(s), marcada(s) y guardada(s) en la carpeta de imágenes ({ImagesFolder()}).");
    }

    /// <summary>Pasta de imagens da extensão; sem pasta configurada, cria Documentos/NXPilot/Imagens e passa a usá-la.</summary>
    private static string ImagesFolder()
    {
        var settings = PowerPointSettings.Load();
        if (settings.ImagesFolder.Length > 0) return settings.ImagesFolder;
        settings.ImagesFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NXPilot", "Imagens");
        settings.Save();
        return settings.ImagesFolder;
    }

    /// <summary>A imagem trazida vai para a pasta de imagens (a busca acha da próxima vez); já estando lá, fica onde está.</summary>
    private static string SaveToImagesFolder(string file)
    {
        var folder = ImagesFolder();
        var full = System.IO.Path.GetFullPath(file);
        var root = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;
        System.IO.Directory.CreateDirectory(folder);
        var name = System.IO.Path.GetFileNameWithoutExtension(full);
        var ext = System.IO.Path.GetExtension(full);
        var target = System.IO.Path.Combine(folder, name + ext);
        for (var i = 2; System.IO.File.Exists(target); i++)
        {
            if (new System.IO.FileInfo(target).Length == new System.IO.FileInfo(full).Length) return target; // a mesma imagem já foi guardada
            target = System.IO.Path.Combine(folder, $"{name}-{i}{ext}");
        }
        System.IO.File.Copy(full, target);
        return target;
    }

    private void SaveShot(string path)
    {
        if (Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Print: marca as primeiras figuras para mostrar a seleção.</summary>
    public void TickFirstForShot(int n) { foreach (var (box, _) in _items.Take(n)) box.IsChecked = true; }

    private void UpdateInsert()
    {
        var n = _items.Count(i => i.Box.IsChecked == true);
        _insert.Content = T($"Inserir selecionadas ({n})", $"Insert selected ({n})", $"Insertar seleccionadas ({n})");
        _insert.IsEnabled = n > 0;
    }
}
