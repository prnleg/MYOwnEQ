# Paridade Mac × Windows

Os dois apps devem parecer e funcionar igual. Ao mudar uma tela ou recurso em um,
atualize o outro e esta lista no mesmo PR.

O que já é compartilhado de verdade (uma fonte só):
- **Core em Rust**: perfis, presets travados, regras, sync, curvas, atualizações.
- **Temas**: `themes/*.json`.
- **Idiomas**: `lang/*.json`. O `scripts/lang-keys.py` lê o código dos dois apps e o CI
  falha se faltar tradução.
- **Logo**: mesmo desenho (`Logo.swift` / `UI/Components/Logo.cs`); o `.ico` do Windows
  é gerado pelo app do Mac (`windows/scripts/make-icon.sh`).

Arquivos equivalentes: `mac/Sources/MyOwnEQ/Views/X.swift` ↔ `windows/MyOwnEQ/UI/Views/X.cs`
(mesmos nomes sempre que possível).

| Tela / recurso | Mac | Windows | Obs. |
|---|:-:|:-:|---|
| Fundo animado com manchas do tema | ✅ | ✅ | |
| Barra lateral: card "tocando agora", busca, Favoritos / Meus perfis / Presets | ✅ | ✅ | |
| Esconder presets (lembra a escolha) | ✅ | ✅ | |
| Mini curva por perfil + indicador "tocando" | ✅ | ✅ | |
| Renomear (menu de contexto + lápis) | ✅ | ✅ | |
| Editar preset cria "Nome (meu)" | ✅ | ✅ | trava real no core |
| Segure para ouvir / Usar agora | ✅ | ✅ | |
| Auto-save ou barra Salvar/Descartar + pergunta ao sair | ✅ | ✅ | Mac ⌘S, Windows Ctrl+S |
| Curva: mola, brilho, shimmer, ondinhas, balão de valores | ✅ | ✅ | |
| Pontos no gráfico: Fácil (4 macros), Pro paramétrico, Pro gráfico | ✅ | ✅ | |
| Mudar Q no gráfico | ✅ | ✅ | Mac: Option+arrastar · Windows: Shift+arrastar ou rodinha |
| Trocar Paramétrico ↔ Gráfico mantendo o som | ✅ | ✅ | |
| Painel da barra de menus / bandeja | ✅ | ✅ | |
| Volume do sistema | ✅ | ✅ | |
| Boost até +12 dB com limitador | ✅ | ⛔ | Equalizer APO não tem limitador; no Windows aparece desligado com aviso |
| Temas (galeria, pasta do usuário, recarregar) | ✅ | ✅ | |
| Idiomas (pt-BR, en-US, pasta do usuário) | ✅ | ✅ | |
| Logo: onda / dedo / imagem própria | ✅ | ✅ | ícone monocromático só no Mac (a bandeja do Windows é sempre colorida) |
| Animações: ligar/desligar, reagir à música, shimmer | ✅ | ✅ | respeita "Reduzir movimento" / "Efeitos de animação" do sistema |
| Dispositivos e regras por app | ✅ | ✅ | Windows tem o campo .exe |
| Sync e atualizações | ✅ | ✅ | |
| Configuração do Equalizer APO | — | ✅ | só Windows |
| Iniciar com o sistema | — | ✅ | Mac: Itens de Login do macOS |
| Self-test com prints de cada tela no CI | — | ✅ | artefato `selftest-screenshots` |
