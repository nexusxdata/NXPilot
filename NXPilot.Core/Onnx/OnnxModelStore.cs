// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using NXData.AgentInstaller;

namespace NXPilot.Onnx;

/// <summary>
/// Pasta do modelo ONNX de embeddings (all-MiniLM-L6-v2, ~90 MB). Fica ao lado da IA Local
/// do NXProject (C:\IA_LOCAL), em C:\IA_LOCAL\ONNX\&lt;modelo&gt;. O download é do pacote
/// NXData.AgentInstaller (<see cref="OnnxModelInstaller"/>), o mesmo que instala a IA Local.
/// </summary>
public sealed class OnnxModelStore(string? root = null)
{
    public const string DefaultRoot = OnnxModelInstaller.DefaultRoot;
    public const string ModelName = OnnxModelInstaller.ModelName;

    private readonly OnnxModelInstaller _installer = new(root);

    public string Folder => _installer.Folder;
    public string ModelPath => _installer.ModelPath;
    public string VocabPath => _installer.VocabPath;

    public bool IsInstalled => _installer.IsInstalled;

    /// <summary>Baixa vocab + modelo (progresso em MB); grava em .part e renomeia no fim.</summary>
    public Task DownloadAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        _installer.InstallAsync(progress == null ? null : new Progress<LocalAiProgress>(p => progress.Report(p.Message)), ct);
}
