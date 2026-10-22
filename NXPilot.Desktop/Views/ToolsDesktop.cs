// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using NXPilot.Desktop.Services;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Ferramentas como a área de trabalho do Windows, dentro da tela principal (no lugar das abas): o mesmo papel de
/// parede do Windows, ícone grande com o nome embaixo, duplo clique abre, arrastar muda de lugar (encaixa na grade e
/// fica guardado em settings.json). Ferramenta nova = mais um <see cref="Tool"/> na lista.
/// </summary>
public sealed class ToolsDesktop : UserControl
{
    /// <summary>✕ Fechar: a tela principal volta para as abas da tarefa.</summary>
    public event Action? CloseRequested;

    /// <summary>Uma ferramenta: ícone, nome, se está pronta para usar (senão, por quê) e o que abre.</summary>
    /// <param name="Icon">Desenho colorido do ícone (veja <see cref="ToolIcons"/>) — emoji no WPF sai preto e some no papel de parede.</param>
    public sealed record Tool(string Id, Func<UIElement> Icon, string Name, string Tip, string? NotReady, Action Open);

    private const double CellW = 112, CellH = 104, Pad = 16;
    private readonly Canvas _desk = new() { Background = DesktopBackground(), ClipToBounds = true };
    private readonly IReadOnlyList<Tool> _tools;
    private readonly Dictionary<string, FrameworkElement> _tiles = new();

