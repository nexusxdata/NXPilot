// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NXPilot.Desktop.Views;

/// <summary>Duplo clique numa grade só vale sobre uma linha (não na barra de rolagem, no cabeçalho nem no vazio).</summary>
public static class GridClicks
{
    public static bool OnRow(RoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = Parent(d))
        {
            if (d is ScrollBar or DataGridColumnHeader or DataGridColumnHeadersPresenter) return false;
            if (d is DataGridRow) return true;
            if (d is DataGrid) return false;
        }
        return false;
    }

    // Visual e lógico: Run/texto de célula não é Visual.
    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
