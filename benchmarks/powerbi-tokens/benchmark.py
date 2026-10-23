# Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
# NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
# Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
# Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com
"""
Benchmark: quantos tokens o Claude Code gasta para criar um projeto Power BI Desktop (PBIP)
a partir de um fluxo de dados — SEM e COM o NXPilot.

O mesmo pedido vai ao Claude Code duas vezes (mesmo modelo, mesma pasta de dados, sem nenhum
outro MCP do usuário):
  - sem: o Claude escreve o projeto sozinho (TMDL à mão ou por script);
  - com: o Claude tem as ferramentas do NXPilot (MCP). Duas formas (--via):
         bench (padrão): NXPilot.Bench --mcp-bench — o mesmo código do app, sem abrir o app; gate
                         fail-closed (só o que é BAIXO passa sozinho, o resto é negado);
         app: o NXPilot aberto, pela API para scripts (Configurar → MCP, "Expor API para scripts" —
              desligada por padrão): NxPilot.exe --mcp --workdir <pasta>; o que não for BAIXO
              aparece no painel do gate e espera você.

Tokens e custo estimado vêm do próprio Claude Code (--output-format json: usage e total_cost_usd).
No fim, os dois projetos são conferidos (NXPilot.Bench --pbip-check) e saem: results/results.json,
results/RESULTS.md e o slide results/slide-<idioma>.svg com o % de economia.

Dados: FICTÍCIOS, gerados aqui com semente fixa (varejo: vendas, produtos, clientes, lojas, metas).
Nada de dado real de empresa.

Uso:
  python benchmark.py                 # 1 rodada de cada, modelo sonnet
  python benchmark.py --runs 3 --model sonnet
  python benchmark.py --only-data     # só gera os dados (dados/)
  python benchmark.py --report-only   # refaz relatório e slides com o results.json existente
  python benchmark.py --via app       # usa o NXPilot aberto (API para scripts ligada)

Requisitos: Python 3.10+, Claude Code (claude) com login, NXPilot.Bench compilado
(build-nxpilot.ps1). Custa tokens da sua conta do Claude: confira o valor estimado no fim.
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import os
import random
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
DATA = HERE / "dados"
WORK = HERE / ".work"
RESULTS = HERE / "results"
# Scripts que o agente escreveu (para ver o que falta no MCP Power BI): fora do GitHub (podem ter caminhos da máquina).
AGENT_SCRIPTS = RESULTS / "scripts-agente"
SOURCE_URL = "github.com/nexusxdata/NXPilot/tree/main/benchmarks/powerbi-tokens"
FLOW_NAME = "Fluxo Exemplo - Varejo (dados ficticios)"
WORKSPACE = "Workspace Exemplo"

SPEC_FILE = HERE / "relatorio_varejo.json"  # as regras do relatório (as mesmas do gerar_relatorio.py, sem IA)

PROMPT = (
    "Crie nesta pasta um relatório Power BI Desktop completo no formato PBIP chamado Varejo (arquivo Varejo.pbip, "
    "pasta Varejo.SemanticModel com o modelo em TMDL e pasta Varejo.Report), a partir do fluxo de dados "
    "descrito em dados/fluxo_varejo.model.json. Uma tabela de importação por entidade, com todas as colunas e os "
    f"tipos certos, e parâmetros para o workspace ('{WORKSPACE}') e para o nome do fluxo. Para abrir com dados sem "
    "acesso ao serviço, as tabelas leem os CSV da pasta dados/ (um por entidade, mesmo nome), com uma pasta de dados "
    "local como parâmetro. "
)


def report_requirements(spec: dict) -> str:
    """As regras do relatorio_varejo.json em texto: o agente recebe o MESMO relatório nas duas rodadas."""
    parts = ["Medidas (DAX, com o formato): " + "; ".join(
        f"{m['name']} na tabela {m['table']} = {m['expression']} (formato {m.get('format', 'padrão')})" for m in spec["measures"]) + "."]
    parts.append("Relacionamentos (muitos para um): " + "; ".join(f"{r['from']} → {r['to']}" for r in spec["relationships"]) + ".")
    for page in spec["pages"]:
        items = []
        for v in page["visuals"]:
            if v["type"] == "textbox":
                items.append(f"caixa de texto \"{v['text']}\"")
                continue
            d = f"{v['type']} \"{v.get('title', '')}\""
            if v.get("category"):
                d += f" com eixo {v['category']}"
            d += " e valores " + ", ".join(v.get("values", []))
            if v.get("line_values"):
                d += " e linha " + ", ".join(v["line_values"])
            items.append(d)
        parts.append(f"Página \"{page['name']}\" com {len(page['visuals'])} visuais: " + "; ".join(items) + ".")
    return " ".join(parts) + " Não faça perguntas: termine quando o projeto estiver pronto e válido (abre no Power BI Desktop)."

# ------------------------------------------------------------------ dados fictícios

CITIES = [("São Paulo", "SP", "Sudeste"), ("Rio de Janeiro", "RJ", "Sudeste"), ("Belo Horizonte", "MG", "Sudeste"),
          ("Vitória", "ES", "Sudeste"), ("Curitiba", "PR", "Sul"), ("Porto Alegre", "RS", "Sul"),
          ("Salvador", "BA", "Nordeste"), ("Recife", "PE", "Nordeste"), ("Fortaleza", "CE", "Nordeste"),
          ("Goiânia", "GO", "Centro-Oeste"), ("Manaus", "AM", "Norte"), ("Belém", "PA", "Norte")]
CATEGORIES = {"Eletrônicos": ["Celulares", "Notebooks", "Fones"], "Casa": ["Cozinha", "Cama e banho", "Decoração"],
              "Esporte": ["Academia", "Ciclismo", "Corrida"], "Moda": ["Camisetas", "Calçados", "Acessórios"]}


def _day(rng: random.Random, start: dt.date, span: int) -> str:
    return (start + dt.timedelta(days=rng.randrange(span))).isoformat()


def build_entities() -> dict[str, tuple[list[tuple[str, str]], list[list]]]:
    """Entidade → (colunas com tipo do model.json, linhas). Semente fixa: sempre os mesmos dados."""
    rng = random.Random(42)
    base = dt.date(2025, 1, 1)
    lojas_cols = [("id_loja", "int64"), ("nome_loja", "string"), ("tipo_loja", "string"), ("cidade", "string"), ("uf", "string"),
                  ("regiao", "string"), ("area_m2", "double"), ("data_abertura", "date"), ("gerente", "string"), ("ativa", "boolean")]
    lojas = []
    for i, (city, uf, reg) in enumerate(CITIES, 1):
        lojas.append([i, f"Loja {i:02d} {city}", rng.choice(["Rua", "Shopping", "Quiosque"]), city, uf, reg,
                      round(rng.uniform(80, 900), 1), _day(rng, dt.date(2015, 1, 1), 3000), f"Gerente {i:02d}", rng.random() > 0.08])

    prod_cols = [("id_produto", "int64"), ("sku", "string"), ("nome_produto", "string"), ("categoria", "string"),
                 ("subcategoria", "string"), ("marca", "string"), ("fornecedor", "string"), ("preco_lista", "double"),
                 ("custo_padrao", "double"), ("peso_kg", "double"), ("ativo", "boolean"), ("data_lancamento", "date"),
                 ("cor", "string"), ("tamanho", "string")]
    produtos = []
    for i in range(1, 61):
        cat = rng.choice(list(CATEGORIES))
        price = round(rng.uniform(19, 4500), 2)
        produtos.append([i, f"SKU-{i:05d}", f"Produto {i:03d}", cat, rng.choice(CATEGORIES[cat]), f"Marca {rng.randint(1, 9)}",
                         f"Fornecedor {rng.randint(1, 15):02d}", price, round(price * rng.uniform(0.35, 0.7), 2),
                         round(rng.uniform(0.1, 12), 2), rng.random() > 0.1, _day(rng, dt.date(2020, 1, 1), 1800),
                         rng.choice(["Preto", "Branco", "Azul", "Vermelho", "Cinza"]), rng.choice(["P", "M", "G", "Único"])])

    cli_cols = [("id_cliente", "int64"), ("nome_cliente", "string"), ("segmento", "string"), ("genero", "string"),
                ("faixa_etaria", "string"), ("data_cadastro", "date"), ("cidade", "string"), ("uf", "string"), ("regiao", "string"),
                ("cep_prefixo", "string"), ("canal_aquisicao", "string"), ("cliente_vip", "boolean"), ("limite_credito", "double"),
                ("score", "int64"), ("ultima_compra", "date"), ("aceita_marketing", "boolean")]
    clientes = []
    for i in range(1, 121):
        city, uf, reg = rng.choice(CITIES)
        clientes.append([i, f"Cliente {i:04d}", rng.choice(["Varejo", "Atacado", "Corporativo"]), rng.choice(["F", "M", "Não informado"]),
                         rng.choice(["18-24", "25-34", "35-44", "45-59", "60+"]), _day(rng, dt.date(2018, 1, 1), 2500), city, uf, reg,
                         f"{rng.randint(10, 99)}{rng.randint(0, 9)}", rng.choice(["Loja", "Site", "App", "Indicação"]),
                         rng.random() > 0.85, round(rng.uniform(500, 20000), 2), rng.randint(300, 950), _day(rng, base, 365), rng.random() > 0.4])

    vend_cols = [("id_venda", "int64"), ("data_venda", "date"), ("id_cliente", "int64"), ("id_produto", "int64"), ("id_loja", "int64"),
                 ("canal", "string"), ("quantidade", "int64"), ("preco_unitario", "double"), ("desconto_pct", "double"),
                 ("valor_bruto", "double"), ("valor_desconto", "double"), ("valor_liquido", "double"), ("custo_unitario", "double"),
                 ("custo_total", "double"), ("margem", "double"), ("frete", "double"), ("imposto", "double"), ("forma_pagamento", "string"),
                 ("parcelas", "int64"), ("status_pedido", "string"), ("data_entrega", "date"), ("prazo_dias", "int64"),
                 ("vendedor", "string"), ("cupom", "string"), ("avaliacao", "int64"), ("devolvido", "boolean"),
                 ("motivo_devolucao", "string"), ("regiao", "string"), ("uf", "string"), ("cidade", "string")]
    vendas = []
    for i in range(1, 301):
        p = rng.choice(produtos)
        loja = rng.choice(lojas)
        qty = rng.randint(1, 6)
        disc = rng.choice([0, 0, 0.05, 0.1, 0.15])
        gross = round(p[7] * qty, 2)
        net = round(gross * (1 - disc), 2)
        cost = round(p[8] * qty, 2)
        day = base + dt.timedelta(days=rng.randrange(365))
        prazo = rng.randint(1, 12)
        back = rng.random() < 0.05
        vendas.append([i, day.isoformat(), rng.randint(1, 120), p[0], loja[0], rng.choice(["Loja", "Site", "App"]), qty, p[7], disc,
                       gross, round(gross - net, 2), net, p[8], cost, round(net - cost, 2), round(rng.uniform(0, 60), 2),
                       round(net * 0.18, 2), rng.choice(["Pix", "Cartão", "Boleto"]), rng.choice([1, 1, 2, 3, 6, 10]),
                       rng.choice(["Entregue", "Entregue", "Em trânsito", "Cancelado"]), (day + dt.timedelta(days=prazo)).isoformat(), prazo,
                       f"Vendedor {rng.randint(1, 25):02d}", rng.choice(["", "", "BEMVINDO10", "FRETE0"]), rng.randint(1, 5), back,
                       rng.choice(["Defeito", "Arrependimento", "Tamanho"]) if back else "", loja[5], loja[4], loja[3]])

    meta_cols = [("ano_mes", "string"), ("id_loja", "int64"), ("meta_valor", "double"), ("meta_quantidade", "int64"),
                 ("meta_margem_pct", "double"), ("campanha", "string"), ("observacao", "string"), ("data_atualizacao", "dateTime")]
    metas = []
    for month in range(1, 13):
        for loja in lojas:
            metas.append([f"2025-{month:02d}", loja[0], round(rng.uniform(20000, 180000), 2), rng.randint(80, 900),
                          round(rng.uniform(0.18, 0.42), 3), rng.choice(["", "Volta às aulas", "Black Friday", "Natal"]), "",
                          f"2025-{month:02d}-01T08:00:00"])

    return {"Vendas": (vend_cols, vendas), "Produtos": (prod_cols, produtos), "Clientes": (cli_cols, clientes),
            "Lojas": (lojas_cols, lojas), "Metas": (meta_cols, metas)}


def make_data() -> dict:
    """Escreve dados/<Entidade>.csv e dados/fluxo_varejo.model.json (formato do model.json de um fluxo de dados Gen1)."""
    DATA.mkdir(parents=True, exist_ok=True)
    entities = build_entities()
    model = {"name": FLOW_NAME, "description": "Fluxo de dados FICTÍCIO para o benchmark do NXPilot (dados gerados).",
             "version": "1.0", "culture": "pt-BR", "modifiedTime": "2026-01-01T00:00:00Z", "entities": []}
    for name, (cols, rows) in entities.items():
        with open(DATA / f"{name}.csv", "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow([c for c, _ in cols])
            w.writerows(rows)
        model["entities"].append({"$type": "LocalEntity", "name": name, "description": f"Entidade {name} (fictícia)",
                                  "attributes": [{"name": c, "dataType": t} for c, t in cols],
                                  "partitions": [{"name": "Part001", "location": f"dados/{name}.csv"}]})
    (DATA / "fluxo_varejo.model.json").write_text(json.dumps(model, ensure_ascii=False, indent=2), encoding="utf-8")
    return {"entities": len(entities), "columns": sum(len(c) for c, _ in entities.values()), "rows": sum(len(r) for _, r in entities.values())}

# ------------------------------------------------------------------ rodadas


def find_claude(arg: str | None) -> str:
    for c in [arg, shutil.which("claude"), str(Path.home() / ".local" / "bin" / "claude.exe")]:
        if c and Path(c).exists():
            return c
    sys.exit("Claude Code não encontrado (instale e faça login, ou passe --claude <caminho>).")


def find_bench(arg: str | None) -> str:
    for c in [arg, REPO / "NXPilot.Bench" / "bin" / "Release" / "net10.0" / "NXPilot.Bench.exe",
              REPO / "NXPilot.Bench" / "bin" / "Debug" / "net10.0" / "NXPilot.Bench.exe"]:
        if c and Path(c).exists():
            return str(Path(c).resolve())
    sys.exit("NXPilot.Bench não encontrado: rode build-nxpilot.ps1 (ou passe --bench <caminho>).")


BASE_TOOLS = ["Read", "Write", "Edit", "Glob", "Grep", "Bash(python:*)", "Bash(py:*)", "Bash(powershell:*)", "Bash(pwsh:*)",
              "Bash(mkdir:*)", "Bash(ls:*)", "Bash(cat:*)", "Bash(dir:*)"]
NXPILOT_TOOLS = ["mcp__nxpilot__powerbi_from_dataflow", "mcp__nxpilot__powerbi_build_report", "mcp__nxpilot__powerbi_validate", "mcp__nxpilot__powerbi_info"]


def find_nxpilot(arg: str | None) -> str:
    local = Path(os.environ.get("LOCALAPPDATA", "")) / "NXPilot" / "app" / "NxPilot.exe"
    for c in [arg, REPO / "NXPilot.Desktop" / "bin" / "Release" / "net10.0-windows" / "NxPilot.exe", local]:
        if c and Path(c).exists():
            return str(Path(c).resolve())
    sys.exit("NxPilot.exe não encontrado (passe --nxpilot <caminho>).")


def find_theme() -> str | None:
    """Tema base mais novo do Power BI Desktop instalado (clássico ou Microsoft Store). Nulo sem Power BI Desktop."""
    bins = [Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Microsoft Power BI Desktop" / "bin"]
    try:
        loc = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-AppxPackage *MicrosoftPowerBIDesktop*).InstallLocation"],
                             capture_output=True, text=True, timeout=60).stdout.strip()
        if loc:
            bins.append(Path(loc.splitlines()[0]) / "bin")
    except Exception:
        pass
    for b in bins:
        themes = b / "WebView2Resources" / "minerva" / "sharedresources" / "BaseThemes"
        try:
            found = sorted(themes.glob("CY*.json"))
        except OSError:
            continue
        if found:
            return str(found[-1])
    return None


def prompt_for(theme: str | None) -> str:
    """O mesmo pedido nas duas rodadas; com Power BI Desktop instalado, os dois sabem onde está o tema base."""
    spec = json.loads(SPEC_FILE.read_text(encoding="utf-8"))
    return (PROMPT + report_requirements(spec)
            + (f" O tema base do Power BI Desktop instalado nesta máquina está em: {theme}" if theme else ""))


def expected_counts() -> dict:
    spec = json.loads(SPEC_FILE.read_text(encoding="utf-8"))
    return {"measures": len(spec["measures"]), "relationships": len(spec["relationships"]), "pages": len(spec["pages"]),
            "visuals": sum(len(p["visuals"]) for p in spec["pages"])}


def run_once(mode: str, idx: int, claude: str, bench: str, model: str, nxpilot: str | None = None, theme: str | None = None) -> dict:
    work = WORK / f"{mode}-{idx}"
    if work.exists():
        shutil.rmtree(work)
    shutil.copytree(DATA, work / "dados")
    # Sem nenhum MCP do usuário nos dois casos (--strict-mcp-config); "com" recebe só o MCP do NXPilot.
    cmd = [claude, "-p", prompt_for(theme), "--output-format", "json", "--model", model, "--permission-mode", "acceptEdits", "--strict-mcp-config"]
    tools = list(BASE_TOOLS)
    if mode == "com":
        cfg = work / ".mcp-bench.json"
        server = ({"command": nxpilot, "args": ["--mcp", "--workdir", str(work)]} if nxpilot
                  else {"command": bench, "args": ["--mcp-bench", str(work)] + ([theme] if theme else [])})
        cfg.write_text(json.dumps({"mcpServers": {"nxpilot": server}}), encoding="utf-8")
        cmd += ["--mcp-config", str(cfg)]
        tools += NXPILOT_TOOLS
    cmd += ["--allowedTools", *tools]
    started = dt.datetime.now()
    print(f"  [{mode} #{idx}] rodando o Claude Code ({model})…", flush=True)
    p = subprocess.run(cmd, cwd=work, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=3600)
    out = p.stdout.strip()
    try:
        data = json.loads(out[out.index("{"):])
    except ValueError:
        data = {"is_error": True, "result": (out or p.stderr)[-2000:]}
    u = data.get("usage") or {}
    tokens = {k: int(u.get(k) or 0) for k in ("input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens")}
    check = json.loads(subprocess.run([bench, "--pbip-check", str(work)], capture_output=True, text=True, encoding="utf-8").stdout or "{}")
    # A ferramenta do NXPilot deixa marcas próprias no modelo (consulta do fluxo com "PorId"); sem elas, o agente fez sozinho.
    expr = next(work.glob("*.SemanticModel/definition/expressions.tmdl"), None)
    used_tool = mode == "com" and expr is not None and "PorId = if" in expr.read_text(encoding="utf-8", errors="replace")
    scripts = sorted(f for f in work.rglob("*") if f.is_file() and f.suffix.lower() in (".py", ".ps1") and "dados" not in f.relative_to(work).parts)
    if scripts:
        keep = AGENT_SCRIPTS / f"{mode}-{idx}"
        if keep.exists():
            shutil.rmtree(keep)
        for f in scripts:
            dest = keep / f.relative_to(work)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, dest)
    r = {
        "mode": mode, "run": idx, "model": model, "started": started.isoformat(timespec="seconds"),
        "duration_s": round((data.get("duration_ms") or 0) / 1000, 1), "turns": data.get("num_turns"),
        "cost_usd": round(float(data.get("total_cost_usd") or 0), 4), **tokens,
        "total_tokens": sum(tokens.values()),
        "new_tokens": tokens["input_tokens"] + tokens["output_tokens"] + tokens["cache_creation_input_tokens"],
        "is_error": bool(data.get("is_error")), "valid": bool(check.get("valid")),
        "tables": check.get("tables"), "columns": check.get("columns"), "problems": check.get("problems", []),
        "measures": check.get("measures"), "relationships": check.get("relationships"), "pages": check.get("pages"), "visuals": check.get("visuals"),
        # Completo = válido e com tudo o que o relatório pede (medidas, relacionamentos, páginas e visuais).
        "complete": bool(check.get("valid")) and all((check.get(k) or 0) >= v for k, v in expected_counts().items()),
        "scripts": [str(f.relative_to(work)) for f in scripts], "used_nxpilot_tool": used_tool,
    }
    print(f"  [{mode} #{idx}] {r['total_tokens']:,} tokens · US$ {r['cost_usd']:.2f} · válido: {r['valid']} · completo: {r['complete']} "
          f"({r['tables']} tabelas, {r['measures']} medidas, {r['relationships']} relacionamentos, {r['pages']} páginas, {r['visuals']} visuais)" + (f" · usou a ferramenta do NXPilot: {used_tool}" if mode == "com" else "")
          + (f" · {len(scripts)} script(s) guardado(s) em {AGENT_SCRIPTS / f'{mode}-{idx}'}" if scripts else ""), flush=True)
    if mode == "com" and not used_tool:
        print("  ⚠ O agente não usou a ferramenta do NXPilot: veja os scripts guardados para melhorar o MCP Power BI.", flush=True)
    return r


def claude_version(claude: str) -> str:
    try:
        return subprocess.run([claude, "--version"], capture_output=True, text=True, timeout=30).stdout.strip()
    except Exception:
        return "?"

# ------------------------------------------------------------------ relatório e slide


def summarize(results: list[dict]) -> dict:
    def avg(mode: str, key: str) -> float:
        vals = [r[key] for r in results if r["mode"] == mode]
        return sum(vals) / len(vals) if vals else 0.0
    s = {m: {k: avg(m, k) for k in ("total_tokens", "new_tokens", "output_tokens", "cost_usd", "duration_s", "turns")} for m in ("sem", "com")}
    for m in ("sem", "com"):
        s[m]["valid"] = all(r["valid"] for r in results if r["mode"] == m)
        s[m]["complete"] = all(r.get("complete", r["valid"]) for r in results if r["mode"] == m)
        s[m]["complete_runs"] = sum(1 for r in results if r["mode"] == m and r.get("complete", r["valid"]))
        s[m]["runs"] = sum(1 for r in results if r["mode"] == m)

    def pct(key: str) -> float:
        return round(100 * (1 - s["com"][key] / s["sem"][key]), 1) if s["sem"][key] else 0.0
    s["savings"] = {"total_tokens_pct": pct("total_tokens"), "new_tokens_pct": pct("new_tokens"), "cost_pct": pct("cost_usd"),
                    "output_tokens_pct": pct("output_tokens"), "time_pct": pct("duration_s")}
    return s


TEXT = {
    "pt": {"title": "Power BI Desktop com o NXPilot", "headline": "{p}% menos tokens",
           "sub": "Mesmo pedido ao Claude Code ({model}): relatório Power BI completo a partir de um fluxo de dados — {e} tabelas, {ms} medidas, {rl} relacionamentos, {pg} páginas, {vs} visuais (dados fictícios)",
           "without": "Sem NXPilot", "with": "Com NXPilot", "tokens": "Tokens (total)", "cost": "Custo estimado (US$)",
           "time": "Tempo", "done": "{k} de {n} relatórios completos", "cost_line": "{p}% menos custo",
           "source": "Fonte do teste: {url}", "runs": "{n} rodada de cada · {date} · {ver}", "runs_n": "{n} rodadas de cada · {date} · {ver}"},
    "en": {"title": "Power BI Desktop with NXPilot", "headline": "{p}% fewer tokens",
           "sub": "Same request to Claude Code ({model}): a complete Power BI report from a dataflow — {e} tables, {ms} measures, {rl} relationships, {pg} pages, {vs} visuals (fictitious data)",
           "without": "Without NXPilot", "with": "With NXPilot", "tokens": "Tokens (total)", "cost": "Estimated cost (US$)",
           "time": "Time", "done": "{k} of {n} reports complete", "cost_line": "{p}% lower cost",
           "source": "Test source: {url}", "runs": "{n} run each · {date} · {ver}", "runs_n": "{n} runs each · {date} · {ver}"},
    "es": {"title": "Power BI Desktop con NXPilot", "headline": "{p}% menos tokens",
           "sub": "Mismo pedido a Claude Code ({model}): informe Power BI completo a partir de un flujo de datos — {e} tablas, {ms} medidas, {rl} relaciones, {pg} páginas, {vs} objetos visuales (datos ficticios)",
           "without": "Sin NXPilot", "with": "Con NXPilot", "tokens": "Tokens (total)", "cost": "Costo estimado (US$)",
           "time": "Tiempo", "done": "{k} de {n} informes completos", "cost_line": "{p}% menos costo",
           "source": "Fuente de la prueba: {url}", "runs": "{n} ejecución de cada · {date} · {ver}", "runs_n": "{n} ejecuciones de cada · {date} · {ver}"},
}


def _esc(t: str) -> str:
    return t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def _num(v: float, lang: str, decimals: int = 0) -> str:
    """Número no formato do idioma: 488,698.5 (en) · 488.698,5 (pt, es)."""
    txt = f"{v:,.{decimals}f}"
    return txt if lang == "en" else txt.replace(",", "_").replace(".", ",").replace("_", ".")


def slide_svg(meta: dict, s: dict, lang: str) -> str:
    """Slide 1280×720 em SVG puro (abre no GitHub, sem biblioteca)."""
    t = TEXT[lang]
    sem, com, sav = s["sem"], s["com"], s["savings"]
    W, H = 1280, 720
    bar_x, bar_w = 360, 640

    def bars(y: int, label: str, a: float, b: float, fmt) -> str:
        top = max(a, b) or 1
        out = [f'<text x="80" y="{y + 22}" class="lab">{_esc(label)}</text>']
        for i, (name, val, color) in enumerate([(t["without"], a, "#9AA5B1"), (t["with"], b, "#1F6FEB")]):
            yy = y + 40 + i * 46
            w = max(4, int(bar_w * val / top))
            out.append(f'<text x="80" y="{yy + 24}" class="row">{_esc(name)}</text>')
            out.append(f'<rect x="{bar_x}" y="{yy}" width="{w}" height="34" rx="6" fill="{color}"/>')
            out.append(f'<text x="{bar_x + w + 12}" y="{yy + 24}" class="val">{_esc(fmt(val))}</text>')
        return "\n".join(out)

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" font-family="Segoe UI, Helvetica, Arial, sans-serif">',
        '<style>.lab{font-size:22px;font-weight:600;fill:#1F2328}.row{font-size:19px;fill:#57606A}.val{font-size:19px;font-weight:600;fill:#1F2328}'
        '.small{font-size:15px;fill:#57606A}</style>',
        f'<rect width="{W}" height="{H}" fill="#FFFFFF"/><rect width="{W}" height="10" fill="#1F6FEB"/>',
        f'<text x="80" y="88" style="font-size:30px;font-weight:600;fill:#57606A">{_esc(t["title"])}</text>',
        f'<text x="80" y="160" style="font-size:66px;font-weight:700;fill:#1F6FEB">{_esc(t["headline"].format(p=f"{sav["total_tokens_pct"]:.0f}"))}</text>',
        f'<text x="80" y="204" style="font-size:26px;font-weight:600;fill:#1A7F37">{_esc(t["cost_line"].format(p=f"{sav["cost_pct"]:.0f}"))}</text>',
        *[f'<text x="80" y="{232 + 22 * i}" class="small">{_esc(part.strip())}</text>' for i, part in enumerate(
            t["sub"].format(model=meta["model"], e=meta["data"]["entities"], c=meta["data"]["columns"], **meta["report"]).split(" — ", 1))],
        bars(280, t["tokens"], sem["total_tokens"], com["total_tokens"], lambda v: _num(v, lang)),
        bars(440, t["cost"], sem["cost_usd"], com["cost_usd"], lambda v: "US$ " + _num(v, lang, 2)),
        f'<text x="80" y="630" class="small">{_esc(t["time"])}: {sem["duration_s"]:.0f}s → {com["duration_s"]:.0f}s</text>',
        f'<text x="80" y="662" class="small">{_esc(t["source"].format(url=SOURCE_URL))}</text>',
        f'<text x="80" y="690" class="small">{_esc(t["runs" if sem["runs"] == 1 else "runs_n"].format(n=sem["runs"], date=meta["date"], ver=meta["claude"]))}</text>',
        "</svg>",
    ]
    return "\n".join(parts)


def write_report(meta: dict, results: list[dict]) -> dict:
    RESULTS.mkdir(parents=True, exist_ok=True)
    s = summarize(results)
    (RESULTS / "results.json").write_text(json.dumps({"meta": meta, "summary": s, "runs": results}, ensure_ascii=False, indent=2), encoding="utf-8")
    sav = s["savings"]
    yes = lambda v: "sim" if v else "não"  # noqa: E731
    lines = [
        "# Benchmark: Power BI Desktop (PBIP) com e sem o NXPilot", "",
        f"- Modelo: `{meta['model']}` · Claude Code: `{meta['claude']}` · data: {meta['date']}",
        f"- Dados: {meta['data']['entities']} entidades, {meta['data']['columns']} colunas, {meta['data']['rows']} linhas (fictícios, `dados/`)",
        f"- Pedido (igual nos dois): o relatório de `relatorio_varejo.json` descrito em texto (`prompt_for` no benchmark.py)"
        + (f" + o caminho do tema base do Power BI Desktop instalado (`{meta['theme']}`)" if meta.get("theme") else ""),
        "- O mesmo relatório sem IA, pela API do NXPilot: `python gerar_relatorio.py`", "",
        "| | Sem NXPilot | Com NXPilot | Economia |", "|---|---:|---:|---:|",
        f"| Tokens (total) | {s['sem']['total_tokens']:,.0f} | {s['com']['total_tokens']:,.0f} | **{sav['total_tokens_pct']}%** |",
        f"| Tokens novos (sem leitura de cache) | {s['sem']['new_tokens']:,.0f} | {s['com']['new_tokens']:,.0f} | {sav['new_tokens_pct']}% |",
        f"| Tokens de saída | {s['sem']['output_tokens']:,.0f} | {s['com']['output_tokens']:,.0f} | {sav['output_tokens_pct']}% |",
        f"| Custo estimado (US$) | {s['sem']['cost_usd']:.2f} | {s['com']['cost_usd']:.2f} | **{sav['cost_pct']}%** |",
        f"| Tempo (s) | {s['sem']['duration_s']:.0f} | {s['com']['duration_s']:.0f} | {sav['time_pct']}% |",
        f"| Projeto válido | {yes(s['sem']['valid'])} | {yes(s['com']['valid'])} | |",
        f"| Relatórios completos (medidas, relacionamentos, páginas, visuais) | {s['sem']['complete_runs']} de {s['sem']['runs']} | "
        f"{s['com']['complete_runs']} de {s['com']['runs']} | |", "",
        "Rodadas:", "",
        "| Modo | # | Tokens | US$ | Turnos | Válido | Completo | Medidas | Relac. | Páginas | Visuais | Usou o NXPilot | Scripts escritos |",
        "|---|---:|---:|---:|---:|---|---|---:|---:|---:|---:|---|---|",
    ]
    for r in results:
        lines.append(f"| {r['mode']} | {r['run']} | {r['total_tokens']:,} | {r['cost_usd']:.2f} | {r['turns']} | {yes(r['valid'])} | "
                     f"{yes(r.get('complete'))} | {r.get('measures')} | {r.get('relationships')} | {r.get('pages')} | {r.get('visuals')} | "
                     f"{'sim' if r.get('used_nxpilot_tool') else ('—' if r['mode'] == 'sem' else 'não')} | "
                     f"{', '.join(r['scripts']) or '—'} |")
    lines += ["", "Custo = estimativa que o próprio Claude Code informa (`total_cost_usd`); tokens = `usage` do Claude Code "
              "(entrada + saída + criação e leitura de cache)."]
    (RESULTS / "RESULTS.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    for lang in TEXT:
        (RESULTS / f"slide-{lang}.svg").write_text(slide_svg(meta, s, lang), encoding="utf-8")
    return s


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", default="sonnet")
    ap.add_argument("--runs", type=int, default=1)
    ap.add_argument("--claude")
    ap.add_argument("--bench")
    ap.add_argument("--via", choices=["bench", "app"], default="bench", help="bench: sem abrir o app; app: NXPilot aberto (API para scripts)")
    ap.add_argument("--nxpilot", help="NxPilot.exe (com --via app)")
    ap.add_argument("--only-data", action="store_true")
    ap.add_argument("--report-only", action="store_true")
    a = ap.parse_args()

    info = make_data()
    print(f"Dados fictícios: {info['entities']} entidades, {info['columns']} colunas, {info['rows']} linhas → {DATA}")
    if a.only_data:
        return
    if a.report_only:
        saved = json.loads((RESULTS / "results.json").read_text(encoding="utf-8"))
        s = write_report(saved["meta"], saved["runs"])
    else:
        claude, bench = find_claude(a.claude), find_bench(a.bench)
        nxpilot = find_nxpilot(a.nxpilot) if a.via == "app" else None
        theme = find_theme()
        print(f"Tema base do Power BI Desktop: {theme or 'não encontrado (projetos sem tema não abrem no Power BI)'}")
        exp = expected_counts()
        meta = {"model": a.model, "claude": claude_version(claude), "date": dt.date.today().isoformat(), "data": info, "via": a.via,
                "report": {"ms": exp["measures"], "rl": exp["relationships"], "pg": exp["pages"], "vs": exp["visuals"]},
                "theme": Path(theme).name if theme else None}
        results = []
        for i in range(1, a.runs + 1):
            for mode in ("sem", "com"):
                results.append(run_once(mode, i, claude, bench, a.model, nxpilot, theme))
        s = write_report(meta, results)
    sav = s["savings"]
    print()
    print(f"Economia com o NXPilot: {sav['total_tokens_pct']}% de tokens · {sav['cost_pct']}% de custo estimado "
          f"(sem: {s['sem']['total_tokens']:,.0f} tokens / US$ {s['sem']['cost_usd']:.2f} · com: {s['com']['total_tokens']:,.0f} / US$ {s['com']['cost_usd']:.2f})")
    print(f"Relatório: {RESULTS / 'RESULTS.md'} · slides: {RESULTS}/slide-pt.svg, slide-en.svg, slide-es.svg")


if __name__ == "__main__":
    main()