    public ToolsDesktop(IReadOnlyList<Tool> tools)
    {
        _tools = tools;
        var close = new Button { Content = T("✕ Fechar", "✕ Close", "✕ Cerrar"), Padding = new Thickness(12, 3, 12, 3) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var arrange = new Button { Content = T("Organizar", "Arrange", "Organizar"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        arrange.Click += (_, _) => { AppSettings.Update(s => s.ToolPositions.Clear()); Layout(); };
        var hint = new TextBlock
        {
            Text = T("Duplo clique abre · arraste para mudar de lugar", "Double-click opens · drag to move", "Doble clic abre · arrastre para mover"),
            Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center,
        };
        var bar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) }; // fica no fundo da tela principal, fora do papel de parede
        DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
        DockPanel.SetDock(arrange, Dock.Right); bar.Children.Add(arrange); bar.Children.Add(hint);
        var root = new DockPanel { Background = Brushes.White };
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(new Border { Child = _desk, CornerRadius = new CornerRadius(6), ClipToBounds = true });
        Content = root;
        SizeChanged += (_, _) => { if (IsLoaded) Layout(); }; // a área mudou de tamanho: reencaixa quem ficou de fora
        foreach (var tool in tools) _tiles[tool.Id] = Tile(tool);
        Loaded += (_, _) => Layout();
    }

    /// <summary>Posições guardadas; quem não tem (ou ficou fora da janela) vai para a próxima célula livre.</summary>
    private void Layout()
    {
        var saved = AppSettings.Load().ToolPositions;
        var taken = new HashSet<(int, int)>();
        foreach (var tool in _tools)
        {
            var cell = saved.TryGetValue(tool.Id, out var pos) && TryParseCell(pos, out var c) && Fits(c) && !taken.Contains(c) ? c : FreeCell(taken);
            taken.Add(cell);
            Place(_tiles[tool.Id], cell);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);

    /// <summary>
    /// O fundo da área de trabalho do Windows: o papel de parede atual (ou a cópia que o Windows guarda); sem imagem,
    /// a cor sólida do desktop. Se nada der, um azul neutro.
    /// </summary>
    private static Brush DesktopBackground()
    {
        try
        {
            var sb = new StringBuilder(520);
            SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0);
            var path = sb.ToString();
            if (!File.Exists(path))
                path = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // não prende o arquivo do Windows
                image.DecodePixelWidth = 1600;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            }
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Colors", "Background", null) is string rgb
                && rgb.Split(' ') is [var r, var g, var b] && byte.TryParse(r, out var rr) && byte.TryParse(g, out var gg) && byte.TryParse(b, out var bb))
                return new SolidColorBrush(Color.FromRgb(rr, gg, bb));
        }
        catch { /* papel de parede ilegível: cai na cor */ }
        return new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79));
    }

    private static bool TryParseCell(string text, out (int Col, int Row) cell)
    {
        var parts = text.Split(',');
        cell = default;
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) return false;
        cell = (c, r);
        return c >= 0 && r >= 0;
    }

    private int Cols => Math.Max(1, (int)((_desk.ActualWidth - Pad) / CellW));
    private int Rows => Math.Max(1, (int)((_desk.ActualHeight - Pad) / CellH));
    private bool Fits((int Col, int Row) c) => c.Col < Cols && c.Row < Rows;

    /// <summary>Como a área de trabalho do Windows: desce a coluna e depois passa para a próxima.</summary>
    private (int, int) FreeCell(HashSet<(int, int)> taken)
    {
        for (var col = 0; col < 50; col++)
            for (var row = 0; row < Rows; row++)
                if (!taken.Contains((col, row))) return (col, row);
        return (0, 0);
    }

    private static void Place(FrameworkElement tile, (int Col, int Row) cell)
    {
        Canvas.SetLeft(tile, Pad + cell.Col * CellW);
        Canvas.SetTop(tile, Pad + cell.Row * CellH);
    }

    private FrameworkElement Tile(Tool tool)
    {
        var ready = tool.NotReady == null;
        var stack = new StackPanel { Width = CellW - 12 };
        var icon = tool.Icon();
        if (icon is FrameworkElement fe) { fe.Width = 48; fe.Height = 48; fe.HorizontalAlignment = HorizontalAlignment.Center; }
        icon.Opacity = ready ? 1 : 0.5;
        icon.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Opacity = 0.45 }; // destaca em fundo claro ou escuro
        stack.Children.Add(icon);
        // Como no Windows: nome branco com sombra, legível sobre qualquer papel de parede.
        stack.Children.Add(new TextBlock
        {
            Text = tool.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brushes.White, Opacity = ready ? 1 : 0.6, FontSize = 12,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.9 },
        });
        var tile = new Border { Child = stack, Padding = new Thickness(6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true };
        tile.ToolTip = ready ? tool.Tip : tool.Tip + "\n\n" + tool.NotReady;
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;

        // Arrastar (passou de 4 px) muda de lugar; duplo clique (sem arrastar) abre.
        Point? down = null; Point grab = default; var dragging = false; (int, int) from = default;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { e.Handled = true; if (ready) tool.Open(); return; }
            down = e.GetPosition(_desk); grab = e.GetPosition(tile); dragging = false; from = CellOf(tile);
            tile.CaptureMouse(); tile.Focus(); e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_desk);
            if (!dragging && (Math.Abs(p.X - start.X) > 4 || Math.Abs(p.Y - start.Y) > 4)) { dragging = true; Panel.SetZIndex(tile, 1); tile.Opacity = 0.8; }
            if (!dragging) return;
            Canvas.SetLeft(tile, Math.Clamp(p.X - grab.X, 0, Math.Max(0, _desk.ActualWidth - tile.ActualWidth)));
            Canvas.SetTop(tile, Math.Clamp(p.Y - grab.Y, 0, Math.Max(0, _desk.ActualHeight - tile.ActualHeight)));
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            tile.ReleaseMouseCapture();
            if (dragging) DropTile(tool.Id, tile, from);
            down = null; dragging = false; tile.Opacity = 1; Panel.SetZIndex(tile, 0);
        };
        tile.KeyDown += (_, e) => { if (e.Key == Key.Enter && ready) tool.Open(); };
        _desk.Children.Add(tile);
        return tile;
    }

    /// <summary>Solta: encaixa na célula mais perto; se outra ferramenta já está lá, troca de lugar com ela. Guarda as posições.</summary>
    private static (int, int) CellOf(FrameworkElement tile) =>
        ((int)Math.Round((Canvas.GetLeft(tile) - Pad) / CellW), (int)Math.Round((Canvas.GetTop(tile) - Pad) / CellH));

    private void DropTile(string id, FrameworkElement tile, (int, int) before)
    {
        var col = Math.Clamp((int)Math.Round((Canvas.GetLeft(tile) - Pad) / CellW), 0, Cols - 1);
        var row = Math.Clamp((int)Math.Round((Canvas.GetTop(tile) - Pad) / CellH), 0, Rows - 1);
        var cells = _tiles.ToDictionary(t => t.Key, t => CellOf(t.Value));
        if (cells.FirstOrDefault(c => c.Key != id && c.Value == (col, row)).Key is { } other)
        {
            Place(_tiles[other], before);
            cells[other] = before;
        }
        Place(tile, (col, row));
        cells[id] = (col, row);
        AppSettings.Update(s =>
        {
            foreach (var (key, cell) in cells) s.ToolPositions[key] = string.Create(CultureInfo.InvariantCulture, $"{cell.Item1},{cell.Item2}");
        });
    }
}

/// <summary>
/// Ícones das ferramentas desenhados em vetor e coloridos, como os ícones de app do Windows (placa clara com cantos
/// arredondados + desenho colorido): ficam visíveis em qualquer papel de parede.
/// </summary>
public static class ToolIcons
{
    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    /// <summary>Gráfico de barras (Amostra de dados).</summary>
    public static UIElement Chart()
    {
        var canvas = new Canvas { Width = 48, Height = 48 };
        canvas.Children.Add(new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE4, 0xEC, 0xF6), 90),
            BorderBrush = Hex("#B9C7D8"), BorderThickness = new Thickness(1),
        });
        void Bar(double x, double h, string color)
        {
            var bar = new Border { Width = 8, Height = h, CornerRadius = new CornerRadius(2, 2, 0, 0), Background = Hex(color) };
            Canvas.SetLeft(bar, x); Canvas.SetTop(bar, 38 - h);
            canvas.Children.Add(bar);
        }
        Bar(10, 14, "#2B88D8"); Bar(20, 24, "#F2A900"); Bar(30, 18, "#2EA043");
        var axis = new Border { Width = 32, Height = 2, Background = Hex("#5A6B80") };
        Canvas.SetLeft(axis, 8); Canvas.SetTop(axis, 38);
        canvas.Children.Add(axis);
        return new Viewbox { Child = canvas };
    }
}
