# Benchmark: Power BI Desktop (PBIP) com e sem o NXPilot

- Modelo: `sonnet` · Claude Code: `2.1.307 (Claude Code)` · data: 2026-10-23
- Dados: 5 entidades, 78 colunas, 636 linhas (fictícios, `dados/`)
- Pedido (igual nos dois): o relatório de `relatorio_varejo.json` descrito em texto (`prompt_for` no benchmark.py) + o caminho do tema base do Power BI Desktop instalado (`CY26SU10.json`)
- O mesmo relatório sem IA, pela API do NXPilot: `python gerar_relatorio.py`

| | Sem NXPilot | Com NXPilot | Economia |
|---|---:|---:|---:|
| Tokens (total) | 478,691 | 278,330 | **41.9%** |
| Tokens novos (sem leitura de cache) | 36,192 | 18,036 | 50.2% |
| Tokens de saída | 10,281 | 2,747 | 73.3% |
| Custo estimado (US$) | 0.25 | 0.11 | **54.2%** |
| Tempo (s) | 71 | 25 | 65.5% |
| Projeto válido | não | sim | |
| Relatórios completos (medidas, relacionamentos, páginas, visuais) | 2 de 3 | 3 de 3 | |

Rodadas:

| Modo | # | Tokens | US$ | Turnos | Válido | Completo | Medidas | Relac. | Páginas | Visuais | Usou o NXPilot | Scripts escritos |
|---|---:|---:|---:|---:|---|---|---:|---:|---:|---:|---|---|
| sem | 1 | 547,178 | 0.27 | 11 | não | não | 8 | 4 | 2 | 21 | — | — |
| com | 1 | 348,982 | 0.13 | 9 | sim | sim | 8 | 4 | 2 | 21 | sim | — |
| sem | 2 | 420,598 | 0.24 | 10 | sim | sim | 8 | 4 | 2 | 21 | — | — |
| com | 2 | 242,329 | 0.10 | 7 | sim | sim | 8 | 4 | 2 | 21 | sim | — |
| sem | 3 | 468,297 | 0.24 | 15 | sim | sim | 8 | 4 | 2 | 21 | — | — |
| com | 3 | 243,679 | 0.11 | 7 | sim | sim | 8 | 4 | 2 | 21 | sim | — |

Custo = estimativa que o próprio Claude Code informa (`total_cost_usd`); tokens = `usage` do Claude Code (entrada + saída + criação e leitura de cache).
