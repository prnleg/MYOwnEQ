# Como contribuir

Valeu por querer ajudar. Uma regra manda em tudo aqui: **o app do Mac e o do
Windows têm que ser iguais** — mesmos recursos, mesmo jeito de usar, mesma cara.
O Linux entra na mesma regra quando o port começar.

## Onde cada coisa mora

| Tipo de código | Onde fica |
|---|---|
| Regras do app, dados, sync, conversões, validação, qualquer lógica | **core em Rust** (`core/`) — uma vez só, para todas as plataformas |
| Desenho das telas, gestos, animações | `mac/` (SwiftUI) e `windows/` (WPF), um de cada |
| Motor de áudio e integração com o sistema | `mac/` (Core Audio) e `windows/` (Equalizer APO) |
| Temas e idiomas | `themes/` e `lang/`, compartilhados |

Se você está prestes a escrever a mesma lógica em Swift **e** em C#, ela deveria
ser um comando novo no core (`core/ffi/src/service.rs`). Veja `docs/ARCHITECTURE.md`.

## A regra de paridade (o CI confere)

1. Todo recurso visível tem uma entrada em `parity/features.json`, com
   `"mac"` e `"windows"` valendo `yes`, `no` (precisa de `note` explicando) ou `n/a`.
2. O código que implementa o recurso leva um comentário `// parity: <id>`
   em cada plataforma marcada `yes`.
3. Todo texto de interface passa por `L("Texto em inglês")`. Se um texto existe
   em só um app, ou o outro app está faltando aquela tela/mensagem, ou é coisa
   do sistema (ex.: "este Mac" × "este PC") e vai declarado em `platform_strings`
   com o motivo.
4. O `PARITY.md` é gerado: rode `scripts/parity.py --write`.

Rodar localmente:

```sh
scripts/parity.py          # paridade
scripts/lang-keys.py       # traduções (atualiza lang/en-US.json)
cargo test --workspace     # core
```

## Mudou só um app?

Tudo bem abrir o PR com uma plataforma só, **desde que** o `features.json` marque
a outra como `no` com uma `note` ("falta no Windows: …"). Assim a diferença fica
visível no `PARITY.md` e alguém pode fechar depois.

## Testar

- Mac: `mac/scripts/build-app.sh --run` (compila, fecha o app aberto e abre o novo).
- Windows: o CI roda o app com `--selftest` e publica prints de todas as telas no
  artefato `selftest-screenshots`.
- Core: `cargo test --workspace`.

## Commits

Mensagens curtas. Fora isso, fique à vontade.
