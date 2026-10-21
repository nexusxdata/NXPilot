// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NXPilot.Desktop.ViewModels;

/// <summary>Tipo do aviso: decide a cor na lista ("Alto" = precisa de você agora).</summary>
public enum NotificationKind { Decision, Task, Failure, Message }

public sealed partial class NotificationItem(NotificationKind kind, string title, string body) : ObservableObject
{
    public NotificationKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Body { get; } = body;
    public DateTime Time { get; } = DateTime.Now;
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>Chave de cor da tela (SemanticBrushConverter).</summary>
    public string ColorKey => Kind switch
    {
        NotificationKind.Decision => "Alto",
        NotificationKind.Failure => "Critico",
        NotificationKind.Message => "Relevante",
        _ => "Baixo"
    };

    [ObservableProperty] private bool _isRead;
}

/// <summary>
/// Central de avisos: o que precisa da pessoa (decisão pendente, plano pronto, falha,
/// mensagem prioritária) mesmo com a janela minimizada — o "automático, e não autônomo"
/// depende de a pessoa ficar sabendo quando é a vez dela.
/// </summary>
public sealed partial class NotificationCenter : ObservableObject
{
    public ObservableCollection<NotificationItem> Items { get; } = new();

    [ObservableProperty] private int _unreadCount;

    /// <summary>Disparado a cada aviso novo — a janela usa para piscar na barra de tarefas.</summary>
    public event Action<NotificationItem>? Raised;

    public NotificationItem Notify(NotificationKind kind, string title, string body)
    {
        var item = new NotificationItem(kind, title, body);
        Items.Insert(0, item);
        while (Items.Count > 200) Items.RemoveAt(Items.Count - 1);
        UnreadCount = Items.Count(i => !i.IsRead);
        Raised?.Invoke(item);
        return item;
    }

    /// <summary>
    /// Aviso que já foi resolvido (a decisão foi aprovada ou negada, o disparo foi aprovado ou
    /// descartado): sai da lista — você já leu e agiu.
    /// </summary>
    public void Resolve(NotificationItem? item)
    {
        if (item == null || !Items.Remove(item)) return;
        UnreadCount = Items.Count(i => !i.IsRead);
    }

    public void MarkAllRead()
    {
        foreach (var i in Items) i.IsRead = true;
        UnreadCount = 0;
    }

    public void Clear()
    {
        Items.Clear();
        UnreadCount = 0;
    }
}
