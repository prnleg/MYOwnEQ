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
│ engine/   o DSP como plugin (ABI VST 2.4) que o Equalizer APO carrega no Windows   │
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
   - Windows, modo clássico: o core gera filtros do Equalizer APO (`apo_config`); o
     loopback só alimenta o espectro (`meq_audio_analyze`).
   - Windows, motor MyOwnEQ (experimental): o Equalizer APO só hospeda o nosso plugin
     (`core/engine`), que roda o mesmo `Processor` do Mac. Veja abaixo.

## Motor MyOwnEQ no Windows

```
app ──escreve──► %ProgramData%\MyOwnEQ\engine.json        (parâmetros por saída, boost incluso)
    ──escreve──► <config do APO>\MyOwnEQ.txt:
                   Device: {guid}
                   VSTPlugin: Library "...\engine\MyOwnEQEngine-<versão>.dll" ChunkData "<id da saída>"
audiodg (Equalizer APO) ── carrega o plugin por saída ──► o plugin lê engine.json e
    aplica pelo canal sem lock (sem recarregar o APO: prévia instantânea)
plugin ──escreve──► engine-status-<saída>.json (batimento: carregado / processando)
```

- Sem admin: a pasta em ProgramData é criada pelo app; o serviço de áudio só lê.
- O app vigia o batimento: se há áudio tocando e o motor não processa por ~10 s, volta
  sozinho para os filtros do Equalizer APO.
- O formato do `engine.json` está em `core/engine/src/schema.rs` (o core escreve, o
  plugin lê — mesmo código). O plugin limita valores absurdos e nunca aplica ganho
  positivo sem o limitador.
- Segurança: o plugin roda dentro do serviço de áudio a partir de uma pasta que o
  usuário pode escrever — o mesmo modelo de confiança da pasta de config do Equalizer
  APO (cujas linhas `VSTPlugin:` já podem apontar para qualquer DLL).
- Testes: `cargo test -p myowneq-engine` (layout do `AEffect`, fluxo completo pelo
  `VSTPluginMain`) e o InteropTests carrega a biblioteca de verdade como o APO faz.

## Threads

- Callback de áudio: nada de alocação, lock ou I/O; só `Audio::process`.
- Comandos do core: thread da interface (main / dispatcher).
- Sync, atualização, servidor embutido: threads próprias; o app consulta com
  `sync_tick` / `update_tick`.

## Testes

| O quê | Onde |
|---|---|
| DSP, modelo, sync, servidor ponta a ponta, comandos | `cargo test --workspace` |
| Modelos C# × core real | `windows/InteropTests` (CI: só no Windows) |
| App do Mac lê qualquer biblioteca | `MyOwnEQ --check-library` (CI) |
| App do Windows abre todas as telas | `MyOwnEQ.exe --selftest` (prints só quando falha) |
| Paridade e traduções | `scripts/parity.py`, `scripts/lang-keys.py` |
| Desinstalar limpa o Equalizer APO | `MyOwnEQ.exe --cleanup` (chamado pelo desinstalador) |

## Decisões conhecidas

- **Windows usa o Equalizer APO como hospedeiro.** Com os filtros dele (modo clássico)
  não há limitador nem boost; com o motor MyOwnEQ, o nosso DSP roda dentro dele e o
  boost funciona. Um APO 100% nosso exigiria admin, por isso ficou de fora.
- **Vidro no Windows** é aproximado: o WPF não desfoca o que está atrás de um elemento.
- **Interfaces nativas** (SwiftUI, WPF, GTK): mais trabalho que uma interface única, mas
  é o que dá cara de app do sistema. A paridade é garantida por regra + core comum.

## Extensões (planejado)

O app principal fica leve; recursos pesados ou de nicho viram extensões (download
separado). Lista e ordem em [ROADMAP.md](../ROADMAP.md).

```
extensão (CLI, celular, microfone…) ──comandos JSON──► canal local do app ──► core
```

- **Canal de controle local**: o app aceita os mesmos comandos JSON do core
  (`select`, `set_enabled`, …) só da própria máquina — socket Unix no Mac, named pipe no
  Windows — com um token salvo na pasta de dados do usuário. É a única coisa que o app
  principal ganha.
- **Cada extensão** mora em `extensions/<nome>/`, tem release própria e fala com o app só
  pelo canal (nada de linkar no app). A linha de comando é a primeira e serve de base
  para as outras.
- **Paridade**: o `features.json` ganha o tipo "extension"; o `parity.py` não exige
  extensões no app principal, mas exige que cada extensão declare em quais plataformas
  funciona.
- **Peso e permissões ficam na extensão**: o driver de áudio virtual do EQ de microfone
  no Mac (pede admin) só existe para quem instalar essa extensão.

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
