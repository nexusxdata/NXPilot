# Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
# NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
# Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
# Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com
"""
Gera o relatório Power BI do benchmark SEM IA: chama as ferramentas do NXPilot direto pela API (MCP por stdio).

  1. powerbi_from_dataflow — projeto PBIP a partir de dados/fluxo_varejo.model.json, lendo os CSV de dados/;
  2. powerbi_build_report  — medidas, relacionamentos e páginas descritos em relatorio_varejo.json;
  3. powerbi_validate      — confere o projeto.

Mesmas regras que o agente recebe no benchmark, sem nenhum modelo de IA: quem baixar o código chega ao mesmo
resultado, sempre igual. Abra a pasta de saída no Power BI Desktop (Varejo.pbip) e clique em Atualizar.

Uso:
  python gerar_relatorio.py                       # saída em .work/sem-ia/
  python gerar_relatorio.py --saida C:\\temp\\varejo
  python gerar_relatorio.py --via app             # NXPilot aberto, com a API para scripts ligada

Requisitos: Python 3.10+, NXPilot compilado (build-nxpilot.ps1); Power BI Desktop para abrir (e para o tema base).
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import benchmark as b  # dados fictícios, localização do NXPilot e do tema base

SPEC = b.HERE / "relatorio_varejo.json"


class McpClient:
    """Cliente MCP mínimo (JSON-RPC, uma mensagem por linha) para o servidor do NXPilot."""

    def __init__(self, command: list[str]):
        self.p = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
        self.next_id = 0
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "gerar_relatorio", "version": "1"}})

    def request(self, method: str, params: dict) -> dict:
        self.next_id += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}) + "\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("O servidor MCP do NXPilot fechou (o NXPilot está aberto e a API para scripts ligada?).")
            msg = json.loads(line)
            if msg.get("id") == self.next_id:
                if "error" in msg:
                    raise RuntimeError(msg["error"].get("message"))
                return msg["result"]

    def call(self, tool: str, args: dict) -> str:
        result = self.request("tools/call", {"name": tool, "arguments": args})
        text = "\n".join(c.get("text", "") for c in result.get("content", []) if c.get("type") == "text")
        if result.get("isError"):
            raise RuntimeError(f"{tool}: {text}")
        return text

    def close(self) -> None:
        self.p.stdin.close()
        self.p.wait(timeout=30)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--saida", default=str(b.WORK / "sem-ia"))
    ap.add_argument("--via", choices=["bench", "app"], default="bench")
    ap.add_argument("--bench")
    ap.add_argument("--nxpilot")
    a = ap.parse_args()

    b.make_data()
    out = Path(a.saida).resolve()
    if out.exists():
        shutil.rmtree(out)
    shutil.copytree(b.DATA, out / "dados")
    theme = b.find_theme()
    if a.via == "app":
        command = [b.find_nxpilot(a.nxpilot), "--mcp", "--workdir", str(out)]
    else:
        command = [b.find_bench(a.bench), "--mcp-bench", str(out)] + ([theme] if theme else [])

    mcp = McpClient(command)
    try:
        print(mcp.call("powerbi_from_dataflow", {"dataflow": "dados/fluxo_varejo.model.json", "name": "Varejo",
                                                 "workspace_name": b.WORKSPACE, "local_data_folder": "dados"}))
        print(mcp.call("powerbi_build_report", json.loads(SPEC.read_text(encoding="utf-8"))))
        print(mcp.call("powerbi_validate", {}))
    finally:
        mcp.close()
    check = subprocess.run([b.find_bench(a.bench), "--pbip-check", str(out)], capture_output=True, text=True, encoding="utf-8").stdout
    print("Conferência:", check.strip())
    print(f"Pronto: abra {out / 'Varejo.pbip'} no Power BI Desktop e clique em Atualizar.")


if __name__ == "__main__":
    sys.exit(main())
