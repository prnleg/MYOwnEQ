# Paridade Mac × Windows

<!-- Gerado por scripts/parity.py --write a partir de parity/features.json. Não edite à mão. -->

Os dois apps devem ter os mesmos recursos. Cada linha abaixo vem de
`parity/features.json`; o CI confere se o código das duas plataformas tem o
marcador `// parity: <id>` de cada recurso e se nenhum texto da interface existe
em só um dos apps sem justificativa. Veja `CONTRIBUTING.md`.

✅ tem · ⛔ falta (com motivo) · — não se aplica

| Recurso | id | Mac | Windows | Obs. |
|---|---|:-:|:-:|---|
| Temas (galeria, pasta do usuário, recarregar) | `themes` | ✅ | ✅ |  |
| Idiomas (pt-BR, en-US, pasta do usuário) | `languages` | ✅ | ✅ |  |
| Logo: onda / dedo / imagem própria | `custom-logo` | ✅ | ✅ | Ícone monocromático só no Mac (a bandeja do Windows é sempre colorida). |
| Animações: ligar/desligar, reagir à música, shimmer | `motion-settings` | ✅ | ✅ |  |
| Fundo animado com manchas do tema | `animated-background` | ✅ | ✅ |  |
| Barra lateral: tocando agora, busca, Favoritos / Meus perfis / Presets (esconder), mini curvas | `sidebar` | ✅ | ✅ |  |
| Renomear perfil (menu de contexto + lápis) | `rename` | ✅ | ✅ |  |
| Confirmar antes de apagar perfil | `delete-confirm` | ✅ | ✅ |  |
| Editar preset cria "Nome (meu)" (lógica no core) | `preset-fork` | ✅ | ✅ |  |
| Segure para ouvir / Usar agora | `hold-to-listen` | ✅ | ✅ |  |
| Auto-save ou barra Salvar/Descartar + pergunta ao sair | `auto-save` | ✅ | ✅ |  |
| Curva: mola, brilho, shimmer, ondinhas, balão de valores, espectro | `curve` | ✅ | ✅ |  |
| Pontos no gráfico: Fácil (4 macros), Pro paramétrico, Pro gráfico | `curve-edit` | ✅ | ✅ |  |
| Mudar Q: rodinha/scroll ou Shift+arrastar | `curve-q-wheel` | ✅ | ✅ | No Mac Option+arrastar também funciona. |
| Trocar Paramétrico ↔ Gráfico mantendo o som (lógica no core) | `eq-layout-convert` | ✅ | ✅ |  |
| Painel Pro: bandas, saída (pré-amp, balanço, loudness), correção | `pro-panel` | ✅ | ✅ |  |
| Painel Fácil: graves/médios/clareza/agudos, AutoEQ, conforto | `easy-panel` | ✅ | ✅ |  |
| Importar AutoEQ e Equalizer APO/Peace, exportar APO | `imports` | ✅ | ✅ |  |
| Painel da barra de menus / bandeja | `tray-panel` | ✅ | ✅ |  |
| Volume do sistema | `volume` | ✅ | ✅ |  |
| Boost até +12 dB com limitador | `boost` | ✅ | ✅ | Windows: precisa do motor MyOwnEQ (Preferências › Motor de áudio do Windows); só com os filtros do Equalizer APO não há limitador. |
| Dispositivos: nome, padrão, favoritos, vínculo entre máquinas, regras por app | `devices-rules` | ✅ | ✅ | Windows tem o campo .exe nas regras. |
| Vincular/desvincular fone entre máquinas com confirmação | `link-feedback` | ✅ | ✅ |  |
| Sync: servidor, hospedar na rede local, procurar na rede | `sync` | ✅ | ✅ |  |
| Máquinas conectadas (online / visto há…) | `sync-peers` | ✅ | ✅ |  |
| Atualizações: automático + "Verificar agora" com progresso | `update-check` | ✅ | ✅ |  |
| Avisos que somem sozinhos (toast) | `notices` | ✅ | ✅ |  |
| Iniciar com o sistema | `start-with-system` | ✅ | ✅ | Mac: Itens de Login (SMAppService). Windows: registro Run. |
| Barra de título integrada ao fundo | `blended-title-bar` | — | ✅ | O Mac usa a barra nativa unificada da janela. |
| Configuração do Equalizer APO | `apo-setup` | — | ✅ |  |
| Motor MyOwnEQ: o DSP do app rodando dentro do Equalizer APO (limitador, boost, prévia instantânea) | `engine` | — | ✅ | No Mac o DSP já roda no próprio app (Core Audio). Experimental no Windows. |
| Aviso de permissão de captura de áudio | `audio-permission` | ✅ | — |  |
| Importar presets do eqMac | `eqmac-import` | ✅ | — |  |
| Self-test com prints de cada tela no CI | `selftest` | ⛔ | ✅ | Mac: só build + --check-library no CI por enquanto. |
