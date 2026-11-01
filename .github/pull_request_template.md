## O que muda

<!-- Uma ou duas frases. -->

## Paridade Mac × Windows

- [ ] Funciona nos **dois** apps (ou o `parity/features.json` explica por que não, no campo `note`)
- [ ] Recurso novo: entrada nova em `parity/features.json` + marcador `// parity: <id>` no código de cada plataforma
- [ ] Lógica que não é desenho de tela nem coisa do sistema foi para o **core em Rust** (`core/`), não duplicada em Swift e C#
- [ ] Textos novos passam por `L("…")` e estão traduzidos em `lang/pt-BR.json` (`scripts/lang-keys.py`)
- [ ] `scripts/parity.py --write` rodado (atualiza o `PARITY.md`)

## Como testei

- [ ] Mac: `mac/scripts/build-app.sh --run`
- [ ] Windows: CI verde + prints do artefato `selftest-screenshots` conferidos
- [ ] `cargo test --workspace`
