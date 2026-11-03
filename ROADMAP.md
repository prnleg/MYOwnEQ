# Roadmap

Regra principal: **o app principal fica leve**. Coisa pequena que quase todo mundo usa
entra no app; coisa pesada ou de nicho vira **extensão** (download separado, instala só
quem quer). Nada aqui é promessa de data.

Cada item vira um PR próprio. Nada vai direto na `main`.

## Fluxo de trabalho

- Um branch por PR (`fix/...`, `feat/...`, `docs/...`, `ci/...`).
- O CI precisa passar (paridade, traduções, testes, build Mac e Windows).
- Recurso novo no app principal precisa existir no Mac e no Windows (veja
  [CONTRIBUTING.md](CONTRIBUTING.md)). Extensões têm regra própria (abaixo).

## PR 1 — Arrumação

| Item | Por quê |
|---|---|
| Limpeza ao desinstalar (Windows) | Hoje o `config.txt` do Equalizer APO continua incluindo o `MyOwnEQ.txt` e o motor continua em `ProgramData\MyOwnEQ`: o EQ segue ativo depois de desinstalar. O desinstalador vai chamar `MyOwnEQ.exe --cleanup` (restaura o `config.txt` original e apaga a pasta). |
| Instalador do Equalizer APO com hash | Versão fixa + SHA256 conferido antes de rodar. Se o download falhar ou o hash não bater, abre a página oficial em vez de executar. |
| Botão "Copiar diagnóstico" | Versão, sistema, saídas, estado do APO por saída, config gerada e últimas linhas do log. Fica mais fácild e caçar o bug. |
| Templates de issue | Bug (pede sistema, dispositivo, diagnóstico) e sugestão de recurso. |
| Barra de título | Em Dispositivos / Sync / Preferências a barra superior "pisca"/muda. Deveria ser uma só, transparente, em todas as telas (Mac e Windows 11). |
| Loudness | Hoje quase não faz efeito: o volume do sistema é tratado como amplitude (amp) linear (em 50% dá só +1,5 dB de grave) e o pré-amp automático compensa o reforço. Vai usar o volume em dB do sistema, curva mais forte, e o pré-amp não anula o loudness. Teste: em 30% de volume o grave sobe pelo menos X dB. |
| CI mais enxuto | Ver seção CI. |

## PR 2 — Som nos fones

- **Crossfeed** (estilo bs2b): mistura um pouco de cada lado no outro, menos cansativo em fone.
- **Ajuste fino do AutoEQ**: inclinação (tilt), grave e agudo por cima da correção. Disponível no modo Easy.
- **Importar correção do REW**: o REW exporta no formato do Equalizer APO, que já importa. Testar e documentar ([docs/REW.md](docs/REW.md)).

## PR 3 — Códigos de compartilhamento

- Cada EQ vira um código `MEQ1-…` que **carrega o EQ inteiro dentro dele** (bandas
  compactadas, ~100 caracteres para 10 bandas). Sem servidor, sem custo.
- Copiar, colar para importar, QR code (QR code meio foda pq não tem muito uso, mas é maneiro kkkkk).

## PR 4 — Busca do AutoEQ

- Achar o fone pelo nome dentro do app (~9 mil modelos) e aplicar a correção em um clique.
  A busca já começa com o nome da saída atual ("Momentum 4 Headphones" → "momentum 4").
- Escolher **quem mediu** (oratory1990, crinacle, Rtings…), o mais confiável primeiro.
- Lista e arquivos vêm do GitHub do AutoEQ e ficam em cache: depois de usado, funciona
  offline. A lista se atualiza sozinha toda semana.
- **Escolher o alvo** (Harman, IEF, neutro…) fica para depois: cada resultado do AutoEQ já
  vem ajustado para um alvo; trocar exige ajustar a curva nós mesmos a partir da medição.

## Próximos (app principal)

- Editor de tema dentro do app + imagem de fundo + códigos de tema (`MEQT-…`).
- Aba Comunidade (EQs e temas do site), depois do site estar no ar.
- EQ separado para esquerdo/direito (modo Pro).
- Atalhos de teclado globais (trocar perfil, ligar/desligar).
- Volume automático (auto gain).
- Escolher o alvo do AutoEQ (ver PR 4).

## Extensões (futuro)

Ver "Extensões" em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Ordem sugerida, porque a primeira é a base das outras:

1. ✅ **Canal de controle local + linha de comando** (`myowneq select "Gaming"`, links
   `myowneq://select/Gaming`). Base para Raycast, Atalhos, AutoHotkey, Stream Deck.
   Veja [extensions/cli](extensions/cli/README.md).
2. **Controle pelo celular**: página web simples mandando comandos pelo mesmo canal (aqui que entraria o uso do QR Code por exemplo, massa né?).
3. **EQ do microfone**: no Windows pelo Equalizer APO nos dispositivos de captura (é fácil);
   no Mac precisa de um dispositivo de áudio virtual (driver, pede admin), por isso fica só dentro da extensão.
   Junto: medir com o microfone para gerar correção automática.
4. **Volume/EQ por app** (Mac primeiro, pelos process taps).
5. **Convolver** (respostas a impulso): correção de sala e surround virtual.

## CI

- Screenshots do self-test: só sobem quando o self-test falha (foi mais útil antes, agora nem tanto).
- App do Windows como artefato: só na `main` e em execução manual, retenção curta.
  **Remover depois** que o fluxo de release estiver estável.
- Testes de interop: uma execução só (hoje rodam no `core.yml` e no `windows.yml`).
- Build Rust no Windows: só release.
- Mudanças só em docs (`*.md`, `docs/`) não disparam o build completo (esse era erro meu).
- Push novo num PR cancela o CI anterior do mesmo PR.

## Estacionado

- **Linux**: plano em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Alternativa mais barata: exportar/importar perfis no formato do EasyEffects e sincronizar.
- **Motor MyOwnEQ como padrão no Windows**: quando provar estável na instalação do zero.
- **Assinatura de código**: sem conta paga da Apple; fica aberto para a comunidade (sou pobre, n quero pagar 100 doletas por ano).
- **Token do sync no Keychain** (hoje em arquivo).
