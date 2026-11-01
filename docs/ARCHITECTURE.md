# Arquitetura do MyOwnEQ

## Visão geral

```
            ┌──────────── apps (uma por plataforma, só tela + sistema) ────────────┐
            │  mac/  SwiftUI + Core Audio tap      windows/  WPF + Equalizer APO     │
            │  (futuro) linux/  GTK4 + PipeWire                                      │
            └───────────────┬───────────────────────────────────┬───────────────────┘
                            │ comandos JSON (C ABI)             │ áudio em tempo real
                            ▼                                   ▼
┌───────────────────────── core em Rust (core/) ──────────────────────────────────────┐
│ ffi/      Core: estado do app, comandos (service.rs), sync (syncing.rs), C ABI      │
│ model/    perfis, presets travados, dispositivos, regras, biblioteca, formatos,     │
│           edição (edit.rs: conversão de layout, sugestão de banda), temas/idiomas   │
│           (assets.rs)                                                               │
│ dsp/      biquads, suavização, limitador com lookahead, loudness, espectro          │
│ sync/     HLC + last-writer-wins, réplica com outbox, cliente WebSocket, mDNS,      │
│           presença (quem está conectado)                                            │
│ update/   atualização assinada (ed25519), download e instalação                    │
└──────────────────────────────────────────────────────────────────────────────────────┘
server/   servidor de sync (axum + SQLite): Docker, nuvem, ou embutido no app (LAN)
themes/   lang/   arquivos compartilhados por todos os apps
```

## Onde cada lógica deve morar

**No core**, sempre que não for desenho de tela nem integração com o sistema:

- tudo sobre perfis, presets (somente leitura, se consertam sozinhos), regras, dispositivos;
- conversão Paramétrico ↔ Gráfico (com volta exata), onde colocar banda nova
  (`convert_eq`, `suggest_band`);
- cópia de preset ao editar (`fork_preset`);
- constantes da interface: frequências dos gráficos, pontos dos macros (`ui_constants`);
- leitura, validação e regras de mesclagem de temas e idiomas (`load_themes`, `load_langs`);
- código de pareamento do sync, presença das máquinas, atualizações.

**Nos apps**: desenhar, gestos, animações, motor de áudio da plataforma, bandeja / barra
de menus, iniciar com o sistema, diálogos nativos.

Se a mesma regra aparecer em Swift e em C#, é sinal de que ela deveria ser um comando
do core. O `scripts/parity.py` garante que os recursos existam nos dois apps; o core
garante que eles se comportem igual.

## Fluxo

1. O app manda comandos JSON (`meq_core_command`): `select`, `upsert_profile`, `preview`…
2. O core atualiza a biblioteca, resolve qual perfil vale (regra > escolha > padrão),
   grava em disco, registra a mudança no sync e responde com o estado completo.
3. Parâmetros de DSP vão para o áudio por um canal sem lock (`rtrb`).
   - Mac: o tap de áudio chama `meq_audio_process` no callback de tempo real.
   - Windows: o core gera o arquivo de configuração do Equalizer APO; o loopback só
     alimenta o espectro (`meq_audio_analyze`).

## Threads

- Callback de áudio: nada de alocação, lock ou I/O; só `Audio::process`.
- Comandos do core: thread da interface (main / dispatcher).
- Sync, atualização, servidor embutido: threads próprias; o app consulta com
  `sync_tick` / `update_tick`.

## Testes

| O quê | Onde |
|---|---|
| DSP, modelo, sync, servidor ponta a ponta, comandos | `cargo test --workspace` |
| Modelos C# × core real | `windows/InteropTests` |
| App do Mac lê qualquer biblioteca | `MyOwnEQ --check-library` (CI) |
| App do Windows abre todas as telas | `MyOwnEQ.exe --selftest` + prints no CI |
| Paridade e traduções | `scripts/parity.py`, `scripts/lang-keys.py` |

## Decisões conhecidas

- **Windows usa Equalizer APO** por enquanto: sem limitador, então o boost fica
  desligado lá. Um motor próprio (APO em Rust) resolveria.
- **Vidro no Windows** é aproximado: o WPF não desfoca o que está atrás de um elemento.
- **Interfaces nativas** (SwiftUI, WPF, GTK): mais trabalho que uma interface única, mas
  é o que dá cara de app do sistema. A paridade é garantida por regra + core comum.

## Próxima fase: Linux

Plano (ainda não implementado):

- **Áudio**: um nó do PipeWire em Rust (`pipewire-rs`) que cria um "sink" virtual
  "MyOwnEQ", roda o nosso DSP e encaminha para a saída real; o app o define como saída
  padrão. Regras por app usam as propriedades dos streams do PipeWire.
- **Interface**: GTK4 + libadwaita via `gtk-rs`, chamando o core direto em Rust (sem C ABI).
  Mesmos temas, idiomas e regra de paridade (nova coluna `linux` no `features.json`).
- **Bandeja**: StatusNotifierItem (`ksni`); no GNOME depende de extensão.
- **Pacotes**: Flatpak e AppImage; atualização pelo próprio Flatpak.
- **Riscos**: versões antigas do PipeWire, bandeja no GNOME, permissões do Flatpak para
  mexer em áudio.
